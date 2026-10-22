using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Anything.Core.Content.Store;

/// <summary>
/// Builds one segment in bounded memory; terms come only from ContentTokenizer over the stored text. Postings are buffered
/// per term as delta varints up to 32 MB, then sorted by term and spilled as run files; Write k-way merges the runs into
/// POSTINGS and DICT. TEXT, PATHS, DOCS and META stream to temp section files as documents are added. Peak managed memory
/// of any commit or merge stays under 96 MB. Spill files are opened delete-on-close, so they vanish with the writer even
/// when the process dies.
/// </summary>
internal sealed class SegmentWriter : IDisposable
{
    /// <summary>Bytes the postings buffer may hold before it is spilled as a run (A3).</summary>
    public const int DefaultRunBudget = 32 << 20;

    readonly int _tokenizerVersion;
    readonly string? _spillFolder;
    readonly int _runBudget;
    readonly Stream _text, _paths, _docs, _meta;
    long _textLength, _pathsLength;
    readonly List<(long Offset, int Stored, int Raw)> _pathIndex = [];
    readonly ByteBuffer _pathBlock = new(8192);
    int _pathsInBlock;
    readonly Dictionary<ContentKind, KindStats> _stats = [];
    readonly TermBuffer _terms = new();
    readonly TermCollector _collector;
    readonly List<Stream> _runs = [];
    readonly byte[] _record = new byte[SegmentFormat.RowSize];
    ulong _lastKey;
    int _docCount, _metaCount;
    long _rawBytes;
    bool _written, _disposed;

    /// <summary>A writer for one segment; spill files go to spillFolder (null = in memory, for calibration and the in-memory store).</summary>
    public SegmentWriter(int tokenizerVersion = ContentTokenizer.Version, string? spillFolder = null)
        : this(tokenizerVersion, spillFolder, DefaultRunBudget) { }

    /// <summary>A writer with its own run budget (tests force spills with a small one).</summary>
    internal SegmentWriter(int tokenizerVersion, string? spillFolder, int runBudget)
    {
        if (tokenizerVersion != ContentTokenizer.Version)
            throw new ArgumentOutOfRangeException(nameof(tokenizerVersion), $"Tokenizer version {tokenizerVersion} is not known.");
        _tokenizerVersion = tokenizerVersion;
        _spillFolder = spillFolder;
        _runBudget = Math.Max(1 << 16, runBudget);
        _collector = new TermCollector(_terms, tokenizerVersion);
        _text = CreateSpill(spillFolder);
        _paths = CreateSpill(spillFolder);
        _docs = CreateSpill(spillFolder);
        _meta = CreateSpill(spillFolder);
    }

    /// <summary>The volume table written into the segment's VOLS section (for recovery without a manifest).</summary>
    internal IReadOnlyList<VolumeEntry> Volumes { get; set; } = [];

    /// <summary>Documents added.</summary>
    public int DocCount => _docCount;
    /// <summary>Raw UTF-8 bytes of stored text added.</summary>
    public long RawBytes => _rawBytes;
    /// <summary>Runs spilled so far (tests).</summary>
    internal int RunCount => _runs.Count;

    /// <summary>
    /// Adds a document; must be called in ascending key order (the local document number is the add index: a batch sorts its
    /// documents by key first, a merge feeds a k-way merge by key). Compressed text bytes are copied unchanged when given
    /// (merges, re-keys), else encoded and compressed. bytesRead and elapsedMs go into the kind's STATS (merges and re-keys
    /// pass the source's average per document). The row's DocFlags.HasText is set here from doc.Body, and HasPicture from
    /// doc.Picture.
    /// </summary>
    public int Add(in DocRow row, string relativePath, StoredDoc doc, ReadOnlyMemory<byte> compressedText = default, long bytesRead = 0, int elapsedMs = 0)
    {
        if (compressedText.IsEmpty)
        {
            var stored = StoredText.Encode(doc, out int raw);
            // A document past the caps is stored cut: its terms come from what is stored, as a merge would make them.
            if (!StoredText.FitsWhole(doc)) doc = (StoredText.Decode(stored, raw) ?? new StoredDoc("", [], null)) with { Picture = doc.Picture };
            return AddStored(row, relativePath, doc, stored, raw, bytesRead, elapsedMs);
        }
        return AddStored(row, relativePath, doc, compressedText.Span, StoredText.BlobLength(doc), bytesRead, elapsedMs);
    }

