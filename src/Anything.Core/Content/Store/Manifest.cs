using System.Buffers.Binary;

namespace Anything.Core.Content.Store;

/// <summary>
/// The commit point: segments, deletions, volume table, per-kind statistics and the recorded mode (§6):
/// <code>
/// "RPCMAN01" u16 format u16 tokenizerVersion u64 commitGeneration i64 createdUtc u32 nextSegmentId u8 mode
/// u16 volumeCount × { u16 index, str key, str displayRoot, i64 lastSeenUtc (ticks) }
/// u32 segmentCount × { u32 id, u32 docCount, u64 bytes, u8 tier, u8 flags (1 = damaged), u32 deletedCount,
///                      varint-delta deleted documents (first absolute), u8 kindCount × { u8 kind, 8 × varint } (deleted stats) }
/// u8 kindCount × { u8 kind, 8 × u64 }                              (live totals: estimator, status, Settings)
/// u32 crc32c (of everything before it), "RPCEND01"
/// </code>
/// Entries are immutable once a manifest is published: a commit copies the entries it changes.
/// </summary>
internal sealed class Manifest
{
    public static ReadOnlySpan<byte> Magic => "RPCMAN01"u8;
    public const ushort CurrentFormat = 1;
    const byte FlagDamaged = 1;

    /// <summary>
    /// What reading a manifest gave: Ok; Bad (it read but failed its checks); Newer (a later build wrote it: never written
    /// over). Parse gives only those; the store's file read adds Missing (no file) and Unavailable (a file it couldn't read
    /// now: in use without read sharing, access denied, an I/O error), which is never taken for Bad.
    /// </summary>
    public enum ParseResult : byte { Ok, Bad, Newer, Missing, Unavailable }

    public ushort TokenizerVersion { get; init; } = ContentTokenizer.Version;
    public long Generation { get; init; }
    public long CreatedUtc { get; init; }
    public uint NextSegmentId { get; init; } = 1;
    public ContentMode Mode { get; init; }
    public IReadOnlyList<VolumeEntry> Volumes { get; init; } = [];
    public IReadOnlyList<SegmentEntry> Segments { get; init; } = [];
    public IReadOnlyDictionary<ContentKind, KindStats> Stats { get; init; } = new Dictionary<ContentKind, KindStats>();

    /// <summary>One segment as the manifest lists it.</summary>
    internal sealed class SegmentEntry
    {
        public required uint Id { get; init; }
        public required int DocCount { get; init; }
        public long Bytes { get; init; }
        public byte Tier { get; init; }
        public bool Damaged { get; init; }
        /// <summary>Deleted documents as a bitmap (null = none); never changed once published.</summary>
        public ulong[]? Deleted { get; init; }
        public int DeletedCount { get; init; }
        /// <summary>What the deleted documents contributed to the segment's statistics.</summary>
        public IReadOnlyDictionary<ContentKind, KindStats> DeletedStats { get; init; } = new Dictionary<ContentKind, KindStats>();

        public bool IsDeleted(int doc) => Deleted != null && (uint)doc < (uint)DocCount && (Deleted[doc >> 6] & (1UL << doc)) != 0;
    }

    /// <summary>The manifest's bytes.</summary>
    public byte[] Serialize()
    {
        using var b = new ByteBuffer(4096);
        b.Write(Magic);
        b.WriteUInt16(CurrentFormat);
        b.WriteUInt16(TokenizerVersion);
        b.WriteInt64(Generation);
        b.WriteInt64(CreatedUtc);
        b.WriteUInt32(NextSegmentId);
        b.WriteByte((byte)Mode);
        b.WriteUInt16((ushort)Volumes.Count);
        foreach (var v in Volumes)
        {
            b.WriteUInt16(v.Index);
            b.WriteString(v.Key);
            b.WriteString(v.DisplayRoot);
            b.WriteInt64(v.LastSeenUtc.Ticks);
        }
        b.WriteUInt32((uint)Segments.Count);
        foreach (var s in Segments)
        {
            b.WriteUInt32(s.Id);
            b.WriteUInt32((uint)s.DocCount);
            b.WriteInt64(s.Bytes);
            b.WriteByte(s.Tier);
            b.WriteByte(s.Damaged ? FlagDamaged : (byte)0);
            b.WriteUInt32((uint)s.DeletedCount);
            int prev = -1, written = 0;
            if (s.Deleted != null)
                for (int w = 0; w < s.Deleted.Length; w++)
                    for (ulong bits = s.Deleted[w]; bits != 0; bits &= bits - 1)
                    {
                        int doc = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        if (doc >= s.DocCount) break;
                        b.WriteVarInt((ulong)(prev < 0 ? doc : doc - prev));
                        prev = doc;
                        written++;
                    }
            if (written != s.DeletedCount) throw new InvalidOperationException("A segment's deleted count doesn't match its bitmap.");
            WriteStats(b, s.DeletedStats, varint: true);
        }
        WriteStats(b, Stats, varint: false);
        uint crc = Crc32C.Compute(b.Span);
        b.WriteUInt32(crc);
        b.Write(SegmentFormat.EndMagic);
        return b.Span.ToArray();
    }

    static void WriteStats(ByteBuffer b, IReadOnlyDictionary<ContentKind, KindStats> stats, bool varint)
    {
        var kinds = stats.Keys.Order().ToList();
        b.WriteByte((byte)kinds.Count);
        foreach (var kind in kinds)
        {
            b.WriteByte((byte)kind);
            for (int i = 0; i < 8; i++)
            {
                long v = Math.Max(0, KindStatsMath.Get(stats[kind], i));
                if (varint) b.WriteVarInt((ulong)v); else b.WriteInt64(v);
            }
        }
    }

