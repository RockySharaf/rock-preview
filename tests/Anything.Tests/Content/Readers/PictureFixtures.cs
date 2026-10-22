using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Anything.Tests.Content.Readers;

/// <summary>An IFD of a TIFF being written: entries (tag, type, count, raw value bytes) and sub-IFDs.</summary>
internal sealed class TiffIfd
{
    public List<(ushort Tag, ushort Type, long Count, byte[] Value)> Entries { get; } = [];
    public TiffIfd? Exif, Gps;
}

/// <summary>Writes TIFF structures (II or MM, classic or BigTIFF) for TIFF files and EXIF blocks.</summary>
internal sealed class TiffWriter(bool bigEndian, bool bigTiff = false)
{
    public byte[] U16(params int[] v) { var b = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) Put16(b, i * 2, v[i]); return b; }
    public byte[] U32(params long[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) Put32(b, i * 4, v[i]); return b; }
    public byte[] Rational(params (long Num, long Den)[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++) { Put32(b, i * 8, v[i].Num); Put32(b, i * 8 + 4, v[i].Den); }
        return b;
    }
    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");

    void Put16(byte[] b, int at, int v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v); }
    void Put32(byte[] b, int at, long v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)v); }
    void Put64(byte[] b, int at, long v) { if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(at), (ulong)v); else BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), (ulong)v); }

    /// <summary>The whole TIFF: header, then (optionally) padding standing for image data, then the IFDs and their values.</summary>
    public byte[] Build(TiffIfd ifd0, int padding = 0)
    {
        var ms = new MemoryStream();
        int header = bigTiff ? 16 : 8;
        ms.Write(new byte[header]);
        ms.Write(new byte[padding]);
        long first = Write(ms, ifd0);
        var b = ms.ToArray();
        b[0] = b[1] = (byte)(bigEndian ? 'M' : 'I');
        Put16(b, 2, bigTiff ? 43 : 42);
        if (bigTiff) { Put16(b, 4, 8); Put16(b, 6, 0); Put64(b, 8, first); }
        else Put32(b, 4, first);
        return b;
    }

    long Write(MemoryStream ms, TiffIfd ifd)
    {
        var entries = ifd.Entries.ToList();
        if (ifd.Exif != null) entries.Add((34665, 4, 1, new byte[4]));
        if (ifd.Gps != null) entries.Add((34853, 4, 1, new byte[4]));
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        if (ms.Position % 2 == 1) ms.WriteByte(0);
        long start = ms.Position;
        int countSize = bigTiff ? 8 : 2, entrySize = bigTiff ? 20 : 12, inline = bigTiff ? 8 : 4;
        ms.Write(new byte[countSize + entries.Count * entrySize + (bigTiff ? 8 : 4)]);
        var valueAt = new long[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value.Length <= inline) continue;
            if (ms.Position % 2 == 1) ms.WriteByte(0);
            valueAt[i] = ms.Position;
            ms.Write(entries[i].Value);
        }
        long exif = ifd.Exif != null ? Write(ms, ifd.Exif) : 0, gps = ifd.Gps != null ? Write(ms, ifd.Gps) : 0;
        var b = ms.GetBuffer();
        if (bigTiff) Put64(b, (int)start, entries.Count); else Put16(b, (int)start, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var (tag, type, count, value) = entries[i];
            int at = (int)start + countSize + i * entrySize;
            Put16(b, at, tag);
            Put16(b, at + 2, type);
            if (bigTiff) Put64(b, at + 4, count); else Put32(b, at + 4, count);
            int v = at + (bigTiff ? 12 : 8);
            bool pointer = (tag == 34665 && ifd.Exif != null) || (tag == 34853 && ifd.Gps != null);
            if (pointer && bigTiff) { Put64(b, v, tag == 34665 ? exif : gps); continue; }
            if (pointer) value = U32(tag == 34665 ? exif : gps);
            if (value.Length <= inline) value.CopyTo(b, v);
            else if (bigTiff) Put64(b, v, valueAt[i]);
            else Put32(b, v, valueAt[i]);
        }
        return start;
    }
}

