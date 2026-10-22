using System.Buffers.Binary;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Readers.PictureFixtures;

namespace Anything.Tests.Content.Readers;

public class PictureReadersTests
{
    static PictureInfo Picture(ExtractedDoc doc) => doc.Picture ?? throw new Xunit.Sdk.XunitException("no picture details");

    static long Utc(int y, int mo, int d, int h, int mi, int s) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).ToFileTimeUtc();

    [Fact]
    public void Jpeg_gives_every_detail_in_order_of_trust()
    {
        var bytes = Jpeg(components: 4, adobeTransform: 2);
        var (doc, source) = ReaderRun.Counted(bytes, "jpg");
        Assert.Equal(DocStatus.NoText, doc.Status); // pictures have details, not body text
        var p = Picture(doc);
        Assert.Equal(640, p.Width);
        Assert.Equal(480, p.Height);
        Assert.Equal(32, p.BitDepth);
        Assert.Equal(ColorMode.Cmyk, p.ColorMode); // YCCK is CMYK
        Assert.Equal(240, p.DpiX); // the Photoshop resolution outranks EXIF (300) and JFIF (72)
        Assert.Equal(240, p.DpiY);
        Assert.Equal(6, p.Orientation);
        Assert.Equal((480, 640), p.Shown);
        Assert.Equal(Utc(2023, 7, 14, 13, 30, 12), p.DateTakenUtc); // 15:30:12 at +02:00
        Assert.Equal(1, p.ExifColorSpace);
        Assert.Equal(200, p.Iso);
        Assert.Equal(2.8f, p.FNumber, 3);
        Assert.Equal(0.004f, p.ExposureSeconds, 5);
        Assert.Equal(50f, p.FocalLengthMm, 3);
        Assert.True(p.HasGps);
        Assert.Equal(47_500_000, p.LatitudeMicro);
        Assert.Equal(-8_258_333, p.LongitudeMicro);
        Assert.Equal("Canon", doc.Field(DocField.CameraMake));
        Assert.Equal("EOS R5 Mark", doc.Field(DocField.CameraModel));
        Assert.Equal("Lighthouse at dusk", doc.Field(DocField.Title)); // IPTC (APP13) comes before EXIF's XP title and XMP
        Assert.Equal("Coated FOGRA39", doc.Field(DocField.ColorProfile));
        var keywords = doc.Field(DocField.Keywords)!;
        foreach (var k in new[] { "coast", "Leuchtturm Ü", "beach", "sunset", "xmp keyword", "extended keyword" }) Assert.Contains(k, keywords);
        Assert.Equal("Kiel, Germany", doc.Field(DocField.Location));
        Assert.Equal("Caption text", doc.Field(DocField.Comment));
        Assert.Equal("Photo Grapher", doc.Field(DocField.Author));
        // The scan data after SOS is never read.
        int sos = bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xDA]);
        Assert.False(source.Touched(sos + 10, bytes.Length));
    }

    [Fact]
    public void Jfif_units_0_is_no_resolution_and_cm_converts()
    {
        var noUnits = ReaderRun.Bytes(Jpeg(irb: false, jfifUnits: 0, jfifDensity: 1, exif: ExifWithoutResolution()), "jpg");
        Assert.Equal(0, Picture(noUnits).DpiX);
        var cm = ReaderRun.Bytes(Jpeg(irb: false, jfifUnits: 2, jfifDensity: 118, exif: ExifWithoutResolution()), "jpg");
        Assert.Equal(300, Picture(cm).DpiX);
        Assert.Equal(ColorMode.Rgb, Picture(cm).ColorMode);
    }

    static byte[] ExifWithoutResolution()
    {
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((271, 2, 4, TiffWriter.Ascii("Foo")));
        ifd.Entries.Add((296, 3, 1, t.U16(1))); // EXIF unit 1: no unit, so no resolution even with values
        ifd.Entries.Add((282, 5, 1, t.Rational((72, 1))));
        return t.Build(ifd);
    }

    [Fact]
    public void Exif_dates_zero_dates_gaps_and_gps_rules()
    {
        Assert.Equal(0, PictureValues.ExifDate("0000:00:00 00:00:00", null));
        Assert.Equal(0, PictureValues.ExifDate("    :  :     :  :  ", null));
        Assert.Equal(0, PictureValues.ExifDate("2023:02:30 10:00:00", null));
        Assert.Equal(Utc(2024, 1, 2, 3, 4, 5), PictureValues.ExifDate("2024:01:02 03:04:05\0", "Z"));
        Assert.Equal(Utc(2024, 1, 2, 8, 34, 5), PictureValues.ExifDate("2024-01-02 03:04:05", "-05:30"));
        // A local time inside a daylight-saving gap never throws (it uses the zone's standard offset).
        var zone = TimeZoneInfo.Local;
        for (var d = new DateTime(2024, 1, 1); d.Year == 2024; d = d.AddHours(1))
            if (zone.IsInvalidTime(d)) { Assert.NotEqual(0, PictureValues.ExifDate(d.ToString("yyyy:MM:dd HH:mm:ss"), null)); break; }

        // GPS without its references, with a zero denominator, or at (0, 0) is no location.
        var noRef = Jpeg(exif: Gps(writeRefs: false));
        Assert.False(Picture(ReaderRun.Bytes(noRef, "jpg")).HasGps);
        Assert.False(Picture(ReaderRun.Bytes(Jpeg(exif: Gps(zeroDenominator: true)), "jpg")).HasGps);
        Assert.False(Picture(ReaderRun.Bytes(Jpeg(exif: Gps(zero: true)), "jpg")).HasGps);
    }

    static byte[] Gps(bool writeRefs = true, bool zeroDenominator = false, bool zero = false)
    {
        var t = new TiffWriter(false);
        var ifd = new TiffIfd { Gps = new TiffIfd() };
        ifd.Entries.Add((271, 2, 4, TiffWriter.Ascii("Foo")));
        if (writeRefs) { ifd.Gps.Entries.Add((1, 2, 2, TiffWriter.Ascii("N"))); ifd.Gps.Entries.Add((3, 2, 2, TiffWriter.Ascii("E"))); }
        long n = zero ? 0 : 10;
        ifd.Gps.Entries.Add((2, 5, 3, t.Rational((n, zeroDenominator ? 0 : 1), (0, 1), (0, 1))));
        ifd.Gps.Entries.Add((4, 5, 3, t.Rational((n, 1), (0, 1), (0, 1))));
        return t.Build(ifd);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Tiff_reads_ifd0_in_either_byte_order_wherever_it_is(bool bigEndian, bool ifdAtEnd, bool bigTiff)
    {
        var bytes = Tiff(bigEndian, ifdAtEnd: ifdAtEnd, bigTiff: bigTiff);
        var (doc, source) = ReaderRun.Counted(bytes, "tif");
        var p = Picture(doc);
        Assert.Equal(2480, p.Width);
        Assert.Equal(3508, p.Height);
        Assert.Equal(ColorMode.Cmyk, p.ColorMode);
        Assert.Equal(32, p.BitDepth);
        Assert.Equal(300, p.DpiX);
        Assert.Equal("Adobe Photoshop", doc.Field(DocField.CreatorTool));
        Assert.Equal("Lighthouse at dusk", doc.Field(DocField.Title));
        if (ifdAtEnd) Assert.False(source.Touched(20_000, 180_000)); // the "pixels" in between are never read
    }

    [Fact]
    public void Tiff_photometric_modes_and_loops()
    {
        Assert.Equal(ColorMode.Rgb, Picture(ReaderRun.Bytes(Tiff(false, photometric: 2, samples: 3), "tiff")).ColorMode);
        Assert.Equal(ColorMode.Lab, Picture(ReaderRun.Bytes(Tiff(false, photometric: 8, samples: 3), "tiff")).ColorMode);
        // An IFD whose EXIF pointer points back at itself, and offsets outside the file.
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((256, 4, 1, t.U32(10)));
        ifd.Entries.Add((34665, 4, 1, t.U32(8)));
        ifd.Entries.Add((271, 2, 1000, t.U32(0x7FFFFFF0)));
        var loop = t.Build(ifd);
        var doc = ReaderRun.Bytes(loop, "tif");
        Assert.Equal(10, Picture(doc).Width);
        Assert.Null(doc.Field(DocField.CameraMake));
    }

    /// <summary>A small TIFF with text tags: ASCII ones (type 2) or Explorer's UTF-16 XP ones (BYTE arrays).</summary>
    static byte[] TiffWithText(params (int Tag, string Value)[] tags)
    {
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((256, 4, 1, t.U32(16)));
        ifd.Entries.Add((257, 4, 1, t.U32(16)));
        foreach (var (tag, value) in tags)
        {
            bool xp = tag >= 0x9C9B;
            var bytes = xp ? Encoding.Unicode.GetBytes(value + "\0") : TiffWriter.Ascii(value);
            ifd.Entries.Add(((ushort)tag, (ushort)(xp ? 1 : 2), bytes.Length, bytes));
        }
        return t.Build(ifd);
    }

    [Fact]
    public void Explorer_comments_survive_ImageDescription()
    {
        // A camera's ImageDescription, and a comment typed in Explorer (XPComment only).
        var camera = ReaderRun.Bytes(TiffWithText((270, "OLYMPUS DIGITAL CAMERA"), (0x9C9C, "with Anna")), "tif");
        Assert.Equal("with Anna", camera.Field(DocField.Comment));
        Assert.Equal("OLYMPUS DIGITAL CAMERA", camera.Field(DocField.Title)); // Windows shows ImageDescription as the title
        // What Explorer writes for Title and Comments: the title into ImageDescription and XPTitle, the comment into XPComment.
        var explorer = ReaderRun.Bytes(TiffWithText((270, "Beach day"), (0x9C9B, "Beach day"), (0x9C9C, "with Anna")), "tif");
        Assert.Equal("Beach day", explorer.Field(DocField.Title));
        Assert.Equal("with Anna", explorer.Field(DocField.Comment));
        // A caption in ImageDescription beside a different title is the comment.
        var caption = ReaderRun.Bytes(TiffWithText((270, "Sunset over the bay"), (0x9C9B, "Beach day")), "tif");
        Assert.Equal("Beach day", caption.Field(DocField.Title));
        Assert.Equal("Sunset over the bay", caption.Field(DocField.Comment));
        // The same in a JPEG's EXIF block.
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((270, 2, 10, TiffWriter.Ascii("Beach day")));
        ifd.Entries.Add((0x9C9B, 1, 20, Encoding.Unicode.GetBytes("Beach day\0")));
        ifd.Entries.Add((0x9C9C, 1, 20, Encoding.Unicode.GetBytes("with Anna\0")));
        var jpeg = ReaderRun.Bytes(Jpeg(irb: false, exif: t.Build(ifd)), "jpg");
        Assert.Equal("Beach day", jpeg.Field(DocField.Title));
        Assert.Equal("with Anna", jpeg.Field(DocField.Comment));
    }

    [Fact]
    public void Exif_user_comment_is_the_comment_after_explorers()
    {
        static ExtractedDoc WithUserComment(byte[] code, byte[] text, bool bigEndian = false, string? xpComment = null)
        {
            var t = new TiffWriter(bigEndian);
            var ifd = new TiffIfd { Exif = new TiffIfd() };
            ifd.Entries.Add((271, 2, 6, TiffWriter.Ascii("Canon")));
            if (xpComment != null) ifd.Entries.Add((0x9C9C, 1, 0, Encoding.Unicode.GetBytes(xpComment + "\0")));
            for (int i = 0; i < ifd.Entries.Count; i++) if (ifd.Entries[i].Count == 0) ifd.Entries[i] = ifd.Entries[i] with { Count = ifd.Entries[i].Value.Length };
            byte[] value = [.. code, .. text];
            ifd.Exif.Entries.Add((0x9286, 7, value.Length, value));
            return ReaderRun.Bytes(t.Build(ifd), "tif");
        }
        // What focus-stacking software writes, which Windows shows as the comment.
        Assert.Equal("Stacked from 3 images.", WithUserComment("ASCII\0\0\0"u8.ToArray(), "Stacked from 3 images.\0"u8.ToArray()).Field(DocField.Comment));
        Assert.Equal("Grüße aus Kiel", WithUserComment("UNICODE\0"u8.ToArray(), Encoding.Unicode.GetBytes("Grüße aus Kiel")).Field(DocField.Comment));
        Assert.Equal("Grüße aus Kiel", WithUserComment("UNICODE\0"u8.ToArray(), Encoding.BigEndianUnicode.GetBytes("Grüße aus Kiel"), bigEndian: true).Field(DocField.Comment));
        Assert.Equal("undefined code", WithUserComment(new byte[8], "undefined code"u8.ToArray()).Field(DocField.Comment));
        // Blank comments (most cameras), JIS and binary maker data are none.
        Assert.Null(WithUserComment("ASCII\0\0\0"u8.ToArray(), Encoding.ASCII.GetBytes(new string(' ', 40))).Field(DocField.Comment));
        Assert.Null(WithUserComment(new byte[8], new byte[64]).Field(DocField.Comment));
        Assert.Null(WithUserComment("JIS\0\0\0\0\0"u8.ToArray(), [0x30, 0x21, 0x30, 0x22]).Field(DocField.Comment));
        Assert.Null(WithUserComment(new byte[8], [0x41, 0x01, 0x02, 0x03, 0x42]).Field(DocField.Comment));
        // A comment typed in Explorer comes first.
        Assert.Equal("with Anna", WithUserComment("ASCII\0\0\0"u8.ToArray(), "Stacked"u8.ToArray(), xpComment: "with Anna").Field(DocField.Comment));
    }

    [Fact]
    public void Tiff_reads_at_most_1_MB_whatever_its_payloads_claim()
    {
        // Photoshop resources holding a 10 MB XMP block.
        var t = new TiffWriter(false);
        var ifd = new TiffIfd();
        ifd.Entries.Add((256, 4, 1, t.U32(2480)));
        ifd.Entries.Add((271, 2, 6, TiffWriter.Ascii("Canon")));
        var xmp = Encoding.UTF8.GetBytes(Xmp("Irb title").PadRight(10 << 20));
        var irb = Irb(0x0424, xmp);
        ifd.Entries.Add((34377, 7, irb.Length, irb));
        var (doc, source) = ReaderRun.Counted(t.Build(ifd), "tif");
        Assert.True(source.BytesRead <= 1 << 20, $"read {source.BytesRead:N0} bytes");
        Assert.Equal(2480, Picture(doc).Width);
        Assert.Equal("Canon", doc.Field(DocField.CameraMake));
        Assert.Null(doc.Field(DocField.Title)); // the XMP block didn't fit

        // IPTC, XMP and ICC tags of 900 KB each: IPTC (read first) fits, the others are skipped.
        var three = new TiffWriter(false);
        var tags = new TiffIfd();
        tags.Entries.Add((256, 4, 1, three.U32(2480)));
        var iptc = Iptc().Concat(new byte[900_000]).ToArray();
        tags.Entries.Add((33723, 7, iptc.Length, iptc));
        var bigXmp = Encoding.UTF8.GetBytes(Xmp("Xmp title").PadRight(900_000));
        tags.Entries.Add((700, 1, bigXmp.Length, bigXmp));
        var icc = Icc("Big profile").Concat(new byte[900_000]).ToArray();
        tags.Entries.Add((34675, 7, icc.Length, icc));
        var (limited, counted) = ReaderRun.Counted(three.Build(tags), "tif");
        Assert.True(counted.BytesRead <= 1 << 20, $"read {counted.BytesRead:N0} bytes");
        Assert.Equal("Lighthouse at dusk", limited.Field(DocField.Title));
        Assert.Null(limited.Field(DocField.ColorProfile));
    }

    [Fact]
    public void Heic_reads_at_most_1_MB_and_only_the_first_exif_and_xmp_items()
    {
        var (big, bigSource) = ReaderRun.Counted(Heic(exifPadding: 3 << 20), "heic");
        Assert.True(bigSource.BytesRead <= 1 << 20, $"read {bigSource.BytesRead:N0} bytes");
        Assert.Null(big.Field(DocField.CameraMake)); // the 3 MB Exif item didn't fit
        Assert.Contains("phone", big.Field(DocField.Keywords));
        Assert.Equal((4032, 3024), (Picture(big).Width, Picture(big).Height));

        var bytes = Heic(secondXmpBytes: 900_000);
        var (two, twoSource) = ReaderRun.Counted(bytes, "heic");
        Assert.Equal("Canon", two.Field(DocField.CameraMake));
        Assert.DoesNotContain("second", two.Field(DocField.Keywords));
        Assert.False(twoSource.Touched(bytes.Length - 900_000, bytes.Length)); // the second XMP item is never read
    }

    [Fact]
    public void Png_reads_chunks_before_and_after_the_image_data_and_never_trusts_text_without_its_crc()
    {
        Assert.Equal(0xCBF43926u, PictureFixtures.Crc32("123456789"u8));
        Assert.Equal(0xCBF43926u, Anything.Core.Content.Extract.Crc32.Compute("123456789"u8));
        var doc = ReaderRun.Bytes(Png(), "png");
        var p = Picture(doc);
        Assert.Equal(1200, p.Width);
        Assert.Equal(800, p.Height);
        Assert.Equal(32, p.BitDepth);
        Assert.Equal(ColorMode.Rgb, p.ColorMode);
        Assert.Equal(300, p.DpiX);
        Assert.Equal("Mountain lake", doc.Field(DocField.Title));
        Assert.Equal("Calm water at dawn", doc.Field(DocField.Comment));
        Assert.Equal("sRGB IEC61966-2.1", doc.Field(DocField.ColorProfile));
        Assert.Equal("Nikon", doc.Field(DocField.CameraMake));
        Assert.Equal("alpine", doc.Field(DocField.Keywords));
        Assert.Equal("Late Author", doc.Field(DocField.Author));
    }

    [Fact]
    public void Png_lying_lengths_stop_the_walk_with_what_was_read()
    {
        var bytes = Png();
        // IHDR stays; the pHYs chunk claims 2 GB.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33), 0x7FFFFFFF);
        var doc = ReaderRun.Bytes(bytes, "png");
        Assert.Equal(1200, Picture(doc).Width);
        Assert.Equal(DocStatus.NoText, doc.Status); // a picture's details are kept; pictures never have body text
        Assert.Null(doc.Field(DocField.Title)); // the walk stopped before the text chunks
    }

    [Fact]
    public void Gif_bmp_and_webp_headers()
    {
        var gif = Picture(ReaderRun.Bytes(Gif(), "gif"));
        Assert.Equal((320, 240), (gif.Width, gif.Height));
        Assert.Equal(ColorMode.Indexed, gif.ColorMode);
        Assert.Equal(8, gif.BitDepth);
        var bmp = Picture(ReaderRun.Bytes(Bmp(), "bmp"));
        Assert.Equal((800, 600), (bmp.Width, bmp.Height));
        Assert.Equal(150, bmp.DpiX);
        Assert.Equal(ColorMode.Rgb, bmp.ColorMode);
        Assert.Equal(ColorMode.Indexed, Picture(ReaderRun.Bytes(Bmp(bpp: 8), "bmp")).ColorMode);
        var webp = ReaderRun.Bytes(WebP(), "webp");
        var w = Picture(webp);
        Assert.Equal((1920, 1080), (w.Width, w.Height));
        Assert.Equal(32, w.BitDepth);
        Assert.Equal("Canon", webp.Field(DocField.CameraMake));
        Assert.Equal("Explorer title", webp.Field(DocField.Title)); // the EXIF chunk comes before the XMP one
        Assert.Contains("web", webp.Field(DocField.Keywords));
        var lossy = Picture(ReaderRun.Bytes(WebP(extended: false), "webp"));
        Assert.Equal((640, 480), (lossy.Width, lossy.Height));
    }

    [Fact]
    public void Heic_reads_the_primary_items_properties_and_exif_through_iloc()
    {
        var bytes = Heic();
        var (doc, source) = ReaderRun.Counted(bytes, "heic");
        var p = Picture(doc);
        Assert.Equal((4032, 3024), (p.Width, p.Height));
        Assert.Equal(24, p.BitDepth);
        Assert.Equal(ColorMode.Rgb, p.ColorMode);
        Assert.Equal(8, p.Orientation); // irot 1 (a quarter turn anticlockwise) wins over the EXIF orientation
        Assert.Equal((3024, 4032), p.Shown);
        Assert.Equal("Canon", doc.Field(DocField.CameraMake));
        Assert.True(p.HasGps);
        Assert.Contains("phone", doc.Field(DocField.Keywords));
        Assert.False(source.Touched(bytes.Length - 100, bytes.Length)); // the image item's data
    }

    [Fact]
    public void Picture_magic_decides_not_the_extension()
    {
        var doc = ReaderRun.Bytes(Png(), "jpg");
        Assert.Equal(1200, Picture(doc).Width);
        Assert.Equal(DocStatus.Unsupported, ReaderRun.Bytes(Encoding.ASCII.GetBytes("not a picture at all"), "jpg").Status);
    }

    [Fact]
    public void Icc_v4_mluc_descriptions()
    {
        var tag = new MemoryStream();
        tag.Write("mluc"u8); tag.Write(new byte[4]);
        var w = new BinaryWriter(tag);
        void Be(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); w.Write(b); }
        Be(2); Be(12);
        var de = Encoding.BigEndianUnicode.GetBytes("Farbprofil");
        var en = Encoding.BigEndianUnicode.GetBytes("Display P3");
        w.Write("deDE"u8); Be((uint)de.Length); Be(40);
        w.Write("enUS"u8); Be((uint)en.Length); Be(40 + (uint)de.Length);
        w.Write(de); w.Write(en);
        var t = tag.ToArray();
        var profile = new byte[144 + t.Length];
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), 1);
        "desc"u8.CopyTo(profile.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(140), (uint)t.Length);
        t.CopyTo(profile, 144);
        Assert.Equal("Display P3", PictureValues.IccDescription(profile));
        Assert.Null(PictureValues.IccDescription(new byte[10]));
    }
}