    /// <summary>Adds a document whose stored bytes and raw length are known (merges and copies pass the source's bytes).</summary>
    internal int AddStored(in DocRow row, string relativePath, StoredDoc doc, ReadOnlySpan<byte> stored, int rawLength, long bytesRead, int elapsedMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_written) throw new InvalidOperationException("The segment was already written.");
        if (_docCount > 0 && row.Key <= _lastKey) throw new ArgumentException("Documents must be added in ascending key order, one per key.", nameof(row));
        if (rawLength < 0 || rawLength > SegmentFormat.MaxBlob || stored.Length > rawLength || (rawLength == 0) != stored.IsEmpty)
            throw new ArgumentException("The stored text doesn't match its raw length.", nameof(stored));
        if (_docCount == int.MaxValue) throw new InvalidOperationException("The segment is full.");

        int local = _docCount;
        long textOffset = _textLength;
        _text.Write(stored);
        _textLength += stored.Length;

        int pairs = _collector.Collect(doc, local);

        var flags = row.Flags & ~(DocFlags.HasText | DocFlags.HasPicture);
        bool hasText = ContentTokenizer.HasWords(doc.Body);
        if (hasText) flags |= DocFlags.HasText;
        if (doc.Picture is not null) flags |= DocFlags.HasPicture;
        SegmentFormat.WriteRow(_record, row with { Flags = flags }, textOffset, stored.Length, rawLength);
        _docs.Write(_record);

        if (doc.Picture is { } picture)
        {
            Span<byte> meta = stackalloc byte[SegmentFormat.MetaSize];
            BinaryPrimitives.WriteInt32LittleEndian(meta, local);
            SegmentFormat.WritePicture(meta[4..], picture);
            _meta.Write(meta);
            _metaCount++;
        }

        AddPath(relativePath);

        var s = _stats.GetValueOrDefault(row.Kind);
        _stats[row.Kind] = new KindStats(s.Docs + 1, s.WithText + (hasText ? 1 : 0), s.RawTextBytes + rawLength, s.StoredBytes + stored.Length,
            s.Pairs + pairs, s.BytesRead + Math.Max(0, bytesRead), s.MsTotal + Math.Max(0, elapsedMs),
            s.Problems + (SegmentFormat.IsProblem(row.Status) ? 1 : 0));

