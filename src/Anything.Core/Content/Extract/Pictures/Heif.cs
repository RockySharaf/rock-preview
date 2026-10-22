using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// HEIC, HEIF and AVIF (ISOBMFF): ispe, pixi, colr, irot/imir, Exif and XMP items through iinf/iloc. Only the top-level
/// box headers and the meta box are read, then the first Exif item's and the first XMP item's extents: at most 1 MB in
/// all (meta box and items together). The primary item's (pitm) properties win; every count, size and offset is checked
/// against its box.
/// </summary>
internal static class Heif
{
    const long MaxMeta = 1 << 20, MaxRead = 1 << 20;
    const int MaxBoxes = 4096;
    static readonly string[] Brands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "avif", "avis", "miaf"];

    readonly record struct Box(string Type, int Start, int End);
    readonly record struct Extent(long Offset, long Length);
    sealed class Item { public uint Id; public string Type = ""; public string? ContentType; public int Method; public long Base; public List<Extent> Extents = []; }

    public static void Read(IByteSource s, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        if (!Header(s, 0, out long ftypSize, out _, out _) || ftypSize > 4096) throw new InvalidDataException("No ftyp.");
        var ftyp = s.ReadBytes(0, ftypSize, 4096) ?? throw new InvalidDataException("Short ftyp.");
        bool known = false;
        for (int at = 8; at + 4 <= ftyp.Length; at += at == 8 ? 8 : 4) // major brand, minor version, compatible brands
            if (Array.IndexOf(Brands, Encoding.ASCII.GetString(ftyp, at, 4)) >= 0) { known = true; break; }
        if (!known) throw new InvalidDataException("Not HEIF.");

        long pos = ftypSize;
        for (int n = 0; n < 64 && pos + 8 <= s.Length; n++)
        {
            budget.Check();
            if (!Header(s, pos, out long size, out string type, out int header)) break;
            if (type == "meta")
            {
                if (size - header > MaxMeta) { parts.Partial = true; break; }
                var meta = s.ReadBytes(pos + header, size - header, MaxMeta);
                if (meta != null) Meta(s, meta, sink, budget, parts);
                break;
            }
            pos += size;
        }
    }

    /// <summary>A box header at an offset in the file: its total size (to the end for size 0), type and header length.</summary>
    static bool Header(IByteSource s, long pos, out long size, out string type, out int header)
    {
        Span<byte> h = stackalloc byte[16];
        size = 0; type = ""; header = 8;
        if (!s.TryRead(pos, h[..8])) return false;
        size = BinaryPrimitives.ReadUInt32BigEndian(h);
        type = Encoding.ASCII.GetString(h[4..8]);
        if (size == 1)
        {
            if (!s.TryRead(pos + 8, h[8..16])) return false;
            size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(h[8..]), long.MaxValue);
            header = 16;
        }
        else if (size == 0) size = s.Length - pos;
        return size >= header && size <= s.Length - pos;
    }

    /// <summary>The child boxes of a range of a box's payload.</summary>
    static List<Box> Children(byte[] b, int from, int to)
    {
        var list = new List<Box>();
        int pos = from;
        while (pos + 8 <= to && list.Count < MaxBoxes)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
            int header = 8;
            if (size == 1)
            {
                if (pos + 16 > to) break;
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(pos + 8)), int.MaxValue);
                header = 16;
            }
            else if (size == 0) size = to - pos;
            if (size < header || size > to - pos) break;
            list.Add(new Box(Encoding.ASCII.GetString(b, pos + 4, 4), pos + header, pos + (int)size));
            pos += (int)size;
        }
        return list;
    }

    static void Meta(IByteSource s, byte[] meta, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        // meta is a full box: version and flags first.
        var boxes = Children(meta, 4, meta.Length);
        uint primary = 0;
        var items = new Dictionary<uint, Item>();
        var properties = new List<Box>();
        var associations = new Dictionary<uint, List<int>>();
        Box? idat = null;
        foreach (var box in boxes)
        {
            budget.Check();
            switch (box.Type)
            {
                case "pitm": parts.Unit(() => { var p = new BoxReader(meta, box.Start, box.End); int v = p.U8(); p.Skip(3); primary = v == 0 ? p.U16() : p.U32(); }); break;
                case "iinf": parts.Unit(() => ItemInfos(meta, box, items)); break;
                case "iloc": parts.Unit(() => Locations(meta, box, items)); break;
                case "idat": idat = box; break;
                case "iprp":
                    foreach (var child in Children(meta, box.Start, box.End))
                    {
                        if (child.Type == "ipco") properties = Children(meta, child.Start, child.End);
                        else if (child.Type == "ipma") parts.Unit(() => Associations(meta, child, associations));
                    }
                    break;
            }
        }
        if (primary == 0 && items.Count > 0) primary = items.Keys.Min();

        // The primary item's properties; pixi and colr of the image tiles when a grid has none of its own.
        var mine = associations.TryGetValue(primary, out var list) ? list : [];
        Box? Property(string type, bool anyItem)
        {
            foreach (int index in mine)
                if (index >= 1 && index <= properties.Count && properties[index - 1].Type == type) return properties[index - 1];
            if (anyItem) foreach (var p in properties) if (p.Type == type) return p;
            return null;
        }
        var pic = sink.Picture;
        int rotation = Property("irot", false) is { } irot && irot.End > irot.Start ? meta[irot.Start] & 3 : 0;
        int mirror = Property("imir", false) is { } imir && imir.End > imir.Start ? (meta[imir.Start] & 1) + 1 : 0;
        pic.Orientation = Orientation(rotation, mirror);
        if (Property("ispe", true) is { } ispe && ispe.End - ispe.Start >= 12)
        {
            pic.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(ispe.Start + 4)), int.MaxValue);
            pic.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(ispe.Start + 8)), int.MaxValue);
        }
        int channels = 0;
        if (Property("pixi", true) is { } pixi && pixi.End - pixi.Start >= 5)
        {
            channels = meta[pixi.Start + 4];
            int bits = 0;
            for (int i = 0; i < channels && pixi.Start + 5 + i < pixi.End; i++) bits += meta[pixi.Start + 5 + i];
            pic.BitDepth = (byte)Math.Clamp(bits, 0, 255);
        }
        if (Property("colr", true) is { } colr && colr.End - colr.Start >= 4)
        {
            string kind = Encoding.ASCII.GetString(meta, colr.Start, 4);
            if (kind is "rICC" or "prof")
            {
                var profile = meta.AsSpan(colr.Start + 4, colr.End - colr.Start - 4);
                if (PictureValues.IccDescription(profile) is { Length: > 0 } name) sink.Field(DocField.ColorProfile, name);
                if (profile.Length >= 20)
                    pic.ColorMode = Encoding.ASCII.GetString(profile[16..20]) switch { "RGB " => ColorMode.Rgb, "GRAY" => ColorMode.Gray, "CMYK" => ColorMode.Cmyk, _ => ColorMode.Unknown };
            }
        }
        if (pic.Width > 0) pic.ColorMode = channels == 1 ? ColorMode.Gray : ColorMode.Rgb;

        // The first Exif item and the first XMP item only, within what is left of the 1 MB the picture may read.
        long allowance = MaxRead - meta.Length;
        bool exifRead = false, xmpRead = false;
        foreach (var item in items.Values)
        {
            budget.Check();
            if (item.Type == "Exif" && !exifRead)
            {
                exifRead = true;
                var data = ItemData(s, meta, idat, item, ref allowance);
                if (data == null || data.Length < 4) { parts.Partial = true; continue; }
                long offset = BinaryPrimitives.ReadUInt32BigEndian(data);
                if (offset > data.Length - 4 - 8) { parts.Partial = true; continue; }
                parts.Unit(() =>
                {
                    using var exif = new MemoryByteSource(data.AsMemory(4 + (int)offset), budget);
                    Tiff.Read(exif, sink, budget, TiffMode.Exif, parts);
                });
            }
            else if (item.Type == "mime" && item.ContentType?.Trim() == "application/rdf+xml" && !xmpRead)
            {
                xmpRead = true;
                if (ItemData(s, meta, idat, item, ref allowance) is not { } xmp || !XmlShape.ReadXmp(xmp, sink)) parts.Partial = true;
            }
        }
    }

    /// <summary>irot (quarter turns anticlockwise) and imir (1 = left-right, 2 = top-bottom) as an EXIF orientation.</summary>
    internal static byte Orientation(int rotation, int mirror) => (rotation & 3, mirror) switch
    {
        (0, 0) => 1, (1, 0) => 8, (2, 0) => 3, (3, 0) => 6,
        (0, 1) => 2, (0, 2) => 4, (2, 1) => 4, (2, 2) => 2,
        (1, 1) => 7, (1, 2) => 5, (3, 1) => 5, (3, 2) => 7,
        _ => 1,
    };

    static void ItemInfos(byte[] meta, Box box, Dictionary<uint, Item> items)
    {
        var r = new BoxReader(meta, box.Start, box.End);
        int version = r.U8();
        r.Skip(3);
        r.Skip(version == 0 ? 2 : 4); // the entry count: the infe boxes that follow are walked instead
        int from = r.Position;
        foreach (var infe in Children(meta, from, box.End))
        {
            if (infe.Type != "infe" || items.Count >= MaxBoxes) continue;
            var e = new BoxReader(meta, infe.Start, infe.End);
            int v = e.U8();
            e.Skip(3);
            if (v < 2) continue;
            uint id = v == 2 ? e.U16() : e.U32();
            e.Skip(2); // protection index
            string type = e.FourCc();
            e.CString(); // item name
            var item = items.TryGetValue(id, out var existing) ? existing : items[id] = new Item { Id = id };
            item.Type = type;
            if (type == "mime") item.ContentType = e.CString();
        }
    }

    static void Locations(byte[] meta, Box box, Dictionary<uint, Item> items)
    {
        var r = new BoxReader(meta, box.Start, box.End);
        int version = r.U8();
        r.Skip(3);
        int sizes = r.U8(), more = r.U8();
        int offsetSize = sizes >> 4, lengthSize = sizes & 15, baseSize = more >> 4, indexSize = version is 1 or 2 ? more & 15 : 0;
        long count = version < 2 ? r.U16() : r.U32();
        for (long i = 0; i < Math.Min(count, MaxBoxes); i++)
        {
            uint id = version < 2 ? r.U16() : r.U32();
            int method = 0;
            if (version is 1 or 2) method = r.U16() & 15;
            r.Skip(2); // data reference index
            long baseOffset = r.Sized(baseSize);
            int extents = r.U16();
            var item = items.TryGetValue(id, out var existing) ? existing : items[id] = new Item { Id = id };
            item.Method = method;
            item.Base = baseOffset;
            for (int x = 0; x < extents; x++)
            {
                if (indexSize > 0) r.Sized(indexSize);
                long offset = r.Sized(offsetSize), length = r.Sized(lengthSize);
                if (item.Extents.Count < 64) item.Extents.Add(new Extent(offset, length));
            }
        }
    }

    static void Associations(byte[] meta, Box box, Dictionary<uint, List<int>> associations)
    {
        var r = new BoxReader(meta, box.Start, box.End);
        int version = r.U8();
        int flags = r.U8() << 16 | r.U8() << 8 | r.U8();
        long count = r.U32();
        for (long i = 0; i < Math.Min(count, MaxBoxes); i++)
        {
            uint id = version < 1 ? r.U16() : r.U32();
            int n = r.U8();
            var list = new List<int>(n);
            for (int a = 0; a < n; a++) list.Add((flags & 1) != 0 ? r.U16() & 0x7FFF : r.U8() & 0x7F);
            associations.TryAdd(id, list);
        }
    }

    /// <summary>
    /// An item's bytes from its extents (in the file, or in idat for construction method 1); null when they don't fit what
    /// is left of the allowance, which they are taken from.
    /// </summary>
    static byte[]? ItemData(IByteSource s, byte[] meta, Box? idat, Item item, ref long allowance)
    {
        long total = 0;
        foreach (var e in item.Extents)
        {
            if (e.Length < 0 || e.Length > MaxRead) return null;
            total += e.Length;
        }
        if (item.Extents.Count == 0 || total <= 0 || total > allowance) return null;
        allowance -= total;
        var data = new byte[total];
        int at = 0;
        foreach (var e in item.Extents)
        {
            long offset = item.Base + e.Offset;
            if (item.Method == 1)
            {
                if (idat is not { } box || offset < 0 || offset + e.Length > box.End - box.Start) return null;
                meta.AsSpan(box.Start + (int)offset, (int)e.Length).CopyTo(data.AsSpan(at));
            }
            else if (item.Method == 0)
            {
                if (offset < 0 || !s.TryRead(offset, data.AsSpan(at, (int)e.Length))) return null;
            }
            else return null;
            at += (int)e.Length;
        }
        return data;
    }

    /// <summary>A bounds-checked big-endian reader over part of a box (InvalidDataException past its end).</summary>
    sealed class BoxReader(byte[] b, int start, int end)
    {
        int _pos = start;
        public int Position => _pos;

        void Need(int n) { if (n < 0 || _pos + n > end) throw new InvalidDataException("Box too short."); }
        public void Skip(int n) { Need(n); _pos += n; }
        public int U8() { Need(1); return b[_pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(_pos)); _pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(_pos)); _pos += 4; return v; }
        public long Sized(int size) => size switch
        {
            0 => 0,
            4 => U32(),
            8 => U64(),
            _ => throw new InvalidDataException("Bad field size."),
        };
        long U64() { Need(8); var v = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(_pos)); _pos += 8; return (long)Math.Min(v, long.MaxValue); }
        public string FourCc() { Need(4); var v = Encoding.ASCII.GetString(b, _pos, 4); _pos += 4; return v; }
        public string CString()
        {
            int nul = Array.IndexOf(b, (byte)0, _pos, end - _pos);
            int stop = nul < 0 ? end : nul;
            string v = Encoding.UTF8.GetString(b, _pos, stop - _pos);
            _pos = Math.Min(end, stop + 1);
            return v;
        }
    }
}
