using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfStructureTests
{
    static W TwoPages()
    {
        var w = W.SimpleDocument(W.Lines("Alpha invoice 42"), W.Lines("Beta page"));
        w.Info = w.Add("<< /Title " + W.Str("Structure test") + " >>");
        return w;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Every_cross_reference_form_reads_the_same(int xref, bool objectStreams)
    {
        var doc = Read(TwoPages().Write((W.XrefKind)xref, objectStreams));
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Alpha invoice 42\fBeta page", doc.Body);
        Assert.Contains((DocField.Title, "Structure test"), doc.Fields);
    }

    [Fact]
    public void Several_uncompressed_object_streams_read_the_same()
    {
        var w = TwoPages();
        w.ObjectsPerStream = 2;
        w.CompressStructure = false;
        Assert.Equal("Alpha invoice 42\fBeta page", Read(w.Write(W.XrefKind.Stream, objectStreams: true)).Body);
    }

    [Fact]
    public void PyMuPDF_written_files_read_as_PyMuPDF_reads_them()
    {
        foreach (var file in new[] { PdfOracles.Plain, PdfOracles.ObjectStreams })
        {
            var doc = Read(file);
            Assert.Equal(DocStatus.Ok, doc.Status);
            Assert.Equal("Hello encrypted world INV-2024-001", doc.Body);
            Assert.Contains((DocField.Title, "Oracle title"), doc.Fields);
            Assert.Contains((DocField.Author, "Rocky"), doc.Fields);
            Assert.Equal(1, doc.Pages);
        }
    }

    [Fact]
    public void An_incremental_update_wins_over_the_older_object()
    {
        var original = W.SimpleDocument(W.Lines("Old words")).Write();
        // Append a new version of the content stream (object 5) and an xref section pointing back with /Prev.
        int oldStart = int.Parse(Encoding.ASCII.GetString(original).Split("startxref\n")[^1].Split('\n')[0]);
        var update = new StringBuilder();
        long at = original.Length;
        string content = W.Lines("New words");
        string obj = $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n";
        update.Append(obj);
        long xref = at + obj.Length;
        update.Append($"xref\n5 1\n{at:D10} 00000 n\r\ntrailer\n<< /Size 6 /Root 1 0 R /Prev {oldStart} >>\nstartxref\n{xref}\n%%EOF\n");
        var file = original.Concat(Ascii(update.ToString())).ToArray();
        Assert.Equal("New words", Read(file).Body);
    }

    [Fact]
    public async Task A_prev_chain_that_loops_ends()
    {
        var w = W.SimpleDocument(W.Lines("Looping trailer"));
        var file = Encoding.Latin1.GetString(w.Write());
        int xrefAt = file.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        // The trailer's /Prev points at its own section.
        file = file.Replace("trailer\n<< ", $"trailer\n<< /Prev {xrefAt} ", StringComparison.Ordinal);
        var task = Task.Run(() => Read(Ascii(file)));
        Assert.True(await Finishes(task, 10_000));
        Assert.Equal("Looping trailer", (await task).Body);
    }

    [Fact]
    public async Task A_page_tree_that_loops_gives_each_page_once()
    {
        var w = W.SimpleDocument(W.Lines("Only page"));
        // The root's kids: the page, the root itself, the page again, and a node whose kid is the root.
        int back = w.Add("<< /Type /Pages /Kids [2 0 R] /Count 1 >>");
        w.Set(2, $"<< /Type /Pages /Kids [4 0 R 2 0 R 4 0 R {back} 0 R] /Count 3 >>");
        var task = Task.Run(() => Read(w.Write()));
        Assert.True(await Finishes(task, 10_000));
        Assert.Equal("Only page", (await task).Body);
        Assert.Equal(1, (await task).Pages);
    }

    [Fact]
    public async Task Ten_thousand_deep_arrays_do_not_overflow_the_stack()
    {
        string deep = new string('[', 10_000) + new string(']', 10_000);
        var w = W.SimpleDocument(deep + " pop BT /F1 12 Tf 72 700 Td (Still here) Tj ET " + new string('[', 10_000));
        w.Set(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 /Junk " + deep + " >>");
        w.Info = w.Add("<< /Title (t) /Also <<" + new string('[', 5000) + " >>");
        var task = Task.Run(() => Read(w.Write()));
        Assert.True(await Finishes(task, 10_000));
        Assert.Equal("Still here", (await task).Body);
    }

    [Fact]
    public void A_two_gigabyte_declared_length_is_not_trusted()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string text = W.Lines("Length lies here");
        int content = w.AddRawStream("<< /Length 2147483647 >>", Ascii(text));
        int short_ = w.AddRawStream("<< /Length 3 >>", Ascii("BT /F1 12 Tf 72 600 Td (Too short length) Tj ET"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents [{content} 0 R {short_} 0 R] >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var doc = Read(w.Write());
        Assert.Equal("Length lies here\nToo short length", doc.Body);
    }

    [Fact]
    public void A_broken_cross_reference_is_rebuilt_by_scanning()
    {
        var file = Encoding.Latin1.GetString(TwoPages().Write());
        // Every offset in the table is wrong.
        var lines = file.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length == 19 && lines[i].EndsWith(" 00000 n\r", StringComparison.Ordinal)) lines[i] = "0000000003 00000 n\r";
        var doc = Read(Ascii(string.Join('\n', lines)));
        Assert.Equal("Alpha invoice 42\fBeta page", doc.Body);
    }

    /// <summary>Three pages and a 3 MB stream nobody reads; with wrongFont, the font's xref entry points 100 KB off, into that stream.</summary>
    static byte[] ThreePagesAndFiller(bool wrongFont, bool brokenStartxref = false)
    {
        var w = W.SimpleDocument(W.Lines("Page one words"), W.Lines("Page two words"), W.Lines("Page three words"));
        var filler = new byte[3 << 20];
        new Random(1).NextBytes(filler);
        // No "obj", "endstream" or "trailer" can turn up in the filler by chance.
        for (int i = 0; i < filler.Length; i++) if (filler[i] is (byte)'o' or (byte)'e' or (byte)'t') filler[i] = (byte)' ';
        w.AddRawStream($"<< /Length {filler.Length} >>", filler);
        var text = Encoding.Latin1.GetString(w.Write());
        if (wrongFont)
        {
            // The table: "xref", "0 N", then a 20-byte entry per object from 0; object 3 is the font.
            int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
            int entry = text.IndexOf('\n', xref + 5) + 1 + 3 * 20;
            long offset = long.Parse(text.AsSpan(entry, 10), System.Globalization.CultureInfo.InvariantCulture);
            text = text[..entry] + (offset + 100_000).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + text[(entry + 10)..];
        }
        if (brokenStartxref)
        {
            int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
            text = text[..at] + "999999999\n%%EOF\n";
        }
        return Ascii(text);
    }

    [Fact]
    public void A_wrong_offset_is_found_by_the_rebuild_when_the_budget_allows_it()
    {
        var doc = Read(ThreePagesAndFiller(wrongFont: true));
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(["Page one words", "Page two words", "Page three words"], Pages(doc));
    }

    [Fact]
    public void A_wrong_offset_with_a_small_byte_budget_keeps_every_page_without_scanning_the_file()
    {
        // Scanning the 3 MB file for the font would run past a 1 MB budget (a network file over its limit) and end
        // with nothing: the font is left out instead, the text reads with a stand-in font, and the document is partial.
        var file = ThreePagesAndFiller(wrongFont: true);
        var (doc, source) = ReadRecorded(file, new ExtractLimits(MaxBytesRead: 1 << 20));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(["Page one words", "Page two words", "Page three words"], Pages(doc));
        Assert.True(source.BytesRead < 256 << 10, $"read {source.BytesRead} of {file.Length}");
    }

    [Fact]
    public void A_broken_cross_reference_past_the_read_budget_is_damaged()
    {
        var file = ThreePagesAndFiller(wrongFont: false, brokenStartxref: true);
        Assert.Equal("Page one words\fPage two words\fPage three words", Read(file).Body); // rebuilt within the local budget
        var (doc, source) = ReadRecorded(file, new ExtractLimits(MaxBytesRead: 1 << 20));
        Assert.Equal(DocStatus.Damaged, doc.Status);
        Assert.True(source.BytesRead < 256 << 10, $"read {source.BytesRead} of {file.Length}");
    }

    [Fact]
    public void A_file_cut_before_its_cross_reference_is_rebuilt()
    {
        var file = TwoPages().Write();
        int cut = IndexOf(file, "xref\n0 ");
        var doc = Read(file[..cut]);
        Assert.Equal("Alpha invoice 42\fBeta page", doc.Body);
    }

    [Fact]
    public void A_broken_file_with_object_streams_is_rebuilt_from_them()
    {
        var w = TwoPages();
        w.CompressStructure = false;
        var file = w.Write(W.XrefKind.Stream, objectStreams: true);
        // Break startxref: the scan finds the object stream and the xref stream's /Root.
        string text = Encoding.Latin1.GetString(file);
        int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
        file = Ascii(text[..at] + "999999999\n%%EOF\n");
        Assert.Equal("Alpha invoice 42\fBeta page", Read(file).Body);
    }

    [Fact]
    public void Offsets_counted_from_a_late_header_still_resolve()
    {
        var w = TwoPages();
        var file = w.Write();
        // The same file behind 300 bytes of junk: every offset is now 300 short.
        var shifted = Ascii(new string('#', 299) + "\n").Concat(file).ToArray();
        Assert.Equal("Alpha invoice 42\fBeta page", Read(shifted).Body);
    }

    [Fact]
    public void Nineteen_byte_xref_entries_are_read()
    {
        var text = Encoding.Latin1.GetString(TwoPages().Write());
        int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        string table = text[xref..];
        string fixedTable = table.Replace(" n\r\n", " n\n", StringComparison.Ordinal).Replace(" f\r\n", " f\n", StringComparison.Ordinal);
        string body = text[..xref] + fixedTable;
        // startxref still points at the table (it didn't move).
        Assert.Equal("Alpha invoice 42\fBeta page", Read(Ascii(body)).Body);
    }

    [Fact]
    public void A_classic_xref_is_read_by_direct_addressing()
    {
        // 300,000 entries (6 MB of table), of which the document uses a handful.
        var w = TwoPages();
        var text = Encoding.Latin1.GetString(w.Write());
        int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        var lines = text[xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        var sb = new StringBuilder(text[..xref]);
        sb.Append("xref\n0 300000\n");
        for (int i = 0; i < count; i++) sb.Append(lines[2 + i]).Append('\n');
        for (int i = count; i < 300_000; i++) sb.Append("0000000000 65535 f\r\n");
        int trailer = Array.FindIndex(lines, l => l.StartsWith("trailer", StringComparison.Ordinal));
        sb.Append(string.Join('\n', lines[trailer..lines.Length]).Replace($"startxref\n{xref}", $"startxref\n{xref}", StringComparison.Ordinal));
        var file = Ascii(sb.ToString());
        var (doc, source) = ReadRecorded(file);
        Assert.Equal("Alpha invoice 42\fBeta page", doc.Body);
        Assert.True(source.BytesRead < 128 << 10, $"read {source.BytesRead} of {file.Length}");
    }

    [Fact]
    public void Decoded_object_streams_kept_stay_under_four_megabytes()
    {
        // Ten object streams of about 1 MB each, every one holding a small object the document needs.
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        var fonts = new List<int>();
        var rng = new Random(9);
        for (int i = 0; i < 10; i++)
        {
            var filler = new byte[1 << 20];
            rng.NextBytes(filler);
            w.Add("<" + Convert.ToHexString(filler, 0, 512 << 10) + ">"); // 1 MB of hex text, never needed
            fonts.Add(w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        }
        w.ObjectsPerStream = 2;
        var sb = new StringBuilder("BT 12 TL 72 700 Td ");
        var names = new StringBuilder();
        for (int i = 0; i < 10; i++) { sb.Append($"/F{i} 12 Tf (word{i}) Tj T* "); names.Append($"/F{i} {fonts[i]} 0 R "); }
        int content = w.AddStream("<< >>", Ascii(sb.Append("ET").ToString()));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << {names} >> >> /Contents {content} 0 R >>");
        w.KeepTopLevel(page);
        w.KeepTopLevel(pages);
        w.KeepTopLevel(catalog);
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var bytes = w.Write(W.XrefKind.Stream, objectStreams: true);

        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(bytes, budget);
        var file = PdfFile.Open(source, budget);
        long peak = 0;
        foreach (int f in fonts)
        {
            Assert.NotNull(file.ResolveDict(new PdfRef(f, 0)));
            peak = Math.Max(peak, file.CachedObjStmBytes);
        }
        Assert.True(peak <= (4 << 20) + (1100 << 10), $"kept {peak} bytes");
        Assert.True(file.CachedObjStms < 10);
        Assert.Equal(string.Join('\n', Enumerable.Range(0, 10).Select(i => $"word{i}")), Read(bytes).Body);
    }

    [Fact]
    public void Lexer_reads_strings_names_and_numbers_leniently()
    {
        var bytes = Ascii("(a\\(b\\)c (nested) \\101\\\nend) /Na#6De#20x <48 65 6c 6C 6f 7> --5 1.2.3 .5 4. +7 /");
        var lx = new PdfLexer(bytes, bytes.Length);
        Assert.True(lx.Next());
        Assert.Equal(PdfToken.String, lx.Kind);
        Assert.Equal("a(b)c (nested) Aend", Encoding.Latin1.GetString(lx.StringBytes));
        Assert.True(lx.Next());
        Assert.Equal(("Name x", PdfToken.Name), (lx.Text, lx.Kind));
        Assert.True(lx.Next());
        Assert.Equal("Hellop", Encoding.Latin1.GetString(lx.StringBytes));
        double[] numbers = [5, 1.2, 0.5, 4, 7];
        foreach (var n in numbers)
        {
            Assert.True(lx.Next());
            Assert.Equal(n, lx.Number, 6);
        }
        Assert.True(lx.Next());
        Assert.Equal((PdfToken.Name, ""), (lx.Kind, lx.Text));
        Assert.False(lx.Next());
    }

    [Fact]
    public void Parser_builds_references_and_skips_what_is_too_deep()
    {
        var bytes = Ascii("<< /A 12 0 R /B [1 2 3 R 4] /C " + new string('[', 40) + new string(']', 40) + " /D (x) >>");
        var lx = new PdfLexer(bytes, bytes.Length);
        lx.Next();
        var d = Assert.IsType<PdfDict>(PdfParser.Parse(lx, true, null, out _));
        Assert.Equal(12, Assert.IsType<PdfRef>(d["A"]).Number);
        var b = Assert.IsType<PdfArray>(d["B"]);
        Assert.Equal(3, b.Count); // 1, "2 3 R", 4
        Assert.Equal((2, 3), (Assert.IsType<PdfRef>(b[1]).Number, ((PdfRef)b[1]).Generation));
        Assert.NotNull(d["C"]);
        Assert.IsType<PdfString>(d["D"]);
    }
}