    /// <summary>Reads a manifest; Newer when a later build wrote it (never written over), Bad when it fails its checks.</summary>
    public static ParseResult Parse(ReadOnlySpan<byte> data, out Manifest? manifest)
    {
        manifest = null;
        if (data.Length < 10 || !data[..8].SequenceEqual(Magic)) return ParseResult.Bad;
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (format > CurrentFormat) return ParseResult.Newer;
        if (format != CurrentFormat || data.Length < 12 + 8 + 8 + 4 + 1 + 2 + 4 + 1 + 12) return ParseResult.Bad;
        if (!data[^8..].SequenceEqual(SegmentFormat.EndMagic)) return ParseResult.Bad;
        var body = data[..^12];
        if (Crc32C.Compute(body) != BinaryPrimitives.ReadUInt32LittleEndian(data[^12..])) return ParseResult.Bad;
        try
        {
            manifest = Read(body);
            return manifest == null ? ParseResult.Bad : ParseResult.Ok;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return ParseResult.Bad;
        }
    }

    static Manifest? Read(ReadOnlySpan<byte> s)
    {
        int p = 10;
        ushort tokenizer = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
        long generation = BinaryPrimitives.ReadInt64LittleEndian(s[(p + 2)..]);
        long created = BinaryPrimitives.ReadInt64LittleEndian(s[(p + 10)..]);
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(s[(p + 18)..]);
        byte mode = s[p + 22];
        p += 23;
        if (generation < 0 || mode > (byte)ContentMode.DocumentsAndPictures) return null;

        int volumeCount = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
        p += 2;
        var volumes = new List<VolumeEntry>(volumeCount);
        var indexes = new HashSet<ushort>();
        for (int i = 0; i < volumeCount; i++)
        {
            if (s.Length - p < 2) return null;
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
            p += 2;
            if (!VarInt.TryReadString(s, ref p, 4096, out string key) || !VarInt.TryReadString(s, ref p, 32 << 10, out string root)) return null;
            if (s.Length - p < 8 || !indexes.Add(index)) return null;
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
            p += 8;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
            volumes.Add(new VolumeEntry(index, key, root, new DateTime(ticks, DateTimeKind.Utc)));
        }

        if (s.Length - p < 4) return null;
        uint segmentCount = BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
        p += 4;
        if (segmentCount > (uint)(s.Length - p) / 23) return null;
        var segments = new List<SegmentEntry>((int)segmentCount);
        var ids = new HashSet<uint>();
        for (int i = 0; i < segmentCount; i++)
        {
            if (s.Length - p < 22) return null;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
            int docCount = BinaryPrimitives.ReadInt32LittleEndian(s[(p + 4)..]);
            long bytes = BinaryPrimitives.ReadInt64LittleEndian(s[(p + 8)..]);
            byte tier = s[p + 16];
            byte flags = s[p + 17];
            int deletedCount = BinaryPrimitives.ReadInt32LittleEndian(s[(p + 18)..]);
            p += 22;
            if (id == 0 || id >= next || !ids.Add(id) || docCount < 0 || bytes < 0 || deletedCount < 0 || deletedCount > docCount) return null;
            ulong[]? deleted = null;
            if (deletedCount > 0)
            {
                deleted = new ulong[(docCount + 63) >> 6];
                int doc = -1;
                for (int k = 0; k < deletedCount; k++)
                {
                    if (!VarInt.TryRead(s, ref p, docCount, out int v) || (k > 0 && v == 0)) return null;
                    doc = k == 0 ? v : doc + v;
                    if (doc >= docCount) return null;
                    deleted[doc >> 6] |= 1UL << doc;
                }
            }
            if (!ReadStats(s, ref p, varint: true, out var deletedStats)) return null;
            segments.Add(new SegmentEntry
            {
                Id = id, DocCount = docCount, Bytes = bytes, Tier = tier, Damaged = (flags & FlagDamaged) != 0,
                Deleted = deleted, DeletedCount = deletedCount, DeletedStats = deletedStats,
            });
        }
        if (!ReadStats(s, ref p, varint: false, out var stats) || p != s.Length) return null;
        return new Manifest
        {
            TokenizerVersion = tokenizer, Generation = generation, CreatedUtc = created, NextSegmentId = next, Mode = (ContentMode)mode,
            Volumes = volumes, Segments = segments, Stats = stats,
        };
    }

    static bool ReadStats(ReadOnlySpan<byte> s, ref int p, bool varint, out IReadOnlyDictionary<ContentKind, KindStats> stats)
    {
        var result = new Dictionary<ContentKind, KindStats>();
        stats = result;
        if (p >= s.Length) return false;
        int count = s[p++];
        Span<long> v = stackalloc long[8];
        for (int i = 0; i < count; i++)
        {
            if (p >= s.Length) return false;
            var kind = (ContentKind)s[p++];
            for (int k = 0; k < 8; k++)
            {
                if (varint)
                {
                    if (!VarInt.TryRead(s, ref p, long.MaxValue, out long value)) return false;
                    v[k] = value;
                }
                else
                {
                    if (s.Length - p < 8) return false;
                    v[k] = BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
                    p += 8;
                    if (v[k] < 0) return false;
                }
            }
            if (!result.TryAdd(kind, KindStatsMath.From(v))) return false;
        }
        return true;
    }
}
