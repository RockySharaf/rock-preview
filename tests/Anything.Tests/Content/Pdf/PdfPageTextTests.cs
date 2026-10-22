using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfPageTextTests
{
    /// <summary>Every occurrence of a word, ignoring case.</summary>
    static Func<string, IReadOnlyList<(int Start, int Length)>> Finder(string word) => text =>
    {
        var list = new List<(int, int)>();
        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
            list.Add((i, word.Length));
        return list;
    };

    static IReadOnlyList<PageBox> Boxes(byte[] pdf, int page, string word)
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(pdf, budget);
        return PdfPageText.FindBoxes(source, page, Finder(word), budget);
    }

    /// <summary>A 600 × 800 page with "Find invoice here" in Helvetica 10 at (100, 600).</summary>
    static byte[] Page(string pageExtras = "", string content = "BT /F1 10 Tf 100 600 Td (Find invoice here) Tj ET", int pages = 1)
    {
        var w = new W();
        int catalog = w.Reserve(), tree = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var kids = new List<int>();
        for (int i = 0; i < pages; i++)
        {
            int stream = w.AddStream("<< >>", Ascii(i == pages - 1 ? content : "BT /F1 10 Tf 100 600 Td (Other page) Tj ET"));
            kids.Add(w.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 600 800] {pageExtras} /Resources << /Font << /F1 {font} 0 R >> >> /Contents {stream} 0 R >>"));
        }
        w.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {pages} >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        w.Root = catalog;
        return w.Write();
    }

    // "Find " is 2,223/1000 em wide in Helvetica and "invoice" 3,112: at 10 pt the word runs from x 122.23 to 153.35, and
    // from 2 pt below the baseline to 8 pt above it (the default descent and ascent), so y 598 to 608.
    const double X0 = 122.23, X1 = 153.35, Y0 = 598, Y1 = 608;

    static void Near(double expected, float actual) => Assert.InRange(actual, expected - 0.002, expected + 0.002);

    static void Box(PageBox box, double x, double y, double w, double h)
    {
        Near(x, box.X); Near(y, box.Y); Near(w, box.Width); Near(h, box.Height);
    }

    [Fact]
    public void Box_of_a_word_on_an_upright_page()
    {
        var box = Assert.Single(Boxes(Page(), 0, "invoice"));
        Box(box, X0 / 600, (800 - Y1) / 800, (X1 - X0) / 600, (Y1 - Y0) / 800);
    }

    [Fact]
    public void Box_within_the_crop_box()
    {
        var box = Assert.Single(Boxes(Page("/CropBox [50 100 550 700]"), 0, "invoice"));
        Box(box, (X0 - 50) / 500, (700 - Y1) / 600, (X1 - X0) / 500, (Y1 - Y0) / 600);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-90)]
    public void Box_on_a_rotated_page(int rotate)
    {
        var box = Assert.Single(Boxes(Page($"/Rotate {rotate}"), 0, "invoice"));
        double u0 = X0 / 600, u1 = X1 / 600, v0 = (800 - Y1) / 800, v1 = (800 - Y0) / 800;
        switch ((rotate + 360) % 360)
        {
            case 90: Box(box, 1 - v1, u0, v1 - v0, u1 - u0); break;   // clockwise: the top goes to the right
            case 180: Box(box, 1 - u1, 1 - v1, u1 - u0, v1 - v0); break;
            default: Box(box, v0, 1 - u1, v1 - v0, u1 - u0); break;   // 270: the top goes to the left
        }
    }

    [Fact]
    public void Matches_on_two_lines_give_a_box_each_and_the_page_index_counts()
    {
        var pdf = Page(content: "BT /F1 10 Tf 100 600 Td (Find invoice) Tj 0 -20 Td (invoice again) Tj ET", pages: 3);
        var boxes = Boxes(pdf, 2, "invoice");
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[1].Y > boxes[0].Y);
        Assert.Empty(Boxes(pdf, 0, "invoice"));
        Assert.Empty(Boxes(pdf, 7, "invoice"));
        // A match that runs over a line end is split into one box per line.
        Assert.Equal(2, Boxes(pdf, 2, "invoice\ninvoice").Count);
    }

    [Fact]
    public void The_ctm_and_form_matrices_move_and_scale_the_box()
    {
        var scaled = Assert.Single(Boxes(Page(content: "q 2 0 0 2 0 0 cm BT /F1 5 Tf 50 300 Td (Find invoice here) Tj ET Q"), 0, "invoice"));
        Box(scaled, X0 / 600, (800 - Y1) / 800, (X1 - X0) / 600, (Y1 - Y0) / 800);

        // The same words drawn by a form XObject whose /Matrix doubles them, placed by a CTM that moves 10 right and
        // 20 down: the form's matrix applies first, then the CTM.
        var w = new W();
        int catalog = w.Reserve(), tree = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int form = w.AddStream("<< /Type /XObject /Subtype /Form /BBox [0 0 300 400] /Matrix [2 0 0 2 0 0] >>", Ascii("BT /F1 5 Tf 50 300 Td (Find invoice here) Tj ET"));
        int stream = w.AddStream("<< >>", Ascii("q 1 0 0 1 10 -20 cm /Fm Do Q"));
        int page = w.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 {font} 0 R >> /XObject << /Fm {form} 0 R >> >> /Contents {stream} 0 R >>");
        w.Set(tree, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        w.Root = catalog;
        var inForm = Assert.Single(Boxes(w.Write(), 0, "invoice"));
        Box(inForm, (X0 + 10) / 600, (800 - (Y1 - 20)) / 800, (X1 - X0) / 600, (Y1 - Y0) / 800);
    }

    [Fact]
    public void A_page_reads_the_same_for_its_boxes_as_for_the_index()
    {
        // One Identity-H font across two pages. Page 1 has 60 good glyphs; page 2 has "invoice", a run of 22 glyphs of
        // which 12 aren't mapped, then "total". The font is judged page by page, so the index and the boxes (which
        // walk page 2 alone) leave out the same runs and see the same text.
        string Codes(string s) => "<" + string.Concat(s.Select(ch => ch == '#' ? "0030" : (ch - 'a' + 1).ToString("X4"))) + ">";
        string page1 = "BT /F1 12 Tf 72 700 Td " + Codes(string.Concat(Enumerable.Repeat("abcdefghijklmnopqrst", 3))) + " Tj ET";
        string page2 = "BT /F1 12 Tf 72 700 Td " + Codes("invoice") + " Tj ET BT /F1 12 Tf 72 680 Td " + Codes("############abcdefghij")
            + " Tj ET BT /F1 12 Tf 72 660 Td " + Codes("total") + " Tj ET";
        var w = new W();
        int catalog = w.Reserve(), tree = w.Reserve();
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 beginbfrange <0001> <001A> <0061> endbfrange endcmap end end"; // 1-26 are a-z; 0x30 isn't mapped
        int tu = w.AddStream("<< >>", Ascii(cmap));
        int cid = w.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X >>");
        int font = w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {tu} 0 R >>");
        var kids = new List<int>();
        foreach (var content in new[] { page1, page2 })
        {
            int stream = w.AddStream("<< >>", Ascii(content));
            kids.Add(w.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {stream} 0 R >>"));
        }
        w.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count 2 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        w.Root = catalog;
        var pdf = w.Write();

        var indexed = Pages(Read(pdf));
        Assert.Equal(2, indexed.Length);
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(pdf, budget);
        string? seen = null;
        var boxes = PdfPageText.FindBoxes(source, 1, text => { seen = text; return Finder("invoice")(text); }, budget);
        Assert.Equal(indexed[1], seen);
        Assert.Equal("invoice", seen);
        Assert.Single(boxes);
    }

    [Fact]
    public void Locked_or_damaged_files_give_no_boxes()
    {
        Assert.Empty(Boxes(PdfOracles.UserPassword, 0, "hello"));
        Assert.Empty(Boxes(Ascii("not a pdf at all"), 0, "x"));
    }

    [Fact]
    public void Oracle_boxes_match_the_text_position()
    {
        // PyMuPDF wrote "Hello encrypted world …" at x 20 on the 300 × 200 page, baseline 60 below the top.
        var box = Assert.Single(Boxes(PdfOracles.Aes256, 0, "encrypted"));
        Assert.InRange(box.X * 300, 50, 51.5);
        Assert.InRange((box.Y + box.Height) * 200, 61, 63);
    }
}
