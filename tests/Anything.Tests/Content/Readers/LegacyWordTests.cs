using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Anything.Core.Content.Extract.Office;

namespace Anything.Tests.Content.Readers;

public class LegacyWordTests
{
    // ------------------------------------------------------------------ fixtures: a FIB, text pieces and a Clx, built in code

    const int FibBytes = 0x200;

    /// <summary>One piece: its bytes as stored (8-bit or UTF-16LE), whether it is 8-bit, and where its row marks are.</summary>
    record Piece(byte[] Bytes, bool Compressed, int[] Rows)
    {
        public int Chars => Compressed ? Bytes.Length : Bytes.Length / 2;
    }

    /// <summary>In a fixture's text, a table row's end: stored as 0x07 like a cell mark, told apart by its properties.</summary>
    const char Row = '\uE000';

    static Piece A(string s) => Marked(s, true);
    static Piece A(params byte[] bytes) => new(bytes, true, []);
    static Piece U(string s) => Marked(s, false);

    static Piece Marked(string s, bool compressed)
    {
        var rows = new List<int>();
        for (int i = 0; i < s.Length; i++) if (s[i] == Row) rows.Add(i);
        s = s.Replace(Row, '\u0007');
        return new(compressed ? Encoding.Latin1.GetBytes(s) : Encoding.Unicode.GetBytes(s), compressed, rows.ToArray());
    }

    /// <summary>The streams of a document; BteAt (in the table) and PageAt (in WordDocument) are -1 without a PapxFkp.</summary>
    record Doc(byte[] WordDocument, byte[] Table, int ClxAt, int PlcAt, int BteAt = -1, int PageAt = -1);

    /// <summary>A PapxInFkp from its style index and sprms: cb counts 2-byte units, and an even size takes cb = 0 and cb'.</summary>
    static byte[] Papx(params byte[] body)
        => body.Length % 2 == 1 ? [(byte)((body.Length + 1) / 2), .. body] : [0, (byte)(body.Length / 2), .. body];

    /// <summary>A table cell's paragraph: sprmPFInTable only.</summary>
    static readonly byte[] CellPapx = Papx(0, 0, 0x16, 0x24, 1);

    /// <summary>
    /// A row-end paragraph as Word writes it, with table sprms of every operand size before sprmPFTtp so that a wrong size
    /// hides it: sprmPFInTable, sprmTDefTable (a 2-byte cb, then cb - 1 bytes), sprmPChgTabs (a size byte), a 4-byte, a
    /// 2-byte and a 3-byte sprm, then sprmPFTtp.
    /// </summary>
    static readonly byte[] RowPapx = Papx(0, 0,
        0x16, 0x24, 1,
        0x08, 0xD6, 5, 0, 0x17, 0x24, 1, 0,
        0x15, 0xC6, 3, 0x17, 0x24, 1,
        0x6B, 0x64, 0x17, 0x24, 1, 0,
        0x02, 0x96, 0x17, 0x24,
        0x61, 0xF6, 0x17, 0x24, 1,
        0x17, 0x24, 1);

