using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

/// <summary>Crafted files that try to make the reader allocate, loop or nest without bound.</summary>
public class PdfHostileTests
{
    /// <summary>
    /// Reads on a thread of its own and measures what that thread allocated. It may use no more than its 15 s budget and a
    /// little more, in CPU time: these run beside the rest of the suite, where the wall clock can stall for seconds.
    /// </summary>
    static (ExtractedDoc Doc, long Allocated) Bounded(byte[] file, ExtractLimits? limits = null, int cpuLimitMs = 20_000)
    {
        var doc = CpuBound.Measure(() => Read(file, limits: limits), out double cpuMs, out long allocated);
        Assert.True(cpuMs < cpuLimitMs, $"took {cpuMs:F0} ms of CPU");
        return (doc, allocated);
    }

    [Fact]
    public void A_huge_string_is_shown_in_bounded_memory()
    {
        var text = new StringBuilder("BT /F1 10 Tf 72 700 Td (");
        for (int i = 0; text.Length < 3_000_000; i++) text.Append("word").Append(i % 1000).Append(' ');
        text.Append(") Tj ET");
        var (doc, allocated) = Bounded(W.SimpleDocument(text.ToString()).Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("word0 word1 word2", doc.Body);
        Assert.True(allocated < 200 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_page_tree_deeper_than_64_is_cut_without_harm()
    {
        var w = W.SimpleDocument(W.Lines("Shallow page"));
        // A chain of 200 /Pages nodes with a page at the bottom, beside the shallow page.
        int parent = 2, first = 0;
        var chain = new List<int>();
        for (int i = 0; i < 200; i++) chain.Add(w.Reserve());
        int deepContent = w.AddStream("<< >>", Ascii(W.Lines("Deep page")));
        int deepPage = w.Add($"<< /Type /Page /Parent {chain[^1]} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {deepContent} 0 R >>");
        for (int i = 0; i < chain.Count; i++)
        {
            int kid = i + 1 < chain.Count ? chain[i + 1] : deepPage;
            w.Set(chain[i], $"<< /Type /Pages /Parent {(i == 0 ? parent : chain[i - 1])} 0 R /Kids [{kid} 0 R] /Count 1 >>");
        }
        first = chain[0];
        w.Set(2, $"<< /Type /Pages /Kids [4 0 R {first} 0 R] /Count 2 >>");
        var (doc, _) = Bounded(w.Write());
        Assert.Equal("Shallow page", doc.Body);
    }

    [Fact]
    public void Forms_nested_twenty_deep_stop_at_eight()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var forms = Enumerable.Range(0, 20).Select(_ => w.Reserve()).ToList();
        for (int i = 0; i < forms.Count; i++)
        {
            string next = i + 1 < forms.Count ? $"/XObject << /N {forms[i + 1]} 0 R >>" : "";
            w.SetStream(forms[i], $"<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] /Resources << /Font << /F {font} 0 R >> {next} >> >>",
                Ascii($"BT /F 10 Tf 72 {700 - i * 20} Td (level{i}) Tj ET" + (i + 1 < forms.Count ? " /N Do" : "")));
        }
        int content = w.AddStream("<< >>", Ascii("/N Do"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /N {forms[0]} 0 R >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var (doc, _) = Bounded(w.Write());
        Assert.Equal(string.Join('\n', Enumerable.Range(0, 8).Select(i => $"level{i}")), doc.Body);
    }

    [Fact]
    public void A_cmap_with_too_many_entries_is_capped()
    {
        var map = new StringBuilder("/CIDInit /ProcSet findresource begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 100000 beginbfchar ");
        for (int i = 0; i < 100_000; i++) map.Append($"<{i:X4}> <{0x4E00 + i % 20000:X4}> ");
        map.Append("endbfchar endcmap end");
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int tu = w.AddStream("<< >>", Ascii(map.ToString()), "Flate");
        int cid = w.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X >>");
        int font = w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {tu} 0 R >>");
        int content = w.AddStream("<< >>", Ascii("BT /F1 12 Tf 72 700 Td <00010002> Tj ET"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var (doc, allocated) = Bounded(w.Write());
        Assert.Equal("丁丂", doc.Body);
        Assert.True(allocated < 100 << 20, $"allocated {allocated >> 20} MB");
    }

    /// <summary>
    /// A one-page document whose font sits in the first of n object streams, and each object stream's /Length is an
    /// object stored in the next one: resolving the font would load all n inside one another.
    /// </summary>
    static byte[] LengthChain(int n)
    {
        var file = new MemoryStream();
        void Text(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        // Objects: 1 catalog, 2 pages, 3 page, 4 font (in stream 6), 5 content, 6 … 5 + n the object streams,
        // 6 + n … 4 + 2n the lengths (length k, of stream 5 + k, lives in stream 6 + k), 5 + 2n the xref stream.
        int xrefNumber = 5 + 2 * n;
        var offsets = new long[xrefNumber + 1];
        Text("%PDF-1.7\n");
        offsets[1] = file.Position; Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position; Text("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = file.Position; Text("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        const string Content = "BT /F1 12 Tf 72 700 Td (Chain words) Tj ET";
        offsets[5] = file.Position; Text($"5 0 obj\n<< /Length {Content.Length} >>\nstream\n{Content}\nendstream\nendobj\n");
        // Stream k holds the font (k = 1) or the length of stream k - 1.
        var data = new string[n + 1];
        data[1] = "4 0 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        for (int k = 2; k <= n; k++) data[k] = $"{5 + n + k - 1} 0 {data[k - 1].Length}";
        for (int k = 1; k <= n; k++)
        {
            int member = k == 1 ? 4 : 5 + n + k - 1;
            string length = k < n ? $"{5 + n + k} 0 R" : data[k].Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            offsets[5 + k] = file.Position;
            Text($"{5 + k} 0 obj\n<< /Type /ObjStm /N 1 /First {$"{member} 0 ".Length} /Length {length} >>\nstream\n{data[k]}\nendstream\nendobj\n");
        }
        var rows = new MemoryStream();
        void Row(byte type, long field2, int field3)
        {
            rows.WriteByte(type);
            for (int shift = 24; shift >= 0; shift -= 8) rows.WriteByte((byte)(field2 >> shift));
            rows.WriteByte((byte)(field3 >> 8));
            rows.WriteByte((byte)field3);
        }
        for (int i = 0; i <= xrefNumber; i++)
        {
            if (i == 0) Row(0, 0, 65535);
            else if (i == 4) Row(2, 6, 0);
            else if (i > 5 + n && i < xrefNumber) Row(2, i - n + 1, 0); // length k (object 5 + n + k) is in stream 6 + k
            else Row(1, offsets[i], 0);
        }
        var xref = rows.ToArray();
        long startxref = file.Position;
        Text($"{xrefNumber} 0 obj\n<< /Type /XRef /Size {xrefNumber + 1} /W [1 4 2] /Root 1 0 R /Length {xref.Length} >>\nstream\n");
        file.Write(xref);
        Text($"\nendstream\nendobj\nstartxref\n{startxref}\n%%EOF\n");
        return file.ToArray();
    }

    [Fact]
    public void A_chain_of_object_streams_each_measured_by_the_next_never_runs_the_stack_out()
    {
        // 5,000 links: loaded inside one another they would take about 50,000 frames, far past any thread's stack (a
        // stack overflow can't be caught: it would end the app). Read on a thread with a small stack, so a deep load
        // fails here rather than only on bigger files.
        var file = LengthChain(5000);
        ExtractedDoc? doc = null;
        var thread = new Thread(() => doc = Read(file), 512 << 10) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(CpuBound.HangMilliseconds), "took too long");
        Assert.NotNull(doc);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Chain words", doc.Body);
    }

    [Fact]
    public void Thousands_of_fonts_with_huge_widths_stay_bounded()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        string widths = "[" + string.Join(' ', Enumerable.Repeat("1000", 70_000)) + "]";
        int shared = w.Add(widths);
        var names = new StringBuilder();
        var show = new StringBuilder("BT 72 700 Td ");
        for (int i = 0; i < 5000; i++)
        {
            int f = w.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /F{i} /FirstChar 0 /Widths {(i < 50 ? widths : $"{shared} 0 R")} >>");
            names.Append($"/F{i} {f} 0 R ");
            show.Append($"/F{i} 1 Tf (a) Tj ");
        }
        int content = w.AddStream("<< >>", Ascii(show.Append("ET").ToString()), "Flate");
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << {names} >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var (doc, allocated) = Bounded(w.Write(), new ExtractLimits(MaxMilliseconds: 15_000));
        // The first font's "a" is text at once, so only a time limit on a very slow run can make it Truncated.
        Assert.Contains(doc.Status, new[] { DocStatus.Ok, DocStatus.Truncated });
        Assert.StartsWith("aaaa", doc.Body);
        // About 100 MB: 50 parsed 70,000-number arrays and 5,000 fonts' tables.
        Assert.True(allocated < 160 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_dense_vector_page_with_a_caption_reads_quickly()
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 760 Td (Caption over the art) Tj ET\n");
        for (int i = 0; i < 200_000; i++) content.Append(i % 600).Append(' ').Append(i % 800).Append(" m ").Append(i % 590).Append(' ').Append(i % 780).Append(" l S\n");
        var file = W.SimpleDocument(content.ToString()).Write();
        // Timed in CPU time: a page that is slow to read is slow every time, a busy PC only now and then.
        var doc = CpuBound.Measure(() => Read(file), out double cpuMs, out _);
        Assert.Equal("Caption over the art", doc.Body);
        Assert.True(cpuMs < 5_000, $"{cpuMs:F0} ms of CPU");
    }

    [Fact]
    public void Inline_images_without_ei_end_the_content()
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td (Before) Tj ET ");
        for (int i = 0; i < 2000; i++) content.Append("BI /W 1 /H 1 ID xyz ");
        var (doc, _) = Bounded(W.SimpleDocument(content.ToString()).Write());
        Assert.Equal("Before", doc.Body);
    }

    /// <summary>A one-page document, then extra text, then a broken startxref: the cross-reference must be rebuilt by scanning.</summary>
    static byte[] BrokenWith(string words, Action<StringBuilder> extra)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(W.SimpleDocument(W.Lines(words)).Write()));
        extra(text);
        text.Append("\nstartxref\n999999999\n%%EOF\n");
        return Ascii(text.ToString());
    }

    [Fact]
    public void A_broken_file_of_a_million_object_headers_is_rebuilt_in_bounded_memory()
    {
        // Unbounded, the scan kept every header in a hash table: 4 million of them in 60 MB held about 290 MB.
        var file = BrokenWith("Rebuilt words", t => { for (int i = 0; i < 1_000_000; i++) t.Append('\n').Append(100_000 + i).Append(" 0 obj null endobj"); });
        var (doc, allocated) = Bounded(file);
        Assert.Equal("Rebuilt words", doc.Body);
        Assert.Equal(DocStatus.Truncated, doc.Status); // the objects past the cap are left out
        Assert.True(allocated < 32 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_broken_file_of_many_trailers_keeps_only_the_newest()
    {
        var file = BrokenWith("Trailer words", t => { for (int i = 0; i < 250_000; i++) t.Append("\ntrailer"); });
        var (doc, allocated) = Bounded(file);
        Assert.Equal("Trailer words", doc.Body); // the catalog the scan found stands in for the trailers' /Root
        Assert.True(allocated < 32 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void An_object_header_across_the_scan_chunks_keeps_its_whole_number()
    {
        // "12 0 obj" with its "1" as the last byte of the first 64 KB: a scan of the next chunk alone saw object 2 (the
        // page tree) there and lost every page.
        var file = BrokenWith("Straddling words", t =>
        {
            t.Append("\n%");
            t.Append(' ', 65_534 - t.Length);
            t.Append("\n12 0 obj\n(junk)\nendobj\n%").Append(' ', 2000);
        });
        Assert.Equal((byte)'1', file[65_535]);
        var doc = Read(file);
        Assert.Equal("Straddling words", doc.Body);
    }

    [Fact]
    public void Info_keywords_of_megabytes_keep_the_first_ones_in_bounded_memory()
    {
        var w = W.SimpleDocument(W.Lines("Keyword body"));
        w.Info = w.Add("<< /Keywords (first, second" + string.Concat(Enumerable.Repeat(",a", 2_000_000)) + ") >>");
        var (doc, allocated) = Bounded(w.Write());
        Assert.Equal("Keyword body", doc.Body);
        Assert.StartsWith("first; second; a", doc.Fields.First(f => f.Field == DocField.Keywords).Value);
        Assert.True(allocated < 64 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_ToUnicode_range_with_a_destination_of_megabytes_keeps_each_glyph_short()
    {
        var w = W.SimpleDocument(W.Lines("Intro"));
        // One bfrange over all 256 codes whose destination is 500,000 characters, shared by 8 simple fonts: before, each
        // font kept a full copy per code (about 255 MB a font).
        string destination = string.Concat(Enumerable.Repeat("0058", 499_999)) + "0041";
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange "
            + "1 beginbfrange <00> <FF> <" + destination + "> endbfrange endcmap end end";
        int toUnicode = w.AddStream("<< >>", Ascii(cmap), "Flate");
        var fonts = Enumerable.Range(0, 8).Select(i => w.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Sample{i} /ToUnicode {toUnicode} 0 R >>")).ToList();
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td (Intro) Tj ET\n");
        for (int i = 0; i < fonts.Count; i++) content.Append($"BT /G{i} 12 Tf 72 {600 - 20 * i} Td <0102> Tj ET\n");
        int stream = w.AddStream("<< >>", Ascii(content.ToString()));
        string names = string.Join(' ', fonts.Select((id, i) => $"/G{i} {id} 0 R"));
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R {names} >> >> /Contents {stream} 0 R >>");

        var (doc, allocated) = Bounded(w.Write());
        Assert.Equal(DocStatus.Ok, doc.Status);
        // Each code's text is the destination's first 64 characters with the last one moved on by the code.
        Assert.Contains(new string('X', 63) + "Y" + new string('X', 63) + "Z", doc.Body);
        Assert.DoesNotContain(new string('X', 65), doc.Body);
        Assert.True(allocated < 64 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_glyph_text_cut_never_splits_a_surrogate_pair()
    {
        string text = new string('a', PdfCMap.MaxGlyphChars - 1) + "\U0001F600" + "tail";
        string cut = PdfCMap.CapGlyph(text);
        Assert.Equal(PdfCMap.MaxGlyphChars - 1, cut.Length);
        Assert.Equal("short", PdfCMap.CapGlyph("short"));
    }

    [Fact]
    public void A_choice_field_of_many_huge_values_is_cut_before_it_is_joined()
    {
        var w = W.SimpleDocument(W.Lines("Choices"));
        // 256 references to one string of 4 million characters: joined whole, a string past .NET's longest (out of memory).
        int big = w.Add("(" + new string('A', 4_000_000) + ")");
        string refs = string.Join(' ', Enumerable.Repeat($"{big} 0 R", 256));
        int widget = w.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /V [{refs}] /Rect [200 700 400 720] >>");
        int single = w.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /V {big} 0 R /Rect [200 600 400 620] >>");
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [{widget} 0 R {single} 0 R] >>");

        var (doc, allocated) = Bounded(w.Write());
        Assert.StartsWith("Choices\nAAAA", doc.Body);
        // Two values of at most 65,536 characters each, and the line breaks between the blocks.
        Assert.True(doc.Body.Length <= 2 * ExtractSink.MaxFieldChars * 16 + 16, $"body of {doc.Body.Length} characters");
        Assert.True(allocated < 96 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void A_UTF16_field_value_keeps_all_of_its_characters_up_to_the_value_cap()
    {
        var w = W.SimpleDocument(W.Lines("Comment"));
        // 40,000 Cyrillic characters in UTF-16 (80,000 bytes): a byte cut sized one byte a character kept only the first half.
        string hex = "FEFF" + string.Concat(Enumerable.Repeat("0416", 40_000)) + "042F";
        int widget = w.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /V <{hex}> /Rect [200 700 400 720] >>");
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [{widget} 0 R] >>");

        var (doc, _) = Bounded(w.Write());
        Assert.Contains(new string('Ж', 40_000) + "Я", doc.Body);
    }

    [Fact]
    public void Object_streams_that_lie_about_their_contents()
    {
        var w = W.SimpleDocument(W.Lines("Body text"));
        w.CompressStructure = false;
        var text = Encoding.Latin1.GetString(w.Write(W.XrefKind.Stream, objectStreams: true));
        // /N far larger than the header, /First past the end.
        var lying = text.Replace("/Type /ObjStm /N ", "/Type /ObjStm /N 99999999", StringComparison.Ordinal);
        var (doc, _) = Bounded(Ascii(lying));
        Assert.Contains(doc.Status, new[] { DocStatus.Ok, DocStatus.NoText, DocStatus.Damaged });
        var pastEnd = System.Text.RegularExpressions.Regex.Replace(text, @"/First \d+", "/First 999999");
        var (doc2, _) = Bounded(Ascii(pastEnd));
        Assert.Contains(doc2.Status, new[] { DocStatus.Ok, DocStatus.NoText, DocStatus.Damaged });
    }
}