        _lastKey = row.Key;
        _docCount++;
        _rawBytes += rawLength;
        if (_terms.UsedBytes >= _runBudget / 2) SpillRun();
        return local;
    }

    /// <summary>Encoded size of the stored text without writing (estimator calibration).</summary>
    public long MeasureStoredBytes() => _textLength;

    void AddPath(string path)
    {
        int n = Encoding.UTF8.GetByteCount(path);
        if (n > SegmentFormat.MaxPathBytes) throw new ArgumentException("The path is too long.", nameof(path));
        _pathBlock.WriteVarInt((ulong)n);
        _pathBlock.Advance(Encoding.UTF8.GetBytes(path, _pathBlock.Reserve(n)));
        if (++_pathsInBlock == SegmentFormat.PathBlock) FlushPathBlock();
    }

    void FlushPathBlock()
    {
        var raw = _pathBlock.Span;
        var stored = StoredText.Compress(raw); // as is when compressing doesn't help (stored == raw length)
        _pathIndex.Add((_pathsLength, stored.Length, raw.Length));
        _paths.Write(stored);
        _pathsLength += stored.Length;
        _pathBlock.Clear();
        _pathsInBlock = 0;
    }

    void SpillRun()
    {
        if (_terms.TermCount == 0) return;
        var run = CreateSpill(_spillFolder, buffered: false); // written in 60 KB chunks, read through the cursor's own buffer
        _terms.WriteRun(run);
        run.Flush();
        _runs.Add(run);
        _terms.Clear();
    }

    /// <summary>Writes the whole segment with CRCs; returns local numbers in Add order and per-kind statistics.</summary>
    public SegmentInfo Write(Stream output, uint segmentId) => Write(output, segmentId, CancellationToken.None, null);

    /// <summary>Writes the segment, polling ct and calling pace between chunks of the postings merge.</summary>
    internal SegmentInfo Write(Stream output, uint segmentId, CancellationToken ct, Action<CancellationToken>? pace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_written) throw new InvalidOperationException("The segment was already written.");
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("The output must be writable and seekable.", nameof(output));
        _written = true;
        if (_pathsInBlock > 0) FlushPathBlock();

        var w = new SectionWriter(output);
        var toc = new List<SegmentFormat.TocEntry>(SegmentFormat.SectionCount);
        var copy = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            w.WriteZeros(SegmentFormat.HeaderSize);

            w.Begin();
            w.Copy(_text, copy);
            toc.Add(w.End(SegmentFormat.Section.Text));

            w.Begin();
            w.Copy(_paths, copy);
            toc.Add(w.End(SegmentFormat.Section.Paths));

            w.Begin();
            Span<byte> entry = stackalloc byte[SegmentFormat.PathIndexEntrySize];
            foreach (var (offset, stored, raw) in _pathIndex)
            {
                BinaryPrimitives.WriteInt64LittleEndian(entry, offset);
                BinaryPrimitives.WriteInt32LittleEndian(entry[8..], stored);
                BinaryPrimitives.WriteInt32LittleEndian(entry[12..], raw);
                w.Write(entry);
            }
            toc.Add(w.End(SegmentFormat.Section.PathIndex));

            int termCount;
            List<DictIndexEntry> dictIndex;
            using (var dictSpill = CreateSpill(_spillFolder))
            {
                w.Begin();
                (termCount, dictIndex) = WritePostings(w, dictSpill, ct, pace);
                toc.Add(w.End(SegmentFormat.Section.Postings));

                w.Begin();
                w.Copy(dictSpill, copy);
                toc.Add(w.End(SegmentFormat.Section.Dict));
            }

            w.Begin();
            using (var idx = new ByteBuffer(Math.Max(256, dictIndex.Count * 40)))
            {
                foreach (var e in dictIndex)
                {
                    idx.WriteInt64(e.Offset);
                    idx.WriteInt64(e.PostingsBase);
                    idx.WriteUInt32(e.Crc);
                    idx.WriteUInt32(e.PostingsCrc);
                    idx.WriteUInt16((ushort)e.FirstTerm.Length);
                    idx.Write(e.FirstTerm);
                }
                w.Write(idx.Span);
            }
            toc.Add(w.End(SegmentFormat.Section.DictIndex));

            w.Begin();
            w.Copy(_docs, copy);
            toc.Add(w.End(SegmentFormat.Section.Docs));

            w.Begin();
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, _metaCount);
            w.Write(count);
            w.Copy(_meta, copy);
            toc.Add(w.End(SegmentFormat.Section.Meta));

            w.Begin();
            using (var stats = new ByteBuffer(512))
            {
                var kinds = _stats.Keys.Order().ToList();
                stats.WriteByte((byte)kinds.Count);
                foreach (var kind in kinds)
                {
                    stats.WriteByte((byte)kind);
                    var s = _stats[kind];
                    for (int i = 0; i < 8; i++) stats.WriteInt64(KindStatsMath.Get(s, i));
                }
                w.Write(stats.Span);
            }
            toc.Add(w.End(SegmentFormat.Section.Stats));

            w.Begin();
            using (var vols = new ByteBuffer(256))
            {
                var list = Volumes.Take(ushort.MaxValue).ToList();
                vols.WriteUInt16((ushort)list.Count);
                foreach (var v in list)
                {
                    vols.WriteUInt16(v.Index);
                    vols.WriteString(v.Key);
                    vols.WriteString(v.DisplayRoot);
                }
                w.Write(vols.Span);
            }
            toc.Add(w.End(SegmentFormat.Section.Volumes));

            long tocOffset = w.Position;
            Span<byte> header = stackalloc byte[SegmentFormat.HeaderSize];
            new SegmentFormat.Header(SegmentFormat.Format, (ushort)_tokenizerVersion, _docCount, termCount, dictIndex.Count, _metaCount,
                DateTime.UtcNow.ToFileTimeUtc(), tocOffset).Write(header);
            toc.Insert(0, new SegmentFormat.TocEntry(SegmentFormat.Section.Header, 0, SegmentFormat.HeaderSize, Crc32C.Compute(header)));

            var tocBytes = new byte[toc.Count * SegmentFormat.TocEntrySize];
            for (int i = 0; i < toc.Count; i++) toc[i].Write(tocBytes.AsSpan(i * SegmentFormat.TocEntrySize));
            w.WriteRaw(tocBytes);
            Span<byte> footer = stackalloc byte[SegmentFormat.FooterSize];
            BinaryPrimitives.WriteInt32LittleEndian(footer, tocBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], Crc32C.Compute(tocBytes));
            SegmentFormat.EndMagic.CopyTo(footer[8..]);
            w.WriteRaw(footer);
            long length = w.Position;

            w.Seek(0);
            w.WriteRaw(header);
            w.Seek(length);
            output.Flush();

            var local = new int[_docCount];
            for (int i = 0; i < local.Length; i++) local[i] = i;
            return new SegmentInfo(segmentId, _docCount, length, local, new Dictionary<ContentKind, KindStats>(_stats));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    /// <summary>Merges the runs into POSTINGS (straight to the output) and DICT (to a spill); returns the term count and DICTIDX.</summary>
    (int Terms, List<DictIndexEntry> Index) WritePostings(SectionWriter w, Stream dictSpill, CancellationToken ct, Action<CancellationToken>? pace)
    {
        var cursors = new List<RunCursor>(_runs.Count + 1);
        try
        {
            int bufferSize = _runs.Count > 256 ? 16 << 10 : 64 << 10;
            for (int i = 0; i < _runs.Count; i++) cursors.Add(new FileRunCursor(_runs[i], i, bufferSize));
            if (_terms.TermCount > 0) cursors.Add(new MemoryRunCursor(_terms, cursors.Count));
            var heap = new RunHeap(cursors.Count);
            foreach (var c in cursors) if (c.MoveNext()) heap.Push(c);

            using var docs = new PostingsEncoder();
            using var dict = new DictBuilder(dictSpill, w.TakePartCrc); // a block's postings are what w got since the block before
            var term = new byte[SegmentFormat.MaxTermBytes];
            long postingsStart = w.Position;
            int terms = 0;
            while (heap.Count > 0)
            {
                var top = heap.Peek;
                int length = top.Term.Length;
                top.Term.CopyTo(term);
                docs.Clear();
                while (heap.Count > 0 && heap.Peek.Term.SequenceEqual(term.AsSpan(0, length)))
                {
                    var c = heap.Pop();
                    c.AppendDocs(docs);
                    if (c.MoveNext()) heap.Push(c);
                }
                long before = w.Position - postingsStart;
                if (docs.Count >= 3) docs.WriteTo(w);
                dict.Add(term.AsSpan(0, length), docs.Count, docs.First, docs.Second, before);
                terms++;
                if ((terms & 0x3FFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    pace?.Invoke(ct);
                }
            }
            dict.Finish();
            return (terms, dict.Index);
        }
        finally
        {
            foreach (var c in cursors) c.Dispose();
        }
    }

    static Stream CreateSpill(string? folder, bool buffered = true)
    {
        if (folder == null) return new MemoryStream();
        Directory.CreateDirectory(folder);
        return new FileStream(Path.Combine(folder, "w" + Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, buffered ? 1 << 16 : 0, FileOptions.DeleteOnClose);
    }

    /// <summary>Deletes the spill files.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in (Stream[])[_text, _paths, _docs, _meta, .. _runs]) s.Dispose();
        _runs.Clear();
        _pathBlock.Dispose();
        _terms.Dispose();
    }

    /// <summary>One DICTIDX entry: the block's offset in DICT, its postings base, its CRC and its run of postings' CRC.</summary>
    internal readonly record struct DictIndexEntry(long Offset, long PostingsBase, uint Crc, uint PostingsCrc, byte[] FirstTerm);

    /// <summary>
    /// Turns tokens into (term, document) pairs: UTF-8 terms, repeats in a document dropped, at most 100,000 per document.
    /// Fields go first: they are small (the reader caps them at 4 KB each, keywords at 64 KB), so a body that reaches the
    /// cap (a CSV of ids) never costs its document the title:, keywords: and other field terms its stored text still has.
    /// </summary>
    sealed class TermCollector(TermBuffer terms, int version) : ITokenSink
    {
        readonly byte[] _utf8 = new byte[SegmentFormat.MaxTermBytes];
        int _doc, _distinct;

        /// <summary>Collects a document's terms; returns how many distinct ones it has.</summary>
        public int Collect(StoredDoc doc, int local)
        {
            _doc = local;
            _distinct = 0;
            foreach (var (field, value) in doc.Fields)
                if (!string.IsNullOrEmpty(value)) ContentTokenizer.TokenizeField(field, value, this, version);
            ContentTokenizer.Tokenize(doc.Body, this, version);
            return _distinct;
        }

        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (_distinct >= ContentTokenizer.MaxDistinctPerDoc || folded.IsEmpty || folded.Length * 3 > _utf8.Length) return;
            if (System.Text.Ascii.FromUtf16(folded, _utf8, out int n) != System.Buffers.OperationStatus.Done) n = Encoding.UTF8.GetBytes(folded, _utf8);
            if (terms.Add(_utf8.AsSpan(0, n), _doc)) _distinct++;
        }
    }
}

