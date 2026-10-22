using System.Buffers.Binary;
using System.Globalization;

namespace Anything.Core.Content.Store;

/// <summary>
/// Segment file layout (§6), little-endian, varints LEB128:
/// <code>
/// HEADER   64 B: "RPCSEG01" u16 format u16 tokenizerVersion u32 docCount u32 termCount u32 dictBlocks u32 metaCount
///          i64 createdUtc (FILETIME) u64 tocOffset, reserved
/// TEXT     per document: Brotli(q5, lgwin 22) of its stored-text blob, or the blob itself (TextLength == RawLength)
/// PATHS    blocks of 64 documents: Brotli(q5) of (varint length + UTF-8 volume-relative path)…
/// PATHIDX  per block: u64 offset in PATHS, u32 stored length, u32 raw length
/// POSTINGS per term with docFreq ≥ 3: [docFreq > 256: varint skipCount = (docFreq-1)/128, then per skip
///          (varint docDelta, varint byteDelta)] then the documents as delta varints (first absolute)
/// DICT     blocks of 64 terms (folded UTF-8, byte order), front-coded: varint shared, varint suffixLength, suffix,
///          varint docFreq, then docFreq ≤ 2 ? varint doc [, varint second - first] : varint postingsOffsetDelta
/// DICTIDX  per block: u64 offset in DICT, u64 postings base, u32 crc32c of the block, u32 crc32c of the block's run of
///          POSTINGS (from its base to the next block's, or the end), u16 first term length, first term
/// DOCS     docCount × 48 B rows, local document order = ascending key
/// META     u32 count; count × 48 B: u32 local document + 44 B picture record, ascending
/// STATS    u8 count × (u8 kind, 8 × i64)
/// VOLS     u16 count × (u16 index, str key, str display root): the volume table the rows' Volume refers to
/// TOC      per section: u8 id, u64 offset, u64 length, u32 crc32c
/// FOOTER   u32 tocLength, u32 crc32c(TOC), "RPCEND01"
/// </code>
/// A skip entry k (1-based) holds the change, since entry k-1, of the last document before block k (documents 128k-1)
/// and of the byte position where block k starts in the document stream; entry 0 is (0, 0). Postings offsets in DICT
/// count from the block's postings base (the first) or the previous term with postings.
/// </summary>
internal static class SegmentFormat
{
    public static ReadOnlySpan<byte> Magic => "RPCSEG01"u8;
    public static ReadOnlySpan<byte> EndMagic => "RPCEND01"u8;
    public const ushort Format = 1;

    public const int HeaderSize = 64, RowSize = 48, MetaSize = 48, PictureSize = 44, TocEntrySize = 21, FooterSize = 16;
    public const int PathIndexEntrySize = 16;
    /// <summary>A DICTIDX entry before its first term: offset, postings base, block CRC, postings CRC, term length.</summary>
    public const int DictIndexFixedSize = 26;
    /// <summary>Largest stored-text blob (UTF-8 bytes before compression).</summary>
    public const int MaxBlob = 8 << 20;
    /// <summary>Blobs shorter than this are stored as they are.</summary>
    public const int RawUnder = 128;
    public const int PathBlock = 64, DictBlock = 64, SkipEvery = 128, SkipAbove = 256;
    /// <summary>Longest term in bytes (64 folded characters of up to 3 UTF-8 bytes plus a field prefix fit easily).</summary>
    public const int MaxTermBytes = 1024;
    /// <summary>Largest decoded block of 64 paths.</summary>
    public const int MaxPathBlock = 8 << 20;
    /// <summary>Longest path in bytes.</summary>
    public const int MaxPathBytes = 128 << 10;
    public const int BrotliQuality = 5, BrotliWindow = 22;
    public const int MaxSections = 32;

    /// <summary>Section ids in the TOC.</summary>
    public enum Section : byte { Header = 0, Text = 1, Paths = 2, PathIndex = 3, Postings = 4, Dict = 5, DictIndex = 6, Docs = 7, Meta = 8, Stats = 9, Volumes = 10 }

    public const int SectionCount = 11;

    /// <summary>The file name of a segment ("seg-0000002A.rpc").</summary>
    public static string FileName(uint id) => "seg-" + id.ToString("X8", CultureInfo.InvariantCulture) + ".rpc";

