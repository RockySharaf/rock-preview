using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Photoshop PSD/PSB: header, image resources and text layers, never channel data. The header gives the picture details
/// (mode, depth × channels; stored in Documents mode too); the image resources go through <see cref="Photoshop8Bim"/>
/// (resolution, IPTC, XMP, EXIF). Layer records only, at most 10,000 layers and 16 MB of records: layer names (luni, else
/// the Pascal name) and the text of type layers (TySh → descriptor → 'Txt '), found in the layer info or, for 16- and
/// 32-bit documents, in the Lr16/Lr32 blocks; a negative layer count is its absolute value, and PSB's 8-byte lengths are
/// honoured. The descriptor walker is iterative and confined to its block: an unknown item ends only that block.
/// </summary>
internal sealed class PsdReader : IContentReader
{
    const int MaxLayers = 10_000, MaxChannels = 56;
    const long MaxRecordBytes = 16 << 20, MaxTextBlock = 4 << 20;

    public ContentKind Kind => ContentKind.Design;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "psd" or "psb";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        source.PreferExactRanges();
        var h = source.ReadBytes(0, 26, 26);
        if (h == null || !h.AsSpan(0, 4).SequenceEqual("8BPS"u8)) throw new InvalidDataException("Not a Photoshop file.");
        int version = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(4));
        if (version is not (1 or 2)) throw new InvalidDataException("Unknown Photoshop version.");
        bool psb = version == 2;
        int channels = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(12));
        var p = sink.Picture;
        p.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(14)), int.MaxValue);
        p.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(18)), int.MaxValue);
        int depth = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(22));
        p.BitDepth = (byte)Math.Clamp(depth * Math.Clamp(channels, 1, MaxChannels), 0, 255);
        p.ColorMode = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(24)) switch
        {
            0 => ColorMode.Bitmap, 1 => ColorMode.Gray, 2 => ColorMode.Indexed, 3 => ColorMode.Rgb, 4 => ColorMode.Cmyk,
            7 => ColorMode.Multichannel, 8 => ColorMode.Duotone, 9 => ColorMode.Lab, _ => ColorMode.Unknown,
        };
        var parts = new PictureParts(budget);

        long pos = 26;
        if (!source.TryU32(pos, true, out uint colorData)) return DocStatus.Ok;
        pos += 4 + colorData;
        if (!source.TryU32(pos, true, out uint resources)) return DocStatus.Ok;
        long resourcesAt = pos + 4;
        if (resources <= source.Length - resourcesAt)
            parts.Unit(() => Photoshop8Bim.Read(new ByteSourceSlice(source, resourcesAt, resources), sink, budget, parts));
        pos = resourcesAt + resources;

        // Layer and mask information.
        var layers = new Layers(source, sink, budget, psb, parts);
        parts.Unit(() => layers.Section(pos));
        return parts.Partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    sealed class Layers(IByteSource s, ExtractSink sink, ExtractBudget budget, bool psb, PictureParts parts)
    {
        long _recordBytes;
        int _layers;
        bool _wrote;

        long Length(long at, bool wide)
        {
            if (wide) return s.TryU64(at, true, out ulong v) ? (long)Math.Min(v, long.MaxValue) : -1;
            return s.TryU32(at, true, out uint u) ? u : -1;
        }

        public void Section(long at)
        {
            int width = psb ? 8 : 4;
            long length = Length(at, psb);
            if (length <= 0) return;
            long start = at + width, end = start + length;
            if (end > s.Length || end < start) end = s.Length;
            // Layer info: its length, then the layer count and records.
            long info = Length(start, psb);
            if (info > 0 && start + width + info <= end) Records(start + width, start + width + info);
            long next = start + width + Math.Max(0, info);
            // Global layer mask info, then additional layer information blocks up to the section's end.
            long mask = Length(next, false);
            if (mask < 0) return;
            next += 4 + mask;
            Blocks(next, end, global: true);
        }

        /// <summary>The layer count (negative = its absolute value; the first alpha channel holds transparency) and the records.</summary>
        void Records(long at, long end)
        {
            if (!s.TryU16(at, true, out int raw)) return;
            int count = Math.Min(Math.Abs((int)(short)raw), MaxLayers);
            long pos = at + 2;
            Span<byte> blend = stackalloc byte[4];
            for (int i = 0; i < count && pos < end && !sink.Full; i++)
            {
                budget.Check();
                if (_layers++ >= MaxLayers) return;
                long recordStart = pos;
                if (!s.TryU16(pos + 16, true, out int channels) || channels > MaxChannels) { parts.Partial = true; return; }
                pos += 18 + channels * (psb ? 10 : 6);
                if (!s.TryRead(pos, blend) || !blend.SequenceEqual("8BIM"u8)) { parts.Partial = true; return; }
                pos += 12; // signature, blend mode key, opacity, clipping, flags, filler
                if (!s.TryU32(pos, true, out uint extra)) { parts.Partial = true; return; }
                long extraStart = pos + 4, extraEnd = extraStart + extra;
                if (extraEnd > end) { parts.Partial = true; return; }
                Layer(extraStart, extraEnd);
                pos = extraEnd;
                _recordBytes += pos - recordStart;
                if (_recordBytes > MaxRecordBytes) return;
            }
        }

        /// <summary>One layer's extra data: mask, blending ranges, Pascal name (padded to 4), then additional information blocks.</summary>
        void Layer(long at, long end)
        {
            long pos = at;
            if (!s.TryU32(pos, true, out uint maskLength)) return;
            pos += 4 + maskLength;
            if (!s.TryU32(pos, true, out uint ranges)) return;
            pos += 4 + ranges;
            if (pos >= end) return;
            var nameLength = s.ReadBytes(pos, 1, 1);
            if (nameLength == null) return;
            int n = nameLength[0];
            string? name = n > 0 && s.ReadBytes(pos + 1, n, 255) is { } nameBytes ? TextDecoding.Ansi().GetString(nameBytes) : null;
            pos += (1 + n + 3) & ~3;
            var layer = new LayerText();
            Blocks(pos, end, global: false, layer);
            string? shownName = layer.UnicodeName ?? name;
            if (layer.Divider) return; // the hidden end marker of a group
            bool nameIsText = layer.Text != null && shownName != null && layer.Text.TrimStart().StartsWith(shownName.Trim(), StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(shownName) && !nameIsText) Line(shownName);
            if (!string.IsNullOrWhiteSpace(layer.Text)) Line(layer.Text);
        }

        void Line(string text)
        {
            if (_wrote) sink.LineBreak();
            _wrote = true;
            sink.Text(text);
        }

        /// <summary>Keys whose length is 8 bytes in PSB files.</summary>
        static bool WideKey(ReadOnlySpan<byte> key) => key.SequenceEqual("LMsk"u8) || key.SequenceEqual("Lr16"u8) || key.SequenceEqual("Lr32"u8)
            || key.SequenceEqual("Layr"u8) || key.SequenceEqual("Mt16"u8) || key.SequenceEqual("Mt32"u8) || key.SequenceEqual("Mtrn"u8)
            || key.SequenceEqual("Alph"u8) || key.SequenceEqual("FMsk"u8) || key.SequenceEqual("lnk2"u8) || key.SequenceEqual("FEid"u8)
            || key.SequenceEqual("FXid"u8) || key.SequenceEqual("PxSD"u8);

        static bool IsSignature(ReadOnlySpan<byte> sig) => sig.SequenceEqual("8BIM"u8) || sig.SequenceEqual("8B64"u8);

        /// <summary>Additional layer information blocks (signature, key, length, data) from at to end.</summary>
        void Blocks(long at, long end, bool global, LayerText? layer = null)
        {
            long pos = at;
            Span<byte> head = stackalloc byte[8];
            for (int n = 0; n < 1000 && pos + 12 <= end; n++)
            {
                budget.Check();
                if (!s.TryRead(pos, head)) return;
                if (!IsSignature(head[..4]))
                {
                    // Writers pad block data to 2 or 4 bytes inconsistently: look a little further before giving up.
                    bool found = false;
                    for (int pad = 1; pad <= 3 && !found; pad++)
                        if (s.TryRead(pos + pad, head) && IsSignature(head[..4])) { pos += pad; found = true; }
                    if (!found) return;
                }
                var key = head[4..8].ToArray();
                bool wide = psb && WideKey(key);
                long length = Length(pos + 8, wide);
                long data = pos + 8 + (wide ? 8 : 4);
                if (length < 0 || length > end - data) { parts.Partial = true; return; }
                string k = Encoding.ASCII.GetString(key);
                if (global && k is "Lr16" or "Lr32" or "Layr") parts.Unit(() => Records(data, data + length));
                else if (layer != null)
                    switch (k)
                    {
                        case "luni" when length >= 4:
                            parts.Unit(() => layer.UnicodeName = UnicodeString(data, data + length));
                            break;
                        case "TySh" when length <= MaxTextBlock:
                            if (s.ReadBytes(data, length, MaxTextBlock) is { } block) parts.Unit(() => layer.Text = TypeTool(block));
                            break;
                        case "lsct" when length >= 4:
                            if (s.TryU32(data, true, out uint kind) && kind == 3) layer.Divider = true;
                            break;
                    }
                pos = data + length;
            }
        }

        string? UnicodeString(long at, long end)
        {
            if (!s.TryU32(at, true, out uint count) || count > (end - at - 4) / 2 || count > 65536) return null;
            var b = s.ReadBytes(at + 4, count * 2, 1 << 17);
            return b == null ? null : Encoding.BigEndianUnicode.GetString(b).TrimEnd('\0');
        }
    }

    sealed class LayerText
    {
        public string? UnicodeName, Text;
        public bool Divider;
    }

    /// <summary>
    /// The text of a type tool block: version, transform, text version, descriptor version, then the text descriptor,
    /// whose 'Txt ' item holds the text ('\r' between paragraphs). Null when the block isn't readable.
    /// </summary>
    internal static string? TypeTool(byte[] block)
    {
        var r = new DescriptorReader(block);
        if (r.U16() != 1) return null;
        r.Skip(48); // six doubles of transform
        r.U16();    // text version (50)
        r.U32();    // descriptor version (16)
        return r.TopLevelText();
    }

    /// <summary>
    /// An iterative walker over a Photoshop action descriptor in one block, with depth and item caps; finds the top-level
    /// 'Txt ' TEXT item. Any read past the block, an unknown item type or too much nesting ends the walk.
    /// </summary>
    internal sealed class DescriptorReader(byte[] b)
    {
        const int MaxDepth = 32, MaxItems = 10_000;
        int _pos;

        sealed class InvalidDescriptor : Exception;

        void Need(long n) { if (n < 0 || _pos + n > b.Length) throw new InvalidDescriptor(); }
        public void Skip(long n) { Need(n); _pos += (int)n; }
        public int U16() { Need(2); int v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(_pos)); _pos += 2; return v; }
        public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(_pos)); _pos += 4; return v; }
        string FourCc() { Need(4); var v = Encoding.ASCII.GetString(b, _pos, 4); _pos += 4; return v; }

        string Unicode()
        {
            uint count = U32();
            if (count > (b.Length - _pos) / 2) throw new InvalidDescriptor();
            string v = Encoding.BigEndianUnicode.GetString(b, _pos, (int)count * 2);
            _pos += (int)count * 2;
            return v.TrimEnd('\0');
        }

        /// <summary>A key or class id: a length then that many bytes, or 0 then a four-character code.</summary>
        string Id()
        {
            uint length = U32();
            if (length == 0) return FourCc();
            Need(length);
            if (length > 1024) throw new InvalidDescriptor();
            var v = Encoding.ASCII.GetString(b, _pos, (int)length);
            _pos += (int)length;
            return v;
        }

        public string? TopLevelText()
        {
            try { return Walk(); }
            catch (InvalidDescriptor) { return null; }
        }

        string? Walk()
        {
            // Each frame: items left, and whether its items have keys (descriptors) or not (lists).
            var stack = new Stack<(long Left, bool Keyed)>();
            Unicode(); // class name
            Id();      // class id
            stack.Push((U32(), true));
            int items = 0;
            string? text = null;
            while (stack.Count > 0)
            {
                var (left, keyed) = stack.Pop();
                if (left <= 0) continue;
                stack.Push((left - 1, keyed));
                if (++items > MaxItems) return text;
                string key = keyed ? Id() : "";
                string type = FourCc();
                switch (type)
                {
                    case "Objc" or "GlbO":
                        Unicode();
                        Id();
                        if (stack.Count >= MaxDepth) throw new InvalidDescriptor();
                        stack.Push((U32(), true));
                        break;
                    case "VlLs":
                        if (stack.Count >= MaxDepth) throw new InvalidDescriptor();
                        stack.Push((U32(), false));
                        break;
                    case "TEXT":
                    {
                        string v = Unicode();
                        if (key == "Txt " && stack.Count == 1 && text == null) return v;
                        break;
                    }
                    case "doub": Skip(8); break;
                    case "UntF": Skip(12); break;
                    case "long": Skip(4); break;
                    case "comp": Skip(8); break;
                    case "bool": Skip(1); break;
                    case "enum": Id(); Id(); break;
                    case "type" or "GlbC": Unicode(); Id(); break;
                    case "alis" or "tdta" or "Pth ": Skip(U32()); break;
                    case "UnFl": Skip(4); Skip(8L * U32()); break;
                    case "obj ": Reference(); break;
                    default: throw new InvalidDescriptor(); // unknown (ObAr and friends): only this block ends
                }
            }
            return text;
        }

        void Reference()
        {
            uint count = U32();
            if (count > 256) throw new InvalidDescriptor();
            for (int i = 0; i < count; i++)
            {
                switch (FourCc())
                {
                    case "prop": Unicode(); Id(); Id(); break;
                    case "Clss": Unicode(); Id(); break;
                    case "Enmr": Unicode(); Id(); Id(); Id(); break;
                    case "rele": Unicode(); Id(); Skip(4); break;
                    case "Idnt" or "indx": Skip(4); break;
                    case "name": Unicode(); Id(); Unicode(); break;
                    default: throw new InvalidDescriptor();
                }
            }
        }
    }
}
