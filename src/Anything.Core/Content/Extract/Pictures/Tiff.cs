using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>What a TIFF structure describes: a TIFF file's image, or an EXIF block inside another format.</summary>
internal enum TiffMode : byte { Image, Exif }

/// <summary>
/// TIFF and BigTIFF IFDs (also EXIF blocks in other formats): size, depth, photometric, resolution, EXIF and GPS. Offsets
/// are relative to the source's start (a TIFF file, or a slice holding just an EXIF block) and must lie inside it; at
/// most 1,000 entries per IFD, visited IFD offsets, and only IFD0 with its EXIF and GPS IFDs are read (later IFDs hold
/// thumbnails). At most 1 MB is read in all: IFDs and every payload (Photoshop resources, XMP, IPTC, ICC) come out of one
/// allowance, and a payload that doesn't fit what is left is skipped (the picture is partial). Text tags: Explorer's XP
/// tags (Title, Comments, Authors, Subject, Tags) come first; ImageDescription (270), which Windows also fills with the
/// title, is the title when there is none yet, is left out when it repeats the title, and is the comment otherwise; the
/// EXIF UserComment is the comment when none came before (as Windows shows it).
/// </summary>
internal static class Tiff
{
    const int MaxEntries = 1000;
    const long MaxRead = 1 << 20;

    /// <summary>One IFD entry: its tag, type, count and where its value lies.</summary>
    readonly record struct Entry(int Tag, int Type, long Count, long ValueAt);

    /// <summary>Reads a TIFF header and its IFDs into the sink, reading at most maxRead bytes (1 MB); false when the header isn't TIFF.</summary>
    public static bool Read(IByteSource s, ExtractSink sink, ExtractBudget budget, TiffMode mode, PictureParts parts, long maxRead = MaxRead)
    {
        Span<byte> h = stackalloc byte[16];
        int n = s.ReadFully(0, h);
        if (n < 8) return false;
        bool big;
        if (h[0] == 'I' && h[1] == 'I') big = false;
        else if (h[0] == 'M' && h[1] == 'M') big = true;
        else return false;
        var t = new Reader(s, big, budget, parts, Math.Min(maxRead, MaxRead));
        int magic = t.U16(h[2..]);
        long ifd0;
        if (magic == 42) ifd0 = t.U32(h[4..]);
        else if (magic == 43 && n >= 16 && t.U16(h[4..]) == 8) { t.BigTiff = true; ifd0 = (long)Math.Min(t.U64(h[8..]), long.MaxValue); }
        else return false;
        t.Run(ifd0, sink, mode);
        return true;
    }

    sealed class Reader(IByteSource s, bool bigEndian, ExtractBudget budget, PictureParts parts, long maxRead)
    {
        readonly HashSet<long> _visited = [];
        readonly long _start = s.BytesRead;
        public bool BigTiff;

        int EntrySize => BigTiff ? 20 : 12;
        int InlineSize => BigTiff ? 8 : 4;

        public int U16(ReadOnlySpan<byte> b) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
        public uint U32(ReadOnlySpan<byte> b) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
        public ulong U64(ReadOnlySpan<byte> b) => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);

        /// <summary>What is left of the allowance.</summary>
        long Left => maxRead - (s.BytesRead - _start);

        bool OverBudget => Left < 0;

        /// <summary>Whether a payload fits what is left; one that doesn't is skipped and the picture is partial.</summary>
        bool Fits(long bytes)
        {
            if (bytes <= Left) return true;
            parts.Partial = true;
            return false;
        }