/// <summary>What a written segment holds (KindStats is declared with the store contracts).</summary>
internal sealed record SegmentInfo(uint Id, int DocCount, long Bytes, int[] LocalOfAdded, IReadOnlyDictionary<ContentKind, KindStats> Stats);

/// <summary>
/// The postings buffer of one run: an open-addressing table of terms (bytes in an arena) and, per term, its documents as
/// delta varints in a slice of a second arena; a slice that fills moves to the arena's end at twice the size. Documents
/// arrive in ascending order, so a term's last document tells whether a token repeats in the same document. A term's
/// state is one struct and a table slot holds the hash beside the id, so a lookup touches few cache lines.
/// </summary>
internal sealed class TermBuffer : IDisposable
{
    struct Term
    {
        public int Start, SliceStart, SliceLength, SliceCap, LastDoc, Count;
        public ushort Length;
    }

    const int TermBytes = 28, SlotBytes = 8;

    byte[] _termArena = new byte[1 << 14];
    int _termArenaLength;
    byte[] _post = new byte[1 << 14];
    int _postLength;
    Term[] _info = new Term[256];
    long[] _table = new long[512]; // (hash << 32) | (id + 1); 0 = empty
    int _terms;

    /// <summary>Distinct terms buffered.</summary>
    public int TermCount => _terms;