    /// <summary>The id in a segment file name; false for other names.</summary>
    public static bool TryParseFileName(ReadOnlySpan<char> name, out uint id)
    {
        id = 0;
        return name.Length == 16 && name.StartsWith("seg-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".rpc", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(name[4..12], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>A problem status: not Ok, Truncated or NoText, and not left out by rule (cloud, opted out, EFS).</summary>
    public static bool IsProblem(DocStatus s)
        => s is not (DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText or DocStatus.CloudOnly or DocStatus.OptedOut or DocStatus.EfsEncrypted);

    /// <summary>The segment header.</summary>
    public readonly record struct Header(ushort Format, ushort TokenizerVersion, int DocCount, int TermCount, int DictBlocks,
        int MetaCount, long CreatedUtc, long TocOffset)
    {
        public void Write(Span<byte> d)
        {
            d[..HeaderSize].Clear();
            Magic.CopyTo(d);
            BinaryPrimitives.WriteUInt16LittleEndian(d[8..], Format);
            BinaryPrimitives.WriteUInt16LittleEndian(d[10..], TokenizerVersion);
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], DocCount);
            BinaryPrimitives.WriteInt32LittleEndian(d[16..], TermCount);
            BinaryPrimitives.WriteInt32LittleEndian(d[20..], DictBlocks);
            BinaryPrimitives.WriteInt32LittleEndian(d[24..], MetaCount);
            BinaryPrimitives.WriteInt64LittleEndian(d[28..], CreatedUtc);
            BinaryPrimitives.WriteInt64LittleEndian(d[36..], TocOffset);
        }

        public static bool TryRead(ReadOnlySpan<byte> s, out Header h)
        {
            h = default;
            if (s.Length < HeaderSize || !s[..8].SequenceEqual(Magic)) return false;
            h = new Header(BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[12..]), BinaryPrimitives.ReadInt32LittleEndian(s[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[20..]), BinaryPrimitives.ReadInt32LittleEndian(s[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(s[28..]), BinaryPrimitives.ReadInt64LittleEndian(s[36..]));
            return h.DocCount >= 0 && h.TermCount >= 0 && h.DictBlocks >= 0 && h.MetaCount >= 0 && h.TocOffset >= HeaderSize;
        }
    }

    /// <summary>One TOC entry.</summary>
    public readonly record struct TocEntry(Section Id, long Offset, long Length, uint Crc)
    {
        public void Write(Span<byte> d)
        {
            d[0] = (byte)Id;
            BinaryPrimitives.WriteInt64LittleEndian(d[1..], Offset);
            BinaryPrimitives.WriteInt64LittleEndian(d[9..], Length);
            BinaryPrimitives.WriteUInt32LittleEndian(d[17..], Crc);
        }

        public static TocEntry Read(ReadOnlySpan<byte> s)
            => new((Section)s[0], BinaryPrimitives.ReadInt64LittleEndian(s[1..]), BinaryPrimitives.ReadInt64LittleEndian(s[9..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[17..]));
    }

    /// <summary>Writes a 48-byte document row.</summary>
    public static void WriteRow(Span<byte> d, in DocRow row, long textOffset, int textLength, int rawLength)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(d, row.Key);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], row.Modified);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], row.Size);
        BinaryPrimitives.WriteInt64LittleEndian(d[24..], textOffset);
        BinaryPrimitives.WriteInt32LittleEndian(d[32..], textLength);
        BinaryPrimitives.WriteInt32LittleEndian(d[36..], rawLength);
        BinaryPrimitives.WriteUInt16LittleEndian(d[40..], row.Volume);
        BinaryPrimitives.WriteUInt16LittleEndian(d[42..], row.Pages);
        d[44] = (byte)row.Kind;
        d[45] = (byte)row.Status;
        d[46] = row.ExtractorVersion;
        d[47] = (byte)row.Flags;
    }

    /// <summary>The key of a row.</summary>
    public static ulong RowKey(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt64LittleEndian(s);

    /// <summary>Reads a 48-byte document row.</summary>
    public static DocRow ReadRow(ReadOnlySpan<byte> s)
        => new(BinaryPrimitives.ReadUInt64LittleEndian(s), BinaryPrimitives.ReadInt64LittleEndian(s[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(s[16..]), BinaryPrimitives.ReadUInt16LittleEndian(s[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[42..]), (ContentKind)s[44], (DocStatus)s[45], s[46], (DocFlags)s[47]);

    /// <summary>Where a row's stored text is.</summary>
    public static (long Offset, int Length, int Raw) RowText(ReadOnlySpan<byte> s)
        => (BinaryPrimitives.ReadInt64LittleEndian(s[24..]), BinaryPrimitives.ReadInt32LittleEndian(s[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(s[36..]));

    const byte PicDims = 1, PicDpi = 2, PicDate = 4, PicGps = 8;

    /// <summary>
    /// Writes a 44-byte picture record: u32 width, u32 height, u16 dpiX, u16 dpiY, u8 bitDepth, u8 colorMode,
    /// u8 orientation, u8 exifColorSpace, u8 rating, u8 flags (dims, dpi, date, gps), i64 dateTakenUtc, i32 latitude,
    /// i32 longitude (micro-degrees), u16 iso, u16 fNumber×100, f32 exposureSeconds, u16 focalLength×10. Values are
    /// clamped to their fields.
    /// </summary>
    public static void WritePicture(Span<byte> d, in PictureInfo p)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)Math.Max(0, p.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)Math.Max(0, p.Height));
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], U16(p.DpiX));
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], U16(p.DpiY));
        d[12] = p.BitDepth;
        d[13] = (byte)p.ColorMode;
        d[14] = p.Orientation;
        d[15] = p.ExifColorSpace;
        d[16] = p.Rating;
        byte flags = 0;
        if (p.Width > 0 || p.Height > 0) flags |= PicDims;
        if (p.DpiX > 0 || p.DpiY > 0) flags |= PicDpi;
        if (p.DateTakenUtc != 0) flags |= PicDate;
        if (p.HasGps) flags |= PicGps;
        d[17] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(d[18..], p.DateTakenUtc);
        BinaryPrimitives.WriteInt32LittleEndian(d[26..], p.LatitudeMicro);
        BinaryPrimitives.WriteInt32LittleEndian(d[30..], p.LongitudeMicro);
        BinaryPrimitives.WriteUInt16LittleEndian(d[34..], U16(p.Iso));
        BinaryPrimitives.WriteUInt16LittleEndian(d[36..], Scaled(p.FNumber, 100));
        BinaryPrimitives.WriteSingleLittleEndian(d[38..], float.IsFinite(p.ExposureSeconds) ? p.ExposureSeconds : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(d[42..], Scaled(p.FocalLengthMm, 10));
    }

    /// <summary>Reads a 44-byte picture record.</summary>
    public static PictureInfo ReadPicture(ReadOnlySpan<byte> s)
    {
        float exposure = BinaryPrimitives.ReadSingleLittleEndian(s[38..]);
        return new PictureInfo(
            Width: (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(s), int.MaxValue),
            Height: (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), int.MaxValue),
            DpiX: BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), DpiY: BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
            BitDepth: s[12], ColorMode: (ColorMode)s[13], Orientation: s[14], ExifColorSpace: s[15], Rating: s[16],
            DateTakenUtc: BinaryPrimitives.ReadInt64LittleEndian(s[18..]), HasGps: (s[17] & PicGps) != 0,
            LatitudeMicro: BinaryPrimitives.ReadInt32LittleEndian(s[26..]), LongitudeMicro: BinaryPrimitives.ReadInt32LittleEndian(s[30..]),
            Iso: BinaryPrimitives.ReadUInt16LittleEndian(s[34..]), FNumber: BinaryPrimitives.ReadUInt16LittleEndian(s[36..]) / 100f,
            ExposureSeconds: float.IsFinite(exposure) ? exposure : 0, FocalLengthMm: BinaryPrimitives.ReadUInt16LittleEndian(s[42..]) / 10f);
    }

    static ushort U16(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);

    static ushort Scaled(float v, float scale)
        => float.IsFinite(v) && v > 0 ? (ushort)Math.Min(ushort.MaxValue, MathF.Round(v * scale)) : (ushort)0;

    /// <summary>The 44-byte record a picture round-trips through (what a reader gets back).</summary>
    public static PictureInfo Quantize(in PictureInfo p)
    {
        Span<byte> b = stackalloc byte[PictureSize];
        WritePicture(b, p);
        return ReadPicture(b);
    }
}

