using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Xunit.Abstractions;
using static Anything.Tests.Content.Readers.OfficeFixtures;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Crafted zips and XML that would make ZipArchive or XmlReader spend hundreds of MB before any walker's own check runs,
/// and file I/O errors that must never turn into Damaged documents.
/// </summary>
public class ZipAndXmlBoundsTests(ITestOutputHelper output)
{
    static ExtractBudget Budget() => new(ExtractLimits.Local, CancellationToken.None);

    /// <summary>Allocation on this thread while reading bytes as a file of an extension.</summary>
    static (ExtractedDoc Doc, long Allocated, long Ms) Measure(byte[] bytes, string extension)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var doc = ReaderRun.Bytes(bytes, extension);
        return (doc, GC.GetAllocatedBytesForCurrentThread() - before, sw.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(900)]
    public void Zip_whose_end_record_would_send_ZipGuard_and_ZipArchive_to_different_directories(int nameLength)
    {
        // 65,000 directory entries; the end record says so, with only the directory size masked (ZipArchive then ignores
        // the Zip64 records), while a valid Zip64 locator and record claim 5 entries.
        const int entries = 65_000;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var name = Encoding.ASCII.GetBytes(new string('a', nameLength));
        for (int i = 0; i < entries; i++)
        {
            w.Write(0x02014B50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u);
            w.Write(name);
        }
        long zip64At = ms.Position;
        w.Write(0x06064B50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write(5UL); w.Write(5UL); w.Write(5UL * (ulong)(46 + nameLength)); w.Write(0UL);
        w.Write(0x07064B50u); w.Write(0u); w.Write((ulong)zip64At); w.Write(1u);
        w.Write(0x06054B50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries); w.Write((ushort)entries);
        w.Write(0xFFFFFFFFu); w.Write(0u); w.Write((ushort)0);
        var bytes = ms.ToArray();

        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(bytes, Budget()), out long counted));
        Assert.Equal(entries, counted); // the count ZipArchive would have built
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var source = new MemoryByteSource(bytes, Budget());
        Assert.Equal(DocStatus.TooBig, OfficeZip.TryOpen(source, out var zip));
        Assert.Null(zip);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"file {bytes.Length >> 20} MB, names of {nameLength}: allocated {allocated >> 10} KB");
        Assert.True(allocated < 1 << 20, $"allocated {allocated >> 10} KB");
    }

    [Fact]
    public void Zip_directory_that_declares_few_entries_but_holds_many()
    {
        var crafted = CraftedDirectory(60_001);
        int end = crafted.Length - 22;
        BitConverter.GetBytes((ushort)5).CopyTo(crafted, end + 8);
        BitConverter.GetBytes((ushort)5).CopyTo(crafted, end + 10);
        BitConverter.GetBytes(235u).CopyTo(crafted, end + 12);
        Assert.Equal(DocStatus.Damaged, ZipGuard.Check(new MemoryByteSource(crafted, Budget())));
        var (doc, allocated, _) = Measure(crafted, "docx");
        Assert.Equal(DocStatus.Damaged, doc.Status);
        Assert.True(allocated < 1 << 20, $"allocated {allocated >> 10} KB");
    }

    [Theory]
    [InlineData(0xFFFF)]
    [InlineData(0)]
    [InlineData(1)]
    public void Zip_with_a_second_end_record_in_its_comment(int fakeComment)
    {
        var crafted = CraftedDirectory(60_001);
        int end = crafted.Length - 22;
        long size = BitConverter.ToUInt32(crafted, end + 12);
        // The real end record declares 5 entries; its comment holds a second end record that tells the truth.
        BitConverter.GetBytes((ushort)5).CopyTo(crafted, end + 8);
        BitConverter.GetBytes((ushort)5).CopyTo(crafted, end + 10);
        BitConverter.GetBytes(235u).CopyTo(crafted, end + 12);
        var fake = new MemoryStream();
        var w = new BinaryWriter(fake);
        w.Write(0x06054B50u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)60_001); w.Write((ushort)60_001); w.Write((uint)size); w.Write(0u); w.Write((ushort)fakeComment);
        var comment = fake.ToArray();
        BitConverter.GetBytes((ushort)comment.Length).CopyTo(crafted, end + 20);
        var bytes = crafted.Concat(comment).ToArray();
        var (doc, allocated, _) = Measure(bytes, "docx");
        Assert.True(doc.Status is DocStatus.Damaged or DocStatus.TooBig, doc.Status.ToString());
        Assert.True(allocated < 1 << 20, $"allocated {allocated >> 10} KB");
    }

    [Fact]
    public void Zip_directory_whose_names_add_up_past_4_MB_is_too_big()
    {
        // 9,000 entries (under the entry limit) with 600-byte names: 5.8 MB of directory, while the end record claims 1 MB.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var name = Encoding.ASCII.GetBytes(new string('n', 600));
        for (int i = 0; i < 9_000; i++)
        {
            w.Write(0x02014B50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u);
            w.Write(name);
        }
        long directory = ms.Position;
        w.Write(0x06054B50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)9_000); w.Write((ushort)9_000);
        w.Write(1u << 20); w.Write((uint)(directory - (1 << 20))); w.Write((ushort)0);
        // The claimed directory starts mid-way, so the walk starts on a name, not a header.
        Assert.Equal(DocStatus.Damaged, ZipGuard.Check(new MemoryByteSource(ms.ToArray(), Budget())));
        // With the offset right and the size masked (ZipArchive never reads the size), the walk counts the real bytes.
        var bytes = ms.ToArray();
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(bytes, bytes.Length - 10);
        BitConverter.GetBytes(0u).CopyTo(bytes, bytes.Length - 6);
        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(bytes, Budget())));
    }

    [Fact]
    public void Real_packages_pass_the_directory_walk()
    {
        foreach (var bytes in new[] { Docx(), Xlsx(stored: true), Pptx(), Odt(), Idml() })
        {
            Assert.Null(ZipGuard.Check(new MemoryByteSource(bytes, Budget()), out long entries));
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Assert.Equal(zip.Entries.Count, entries);
        }
    }

    static string Deep(int levels) => new StringBuilder(levels * 3).Insert(0, "<x>", levels).ToString();

    [Theory]
    [InlineData("skipped pPr")]
    [InlineData("inside t")]
    [InlineData("rels")]
    [InlineData("core properties")]
    public void Deep_xml_nesting_in_a_docx_stops_the_part_not_the_process(string where)
    {
        string deep = Deep(5_000_000); // 15 MB inflated, under the 16 MB entry cap
        byte[] docx = where switch
        {
            "skipped pPr" => Docx(body: $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:pPr>{deep}"),
            "inside t" => Docx(body: $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>{deep}"),
            "rels" => Zip(false, ("_rels/.rels", $"<Relationships xmlns=\"{Rels}\">" + deep), ("word/document.xml", DocxBody())),
            _ => Zip(false, ("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"x\"><dc:title xmlns:dc=\"y\">" + deep), ("word/document.xml", DocxBody())),
        };
        var (doc, allocated, ms) = Measure(docx, "docx");
        output.WriteLine($"{where}: {doc.Status}, allocated {allocated >> 20} MB in {ms} ms");
        Assert.True(allocated < 24 << 20, $"allocated {allocated >> 20} MB");
        Assert.Equal(DocStatus.Truncated, doc.Status); // the other parts are still read
        if (where != "skipped pPr" && where != "inside t") Assert.Contains("Invoice", doc.Body);
    }

    [Theory]
    [InlineData("odt meta")]
    [InlineData("idml properties")]
    [InlineData("idml metadata")]
    public void Deep_xml_nesting_in_odf_and_idml_stops_the_part(string where)
    {
        string deep = Deep(5_000_000);
        const string content = "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:text><text:p>words</text:p></office:text></office:body></office:document-content>";
        byte[] file = where switch
        {
            "odt meta" => Zip(false, ("mimetype", "application/vnd.oasis.opendocument.text"),
                ("meta.xml", "<office:document-meta xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\">" + deep), ("content.xml", content)),
            "idml properties" => Zip(false, ("designmap.xml", "<Document><idPkg:Story xmlns:idPkg=\"x\" src=\"Stories/a.xml\"/></Document>"),
                ("Stories/a.xml", "<Story><Properties>" + deep)),
            _ => Zip(false, ("META-INF/metadata.xml", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + deep),
                ("Stories/a.xml", "<Story><Content>story words</Content></Story>")),
        };
        string extension = where.StartsWith("odt", StringComparison.Ordinal) ? "odt" : "idml";
        var (doc, allocated, _) = Measure(file, extension);
        output.WriteLine($"{where}: {doc.Status}, allocated {allocated >> 20} MB");
        Assert.True(allocated < 24 << 20, $"allocated {allocated >> 20} MB");
        if (where != "idml properties") Assert.Equal(DocStatus.Truncated, doc.Status);
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(4_000_000)]
    [InlineData(15_000_000)]
    public void A_giant_tag_stops_its_part(int chars)
    {
        var sb = new StringBuilder($"<w:document xmlns:w=\"{W}\"><w:body><w:p ");
        for (int i = 0; sb.Length < chars; i++) sb.Append('a').Append(i).Append("=\"\" ");
        sb.Append("/></w:body></w:document>");
        var (doc, allocated, ms) = Measure(Docx(body: sb.ToString()), "docx");
        output.WriteLine($"{chars:N0} chars: {doc.Status} in {ms} ms, allocated {allocated >> 20} MB");
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Contains("Confidential header", doc.Body);
        Assert.True(allocated < 24 << 20, $"allocated {allocated >> 20} MB");
        Assert.True(ms < 2000, $"{ms} ms");
    }

    [Theory]
    [InlineData("elements")]
    [InlineData("attributes")]
    public void A_part_of_a_million_distinct_names_stops_its_part(string kind)
    {
        // XmlReader keeps every distinct name for the part's life: unchecked, this part held about 130 MB in one lane.
        var sb = new StringBuilder($"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>first words</w:t></w:r></w:p>");
        if (kind == "elements") for (int i = 0; i < 1_000_000; i++) sb.Append("<x").Append(i).Append("/>");
        else
            for (int i = 0; i < 1_000_000;)
            {
                sb.Append("<w:p");
                for (int k = 0; k < 4000; k++, i++) sb.Append(" a").Append(i).Append("=\"\"");
                sb.Append("/>");
            }
        sb.Append("</w:body></w:document>");
        var (doc, allocated, ms) = Measure(Docx(body: sb.ToString()), "docx");
        output.WriteLine($"{kind}: {doc.Status} in {ms} ms, allocated {allocated >> 20} MB");
        Assert.Equal(DocStatus.Truncated, doc.Status); // the other parts are still read
        Assert.Contains("Confidential header", doc.Body);
        Assert.True(allocated < 24 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void The_name_table_refuses_new_names_past_its_caps()
    {
        var table = new CappedNameTable();
        for (int i = 0; i < CappedNameTable.MaxNames; i++) table.Add("n" + i);
        Assert.Equal("n5", table.Add("n5")); // a name it already holds costs nothing
        Assert.Throws<InvalidDataException>(() => table.Add("one more"));
        // Long names reach the character cap long before the count.
        var chars = new CappedNameTable();
        int added = 0;
        Assert.Throws<InvalidDataException>(() => { for (; added < CappedNameTable.MaxNames; added++) chars.Add(new string('x', 1000) + added); });
        Assert.True(added < 1100, $"{added} names");
    }

    [Fact]
    public void Xml_shape_follows_comments_cdata_instructions_and_quotes()
    {
        static bool Allows(string xml, Encoding? encoding = null) => XmlShape.Allows((encoding ?? Encoding.UTF8).GetBytes(xml));
        string Nested(int n) => string.Concat(Enumerable.Repeat("<a>", n)) + string.Concat(Enumerable.Repeat("</a>", n));
        Assert.True(Allows(Nested(OfficeXml.MaxDepth + 1)));
        Assert.False(Allows(Nested(OfficeXml.MaxDepth + 2)));
        // Siblings, empty elements and end tags keep the depth down.
        Assert.True(Allows("<r>" + string.Concat(Enumerable.Repeat("<a/><b x='1'></b>", 10_000)) + "</r>"));
        // Markup characters inside comments, CDATA, instructions and quoted values open nothing.
        string fake = string.Concat(Enumerable.Repeat("<a>", 1000));
        Assert.True(Allows($"<?xml version=\"1.0\"?><r><!-- {fake} --><![CDATA[{fake}]]><?pi {fake} ?><e v=\"{fake.Replace("<", "&lt;")} > / '\" w='\">'/></r>"));
        Assert.True(Allows("<r a=\"/\"><b/></r>"));
        // UTF-16 in either byte order, with and without a byte order mark.
        foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
        {
            Assert.True(Allows(Nested(200), encoding));
            Assert.False(Allows(Nested(300), encoding));
            Assert.False(XmlShape.Allows([.. encoding.GetPreamble(), .. encoding.GetBytes(Nested(300))]));
        }
        // A tag just under the limit passes; one over it doesn't.
        Assert.True(Allows("<a b=\"" + new string('x', XmlShape.MaxTagChars - 10) + "\"/>"));
        Assert.False(Allows("<a b=\"" + new string('x', XmlShape.MaxTagChars) + "\"/>"));
        // So for the attributes of one tag (quotes inside values don't count).
        string Attributes(int n) => "<a " + string.Concat(Enumerable.Range(0, n).Select(i => $"a{i}='\"' ")) + "/>";
        Assert.True(Allows(Attributes(XmlShape.MaxAttributes)));
        Assert.False(Allows(Attributes(XmlShape.MaxAttributes + 1)));
        Assert.True(Allows("<r>" + string.Concat(Enumerable.Repeat(Attributes(100), 100)) + "</r>")); // per tag, not per document
        // Bytes fed a few at a time give the same answer (a UTF-16 unit split between reads).
        var shape = new XmlShape();
        var utf16 = Encoding.Unicode.GetBytes(Nested(300));
        Assert.Throws<InvalidDataException>(() => { for (int i = 0; i < utf16.Length; i += 3) shape.Feed(utf16.AsSpan(i, Math.Min(3, utf16.Length - i))); });
    }

    [Fact]
    public void Word_vml_shapes_with_their_drawingml_copy_in_one_attribute_read()
    {
        // Word keeps a base64 copy of each VML shape in o:gfxdata: a real worksheet had one of 466 KB.
        string gfx = Convert.ToBase64String(new byte[700_000]);
        var body = $"<w:document xmlns:w=\"{W}\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"><w:body>" +
            $"<w:p><w:r><w:pict><v:group id=\"Group 15\" o:gfxdata=\"{gfx}\" style=\"width:100pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>shape words</w:t></w:r></w:p>" +
            "</w:txbxContent></v:textbox></v:group></w:pict></w:r></w:p><w:p><w:r><w:t>body after the shape</w:t></w:r></w:p></w:body></w:document>";
        var (doc, allocated, _) = Measure(Docx(body: body), "docx");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Contains("shape words", doc.Body);
        Assert.Contains("body after the shape", doc.Body);
        Assert.True(allocated < 32 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void Real_office_xml_passes_the_shape_check()
    {
        foreach (var (bytes, ext) in new[] { (Docx(), "docx"), (Xlsx(), "xlsx"), (Pptx(), "pptx"), (Odt(), "odt"), (Ods(), "ods"), (Odp(), "odp"), (Idml(), "idml") })
            Assert.Equal(DocStatus.Ok, ReaderRun.Bytes(bytes, ext).Status);
    }

    [Fact]
    public void Deep_xmp_in_pictures_is_skipped_and_the_rest_kept()
    {
        // A TIFF whose Photoshop resources hold a 900 KB XMP packet nested 300,000 deep (inside the TIFF's 1 MB).
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((256, 4, 1, t.U32(640)));
        ifd.Entries.Add((257, 4, 1, t.U32(480)));
        ifd.Entries.Add((271, 2, 6, TiffWriter.Ascii("Canon")));
        var irb = PictureFixtures.Irb(0x0424, Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + Deep(300_000)));
        ifd.Entries.Add((34377, 7, irb.Length, irb));
        var (tiff, tiffAllocated, _) = Measure(t.Build(ifd), "tif");
        Assert.Equal(640, tiff.Picture!.Value.Width);
        Assert.Equal("Canon", tiff.Field(DocField.CameraMake));
        Assert.True(tiffAllocated < 8 << 20, $"allocated {tiffAllocated >> 20} MB");

        // A WebP whose XMP chunk is 3.9 MB nested 1,300,000 deep.
        static byte[] Chunk(string fourcc, byte[] data)
        {
            var c = new byte[8 + data.Length + (data.Length & 1)];
            Encoding.ASCII.GetBytes(fourcc).CopyTo(c, 0);
            BitConverter.GetBytes((uint)data.Length).CopyTo(c, 4);
            data.CopyTo(c, 8);
            return c;
        }
        var vp8x = new byte[10];
        vp8x[0] = 0x04;
        vp8x[4] = 99; vp8x[7] = 49; // 100 × 50
        byte[] body = [.. "WEBP"u8, .. Chunk("VP8X", vp8x), .. Chunk("XMP ", Encoding.UTF8.GetBytes("<x:xmpmeta>" + Deep(1_300_000)))];
        byte[] webp = [.. "RIFF"u8, .. BitConverter.GetBytes((uint)body.Length), .. body];
        var (doc, allocated, _) = Measure(webp, "webp");
        Assert.Equal((100, 50), (doc.Picture!.Value.Width, doc.Picture!.Value.Height));
        Assert.True(allocated < 16 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void Io_errors_during_a_read_always_reach_the_file_source()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fixtures = new (string Name, string Extension, byte[] Bytes)[]
        {
            ("docx", "docx", Docx()), ("docx-stored", "docx", Docx(stored: true)), ("xlsx", "xlsx", Xlsx()), ("pptx", "pptx", Pptx()),
            ("odt", "odt", Odt()), ("ods", "ods", Ods()), ("idml", "idml", Idml()),
            ("msg", "msg", MailReadersTests.Msg()), ("eml", "eml", MailReadersTests.GeneratedMessage()),
            ("jpeg", "jpg", PictureFixtures.Jpeg(components: 4, adobeTransform: 2)), ("png", "png", PictureFixtures.Png()),
            ("tiff", "tif", PictureFixtures.Tiff(false)), ("heic", "heic", PictureFixtures.Heic()), ("webp", "webp", PictureFixtures.WebP()),
            ("psd", "psd", DesignFixtures.Psd()), ("eps", "eps", DesignFixtures.Eps(dos: true)),
        };
        var swallowed = new List<string>();
        foreach (var (name, extension, bytes) in fixtures)
        {
            var kind = ContentTypes.KindOf("x." + extension);
            var b = Budget();
            var counting = new FailingSource(bytes, b, int.MaxValue);
            ContentReaders.Extract(counting, extension, kind, b);
            for (int k = 1; k <= counting.Reads; k++)
            {
                var budget = Budget();
                var source = new FailingSource(bytes, budget, k);
                try
                {
                    var doc = ContentReaders.Extract(source, extension, kind, budget);
                    if (source.Threw) swallowed.Add($"{name}: read {k} of {counting.Reads} failed with an I/O error, yet Extract returned {doc.Status}");
                }
                catch (ContentIoException) { }
            }
        }
        foreach (var s in swallowed) output.WriteLine(s);
        Assert.Empty(swallowed);
    }
}
