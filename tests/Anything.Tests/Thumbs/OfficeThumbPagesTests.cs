using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using Anything.Core.Content.Extract.Office;
using Anything.Preview;
using Anything.Preview.Thumbnails;
using Anything.Tests.Content.Readers;

namespace Anything.Tests.Thumbs;

/// <summary>
/// Rock Preview's own pages for Word and Excel files without a thumbnail of their own: the old .doc and .xls through their
/// readers, .xlsx through OfficePreview.XlsxFirstRows, the sheet page drawn by ThumbnailProvider.SheetPage. Every file is
/// built here, in the test's own temp folder, and deleted after.
/// </summary>
public sealed class OfficeThumbPagesTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumb-pages-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static Bitmap? Extract(string path, int size, CancellationToken ct = default)
        => ThumbnailProvider.ExtractAsync(path, Path.GetExtension(path).ToLowerInvariant(), size, ct).GetAwaiter().GetResult();

    static void IsPageOfClass(Bitmap? page, int size)
    {
        Assert.NotNull(page);
        Assert.True(ThumbnailProvider.IsTextPage(page)); // shown, never kept on disk
        Assert.Equal(size, page.Height);
        Assert.Equal((int)(size * 0.78), page.Width);
    }

    // ------------------------------------------------------------------ old Word and Excel files

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    public void An_old_Word_file_without_a_picture_gets_a_page_of_its_first_lines(int size)
    {
        string path = Write("report.doc", LegacyWordTests.DocFile("Quarterly report\rThe first paragraph.\rAnother one.\r"));
        using var page = Extract(path, size);
        IsPageOfClass(page, size);
        // A template too.
        string dot = Write("letter.dot", LegacyWordTests.DocFile("Dear reader,\r"));
        using var dotPage = Extract(dot, size);
        IsPageOfClass(dotPage, size);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    public void An_old_Excel_file_without_a_picture_gets_a_sheet_page_of_its_first_visible_sheet(int size)
    {
        string path = Write("budget.xls", LegacyExcelTests.XlsFile(
            ("Hidden", true, [(0, 0, "not shown")], []),
            ("Data", false, [(0, 0, "Item"), (1, 0, "Rent")], [(1, 1, 1200)])));
        using var page = Extract(path, size);
        IsPageOfClass(page, size);
        string xlt = Write("budget.xlt", LegacyExcelTests.XlsFile(("S", false, [(0, 0, "Total")], [])));
        using var xltPage = Extract(xlt, size);
        IsPageOfClass(xltPage, size);
    }

    [Fact]
    public void The_sheet_chosen_is_the_first_not_hidden_else_the_first()
    {
        LegacyExcelSheet Sheet(bool hidden) => new("S", [], false, hidden, NotRead: true);
        Assert.Equal(1, ThumbnailProvider.FirstShownSheet([Sheet(true), Sheet(false), Sheet(false)]));
        Assert.Equal(0, ThumbnailProvider.FirstShownSheet([Sheet(true), Sheet(true)]));
        Assert.Equal(-1, ThumbnailProvider.FirstShownSheet([]));
    }

    [Fact]
    public void An_empty_document_and_an_empty_workbook_get_no_page()
    {
        string doc = Write("empty.doc", LegacyWordTests.DocFile("\r"));
        Assert.NotNull(LegacyWord.ReadText(doc, 100, CancellationToken.None)); // read as a Word file: no text, not "not Word"
        Assert.Null(Extract(doc, 256));

        string xls = Write("empty.xls", LegacyExcelTests.XlsFile(("Sheet1", false, [], [])));
        Assert.NotNull(LegacyExcel.ReadSheets(xls, 40, 12, CancellationToken.None));
        Assert.Null(Extract(xls, 256));
        // Only blank cells: nothing to show either.
        string blank = Write("blank.xls", LegacyExcelTests.XlsFile(("Sheet1", false, [(0, 0, "   ")], [])));
        Assert.Null(Extract(blank, 256));
    }

    [Theory]
    [InlineData("letter.doc", "{\\rtf1\\ansi\\deff0 {\\fonttbl {\\f0 Arial;}} \\f0 Dear reader, this is RTF.\\par }")]
    [InlineData("notes.doc", "Plain text saved with a .doc name.\r\nSecond line.\r\n")]
    [InlineData("table.xls", "<html><body><table><tr><td>Name</td><td>Value</td></tr></table></body></html>")]
    [InlineData("list.xls", "Name,Value\r\nRent,1200\r\n")]
    public void A_file_that_only_carries_the_extension_gets_its_text_page_as_before(string name, string content)
    {
        string path = Write(name, Encoding.UTF8.GetBytes(content));
        // The reader says "not this format" ...
        if (name.EndsWith(".doc")) Assert.Null(LegacyWord.ReadText(path, 8000, CancellationToken.None));
        else Assert.Null(LegacyExcel.ReadSheets(path, 40, 12, CancellationToken.None));
        // ... so the file goes on to the text sniff, which gave its page when only Windows was asked.
        using var before = ThumbSources.TextSniff(path, 256);
        Assert.NotNull(before);
        using var page = Extract(path, 256);
        IsPageOfClass(page, 256);
        Assert.Equal(before.Size, page!.Size);
    }

    [Fact]
    public void A_binary_file_that_is_neither_gets_no_page()
    {
        string path = Write("random.doc", Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 % 251)).ToArray());
        Assert.Null(Extract(path, 256));
        string xls = Write("random.xls", Enumerable.Range(0, 4096).Select(i => (byte)(i * 41 % 253)).ToArray());
        Assert.Null(Extract(xls, 256));
    }

    [Fact]
    public void An_old_file_over_its_size_cap_gets_no_page_of_its_own()
    {
        // The same file padded at the end: at its cap it is read, one byte over it the reader is not called (and the text
        // sniff finds a binary file). Word files have 16 MB, Excel files their own lower cap.
        void AtAndOverCap(string name, byte[] bytes, long cap)
        {
            string under = Write("under-" + name, bytes);
            using (var fs = new FileStream(under, FileMode.Open)) fs.SetLength(cap);
            using (var page = Extract(under, 256)) IsPageOfClass(page, 256);
            string over = Write("over-" + name, bytes);
            using (var fs = new FileStream(over, FileMode.Open)) fs.SetLength(cap + 1);
            Assert.Null(Extract(over, 256));
        }
        Assert.True(ThumbnailProvider.OldExcelMaxBytes < ThumbnailProvider.OldOfficeMaxBytes);
        AtAndOverCap("report.doc", LegacyWordTests.DocFile("Quarterly report\r"), ThumbnailProvider.OldOfficeMaxBytes);
        AtAndOverCap("budget.xls", LegacyExcelTests.XlsFile(("S", false, [(0, 0, "Total")], [])), ThumbnailProvider.OldExcelMaxBytes);
    }

    [Fact]
    public void A_number_wider_than_its_cell_is_drawn_as_hashes_and_text_is_cut()
    {
        // Five pixels a character, a cell 50 pixels wide (10 characters), at most 12 characters kept.
        static (string, bool) Shown(string cell) => ThumbnailProvider.CellShown(cell, 50, 12, s => s.Length * 5f);
        Assert.Equal(("12345", true), Shown("12345"));
        Assert.Equal(("-42.5", true), Shown("  -42.5 "));
        Assert.Equal(("1234567890", true), Shown("1234567890")); // exactly fits
        // Too wide: never a number with digits lost at either end.
        Assert.Equal(("###", false), Shown("12345678901"));
        Assert.Equal(("###", false), Shown("12345678901234"));
        Assert.Equal(("###", false), Shown(new string('9', 40)));
        Assert.Equal(("###", false), Shown("12345678901234\nsecond line"));
        // Text keeps its start and is cut at the end, as before.
        Assert.Equal(("abcdefghijkl", false), Shown("abcdefghijklmnop"));
        Assert.Equal(("12 apples an", false), Shown("12 apples and pears"));
        Assert.Equal(("first", false), Shown("first\nsecond"));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string doc = Write("report.doc", LegacyWordTests.DocFile("Quarterly report\r"));
        Assert.ThrowsAny<OperationCanceledException>(() => Extract(doc, 256, cts.Token));
        string xls = Write("budget.xls", LegacyExcelTests.XlsFile(("S", false, [(0, 0, "Total")], [])));
        Assert.ThrowsAny<OperationCanceledException>(() => Extract(xls, 256, cts.Token));
        string xlsx = Write("budget.xlsx", Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "x"))))));
        Assert.ThrowsAny<OperationCanceledException>(() => Extract(xlsx, 256, cts.Token));
    }

    [Fact]
    public void The_first_lines_follow_the_Word_page_limits()
    {
        var many = string.Join("\n", Enumerable.Range(1, 100).Select(i => "Line " + i));
        Assert.Equal(64, ThumbnailProvider.FirstLines(many)!.Split('\n').Length);
        Assert.Equal(200, ThumbnailProvider.FirstLines(new string('x', 1000))!.Length);
        Assert.Equal("a\nb", ThumbnailProvider.FirstLines("a\r\nb\n\n \n"));
        Assert.Null(ThumbnailProvider.FirstLines("\n \n\t\n"));
        Assert.Null(ThumbnailProvider.FirstLines(""));
    }

    // ------------------------------------------------------------------ new Excel files

    static readonly string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly string Rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static string Workbook(params (string Name, string? State)[] sheets)
        => $"<workbook xmlns=\"{S}\" xmlns:r=\"{Rns}\"><sheets>"
            + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.Name}\" sheetId=\"{i + 1}\"{(s.State == null ? "" : $" state=\"{s.State}\"")} r:id=\"rId{i + 1}\"/>"))
            + "</sheets></workbook>";

    static string Rels(int sheets)
        => "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + string.Concat(Enumerable.Range(1, sheets).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"worksheet\" Target=\"worksheets/sheet{i}.xml\"/>"))
            + "</Relationships>";

    static string SheetXml(params string[] rows) => $"<worksheet xmlns=\"{S}\"><sheetData>{string.Concat(rows)}</sheetData></worksheet>";
    static string Row(int r, params string[] cells) => $"<row r=\"{r}\">{string.Concat(cells)}</row>";
    static string Num(string at, string v) => $"<c r=\"{at}\"><v>{v}</v></c>";
    static string Shared(string at, int index) => $"<c r=\"{at}\" t=\"s\"><v>{index}</v></c>";
    static string Inline(string at, string text) => $"<c r=\"{at}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";
    static string Sst(params string[] items) => $"<sst xmlns=\"{S}\">{string.Concat(items.Select(i => $"<si>{i}</si>"))}</sst>";

    static byte[] Xlsx(string workbook, string rels, params (string Name, string Xml)[] parts)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var s = z.CreateEntry(name, CompressionLevel.Fastest).Open();
                s.Write(Encoding.UTF8.GetBytes(xml));
            }
            Add("xl/workbook.xml", workbook);
            Add("xl/_rels/workbook.xml.rels", rels);
            foreach (var (name, xml) in parts) Add(name, xml);
        }
        return ms.ToArray();
    }

    List<string?[]>? FirstRows(byte[] xlsx, int maxRows = 40, int maxColumns = 12)
        => OfficePreview.XlsxFirstRows(Write("book-" + Guid.NewGuid().ToString("N") + ".xlsx", xlsx), maxRows, maxColumns, CancellationToken.None);

    [Fact]
    public void Xlsx_first_rows_come_from_the_first_visible_sheet_with_each_kind_of_cell()
    {
        var rows = FirstRows(Xlsx(Workbook(("Secret", "hidden"), ("Data", null)), Rels(2),
            ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "hidden sheet")))),
            ("xl/worksheets/sheet2.xml", SheetXml(
                Row(1, Shared("A1", 0), Num("B1", "42"), "<c r=\"C1\" t=\"b\"><v>1</v></c>", "<c r=\"D1\" t=\"e\"><v>#DIV/0!</v></c>"),
                Row(3, "<c r=\"A3\" t=\"inlineStr\"><is><r><t>Rich </t></r><r><t>inline</t></r><rPh><t>skip</t></rPh></is></c>",
                    "<c r=\"B3\" t=\"str\"><f>A1</f><v>abc</v></c>", Shared("C3", 1), "<c r=\"D3\" t=\"b\"><v>0</v></c>"))),
            ("xl/sharedStrings.xml", Sst("<t>Name</t>", "<r><t>Bo</t></r><rPh><t>phonetic</t></rPh><r><t>ld</t></r>"))));
        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new string?[] { "Name", "42", "TRUE", "#DIV/0!" }, rows[0]);
        Assert.Empty(rows[1]); // an empty row between
        Assert.Equal(new string?[] { "Rich inline", "abc", "Bold", "FALSE" }, rows[2]);
    }

    [Fact]
    public void Xlsx_with_every_sheet_hidden_shows_the_first_and_an_empty_sheet_has_no_rows()
    {
        var rows = FirstRows(Xlsx(Workbook(("One", "hidden"), ("Two", "veryHidden")), Rels(2),
            ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "first")))),
            ("xl/worksheets/sheet2.xml", SheetXml(Row(1, Inline("A1", "second"))))));
        Assert.Equal("first", Assert.Single(Assert.Single(rows!)));

        var empty = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml())));
        Assert.NotNull(empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void Xlsx_rows_and_columns_stop_at_the_limits()
    {
        var sheet = SheetXml(Enumerable.Range(1, 60).Select(r => Row(r, Enumerable.Range(1, 20).Select(c => Num(OfficePreview.ColumnName(c) + r, (r * 100 + c).ToString())).ToArray())).ToArray());
        var rows = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", sheet)), maxRows: 40, maxColumns: 12);
        Assert.NotNull(rows);
        Assert.Equal(40, rows.Count);
        Assert.All(rows, r => Assert.Equal(12, r.Length));
        Assert.Equal("4012", rows[39][11]);
        // Cells without a reference follow the one before them.
        var plain = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml",
            SheetXml("<row><c><v>1</v></c><c><v>2</v></c></row><row><c r=\"C2\"><v>3</v></c><c><v>4</v></c></row>"))));
        Assert.Equal(new string?[] { "1", "2" }, plain![0]);
        Assert.Equal(new string?[] { null, null, "3", "4" }, plain[1]);
    }

    [Fact]
    public void Xlsx_sheet_xml_is_read_up_to_four_megabytes_and_the_rows_before_are_kept()
    {
        // Row 3 holds a cell larger than the cap: rows 1 and 2 are shown, row 3 and later never read.
        string huge = new('x', (int)OfficePreview.FirstRowsMaxSheetBytes + 1000);
        var sheet = SheetXml(Row(1, Inline("A1", "one")), Row(2, Inline("A2", "two")), Row(3, Inline("A3", huge)), Row(4, Inline("A4", "four")));
        var rows = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", sheet)));
        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);
        Assert.Equal("two", rows[1][0]);
    }

    [Fact]
    public void Xlsx_shared_strings_are_read_only_as_far_as_needed_and_within_their_caps()
    {
        string sheet1 = SheetXml(Row(1, Shared("A1", 0)));
        // The strings after the one needed are never read: a huge one there costs nothing.
        string afterHuge = Sst("<t>needed</t>", $"<t>{new string('y', (int)OfficePreview.FirstRowsMaxSharedBytes + 1000)}</t>");
        var rows = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", sheet1), ("xl/sharedStrings.xml", afterHuge)));
        Assert.Equal("needed", rows![0][0]);
        // A string needed after more than the cap of shared strings: no rows (the icon).
        string beforeHuge = Sst($"<t>{new string('y', (int)OfficePreview.FirstRowsMaxSharedBytes + 1000)}</t>", "<t>needed</t>");
        Assert.Null(FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Shared("A1", 1)))), ("xl/sharedStrings.xml", beforeHuge))));
        // An index past the cap: no rows, without reading the strings; at the cap it is read (here past the list: empty).
        Assert.Null(FirstRows(Xlsx(Workbook(("S", null)), Rels(1),
            ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Shared("A1", OfficePreview.FirstRowsMaxSharedIndex + 1)))), ("xl/sharedStrings.xml", Sst("<t>a</t>")))));
        var atCap = FirstRows(Xlsx(Workbook(("S", null)), Rels(1),
            ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Shared("A1", OfficePreview.FirstRowsMaxSharedIndex), Inline("B1", "b")))), ("xl/sharedStrings.xml", Sst("<t>a</t>"))));
        Assert.Equal(new string?[] { null, "b" }, atCap![0]);
        // No shared strings part: the cells are empty.
        var none = FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Shared("A1", 0), Inline("B1", "b"))))));
        Assert.Equal(new string?[] { null, "b" }, none![0]);
    }

    [Fact]
    public void A_broken_xlsx_gives_null()
    {
        Assert.Null(OfficePreview.XlsxFirstRows(Write("broken.xlsx", Encoding.ASCII.GetBytes("not a zip at all")), 40, 12, CancellationToken.None));
        // No workbook part, a workbook without sheets, a missing sheet part, broken XML.
        var noBook = new MemoryStream();
        using (var z = new ZipArchive(noBook, ZipArchiveMode.Create, leaveOpen: true)) z.CreateEntry("other.xml");
        Assert.Null(FirstRows(noBook.ToArray()));
        Assert.Null(FirstRows(Xlsx(Workbook(), Rels(0))));
        Assert.Null(FirstRows(Xlsx(Workbook(("S", null)), Rels(1))));
        Assert.Null(FirstRows(Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r=\"1\"><c"))));
        Assert.Null(FirstRows(Xlsx("<workbook", Rels(1))));
    }

    [Fact]
    public void Xlsx_first_rows_honour_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string path = Write("c.xlsx", Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "x"))))));
        Assert.ThrowsAny<OperationCanceledException>(() => OfficePreview.XlsxFirstRows(path, 40, 12, cts.Token));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    public void An_xlsx_without_a_picture_gets_a_sheet_page_and_its_saved_picture_still_wins(int size)
    {
        var book = Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "Total"), Num("B1", "12")))));
        using (var page = Extract(Write("book.xlsx", book), size)) IsPageOfClass(page, size);
        // An empty sheet: no page.
        Assert.Null(Extract(Write("empty.xlsx", Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml()))), size));

        // With a saved thumbnail, that picture is the thumbnail, not a page.
        using var thumb = new Bitmap(64, 48);
        using (var g = Graphics.FromImage(thumb))
        {
            g.Clear(Color.SteelBlue);
            g.FillRectangle(Brushes.Orange, 8, 8, 30, 20);
        }
        var png = new MemoryStream();
        thumb.Save(png, ImageFormat.Png);
        var withThumb = Xlsx(Workbook(("S", null)), Rels(1), ("xl/worksheets/sheet1.xml", SheetXml(Row(1, Inline("A1", "Total")))));
        withThumb = AddEntry(withThumb, "docProps/thumbnail.png", png.ToArray());
        using var saved = Extract(Write("saved.xlsx", withThumb), size);
        Assert.NotNull(saved);
        Assert.False(ThumbnailProvider.IsTextPage(saved));
    }

    [Fact]
    public void A_pptx_without_a_picture_still_gets_no_page()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = z.CreateEntry("ppt/slides/slide1.xml").Open())
            s.Write(Encoding.UTF8.GetBytes("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>"));
        Assert.Null(Extract(Write("deck.pptx", ms.ToArray()), 256));
    }

    static byte[] AddEntry(byte[] zip, string name, byte[] data)
    {
        var ms = new MemoryStream();
        ms.Write(zip);
        using (var z = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        using (var s = z.CreateEntry(name).Open())
            s.Write(data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ the sheet page

    public static TheoryData<string, int> OddSheets()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in new[] { "one cell", "ragged", "long", "right-to-left", "CJK", "null cells", "null row", "multi-line", "numbers" })
            foreach (int size in new[] { 16, 256, 512 })
                data.Add(name, size);
        return data;
    }

    static IReadOnlyList<string?[]?> Sheet(string name) => name switch
    {
        "one cell" => [["x"]],
        "ragged" => [["a"], [], [null, null, "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p"], ["q", "r"]],
        "long" => [[new string('W', 100_000), "short", new string('9', 40)]],
        "right-to-left" => [["\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD", "\u0645\u0631\u062D\u0628\u0627 123", "abc"]],
        "CJK" => [["\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8", "\uD55C\uAD6D\uC5B4", "\u4E2D\u6587", "\uD83D\uDE00"]],
        "null cells" => [[null, null, "x", null]],
        "null row" => [null, ["after a null row"]],
        "multi-line" => [["first line\nsecond line", "tab\there", "\r\nstarts with a break"]],
        "numbers" => [["1234.5", "-42", "1E+20", "0.25", "12,345"]],
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(OddSheets))]
    public void The_sheet_page_draws_odd_sheets_at_every_size(string name, int size)
    {
        using var page = ThumbnailProvider.SheetPage(Sheet(name), size);
        Assert.NotNull(page);
        Assert.True(ThumbnailProvider.IsTextPage(page));
        Assert.Equal(size, page.Height);
        Assert.Equal(Math.Max(16, (int)(size * 0.78)), page.Width);
        // The header row is drawn (light grey), not just a white page.
        Assert.NotEqual(Color.White.ToArgb(), page.GetPixel(page.Width / 2, 2).ToArgb());
    }

    [Fact]
    public void A_sheet_with_nothing_to_show_gives_no_page()
    {
        Assert.Null(ThumbnailProvider.SheetPage(null, 256));
        Assert.Null(ThumbnailProvider.SheetPage([], 256));
        Assert.Null(ThumbnailProvider.SheetPage([[], []], 512));         // zero columns
        Assert.Null(ThumbnailProvider.SheetPage([null, [null, ""], [" ", "\t"]], 256));
        // Odd sizes don't throw either.
        using var tiny = ThumbnailProvider.SheetPage([["x"]], 0);
        Assert.NotNull(tiny);
    }
}
