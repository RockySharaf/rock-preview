using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class XmpTests
{
    const string Head = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:dc="http://purl.org/dc/elements/1.1/"
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
            xmlns:tiff="http://ns.adobe.com/tiff/1.0/"
            xmlns:exif="http://ns.adobe.com/exif/1.0/"
            xmlns:xmpTPg="http://ns.adobe.com/xap/1.0/t/pg/"
            xmlns:stFnt="http://ns.adobe.com/xap/1.0/sType/Font#"
            xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"
        """;

    const string Full = Head + """
            xmp:Rating="4" xmp:CreatorTool="Adobe Photoshop 25.0" tiff:Make="Canon" tiff:Model="EOS R5"
            photoshop:ColorMode="4" photoshop:City="Lyon" photoshop:Country="France">
           <dc:title><rdf:Alt><rdf:li xml:lang="fr">Coucher de soleil</rdf:li><rdf:li xml:lang="x-default">Sunset over the bay</rdf:li></rdf:Alt></dc:title>
           <dc:description><rdf:Alt><rdf:li xml:lang="x-default">Taken from the pier</rdf:li></rdf:Alt></dc:description>
           <dc:subject><rdf:Bag><rdf:li>sunset</rdf:li><rdf:li>beach</rdf:li><rdf:li>Sunset</rdf:li></rdf:Bag></dc:subject>
           <dc:creator><rdf:Seq><rdf:li>Ann Smith</rdf:li><rdf:li>Bo Lee</rdf:li></rdf:Seq></dc:creator>
           <photoshop:ICCProfile>sRGB IEC61966-2.1</photoshop:ICCProfile>
           <tiff:Orientation>6</tiff:Orientation>
           <tiff:XResolution>300/1</tiff:XResolution>
           <tiff:YResolution>300/1</tiff:YResolution>
           <tiff:ResolutionUnit>2</tiff:ResolutionUnit>
           <exif:DateTimeOriginal>2023-07-14T15:30:12+02:00</exif:DateTimeOriginal>
           <exif:GPSLatitude>45,45.6N</exif:GPSLatitude>
           <exif:GPSLongitude>4,50.4E</exif:GPSLongitude>
           <exif:ISOSpeedRatings><rdf:Seq><rdf:li>200</rdf:li></rdf:Seq></exif:ISOSpeedRatings>
           <exif:FNumber>28/10</exif:FNumber>
           <exif:ExposureTime>1/250</exif:ExposureTime>
           <exif:FocalLength>50/1</exif:FocalLength>
           <exif:ColorSpace>1</exif:ColorSpace>
           <xmpTPg:NPages>12</xmpTPg:NPages>
           <xmpTPg:Fonts><rdf:Bag>
             <rdf:li rdf:parseType="Resource"><stFnt:fontName>MyriadPro-Regular</stFnt:fontName><stFnt:fontFamily>Myriad Pro</stFnt:fontFamily></rdf:li>
             <rdf:li><rdf:Description stFnt:fontName="Helvetica-Bold" stFnt:fontFamily="Helvetica"/></rdf:li>
             <rdf:li rdf:parseType="Resource"><stFnt:fontFamily>Garamond</stFnt:fontFamily></rdf:li>
           </rdf:Bag></xmpTPg:Fonts>
           <xmpTPg:PlateNames><rdf:Seq><rdf:li>Cyan</rdf:li><rdf:li>Magenta</rdf:li><rdf:li>PANTONE 286 C</rdf:li></rdf:Seq></xmpTPg:PlateNames>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    static ExtractedDoc Read(string packet, Encoding? encoding = null)
    {
        var sink = new ExtractSink(ExtractLimits.Local);
        Xmp.Read((encoding ?? Encoding.UTF8).GetBytes(packet), sink);
        return sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0);
    }

    static string? Field(ExtractedDoc doc, DocField field) => doc.Fields.FirstOrDefault(f => f.Field == field).Value;

    [Fact]
    public void Reads_fields_and_picture_details()
    {
        var doc = Read(Full);
        Assert.Equal("Sunset over the bay", Field(doc, DocField.Title));
        Assert.Equal("Taken from the pier", Field(doc, DocField.Comment));
        Assert.Equal("sunset; beach", Field(doc, DocField.Keywords));
        Assert.Equal("Ann Smith; Bo Lee", Field(doc, DocField.Author));
        Assert.Equal("Adobe Photoshop 25.0", Field(doc, DocField.CreatorTool));
        Assert.Equal("Canon", Field(doc, DocField.CameraMake));
        Assert.Equal("EOS R5", Field(doc, DocField.CameraModel));
        Assert.Equal("sRGB IEC61966-2.1", Field(doc, DocField.ColorProfile));
        Assert.Equal("Lyon, France", Field(doc, DocField.Location));
        Assert.Equal("MyriadPro-Regular; Helvetica-Bold; Garamond", Field(doc, DocField.Fonts));
        Assert.Equal("Cyan; Magenta; PANTONE 286 C", Field(doc, DocField.Plates));
        Assert.Equal(12, doc.Pages);

        var p = Assert.NotNull(doc.Picture);
        Assert.Equal(4, p.Rating);
        Assert.Equal(ColorMode.Cmyk, p.ColorMode);
        Assert.Equal(6, p.Orientation);
        Assert.Equal((300, 300), (p.DpiX, p.DpiY));
        Assert.Equal(new DateTimeOffset(2023, 7, 14, 13, 30, 12, TimeSpan.Zero).UtcDateTime.ToFileTimeUtc(), p.DateTakenUtc);
        Assert.True(p.HasGps);
        Assert.Equal(45_760_000, p.LatitudeMicro);
        Assert.Equal(4_840_000, p.LongitudeMicro);
        Assert.Equal(200, p.Iso);
        Assert.Equal(2.8f, p.FNumber, 3);
        Assert.Equal(0.004f, p.ExposureSeconds, 5);
        Assert.Equal(50f, p.FocalLengthMm);
        Assert.Equal(1, p.ExifColorSpace);
        Assert.Equal("", doc.Body);
    }

    [Fact]
    public void A_dtd_is_refused()
    {
        string evil = """
            <!DOCTYPE x [<!ENTITY big "aaaaaaaaaa"><!ENTITY e SYSTEM "file:///c:/windows/win.ini">]>
            """ + Full.Replace("Sunset over the bay", "&big;&e;");
        var doc = Read(evil);
        Assert.Empty(doc.Fields);
        Assert.Null(doc.Picture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<x:xmpmeta><rdf:RDF><unclosed")]
    [InlineData("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'></x:xmpmeta>")]
    public void Bad_xml_adds_nothing(string packet)
    {
        var doc = Read(packet);
        Assert.Empty(doc.Fields);
        Assert.Null(doc.Picture);
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var rnd = new Random(7);
        var bytes = Encoding.UTF8.GetBytes(Full);
        for (int i = 0; i < 300; i++)
        {
            var mutated = (byte[])bytes.Clone();
            for (int k = 0; k < 8; k++) mutated[rnd.Next(mutated.Length)] = (byte)rnd.Next(256);
            Xmp.Read(mutated[..rnd.Next(mutated.Length)], new ExtractSink(ExtractLimits.Local));
        }
    }

    [Fact]
    public void Fields_completed_before_an_error_are_kept()
    {
        int cut = Full.IndexOf("<dc:subject>", StringComparison.Ordinal);
        var doc = Read(Full[..cut] + "<dc:subject><rdf:Bag><rdf:li>sun");
        Assert.Equal("Sunset over the bay", Field(doc, DocField.Title));
        Assert.Equal("Taken from the pier", Field(doc, DocField.Comment));
        Assert.Null(Field(doc, DocField.Keywords));
        // Attributes of the description were read before the break, and details made of several properties survive it.
        Assert.Equal("Canon", Field(doc, DocField.CameraMake));
        Assert.Equal("Lyon, France", Field(doc, DocField.Location));
    }

    [Fact]
    public void Old_xapmeta_packets_are_found_and_read()
    {
        string old = Full.Replace("x:xmpmeta", "x:xapmeta");
        var window = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 junk ").Concat(Encoding.UTF8.GetBytes(old)).Concat(Encoding.ASCII.GetBytes(" trailing")).ToArray();
        var (start, length) = Xmp.FindPacket(window);
        Assert.Equal(20, start);
        Assert.Equal(Encoding.UTF8.GetByteCount(old), length);
        var sink = new ExtractSink(ExtractLimits.Local);
        Xmp.Read(window.AsSpan(start, length), sink);
        Assert.Equal("Sunset over the bay", Field(sink.ToDoc(ContentKind.Design, DocStatus.Ok, 1, 0, 0), DocField.Title));
        // The same packet read in place from the window (no copy).
        var inPlace = new ExtractSink(ExtractLimits.Local);
        Xmp.Read(new ReadOnlyMemory<byte>(window, start, length), inPlace);
        Assert.Equal("Canon", Field(inPlace.ToDoc(ContentKind.Design, DocStatus.Ok, 1, 0, 0), DocField.CameraMake));
    }

    [Fact]
    public void History_ancestors_and_placed_documents_are_skipped()
    {
        var ancestors = new StringBuilder("<photoshop:DocumentAncestors><rdf:Bag>");
        for (int i = 0; i < 10_000; i++) ancestors.Append("<rdf:li>xmp.did:").Append(i.ToString("x8")).Append("</rdf:li>");
        ancestors.Append("</rdf:Bag></photoshop:DocumentAncestors>");
        string pantry = """
            <xmpMM:Pantry><rdf:Bag><rdf:li rdf:parseType="Resource">
              <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Stock photo 123</rdf:li></rdf:Alt></dc:title>
              <dc:subject><rdf:Bag><rdf:li>stock</rdf:li></rdf:Bag></dc:subject>
            </rdf:li></rdf:Bag></xmpMM:Pantry>
            <xmpMM:History><rdf:Seq><rdf:li rdf:parseType="Resource"><dc:title>saved</dc:title></rdf:li></rdf:Seq></xmpMM:History>
            """;
        string packet = Head + ">" + ancestors + pantry + """
             <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Brochure 2024</rdf:li></rdf:Alt></dc:title>
             <dc:subject><rdf:Bag><rdf:li>print</rdf:li></rdf:Bag></dc:subject>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        var doc = Read(packet);
        Assert.Equal("Brochure 2024", Field(doc, DocField.Title));
        Assert.Equal("print", Field(doc, DocField.Keywords));
    }

    [Fact]
    public void Utf16_packets_are_found_in_either_byte_order()
    {
        // The same bytes shifted by one read as either byte order: the packet header's byte order mark decides.
        string header = "<?xpacket begin=\"" + (char)0xFEFF + "\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n";
        foreach (var enc in new[] { Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var window = new byte[] { 1, 2, 3 }.Concat(enc.GetBytes(header + Full)).Concat(new byte[] { 9, 9 }).ToArray();
            var (start, length) = Xmp.FindPacket(window);
            Assert.Equal(3 + enc.GetByteCount(header), start);
            Assert.Equal(enc.GetByteCount(Full), length);
            var sink = new ExtractSink(ExtractLimits.Local);
            Xmp.Read(window.AsSpan(start, length), sink);
            Assert.Equal("Sunset over the bay", Field(sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0), DocField.Title));
        }
        // Without a header, little-endian (what Windows writes).
        var bare = new byte[] { 1, 2, 3 }.Concat(Encoding.Unicode.GetBytes(Full)).ToArray();
        Assert.Equal(3, Xmp.FindPacket(bare).Start);
    }

    [Fact]
    public void Bare_rdf_packets_and_cut_off_packets()
    {
        const string bare = """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Plan</dc:title></rdf:Description></rdf:RDF>""";
        var window = Encoding.UTF8.GetBytes("xx" + bare + "yy");
        Assert.Equal((2, bare.Length), Xmp.FindPacket(window));

        var cut = Encoding.UTF8.GetBytes("ab<x:xmpmeta xmlns:x='adobe:ns:meta/'>never closed");
        Assert.Equal((2, cut.Length - 2), Xmp.FindPacket(cut));
        Assert.Equal((-1, 0), Xmp.FindPacket(Encoding.UTF8.GetBytes("no packet here")));
        Assert.Equal((-1, 0), Xmp.FindPacket([]));
    }

    [Fact]
    public void Locations_need_a_reference_and_zero_zero_is_no_fix()
    {
        string south = Full.Replace("45,45.6N", "33,52,4.8S").Replace("4,50.4E", "151,12,36W");
        var p = Read(south).Picture!.Value;
        Assert.Equal(-33_868_000, p.LatitudeMicro);
        Assert.Equal(-151_210_000, p.LongitudeMicro);

        Assert.False(Read(Full.Replace("45,45.6N", "0,0N").Replace("4,50.4E", "0,0E")).Picture!.Value.HasGps);
        Assert.False(Read(Full.Replace("45,45.6N", "45,45.6")).Picture!.Value.HasGps);
    }

    [Theory]
    [InlineData("2023-07-14T15:30:12Z", 2023, 7, 14, 15, 30, 12)]
    [InlineData("2023-07-14T15:30:12.25-05:00", 2023, 7, 14, 20, 30, 12)]
    public void Dates_with_an_offset_are_exact(string value, int y, int mo, int d, int h, int mi, int s)
    {
        long expected = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.Equal(expected, Xmp.Date(value) / 10_000_000 * 10_000_000);
    }

    [Fact]
    public void Dates_without_an_offset_use_this_pcs_time_zone()
    {
        var local = new DateTime(2023, 7, 14, 15, 30, 12, DateTimeKind.Unspecified);
        long expected = (local - TimeZoneInfo.Local.GetUtcOffset(local)).Ticks;
        Assert.Equal(new DateTime(expected, DateTimeKind.Utc).ToFileTimeUtc(), Xmp.Date("2023-07-14T15:30:12"));
        Assert.Equal(Xmp.Date("2023-07-14T15:30:12"), Xmp.Date("2023:07:14 15:30:12"));
        Assert.Equal(0, Xmp.Date("0000:00:00 00:00:00"));
        Assert.Equal(0, Xmp.Date("garbage"));
    }

    [Theory]
    [InlineData("300", 300.0), InlineData("28/10", 2.8), InlineData(" 72.5 ", 72.5)]
    public void Numbers_and_rationals(string value, double expected) => Assert.Equal(expected, Xmp.Number(value)!.Value, 6);

    [Theory]
    [InlineData("1/0"), InlineData("x"), InlineData(""), InlineData("NaN/1")]
    public void Bad_numbers_are_unknown(string value) => Assert.Null(Xmp.Number(value));

    [Fact]
    public void Format_tells_whose_packet_it_is()
    {
        Assert.Equal("application/pdf", Xmp.Format(Encoding.UTF8.GetBytes(Full.Replace("<tiff:Orientation>", "<dc:format>application/pdf</dc:format><tiff:Orientation>"))));
        Assert.Equal("image/jpeg", Xmp.Format(Encoding.UTF8.GetBytes(Full.Replace("xmp:Rating=\"4\"", "dc:format=\"image/jpeg\" xmp:Rating=\"4\""))));
        Assert.Equal("application/postscript", Xmp.Format(Encoding.Unicode.GetBytes(Full.Replace("<tiff:Orientation>", "<dc:format> application/postscript </dc:format><tiff:Orientation>"))));
        Assert.Null(Xmp.Format(Encoding.UTF8.GetBytes(Full)));
        // A placed image's format inside the document's own packet is not the document's.
        string placed = Full.Replace("<tiff:Orientation>",
            "<xmpMM:Ingredients><rdf:Bag><rdf:li rdf:parseType=\"Resource\"><dc:format>image/tiff</dc:format></rdf:li></rdf:Bag></xmpMM:Ingredients><tiff:Orientation>");
        Assert.Null(Xmp.Format(Encoding.UTF8.GetBytes(placed)));
        Assert.Null(Xmp.Format("not xml"u8));
        Assert.Null(Xmp.Format([]));
        // Format adds nothing anywhere: reading the packet afterwards still gives every field.
        Assert.Equal("Sunset over the bay", Field(Read(Full), DocField.Title));
    }

    [Fact]
    public void A_huge_value_is_read_a_chunk_at_a_time()
    {
        // An 8 MB title (INDD and EPS windows and PSD resources can hold such packets): only the capped value is kept.
        string packet = Full.Replace("Sunset over the bay", new string('t', 8 << 20));
        var bytes = Encoding.UTF8.GetBytes(packet);
        var sink = new ExtractSink(ExtractLimits.Local);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Xmp.Read(new ReadOnlyMemory<byte>(bytes), sink);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var doc = sink.ToDoc(ContentKind.Design, DocStatus.Ok, 1, 0, 0);
        Assert.Equal(ExtractSink.MaxFieldChars, Field(doc, DocField.Title)!.Length);
        Assert.Equal("Canon", Field(doc, DocField.CameraMake));
        Assert.True(allocated < 4L << 20, $"allocated {allocated:N0} bytes for an 8 MB value");
    }

    [Fact]
    public void A_huge_keyword_bag_keeps_only_what_fits()
    {
        var bag = new StringBuilder();
        for (int i = 0; i < 400_000; i++) bag.Append("<rdf:li>kw").Append(i).Append("</rdf:li>");
        string packet = Full.Replace("<rdf:li>sunset</rdf:li><rdf:li>beach</rdf:li><rdf:li>Sunset</rdf:li>", bag.ToString());
        var bytes = Encoding.UTF8.GetBytes(packet);
        var sink = new ExtractSink(ExtractLimits.Local);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Xmp.Read(new ReadOnlyMemory<byte>(bytes), sink);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        string keywords = Field(sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0), DocField.Keywords)!;
        Assert.StartsWith("kw0; kw1; kw2", keywords);
        Assert.True(keywords.Length <= ExtractSink.MaxKeywordChars);
        Assert.True(allocated < 64L << 20, $"allocated {allocated:N0} bytes for 400,000 keywords");
    }

    [Fact]
    public void Centimetre_resolution_is_converted_and_no_unit_is_unknown()
    {
        var cm = Read(Full.Replace("<tiff:ResolutionUnit>2", "<tiff:ResolutionUnit>3").Replace("300/1", "118/1")).Picture!.Value;
        Assert.Equal(300, cm.DpiX);
        var none = Read(Full.Replace("<tiff:ResolutionUnit>2", "<tiff:ResolutionUnit>1")).Picture!.Value;
        Assert.Equal(0, none.DpiX);
    }
}
