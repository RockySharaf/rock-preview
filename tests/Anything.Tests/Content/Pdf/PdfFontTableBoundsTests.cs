using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

/// <summary>
/// Hostile font tables and cross-references stay within the time and memory a document may take: codespace ranges are
/// capped and matched by code length, /W tables and CMaps are parsed once per document and charged to a document-wide
/// budget, and xref tables of millions of empty subsection headers cost nothing.
/// </summary>
public class PdfFontTableBoundsTests
{
    /// <summary>One page whose resources name the fonts given (F0, F1 …), with this content.</summary>
    static byte[] Page(W w, IReadOnlyList<int> fonts, string content)
    {
        int catalog = w.Reserve(), pages = w.Reserve();
        int stream = w.AddStream("<< >>", Encoding.Latin1.GetBytes(content), "Flate");
        string names = string.Join(' ', fonts.Select((id, i) => $"/F{i} {id} 0 R"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << {names} >> >> /Contents {stream} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        return w.Write();
    }

    static string CMap(string body) =>
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Test def " + body + " endcmap CMapName currentdict /CMap defineresource pop end end";

    [Fact]
    public void An_encoding_CMap_with_thousands_of_codespace_ranges_reads_in_its_time()
    {
        var w = new W();
        // The real range first, then 20,000 more (distinct: none is a duplicate): before, every code walked all of them.
        var cs = new StringBuilder("20001 begincodespacerange <0000> <FFFF>\n");
        for (int i = 0; i < 20_000; i++) cs.Append($"<{0x8000 + i:X4}> <{0x8000 + i:X4}>\n");
        cs.Append("endcodespacerange");
        int encoding = w.AddStream("<< /Type /CMap /CMapName /Test >>", Encoding.ASCII.GetBytes(CMap(cs.ToString())), "Flate");
        int toUnicode = w.AddStream("<< >>", Encoding.ASCII.GetBytes(CMap("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <0000> endbfrange")), "Flate");
        int descendant = w.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Sample /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>");
        int font = w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Sample /Encoding {encoding} 0 R /DescendantFonts [{descendant} 0 R] /ToUnicode {toUnicode} 0 R >>");
        // 500 strings of 2,000 codes: under a run each (4,096 glyphs) and fewer than 1,024 tokens in all, so only the
        // per-glyph budget check sees the time; before, each code cost 20,000 comparisons.
        string word = string.Concat(Enumerable.Repeat("0041", 1999)) + "0020";
        var content = new StringBuilder("BT /F0 12 Tf 72 700 Td\n");
        for (int i = 0; i < 500; i++) content.Append('<').Append(word).Append("> Tj\n");
        content.Append("ET");
        var pdf = Page(w, [font], content.ToString());

        var sw = Stopwatch.StartNew();
        var doc = Read(pdf, limits: ExtractLimits.Local);
        sw.Stop();
        Assert.NotEqual(DocStatus.TimedOut, doc.Status);
        Assert.Contains("AAAAAAAA", doc.Body);
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Thousands_of_Type0_fonts_sharing_one_descendant_share_its_widths()
    {
        var w = new W();
        // One descendant with a flat /W of 65,530 widths, named by 1,000 Type0 fonts: before, each kept its own copy (about
        // 1 MB each); now the table is parsed once and a flat run is one range.
        string widths = "[0 [" + string.Join(' ', Enumerable.Repeat("1", 65_530)) + "]]";
        int wArray = w.Add(widths);
        int toUnicode = w.AddStream("<< >>", Encoding.ASCII.GetBytes(CMap("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <0000> endbfrange")), "Flate");
        int descendant = w.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Sample /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /W {wArray} 0 R >>");
        var fonts = new List<int>();
        for (int i = 0; i < 1000; i++)
            fonts.Add(w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Sample{i} /Encoding /Identity-H /DescendantFonts [{descendant} 0 R] /ToUnicode {toUnicode} 0 R >>"));
        var content = new StringBuilder("BT 72 700 Td\n");
        for (int i = 0; i < fonts.Count; i++) content.Append($"/F{i} 12 Tf <00480069> Tj\n");
        content.Append("ET");
        var pdf = Page(w, fonts, content.ToString());

        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = Read(pdf, limits: ExtractLimits.Local);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Contains("Hi", doc.Body);
        Assert.True(allocated < 200L << 20, $"allocated {allocated / (1 << 20)} MB");
    }

    [Fact]
    public void Fonts_sharing_one_large_ToUnicode_decode_it_once()
    {
        var w = new W();
        // 65,536 bfchar entries (about 0.9 MB of CMap), shared by 200 fonts: decoded once, not 200 times past the decode budget.
        var map = new StringBuilder();
        for (int start = 0; start < 65_536; start += 100)
        {
            int n = Math.Min(100, 65_536 - start);
            map.Append(n).Append(" beginbfchar\n");
            for (int c = start; c < start + n; c++) map.Append($"<{c:X4}> <{c:X4}>\n");
            map.Append("endbfchar\n");
        }
        int toUnicode = w.AddStream("<< >>", Encoding.ASCII.GetBytes(CMap("1 begincodespacerange <0000> <FFFF> endcodespacerange " + map)), "Flate");
        int descendant = w.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Sample /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>");
        var fonts = new List<int>();
        for (int i = 0; i < 200; i++)
            fonts.Add(w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Sample{i} /Encoding /Identity-H /DescendantFonts [{descendant} 0 R] /ToUnicode {toUnicode} 0 R >>"));
        var content = new StringBuilder("BT 72 700 Td\n");
        for (int i = 0; i < fonts.Count; i++) content.Append($"/F{i} 12 Tf <004F004B> Tj\n");
        content.Append("ET");

        var doc = Read(Page(w, fonts, content.ToString()), limits: ExtractLimits.Local);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Contains("OK", doc.Body);
    }

    [Fact]
    public void Millions_of_empty_xref_subsection_headers_cost_neither_memory_nor_time()
    {
        var plain = W.SimpleDocument(W.Lines("Quarterly report", "Net income rose")).Write();
        int at = IndexOf(plain, "xref\n") + 5;
        // Two million "9 0" headers before the real subsection (8 MB, under every read limit): before, 16 bytes each.
        var headers = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("9 0\n", 2_000_000)));
        byte[] padded = [.. plain.AsSpan(0, at), .. headers, .. plain.AsSpan(at)];
        // startxref still points at "xref": only the table grew.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var doc = Read(padded, limits: ExtractLimits.Local);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Contains("Net income rose", doc.Body);
        Assert.True(allocated < 24L << 20, $"allocated {allocated / (1 << 20)} MB");
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void An_xref_table_of_more_subsections_than_allowed_is_rebuilt_and_still_reads()
    {
        var plain = W.SimpleDocument(W.Lines("Quarterly report", "Net income rose")).Write();
        int at = IndexOf(plain, "xref\n") + 5;
        // 70,000 one-entry subsections of unused numbers, none carrying on from the one before: more than a table keeps.
        var sb = new StringBuilder();
        for (int i = 0; i < 70_000; i++) sb.Append(100_000 + 2 * i).Append(" 1\n0000000000 65535 f\r\n");
        byte[] padded = [.. plain.AsSpan(0, at), .. Encoding.ASCII.GetBytes(sb.ToString()), .. plain.AsSpan(at)];
        var doc = Read(padded, limits: ExtractLimits.Local);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Contains("Net income rose", doc.Body);
    }
}