    /// <summary>
    /// A 512-byte PapxFkp: crun+1 stream positions, crun BxPap entries of 13 bytes (the first byte the properties' offset
    /// / 2, 0 for none), the properties (each written once however many runs share it), and crun in the last byte.
    /// </summary>
    static byte[] FkpPage(int[] fcs, byte[]?[] props)
    {
        int crun = props.Length;
        Assert.True(crun is > 0 and < 30 && fcs.Length == crun + 1);
        var page = new byte[512];
        for (int i = 0; i <= crun; i++) W32(page, i * 4, fcs[i]);
        int at = (crun + 1) * 4 + crun * 13;
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < crun; r++)
        {
            if (props[r] is not { } p) continue;
            if (!placed.TryGetValue(p, out int offset))
            {
                at += at & 1;
                Assert.True(at + p.Length <= 511, "the properties don't fit in the page");
                offset = at;
                p.CopyTo(page, at);
                at += p.Length;
                placed[p] = offset;
            }
            page[(crun + 1) * 4 + r * 13] = (byte)(offset / 2);
        }
        Assert.True(at <= 511);
        page[511] = (byte)crun;
        return page;
    }

    /// <summary>
    /// A WordDocument stream (a FIB with wIdent, flags, csw/cslw/cbRgFcLcb, ccpText, the other stories' counts, fcClx and
    /// lcbClx; then the pieces' bytes, stored in fileOrder; then, when a piece has row marks, a PapxFkp page whose runs
    /// give each row mark rowPapx and the text around them CellPapx) and a table stream (a few bytes of padding, optional
    /// Prc blocks, the Pcdt, then the PlcBtePapx). stories are ccpFtn, ccpHdd, ccpMcr, ccpAtn, ccpEdn, ccpTxbx, ccpHdrTxbx.
    /// </summary>
    static Doc Build(Piece[] pieces, int? ccpText = null, ushort flags = 0, ushort ident = 0xA5EC, int[]? fileOrder = null, byte[][]? prcs = null,
        int[]? stories = null, bool papx = true, byte[]? rowPapx = null)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[FibBytes]);
        var fcs = new uint[pieces.Length];
        var marks = new List<(int At, int Width)>();
        foreach (int i in fileOrder ?? Enumerable.Range(0, pieces.Length).ToArray())
        {
            if (pieces[i].Compressed)
            {
                fcs[i] = (uint)(stream.Length * 2) | 0x40000000;
            }
            else
            {
                if ((stream.Length & 1) != 0) stream.WriteByte(0);
                fcs[i] = (uint)stream.Length;
            }
            int width = pieces[i].Compressed ? 1 : 2;
            foreach (int k in pieces[i].Rows) marks.Add(((int)stream.Length + k * width, width));
            stream.Write(pieces[i].Bytes);
            stream.WriteByte(0xEE); // a stray byte between pieces, never part of the text
        }
        int pageAt = -1;
        var runs = new List<int> { FibBytes };
        if (papx && marks.Count > 0)
        {
            int textEnd = (int)stream.Length;
            while (stream.Length % 512 != 0) stream.WriteByte(0);
            pageAt = (int)stream.Length;
            var props = new List<byte[]?>();
            marks.Sort();
            foreach (var (at, width) in marks)
            {
                if (at > runs[^1]) { runs.Add(at); props.Add(CellPapx); }
                runs.Add(at + width);
                props.Add(rowPapx ?? RowPapx);
            }
            if (textEnd > runs[^1]) { runs.Add(textEnd); props.Add(CellPapx); }
            stream.Write(FkpPage(runs.ToArray(), props.ToArray()));
        }
        var doc = stream.ToArray();

        var table = new MemoryStream();
        table.Write([9, 9, 9, 9, 9, 9, 9]);
        int clxAt = (int)table.Length;
        foreach (var prc in prcs ?? [])
        {
            table.WriteByte(0x01);
            table.Write(BitConverter.GetBytes((ushort)prc.Length));
            table.Write(prc);
        }
        table.WriteByte(0x02);
        table.Write(BitConverter.GetBytes((uint)(4 + 12 * pieces.Length)));
        int plcAt = (int)table.Length;
        int cp = 0;
        table.Write(BitConverter.GetBytes(cp));
        foreach (var p in pieces) table.Write(BitConverter.GetBytes(cp += p.Chars));
        for (int i = 0; i < pieces.Length; i++)
        {
            table.Write(BitConverter.GetBytes((ushort)0));
            table.Write(BitConverter.GetBytes(fcs[i]));
            table.Write(BitConverter.GetBytes((ushort)0));
        }
        int lcbClx = (int)table.Length - clxAt;
        table.Write([7, 7, 7]);
        int bteAt = -1;
        if (pageAt >= 0)
        {
            bteAt = (int)table.Length;
            table.Write(BitConverter.GetBytes(runs[0]));
            table.Write(BitConverter.GetBytes(runs[^1]));
            table.Write(BitConverter.GetBytes(pageAt / 512));
            table.Write([5, 5]);
        }

        W16(doc, 0x00, ident);
        W16(doc, 0x02, 0x00C1);
        W16(doc, 0x0A, flags);
        W16(doc, 0x20, 14);
        W16(doc, 0x3E, 22);
        W32(doc, 0x4C, ccpText ?? cp);
        for (int i = 0; i < (stories?.Length ?? 0); i++) W32(doc, 0x50 + i * 4, stories![i]);
        W16(doc, 0x98, 0x5D);
        if (bteAt >= 0)
        {
            W32(doc, BteFc, bteAt);
            W32(doc, BteFc + 4, 12);
        }
        W32(doc, 0x1A2, clxAt);
        W32(doc, 0x1A6, lcbClx);
        return new Doc(doc, table.ToArray(), clxAt, plcAt, bteAt, pageAt);
    }

    /// <summary>Where the FIB holds fcPlcfBtePapx: the 14th pair after cbRgFcLcb at 0x98.</summary>
    const int BteFc = 0x9A + 13 * 8;

    static void W16(byte[] b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v);
    static void W32(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
    static void W32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    /// <summary>Reads a document whose table is "0Table" (or "1Table" when its flag is set).</summary>
    static string? Read(Doc d, int maxChars = 100_000)
    {
        bool one = (BinaryPrimitives.ReadUInt16LittleEndian(d.WordDocument.AsSpan(0x0A)) & 0x0200) != 0;
        return LegacyWord.FromStreams(d.WordDocument, one ? null : d.Table, one ? d.Table : null, maxChars);
    }

    static string? Text(string s, int maxChars = 100_000) => Read(Build([U(s)]), maxChars);

    // ------------------------------------------------------------------ normal documents

    [Fact]
    public void One_compressed_piece()
        => Assert.Equal("Hello world\nSecond paragraph\n", Read(Build([A("Hello world\rSecond paragraph\r")])));

    [Fact]
    public void One_unicode_piece()
        => Assert.Equal("Grüße 日本語 Ωmega\n", Read(Build([U("Grüße 日本語 Ωmega\r")])));

    [Fact]
    public void Mixed_pieces_stored_in_another_order_read_in_document_order()
    {
        var d = Build([A("First "), U("zweite Ü "), A("third "), U("четвёртый\r")], fileOrder: [3, 1, 0, 2]);
        Assert.Equal("First zweite Ü third четвёртый\n", Read(d));
    }

    [Fact]
    public void Text_after_ccpText_is_left_out()
    {
        Assert.Equal("Main\n", Read(Build([A("Main\rFootnote text\r")], ccpText: 5)));
        // The limit falls inside the second piece, and a third one after it is never read.
        Assert.Equal("Body te", Read(Build([A("Body "), U("text and more"), A("header")], ccpText: 7)));
        Assert.Equal("", Read(Build([A("Only a header")], ccpText: 0)));
    }

    [Fact]
    public void Eight_bit_pieces_use_windows_1252_for_0x80_to_0x9F()
    {
        // “quoted” €5 – dash — long … ’s
        var d = Build([A(0x93, (byte)'q', 0x94, (byte)' ', 0x80, (byte)'5', (byte)' ', 0x96, (byte)' ', 0x97, (byte)' ', 0x85, 0x92, (byte)'s', 0xE9, 0xFF)]);
        Assert.Equal("“q” €5 – — …’séÿ", Read(d));
        Assert.Equal('\u0081', LegacyWord.Cp1252(0x81)); // unassigned bytes keep their value
        Assert.Equal('Ÿ', LegacyWord.Cp1252(0x9F));
        Assert.Equal('™', LegacyWord.Cp1252(0x99));
        Assert.Equal('A', LegacyWord.Cp1252(0x41));
        Assert.Equal('\u00A0', LegacyWord.Cp1252(0xA0));
    }

    [Fact]
    public void Prc_blocks_before_the_piece_table_are_skipped()
        => Assert.Equal("ok\n", Read(Build([A("ok\r")], prcs: [[1, 2, 3, 4], [], new byte[300]])));

    // ------------------------------------------------------------------ clean-up rules

    [Theory]
    [InlineData("a\rb", "a\nb")]          // paragraph end
    [InlineData("a\vb", "a\nb")]          // line break
    [InlineData("a\fb", "a\nb")]          // page or section break
    [InlineData("a\u0007b\u0007\u0007", "a\tb\t\t")] // cell and row marks
    [InlineData("a\tb", "a\tb")]          // tab kept
    [InlineData("non\u001Ebreaking", "non-breaking")]
    [InlineData("op\u001Ftional", "optional")]
    [InlineData("no\u00A0break", "no break")]
    [InlineData("\u0001pic\u0002note\u0005ann\u0008draw", "picnoteanndraw")]
    [InlineData("\u0000\u0003\u0004\u0006\n\u000E\u0010\u0016\u001Dx", "x")] // other controls dropped
    [InlineData("  two  spaces  \r\r", "  two  spaces  \n\n")] // nothing collapsed
    public void Characters_are_cleaned_up(string word, string expected)
    {
        Assert.Equal(expected, Text(word));
        // The same rules in 8-bit pieces (the characters are all below 0x100).
        Assert.Equal(expected, Read(Build([A(word)])));
    }

    [Theory]
    [InlineData("a\u0013 HYPERLINK \"x\" \u0014link\u0015 b", "alink b")]
    [InlineData("a\u0013 PAGE \u0015b", "ab")] // no separator: nothing kept
    [InlineData("\u0013 IF \u0013 DATE \u0014today\u0015 = 1 \u0014yes\u0015.", "yes.")] // nested in the instruction
    [InlineData("\u0013 A \u0014x\u0013 B \u0014y\u0015z\u0015!", "xyz!")] // nested in the result
    [InlineData("\u0013 A \u0014x\u0013 B \u0015z\u0015!", "xz!")]
    [InlineData("stray\u0015 end\u0014s", "stray ends")] // marks outside a field are dropped
    [InlineData("a\u0013 open never closed \u0014", "a")]
    [InlineData("a\u0013 open never closed", "a")]
    [InlineData("\u0013 X \u0014r1\u0014r2\u0015", "r1r2")] // a second separator changes nothing
    public void Fields_keep_their_results(string word, string expected) => Assert.Equal(expected, Text(word));

    [Fact]
    public void Fields_span_pieces()
    {
        var d = Build([A("Go \u0013 HYPER"), U("LINK \u0014"), A("there\u0015"), U(" now")]);
        Assert.Equal("Go there now", Read(d));
    }

    [Fact]
    public void Deeply_nested_fields_hide_until_they_end()
    {
        string open = string.Concat(Enumerable.Repeat("\u0013 F \u0014", 1000));
        string close = new('\u0015', 1000);
        Assert.Equal("ab", Text("a" + new string('\u0013', 1000) + "hidden" + close + "b"));
        // In results all the way down: visible while the stack keeps each level, hidden past its depth.
        Assert.Equal("ab", Text("a" + open + "deep" + close + "b"));
        string shallow = string.Concat(Enumerable.Repeat("\u0013 F \u0014", 10));
        Assert.Equal("adeepb", Text("a" + shallow + "deep" + new string('\u0015', 10) + "b"));
    }

    // ------------------------------------------------------------------ table rows

    // Empty cells first, in the middle, last, and a whole empty row: the row mark must come from the properties.
    const string Table = "Before\r\u0007b\u0007c\u0007\uE000a\u0007\u0007c\u0007\uE000a\u0007b\u0007\u0007\uE000\u0007\u0007\u0007\uE000After\r";
    const string TableRows = "Before\n\tb\tc\na\t\tc\na\tb\t\n\t\t\nAfter\n";
    const string TableAsToday = "Before\n\tb\tc\t\ta\t\tc\t\ta\tb\t\t\t\t\t\t\tAfter\n";

    [Fact]
    public void Table_rows_come_out_one_per_line_with_empty_cells_kept()
    {
        Assert.Equal(TableRows, Read(Build([U(Table)])));
        Assert.Equal(TableRows, Read(Build([A(Table)])));
        // Without the paragraph properties every mark is a cell mark, as before.
        Assert.Equal(TableAsToday, Read(Build([U(Table)], papx: false)));
    }

    [Fact]
    public void A_cell_with_two_paragraphs()
        => Assert.Equal("one\ntwo\tx\nnext\n", Read(Build([A("one\rtwo\u0007x\u0007\uE000next\r")])));

    [Fact]
    public void Row_marks_in_16_bit_and_8_bit_pieces_stored_out_of_order()
    {
        // A row mark at the start of a 16-bit piece, one at the end of an 8-bit piece, and one in the middle of each.
        var d = Build([A("a\u0007b\u0007"), U("\uE000c\u0007\uE000ü\u0007"), A("d\u0007\uE000e\u0007"), U("f\u0007\uE000")], fileOrder: [2, 0, 3, 1]);
        Assert.Equal("a\tb\nc\nü\td\ne\tf\n", Read(d));
    }

    [Fact]
    public void A_row_mark_removes_one_tab_and_is_hidden_inside_a_field()
    {
        // Only the tab the last cell mark gave goes; a tab typed in the cell stays.
        Assert.Equal("a\tb\t\nc\n", Read(Build([A("a\u0007b\t\u0007\uE000c\r")])));
        // A row mark inside a field's instruction is hidden like any character.
        Assert.Equal("x\tr\n", Read(Build([A("x\u0007\u0013 F \uE000\u0014r\u0015\u0007\uE000")])));
    }

    [Fact]
    public void Nested_table_marks_stay_plain_paragraphs()
    {
        // Inner cells and rows end on paragraph marks carrying sprmPFInnerTableCell and sprmPFInnerTtp, not sprmPFTtp;
        // here those properties sit on a 0x07 as well, which stays a cell mark.
        byte[] inner = Papx(0, 0, 0x16, 0x24, 1, 0x4B, 0x24, 1, 0x4C, 0x24, 1);
        var d = Build([A("A\u0007in 1\rin 2\r\r\u0007\uE000")], rowPapx: inner);
        Assert.Equal("A\tin 1\nin 2\n\n\t\t", Read(d));
    }

    [Fact]
    public void SprmPFTtp_of_zero_or_behind_an_unreadable_tab_change_is_not_a_row_end()
    {
        Assert.Equal("a\t\t", Read(Build([A("a\u0007\uE000")], rowPapx: Papx(0, 0, 0x17, 0x24, 0))));
        // sprmPChgTabs with a size byte of 255 ends the reading of that paragraph's sprms.
        Assert.Equal("a\t\t", Read(Build([A("a\u0007\uE000")], rowPapx: Papx(0, 0, 0x15, 0xC6, 255, 0, 0x17, 0x24, 1))));
        // A sprm that runs past its properties: nothing after it is read.
        Assert.Equal("a\t\t", Read(Build([A("a\u0007\uE000")], rowPapx: Papx(0, 0, 0x00, 0xD6, 200, 0x17, 0x24, 1))));
        // An even-sized PapxInFkp (cb = 0 and cb') reads too.
        Assert.Equal("a\n", Read(Build([A("a\u0007\uE000")], rowPapx: Papx(0, 0, 0x16, 0x24, 1, 0x17, 0x24, 1))));
    }

    /// <summary>The table document with its PapxFkp page or PlcBtePapx changed.</summary>
    static string? Changed(Action<byte[], byte[], Doc> change, Piece? piece = null)
    {
        var d = Build([piece ?? A(Table)]);
        var doc = (byte[])d.WordDocument.Clone();
        var table = (byte[])d.Table.Clone();
        change(doc, table, d);
        return LegacyWord.FromStreams(doc, table, null, 100_000);
    }

    [Fact]
    public void Missing_or_unreadable_paragraph_properties_give_every_mark_as_a_tab()
    {
        Assert.Equal(TableRows, Changed((_, _, _) => { }));
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc + 4, 0))); // lcbPlcfBtePapx zero
        Assert.Equal(TableAsToday, Changed((doc, _, _) => { W32(doc, BteFc, 0); W32(doc, BteFc + 4, 0); }));
        Assert.Equal(TableAsToday, Changed((doc, t, _) => W32(doc, BteFc, t.Length - 4))); // past the table
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc, uint.MaxValue)));
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc + 4, uint.MaxValue)));
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc + 4, 8))); // too short for one entry
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc + 4, 13))); // not a whole number of entries
        Assert.Equal(TableAsToday, Changed((doc, _, _) => W32(doc, BteFc + 4, 14)));
    }

    [Fact]
    public void Broken_pages_and_runs_are_skipped()
    {
        int Bx(byte[] doc, Doc d, int r) => d.PageAt + (doc[d.PageAt + 511] + 1) * 4 + r * 13;
        int RowRun(byte[] doc, Doc d) // the first run whose properties are RowPapx's size
        {
            for (int r = 0; ; r++) if (doc[d.PageAt + doc[Bx(doc, d, r)] * 2] == RowPapx[0]) return r;
        }

        Assert.Equal(TableAsToday, Changed((doc, t, d) => W32(t, d.BteAt + 8, doc.Length / 512))); // page past the stream
        Assert.Equal(TableAsToday, Changed((doc, t, d) => W32(t, d.BteAt + 8, int.MaxValue)));
        Assert.Equal(TableAsToday, Changed((doc, t, d) => W32(t, d.BteAt + 8, 0))); // the FIB's page: crun 0
        Assert.Equal(TableAsToday, Changed((doc, _, d) => doc[d.PageAt + 511] = 0)); // no runs
        Assert.Equal(TableAsToday, Changed((doc, _, d) => doc[d.PageAt + 511] = 30)); // more runs than a page holds
        Assert.Equal(TableAsToday, Changed((doc, _, d) => doc[d.PageAt + 511] = 255));
        Assert.Equal(TableAsToday, Changed((doc, _, d) => { for (int r = 0; r < doc[d.PageAt + 511]; r++) doc[Bx(doc, d, r)] = 0; })); // no properties
        Assert.Equal(TableAsToday, Changed((doc, _, d) => { for (int r = 0; r < doc[d.PageAt + 511]; r++) doc[Bx(doc, d, r)] = 255; })); // properties at the page's end
        Assert.Equal(TableAsToday, Changed((doc, _, d) => { for (int r = 0; r < doc[d.PageAt + 511]; r++) doc[Bx(doc, d, r)] = 254; doc[d.PageAt + 508] = 2; })); // size past the page
        Assert.Equal(TableAsToday, Changed((doc, _, d) => { for (int r = 0; r < doc[d.PageAt + 511]; r++) doc[Bx(doc, d, r)] = 254; doc[d.PageAt + 508] = 0; doc[d.PageAt + 509] = 1; }));
        Assert.Equal(TableAsToday, Changed((doc, _, d) => { for (int r = 0; r < doc[d.PageAt + 511]; r++) doc[Bx(doc, d, r)] = 254; doc[d.PageAt + 508] = 1; })); // too short for a style
        // One row run's positions out of order or negative: that row alone becomes a tab.
        string oneRowLost = "Before\n\tb\tc\t\ta\t\tc\na\tb\t\n\t\t\nAfter\n";
        Assert.Equal(oneRowLost, Changed((doc, _, d) => { int r = RowRun(doc, d); W32(doc, d.PageAt + r * 4 + 4, BinaryPrimitives.ReadInt32LittleEndian(doc.AsSpan(d.PageAt + r * 4))); }));
        Assert.Equal(oneRowLost, Changed((doc, _, d) => W32(doc, d.PageAt + RowRun(doc, d) * 4, -5)));
        // A row run's properties made too long for the page.
        Assert.Equal(oneRowLost, Changed((doc, _, d) => { int r = RowRun(doc, d); doc[Bx(doc, d, r)] = 250; doc[d.PageAt + 500] = 0; doc[d.PageAt + 501] = 100; }));
    }

    [Fact]
    public void Pages_cut_off_by_a_short_stream_are_skipped()
    {
        var d = Build([A(Table)]);
        for (int n = d.PageAt; n < d.WordDocument.Length; n++)
            Assert.Equal(TableAsToday, LegacyWord.FromStreams(d.WordDocument.AsSpan(0, n), d.Table, null, 100_000));
    }

    [Fact]
    public void A_page_listed_100000_times_ends_fast_with_every_mark_a_tab()
    {
        var d = Build([A(Table)]);
        const int n = 100_000;
        var table = new byte[d.Table.Length + 4 * (n + 1) + 4 * n];
        d.Table.CopyTo(table, 0);
        int at = d.Table.Length;
        for (int i = 0; i <= n; i++) W32(table, at + i * 4, FibBytes);
        for (int i = 0; i < n; i++) W32(table, at + (n + 1) * 4 + i * 4, d.PageAt / 512);
        var doc = (byte[])d.WordDocument.Clone();
        W32(doc, BteFc, at);
        W32(doc, BteFc + 4, 8 * n + 4);
        var sw = Stopwatch.StartNew();
        Assert.Equal(TableAsToday, LegacyWord.FromStreams(doc, table, null, 100_000));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        // Listed as often as the stream has pages, it is still read.
        W32(doc, BteFc + 4, 8 * (doc.Length / 512) + 4);
        W32(table, at + (doc.Length / 512) * 4, FibBytes);
        for (int i = 0; i < doc.Length / 512; i++) W32(table, at + (doc.Length / 512 + 1) * 4 + i * 4, d.PageAt / 512);
        Assert.Equal(TableRows, LegacyWord.FromStreams(doc, table, null, 100_000));
    }

    [Fact]
    public void Too_many_row_ends_give_every_mark_as_a_tab_throughout()
    {
        // Pages of 29 one-byte row-end runs over the same text: the cap is counted over all of them, and once reached no
        // mark anywhere becomes a row end, not only those after the cap.
        var d = Build([A("a\u0007\u0007b\u0007\u0007\r")], papx: false);
        var fcs = Enumerable.Range(FibBytes, 30).ToArray();
        var page = FkpPage(fcs, Enumerable.Repeat<byte[]?>(Papx(0, 0, 0x17, 0x24, 1), 29).ToArray());
        string? R(int pages)
        {
            int first = (d.WordDocument.Length + 511) / 512;
            var doc = new byte[(first + pages) * 512];
            d.WordDocument.CopyTo(doc, 0);
            for (int p = 0; p < pages; p++) page.CopyTo(doc, (first + p) * 512);
            var table = new byte[d.Table.Length + 4 * (pages + 1) + 4 * pages];
            d.Table.CopyTo(table, 0);
            int at = d.Table.Length;
            for (int i = 0; i <= pages; i++) W32(table, at + i * 4, FibBytes);
            for (int i = 0; i < pages; i++) W32(table, at + (pages + 1) * 4 + i * 4, first + i);
            W32(doc, BteFc, at);
            W32(doc, BteFc + 4, 8 * pages + 4);
            return LegacyWord.FromStreams(doc, table, null, 100_000);
        }
        var sw = Stopwatch.StartNew();
        Assert.Equal("a\n\nb\n\n\n", R((LegacyWord.MaxRowEndRuns - 1) / 29));
        Assert.Equal("a\t\tb\t\t\n", R(LegacyWord.MaxRowEndRuns / 29 + 1));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Random_damage_to_the_paragraph_properties_never_throws()
    {
        var d = Build([U(Table)]);
        var random = new Random(4321);
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 3000; round++)
        {
            var doc = (byte[])d.WordDocument.Clone();
            var table = (byte[])d.Table.Clone();
            for (int k = random.Next(1, 8); k > 0; k--)
            {
                switch (random.Next(4))
                {
                    case 0: doc[d.PageAt + random.Next(512)] = (byte)random.Next(256); break;
                    case 1: table[d.BteAt + random.Next(12)] = (byte)random.Next(256); break;
                    case 2: doc[BteFc + random.Next(8)] = (byte)random.Next(256); break;
                    default: doc[d.PageAt + random.Next(2) * 4 + random.Next(4)] = (byte)random.Next(256); break;
                }
            }
            // Damage there only changes which marks are rows: the words stay.
            var text = LegacyWord.FromStreams(doc, table, null, 100_000);
            Assert.Equal("BeforebcacabAfter", text!.Replace("\t", "").Replace("\n", ""));
        }
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ stories after the main document

    // Main, footnotes, headers, macros, comments, endnotes, text boxes, header text boxes, and the closing mark.
    static readonly string[] StoryTexts =
        ["Main\r", "\u0002 Foot one\r\r", "Header\r\r", "", "\u0005Comment\r", "\u0002 End one\r\r", "\rBox one\r\rBox two\r\r", "Header box\r", "\r"];

    /// <summary>A document of those stories, one piece each, 8-bit and 16-bit in turn; counts replace the FIB's story counts.</summary>
    static Doc Stories(int[]? counts = null, string[]? texts = null)
    {
        texts ??= StoryTexts;
        var pieces = texts.Where(t => t.Length > 0).Select((t, i) => i % 2 == 0 ? A(t) : U(t)).ToArray();
        return Build(pieces, ccpText: texts[0].Length, stories: counts ?? texts.Skip(1).Take(7).Select(t => t.Length).ToArray());
    }

    [Fact]
    public void Text_boxes_footnotes_and_endnotes_follow_the_main_text_after_one_empty_line()
    {
        Assert.Equal("Main\n\nBox one\n\nBox two\n\nFoot one\n\nEnd one\n\n", Read(Stories()));
        // Main text without a closing paragraph mark, and stories that are empty or give only paragraph marks.
        string[] texts = ["Main", "\u0002 F\r", "", "", "", "\r\r", "\u0013 PAGE \u0015\r", "", "\r"];
        Assert.Equal("Main\n\nF\n", Read(Stories(texts: texts)));
        // No main text: the first story starts at the top.
        string[] noMain = ["\r\r", "\u0002 F\r", "", "", "", "", "Box\r", "", "\r"];
        Assert.Equal("Box\n\nF\n", Read(Stories(texts: noMain)));
        // Headers, comments and header text boxes stay out even when they're all there is.
        string[] hidden = ["", "", "Head\r", "", "\u0005Note\r", "", "", "HBox\r", "\r"];
        Assert.Equal("", Read(Stories(texts: hidden)));
    }

    [Fact]
    public void Table_rows_in_a_later_story()
    {
        string[] texts = ["Main\r", "", "", "", "", "", "a\u0007b\u0007\uE000\r", "", "\r"];
        Assert.Equal("Main\n\na\tb\n\n", Read(Stories(texts: texts)));
    }

    [Fact]
    public void A_story_count_past_the_pieces_or_negative_ends_the_reading()
    {
        int[] Counts(int index, int value)
        {
            var c = StoryTexts.Skip(1).Take(7).Select(t => t.Length).ToArray();
            c[index] = value;
            return c;
        }
        const string all = "Main\n\nBox one\n\nBox two\n\nFoot one\n\nEnd one\n\n";
        // Text boxes come last of the stories read, in the stream: when they, or a story before them, reach past the piece
        // table, neither they nor the stories shown after them are read.
        Assert.Equal("Main\n", Read(Stories(counts: Counts(5, 1000))));
        Assert.Equal("Main\n", Read(Stories(counts: Counts(5, int.MaxValue))));
        Assert.Equal("Main\n", Read(Stories(counts: Counts(0, 1000))));
        Assert.Equal("Main\n", Read(Stories(counts: Counts(4, int.MaxValue))));
        Assert.Equal(all, Read(Stories(counts: Counts(6, 1000)))); // header text boxes are never read
        // A negative count at or before the text boxes stops at the main text; a negative header-box count changes nothing.
        Assert.Equal("Main\n", Read(Stories(counts: Counts(1, -1))));
        Assert.Equal("Main\n", Read(Stories(counts: Counts(4, -1))));
        Assert.Equal("Main\n", Read(Stories(counts: Counts(5, int.MinValue))));
        Assert.Equal(all, Read(Stories(counts: Counts(6, -1))));
        // A main text that reaches past the pieces is read as far as they go, as before, and nothing after it.
        var d = Stories();
        var doc = (byte[])d.WordDocument.Clone();
        W32(doc, 0x4C, 1000);
        Assert.Equal("Main\n Foot one\n\nHeader\n\nComment\n End one\n\n\nBox one\n\nBox two\n\nHeader box\n\n",
            LegacyWord.FromStreams(doc, d.Table, null, 100_000));
    }

    [Fact]
    public void An_unclosed_field_does_not_hide_the_next_story()
    {
        string[] texts = ["Main \u0013 PAGE \r", "\u0002 F \u0013 X \u0014shown\r", "", "", "", "\u0002 E\r", "Box\r", "", "\r"];
        Assert.Equal("Main \n\nBox\n\nF shown\n\nE\n", Read(Stories(texts: texts)));
    }

    [Fact]
    public void The_space_after_a_note_mark_at_a_paragraph_start_is_left_out_in_notes_only()
    {
        // Two notes in each story, one with two spaces after its mark, a second paragraph in a note, and a note without
        // the mark. The same bytes in the main text and the text boxes keep their spaces.
        const string notes = "\u0002 One\r\u0002  Two\rsecond para\r\u0002 \u0002 mid\rNo mark\r";
        string[] texts = [notes, notes, "", "", "", notes, notes, "", "\r"];
        const string kept = " One\n  Two\nsecond para\n  mid\nNo mark\n";
        const string cut = "One\n Two\nsecond para\n mid\nNo mark\n";
        Assert.Equal(kept + "\n" + kept + "\n" + cut + "\n" + cut, Read(Stories(texts: texts)));
        // A mark in the middle of a paragraph, or after another dropped character, keeps the space after it.
        string[] middle = ["Main\r", "a\u0002 b\r\u0001\u0002 c\r\u0002\u0002 d\r", "", "", "", "", "", "", "\r"];
        Assert.Equal("Main\n\na b\n c\n d\n", Read(Stories(texts: middle)));
        // At the start of a cell, a line after a line break, and after a field's hidden instruction.
        string[] places = ["Main\r", "x\u0007\u0002 c\r\u0002 a\v\u0002 b\r\u0013 X \u0015\u0002 f\r", "", "", "", "", "", "", "\r"];
        Assert.Equal("Main\n\nx\tc\na\n b\nf\n", Read(Stories(texts: places)));
    }

    [Fact]
    public void The_note_space_rule_keeps_maxChars_and_the_story_separator()
    {
        string[] texts = ["Main\r", "\u0002 Foot\r", "", "", "", "\u0002 End\r", "\u0002 Box\r", "", "\r"];
        var d = Stories(texts: texts);
        string full = Read(d)!;
        Assert.Equal("Main\n\n Box\n\nFoot\n\nEnd\n", full);
        for (int n = 1; n <= full.Length + 2; n++) Assert.Equal(full[..Math.Min(n, full.Length)], Read(d, n));
        // A note story that starts the text: no empty line before it, and its space still goes.
        string[] onlyNotes = ["", "\u0002 Foot\r", "", "", "", "", "", "", "\r"];
        Assert.Equal("Foot\n", Read(Stories(texts: onlyNotes)));
    }

    [Fact]
    public void MaxChars_reached_inside_a_later_story()
    {
        var d = Stories();
        string full = Read(d)!;
        for (int n = 1; n <= full.Length + 2; n++) Assert.Equal(full[..Math.Min(n, full.Length)], Read(d, n));
        // A surrogate pair in a later story is never cut in two.
        string[] texts = ["Main\r", "", "", "", "", "", "x😀y\r", "", "\r"];
        Assert.Equal("Main\n\nx", Read(Stories(texts: texts), 8));
        Assert.Equal("Main\n\nx😀", Read(Stories(texts: texts), 9));
    }

    // ------------------------------------------------------------------ table streams, encryption, versions, limits

    [Fact]
    public void FWhichTblStm_chooses_the_table_stream()
    {
        var one = Build([A("in one table\r")], flags: 0x0200);
        var junk = new byte[one.Table.Length];
        Assert.Equal("in one table\n", LegacyWord.FromStreams(one.WordDocument, junk, one.Table, 100));
        Assert.Null(LegacyWord.FromStreams(one.WordDocument, one.Table, null, 100)); // 1Table missing

        var zero = Build([A("in zero table\r")]);
        Assert.Equal("in zero table\n", LegacyWord.FromStreams(zero.WordDocument, zero.Table, junk, 100));
        Assert.Null(LegacyWord.FromStreams(zero.WordDocument, null, zero.Table, 100)); // 0Table missing
    }

    [Fact]
    public void Encrypted_documents_give_null()
    {
        Assert.Null(Read(Build([A("secret\r")], flags: 0x0100)));
        Assert.Null(Read(Build([A("secret\r")], flags: 0x0100 | 0x8000))); // XOR obfuscation is flagged encrypted too
        Assert.Null(Read(Build([A("secret\r")], flags: 0x0300)));
    }

    [Fact]
    public void Other_flags_are_ignored()
        => Assert.Equal("text\n", Read(Build([A("text\r")], flags: 0x0001 | 0x0004 | 0x0008 | 0x00F0 | 0x1000 | 0x4000)));

    [Fact]
    public void Word_6_and_95_or_other_magic_give_null()
    {
        Assert.Null(Read(Build([A("old\r")], ident: 0xA5DC)));
        Assert.Null(Read(Build([A("old\r")], ident: 0x0000)));
        Assert.Null(Read(Build([A("old\r")], ident: 0xECA5)));
    }

    [Fact]
    public void MaxChars_stops_the_text()
    {
        var d = Build([A("abcdef"), U("ghijkl\r")]);
        Assert.Equal("abcdefghijkl\n", Read(d, 13));
        Assert.Equal("abcdefghijkl\n", Read(d, 1000));
        Assert.Equal("abcdefgh", Read(d, 8));
        Assert.Equal("a", Read(d, 1));
        Assert.Null(Read(d, 0));
        Assert.Null(Read(d, -5));
        // Dropped characters don't count, and a surrogate pair is never cut in two.
        Assert.Equal("ab", Text("\u0001a\u0013 X \u0015b c", 2));
        Assert.Equal("x", Text("x😀y", 2));
        Assert.Equal("x😀", Text("x😀y", 3));
    }

    // ------------------------------------------------------------------ hostile input

    [Fact]
    public void Truncated_streams_give_null_or_a_prefix_never_throw()
    {
        var d = Build([A("Alpha beta "), U("gamma delta\r"), A("epsilon\r")], fileOrder: [2, 0, 1]);
        string full = Read(d)!;
        Assert.Equal("Alpha beta gamma delta\nepsilon\n", full);
        for (int n = 0; n < d.WordDocument.Length; n++)
        {
            var text = LegacyWord.FromStreams(d.WordDocument.AsSpan(0, n), d.Table, null, 1000);
            Assert.True(text == null || full.StartsWith(text, StringComparison.Ordinal), $"document cut at {n}");
        }
        for (int n = 0; n < d.Table.Length; n++)
        {
            var text = LegacyWord.FromStreams(d.WordDocument, d.Table.AsSpan(0, n).ToArray(), null, 1000);
            Assert.True(text == null || text == full, $"table cut at {n}");
        }
    }

    [Fact]
    public void FIB_counts_and_offsets_outside_the_buffers_give_null()
    {
        var d = Build([A("text\r")]);
        byte[] Doc(Action<byte[]> change) { var b = (byte[])d.WordDocument.Clone(); change(b); return b; }
        string? R(byte[] doc) => LegacyWord.FromStreams(doc, d.Table, null, 100);

        Assert.Null(R(Doc(b => W32(b, 0x1A2, d.Table.Length + 1)))); // fcClx past the table
        Assert.Null(R(Doc(b => W32(b, 0x1A2, uint.MaxValue))));
        Assert.Null(R(Doc(b => W32(b, 0x1A6, uint.MaxValue)))); // lcbClx huge
        Assert.Null(R(Doc(b => W32(b, 0x1A6, d.Table.Length))));
        Assert.Null(R(Doc(b => W32(b, 0x1A6, 0)))); // no Clx
        Assert.Null(R(Doc(b => W32(b, 0x4C, -1)))); // ccpText negative
        Assert.Null(R(Doc(b => W16(b, 0x20, 0xFFFF)))); // csw past the stream
        Assert.Null(R(Doc(b => W16(b, 0x3E, 0xFFFF)))); // cslw past the stream
        Assert.Null(R(Doc(b => W16(b, 0x3E, 2)))); // too few 32-bit values for ccpText
        Assert.Null(R(Doc(b => W16(b, 0x98, 10)))); // too few pairs for fcClx
        Assert.Equal("text\n", R(Doc(b => W32(b, 0x4C, int.MaxValue)))); // ccpText larger than the pieces
    }

    [Fact]
    public void Broken_Clx_gives_null()
    {
        var d = Build([A("text\r")]);
        string? R(Action<byte[]> change) { var t = (byte[])d.Table.Clone(); change(t); return LegacyWord.FromStreams(d.WordDocument, t, null, 100); }

        Assert.Null(R(t => t[d.ClxAt] = 0x03)); // neither Prc nor Pcdt
        Assert.Null(R(t => W32(t, d.ClxAt + 1, uint.MaxValue))); // Pcdt length huge
        Assert.Null(R(t => W32(t, d.ClxAt + 1, 0))); // Pcdt length zero
        Assert.Null(R(t => W32(t, d.ClxAt + 1, 15))); // too short for one piece
        Assert.Null(R(t => { t[d.ClxAt] = 0x01; W16(t, d.ClxAt + 1, 0xFFFF); })); // a Prc past the Clx
    }

    [Fact]
    public void Pieces_out_of_bounds_or_order_give_what_was_read_before_them()
    {
        var d = Build([A("first "), U("second "), A("third")]);
        int pcd(int i) => d.PlcAt + 4 * 4 + i * 8 + 2;
        string? R(Action<byte[]> change) { var t = (byte[])d.Table.Clone(); change(t); return LegacyWord.FromStreams(d.WordDocument, t, null, 100); }

        Assert.Equal("first ", R(t => W32(t, pcd(1), 0x3FFFFFF0u))); // Unicode piece past the stream
        Assert.Equal("first ", R(t => W32(t, pcd(1), 0x7FFFFFFEu))); // 8-bit piece past the stream
        Assert.Equal("first second ", R(t => W32(t, pcd(2), (uint)(d.WordDocument.Length * 2 - 2) | 0x40000000))); // runs off the end
        Assert.Null(R(t => W32(t, pcd(0), 0x3FFFFFF0u))); // nothing before it
        Assert.Equal("first ", R(t => W32(t, d.PlcAt + 8, 2))); // positions going back
        Assert.Null(R(t => W32(t, d.PlcAt, -10))); // negative position
        Assert.Equal("first second third", R(t => W32(t, d.PlcAt + 12, int.MaxValue))); // the last piece claims too much: ccpText cuts it
        Assert.Equal("first ", R(t => W32(t, d.PlcAt + 8, int.MinValue)));
        Assert.Equal("first second third", R(t => W16(t, pcd(0) - 2, 0xFFFF))); // the Pcd's other fields are ignored

        // With ccpText past everything, a last piece claiming more than the stream holds ends the walk before it.
        var big = (byte[])d.WordDocument.Clone();
        W32(big, 0x4C, int.MaxValue);
        var t2 = (byte[])d.Table.Clone();
        W32(t2, d.PlcAt + 12, int.MaxValue);
        Assert.Equal("first second ", LegacyWord.FromStreams(big, t2, null, 100));
    }

    [Fact]
    public void Many_pieces_over_the_same_bytes_end_fast()
    {
        // 300,000 pieces of 1,000 characters each, all over the same bytes: 300 million characters claimed from a stream of
        // a few kilobytes. The work stops at the stream's size.
        const int pieces = 300_000;
        var doc = Build([A(new string('x', 1000))]).WordDocument;
        var table = new byte[5 + 4 + 12 * pieces];
        table[0] = 0x02;
        W32(table, 1, 4 + 12 * pieces);
        for (int i = 0; i <= pieces; i++) W32(table, 5 + i * 4, i * 1000);
        for (int i = 0; i < pieces; i++) W32(table, 5 + (pieces + 1) * 4 + i * 8 + 2, (FibBytes * 2) | 0x40000000);
        W32(doc, 0x1A2, 0);
        W32(doc, 0x1A6, table.Length);
        W32(doc, 0x4C, int.MaxValue);
        var sw = Stopwatch.StartNew();
        var text = LegacyWord.FromStreams(doc, table, null, int.MaxValue);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        Assert.NotNull(text);
        Assert.True(text!.Length <= doc.Length);
        Assert.Equal(1000, text.Length);
    }

    [Fact]
    public void Endless_Prc_blocks_and_zero_length_pieces_end()
    {
        // A Clx of nothing but empty Prc blocks.
        var d = Build([A("x")]);
        var table = new byte[3 * 100_000];
        for (int i = 0; i < table.Length; i += 3) table[i] = 0x01;
        var doc = (byte[])d.WordDocument.Clone();
        W32(doc, 0x1A2, 0);
        W32(doc, 0x1A6, table.Length);
        Assert.Null(LegacyWord.FromStreams(doc, table, null, 100));

        // 100,000 pieces of no characters.
        const int pieces = 100_000;
        var zero = new byte[5 + 4 + 12 * pieces];
        zero[0] = 0x02;
        W32(zero, 1, 4 + 12 * pieces);
        W32(doc, 0x1A6, zero.Length);
        Assert.Equal("", LegacyWord.FromStreams(doc, zero, null, 100));
    }

    [Fact]
    public void Random_damage_never_throws()
    {
        var d = Build([A("Alpha \u0013 F \u0014beta\u0015 "), U("gamma ü\r"), A("delta\u0007\r")], fileOrder: [1, 2, 0], prcs: [[1, 2, 3]]);
        var random = new Random(1234);
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 3000; round++)
        {
            var doc = (byte[])d.WordDocument.Clone();
            var table = (byte[])d.Table.Clone();
            for (int k = random.Next(1, 6); k > 0; k--)
            {
                // Mostly the FIB's fields and the Clx, where damage matters.
                if (random.Next(2) == 0) doc[random.Next(2) == 0 ? random.Next(0x1B0) : random.Next(doc.Length)] = (byte)random.Next(256);
                else table[random.Next(table.Length)] = (byte)random.Next(256);
            }
            _ = LegacyWord.FromStreams(doc, table, table, random.Next(1, 200));
        }
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyWord.ReadText("x.doc", 100, cts.Token));
    }

    // ------------------------------------------------------------------ the public method, on files

    /// <summary>A small Word 97-2003 file written by OLE, one 8-bit piece of text ('\r' ends a paragraph), for other tests.</summary>
    internal static byte[] DocFile(string text) => DocFile(text, encrypted: false);

    /// <summary>The same, with the FIB's fEncrypted flag set when asked (a password or XOR obfuscation).</summary>
    internal static byte[] DocFile(string text, bool encrypted)
    {
        var d = Build([A(text)], flags: (ushort)(0x0200 | (encrypted ? 0x0100 : 0)));
        return CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "WordDocument", d.WordDocument);
            CfbFixtures.Stream(root, "1Table", d.Table);
        });
    }

    static string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "rp-doc-" + Guid.NewGuid().ToString("N") + ".doc");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ReadText_of_a_file_that_is_not_a_compound_file_or_is_missing_gives_null()
    {
        string path = TempFile(Encoding.ASCII.GetBytes("This is a plain text file named .doc, not a Word document."));
        try { Assert.Null(LegacyWord.ReadText(path, 1000, CancellationToken.None)); }
        finally { File.Delete(path); }
        Assert.Null(LegacyWord.ReadText(Path.Combine(Path.GetTempPath(), "rp-missing-" + Guid.NewGuid().ToString("N") + ".doc"), 1000, CancellationToken.None));
        Assert.Null(LegacyWord.ReadText("", 1000, CancellationToken.None));
        string empty = TempFile([]);
        try { Assert.Null(LegacyWord.ReadText(empty, 1000, CancellationToken.None)); }
        finally { File.Delete(empty); }
    }

    [Fact]
    public void ReadText_reads_a_compound_file_written_by_OLE()
    {
        // The pieces large enough that WordDocument is a regular stream, as in real files.
        var d = Build([A("Report title\r" + new string('.', 5000) + "\r"), U("Zweite Seite\r")], flags: 0x0200);
        var file = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "WordDocument", d.WordDocument);
            CfbFixtures.Stream(root, "1Table", d.Table);
            CfbFixtures.Stream(root, "0Table", new byte[64]); // the other table, never read
        });
        string path = TempFile(file);
        try
        {
            var text = LegacyWord.ReadText(path, 100_000, CancellationToken.None);
            Assert.Equal("Report title\n" + new string('.', 5000) + "\nZweite Seite\n", text);
            Assert.Equal("Report", LegacyWord.ReadText(path, 6, CancellationToken.None));
        }
        finally { File.Delete(path); }

        // Without a WordDocument stream (an Excel or PowerPoint file, for one), or with an encrypted package: null.
        string other = TempFile(CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", new byte[5000])));
        string encrypted = TempFile(CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "EncryptionInfo", new byte[100]);
            CfbFixtures.Stream(root, "EncryptedPackage", new byte[5000]);
        }));
        try
        {
            Assert.Null(LegacyWord.ReadText(other, 1000, CancellationToken.None));
            Assert.Null(LegacyWord.ReadText(encrypted, 1000, CancellationToken.None));
        }
        finally
        {
            File.Delete(other);
            File.Delete(encrypted);
        }
    }
}