    /// <summary>Memory the buffer holds (arrays as allocated; they are kept for the next run after a spill).</summary>
    public long Bytes => _termArena.Length + (long)_post.Length + _info.Length * (long)TermBytes + _table.Length * (long)SlotBytes;

    /// <summary>
    /// Bytes in use by the current run. Arrays grow by doubling, so spilling once this reaches half the budget keeps what
    /// they hold (<see cref="Bytes"/>) within the budget.
    /// </summary>
    public long UsedBytes => _termArenaLength + (long)_postLength + _terms * (long)(TermBytes + 2 * SlotBytes);

    /// <summary>Adds a term for a document; false when the document already has it.</summary>
    public bool Add(ReadOnlySpan<byte> term, int doc)
    {
        uint hash = Hash(term);
        int mask = _table.Length - 1;
        int slot = (int)hash & mask;
        while (true)
        {
            long e = _table[slot];
            if (e == 0) break;
            if ((uint)(e >> 32) == hash)
            {
                int id = (int)e - 1;
                ref var t = ref _info[id];
                if (_termArena.AsSpan(t.Start, t.Length).SequenceEqual(term))
                {
                    if (t.LastDoc == doc) return false;
                    Append(ref t, (uint)(doc - t.LastDoc));
                    t.LastDoc = doc;
                    t.Count++;
                    return true;
                }
            }
            slot = (slot + 1) & mask;
        }
        NewTerm(term, hash, slot, doc);
        return true;
    }

    static uint Hash(ReadOnlySpan<byte> term)
    {
        var h = new HashCode();
        h.AddBytes(term);
        return (uint)h.ToHashCode();
    }

    void NewTerm(ReadOnlySpan<byte> term, uint hash, int slot, int doc)
    {
        if (_terms == _info.Length) Array.Resize(ref _info, _info.Length * 2);
        int id = _terms++;
        if (_termArenaLength + term.Length > _termArena.Length) Array.Resize(ref _termArena, Math.Max(_termArena.Length * 2, _termArenaLength + term.Length));
        term.CopyTo(_termArena.AsSpan(_termArenaLength));
        ref var t = ref _info[id];
        t = new Term { Start = _termArenaLength, Length = (ushort)term.Length, Count = 1, LastDoc = doc };
        _termArenaLength += term.Length;
        Append(ref t, (uint)doc);
        _table[slot] = ((long)hash << 32) | (uint)(id + 1);
        if (_terms * 2 > _table.Length) Rehash();
    }

    void Append(ref Term t, uint value)
    {
        int need = VarInt.Size(value);
        if (t.SliceLength + need > t.SliceCap)
        {
            int cap = Math.Max(4, Math.Max(t.SliceCap * 2, t.SliceLength + need));
            if (_postLength + cap > _post.Length) Array.Resize(ref _post, Math.Max(_post.Length * 2, _postLength + cap));
            _post.AsSpan(t.SliceStart, t.SliceLength).CopyTo(_post.AsSpan(_postLength));
            t.SliceStart = _postLength;
            t.SliceCap = cap;
            _postLength += cap;
        }
        t.SliceLength += VarInt.Write(_post.AsSpan(t.SliceStart + t.SliceLength), value);
    }