        public void Run(long ifd0, ExtractSink sink, TiffMode mode)
        {
            var entries = Ifd(ifd0);
            if (entries == null) { parts.Partial = true; return; }
            var p = sink.Picture;
            // A Photoshop resource block's resolution outranks the TIFF tags; its blocks come out of what is left.
            if (Find(entries, 34377) is { } irb && Left > 0)
            {
                long allowance = Left;
                parts.Unit(() => Photoshop8Bim.Read(new ByteSourceSlice(s, irb.ValueAt, Size(irb)), sink, budget, parts, allowance));
            }
            if (mode == TiffMode.Image)
            {
                p.Width = (int)Math.Min(UInt(entries, 256) ?? 0, int.MaxValue);
                p.Height = (int)Math.Min(UInt(entries, 257) ?? 0, int.MaxValue);
                int samples = (int)Math.Min(UInt(entries, 277) ?? 1, 64);
                int bits = 0;
                if (Find(entries, 258) is { } bps)
                {
                    long count = Math.Min(bps.Count, 64);
                    for (int i = 0; i < count; i++) bits += (int)Math.Min(Value(bps, i) ?? 0, 64);
                    if (count == 1 && samples > 1) bits *= samples;
                }
                else bits = samples; // baseline default: 1 bit per sample
                p.BitDepth = (byte)Math.Clamp(bits, 0, 255);
                int photometric = (int)(UInt(entries, 262) ?? 99);
                long inkSet = UInt(entries, 332) ?? 1;
                p.ColorMode = photometric switch
                {
                    0 or 1 => bits == 1 ? ColorMode.Bitmap : ColorMode.Gray,
                    2 or 6 => ColorMode.Rgb,
                    3 => ColorMode.Indexed,
                    5 => inkSet == 1 ? ColorMode.Cmyk : ColorMode.Multichannel,
                    8 or 9 or 10 => ColorMode.Lab,
                    _ => ColorMode.Unknown,
                };
            }
            Resolution(entries, p);
            if (UInt(entries, 274) is { } orientation) p.Orientation = (byte)Math.Min(orientation, 255);
            if (UInt(entries, 18246) is { } rating && rating is >= 1 and <= 5) p.Rating = (byte)rating;
            // IPTC before the text tags, as a JPEG's APP13 is: its title decides what ImageDescription is.
            if (Find(entries, 33723) is { } iptc && Bytes(iptc, 1 << 20) is { } iptcBytes) parts.Unit(() => Iptc.Read(iptcBytes, sink));
            // The XP tags Explorer writes (UTF-16 in BYTE arrays) before the ASCII tags: a comment typed in Explorer is
            // only there, while Windows writes the title into ImageDescription too.
            XpText(entries, 0x9C9B, sink, DocField.Title);
            XpText(entries, 0x9C9C, sink, DocField.Comment);
            XpText(entries, 0x9C9D, sink, DocField.Author);
            XpText(entries, 0x9C9F, sink, DocField.Subject);
            if (Find(entries, 0x9C9E) is { } xpKeywords && Bytes(xpKeywords, 64 << 10) is { } kw)
                foreach (var k in Utf16(kw).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sink.Field(DocField.Keywords, k);
            Ascii(entries, 271, sink, DocField.CameraMake);
            Ascii(entries, 272, sink, DocField.CameraModel);
            Description(entries, sink);
            Ascii(entries, 315, sink, DocField.Author);
            Ascii(entries, 305, sink, DocField.CreatorTool);

            if (Offset(entries, 34665) is { } exif) parts.Unit(() => Exif(exif, sink));
            if (Offset(entries, 34853) is { } gps) parts.Unit(() => Gps(gps, sink));
            if (Find(entries, 700) is { } xmp && Bytes(xmp, 1 << 20) is { } xmpBytes && !XmlShape.ReadXmp(xmpBytes, sink)) parts.Partial = true;
            if (Find(entries, 34675) is { } icc && Bytes(icc, 1 << 20) is { } iccBytes && PictureValues.IccDescription(iccBytes) is { Length: > 0 } profile)
                sink.Field(DocField.ColorProfile, profile);
        }

        /// <summary>
        /// ImageDescription (270): Windows writes the title there as well as in XPTitle and XMP, cameras write their name
        /// ("OLYMPUS DIGITAL CAMERA"), other tools a caption. The title when there is none yet; left out when it repeats
        /// the title; else the comment (when no comment came first, from Explorer's XPComment or IPTC).
        /// </summary>
        void Description(List<Entry> entries, ExtractSink sink)
        {
            if (AsciiValue(entries, 270, ExtractSink.MaxFieldChars) is not { Length: > 0 } description) return;
            string? title = sink.Value(DocField.Title);
            if (title == null) sink.Field(DocField.Title, description);
            else if (!string.Equals(title.Trim(), description.Trim(), StringComparison.OrdinalIgnoreCase)) sink.Field(DocField.Comment, description);
        }

        void Exif(long offset, ExtractSink sink)
        {
            var entries = Ifd(offset);
            if (entries == null) { parts.Partial = true; return; }
            var p = sink.Picture;
            string? date = AsciiValue(entries, 36867, 64), offsetTime = AsciiValue(entries, 36881, 16);
            if (PictureValues.ExifDate(date, offsetTime) is long taken and > 0) p.DateTakenUtc = taken;
            if (UInt(entries, 0xA001) is { } cs) p.ExifColorSpace = cs == 1 ? (byte)1 : cs == 0xFFFF ? (byte)2 : (byte)0;
            if (UInt(entries, 0x8827) is { } iso) p.Iso = (int)Math.Min(iso, int.MaxValue);
            if (Rational(entries, 0x829D) is { } f) p.FNumber = (float)f;
            if (Rational(entries, 0x829A) is { } exposure) p.ExposureSeconds = (float)exposure;
            if (Rational(entries, 0x920A) is { } focal) p.FocalLengthMm = (float)focal;
            p.Width = (int)Math.Min(UInt(entries, 0xA002) ?? 0, int.MaxValue);
            p.Height = (int)Math.Min(UInt(entries, 0xA003) ?? 0, int.MaxValue);
            if (UserComment(entries) is { } comment) sink.Field(DocField.Comment, comment);
        }

        /// <summary>
        /// EXIF UserComment (0x9286), which Windows shows as the comment (after XPComment): an 8-byte character code, then
        /// the text. "ASCII" and an undefined code (8 zero bytes) are 8-bit text (UTF-8 when valid, else ANSI), "UNICODE"
        /// is UTF-16 in the TIFF's byte order; JIS (raw JIS X 0208) is left out, and so is text with control characters
        /// (binary maker data some cameras put there). Blank comments, which most cameras write, are none.
        /// </summary>
        string? UserComment(List<Entry> entries)
        {
            if (Find(entries, 0x9286) is not { } e || e.Type is not (1 or 2 or 7) || e.Count <= 8) return null;
            long size = Math.Min(e.Count, 8 + ExtractSink.MaxFieldChars * 2L);
            if (!Fits(size) || s.ReadBytes(e.ValueAt, size, size) is not { } b) return null;
            var code = b.AsSpan(0, 8);
            var text = b.AsSpan(8);
            string value;
            if (code.SequenceEqual("UNICODE\0"u8))
                value = (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(text[..(text.Length & ~1)]).Split('\0')[0];
            else if (code.SequenceEqual("ASCII\0\0\0"u8) || code.IndexOfAnyExcept((byte)0) < 0) value = PictureValues.Text(text);
            else return null;
            value = value.Trim();
            foreach (char c in value) if (char.IsControl(c) && c is not ('\t' or '\r' or '\n')) return null;
            return value.Length > 0 ? value : null;
        }

        void Gps(long offset, ExtractSink sink)
        {
            var entries = Ifd(offset);
            if (entries == null) { parts.Partial = true; return; }
            string? latRef = AsciiValue(entries, 1, 4), lonRef = AsciiValue(entries, 3, 4);
            if (latRef is not ("N" or "S") || lonRef is not ("E" or "W")) return; // a location needs its references
            if (Degrees(entries, 2) is not { } lat || Degrees(entries, 4) is not { } lon) return;
            sink.Picture.SetGps(latRef == "S" ? -lat : lat, lonRef == "W" ? -lon : lon);
        }

        /// <summary>Degrees, minutes, seconds as three rationals; null when any denominator is 0.</summary>
        double? Degrees(List<Entry> entries, int tag)
        {
            if (Find(entries, tag) is not { } e || e.Count < 1) return null;
            double value = 0, scale = 1;
            for (int i = 0; i < Math.Min(e.Count, 3); i++)
            {
                if (RationalAt(e, i) is not { } part || part < 0) return null;
                value += part / scale;
                scale *= 60;
            }
            return value;
        }

        void Resolution(List<Entry> entries, PictureBuilder p)
        {
            int unit = (int)(UInt(entries, 296) ?? 2);
            if (Rational(entries, 282) is { } x) p.DpiX = PictureValues.Dpi(x, unit);
            if (Rational(entries, 283) is { } y) p.DpiY = PictureValues.Dpi(y, unit);
        }

        /// <summary>The entries of the IFD at an offset; null when the offset is outside, already read, or the IFD doesn't fit.</summary>
        List<Entry>? Ifd(long offset)
        {
            budget.Check();
            if (offset < 8 || offset >= s.Length || !_visited.Add(offset) || OverBudget) return null;
            Span<byte> head = stackalloc byte[8];
            int countSize = BigTiff ? 8 : 2;
            if (!s.TryRead(offset, head[..countSize])) return null;
            long count = BigTiff ? (long)Math.Min(U64(head), int.MaxValue) : U16(head);
            if (count == 0 || count > MaxEntries) return null;
            long tableAt = offset + countSize, tableSize = count * EntrySize;
            var table = s.ReadBytes(tableAt, tableSize, MaxEntries * 20);
            if (table == null) return null;
            var list = new List<Entry>((int)count);
            for (int i = 0; i < count; i++)
            {
                var e = table.AsSpan(i * EntrySize, EntrySize);
                int tag = U16(e), type = U16(e[2..]);
                long n = BigTiff ? (long)Math.Min(U64(e[4..]), long.MaxValue) : U32(e[4..]);
                int width = TypeSize(type);
                if (width == 0 || n <= 0 || n > s.Length) continue;
                long size = n * width;
                long valueAt = size <= InlineSize ? tableAt + i * EntrySize + (BigTiff ? 12 : 8)
                    : BigTiff ? (long)Math.Min(U64(e[12..]), long.MaxValue) : U32(e[8..]);
                if (valueAt < 0 || valueAt > s.Length - size) continue; // values must lie inside the block
                list.Add(new Entry(tag, type, n, valueAt));
            }
            return list;
        }

        static int TypeSize(int type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            5 or 10 or 12 or 16 or 17 or 18 => 8,
            _ => 0,
        };

        static Entry? Find(List<Entry> entries, int tag)
        {
            foreach (var e in entries) if (e.Tag == tag) return e;
            return null;
        }

        long Size(Entry e) => e.Count * TypeSize(e.Type);

        /// <summary>An integer value at an index (BYTE, SHORT, LONG, LONG8, IFD); null for other types.</summary>
        long? Value(Entry e, long index)
        {
            if (index >= e.Count) return null;
            int width = TypeSize(e.Type);
            Span<byte> b = stackalloc byte[8];
            if (!s.TryRead(e.ValueAt + index * width, b[..width])) return null;
            return e.Type switch
            {
                1 or 7 => b[0],
                3 => U16(b),
                8 => (short)U16(b),
                4 or 13 => U32(b),
                9 => (int)U32(b),
                16 or 18 => (long)Math.Min(U64(b), long.MaxValue),
                _ => null,
            };
        }

        long? UInt(List<Entry> entries, int tag) => Find(entries, tag) is { } e ? Value(e, 0) is long v && v >= 0 ? v : null : null;

        long? Offset(List<Entry> entries, int tag) => UInt(entries, tag) is long v && v > 0 ? v : null;

        double? Rational(List<Entry> entries, int tag) => Find(entries, tag) is { } e ? RationalAt(e, 0) : null;

        /// <summary>A RATIONAL/SRATIONAL (or a plain number) at an index; null when the denominator is 0.</summary>
        double? RationalAt(Entry e, long index)
        {
            if (e.Type is not (5 or 10)) return Value(e, index) is long v ? v : null;
            Span<byte> b = stackalloc byte[8];
            if (index >= e.Count || !s.TryRead(e.ValueAt + index * 8, b)) return null;
            double num = e.Type == 5 ? U32(b) : (int)U32(b), den = e.Type == 5 ? U32(b[4..]) : (int)U32(b[4..]);
            if (den == 0) return null;
            double r = num / den;
            return double.IsFinite(r) ? r : null;
        }

        /// <summary>A payload of at most max bytes, when it fits what is left of the allowance (else the picture is partial).</summary>
        byte[]? Bytes(Entry e, long max)
        {
            long size = Size(e);
            return size <= max && Fits(size) ? s.ReadBytes(e.ValueAt, size, max) : null;
        }

        string? AsciiValue(List<Entry> entries, int tag, int max)
        {
            if (Find(entries, tag) is not { } e || e.Type is not (1 or 2 or 7)) return null;
            long size = Math.Min(e.Count, max);
            var b = Fits(size) ? s.ReadBytes(e.ValueAt, size, max) : null;
            return b == null ? null : PictureValues.Text(b);
        }

        void Ascii(List<Entry> entries, int tag, ExtractSink sink, DocField field)
        {
            if (AsciiValue(entries, tag, ExtractSink.MaxFieldChars) is { Length: > 0 } v) sink.Field(field, v);
        }

        void XpText(List<Entry> entries, int tag, ExtractSink sink, DocField field)
        {
            if (Find(entries, tag) is { } e && Bytes(e, 64 << 10) is { } b && Utf16(b) is { Length: > 0 } v) sink.Field(field, v);
        }

        static string Utf16(byte[] b) => Encoding.Unicode.GetString(b, 0, b.Length & ~1).TrimEnd('\0').Trim();
    }
}
