using System.Text;
using Anything.Core.Content;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfFontTests
{
    static PdfFontTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>One page whose resources name fonts F1, F2 … (built by the callback, which returns each font's object number).</summary>
    static byte[] Page(Func<W, int[]> fonts, string content)
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        var ids = fonts(w);
        int stream = w.AddStream("<< >>", Encoding.Latin1.GetBytes(content));
        string names = string.Join(' ', ids.Select((id, i) => $"/F{i + 1} {id} 0 R"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << {names} >> >> /Contents {stream} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        return w.Write();
    }

    static string Body(Func<W, int[]> fonts, string content) => Read(Page(fonts, content)).Body;

    static string CMap(string mappings) =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def "
        + "/CMapName /Adobe-Identity-UCS def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange "
        + mappings + " endcmap CMapName currentdict /CMap defineresource pop end end";

    static int Type0(W w, string encoding, string? toUnicode, string? widths = null)
    {
        int descendant = w.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Sample /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> {widths} >>");
        string tu = "";
        if (toUnicode != null) tu = $"/ToUnicode {w.AddStream("<< >>", Encoding.ASCII.GetBytes(CMap(toUnicode)), "Flate")} 0 R";
        return w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Sample /Encoding /{encoding} /DescendantFonts [{descendant} 0 R] {tu} >>");
    }

    const string HelloMap = "4 beginbfchar <0001> <0048> <0002> <0065> <0003> <0072> <0004> <0064> endbfchar "
        + "3 beginbfrange <0020> <0025> <006C> <0030> <0032> [<0020> <0057> <D83DDE00>] <0010> <0010> <00660069> endbfrange";

    [Fact]
    public void ToUnicode_bfchar_and_bfrange_with_arrays_and_ligatures()
    {
        string body = Body(w => [Type0(w, "Identity-H", HelloMap)],
            "BT /F1 12 Tf 72 700 Td <00010002002000200023003000310023000300200004> Tj 0 -20 Td <001000200021> Tj 0 -20 Td <0032> Tj ET");
        Assert.Equal("Hello World\nfilm\n\U0001F600", body);
    }

    [Fact]
    public void Identity_fonts_without_ToUnicode_give_no_text()
    {
        var doc = Read(Page(w => [Type0(w, "Identity-H", null)], "BT /F1 12 Tf 72 700 Td <0001000200030004> Tj ET"));
        Assert.Equal(DocStatus.NoText, doc.Status);
    }

    [Fact]
    public void Base_encoding_with_differences_and_glyph_names()
    {
        string body = Body(w => [w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Custom /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
            + "/Differences [65 /Euro /uni00E9 /a.sc /f_f_i /g123 /eacute /afii10017 /Gcommaaccent] >> >>")],
            "BT /F1 12 Tf 72 700 Td (xAB CD \\200) Tj 0 -20 Td (FGH) Tj 0 -20 Td (yyyE) Tj ET");
        Assert.Equal("x€é affi €\néАĢ\nyyy", body);
    }

    [Fact]
    public void MacRoman_and_Symbol_and_Standard_encodings()
    {
        string body = Body(w =>
            [
                w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /MacRomanEncoding >>"),
                w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>"),
                w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            ],
            "BT /F1 12 Tf 72 700 Td (caf\\216 \\207) Tj ET BT /F2 12 Tf 72 680 Td (abg) Tj ET BT /F3 12 Tf 72 660 Td (don't \\256) Tj ET");
        Assert.Equal("café á\nαβγ\ndon’t fi", body);
    }

    public static TheoryData<string, int, string> PredefinedCMaps => new()
    {
        { "UniJIS-UCS2-H", -2, "東京タワー" },
        { "UniGB-UTF16-H", -16, "中文\U0002000B字" },
        { "UniKS-UCS2-H", -2, "한국어" },
        { "90ms-RKSJ-H", 932, "東京2024年" },
        { "GBK-EUC-H", 936, "中华人民2024" },
        { "KSCms-UHC-H", 949, "한국어문서" },
        { "ETen-B5-H", 950, "臺灣資料" },
    };

    [Theory]
    [MemberData(nameof(PredefinedCMaps))]
    public void Predefined_CJK_CMaps_decode_without_ToUnicode(string cmap, int codePage, string text)
    {
        byte[] codes = codePage switch
        {
            -2 or -16 => Encoding.BigEndianUnicode.GetBytes(text),
            _ => Encoding.GetEncoding(codePage).GetBytes(text),
        };
        string body = Body(w => [Type0(w, cmap, null)], $"BT /F1 12 Tf 72 700 Td <{Convert.ToHexString(codes)}> Tj ET");
        Assert.Equal(text, body);
    }

    [Fact]
    public void PyMuPDF_cjk_font_without_ToUnicode_reads()
    {
        var doc = Read(PdfOracles.UniGbUtf16);
        Assert.Equal("東京都の天気 2024年", doc.Body);
    }

    [Fact]
    public void Vertical_writing_keeps_a_column_together()
    {
        string body = Body(w => [Type0(w, "Identity-V", "2 beginbfchar <0001> <6771> <0002> <4EAC> endbfchar")],
            "BT /F1 12 Tf 300 700 Td <00010002> Tj ET");
        Assert.Equal("東京", body);
    }

    [Fact]
    public void Garbage_fonts_are_left_out_and_good_fonts_kept()
    {
        // F1 maps every code into the private use area (a broken subset font); F2 is fine.
        string body = Body(w => [Type0(w, "Identity-H", "1 beginbfrange <0000> <00FF> <E000> endbfrange"), w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")],
            "BT /F1 12 Tf 72 700 Td <0001000200030004000500060007000800090010> Tj ET BT /F2 12 Tf 72 680 Td (Readable words) Tj ET "
            + "BT /F1 12 Tf 72 660 Td <00110012001300140015> Tj ET");
        Assert.Equal("Readable words", body);
    }

    [Fact]
    public void The_garbage_guard_works_per_run_then_per_font()
    {
        // Codes 1-9 read as a-i; 0x20-0x2F aren't mapped.
        const string Map = "1 beginbfrange <0001> <0009> <0061> endbfrange";
        string Codes(params int[] c) => "<" + string.Concat(c.Select(x => x.ToString("X4"))) + ">";
        string body = Body(w => [Type0(w, "Identity-H", Map)],
            "BT /F1 12 Tf 72 700 Td " + Codes(1, 2, 0x20, 0x21) + " Tj ET"          // 50% unreadable: the run is left out
            + " BT /F1 12 Tf 72 680 Td " + Codes(3, 4, 5) + " Tj ET"                // fine
            + " BT /F1 12 Tf 72 660 Td " + Codes(1, 2, 3, 4, 5, 6, 7, 0x20, 0x21, 0x22) + " Tj ET" // 30%: kept, unreadable ones dropped
            + " BT /F1 12 Tf 72 640 Td " + Codes(1, 2, 3, 0x20, 0x21, 0x22) + " Tj ET"  // 50%: left out; the font is now 8 of 23 unreadable
            + " BT /F1 12 Tf 72 620 Td " + Codes(9, 9, 9) + " Tj ET");             // the font is garbage now: left out too
        Assert.Equal("cde\nabcdefg", body);
    }

    [Fact]
    public void Standard_14_widths_place_glyphs_when_there_are_no_widths()
    {
        // "iiii" in Helvetica 10 is 8.88 wide; "x" starts right there: one word. With a 500 fallback it would split.
        string glued = Body(w => [w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")],
            "BT /F1 10 Tf 0 0 Td (iiii) Tj ET BT /F1 10 Tf 8.88 0 Td (x) Tj ET BT /F1 10 Tf 100 0 Td (ii) Tj ET BT /F1 10 Tf 107.44 0 Td (y) Tj ET");
        Assert.Equal("iiiix ii y", glued);
    }

    [Fact]
    public void A_standard_14_font_keeps_its_glyphs_when_one_maps_outside_the_bmp()
    {
        // Helvetica with no /Widths takes its built-in widths by each glyph's text; one code maps to U+1D400 (a
        // surrogate pair): its width is a stand-in, and the font's other glyphs still read.
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange "
            + "1 beginbfchar <41> <D835DC00> endbfchar endcmap end end";
        var doc = Read(Page(w =>
            {
                int tu = w.AddStream("<< >>", Encoding.ASCII.GetBytes(cmap));
                return [w.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {tu} 0 R >>")];
            },
            "BT /F1 12 Tf 72 700 Td (Hello world A) Tj ET"));
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Hello world \U0001D400", doc.Body);
    }

    [Fact]
    public void MissingWidth_is_used_for_codes_outside_Widths()
    {
        string body = Body(w =>
            {
                int fd = w.Add("<< /Type /FontDescriptor /FontName /Custom /Flags 32 /MissingWidth 250 /Ascent 700 /Descent -200 >>");
                return [w.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /Custom /FirstChar 65 /LastChar 65 /Widths [600] /FontDescriptor {fd} 0 R /Encoding /WinAnsiEncoding >>")];
            },
            "BT /F1 10 Tf 0 0 Td (bbbb) Tj ET BT /F1 10 Tf 10 0 Td (c) Tj ET");
        Assert.Equal("bbbbc", body);
    }

    [Fact]
    public void Cid_fonts_use_DW_1000_and_W()
    {
        const string Map = "1 beginbfrange <0001> <0003> <0061> endbfrange";
        // No /W: every glyph is 1 em. Two glyphs at 10 pt end at 20.
        Assert.Equal("aab", Body(w => [Type0(w, "Identity-H", Map)], "BT /F1 10 Tf 0 0 Td <00010001> Tj ET BT /F1 10 Tf 20 0 Td <0002> Tj ET"));
        Assert.Equal("aa b", Body(w => [Type0(w, "Identity-H", Map)], "BT /F1 10 Tf 0 0 Td <00010001> Tj ET BT /F1 10 Tf 23.5 0 Td <0002> Tj ET"));
        // /W [1 [500] 2 3 250]: "ab" is 7.5 wide at 10 pt.
        Assert.Equal("abc", Body(w => [Type0(w, "Identity-H", Map, "/W [1 [500] 2 3 250]")], "BT /F1 10 Tf 0 0 Td <00010002> Tj ET BT /F1 10 Tf 7.5 0 Td <0003> Tj ET"));
    }

    [Fact]
    public void Kerns_gaps_rise_rotation_and_scaling()
    {
        string Text(string content) => Body(w => [w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")], content);
        Assert.Equal("Hello World", Text("BT /F1 12 Tf 72 700 Td [(Hello) -250 (World)] TJ ET"));
        Assert.Equal("Hello", Text("BT /F1 12 Tf 72 700 Td [(Hel) -100 (lo)] TJ ET"));
        Assert.Equal("Hello", Text("BT /F1 12 Tf 72 700 Td [(Hel) 30 (lo)] TJ ET"));
        Assert.Equal("a b", Text("BT /F1 12 Tf 5 Tw 72 700 Td (a b) Tj ET"));
        Assert.Equal("x2", Text("BT /F1 12 Tf 72 700 Td (x) Tj 4 Ts (2) Tj ET"));
        Assert.Equal("line one\nline two", Text("BT /F1 12 Tf 72 700 Td (line one) Tj 0 -14 Td (line two) Tj ET"));
        Assert.Equal("Rotated words", Text("BT /F1 12 Tf 0 1 -1 0 300 300 Tm (Rotated) Tj ( words) Tj ET"));
        Assert.Equal("Squeezed text", Text("BT /F1 12 Tf 50 Tz 72 700 Td (Squeezed) Tj (text) Tj ET".Replace("(text)", "[-700 (text)]", StringComparison.Ordinal).Replace("[-700 (text)] Tj", "[-700 (text)] TJ", StringComparison.Ordinal)));
        Assert.Equal("Scaled by cm", Text("q 2 0 0 2 0 0 cm BT /F1 6 Tf 36 350 Td (Scaled) Tj ( by cm) Tj ET Q"));
        Assert.Equal("Next column", Text("BT /F1 12 Tf 300 700 Td (Next) Tj -260 0 Td (column) Tj ET"));
    }
}
