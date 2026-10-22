using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Store;

/// <summary>
/// One memory-mapped (or in-memory) segment. Open checks the footer, the TOC and the CRCs of the header, PATHIDX, DICTIDX,
/// DOCS, META, STATS and VOLS, and the structure of the rows, picture records and dictionary index. Each DICT block, and
/// the run of POSTINGS its terms point into, is checked against its own CRC in DICTIDX the first time it is read, so a
/// flipped bit there gives no documents rather than wrong ones; <see cref="Verify"/> (the idle scrub) checks every section.
/// A structural fault met while decoding (postings not ascending, a document at or above DocCount, a count that doesn't
/// match, an offset outside its section, text or paths that don't decode) marks the segment damaged and yields nothing
/// for that piece. Every member is safe from many threads; the mapping stays until the last reference is released.
/// </summary>
internal sealed class SegmentReader : IContentSegment, IDisposable
{
    /// <summary>Reads of a segment after its last reference went (a holder's bug; the stress tests assert none).</summary>
    internal static long AccessesAfterRelease;

    readonly SegmentBytes _bytes;
    readonly SegmentFormat.Header _header;
    readonly SegmentFormat.TocEntry[] _toc;
    readonly long _textOff, _textLen, _pathsOff, _pathsLen, _pathIdxOff, _postOff, _postLen, _dictOff, _dictLen, _docsOff, _metaOff;
    readonly int _pathBlocks;
    readonly long[] _blockOffset, _blockBase;
    readonly uint[] _blockCrc, _postCrc;
    readonly int[] _firstStart;
    readonly byte[] _firstTerms;
    readonly int[] _verified, _postVerified;
    readonly Dictionary<ContentKind, KindStats> _stats;
    readonly VolumeEntry[] _volumes;
    readonly ulong _footer;
    int _damaged, _refs = 1, _verifiedAll;