    void Rehash()
    {
        var old = _table;
        _table = new long[old.Length * 2];
        int mask = _table.Length - 1;
        foreach (long e in old)
        {
            if (e == 0) continue;
            int slot = (int)(uint)(e >> 32) & mask;
            while (_table[slot] != 0) slot = (slot + 1) & mask;
            _table[slot] = e;
        }
    }

    public ReadOnlySpan<byte> TermAt(int id) => _termArena.AsSpan(_info[id].Start, _info[id].Length);
    /// <summary>A term's documents as delta varints, the first absolute.</summary>
    public ReadOnlySpan<byte> PostingsAt(int id) => _post.AsSpan(_info[id].SliceStart, _info[id].SliceLength);
    public int CountAt(int id) => _info[id].Count;

    /// <summary>Term ids in term byte order.</summary>
    public int[] SortedOrder()
    {
        var order = new int[_terms];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, new TermOrder(this));
        return order;
    }

    sealed class TermOrder(TermBuffer b) : IComparer<int>
    {
        public int Compare(int x, int y) => b.TermAt(x).SequenceCompareTo(b.TermAt(y));
    }

    /// <summary>Writes the run in term order: varint length, term, varint count, varint byte length, the delta varints.</summary>
    public void WriteRun(Stream output)
    {
        using var buffer = new ByteBuffer(1 << 16);
        foreach (int id in SortedOrder())
        {
            var term = TermAt(id);
            var postings = PostingsAt(id);
            buffer.WriteVarInt((ulong)term.Length);
            buffer.Write(term);
            buffer.WriteVarInt((ulong)_info[id].Count);
            buffer.WriteVarInt((ulong)postings.Length);
            buffer.Write(postings);
            if (buffer.Length >= 60 << 10)
            {
                output.Write(buffer.Span);
                buffer.Clear();
            }
        }
        output.Write(buffer.Span);
    }

    /// <summary>Empties the buffer (arrays kept for the next run).</summary>
    public void Clear()
    {
        _terms = 0;
        _termArenaLength = 0;
        _postLength = 0;
        Array.Clear(_table);
    }

    public void Dispose()
    {
        _terms = 0;
        _termArena = _post = [];
        _info = [];
        _table = new long[2];
    }
}

/// <summary>Accumulates one term's documents: delta varints plus a skip entry every 128 documents.</summary>
internal sealed class PostingsEncoder : IDisposable
{
    readonly ByteBuffer _stream = new(1 << 12), _skips = new(256);
    int _skipCount;
    long _prevBase, _prevPos;

    public int Count { get; private set; }
    public int First { get; private set; }
    public int Second { get; private set; }
    public int Last { get; private set; }

    public void Clear()
    {
        _stream.Clear();
        _skips.Clear();
        _skipCount = 0;
        _prevBase = _prevPos = 0;
        Count = 0;
        First = Second = Last = -1;
    }

    public void Add(int doc)
    {
        if (Count > 0 && doc <= Last) throw new InvalidOperationException("Postings must ascend.");
        if (Count > 0 && Count % SegmentFormat.SkipEvery == 0)
        {
            _skips.WriteVarInt((ulong)(Last - _prevBase));
            _skips.WriteVarInt((ulong)(_stream.Length - _prevPos));
            _prevBase = Last;
            _prevPos = _stream.Length;
            _skipCount++;
        }
        _stream.WriteVarInt((ulong)(Count == 0 ? doc : doc - Last));
        if (Count == 0) First = doc;
        else if (Count == 1) Second = doc;
        Last = doc;
        Count++;
    }

    /// <summary>Writes [skip table] + documents (only for terms with 3 or more documents).</summary>
    public void WriteTo(SectionWriter w)
    {
        if (Count > SegmentFormat.SkipAbove)
        {
            Span<byte> v = stackalloc byte[VarInt.MaxLength];
            w.Write(v[..VarInt.Write(v, (ulong)_skipCount)]);
            w.Write(_skips.Span);
        }
        w.Write(_stream.Span);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _skips.Dispose();
    }
}

/// <summary>Writes sections to the output, keeping the running CRC of the current one and of the current part of it.</summary>
internal sealed class SectionWriter(Stream output)
{
    readonly long _origin = output.Position;
    long _start;
    uint _crc, _part = Crc32C.Start;