/// <summary>Sums of <see cref="KindStats"/> per kind (segments, deletions, views).</summary>
internal static class KindStatsMath
{
    public static KindStats Add(in KindStats a, in KindStats b)
        => new(a.Docs + b.Docs, a.WithText + b.WithText, a.RawTextBytes + b.RawTextBytes, a.StoredBytes + b.StoredBytes,
            a.Pairs + b.Pairs, a.BytesRead + b.BytesRead, a.MsTotal + b.MsTotal, a.Problems + b.Problems);

    /// <summary>a - b, never below zero.</summary>
    public static KindStats Subtract(in KindStats a, in KindStats b)
        => new(Math.Max(0, a.Docs - b.Docs), Math.Max(0, a.WithText - b.WithText), Math.Max(0, a.RawTextBytes - b.RawTextBytes),
            Math.Max(0, a.StoredBytes - b.StoredBytes), Math.Max(0, a.Pairs - b.Pairs), Math.Max(0, a.BytesRead - b.BytesRead),
            Math.Max(0, a.MsTotal - b.MsTotal), Math.Max(0, a.Problems - b.Problems));

    /// <summary>The values of a stats record in file order.</summary>
    public static long Get(in KindStats s, int i) => i switch
    {
        0 => s.Docs, 1 => s.WithText, 2 => s.RawTextBytes, 3 => s.StoredBytes, 4 => s.Pairs, 5 => s.BytesRead, 6 => s.MsTotal, _ => s.Problems,
    };

    public static KindStats From(ReadOnlySpan<long> v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);

    /// <summary>Kind slots (ContentKind values that fit a byte).</summary>
    public const int Kinds = 256;
}
