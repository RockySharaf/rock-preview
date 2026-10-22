using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

/// <summary>Crafted files that try to make the reader allocate, loop or nest without bound.</summary>
public class PdfHostileTests
{
    /// <summary>Reads on another thread within a deadline and measures what that thread allocated.</summary>
    static async Task<(ExtractedDoc Doc, long Allocated)> Bounded(byte[] file, ExtractLimits? limits = null, int deadlineMs = 20_000)
    {
        long allocated = 0;
        var task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var doc = Read(file, limits: limits);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return doc;
        });
        Assert.True(await Finishes(task, deadlineMs), "took too long");
        return (await task, allocated);
    }

    [Fact]
    public async Task A_huge_string_is_shown_in_bounded_memory()
    {
        var text = new StringBuilder("BT /F1 10 Tf 72 700 Td (");
        for (int i = 0; text.Length < 3_000_000; i++) text.Append("word").Append(i % 1000).Append(' ');
        text.Append(") Tj ET");
        var (doc, allocated) = await Bounded(W.SimpleDocument(text.ToString()).Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("word0 word1 word2", doc.Body);
        Assert.True(allocated < 200 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public async Task A_page_tree_deeper_than_64_is_cut_without_harm()
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
        var (doc, _) = await Bounded(w.Write());
        Assert.Equal("Shallow page", doc.Body);
    }

    [Fact]
    public async Task Forms_nested_twenty_deep_stop_at_eight()
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
        var (doc, _) = await Bounded(w.Write());
        Assert.Equal(string.Join('\n', Enumerable.Range(0, 8).Select(i => $"level{i}")), doc.Body);
    }

    [Fact]
    public async Task A_cmap_with_too_many_entries_is_capped()
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
        var (doc, allocated) = await Bounded(w.Write());
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
    public async Task A_chain_of_object_streams_each_measured_by_the_next_never_runs_the_stack_out()
    {
        // 5,000 links: loaded inside one another they would take about 50,000 frames, far past any thread's stack (a
        // stack overflow can't be caught: it would end the app). Read on a thread with a small stack, so a deep load
        // fails here rather than only on bigger files.
        var file = LengthChain(5000);
        ExtractedDoc? doc = null;
        var thread = new Thread(() => doc = Read(file), 512 << 10) { IsBackground = true };
        thread.Start();
        Assert.True(await Finishes(Task.Run(thread.Join), 20_000), "took too long");
        Assert.NotNull(doc);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Chain words", doc.Body);
    }

    [Fact]
    public async Task Thousands_of_fonts_with_huge_widths_stay_bounded()
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
        var (doc, allocated) = await Bounded(w.Write(), new ExtractLimits(MaxMilliseconds: 15_000));
        // The first font's "a" is text at once, so only a time limit on a very slow run can make it Truncated.
        Assert.Contains(doc.Status, new[] { DocStatus.Ok, DocStatus.Truncated });
        Assert.StartsWith("aaaa", doc.Body);
        // About 100 MB: 50 parsed 70,000-number arrays and 5,000 fonts' tables.
        Assert.True(allocated < 160 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public async Task A_dense_vector_page_with_a_caption_reads_quickly()
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 760 Td (Caption over the art) Tj ET\n");
        for (int i = 0; i < 200_000; i++) content.Append(i % 600).Append(' ').Append(i % 800).Append(" m ").Append(i % 590).Append(' ').Append(i % 780).Append(" l S\n");
        var file = W.SimpleDocument(content.ToString()).Write();
        var started = Environment.TickCount64;
        var (doc, _) = await Bounded(file);
        Assert.Equal("Caption over the art", doc.Body);
        Assert.True(Environment.TickCount64 - started < 5_000, $"{Environment.TickCount64 - started} ms");
    }

    [Fact]
    public async Task Inline_images_without_ei_end_the_content()
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td (Before) Tj ET ");
        for (int i = 0; i < 2000; i++) content.Append("BI /W 1 /H 1 ID xyz ");
        var (doc, _) = await Bounded(W.SimpleDocument(content.ToString()).Write());
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
    public async Task A_broken_file_of_a_million_object_headers_is_rebuilt_in_bounded_memory()
    {
        // Unbounded, the scan kept every header in a hash table: 4 million of them in 60 MB held about 290 MB.
        var file = BrokenWith("Rebuilt words", t => { for (int i = 0; i < 1_000_000; i++) t.Append('\n').Append(100_000 + i).Append(" 0 obj null endobj"); });
        var (doc, allocated) = await Bounded(file);
        Assert.Equal("Rebuilt words", doc.Body);
        Assert.Equal(DocStatus.Truncated, doc.Status); // the objects past the cap are left out
        Assert.True(allocated < 32 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public async Task A_broken_file_of_many_trailers_keeps_only_the_newest()
    {
        var file = BrokenWith("Trailer words", t => { for (int i = 0; i < 250_000; i++) t.Append("\ntrailer"); });
        var (doc, allocated) = await Bounded(file);
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
    public async Task Info_keywords_of_megabytes_keep_the_first_ones_in_bounded_memory()
    {
        var w = W.SimpleDocument(W.Lines("Keyword body"));
        w.Info = w.Add("<< /Keywords (first, second" + string.Concat(Enumerable.Repeat(",a", 2_000_000)) + ") >>");
        var (doc, allocated) = await Bounded(w.Write());
        Assert.Equal("Keyword body", doc.Body);
        Assert.StartsWith("first; second; a", doc.Fields.First(f => f.Field == DocField.Keywords).Value);
        Assert.True(allocated < 64 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public async Task Object_streams_that_lie_about_their_contents()
    {
        var w = W.SimpleDocument(W.Lines("Body text"));
        w.CompressStructure = false;
        var text = Encoding.Latin1.GetString(w.Write(W.XrefKind.Stream, objectStreams: true));
        // /N far larger than the header, /First past the end.
        var lying = text.Replace("/Type /ObjStm /N ", "/Type /ObjStm /N 99999999", StringComparison.Ordinal);
        var (doc, _) = await Bounded(Ascii(lying));
        Assert.Contains(doc.Status, new[] { DocStatus.Ok, DocStatus.NoText, DocStatus.Damaged });
        var pastEnd = System.Text.RegularExpressions.Regex.Replace(text, @"/First \d+", "/First 999999");
        var (doc2, _) = await Bounded(Ascii(pastEnd));
        Assert.Contains(doc2.Status, new[] { DocStatus.Ok, DocStatus.NoText, DocStatus.Damaged });
    }
}