    SegmentReader(uint id, string? path, SegmentBytes bytes, SegmentFormat.Header header, SegmentFormat.TocEntry[] toc,
        long[] blockOffset, long[] blockBase, uint[] blockCrc, uint[] postCrc, int[] firstStart, byte[] firstTerms,
        Dictionary<ContentKind, KindStats> stats, VolumeEntry[] volumes)
    {
        Id = id;
        Path = path;
        _bytes = bytes;
        _header = header;
        _toc = toc;
        (_textOff, _textLen) = Of(SegmentFormat.Section.Text);
        (_pathsOff, _pathsLen) = Of(SegmentFormat.Section.Paths);
        _pathIdxOff = Of(SegmentFormat.Section.PathIndex).Offset;
        (_postOff, _postLen) = Of(SegmentFormat.Section.Postings);
        (_dictOff, _dictLen) = Of(SegmentFormat.Section.Dict);
        _docsOff = Of(SegmentFormat.Section.Docs).Offset;
        _metaOff = Of(SegmentFormat.Section.Meta).Offset + 4;
        _pathBlocks = (header.DocCount + SegmentFormat.PathBlock - 1) / SegmentFormat.PathBlock;
        _blockOffset = blockOffset;
        _blockBase = blockBase;
        _blockCrc = blockCrc;
        _postCrc = postCrc;
        _firstStart = firstStart;
        _firstTerms = firstTerms;
        _verified = new int[(blockOffset.Length + 31) / 32];
        _postVerified = new int[(blockOffset.Length + 31) / 32];
        _stats = stats;
        _volumes = volumes;
        _footer = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span(bytes.Length - SegmentFormat.FooterSize, 8));
    }

    (long Offset, long Length) Of(SegmentFormat.Section id) => (_toc[(int)id].Offset, _toc[(int)id].Length);

    /// <summary>Opens a segment; null when its footer, TOC or open-time sections fail their CRC, or it can't be opened.</summary>
    public static SegmentReader? Open(string path, uint id) => Open(path, id, out _);

    /// <summary>Why a segment file didn't open.</summary>
    internal enum OpenFailure : byte
    {
        /// <summary>It opened.</summary>
        None,
        /// <summary>The file isn't there (a reader may read the manifest again; a writer drops it).</summary>
        Missing,
        /// <summary>It couldn't be read now (in use without read sharing, access denied, an I/O error), after retries: not damage.</summary>
        Unavailable,
        /// <summary>It read, but failed its checks: damaged.</summary>
        Invalid,
    }

    /// <summary>Opens a segment file; failure says why not. A file that can't be read now is retried a few times first.</summary>
    internal static SegmentReader? Open(string path, uint id, out OpenFailure failure)
    {
        for (int attempt = 0; ; attempt++)
        {
            MappedBytes? bytes = null;
            try
            {
                bytes = MappedBytes.Open(path);
                var reader = bytes == null ? null : Create(bytes, id, path);
                if (reader == null) bytes?.Dispose();
                failure = reader == null ? OpenFailure.Invalid : OpenFailure.None;
                return reader;
            }
            catch (Exception e) when (StoreFiles.IsMissing(e))
            {
                bytes?.Dispose();
                failure = OpenFailure.Missing;
                return null;
            }
            catch (Exception e) when (StoreFiles.IsUnavailable(e))
            {
                bytes?.Dispose();
                if (attempt >= StoreFiles.Retries)
                {
                    failure = OpenFailure.Unavailable;
                    return null;
                }
                Thread.Sleep(StoreFiles.RetryWait); // a scanner or a backup may hold it for a moment
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException)
            {
                bytes?.Dispose();
                failure = OpenFailure.Invalid;
                return null;
            }
        }
    }

    /// <summary>Opens a segment held in memory.</summary>
    public static SegmentReader? Open(ReadOnlyMemory<byte> bytes, uint id) => Create(new MemoryBytes(bytes), id, null);

    static SegmentReader? Create(SegmentBytes b, uint id, string? path)
    {
        try
        {
            return Parse(b, id, path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // anything unexpected in bad data: not a usable segment
        }
    }

    static SegmentReader? Parse(SegmentBytes b, uint id, string? path)
    {
        long len = b.Length;
        if (len < SegmentFormat.HeaderSize + SegmentFormat.FooterSize) return null;
        var footer = b.Span(len - SegmentFormat.FooterSize, SegmentFormat.FooterSize);
        if (!footer[8..].SequenceEqual(SegmentFormat.EndMagic)) return null;
        int tocLength = BinaryPrimitives.ReadInt32LittleEndian(footer);
        uint tocCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        if (tocLength <= 0 || tocLength % SegmentFormat.TocEntrySize != 0 || tocLength > SegmentFormat.MaxSections * SegmentFormat.TocEntrySize) return null;
        long tocOffset = len - SegmentFormat.FooterSize - tocLength;
        if (tocOffset < SegmentFormat.HeaderSize) return null;
        var tocBytes = b.Span(tocOffset, tocLength);
        if (Crc32C.Compute(tocBytes) != tocCrc) return null;

        var toc = new SegmentFormat.TocEntry[SegmentFormat.SectionCount];
        int seen = 0;
        for (int i = 0; i < tocLength / SegmentFormat.TocEntrySize; i++)
        {
            var e = SegmentFormat.TocEntry.Read(tocBytes[(i * SegmentFormat.TocEntrySize)..]);
            if ((int)e.Id >= SegmentFormat.SectionCount) continue; // a section this build doesn't know
            if ((seen & (1 << (int)e.Id)) != 0 || e.Offset < 0 || e.Length < 0 || e.Offset > tocOffset || e.Length > tocOffset - e.Offset) return null;
            seen |= 1 << (int)e.Id;
            toc[(int)e.Id] = e;
        }
        if (seen != (1 << SegmentFormat.SectionCount) - 1) return null;
        var headerEntry = toc[(int)SegmentFormat.Section.Header];
        if (headerEntry.Offset != 0 || headerEntry.Length != SegmentFormat.HeaderSize) return null;
        var headerBytes = b.Span(0, SegmentFormat.HeaderSize);
        if (Crc32C.Compute(headerBytes) != headerEntry.Crc) return null;
        if (!SegmentFormat.Header.TryRead(headerBytes, out var h) || h.Format != SegmentFormat.Format || h.TocOffset != tocOffset) return null;
        for (int s = 1; s < SegmentFormat.SectionCount; s++)
            if (toc[s].Offset < SegmentFormat.HeaderSize) return null;

        foreach (var s in (ReadOnlySpan<SegmentFormat.Section>)[SegmentFormat.Section.PathIndex, SegmentFormat.Section.DictIndex,
            SegmentFormat.Section.Docs, SegmentFormat.Section.Meta, SegmentFormat.Section.Stats, SegmentFormat.Section.Volumes])
            if (!CrcMatches(b, toc[(int)s])) return null;

        int docs = h.DocCount;
        var docsEntry = toc[(int)SegmentFormat.Section.Docs];
        if (docsEntry.Length != (long)docs * SegmentFormat.RowSize) return null;
        var metaEntry = toc[(int)SegmentFormat.Section.Meta];
        if (metaEntry.Length != 4 + (long)h.MetaCount * SegmentFormat.MetaSize) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(b.Span(metaEntry.Offset, 4)) != h.MetaCount) return null;
        int pathBlocks = (docs + SegmentFormat.PathBlock - 1) / SegmentFormat.PathBlock;
        var pathIdx = toc[(int)SegmentFormat.Section.PathIndex];
        if (pathIdx.Length != (long)pathBlocks * SegmentFormat.PathIndexEntrySize) return null;

        // Rows: keys ascending, text inside TEXT, lengths consistent.
        long textLen = toc[(int)SegmentFormat.Section.Text].Length;
        ulong prevKey = 0;
        for (int d = 0; d < docs; d++)
        {
            var row = b.Span(docsEntry.Offset + (long)d * SegmentFormat.RowSize, SegmentFormat.RowSize);
            ulong key = SegmentFormat.RowKey(row);
            if (d > 0 && key <= prevKey) return null;
            prevKey = key;
            var (off, tl, raw) = SegmentFormat.RowText(row);
            if (tl < 0 || raw < 0 || raw > SegmentFormat.MaxBlob || tl > raw || (tl == 0) != (raw == 0) || off < 0 || off > textLen - tl) return null;
        }

        // Picture records: documents ascending and in range.
        int prevDoc = -1;
        for (int i = 0; i < h.MetaCount; i++)
        {
            int doc = BinaryPrimitives.ReadInt32LittleEndian(b.Span(metaEntry.Offset + 4 + (long)i * SegmentFormat.MetaSize, 4));
            if (doc <= prevDoc || doc >= docs) return null;
            prevDoc = doc;
        }

        // Path blocks inside PATHS.
        long pathsLen = toc[(int)SegmentFormat.Section.Paths].Length;
        for (int i = 0; i < pathBlocks; i++)
        {
            var e = b.Span(pathIdx.Offset + (long)i * SegmentFormat.PathIndexEntrySize, SegmentFormat.PathIndexEntrySize);
            long off = BinaryPrimitives.ReadInt64LittleEndian(e);
            int stored = BinaryPrimitives.ReadInt32LittleEndian(e[8..]), raw = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            if (stored <= 0 || raw < stored || raw > SegmentFormat.MaxPathBlock || off < 0 || off > pathsLen - stored) return null;
        }

        // Dictionary index (kept in memory).
        int blocks = h.DictBlocks;
        if ((blocks == 0) != (h.TermCount == 0)) return null;
        if (blocks > 0 && (h.TermCount > (long)blocks * SegmentFormat.DictBlock || h.TermCount <= (long)(blocks - 1) * SegmentFormat.DictBlock)) return null;
        var dictIdx = toc[(int)SegmentFormat.Section.DictIndex];
        long dictLen = toc[(int)SegmentFormat.Section.Dict].Length, postLen = toc[(int)SegmentFormat.Section.Postings].Length;
        if (dictIdx.Length > int.MaxValue) return null;
        var idx = b.Span(dictIdx.Offset, (int)dictIdx.Length);
        var blockOffset = new long[blocks];
        var blockBase = new long[blocks];
        var blockCrc = new uint[blocks];
        var postCrc = new uint[blocks];
        var firstStart = new int[blocks + 1];
        using var firsts = new ByteBuffer(Math.Max(64, blocks * 8));
        int p = 0;
        for (int i = 0; i < blocks; i++)
        {
            if (idx.Length - p < SegmentFormat.DictIndexFixedSize) return null;
            long off = BinaryPrimitives.ReadInt64LittleEndian(idx[p..]);
            long bas = BinaryPrimitives.ReadInt64LittleEndian(idx[(p + 8)..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(idx[(p + 16)..]);
            uint pcrc = BinaryPrimitives.ReadUInt32LittleEndian(idx[(p + 20)..]);
            int tl = BinaryPrimitives.ReadUInt16LittleEndian(idx[(p + 24)..]);
            p += SegmentFormat.DictIndexFixedSize;
            if (tl == 0 || tl > SegmentFormat.MaxTermBytes || idx.Length - p < tl) return null;
            var term = idx.Slice(p, tl);
            p += tl;
            if (i == 0 ? off != 0 : off <= blockOffset[i - 1]) return null;
            if (off >= dictLen || bas < 0 || bas > postLen || (i > 0 && bas < blockBase[i - 1])) return null;
            if (i > 0 && term.SequenceCompareTo(firsts.Span[firstStart[i - 1]..]) <= 0) return null;
            blockOffset[i] = off;
            blockBase[i] = bas;
            blockCrc[i] = crc;
            postCrc[i] = pcrc;
            firstStart[i] = firsts.Length;
            firsts.Write(term);
        }
        if (p != idx.Length) return null;
        firstStart[blocks] = firsts.Length;
        for (int i = 0; i < blocks; i++)
        {
            long end = i + 1 < blocks ? blockOffset[i + 1] : dictLen;
            if (end - blockOffset[i] > SegmentFormat.DictBlock * (SegmentFormat.MaxTermBytes + 4L * VarInt.MaxLength)) return null;
        }
        if (blocks == 0 && dictLen != 0) return null;

        // Statistics.
        var statsEntry = toc[(int)SegmentFormat.Section.Stats];
        if (statsEntry.Length > 1 + 256 * 65) return null;
        var statsBytes = b.Span(statsEntry.Offset, (int)statsEntry.Length);
        if (statsBytes.IsEmpty || statsBytes.Length != 1 + statsBytes[0] * 65) return null;
        var stats = new Dictionary<ContentKind, KindStats>();
        Span<long> values = stackalloc long[8];
        for (int i = 0; i < statsBytes[0]; i++)
        {
            var e = statsBytes[(1 + i * 65)..];
            for (int k = 0; k < 8; k++)
            {
                values[k] = BinaryPrimitives.ReadInt64LittleEndian(e[(1 + k * 8)..]);
                if (values[k] < 0) return null;
            }
            if (!stats.TryAdd((ContentKind)e[0], KindStatsMath.From(values))) return null;
        }

        // Volume table.
        var volEntry = toc[(int)SegmentFormat.Section.Volumes];
        if (volEntry.Length < 2 || volEntry.Length > 64 << 20) return null;
        var vol = b.Span(volEntry.Offset, (int)volEntry.Length);
        int volCount = BinaryPrimitives.ReadUInt16LittleEndian(vol);
        var volumes = new VolumeEntry[volCount];
        int vp = 2;
        for (int i = 0; i < volCount; i++)
        {
            if (vol.Length - vp < 2) return null;
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(vol[vp..]);
            vp += 2;
            if (!VarInt.TryReadString(vol, ref vp, 4096, out string key) || !VarInt.TryReadString(vol, ref vp, 32 << 10, out string root)) return null;
            volumes[i] = new VolumeEntry(index, key, root, DateTime.MinValue);
        }
        if (vp != vol.Length) return null;

        return new SegmentReader(id, path, b, h, toc, blockOffset, blockBase, blockCrc, postCrc, firstStart, firsts.Span.ToArray(), stats, volumes);
    }

    static bool CrcMatches(SegmentBytes b, in SegmentFormat.TocEntry e, CancellationToken ct = default)
    {
        uint crc = Crc32C.Start;
        for (long done = 0; done < e.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(1 << 20, e.Length - done);
            crc = Crc32C.Update(crc, b.Span(e.Offset + done, n));
            done += n;
        }
        return Crc32C.Finish(crc) == e.Crc;
    }

    /// <summary>Stable segment id (file name); never reused.</summary>
    public uint Id { get; }
    /// <summary>The file, or null when the segment is in memory.</summary>
    internal string? Path { get; }

    /// <summary>The file's length and footer (TOC length and CRC): tells this segment's file from a later one with the same name.</summary>
    internal (long Length, ulong Footer) Fingerprint => (_bytes.Length, _footer);

    /// <summary>Whether the file at path is still this segment's (same length and footer); false when it can't be read.</summary>
    internal static bool IsSameFile(string path, (long Length, ulong Footer) fingerprint)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (fs.Length != fingerprint.Length || fs.Length < SegmentFormat.FooterSize) return false;
            Span<byte> footer = stackalloc byte[8];
            fs.Position = fs.Length - SegmentFormat.FooterSize;
            fs.ReadExactly(footer);
            return BinaryPrimitives.ReadUInt64LittleEndian(footer) == fingerprint.Footer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    public int DocCount => _header.DocCount;
    public int TokenizerVersion => _header.TokenizerVersion;
    public long CreatedUtc => _header.CreatedUtc;
    /// <summary>Distinct terms in the dictionary.</summary>
    internal int TermCount => _header.TermCount;
    /// <summary>The segment's size in bytes.</summary>
    internal long Bytes => _bytes.Length;
    /// <summary>The volume table written with the segment (LastSeenUtc unknown).</summary>
    internal IReadOnlyList<VolumeEntry> Volumes => _volumes;
    /// <summary>Per-kind totals of every document written (deleted ones included).</summary>
    internal IReadOnlyDictionary<ContentKind, KindStats> Stats => _stats;

    /// <summary>A structural fault was met while decoding: drop at the next commit.</summary>
    public bool IsDamaged => Volatile.Read(ref _damaged) != 0;
    internal void MarkDamaged() => Volatile.Write(ref _damaged, 1);
    /// <summary>Every section was checked against its CRC (the idle scrub).</summary>
    internal bool IsVerified => Volatile.Read(ref _verifiedAll) != 0;

    /// <summary>Per-kind totals of the segment.</summary>
    public KindStats StatsOf(ContentKind kind) => _stats.GetValueOrDefault(kind);

    /// <summary>Average bytes read and milliseconds per document of a kind (carried by copies and merges).</summary>
    internal (long BytesRead, int Ms) Averages(ContentKind kind)
    {
        var s = StatsOf(kind);
        return s.Docs > 0 ? (s.BytesRead / s.Docs, (int)Math.Min(int.MaxValue, s.MsTotal / s.Docs)) : (0, 0);
    }

    ReadOnlySpan<byte> RowSpan(int doc)
    {
        if ((uint)doc >= (uint)DocCount) throw new ArgumentOutOfRangeException(nameof(doc));
        return _bytes.Span(_docsOff + (long)doc * SegmentFormat.RowSize, SegmentFormat.RowSize);
    }

    public DocRow Row(int doc) => SegmentFormat.ReadRow(RowSpan(doc));

    /// <summary>The key of a document (one read).</summary>
    internal ulong KeyAt(int doc) => SegmentFormat.RowKey(RowSpan(doc));

    /// <summary>Local document with this key, or -1 (binary search over the key-sorted rows).</summary>
    public int Find(ulong key)
    {
        int lo = 0, hi = DocCount - 1;
        while (lo <= hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            ulong k = KeyAt(mid);
            if (k == key) return mid;
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    ReadOnlySpan<byte> FirstTerm(int block) => _firstTerms.AsSpan(_firstStart[block], _firstStart[block + 1] - _firstStart[block]);

    /// <summary>The last block whose first term is at or before term; -1 when term comes before every block.</summary>
    int BlockFor(ReadOnlySpan<byte> term)
    {
        int lo = 0, hi = _blockOffset.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (FirstTerm(mid).SequenceCompareTo(term) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    int TermsIn(int block) => block + 1 < _blockOffset.Length ? SegmentFormat.DictBlock : _header.TermCount - SegmentFormat.DictBlock * block;

    /// <summary>A block's bytes after checking its CRC once; empty (and the segment damaged) when it fails.</summary>
    ReadOnlySpan<byte> Block(int block)
    {
        long start = _blockOffset[block];
        long end = block + 1 < _blockOffset.Length ? _blockOffset[block + 1] : _dictLen;
        var bytes = _bytes.Span(_dictOff + start, (int)(end - start));
        int bit = 1 << (block & 31);
        if ((Volatile.Read(ref _verified[block >> 5]) & bit) == 0)
        {
            if (Crc32C.Compute(bytes) != _blockCrc[block])
            {
                MarkDamaged();
                return default;
            }
            Interlocked.Or(ref _verified[block >> 5], bit);
        }
        return bytes;
    }

    /// <summary>Decodes one dictionary block entry by entry.</summary>
    ref struct BlockReader
    {
        readonly ReadOnlySpan<byte> _data;
        readonly Span<byte> _term;
        readonly int _docCount;
        readonly long _postLen;
        int _pos, _left, _termLength;
        long _prevOffset;

        public BlockReader(ReadOnlySpan<byte> data, int terms, long postingsBase, int docCount, long postLen, Span<byte> scratch)
        {
            _data = data;
            _left = data.IsEmpty ? 0 : terms;
            _prevOffset = postingsBase;
            _docCount = docCount;
            _postLen = postLen;
            _term = scratch;
            Fault = data.IsEmpty && terms > 0;
        }

        public bool Fault { get; private set; }
        public readonly ReadOnlySpan<byte> Term => _term[.._termLength];

        public bool Next(out TermPostings postings)
        {
            postings = default;
            if (_left == 0 || Fault)
            {
                if (!Fault && _left == 0 && _pos != _data.Length) Fault = true;
                return false;
            }
            int p = _pos;
            if (!Small(_data, ref p, _termLength, out int shared)
                || !Small(_data, ref p, SegmentFormat.MaxTermBytes - shared, out int suffix)
                || suffix > _data.Length - p || shared + suffix == 0)
                return Failed();
            _data.Slice(p, suffix).CopyTo(_term[shared..]);
            p += suffix;
            _termLength = shared + suffix;
            if (!Small(_data, ref p, _docCount, out int docFreq) || docFreq == 0) return Failed();
            if (docFreq <= 2)
            {
                if (!Small(_data, ref p, _docCount - 1, out int d0)) return Failed();
                int d1 = -1;
                if (docFreq == 2)
                {
                    if (!Small(_data, ref p, _docCount - 1 - d0, out int delta) || delta == 0) return Failed();
                    d1 = d0 + delta;
                }
                postings = new TermPostings(docFreq, -1, d0, d1);
            }
            else
            {
                if (!VarInt.TryRead(_data, ref p, _postLen, out long delta)) return Failed();
                long offset = _prevOffset + delta;
                if (offset >= _postLen) return Failed();
                _prevOffset = offset;
                postings = new TermPostings(docFreq, offset, -1, -1);
            }
            _pos = p;
            _left--;
            return true;
        }

        bool Failed()
        {
            Fault = true;
            return false;
        }

        /// <summary>A varint of at most max, with the one-byte case inline (most lengths, counts and deltas).</summary>
        static bool Small(ReadOnlySpan<byte> s, ref int p, int max, out int value)
        {
            if ((uint)p < (uint)s.Length && s[p] < 0x80)
            {
                value = s[p];
                if (value > max) return false;
                p++;
                return true;
            }
            return VarInt.TryRead(s, ref p, max, out value);
        }
    }

    BlockReader ReaderFor(int block, Span<byte> scratch)
        => new(Block(block), TermsIn(block), _blockBase[block], DocCount, _postLen, scratch);

    /// <summary>
    /// Visits terms in [from, to) in byte order (folded UTF-8); an empty <paramref name="to"/> means no upper bound. Stops
    /// when visit returns false; polls ct once per block.
    /// </summary>
    public void Terms(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, TermVisitor visit, CancellationToken ct)
    {
        int blocks = _blockOffset.Length;
        if (blocks == 0 || (!to.IsEmpty && to.SequenceCompareTo(from) <= 0)) return;
        int first = from.IsEmpty ? 0 : Math.Max(0, BlockFor(from));
        Span<byte> scratch = stackalloc byte[SegmentFormat.MaxTermBytes];
        for (int b = first; b < blocks; b++)
        {
            ct.ThrowIfCancellationRequested();
            if (!to.IsEmpty && b > first && FirstTerm(b).SequenceCompareTo(to) >= 0) return;
            // Terms of block b come before the next block's first term: compare with from only in the first block, and with
            // to only in a block the bound can fall in.
            bool checkFrom = b == first && !from.IsEmpty;
            bool checkTo = !to.IsEmpty && (b + 1 >= blocks || FirstTerm(b + 1).SequenceCompareTo(to) > 0);
            var r = ReaderFor(b, scratch);
            bool firstTerm = true;
            while (r.Next(out var postings))
            {
                var term = r.Term;
                if (firstTerm && !term.SequenceEqual(FirstTerm(b)))
                {
                    MarkDamaged(); // the block doesn't start where DICTIDX says
                    break;
                }
                firstTerm = false;
                if (checkFrom)
                {
                    if (term.SequenceCompareTo(from) < 0) continue;
                    checkFrom = false; // terms ascend
                }
                if (checkTo && term.SequenceCompareTo(to) >= 0) return;
                if (!visit(term, postings)) return;
            }
            if (r.Fault) MarkDamaged();
        }
    }

    /// <summary>Exact term lookup.</summary>
    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermPostings postings)
    {
        postings = default;
        if (term.IsEmpty || term.Length > SegmentFormat.MaxTermBytes || _blockOffset.Length == 0) return false;
        int b = BlockFor(term);
        if (b < 0) return false;
        Span<byte> scratch = stackalloc byte[SegmentFormat.MaxTermBytes];
        var r = ReaderFor(b, scratch);
        while (r.Next(out var p))
        {
            int c = r.Term.SequenceCompareTo(term);
            if (c == 0)
            {
                postings = p;
                return true;
            }
            if (c > 0) return false;
        }
        if (r.Fault) MarkDamaged();
        return false;
    }

    /// <summary>
    /// Checks, once per dictionary block, the CRC of the run of POSTINGS its terms point into (from its postings base to
    /// the next block's); false, and the segment damaged, when it fails.
    /// </summary>
    bool PostingsChecked(long offset)
    {
        // The block holding offset: the last whose base is at or before it (blocks without postings share the next base).
        int lo = 0, hi = _blockBase.Length - 1, b = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_blockBase[mid] <= offset) { b = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (b < 0)
        {
            MarkDamaged();
            return false;
        }
        int bit = 1 << (b & 31);
        if ((Volatile.Read(ref _postVerified[b >> 5]) & bit) != 0) return true;
        long start = _blockBase[b], end = b + 1 < _blockBase.Length ? _blockBase[b + 1] : _postLen;
        uint crc = Crc32C.Start;
        for (long done = start; done < end;)
        {
            int n = (int)Math.Min(1 << 20, end - done);
            crc = Crc32C.Update(crc, _bytes.Span(_postOff + done, n));
            done += n;
        }
        if (Crc32C.Finish(crc) != _postCrc[b])
        {
            MarkDamaged();
            return false;
        }
        Interlocked.Or(ref _postVerified[b >> 5], bit);
        return true;
    }

    /// <summary>The term's documents of a block of postings, after checks; false (and the segment damaged) on a fault.</summary>
    bool DecodePostings(in TermPostings p, Span<int> docs, CancellationToken ct)
    {
        int n = p.DocFreq;
        if (p.Offset < 0 || p.Offset >= _postLen || !PostingsChecked(p.Offset)) return false;
        long max = Math.Min(_postLen - p.Offset, 2L * VarInt.MaxLength * (n / SegmentFormat.SkipEvery + 1) + VarInt.MaxLength + 5L * n);
        var s = _bytes.Span(_postOff + p.Offset, (int)Math.Min(max, int.MaxValue));
        int pos = 0;
        if (n > SegmentFormat.SkipAbove)
        {
            if (!VarInt.TryRead(s, ref pos, n, out int skips) || skips != (n - 1) / SegmentFormat.SkipEvery) return false;
            for (int i = 0; i < 2 * skips; i++)
                if (!VarInt.TryRead(s, ref pos, out _)) return false;
        }
        int doc = -1, count = DocCount;
        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFFF) == 0xFFFF) ct.ThrowIfCancellationRequested();
            ulong v;
            if ((uint)pos < (uint)s.Length && s[pos] < 0x80) v = s[pos++]; // most deltas take one byte
            else if (!VarInt.TryRead(s, ref pos, out v)) return false;
            long next = i == 0 ? (long)v : (v == 0 ? -1 : doc + (long)v);
            if (next < 0 || next >= count || (i > 0 && v > int.MaxValue)) return false;
            doc = (int)next;
            docs[i] = doc;
        }
        return true;
    }

    /// <summary>Adds a term's documents for this segment's ordinal to a builder; polls ct. Nothing is added on a fault.</summary>
    public void ReadPostings(in TermPostings postings, int segmentOrdinal, DocSetBuilder into, CancellationToken ct)
    {
        int n = postings.DocFreq;
        if (n <= 0 || n > DocCount) return;
        if (n <= 2)
        {
            int d0 = postings.Inline0, d1 = postings.Inline1;
            if ((uint)d0 >= (uint)DocCount || (n == 2 && (d1 <= d0 || d1 >= DocCount))) return;
            into.Add(segmentOrdinal, d0);
            if (n == 2) into.Add(segmentOrdinal, d1);
            return;
        }
        var docs = ArrayPool<int>.Shared.Rent(n);
        try
        {
            if (!DecodePostings(postings, docs, ct))
            {
                MarkDamaged();
                return;
            }
            into.AddSorted(segmentOrdinal, docs.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(docs);
        }
    }

    /// <summary>A term's documents one by one, with skips (AND of long postings); null when the postings are damaged.</summary>
    internal PostingsCursor? Cursor(in TermPostings postings)
    {
        int n = postings.DocFreq;
        if (n <= 0 || n > DocCount) return null;
        if (n <= 2)
        {
            if ((uint)postings.Inline0 >= (uint)DocCount || (n == 2 && (postings.Inline1 <= postings.Inline0 || postings.Inline1 >= DocCount))) return null;
            return new PostingsCursor(n == 1 ? [postings.Inline0] : [postings.Inline0, postings.Inline1]);
        }
        var docs = new int[n];
        if (!DecodePostings(postings, docs, CancellationToken.None))
        {
            MarkDamaged();
            return null;
        }
        // The skip table is re-read so Advance can jump: entry k = (last document before block k, its index).
        var skipDocs = new int[(n - 1) / SegmentFormat.SkipEvery];
        if (n > SegmentFormat.SkipAbove)
        {
            var s = _bytes.Span(_postOff + postings.Offset, (int)Math.Min(_postLen - postings.Offset, 2L * VarInt.MaxLength * (skipDocs.Length + 1)));
            int pos = 0;
            VarInt.TryRead(s, ref pos, out _);
            long baseDoc = 0;
            for (int k = 0; k < skipDocs.Length; k++)
            {
                if (!VarInt.TryRead(s, ref pos, out ulong dd) || !VarInt.TryRead(s, ref pos, out _)) return null;
                baseDoc += (long)dd;
                if (baseDoc != docs[(k + 1) * SegmentFormat.SkipEvery - 1])
                {
                    MarkDamaged(); // the skip table disagrees with the documents
                    return null;
                }
                skipDocs[k] = (int)baseDoc;
            }
        }
        else for (int k = 0; k < skipDocs.Length; k++) skipDocs[k] = docs[(k + 1) * SegmentFormat.SkipEvery - 1];
        return new PostingsCursor(docs, skipDocs);
    }

    ReadOnlySpan<byte> TextOf(int doc, out int rawLength)
    {
        var (off, length, raw) = SegmentFormat.RowText(RowSpan(doc));
        rawLength = raw;
        return length == 0 ? default : _bytes.Span(_textOff + off, length);
    }

    /// <summary>A document's stored (compressed) and raw text lengths.</summary>
    internal (int Stored, int Raw) TextLengths(int doc)
    {
        var (_, length, raw) = SegmentFormat.RowText(RowSpan(doc));
        return (length, raw);
    }

    /// <summary>A document's stored bytes as written (valid while the segment is held) and its raw length.</summary>
    internal ReadOnlySpan<byte> StoredSpan(int doc, out int rawLength) => TextOf(doc, out rawLength);

    /// <summary>Decompressed stored text with the picture details; null when damaged.</summary>
    public StoredDoc? ReadStored(int doc)
    {
        var text = TextOf(doc, out int raw);
        var stored = StoredText.Decode(text, raw);
        if (stored == null)
        {
            MarkDamaged();
            return null;
        }
        return TryGetPicture(doc, out var picture) ? stored with { Picture = picture } : stored;
    }

    /// <summary>The compressed stored-text bytes as written (merges and re-keys copy them).</summary>
    public ReadOnlyMemory<byte> ReadStoredRaw(int doc)
    {
        var (off, length, _) = SegmentFormat.RowText(RowSpan(doc));
        if (length == 0) return ReadOnlyMemory<byte>.Empty;
        if (_bytes is MemoryBytes m) return m.Memory.Slice((int)(_textOff + off), length);
        return _bytes.Span(_textOff + off, length).ToArray();
    }

    [ThreadStatic] static PathCache? t_paths;

    /// <summary>The last decoded path block of this thread.</summary>
    sealed class PathCache
    {
        public SegmentReader? Owner;
        public int Block = -1, Count;
        public byte[] Data = new byte[4096];
        public readonly int[] Start = new int[SegmentFormat.PathBlock], Length = new int[SegmentFormat.PathBlock];
    }

    /// <summary>The volume-relative path; null when damaged.</summary>
    public string? ReadPath(int doc)
    {
        if ((uint)doc >= (uint)DocCount) throw new ArgumentOutOfRangeException(nameof(doc));
        int block = doc / SegmentFormat.PathBlock, i = doc % SegmentFormat.PathBlock;
        var cache = t_paths ??= new PathCache();
        if (!ReferenceEquals(cache.Owner, this) || cache.Block != block)
        {
            cache.Owner = null;
            if (!LoadPaths(block, cache))
            {
                MarkDamaged();
                return null;
            }
            cache.Owner = this;
            cache.Block = block;
        }
        var bytes = cache.Data.AsSpan(cache.Start[i], cache.Length[i]);
        if (!Utf8.IsValid(bytes))
        {
            MarkDamaged();
            return null;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    bool LoadPaths(int block, PathCache cache)
    {
        var e = _bytes.Span(_pathIdxOff + (long)block * SegmentFormat.PathIndexEntrySize, SegmentFormat.PathIndexEntrySize);
        long off = BinaryPrimitives.ReadInt64LittleEndian(e);
        int stored = BinaryPrimitives.ReadInt32LittleEndian(e[8..]), raw = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
        if (cache.Data.Length < raw) cache.Data = new byte[Math.Max(raw, cache.Data.Length * 2)];
        if (!StoredText.TryDecompress(_bytes.Span(_pathsOff + off, stored), raw, cache.Data)) return false;
        int count = Math.Min(SegmentFormat.PathBlock, DocCount - block * SegmentFormat.PathBlock);
        var data = cache.Data.AsSpan(0, raw);
        int pos = 0;
        for (int k = 0; k < count; k++)
        {
            if (!VarInt.TryRead(data, ref pos, SegmentFormat.MaxPathBytes, out int length) || length > data.Length - pos) return false;
            cache.Start[k] = pos;
            cache.Length[k] = length;
            pos += length;
        }
        cache.Count = count;
        return pos == data.Length;
    }

    /// <summary>Documents with picture details.</summary>
    public int PictureCount => _header.MetaCount;

    /// <summary>The i-th picture record, ascending by document.</summary>
    public (int Doc, PictureInfo Info) Picture(int i)
    {
        if ((uint)i >= (uint)PictureCount) throw new ArgumentOutOfRangeException(nameof(i));
        var s = _bytes.Span(_metaOff + (long)i * SegmentFormat.MetaSize, SegmentFormat.MetaSize);
        return (BinaryPrimitives.ReadInt32LittleEndian(s), SegmentFormat.ReadPicture(s[4..]));
    }

    /// <summary>Picture details of one document.</summary>
    public bool TryGetPicture(int doc, out PictureInfo info)
    {
        int lo = 0, hi = PictureCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = _bytes.Span(_metaOff + (long)mid * SegmentFormat.MetaSize, SegmentFormat.MetaSize);
            int d = BinaryPrimitives.ReadInt32LittleEndian(s);
            if (d == doc)
            {
                info = SegmentFormat.ReadPicture(s[4..]);
                return true;
            }
            if (d < doc) lo = mid + 1; else hi = mid - 1;
        }
        info = default;
        return false;
    }

    /// <summary>
    /// Checks every section against its CRC and every dictionary block against its own (the idle scrub, and before a
    /// merge reads the segment); false, and the segment damaged, when one fails. Polls ct between 1 MB chunks.
    /// </summary>
    internal bool Verify(CancellationToken ct)
    {
        if (IsVerified) return !IsDamaged;
        for (int s = 1; s < SegmentFormat.SectionCount; s++)
            if (!CrcMatches(_bytes, _toc[s], ct))
            {
                MarkDamaged();
                return false;
            }
        for (int b = 0; b < _blockOffset.Length; b++)
        {
            if ((b & 255) == 0) ct.ThrowIfCancellationRequested();
            if (Block(b).IsEmpty && TermsIn(b) > 0) return false;
        }
        Volatile.Write(ref _verifiedAll, 1);
        return !IsDamaged;
    }

    /// <summary>Called once the last reference is gone and the segment is unmapped (the store deletes a retired file).</summary>
    internal Action<SegmentReader>? Released { get; set; }

    /// <summary>Whether every reference was released (the mapping is gone).</summary>
    internal bool IsReleased => Volatile.Read(ref _refs) <= 0;

    /// <summary>Adds a reference unless the last one is gone.</summary>
    internal bool TryRetain()
    {
        for (int refs = Volatile.Read(ref _refs); refs > 0; refs = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        return false;
    }

    /// <summary>Releases one reference: the opener's, or a view's. The last one unmaps the segment.</summary>
    internal void Release()
    {
        int refs = Interlocked.Decrement(ref _refs);
        if (refs == 0)
        {
            _bytes.Dispose();
            Released?.Invoke(this);
        }
        else if (refs < 0) Interlocked.Exchange(ref _refs, 0);
    }

    /// <summary>Releases the opener's reference; the segment unmaps once no view holds it either.</summary>
    public void Dispose() => Release();
}

/// <summary>A term's documents with skip points every 128 documents.</summary>
internal sealed class PostingsCursor
{
    readonly int[] _docs, _skipDocs;
    int _i = -1;

    internal PostingsCursor(int[] docs, int[]? skipDocs = null)
    {
        _docs = docs;
        _skipDocs = skipDocs ?? [];
    }

    public int Count => _docs.Length;
    /// <summary>The current document (after MoveNext or Advance returned true).</summary>
    public int Doc => _docs[_i];
    /// <summary>Skip entries used by Advance so far (tests).</summary>
    public int SkipsTaken { get; private set; }

    public bool MoveNext() => ++_i < _docs.Length;

    /// <summary>Moves to the first document at or after target (never backwards); false when none is left.</summary>
    public bool Advance(int target)
    {
        if (_i >= 0 && _i < _docs.Length && _docs[_i] >= target) return true;
        // Jump to the furthest block whose previous block ends before target.
        int block = (_i < 0 ? 0 : _i) / SegmentFormat.SkipEvery;
        while (block < _skipDocs.Length && _skipDocs[block] < target)
        {
            block++;
            SkipsTaken++;
        }
        int start = Math.Max(_i + 1, block * SegmentFormat.SkipEvery);
        for (_i = start; _i < _docs.Length; _i++)
            if (_docs[_i] >= target) return true;
        return false;
    }
}

/// <summary>A segment's bytes: memory-mapped or in memory. Reads after Dispose throw (and are counted).</summary>
internal abstract class SegmentBytes : IDisposable
{
    public abstract long Length { get; }
    /// <summary>length bytes at offset; throws when outside the segment.</summary>
    public abstract ReadOnlySpan<byte> Span(long offset, int length);
    public abstract void Dispose();

    protected static ObjectDisposedException Released()
    {
        Interlocked.Increment(ref SegmentReader.AccessesAfterRelease);
        return new ObjectDisposedException("segment", "A segment was read after its last reference was released.");
    }
}

/// <summary>Segment bytes held in memory (the in-memory store and tests).</summary>
internal sealed class MemoryBytes(ReadOnlyMemory<byte> memory) : SegmentBytes
{
    volatile bool _released;

    public ReadOnlyMemory<byte> Memory => memory;
    public override long Length => memory.Length;

    public override ReadOnlySpan<byte> Span(long offset, int length)
    {
        if (_released) throw Released();
        if (offset < 0 || length < 0 || offset > memory.Length - length) throw new ArgumentOutOfRangeException(nameof(offset));
        return memory.Span.Slice((int)offset, length);
    }

    public override void Dispose() => _released = true;
}

/// <summary>A read-only mapping of a segment file, opened with Read|Delete sharing so a replace or delete never waits for it.</summary>
internal sealed unsafe class MappedBytes : SegmentBytes
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _pointer;
    readonly long _length;
    int _released;

    MappedBytes(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, long length)
    {
        _file = file;
        _view = view;
        _pointer = pointer;
        _length = length;
    }

    /// <summary>Maps a file; null when it is too short to be a segment.</summary>
    public static MappedBytes? Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            long length = stream.Length;
            if (length < SegmentFormat.HeaderSize + SegmentFormat.FooterSize)
            {
                stream.Dispose();
                return null;
            }
            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            return new MappedBytes(file, view, p + view.PointerOffset, length);
        }
        catch
        {
            view?.Dispose();
            if (file != null) file.Dispose(); else stream.Dispose();
            throw;
        }
    }

    public override long Length => _length;

    public override ReadOnlySpan<byte> Span(long offset, int length)
    {
        if (Volatile.Read(ref _released) != 0) throw Released();
        if (offset < 0 || length < 0 || offset > _length - length) throw new ArgumentOutOfRangeException(nameof(offset));
        return new ReadOnlySpan<byte>(_pointer + offset, length);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
