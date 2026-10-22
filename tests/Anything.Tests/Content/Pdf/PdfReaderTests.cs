using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfReaderTests
{
    [Fact]
    public void Reads_text_of_every_page_with_page_breaks_and_lines()
    {
        var doc = Read(W.SimpleDocument(W.Lines("Quarterly invoice INV-2024-001", "Second line"), W.Lines("Page two words")).Write());
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(2, doc.Pages);
        Assert.Equal(1, doc.ExtractorVersion);
        Assert.Equal(["Quarterly invoice INV-2024-001\nSecond line", "Page two words"], Pages(doc));
    }

    [Fact]
    public void A_page_without_fonts_still_counts_and_keeps_the_page_numbers()
    {
        // Pages 1 and 3 are scans (an image, no fonts): the text pages keep their numbers.
        var doc = Read(W.SimpleDocument(null, W.Lines("Text on page two"), null, W.Lines("Page four")).Write());
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(4, doc.Pages);
        Assert.Equal(["", "Text on page two", "", "Page four"], Pages(doc));
    }

    [Fact]
    public void The_content_of_a_page_without_fonts_is_never_read()
    {
        // Page 1 is outlined art: 600 KB of path operators and no fonts in its resources.
        var w = W.SimpleDocument("placeholder", W.Lines("Second page text"));
        var art = new StringBuilder();
        for (int i = 0; art.Length < 600_000; i++) art.Append(i % 500).Append(' ').Append(i % 700).Append(" m 10 20 l S\n");
        int content = w.AddRawStream($"<< /Length {art.Length} >>", Ascii(art.ToString()));
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /ProcSet [/PDF] >> /Contents {content} 0 R >>");
        var file = w.Write();
        int start = IndexOf(file, $"<< /Length {art.Length} >>\nstream\n") + $"<< /Length {art.Length} >>\nstream\n".Length;
        var (doc, source) = ReadRecorded(file);
        Assert.Equal(["", "Second page text"], Pages(doc));
        Assert.True(source.BytesWithin(start, start + art.Length) <= 16384, $"read {source.BytesWithin(start, start + art.Length)} bytes of the art");
    }

    [Fact]
    public void A_document_of_scans_only_is_no_text_with_its_page_count()
    {
        var doc = Read(W.SimpleDocument(null, null, null).Write());
        Assert.Equal(DocStatus.NoText, doc.Status);
        Assert.Equal(3, doc.Pages);
        Assert.Equal("", doc.Body);
    }

    [Fact]
    public void Image_xobject_streams_are_never_read()
    {
        // A 2 MB image (its data starts with a marker) drawn by a page that also has text.
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pixels = new byte[2 << 20];
        new Random(5).NextBytes(pixels);
        int image = w.AddRawStream($"<< /Type /XObject /Subtype /Image /Width 1024 /Height 2048 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {pixels.Length} >>", pixels);
        int content = w.AddStream("<< >>", Ascii("q 100 0 0 100 0 0 cm /Im1 Do Q BT /F1 12 Tf 72 700 Td (Caption under the photo) Tj ET"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var file = w.Write();
        int dataStart = IndexOf(file, "/Length 2097152 >>\nstream\n") + "/Length 2097152 >>\nstream\n".Length;
        var (doc, source) = ReadRecorded(file);
        Assert.Equal("Caption under the photo", doc.Body);
        // At most the block that holds the image's dictionary touches its data: the stream is never read through.
        Assert.True(source.BytesWithin(dataStart, dataStart + pixels.Length) <= 16384, $"read {source.BytesWithin(dataStart, dataStart + pixels.Length)} bytes of the image");
        Assert.True(doc.BytesRead < 64 << 10, $"read {doc.BytesRead} bytes of {file.Length}");
    }

    [Fact]
    public void Info_fields_decode_pdfdoc_utf16_and_utf8_strings()
    {
        var w = W.SimpleDocument(W.Lines("Body text"));
        w.Info = w.Add("<< /Title " + W.Str("Résumé de Café") + " /Author (Rocky \\(RS\\)) /Subject <EFBBBF4772c3bc6e6520416c6d> /Keywords (alpha, beta; gamma) /Creator (Writer 1.0) /Producer (ignored) >>");
        var doc = Read(w.Write());
        var fields = doc.Fields.ToDictionary(f => f.Field, f => f.Value);
        Assert.Equal("Résumé de Café", fields[DocField.Title]);
        Assert.Equal("Rocky (RS)", fields[DocField.Author]);
        Assert.Equal("Grüne Alm", fields[DocField.Subject]);
        Assert.Equal("alpha; beta; gamma", fields[DocField.Keywords]);
        Assert.Equal("Writer 1.0", fields[DocField.CreatorTool]);
    }

    [Fact]
    public void Xmp_metadata_gives_fields_and_fonts()
    {
        var w = W.SimpleDocument(W.Lines("Body"));
        string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:xmp='http://ns.adobe.com/xap/1.0/'>"
            + "<dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Spring catalogue</rdf:li></rdf:Alt></dc:title>"
            + "<dc:subject><rdf:Bag><rdf:li>print</rdf:li><rdf:li>catalogue</rdf:li></rdf:Bag></dc:subject>"
            + "<xmp:CreatorTool>InDesign</xmp:CreatorTool></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='r'?>";
        int meta = w.AddStream("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(xmp), "Flate");
        w.Set(w.Root, $"<< /Type /Catalog /Pages 2 0 R /Metadata {meta} 0 R >>");
        var fields = Read(w.Write()).Fields.ToDictionary(f => f.Field, f => f.Value);
        Assert.Equal("Spring catalogue", fields[DocField.Title]);
        Assert.Equal("print; catalogue", fields[DocField.Keywords]);
        Assert.Equal("InDesign", fields[DocField.CreatorTool]);
    }

    [Fact]
    public void Form_xobjects_are_walked_with_their_matrix_and_resources_and_never_into_themselves()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve(), form = w.Reserve(), inner = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>");
        // The form draws text, a nested form, and itself (a loop that must not be followed).
        w.SetStream(form, $"<< /Type /XObject /Subtype /Form /BBox [0 0 500 500] /Matrix [1 0 0 1 10 10] /Resources << /Font << /T {font} 0 R >> /XObject << /Self {form} 0 R /In {inner} 0 R >> >> >>",
            Ascii("BT /T 10 Tf 0 0 Td (Form text) Tj ET /Self Do /In Do"));
        w.SetStream(inner, $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /T {font} 0 R >> >> >>", Ascii("BT /T 10 Tf 0 -40 Td (Nested words) Tj ET"));
        int content = w.AddStream("<< >>", Ascii("q /Fm1 Do Q"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 {form} 0 R >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        var doc = Read(w.Write());
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("Form text\nNested words", doc.Body);
    }

    [Fact]
    public void Inline_images_with_binary_data_are_skipped()
    {
        // The image data holds "EI", "BT" and ")" bytes that must not be taken for content.
        byte[] data = [.. Ascii("EI BT (x) Tj"), 0xFF, 0x00, 0x45, 0x49, 0x0A, 0x80, 0x81];
        string head = "BT /F1 12 Tf 72 700 Td (Before) Tj ET\nBI /W 4 /H 4 /CS /G /BPC 8 ID ";
        var content = new List<byte>(Ascii(head));
        content.AddRange(data);
        content.AddRange(new byte[16 - data.Length % 16]);
        content.AddRange(Ascii("\nEI\nBT /F1 12 Tf 72 600 Td (After) Tj ET"));
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int stream = w.AddStream("<< >>", [.. content]);
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {stream} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        Assert.Equal("Before\nAfter", Read(w.Write()).Body);
    }

    [Fact]
    public void Inline_image_with_a_length_hint_is_skipped_exactly()
    {
        string content = "BI /W 8 /H 1 /BPC 8 /CS /G /L 8 ID 12EI4567 EI BT /F1 12 Tf 72 700 Td (Shown) Tj ET";
        Assert.Equal("Shown", Read(W.SimpleDocument(content).Write()).Body);
    }

    [Fact]
    public void Several_content_streams_are_joined()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int a = w.AddStream("<< >>", Ascii("BT /F1 12 Tf 72 700 Td (First part) Tj"), "Flate");
        int b = w.AddStream("<< >>", Ascii("ET BT /F1 12 Tf 72 680 Td (Second part) Tj ET"), "A85");
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents [{a} 0 R {b} 0 R] >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        // The streams are one content stream split between operators (inside BT … ET here).
        Assert.Equal("First part\nSecond part", Read(w.Write()).Body);
    }

    [Fact]
    public void Resources_are_inherited_from_the_page_tree()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve(), middle = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int content = w.AddStream("<< >>", Ascii(W.Lines("Inherited font")));
        int page = w.Add($"<< /Type /Page /Parent {middle} 0 R /Contents {content} 0 R >>");
        w.Set(middle, $"<< /Type /Pages /Parent {pages} 0 R /Kids [{page} 0 R] /Count 1 >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{middle} 0 R] /Count 1 /Resources << /Font << /F1 {font} 0 R >> >> /MediaBox [0 0 600 800] >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        Assert.Equal("Inherited font", Read(w.Write()).Body);
    }

    [Fact]
    public void Widget_annotations_add_their_appearance_or_their_value()
    {
        var w = W.SimpleDocument(W.Lines("Applicant name:"));
        var page = 4; // SimpleDocument: catalog 1, pages 2, font 3, page 4, content 5
        int helv = 3;
        int ap = w.AddStream($"<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv {helv} 0 R >> >> >>", Ascii("/Tx BMC BT /Helv 10 Tf 2 5 Td (Jane Appleseed) Tj ET EMC"));
        int filled = w.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane Appleseed) /Rect [200 700 400 720] /AP << /N {ap} 0 R >> >>");
        int noAppearance = w.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (city) /V " + W.Str("Thunder Bay") + " /Rect [200 670 400 690] >>");
        int hidden = w.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (secret) /V (Hidden value) /F 2 /Rect [0 0 10 10] >>");
        int check = w.Add("<< /Type /Annot /Subtype /Widget /FT /Btn /T (ok) /V /Yes /AS /Yes /Rect [0 0 10 10] >>");
        int link = w.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>");
        w.Set(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [{filled} 0 R {noAppearance} 0 R {hidden} 0 R {check} 0 R {link} 0 R] >>");
        var doc = Read(w.Write(crypt: W.CryptKind.Aes128));
        Assert.Equal("Applicant name:\nJane Appleseed\nThunder Bay", doc.Body);
    }

    [Fact]
    public void Need_appearances_uses_the_field_value_over_a_stale_appearance()
    {
        var w = W.SimpleDocument(W.Lines("Form"));
        int ap = w.AddStream("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 3 0 R >> >> >>", Ascii("BT /Helv 10 Tf 2 5 Td (Old value) Tj ET"));
        int field = w.Reserve();
        int parent = w.Add("<< /FT /Tx /T (group) /V (New value) >>");
        w.Set(field, $"<< /Type /Annot /Subtype /Widget /T (f) /Parent {parent} 0 R /Rect [200 700 400 720] /AP << /N {ap} 0 R >> >>");
        int form = w.Add($"<< /Fields [{parent} 0 R] /NeedAppearances true >>");
        w.Set(1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm {form} 0 R >>");
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [{field} 0 R] >>");
        Assert.Equal("Form\nNew value", Read(w.Write()).Body);
    }

    [Fact]
    public void One_broken_page_keeps_the_other_pages_words_as_truncated()
    {
        var w = W.SimpleDocument(W.Lines("Good first page"), "placeholder", W.Lines("Good third page"));
        // Page 2's content: a Flate stream that breaks after a few bytes and holds no text before the break.
        var broken = W.Zlib(Ascii(W.Lines("Lost words here and more")));
        for (int i = 4; i < broken.Length; i++) broken[i] ^= 0x5A;
        int bad = w.AddRawStream($"<< /Filter /FlateDecode /Length {broken.Length} >>", broken);
        w.Set(6, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {bad} 0 R >>");
        var doc = Read(w.Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        var pages = Pages(doc);
        Assert.Equal(3, pages.Length);
        Assert.Equal("Good first page", pages[0]);
        Assert.Equal("", pages[1]);
        Assert.Equal("Good third page", pages[2]);
    }

    [Fact]
    public void A_content_stream_cut_short_keeps_its_beginning_as_truncated()
    {
        // Page 2's Flate stream ends halfway (a cut file, a /Length too short): its first lines stay, and the
        // document says it is partial even though nothing threw.
        var w = W.SimpleDocument(W.Lines("Good first page"), "placeholder");
        var full = W.Zlib(Ascii(W.Lines(Enumerable.Range(0, 400).Select(i => $"Second page line {i} words").ToArray())));
        var cut = full[..(full.Length / 2)];
        int stream = w.AddRawStream($"<< /Filter /FlateDecode /Length {cut.Length} >>", cut);
        w.Set(6, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {stream} 0 R >>");
        var doc = Read(w.Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("Second page line 0 words\nSecond page line 1 words", Pages(doc)[1]);
        Assert.DoesNotContain("line 399", doc.Body);
    }

    [Fact]
    public void A_page_whose_objects_loop_is_left_out_and_the_document_is_truncated()
    {
        // Page 2's content and font are a reference that points at itself: nothing can be read there, pages 1 and 3
        // stay, and page 2 keeps its place.
        var w = W.SimpleDocument(W.Lines("Alpha page"), W.Lines("Beta page"), W.Lines("Gamma page"));
        int loop = w.Reserve();
        w.Set(loop, $"{loop} 0 R");
        w.Set(6, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {loop} 0 R >> /XObject << /X {loop} 0 R >> >> /Contents {loop} 0 R >>");
        var doc = Read(w.Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(["Alpha page", "", "Gamma page"], Pages(doc));
    }

    [Fact]
    public void A_page_missing_from_the_tree_is_left_out_and_the_document_is_truncated()
    {
        var w = W.SimpleDocument(W.Lines("One"), W.Lines("Two"), W.Lines("Three"));
        // The second kid is an object the file doesn't have.
        w.Set(2, "<< /Type /Pages /Kids [4 0 R 999 0 R 8 0 R] /Count 3 >>");
        var doc = Read(w.Write());
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(["One", "Three"], Pages(doc));
    }

    [Fact]
    public void A_whole_document_read_without_trouble_is_ok()
    {
        // Partial reads make a document Truncated; a sound one with streams of every kind stays Ok.
        var w = W.SimpleDocument(W.Lines("Plain words"));
        int form = w.AddStream("<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] >>", Ascii("BT /F1 10 Tf 72 500 Td (Form words) Tj ET"), "Flate");
        int content = w.AddStream("<< >>", Ascii("BT /F1 12 Tf 72 700 Td (Page words) Tj ET /Fm Do"), "A85", "Flate");
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> /XObject << /Fm {form} 0 R >> >> /Contents {content} 0 R >>");
        foreach (var crypt in new[] { W.CryptKind.None, W.CryptKind.Rc4_128, W.CryptKind.Aes128, W.CryptKind.Aes256 })
        {
            var doc = Read(w.Write(W.XrefKind.Stream, objectStreams: true, crypt));
            Assert.Equal(DocStatus.Ok, doc.Status);
            Assert.Equal("Page words\nForm words", doc.Body);
        }
    }

    [Fact]
    public void Text_limit_truncates_and_keeps_the_start()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"Line number {i} with several words").ToArray();
        var doc = Read(W.SimpleDocument(W.Lines(lines)).Write(), limits: new ExtractLimits(MaxTextChars: 500));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("Line number 0 with several words\nLine number 1", doc.Body);
        Assert.True(doc.Body.Length <= 500);
    }

    [Fact]
    public void Page_limit_stops_with_the_declared_page_count()
    {
        var contents = Enumerable.Range(0, 12).Select(i => W.Lines($"Page {i} text")).ToArray();
        var doc = Read(W.SimpleDocument(contents).Write(), limits: new ExtractLimits(MaxPages: 5));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(5, Pages(doc).Length);
        Assert.Equal(12, doc.Pages);
    }

    [Fact]
    public void Byte_limit_keeps_what_was_read_or_says_too_big()
    {
        var rng = new Random(3);
        var contents = Enumerable.Range(0, 200).Select(i => W.Lines(string.Join(' ', Enumerable.Range(0, 200).Select(k => $"w{i}x{k}r{rng.Next(1_000_000)}")))).ToArray();
        var file = W.SimpleDocument(contents).Write();
        var doc = Read(file, limits: new ExtractLimits(MaxBytesRead: file.Length / 2));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("w0x0r", doc.Body);
        Assert.Equal(200, doc.Pages); // pagecount: still finds it
        Assert.Equal(DocStatus.TooBig, Read(file, limits: new ExtractLimits(MaxBytesRead: 600)).Status);
    }

    [Fact]
    public void A_document_level_limit_keeps_the_declared_page_count()
    {
        var rng = new Random(5);
        var contents = Enumerable.Range(0, 20).Select(i => W.Lines(string.Join(' ', Enumerable.Range(0, 300).Select(k => $"p{i}w{k}r{rng.Next(1_000_000)}")))).ToArray();
        var file = W.SimpleDocument(contents).Write();
        Assert.Equal(20, Read(file).Pages);
        // The content streams decode past the limit at about page 5: the text of the pages before is kept, and so is the count.
        var decoded = Read(file, limits: new ExtractLimits(MaxDecodedBytes: 22_000));
        Assert.Equal(DocStatus.Truncated, decoded.Status);
        Assert.InRange(Pages(decoded).Length, 2, 8);
        Assert.Equal(20, decoded.Pages);
        // So for time: a clock that moves on each look runs out halfway through (as many looks as a whole read takes, halved).
        long looks = 0;
        ExtractedDoc Timed(int limit)
        {
            var budget = new ExtractBudget(new ExtractLimits(MaxMilliseconds: limit), CancellationToken.None, () => ++looks);
            using var source = new MemoryByteSource(file, budget);
            return ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget);
        }
        Assert.Equal(DocStatus.Ok, Timed(int.MaxValue).Status);
        var timed = Timed((int)(looks / 2));
        Assert.Equal(DocStatus.Truncated, timed.Status);
        Assert.Equal(20, timed.Pages);
    }

    [Fact]
    public void Time_limit_is_checked_while_reading()
    {
        long now = 0;
        var contents = Enumerable.Range(0, 50).Select(i => W.Lines($"Page {i}")).ToArray();
        var file = W.SimpleDocument(contents).Write();
        var budget = new ExtractBudget(new ExtractLimits(MaxMilliseconds: 100), CancellationToken.None, () => now += 7);
        using var source = new MemoryByteSource(file, budget);
        var doc = ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget);
        Assert.Contains(doc.Status, new[] { DocStatus.TimedOut, DocStatus.Truncated });
    }

    [Fact]
    public void Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var budget = new ExtractBudget(ExtractLimits.Local, cts.Token);
        using var source = new MemoryByteSource(W.SimpleDocument(W.Lines("x")).Write(), budget);
        Assert.ThrowsAny<OperationCanceledException>(() => ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget));
    }

    [Fact]
    public void Ai_files_with_a_pdf_header_are_read_as_pdf()
    {
        var w = W.SimpleDocument(W.Lines("Logo artwork text"));
        w.Preamble = "%!PS-Adobe-3.0 junk before the header\n";
        var doc = Read(w.Write(), "ai", kind: ContentKind.Design);
        Assert.Equal(ContentKind.Design, doc.Kind);
        Assert.Equal("Logo artwork text", doc.Body);
    }

    [Fact]
    public void Ai_files_without_a_pdf_header_give_their_xmp()
    {
        string ai = "%!PS-Adobe-3.0\n%%Creator: Adobe Illustrator\n" + new string('%', 2000) + "\n"
            + "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Old logo</rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>\n%%EOF";
        var doc = Read(Ascii(ai), "ai", kind: ContentKind.Design);
        Assert.Equal(DocStatus.NoText, doc.Status);
        Assert.Contains((DocField.Title, "Old logo"), doc.Fields);
    }

    [Fact]
    public void Files_that_are_not_pdf_are_damaged_and_cheap()
    {
        var html = Ascii("<!DOCTYPE html><html><head><title>Just a moment</title></head><body>" + new string('x', 200_000) + "</body></html>");
        var (doc, source) = ReadRecorded(html);
        Assert.Equal(DocStatus.Damaged, doc.Status);
        Assert.True(source.BytesRead < 64 << 10, $"read {source.BytesRead}");
    }

    [Fact]
    public void Type3_fonts_map_through_their_encoding_and_scale_widths()
    {
        var w = new W();
        int catalog = w.Reserve(), pages = w.Reserve();
        int procs = w.Add("<< >>");
        int font = w.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs {procs} 0 R /Encoding << /Type /Encoding /Differences [65 /H /i 32 /space] >> /FirstChar 32 /LastChar 66 /Widths [{string.Join(' ', Enumerable.Repeat("500", 35))}] >>");
        int content = w.AddStream("<< >>", Ascii("BT /T3 20 Tf 72 700 Td (AB AB) Tj ET"));
        int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 {font} 0 R >> >> /Contents {content} 0 R >>");
        w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        w.Root = catalog;
        Assert.Equal("Hi Hi", Read(w.Write()).Body);
    }
}
