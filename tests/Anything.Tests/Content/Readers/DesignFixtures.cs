using System.Buffers.Binary;
using System.Text;

namespace Anything.Tests.Content.Readers;

/// <summary>Photoshop (PSD/PSB) files with layers and type layers, and InDesign/EPS blobs with XMP, written from the file format specification.</summary>
internal static class DesignFixtures
{
    sealed class Be(bool psb)
    {
        public readonly MemoryStream S = new();
        public Be U8(int v) { S.WriteByte((byte)v); return this; }
        public Be U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); S.Write(b); return this; }
        public Be U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); S.Write(b); return this; }
        public Be U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); S.Write(b); return this; }
        public Be Len(long v, bool wide) => wide && psb ? U64(v) : U32(v);
        public Be Bytes(byte[] b) { S.Write(b); return this; }
        public Be Ascii(string s) { S.Write(Encoding.ASCII.GetBytes(s)); return this; }
        public byte[] ToArray() => S.ToArray();
    }

    static byte[] UnicodeString(string s)
    {
        var b = new Be(false).U32(s.Length);
        b.Bytes(Encoding.BigEndianUnicode.GetBytes(s));
        return b.ToArray();
    }

    static byte[] Key(string k) => k.Length == 4 ? new Be(false).U32(0).Ascii(k).ToArray() : new Be(false).U32(k.Length).Ascii(k).ToArray();

    /// <summary>A type tool block whose text descriptor has other items (a nested descriptor, a list) before 'Txt '.</summary>
    public static byte[] TypeTool(string text)
    {
        var d = new Be(false);
        d.U16(1);
        d.Bytes(new byte[48]);
        d.U16(50).U32(16);
        d.Bytes(UnicodeString("\0")).Bytes(Key("TxLr")).U32(4);
        // A nested descriptor with a double and a unit float.
        d.Bytes(Key("bounds")).Ascii("Objc").Bytes(UnicodeString("")).Bytes(Key("bounds")).U32(2)
            .Bytes(Key("Left")).Ascii("UntF").Ascii("#Pnt").Bytes(new byte[8])
            .Bytes(Key("Top ")).Ascii("doub").Bytes(new byte[8]);
        // A list of longs.
        d.Bytes(Key("list")).Ascii("VlLs").U32(2).Ascii("long").U32(1).Ascii("long").U32(2);
        d.Bytes(Key("Txt ")).Ascii("TEXT").Bytes(UnicodeString(text + "\0"));
        d.Bytes(Key("EngineData")).Ascii("tdta").U32(5).Ascii("<< >>");
        return d.ToArray();
    }

    static byte[] Info(Be w, string key, byte[] data)
    {
        w.Ascii("8BIM").Ascii(key);
        w.Len(data.Length, key is "Lr16" or "Lr32" or "Layr" or "LMsk");
        w.Bytes(data);
        if (data.Length % 2 == 1) w.U8(0);
        return w.ToArray();
    }

    sealed record Layer(string Name, string? Unicode = null, string? Text = null, bool Divider = false);

    static byte[] Records(bool psb, IReadOnlyList<Layer> layers)
    {
        var w = new Be(psb);
        w.U16(layers.Count);
        foreach (var layer in layers)
        {
            w.U32(0).U32(0).U32(100).U32(200); // rectangle
            w.U16(3);
            for (int c = 0; c < 3; c++) { w.U16(c); if (psb) w.U64(2); else w.U32(2); }
            w.Ascii("8BIM").Ascii("norm").U8(255).U8(0).U8(0).U8(0);
            var extra = new Be(psb);
            extra.U32(0).U32(0);
            var name = Encoding.ASCII.GetBytes(layer.Name);
            extra.U8(name.Length).Bytes(name);
            int pad = (4 - (1 + name.Length) % 4) % 4;
            extra.Bytes(new byte[pad]);
            if (layer.Unicode != null) Info(extra, "luni", UnicodeString(layer.Unicode));
            if (layer.Text != null) Info(extra, "TySh", TypeTool(layer.Text));
            if (layer.Divider) Info(extra, "lsct", new Be(false).U32(3).ToArray());
            var e = extra.ToArray();
            w.U32(e.Length).Bytes(e);
        }
        // Channel image data: compression + 0 bytes per channel (2 bytes each, as declared).
        for (int i = 0; i < layers.Count * 3; i++) w.U16(0);
        return w.ToArray();
    }

    static readonly Layer[] DefaultLayers =
    [
        new("Logo client", "Logo client Ä"),
        new("Headline text", "Headline text", "Headline text\rSecond paragraph of copy"),
        new("</Layer group>", Divider: true),
    ];

    /// <summary>A PSD (or PSB): CMYK 8-bit (or 16-bit with layers in Lr16), 300 dpi, IPTC and XMP resources, and the layers above.</summary>
    public static byte[] Psd(bool psb = false, int depth = 8, int layerCount = -1, int mode = 4)
    {
        var w = new Be(psb);
        w.Ascii("8BPS").U16(psb ? 2 : 1).Bytes(new byte[6]).U16(4).U32(3508).U32(2480).U16(depth).U16(mode);
        w.U32(0); // colour mode data
        var resources = Concat(PictureFixtures.Irb(0x03ED, PictureFixtures.Resolution(300, unit: 2)),
            PictureFixtures.Irb(0x040C, new byte[3000]), // a thumbnail, skipped
            PictureFixtures.Irb(0x0424, Encoding.UTF8.GetBytes(OfficeFixtures.XmpPacket("Poster Design", "application/vnd.adobe.photoshop", "poster", "print"))));
        w.U32(resources.Length).Bytes(resources);
        var layers = layerCount < 0 ? DefaultLayers : Enumerable.Range(0, layerCount).Select(i => new Layer("L" + i)).ToArray();
        var records = Records(psb, layers);
        var section = new Be(psb);
        if (depth == 8)
        {
            section.Len(records.Length, true).Bytes(records);
            section.U32(0); // global layer mask
        }
        else
        {
            section.Len(0, true);
            section.U32(0);
            Info(section, depth == 16 ? "Lr16" : "Lr32", records);
        }
        var s = section.ToArray();
        w.Len(s.Length, true).Bytes(s);
        w.U16(0).Bytes(new byte[5000]); // merged image data, never read
        return w.ToArray();
    }

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>An InDesign-like blob: a placed picture's packet at 1 MB, the document's at 3 MB, 8 MB in all.</summary>
    public static byte[] Indd()
    {
        var b = new byte[8 << 20];
        new byte[] { 0x06, 0x06, 0xED, 0xF5, 0xD8, 0x1D, 0x46, 0xE5, 0xBD, 0x31, 0xEF, 0xE7, 0xFE, 0x74, 0xB7, 0x1D }.CopyTo(b, 0);
        Encoding.UTF8.GetBytes(OfficeFixtures.XmpPacket("Stock photo title", "image/jpeg", "stock")).CopyTo(b, 1 << 20);
        Encoding.UTF8.GetBytes(OfficeFixtures.XmpPacket("Catalog 2024", "application/x-indesign", "catalog", "spring")).CopyTo(b, 3 << 20);
        return b;
    }

    public static byte[] Eps(bool dos = false)
    {
        var ps = Encoding.Latin1.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%Title: (Spring Poster)\n%%Creator: Adobe Illustrator(R) 24.0\n" +
            "%ADO_ContainsXMP: MainFirst\n%%EndComments\n" +
            OfficeFixtures.XmpPacket("Poster XMP", "application/postscript", "eps keyword") + "\n" +
            new string('%', 5000) + "\n" + OfficeFixtures.XmpPacket("Placed", "image/tiff", "placed only") + "\n%%EOF\n");
        if (!dos) return ps;
        var head = new byte[30];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)ps.Length);
        return Concat(head, ps, new byte[2000]);
    }
}