    public long Position => output.Position - _origin;

    public void Begin()
    {
        _start = Position;
        _crc = _part = Crc32C.Start;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _crc = Crc32C.Update(_crc, bytes);
        _part = Crc32C.Update(_part, bytes);
        output.Write(bytes);
    }

    /// <summary>The CRC of what Write got since Begin or the last call, which starts the next part.</summary>
    public uint TakePartCrc()
    {
        uint crc = Crc32C.Finish(_part);
        _part = Crc32C.Start;
        return crc;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => output.Write(bytes);

    public void WriteZeros(int count)
    {
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        while (count > 0)
        {
            int n = Math.Min(count, zeros.Length);
            output.Write(zeros[..n]);
            count -= n;
        }
    }

    public void Copy(Stream source, byte[] buffer)
    {
        source.Flush();
        source.Position = 0;
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0) Write(buffer.AsSpan(0, n));
    }

    public SegmentFormat.TocEntry End(SegmentFormat.Section id) => new(id, _start, Position - _start, Crc32C.Finish(_crc));

    public void Seek(long position) => output.Position = _origin + position;
}

/// <summary>
/// Builds DICT blocks of 64 front-coded terms and their DICTIDX entries (offset, postings base, CRC, postings CRC, first
/// term). postingsCrc gives the CRC of the postings written since the previous block ended: each term's postings are
/// written before it is added, so at a block's end that is exactly the block's run.
/// </summary>
internal sealed class DictBuilder(Stream output, Func<uint> postingsCrc) : IDisposable
{
    readonly ByteBuffer _block = new(4096);
    readonly byte[] _prev = new byte[SegmentFormat.MaxTermBytes];
    int _prevLength, _inBlock;
    long _base, _prevOffset, _written;
    byte[] _first = [];

    public List<SegmentWriter.DictIndexEntry> Index { get; } = [];

    /// <summary>Adds the next term; postingsPosition is where its postings start (or would: the current postings length).</summary>
    public void Add(ReadOnlySpan<byte> term, int docFreq, int first, int second, long postingsPosition)
    {
        if (_inBlock == 0)
        {
            _base = _prevOffset = postingsPosition;
            _first = term.ToArray();
            _prevLength = 0;
        }
        int shared = _inBlock == 0 ? 0 : term.CommonPrefixLength(_prev.AsSpan(0, _prevLength));
        _block.WriteVarInt((ulong)shared);
        _block.WriteVarInt((ulong)(term.Length - shared));
        _block.Write(term[shared..]);
        _block.WriteVarInt((ulong)docFreq);
        if (docFreq <= 2)
        {
            _block.WriteVarInt((ulong)first);
            if (docFreq == 2) _block.WriteVarInt((ulong)(second - first));
        }
        else
        {
            _block.WriteVarInt((ulong)(postingsPosition - _prevOffset));
            _prevOffset = postingsPosition;
        }
        term.CopyTo(_prev);
        _prevLength = term.Length;
        if (++_inBlock == SegmentFormat.DictBlock) Flush();
    }

    void Flush()
    {
        if (_inBlock == 0) return;
        var block = _block.Span;
        Index.Add(new SegmentWriter.DictIndexEntry(_written, _base, Crc32C.Compute(block), postingsCrc(), _first));
        output.Write(block);
        _written += block.Length;
        _block.Clear();
        _inBlock = 0;
    }

    /// <summary>Writes the last, partly filled block.</summary>
    public void Finish() => Flush();

    public void Dispose() => _block.Dispose();
}

/// <summary>One sorted run of (term, documents) entries; runs hold ascending, disjoint document ranges in run order.</summary>
internal abstract class RunCursor(int index) : IDisposable
{
    /// <summary>Run order (a tie on a term goes to the lower index: its documents come first).</summary>
    public int Index { get; } = index;
    public abstract ReadOnlySpan<byte> Term { get; }
    public abstract bool MoveNext();
    /// <summary>Adds the current term's documents to the encoder.</summary>
    public abstract void AppendDocs(PostingsEncoder into);
    public virtual void Dispose() { }
}

/// <summary>The run still in memory (the buffer's last contents).</summary>
internal sealed class MemoryRunCursor(TermBuffer terms, int index) : RunCursor(index)
{
    readonly int[] _order = terms.SortedOrder();
    int _i = -1;

    public override ReadOnlySpan<byte> Term => terms.TermAt(_order[_i]);