/// <summary>Pictures written byte by byte from their specifications: JPEG, TIFF, PNG, GIF, BMP, WebP and HEIC.</summary>
internal static class PictureFixtures
{
    /// <summary>The EXIF block of a camera picture: make, model, orientation 6, 300 dpi, date with offset, exposure, GPS.</summary>
    public static byte[] Exif(bool bigEndian = false, bool gps = true, string date = "2023:07:14 15:30:12", string? offset = "+02:00")
    {
        var t = new TiffWriter(bigEndian);
        var ifd0 = new TiffIfd();
        ifd0.Entries.Add((271, 2, 6, TiffWriter.Ascii("Canon")));
        ifd0.Entries.Add((272, 2, 13, TiffWriter.Ascii("EOS R5 Mark")));
        ifd0.Entries.Add((274, 3, 1, t.U16(6)));
        ifd0.Entries.Add((282, 5, 1, t.Rational((300, 1))));
        ifd0.Entries.Add((283, 5, 1, t.Rational((300, 1))));
        ifd0.Entries.Add((296, 3, 1, t.U16(2)));
        ifd0.Entries.Add((0x9C9B, 1, 0, Encoding.Unicode.GetBytes("Explorer title\0")));
        ifd0.Entries.Add((0x9C9E, 1, 0, Encoding.Unicode.GetBytes("beach;sunset\0")));
        for (int i = 0; i < ifd0.Entries.Count; i++)
            if (ifd0.Entries[i].Count == 0) ifd0.Entries[i] = ifd0.Entries[i] with { Count = ifd0.Entries[i].Value.Length };
        ifd0.Exif = new TiffIfd();
        ifd0.Exif.Entries.Add((36867, 2, 20, TiffWriter.Ascii(date)));
        if (offset != null) ifd0.Exif.Entries.Add((36881, 2, 7, TiffWriter.Ascii(offset)));
        ifd0.Exif.Entries.Add((0xA001, 3, 1, t.U16(1)));
        ifd0.Exif.Entries.Add((0x8827, 3, 1, t.U16(200)));
        ifd0.Exif.Entries.Add((0x829D, 5, 1, t.Rational((28, 10))));
        ifd0.Exif.Entries.Add((0x829A, 5, 1, t.Rational((1, 250))));
        ifd0.Exif.Entries.Add((0x920A, 5, 1, t.Rational((50, 1))));
        if (gps)
        {
            ifd0.Gps = new TiffIfd();
            ifd0.Gps.Entries.Add((1, 2, 2, TiffWriter.Ascii("N")));
            ifd0.Gps.Entries.Add((2, 5, 3, t.Rational((47, 1), (30, 1), (0, 1))));
            ifd0.Gps.Entries.Add((3, 2, 2, TiffWriter.Ascii("W")));
            ifd0.Gps.Entries.Add((4, 5, 3, t.Rational((8, 1), (15, 1), (3000, 100))));
        }
        return t.Build(ifd0);
    }

    static byte[] Segment(int marker, byte[] payload)
    {
        var b = new byte[4 + payload.Length];
        b[0] = 0xFF; b[1] = (byte)marker;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(b, 4);
        return b;
    }

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Iptc(bool utf8 = true)
    {
        var ms = new MemoryStream();
        void Set(int record, int dataset, byte[] v)
        {
            ms.WriteByte(0x1C); ms.WriteByte((byte)record); ms.WriteByte((byte)dataset);
            ms.WriteByte((byte)(v.Length >> 8)); ms.WriteByte((byte)v.Length);
            ms.Write(v);
        }
        if (utf8) Set(1, 90, [0x1B, 0x25, 0x47]);
        var enc = utf8 ? Encoding.UTF8 : Encoding.Latin1;
        Set(2, 5, enc.GetBytes("Lighthouse at dusk"));
        Set(2, 25, enc.GetBytes("coast"));
        Set(2, 25, enc.GetBytes("Leuchtturm Ü"));
        Set(2, 120, enc.GetBytes("Caption text"));
        Set(2, 80, enc.GetBytes("Photo Grapher"));
        Set(2, 90, enc.GetBytes("Kiel"));
        Set(2, 101, enc.GetBytes("Germany"));
        return ms.ToArray();
    }

    /// <summary>A Photoshop image resource block (8BIM) with a Pascal name.</summary>
    public static byte[] Irb(int id, byte[] data, string name = "")
    {
        var ms = new MemoryStream();
        ms.Write("8BIM"u8);
        ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
        ms.WriteByte((byte)name.Length); ms.Write(Encoding.ASCII.GetBytes(name));
        if ((1 + name.Length) % 2 == 1) ms.WriteByte(0);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
        ms.Write(size);
        ms.Write(data);
        if (data.Length % 2 == 1) ms.WriteByte(0);
        return ms.ToArray();
    }