    public override bool MoveNext() => ++_i < _order.Length;

    public override void AppendDocs(PostingsEncoder into)
    {
        int id = _order[_i];
        var postings = terms.PostingsAt(id);
        int count = terms.CountAt(id), pos = 0, doc = 0;
        for (int k = 0; k < count; k++)
        {
            if (!VarInt.TryRead(postings, ref pos, out ulong v)) throw new InvalidDataException("A postings buffer is corrupt.");
            doc = k == 0 ? (int)v : doc + (int)v;
            into.Add(doc);
        }
    }
}

/// <summary>A run spilled to a file (or memory stream), read sequentially through a buffer.</summary>
internal sealed class FileRunCursor : RunCursor
{
    readonly Stream _stream;
    readonly byte[] _buffer;
    readonly byte[] _term = new byte[SegmentFormat.MaxTermBytes];
    int _pos, _length, _termLength, _count;
    long _postingBytes;

    public FileRunCursor(Stream stream, int index, int bufferSize) : base(index)
    {
        _stream = stream;
        stream.Flush();
        stream.Position = 0;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(bufferSize, 4096));
    }

    public override ReadOnlySpan<byte> Term => _term.AsSpan(0, _termLength);

    /// <summary>At least need bytes buffered; false at the end of the run with fewer.</summary>
    bool Fill(int need)
    {
        if (_length - _pos >= need) return true;
        if (_pos > 0)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _length - _pos);
            _length -= _pos;
            _pos = 0;
        }
        while (_length < need)
        {
            int n = _stream.Read(_buffer, _length, _buffer.Length - _length);
            if (n <= 0) break;
            _length += n;
        }
        return _length - _pos >= need;
    }

    ulong ReadVarInt()
    {
        Fill(VarInt.MaxLength);
        int p = _pos;
        if (!VarInt.TryRead(_buffer.AsSpan(0, _length), ref p, out ulong v)) throw new InvalidDataException("A spilled run is corrupt.");
        _pos = p;
        return v;
    }

    public override bool MoveNext()
    {
        if (_postingBytes != 0) throw new InvalidOperationException("The previous term's documents weren't read.");
        if (!Fill(1)) return false;
        ulong length = ReadVarInt();
        if (length == 0 || length > SegmentFormat.MaxTermBytes || !Fill((int)length)) throw new InvalidDataException("A spilled run is corrupt.");
        _termLength = (int)length;
        _buffer.AsSpan(_pos, _termLength).CopyTo(_term);
        _pos += _termLength;
        _count = checked((int)ReadVarInt());
        _postingBytes = checked((long)ReadVarInt());
        return true;
    }

    public override void AppendDocs(PostingsEncoder into)
    {
        int doc = 0;
        for (int k = 0; k < _count; k++)
        {
            Fill((int)Math.Min(VarInt.MaxLength, _postingBytes));
            int p = _pos;
            int end = (int)Math.Min(_length, _pos + _postingBytes);
            if (!VarInt.TryRead(_buffer.AsSpan(0, end), ref p, out ulong v)) throw new InvalidDataException("A spilled run is corrupt.");
            _postingBytes -= p - _pos;
            _pos = p;
            doc = k == 0 ? (int)v : checked(doc + (int)v);
            into.Add(doc);
        }
        if (_postingBytes != 0) throw new InvalidDataException("A spilled run is corrupt.");
    }

    public override void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}

/// <summary>A binary min-heap of run cursors by (term, run index).</summary>
internal sealed class RunHeap(int capacity)
{
    readonly RunCursor[] _items = new RunCursor[Math.Max(1, capacity)];

    public int Count { get; private set; }
    public RunCursor Peek => _items[0];

    static bool Less(RunCursor a, RunCursor b)
    {
        int c = a.Term.SequenceCompareTo(b.Term);
        return c < 0 || (c == 0 && a.Index < b.Index);
    }

    public void Push(RunCursor c)
    {
        int i = Count++;
        _items[i] = c;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!Less(_items[i], _items[parent])) break;
            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    public RunCursor Pop()
    {
        var top = _items[0];
        _items[0] = _items[--Count];
        _items[Count] = null!;
        int i = 0;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, m = i;
            if (l < Count && Less(_items[l], _items[m])) m = l;
            if (r < Count && Less(_items[r], _items[m])) m = r;
            if (m == i) break;
            (_items[i], _items[m]) = (_items[m], _items[i]);
            i = m;
        }
        return top;
    }
}