    public static byte[] Resolution(double dpi, int unit = 1)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(dpi * 65536));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)unit);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(dpi * 65536));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)unit);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14), 1);
        return b;
    }

    public static string Xmp(string title, params string[] keywords) => OfficeFixtures.XmpPacket(title, "image/jpeg", keywords);

    /// <summary>An ICC profile header with a v2 'desc' tag.</summary>
    public static byte[] Icc(string description, string colorSpace = "RGB ")
    {
        var desc = new MemoryStream();
        desc.Write("desc"u8); desc.Write(new byte[4]);
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)description.Length + 1); desc.Write(len);
        desc.Write(Encoding.ASCII.GetBytes(description)); desc.WriteByte(0);
        desc.Write(new byte[12]);
        var tag = desc.ToArray();
        var b = new byte[128 + 4 + 12 + tag.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(colorSpace).CopyTo(b, 16);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(128), 1);
        "desc"u8.CopyTo(b.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(140), (uint)tag.Length);
        tag.CopyTo(b, 144);
        return b;
    }

    /// <summary>
    /// A JPEG with APP0 JFIF (72 dpi), APP1 Exif (300 dpi, GPS), APP13 IRB (240 dpi, IPTC), APP14 Adobe, APP1 XMP and an
    /// extended XMP chunk, APP2 ICC, COM, then SOF with the given components, SOS and scan bytes that must never be read.
    /// </summary>
    public static byte[] Jpeg(int components = 3, int adobeTransform = 1, bool irb = true, int jfifUnits = 1, int jfifDensity = 72, byte[]? exif = null)
    {
        var jfif = Concat("JFIF\0"u8.ToArray(), [1, 2, (byte)jfifUnits, (byte)(jfifDensity >> 8), (byte)jfifDensity, (byte)(jfifDensity >> 8), (byte)jfifDensity, 0, 0]);
        var exifBlock = Concat("Exif\0\0"u8.ToArray(), exif ?? Exif());
        var photoshop = Concat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), Irb(0x03ED, Resolution(240)), Irb(0x0404, Iptc()));
        var adobe = Concat("Adobe"u8.ToArray(), [0, 100, 0, 0, 0, 0, (byte)adobeTransform]);
        string guid = "0123456789ABCDEF0123456789ABCDEF";
        var mainXmp = Concat(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), Encoding.UTF8.GetBytes(Xmp("XMP title", "xmp keyword")));
        var extendedPacket = Encoding.UTF8.GetBytes(OfficeFixtures.XmpPacket("Extended title", "image/jpeg", "extended keyword"));
        var extended = new MemoryStream();
        extended.Write(Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0"));
        extended.Write(Encoding.ASCII.GetBytes(guid));
        var be = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)extendedPacket.Length);
        extended.Write(be);
        extended.Write(extendedPacket);
        var icc = Concat(Encoding.ASCII.GetBytes("ICC_PROFILE\0"), [1, 1], Icc("Coated FOGRA39"));
        var sof = new byte[] { 8, 0x01, 0xE0, 0x02, 0x80, (byte)components };
        var sos = Concat([0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 0x3F, 0], Encoding.ASCII.GetBytes(new string('S', 4000)), [0xFF, 0xD9]);
        var parts = new List<byte[]> { new byte[] { 0xFF, 0xD8 }, Segment(0xE0, jfif), Segment(0xE1, exifBlock) };
        if (irb) parts.Add(Segment(0xED, photoshop));
        parts.Add(Segment(0xEE, adobe));
        parts.Add(Segment(0xE1, mainXmp));
        parts.Add(Segment(0xE1, extended.ToArray()));
        parts.Add(Segment(0xE2, icc));
        parts.Add(Segment(0xFE, Encoding.ASCII.GetBytes("Shot on location")));
        parts.Add(Segment(0xC0, sof.Concat(new byte[components * 3]).ToArray()));
        parts.Add(sos);
        return Concat([.. parts]);
    }

    /// <summary>A TIFF file: size, depth, photometric, resolution in centimetres, with the IFD after the "pixels" when asked.</summary>
    public static byte[] Tiff(bool bigEndian, int photometric = 5, int samples = 4, bool ifdAtEnd = false, bool bigTiff = false)
    {
        var t = new TiffWriter(bigEndian, bigTiff);
        var ifd0 = new TiffIfd();
        ifd0.Entries.Add((256, 4, 1, t.U32(2480)));
        ifd0.Entries.Add((257, 4, 1, t.U32(3508)));
        ifd0.Entries.Add((258, 3, samples, t.U16(Enumerable.Repeat(8, samples).ToArray())));
        ifd0.Entries.Add((262, 3, 1, t.U16(photometric)));
        ifd0.Entries.Add((277, 3, 1, t.U16(samples)));
        ifd0.Entries.Add((282, 5, 1, t.Rational((11811, 100))));
        ifd0.Entries.Add((283, 5, 1, t.Rational((11811, 100))));
        ifd0.Entries.Add((296, 3, 1, t.U16(3)));
        ifd0.Entries.Add((305, 2, 15, TiffWriter.Ascii("Adobe Photoshop")));
        ifd0.Entries.Add((33723, 7, Iptc().Length, Iptc()));
        return t.Build(ifd0, ifdAtEnd ? 200_000 : 0);
    }

    static byte[] Chunk(string type, byte[] data)
    {
        var b = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        data.CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + data.Length), Crc32(b.AsSpan(4, 4 + data.Length)));
        return b;
    }

    /// <summary>CRC-32 (IEEE), bit by bit (an implementation independent of the reader's table); check value of "123456789" is CBF43926.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>A PNG: IHDR, pHYs 300 dpi, iCCP, tEXt, zTXt, iTXt XMP, eXIf, IDAT (with "tEXt" bytes inside), text after IDAT, IEND.</summary>
    public static byte[] Png(bool textAfterImage = true)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1200);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 800);
        ihdr[8] = 8; ihdr[9] = 6;
        var phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys, 11811);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), 11811);
        phys[8] = 1;
        var iccp = Concat(Encoding.Latin1.GetBytes("sRGB IEC61966-2.1"), [0, 0], Zlib(Icc("sRGB IEC61966-2.1")));
        var text = Concat(Encoding.Latin1.GetBytes("Title"), [0], Encoding.Latin1.GetBytes("Mountain lake"));
        var ztxt = Concat(Encoding.Latin1.GetBytes("Description"), [0, 0], Zlib(Encoding.Latin1.GetBytes("Calm water at dawn")));
        var itxt = Concat(Encoding.Latin1.GetBytes("XML:com.adobe.xmp"), [0, 1, 0, 0, 0], Zlib(Encoding.UTF8.GetBytes(Xmp("XMP lake", "alpine"))));
        var exif = new TiffWriter(true);
        var ifd = new TiffIfd();
        ifd.Entries.Add((271, 2, 6, TiffWriter.Ascii("Nikon")));
        var fakePixels = new byte[20_000];
        new Random(3).NextBytes(fakePixels);
        "tEXtAuthor\0Bogus"u8.CopyTo(fakePixels.AsSpan(500)); // must not be taken for a chunk: no CRC matches
        var author = Concat(Encoding.Latin1.GetBytes("Author"), [0], Encoding.Latin1.GetBytes("Late Author"));
        var parts = new List<byte[]>
        {
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, Chunk("IHDR", ihdr), Chunk("pHYs", phys), Chunk("iCCP", iccp),
            Chunk("tEXt", text), Chunk("zTXt", ztxt), Chunk("iTXt", itxt), Chunk("eXIf", exif.Build(ifd)), Chunk("IDAT", fakePixels),
        };
        if (textAfterImage) parts.Add(Chunk("tEXt", author));
        parts.Add(Chunk("IEND", []));
        return Concat([.. parts]);
    }

    public static byte[] Gif() => Concat("GIF89a"u8.ToArray(), [0x40, 0x01, 0xF0, 0x00, 0xF7, 0, 0], new byte[768], [0x3B]);

    public static byte[] Bmp(int bpp = 24, int height = -600)
    {
        var b = new byte[54 + 100];
        "BM"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), 800);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)bpp);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(38), 5906);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(42), 5906);
        return b;
    }

    static byte[] RiffChunk(string fourcc, byte[] data)
    {
        var b = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
        data.CopyTo(b, 8);
        return b;
    }

    /// <summary>A WebP with VP8X (alpha, EXIF, XMP), the image data, then EXIF and XMP chunks at the end.</summary>
    public static byte[] WebP(bool extended = true)
    {
        byte[] body;
        if (extended)
        {
            var vp8x = new byte[10];
            vp8x[0] = 0x10 | 0x08 | 0x04;
            int w = 1919, h = 1079;
            vp8x[4] = (byte)w; vp8x[5] = (byte)(w >> 8); vp8x[6] = (byte)(w >> 16);
            vp8x[7] = (byte)h; vp8x[8] = (byte)(h >> 8); vp8x[9] = (byte)(h >> 16);
            body = Concat("WEBP"u8.ToArray(), RiffChunk("VP8X", vp8x), RiffChunk("VP8L", Lossless(1920, 1080)), RiffChunk("EXIF", Exif()),
                RiffChunk("XMP ", Encoding.UTF8.GetBytes(Xmp("WebP title", "web"))));
        }
        else
        {
            var vp8 = new byte[30];
            vp8[3] = 0x9D; vp8[4] = 0x01; vp8[5] = 0x2A;
            BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(6), 640);
            BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(8), 480);
            body = Concat("WEBP"u8.ToArray(), RiffChunk("VP8 ", vp8));
        }
        var head = new byte[8];
        "RIFF"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)body.Length);
        return Concat(head, body);
    }

    static byte[] Lossless(int w, int h)
    {
        var b = new byte[2000];
        b[0] = 0x2F;
        uint bits = (uint)(w - 1) | (uint)(h - 1) << 14 | 1u << 28;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), bits);
        return b;
    }

    static byte[] Box(string type, params byte[][] content)
    {
        var data = Concat(content);
        var b = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        data.CopyTo(b, 8);
        return b;
    }

    static byte[] FullBox(string type, int version, params byte[][] content) => Box(type, Concat([(byte)version, 0, 0, 0], Concat(content)));
    static byte[] Be16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    static byte[] Be32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }

    /// <summary>
    /// A HEIC: ftyp, meta (pitm, iinf, iloc v1, iprp with ispe 4032×3024, pixi 3×8, colr nclx, irot 1, idat with the XMP),
    /// and mdat holding the Exif item (4-byte offset, "Exif\0\0", TIFF) and image bytes never read. On request the Exif
    /// item is padded to a hostile size, and a second XMP item of the given size follows the image bytes in mdat.
    /// </summary>
    public static byte[] Heic(int exifPadding = 0, int secondXmpBytes = 0)
    {
        var exifItem = Concat(Be32(6), "Exif\0\0"u8.ToArray(), Exif(bigEndian: true), new byte[exifPadding]);
        var xmp = Encoding.UTF8.GetBytes(Xmp("HEIC title", "phone"));
        var secondXmp = secondXmpBytes == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(Xmp("Second XMP title", "second").PadRight(secondXmpBytes));
        var ftyp = Box("ftyp", "heic"u8.ToArray(), Be32(0), "mif1heic"u8.ToArray());
        int items = secondXmp.Length > 0 ? 4 : 3;
        byte[] Meta(long mdatData) =>
            FullBox("meta", 0,
                FullBox("hdlr", 0, Be32(0), "pict"u8.ToArray(), new byte[12], [0]),
                FullBox("pitm", 0, Be16(1)),
                FullBox("iinf", 0, Be16(items),
                    FullBox("infe", 2, Be16(1), Be16(0), "hvc1"u8.ToArray(), [0]),
                    FullBox("infe", 2, Be16(2), Be16(0), "Exif"u8.ToArray(), [0]),
                    FullBox("infe", 2, Be16(3), Be16(0), "mime"u8.ToArray(), [0], Encoding.ASCII.GetBytes("application/rdf+xml\0")),
                    items == 4 ? FullBox("infe", 2, Be16(4), Be16(0), "mime"u8.ToArray(), [0], Encoding.ASCII.GetBytes("application/rdf+xml\0")) : Array.Empty<byte>()),
                FullBox("iloc", 1, [0x44, 0x00], Be16(items),
                    Be16(1), Be16(0), Be16(0), Be16(1), Be32(mdatData + exifItem.Length), Be32(100),
                    Be16(2), Be16(0), Be16(0), Be16(1), Be32(mdatData), Be32(exifItem.Length),
                    Be16(3), Be16(1), Be16(0), Be16(1), Be32(0), Be32(xmp.Length),
                    items == 4 ? Concat(Be16(4), Be16(0), Be16(0), Be16(1), Be32(mdatData + exifItem.Length + 100), Be32(secondXmp.Length)) : Array.Empty<byte>()),
                Box("idat", xmp),
                Box("iprp",
                    Box("ipco",
                        FullBox("ispe", 0, Be32(4032), Be32(3024)),
                        FullBox("pixi", 0, [3, 8, 8, 8]),
                        Box("colr", "nclx"u8.ToArray(), Be16(1), Be16(13), Be16(6), [0x80]),
                        Box("irot", [1])),
                    FullBox("ipma", 0, Be32(1), Be16(1), [4, 0x81, 0x82, 0x03, 0x84])));
        // Lay out once to learn where mdat's data starts, then build for real.
        int metaLength = Meta(0).Length;
        long mdatData = ftyp.Length + metaLength + 8;
        var mdat = Box("mdat", exifItem, new byte[100], secondXmp);
        return Concat(ftyp, Meta(mdatData), mdat);
    }
}
