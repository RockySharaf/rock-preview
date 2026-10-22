using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// A dictionary-based IContentStore: segments are sorted arrays of rows, deletions are per-segment sets, every commit is
/// a new immutable view. Views count references and throw once released, so lifetime bugs fail loudly. Store invariant:
/// at most one live document per key (Add replaces by key, as the real store does).
/// </summary>
internal sealed class FakeStore : IContentStore
{
    readonly object _gate = new();
    List<FakeSegment> _segments = [];
    Dictionary<uint, HashSet<int>> _deleted = [];
    readonly List<VolumeEntry> _volumes = [];
    uint _nextId = 1;
    long _commit;
    FakeView _current;
    ContentMode _mode;

    public FakeStore() => _current = NewView();

    public bool IsReadOnly { get; set; }
    public bool IsNewerFormat { get; set; }
    bool _unavailable;
    /// <summary>Reading IsUnavailable throws while set (a store fault in every follower iteration).</summary>
    public volatile bool Fault;
    public bool IsUnavailable
    {
        get => Fault ? throw new IOException("The store faulted.") : _unavailable;
        set => _unavailable = value;
    }
    /// <summary>Segments failed their checks: the next commit (an empty one too) drops them.</summary>
    public HashSet<uint> Damaged { get; } = [];
    public bool HasDamage
    {
        get { lock (_gate) return !IsReadOnly && _segments.Any(s => Damaged.Contains(s.Id)); }
    }
    public int ScrubCalls, FinishDeletingCount;
    /// <summary>Scrub calls answer false this many times first (one more segment to verify each time).</summary>
    public int ScrubSegmentsLeft { get; set; }
    public bool Scrub(CancellationToken ct)
    {
        lock (_gate)
        {
            ScrubCalls++;
            if (ScrubSegmentsLeft <= 0) return true;
            ScrubSegmentsLeft--;
            return false;
        }
    }
    /// <summary>Rekey answers null (can't now: no room, a large segment while busy).</summary>
    public bool RekeyDeclines { get; set; }
    public int RekeyCount;

    /// <summary>As the real store: the segment rewritten with the moved documents under their new rows and paths.</summary>
    public StoreChange? Rekey(uint segment, IReadOnlyList<(int Doc, DocRow Row, string Path)> moves, bool allowLarge, CancellationToken ct,
        Action<CancellationToken>? pace = null)
    {
        lock (_gate)
        {
            if (IsReadOnly) throw new InvalidOperationException("read-only");
            if (RekeyDeclines) return null;
            pace?.Invoke(ct);
            var seg = _segments.FirstOrDefault(s => s.Id == segment);
            if (seg == null || Damaged.Contains(segment)) return null;
            var gone = _deleted.TryGetValue(segment, out var d) ? d : [];
            var deleted = _deleted.ToDictionary(p => p.Key, p => new HashSet<int>(p.Value));
            var local = Enumerable.Repeat(-1, moves.Count).ToArray();
            var moving = new Dictionary<int, int>(); // doc → move index
            for (int i = 0; i < moves.Count; i++)
                if (!gone.Contains(moves[i].Doc)) moving[moves[i].Doc] = i;
            // A live document elsewhere under a moved document's new key is deleted, as in a batch.
            using (var view = _current.AddRef())
                foreach (var i in moving.Values)
                    if (view.Find(moves[i].Row.Key) is { } other && view.Segments[other.Segment].Id != segment)
                    {
                        uint id = view.Segments[other.Segment].Id;
                        if (!deleted.TryGetValue(id, out var set)) deleted[id] = set = [];
                        set.Add(other.Doc);
                    }
            var entries = new List<(DocRow Row, string Path, StoredDoc Doc, int Old, int Move)>();
            for (int doc = 0; doc < seg.DocCount; doc++)
            {
                if (gone.Contains(doc)) continue;
                if (moving.TryGetValue(doc, out int m)) entries.Add((moves[m].Row, moves[m].Path, seg.Docs[doc], doc, m));
                else entries.Add((seg.Rows[doc], seg.Paths[doc], seg.Docs[doc], doc, -1));
            }
            entries.Sort((a, b) => a.Row.Key.CompareTo(b.Row.Key));
            var rewritten = new FakeSegment(_nextId++, [.. entries.Select(e => e.Row)], [.. entries.Select(e => e.Path)], [.. entries.Select(e => e.Doc)], Clock().ToFileTimeUtc());
            var oldToNew = Enumerable.Repeat(-1, seg.DocCount).ToArray();
            for (int k = 0; k < entries.Count; k++)
            {
                if (entries[k].Move >= 0) local[entries[k].Move] = k;
                else oldToNew[entries[k].Old] = k;
            }
            var list = new List<FakeSegment>(_segments);
            list[list.IndexOf(seg)] = rewritten;
            deleted.Remove(segment);
            _segments = list;
            _deleted = deleted;
            RekeyCount++;
            var current = Republish();
            return new StoreChange(current.AddRef(), rewritten.Id, local, [(segment, oldToNew)], [segment]);
        }
    }
    public long BytesOnDisk => _segments.Sum(s => s.DocCount * 100L);
    public ContentMode RecordedMode => _mode;
    public int CommitCount, MergeCount, DeleteAllCount, Rewrites;
    /// <summary>Merge when this many segments exist (0 = never).</summary>
    public int MergeAt { get; set; }
    /// <summary>DeleteAll answers false this many times first.</summary>
    public int DeleteAllFailures { get; set; }
    public bool Disposed { get; private set; }
    /// <summary>When segments are written (their CreatedUtc); the harness sets its clock.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public IStoreView Acquire()
    {
        lock (_gate) return _current.AddRef();
    }

    public void RecordMode(ContentMode mode) => _mode = mode;

    public ushort VolumeIndex(string volumeKey, string displayRoot)
    {
        lock (_gate)
        {
            foreach (var v in _volumes) if (string.Equals(v.Key, volumeKey, StringComparison.OrdinalIgnoreCase)) return v.Index;
            var e = new VolumeEntry((ushort)_volumes.Count, volumeKey, displayRoot, DateTime.MinValue);
            _volumes.Add(e);
            return e.Index;
        }
    }

    public void SeenVolume(ushort volume, DateTime utc)
    {
        lock (_gate)
            for (int i = 0; i < _volumes.Count; i++)
                if (_volumes[i].Index == volume) _volumes[i] = _volumes[i] with { LastSeenUtc = utc };
    }

    /// <summary>A volume's key by its index.</summary>
    public string VolumeKey(ushort volume)
    {
        lock (_gate) return _volumes.FirstOrDefault(v => v.Index == volume)?.Key ?? "?";
    }

    /// <summary>Sets a volume's last-seen time (tests of the 90-day purge).</summary>
    public void SetLastSeen(ushort volume, DateTime utc)
    {
        lock (_gate)
        {
            for (int i = 0; i < _volumes.Count; i++)
                if (_volumes[i].Index == volume) _volumes[i] = _volumes[i] with { LastSeenUtc = utc };
            Republish();
        }
    }

    public IStoreBatch BeginBatch()
    {
        if (IsReadOnly) throw new InvalidOperationException("read-only");
        return new FakeBatch(this, Acquire());
    }

    public MergePlan? PlanMerge(bool allowLarge)
    {
        lock (_gate)
        {
            if (MergeAt <= 0 || _segments.Count < MergeAt) return null;
            var pick = _segments.OrderBy(s => s.DocCount).Take(MergeAt).Select(s => s.Id).ToList();
            return new MergePlan(pick, _segments.Where(s => pick.Contains(s.Id)).Sum(s => s.DocCount), Large: false);
        }
    }

    public StoreChange Merge(MergePlan plan, CancellationToken ct, Action<CancellationToken>? pace = null)
    {
        lock (_gate)
        {
            pace?.Invoke(ct);
            var inputs = _segments.Where(s => plan.Segments.Contains(s.Id)).ToList();
            var docs = new List<(DocRow Row, string Path, StoredDoc Doc, uint From, int Local)>();
            foreach (var seg in inputs)
                for (int i = 0; i < seg.DocCount; i++)
                    if (!_deleted.TryGetValue(seg.Id, out var d) || !d.Contains(i)) docs.Add((seg.Rows[i], seg.Paths[i], seg.Docs[i], seg.Id, i));
            docs.Sort((a, b) => a.Row.Key.CompareTo(b.Row.Key));
            var merged = new FakeSegment(_nextId++, [.. docs.Select(d => d.Row)], [.. docs.Select(d => d.Path)], [.. docs.Select(d => d.Doc)], Clock().ToFileTimeUtc());
            var remaps = new List<(uint, int[])>();
            foreach (var seg in inputs)
            {
                var map = Enumerable.Repeat(-1, seg.DocCount).ToArray();
                remaps.Add((seg.Id, map));
            }
            for (int k = 0; k < docs.Count; k++)
                remaps.First(r => r.Item1 == docs[k].From).Item2[docs[k].Local] = k;
            var list = _segments.Where(s => !plan.Segments.Contains(s.Id)).ToList();
            int at = _segments.FindIndex(s => plan.Segments.Contains(s.Id));
            list.Insert(Math.Min(Math.Max(0, at), list.Count), merged);
            _segments = list;
            foreach (var seg in inputs) _deleted.Remove(seg.Id);
            MergeCount++;
            var view = Republish();
            return new StoreChange(view.AddRef(), merged.Id, [], remaps, [.. plan.Segments]);
        }
    }

    bool _deletePending;

    /// <summary>
    /// Empties the store at once (as the real one publishes an empty view); fails while DeleteAllFailures says so, leaving
    /// files for FinishDeleting. A store open read-only (in use) throws and deletes nothing, as the real one does; one made
    /// by a newer version holds the lock, so it deletes and may be written again.
    /// </summary>
    public bool DeleteAll()
    {
        lock (_gate)
        {
            if (IsReadOnly && !IsNewerFormat) throw new InvalidOperationException("The text index is open read-only.");
            if (IsNewerFormat) IsReadOnly = IsNewerFormat = false;
            DeleteAllCount++;
            _segments = [];
            _deleted = [];
            _volumes.Clear();
            Republish();
            if (DeleteAllFailures > 0) { DeleteAllFailures--; _deletePending = true; return false; }
            _deletePending = false;
            return true;
        }
    }

    public bool FinishDeleting()
    {
        lock (_gate)
        {
            FinishDeletingCount++;
            // As the real store: a read-only one says only whether anything is pending (nothing is, it never started).
            if (IsReadOnly) return !_deletePending;
            if (!_deletePending) return true;
            if (DeleteAllFailures > 0) { DeleteAllFailures--; return false; }
            _deletePending = false;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Disposed) return;
            Disposed = true;
            _current.Dispose();
        }
    }

    /// <summary>Reopens after a Dispose (the follower disposes its store when it stops; a later index opens it again).</summary>
    public FakeStore Reopen()
    {
        lock (_gate)
        {
            if (Disposed)
            {
                Disposed = false;
                _current = NewView();
            }
            // An open while a file is in use is read-only for its session; the next one, once nothing holds it, isn't.
            if (UnavailableOpens > 0)
            {
                UnavailableOpens--;
                IsReadOnly = IsUnavailable = true;
            }
            else if (IsUnavailable) IsReadOnly = IsUnavailable = false;
            return this;
        }
    }

    /// <summary>Opens that find a file in use (IsUnavailable, read-only for that session) before one that doesn't.</summary>
    public int UnavailableOpens { get; set; }

    /// <summary>Commits throw this many times first (a full disk).</summary>
    public int CommitFailures { get; set; }

    /// <summary>
    /// As the real store does for small segments (A4): a commit whose copies move 30% or more of a segment's live documents
    /// rewrites that segment into the new one, and the change carries its remap (old to new local numbers).
    /// </summary>
    public bool RewriteCopiedSegments { get; set; }

    /// <summary>Commits tried, failed ones included.</summary>
    public int CommitAttempts { get; private set; }

    internal StoreChange Commit(FakeView from, List<(DocRow Row, string Path, StoredDoc Doc)> adds, List<DocRef> deletes, List<DocRef?>? copies = null)
    {
        lock (_gate)
        {
            CommitAttempts++;
            if (CommitFailures > 0)
            {
                CommitFailures--;
                throw new IOException("There is not enough space on the disk.");
            }
            if (!ReferenceEquals(from.Segments, _segments) && from.CommitGeneration != _commit)
                throw new InvalidOperationException("A batch committed over a newer view (a second writer?)");
            var dropped = _segments.Where(s => Damaged.Contains(s.Id)).Select(s => s.Id).ToList();
            var deleted = _deleted.ToDictionary(p => p.Key, p => new HashSet<int>(p.Value));
            void Del(uint seg, int doc)
            {
                if (!deleted.TryGetValue(seg, out var set)) deleted[seg] = set = [];
                set.Add(doc);
            }
            foreach (var d in deletes)
            {
                if ((uint)d.Segment >= (uint)from.Segments.Count) throw new ArgumentOutOfRangeException(nameof(deletes), "a DocRef outside the batch's view");
                Del(from.Segments[d.Segment].Id, d.Doc);
            }
            var local = new int[adds.Count];
            uint? newId = null;
            var list = new List<FakeSegment>(_segments.Where(s => !dropped.Contains(s.Id)));
            foreach (uint d in dropped) { deleted.Remove(d); Damaged.Remove(d); }
            var remaps = new List<(uint, int[])>();
            if (adds.Count > 0)
            {
                var keys = adds.Select(a => a.Row.Key).ToList();
                if (keys.Distinct().Count() != keys.Count) throw new InvalidOperationException("two documents with one key in a batch");
                foreach (var a in adds)
                    if (from.Find(a.Row.Key) is { } old) Del(from.Segments[old.Segment].Id, old.Doc);
                // Segments the copies mostly move: their documents still live after this commit go into the new segment too.
                var rewrite = new List<FakeSegment>();
                if (RewriteCopiedSegments && copies != null)
                    foreach (var g in copies.Where(c => c != null).GroupBy(c => c!.Value.Segment))
                    {
                        var seg0 = (FakeSegment)from.Segments[g.Key];
                        int live = seg0.DocCount - (_deleted.TryGetValue(seg0.Id, out var dd) ? dd.Count : 0);
                        if (list.Contains(seg0) && g.Count() * 10 >= live * 3) rewrite.Add(seg0);
                    }
                var entries = adds.Select((a, i) => (Row: FixFlags(a.Row, a.Doc), a.Path, a.Doc, Add: i, From: 0u, Local: -1)).ToList();
                foreach (var seg0 in rewrite)
                    for (int i = 0; i < seg0.DocCount; i++)
                        if (!deleted.TryGetValue(seg0.Id, out var gone) || !gone.Contains(i)) entries.Add((seg0.Rows[i], seg0.Paths[i], seg0.Docs[i], -1, seg0.Id, i));
                entries.Sort((a, b) => a.Row.Key.CompareTo(b.Row.Key));
                var seg = new FakeSegment(_nextId++, [.. entries.Select(e => e.Row)], [.. entries.Select(e => e.Path)], [.. entries.Select(e => e.Doc)], Clock().ToFileTimeUtc());
                foreach (var seg0 in rewrite)
                {
                    var oldToNew = Enumerable.Repeat(-1, seg0.DocCount).ToArray();
                    remaps.Add((seg0.Id, oldToNew));
                    list.Remove(seg0);
                    deleted.Remove(seg0.Id);
                    dropped.Add(seg0.Id);
                }
                for (int k = 0; k < entries.Count; k++)
                {
                    if (entries[k].Add >= 0) local[entries[k].Add] = k;
                    else remaps.First(r => r.Item1 == entries[k].From).Item2[entries[k].Local] = k;
                }
                list.Add(seg);
                newId = seg.Id;
                if (rewrite.Count > 0) Rewrites++;
            }
            _segments = list;
            _deleted = deleted;
            CommitCount++;
            var view = Republish();
            return new StoreChange(view.AddRef(), newId, local, remaps, dropped);
        }
    }

    static DocRow FixFlags(DocRow row, StoredDoc doc)
        => row with { Flags = ContentTokenizer.HasWords(doc.Body) ? row.Flags | DocFlags.HasText : row.Flags & ~DocFlags.HasText };

    FakeView Republish()
    {
        var old = _current;
        _current = NewView();
        old.Dispose();
        return _current;
    }

    FakeView NewView() => new(++_commit, _segments, _deleted.ToDictionary(p => p.Key, p => (IReadOnlySet<int>)new HashSet<int>(p.Value)), [.. _volumes]);

    /// <summary>Live documents now (tests' ground truth checks).</summary>
    public List<(DocRow Row, string Path, StoredDoc Doc)> Live()
    {
        using var v = Acquire();
        var result = new List<(DocRow, string, StoredDoc)>();
        foreach (var (doc, row) in v.RowsByKey())
        {
            var seg = (FakeSegment)v.Segments[doc.Segment];
            result.Add((row, seg.Paths[doc.Doc], seg.Docs[doc.Doc]));
        }
        return result;
    }
}

internal sealed class FakeSegment(uint id, DocRow[] rows, string[] paths, StoredDoc[] docs, long created) : IContentSegment
{
    public DocRow[] Rows => rows;
    public string[] Paths => paths;
    public StoredDoc[] Docs => docs;
    public uint Id => id;
    public int DocCount => rows.Length;
    public int TokenizerVersion => ContentTokenizer.Version;
    public long CreatedUtc { get; set; } = created;
    public DocRow Row(int doc) => rows[doc];
    public int Find(ulong key)
    {
        int lo = 0, hi = rows.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (rows[mid].Key == key) return mid;
            if (rows[mid].Key < key) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }
    public void Terms(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, TermVisitor visit, CancellationToken ct) { }
    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermPostings postings) { postings = default; return false; }
    public void ReadPostings(in TermPostings postings, int segmentOrdinal, DocSetBuilder into, CancellationToken ct) { }
    public StoredDoc? ReadStored(int doc) => docs[doc];
    public ReadOnlyMemory<byte> ReadStoredRaw(int doc) => default;
    public string? ReadPath(int doc) => paths[doc];
    public int PictureCount => docs.Count(d => d.Picture != null);
    public (int Doc, PictureInfo Info) Picture(int i)
    {
        int seen = 0;
        for (int d = 0; d < docs.Length; d++)
            if (docs[d].Picture is { } p && seen++ == i) return (d, p);
        throw new ArgumentOutOfRangeException(nameof(i));
    }
    public bool TryGetPicture(int doc, out PictureInfo info)
    {
        info = docs[doc].Picture ?? default;
        return docs[doc].Picture != null;
    }
}

internal sealed class FakeView(long commit, List<FakeSegment> segments, Dictionary<uint, IReadOnlySet<int>> deleted, List<VolumeEntry> volumes) : IStoreView
{
    int _refs = 1;
    public bool Released => Volatile.Read(ref _refs) <= 0;
    void Alive() { if (Released) throw new ObjectDisposedException("FakeView", "A released store view was used."); }

    public long CommitGeneration => commit;
    public IReadOnlyList<IContentSegment> Segments { get { Alive(); return segments; } }
    public IReadOnlyList<VolumeEntry> Volumes { get { Alive(); return volumes; } }
    public bool IsDeleted(DocRef doc)
    {
        Alive();
        return deleted.TryGetValue(segments[doc.Segment].Id, out var d) && d.Contains(doc.Doc);
    }
    public DocRef? Find(ulong key)
    {
        Alive();
        for (int s = segments.Count - 1; s >= 0; s--)
        {
            int d = segments[s].Find(key);
            if (d >= 0 && !IsDeleted(new DocRef(s, d))) return new DocRef(s, d);
        }
        return null;
    }
    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey()
    {
        Alive();
        // One segment and nothing deleted (the perf tests' store): streamed in order, no copy.
        if (segments.Count == 1 && !deleted.Values.Any(d => d.Count > 0)) return Stream(segments[0]);
        var all = new List<(DocRef, DocRow)>();
        for (int s = 0; s < segments.Count; s++)
            for (int d = 0; d < segments[s].DocCount; d++)
                if (!IsDeleted(new DocRef(s, d))) all.Add((new DocRef(s, d), segments[s].Row(d)));
        all.Sort((a, b) => a.Item2.Key.CompareTo(b.Item2.Key));
        return all;
    }
    static IEnumerable<(DocRef Ref, DocRow Row)> Stream(FakeSegment seg)
    {
        var rows = seg.Rows;
        for (int d = 0; d < rows.Length; d++) yield return (new DocRef(0, d), rows[d]);
    }

    public IReadOnlyDictionary<ContentKind, KindStats> Stats
    {
        get
        {
            var d = new Dictionary<ContentKind, KindStats>();
            foreach (var (_, row) in RowsByKey())
            {
                var k = d.GetValueOrDefault(row.Kind);
                d[row.Kind] = k with { Docs = k.Docs + 1, StoredBytes = k.StoredBytes + 1000, Pairs = k.Pairs + 100, MsTotal = k.MsTotal + 10, BytesRead = k.BytesRead + 5000 };
            }
            return d;
        }
    }
    public IStoreView AddRef() => TryAddRef() ? this : throw new ObjectDisposedException("FakeView");
    public bool TryAddRef()
    {
        for (int r = Volatile.Read(ref _refs); r > 0; r = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return true;
        return false;
    }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refs) < 0) throw new InvalidOperationException("A store view was released once too often.");
    }
}

internal sealed class FakeBatch(FakeStore store, IStoreView view) : IStoreBatch
{
    readonly List<(DocRow Row, string Path, StoredDoc Doc)> _adds = [];
    readonly List<DocRef?> _copies = [];
    readonly List<DocRef> _deletes = [];
    bool _done;
    public int Count => _adds.Count;
    public long RawBytes => _adds.Sum(a => (long)a.Doc.Body.Length);
    public int Add(in DocRow row, string relativePath, StoredDoc doc, long bytesRead = 0, int elapsedMs = 0)
    {
        _adds.Add((row, relativePath, doc));
        _copies.Add(null);
        return _adds.Count - 1;
    }
    public int AddCopy(DocRef source, in DocRow row, string relativePath)
    {
        var seg = view.Segments[source.Segment];
        if (view.IsDeleted(source)) throw new InvalidOperationException("AddCopy of a deleted document");
        _adds.Add((row, relativePath, seg.ReadStored(source.Doc)!));
        _copies.Add(source);
        return _adds.Count - 1;
    }
    public void Delete(DocRef doc) => _deletes.Add(doc);
    public StoreChange Commit()
    {
        if (_done) throw new InvalidOperationException("committed twice");
        _done = true;
        return store.Commit((FakeView)view, _adds, _deletes, _copies);
    }
    public void Dispose()
    {
        if (view is FakeView { Released: false }) view.Dispose();
    }
}

/// <summary>The files on a fake disk: path → size, date, words; counts reads; per-path behaviour hooks.</summary>
internal sealed class FakeFiles : IContentSource
{
    readonly object _gate = new();
    readonly Dictionary<string, (long Size, long Modified, string Body, FileAttributes Attributes)> _files = new(StringComparer.OrdinalIgnoreCase);
    public int Reads, Stats;
    public readonly List<string> ReadPaths = [];
    /// <summary>Overrides a read (return null to read normally).</summary>
    public Func<ExtractRequest, ContentRead?>? OnRead { get; set; }
    /// <summary>Roots (prefixes) that don't answer: reads come back Offline.</summary>
    public HashSet<string> OfflineRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Put(string path, long size, long modified, string body, FileAttributes attributes = FileAttributes.Archive)
    {
        lock (_gate) _files[path] = (size, modified, body, attributes);
    }

    public void Remove(string path)
    {
        lock (_gate) _files.Remove(path);
    }

    public void Move(string from, string to)
    {
        lock (_gate)
        {
            if (_files.Remove(from, out var f)) _files[to] = f;
        }
    }

    public bool Has(string path) { lock (_gate) return _files.ContainsKey(path); }

    public (long Size, long Modified, string Body, FileAttributes Attributes) Get(string path) { lock (_gate) return _files[path]; }

    public ContentRead Read(ExtractRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OnRead?.Invoke(request) is { } custom) { lock (_gate) { Reads++; ReadPaths.Add(request.FullPath); } return custom; }
        lock (_gate)
        {
            Reads++;
            ReadPaths.Add(request.FullPath);
            if (OfflineRoots.Any(r => request.FullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), request.ExpectedSize, request.ExpectedModifiedUtc, false, Offline: true);
            if (!_files.TryGetValue(request.FullPath, out var f))
                return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), -1, 0, Retry: true, false);
            var doc = new ExtractedDoc { Kind = request.Kind, Status = f.Body.Length == 0 ? DocStatus.NoText : DocStatus.Ok, Body = f.Body, ExtractorVersion = 0, BytesRead = Math.Min(f.Size, 4096), ElapsedMs = 1 };
            return new ContentRead(doc, f.Size, f.Modified, false, false);
        }
    }

    public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
    {
        lock (_gate)
        {
            Stats++;
            if (_files.TryGetValue(fullPath, out var f))
            {
                size = f.Size;
                modifiedUtc = f.Modified;
                attributes = f.Attributes;
                return true;
            }
        }
        size = -1;
        modifiedUtc = 0;
        attributes = 0;
        return false;
    }
}

internal sealed class FakeGate : IRootGate
{
    readonly Dictionary<string, RootGateState?> _states = new(StringComparer.OrdinalIgnoreCase);
    public RootGateState? Default { get; set; } = new RootGateState(RootStatus.Ready, true, true);
    public int Refreshes;
    public void Set(string root, RootGateState? state) { lock (_states) _states[root] = state; }
    public void Set(string root, RootStatus status) => Set(root, new RootGateState(status, true, status == RootStatus.Ready));
    public RootGateState? StateOf(string rootName)
    {
        lock (_states) return _states.TryGetValue(rootName, out var s) ? s : Default;
    }
    public void Refresh() => Refreshes++;
}

internal sealed class FakePower : IPowerPolicy
{
    public PowerState State { get; set; } = PowerState.Unlimited;
    public PowerState Now(string contentFolder) => State;
}

internal sealed class FakeNames(FileIndex index) : IIndexManager
{
    public FileIndex Index { get; } = index;
    public IndexState State { get; set; } = IndexState.Ready;
    public string StatusText => "";
    public event Action? StateChanged;
    public bool IsElevated { get; set; }
    public bool UsesService => false;
    public IReadOnlyList<IndexedRootInfo> Roots { get; set; } = [];
    public int GetRootsCalls;
    /// <summary>Runs inside GetRoots (the real one counts every entry under the index's read lock).</summary>
    public Action? OnGetRoots { get; set; }
    public void Start(IndexSettings settings) { }
    public void Rebuild() { }
    public void Save() { }
    public IReadOnlyList<IndexedRootInfo> GetRoots()
    {
        Interlocked.Increment(ref GetRootsCalls);
        OnGetRoots?.Invoke();
        return Roots;
    }
    public void Raise() => StateChanged?.Invoke();
    public void Dispose() { }
}

/// <summary>A test clock: UTC, moved by hand.</summary>
internal sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => Now += by;
    public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
}

/// <summary>
/// A FileIndex, a fake disk and a content index over them, driven by hand: ManualLoop (no threads), a fake clock and
/// volume keys "path:C:" (or the share's own for UNC roots), so keys are the same on every PC.
/// </summary>
internal sealed class Harness : IDisposable
{
    public readonly FileIndex Index = new();
    public readonly FakeFiles Files = new();
    public readonly FakeGate Gate = new();
    public readonly FakePower Power = new();
    public readonly FakeClock Clock = new();
    public readonly FakeNames Names;
    public FakeStore Store;
    public readonly ContentIndex Content;
    public readonly ContentSettings Settings;
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    public int StoreOpens;

    public Harness(ContentSettings? settings = null, FakeStore? store = null, ContentIndexOptions? options = null, Action<Harness>? before = null,
        Func<FakeNames, IRootGate>? gate = null)
    {
        Names = new FakeNames(Index);
        Store = store ?? new FakeStore();
        Store.Clock = () => Clock.Now;
        Settings = settings ?? DefaultSettings();
        before?.Invoke(this);
        var o = (options ?? new ContentIndexOptions()) with
        {
            ManualLoop = true,
            Clock = () => Clock.Now,
            VolumeResolver = options?.VolumeResolver ?? Resolve,
            RecentItems = () => Recent,
            ProbeRoot = (_, _) => ProbeResult,
        };
        RootGate = gate?.Invoke(Names);
        Content = new ContentIndex(() =>
        {
            int n = ++StoreOpens;
            if (FailOpen?.Invoke(n) == true) throw new IOException("in use by another process");
            return Store.Reopen();
        }, Files, RootGate ?? Gate, Power, o);
    }

    /// <summary>The gate given instead of <see cref="Gate"/> (the real RootGate), if any.</summary>
    public readonly IRootGate? RootGate;

    /// <summary>Whether the n-th store open (1-based) fails (a scanner holds the folder).</summary>
    public Func<int, bool>? FailOpen { get; set; }

    public List<string> Recent { get; set; } = [];
    public int ProbeResult { get; set; }

    public static ContentSettings DefaultSettings() => new()
    {
        Mode = ContentMode.Documents,
        ModeExplicit = true,
        UseMyFolders = false,
        Folders = [@"C:\Users\Me"],
    };

    /// <summary>Volume keys without Win32: "path:X:" for a drive letter, the share's own key for a UNC root.</summary>
    public static (string Key, string RelativePrefix, string DisplayRoot) Resolve(string path)
    {
        string p = path.TrimEnd('\\');
        if (VolumeKeysAccess.TryUnc(p, out var unc)) return unc;
        string root = p.Length >= 2 && p[1] == ':' ? p[..2].ToUpperInvariant() : p;
        string rest = p.Length > 2 ? p[2..].Trim('\\') : "";
        return ("path:" + root, rest, root + "\\");
    }

    public ContentFollower Follower => Content.Follower ?? throw new InvalidOperationException("No follower: the mode is off.");

    public void Start()
    {
        Content.Start(Names, Settings);
    }

    /// <summary>Runs the follower until it has nothing to do now.</summary>
    public int Run(int max = 10_000) => Follower.RunUntilIdle(max);

    /// <summary>Lets time pass (settle, grace, commit timers) and runs the follower each step.</summary>
    public void Pass(int ms, int step = 1000)
    {
        for (int t = 0; t < ms; t += step)
        {
            Clock.Advance(Math.Min(step, ms - t));
            Run();
        }
        Run();
    }

    /// <summary>Lets everything settle: 12 s in 1 s steps (settle 5 s, grace 10 s, commit 2 s).</summary>
    public void Settle() => Pass(12_000);

    // ── the index and the disk together ──

    public int Folder(string path)
    {
        if (_ids.TryGetValue(path, out int existing) && Index.IsAlive(existing)) return existing;
        int cut = path.LastIndexOf('\\');
        int id = IsRoot(path) ? Index.AddRoot(path) : Index.Add(path[(cut + 1)..], Folder(path[..cut]), FileAttributes.Directory, -1, 0, 0);
        _ids[path] = id;
        return id;
    }

    /// <summary>"C:" or "\\server\share".</summary>
    public static bool IsRoot(string path)
        => path.Length == 2 && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal) && path[2..].Count(c => c == '\\') == 1;

    /// <summary>The root of a path ("C:", "\\nas\share").</summary>
    public static string RootOf(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return path[..2];
        int second = path.IndexOf('\\', path.IndexOf('\\', 2) + 1);
        return second < 0 ? path : path[..second];
    }

    long _time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    public long NextTime() => _time += 10_000_000;

    public int File(string path, string body = "words here", long? size = null, long? modified = null, FileAttributes attributes = FileAttributes.Archive)
    {
        int cut = path.LastIndexOf('\\');
        long s = size ?? body.Length + 10, m = modified ?? NextTime();
        Files.Put(path, s, m, body, attributes);
        int id = Index.Add(path[(cut + 1)..], Folder(path[..cut]), attributes, s, m, 0);
        _ids[path] = id;
        Index.RaiseChanged();
        return id;
    }

    public int Id(string path) => _ids[path];

    /// <summary>Puts a file the disk still has back into the index with its own size and date (a rescan, a re-plug).</summary>
    public int Readd(string path)
    {
        var (size, modified, _, attributes) = Files.Get(path);
        int cut = path.LastIndexOf('\\');
        int id = Index.Add(path[(cut + 1)..], Folder(path[..cut]), attributes, size, modified, 0);
        _ids[path] = id;
        Index.RaiseChanged();
        return id;
    }

    public void Change(string path, string body)
    {
        long m = NextTime();
        Files.Put(path, body.Length + 10, m, body);
        Index.Update(_ids[path], size: body.Length + 10, modifiedFileTimeUtc: m);
    }

    public void Delete(string path)
    {
        Files.Remove(path);
        Index.Remove(_ids[path]);
    }

    public void Rename(string from, string to)
    {
        int id = _ids[from];
        int cut = to.LastIndexOf('\\');
        int parent = Folder(to[..cut]);
        var below = _ids.Keys.Where(k => k.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        Files.Move(from, to);
        foreach (var path in below) Files.Move(path, to + path[from.Length..]);
        Index.Rename(id, to[(cut + 1)..], parent);
        _ids.Remove(from);
        _ids[to] = id;
        foreach (var key in below)
        {
            int child = _ids[key];
            _ids.Remove(key);
            _ids[to + key[from.Length..]] = child;
        }
    }

    /// <summary>Paths the harness knows below a folder.</summary>
    public List<string> Under(string folder) => [.. _ids.Keys.Where(k => k.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))];

    /// <summary>Drops a path's id (its entry was removed with its root, or the index was cleared).</summary>
    public void Forget(string path) => _ids.Remove(path);

    /// <summary>Whether the harness has an id for a path.</summary>
    public bool Knows(string path) => _ids.ContainsKey(path);

    /// <summary>Remaps the harness's ids after a Compact.</summary>
    public int[] Compact()
    {
        var remap = Index.Compact();
        foreach (var key in _ids.Keys.ToList())
        {
            int id = _ids[key];
            if ((uint)id < (uint)remap.Length && remap[id] >= 0) _ids[key] = remap[id];
            else _ids.Remove(key);
        }
        return remap;
    }

    public void ForgetIds() => _ids.Clear();

    // ── what the content index holds ──

    public static ulong Key(string path)
    {
        var (key, rel, _) = Resolve(path);
        return ContentKeys.Of(key, rel);
    }

    public DocRow? Row(string path)
    {
        ulong key = Key(path);
        foreach (var (row, _, _) in Store.Live()) if (row.Key == key) return row;
        return null;
    }

    public StoredDoc? Doc(string path)
    {
        ulong key = Key(path);
        foreach (var (row, _, doc) in Store.Live()) if (row.Key == key) return doc;
        return null;
    }

    /// <summary>The FileIndex id the published map binds a path's document to (-1 = none).</summary>
    public int BoundId(string path)
    {
        using var view = Content.AcquireView();
        if (view == null) return -1;
        if (view.Store.Find(Key(path)) is not { } doc) return -1;
        var map = Content.MapFor(Index, view);
        return map?.FileId(view.Store.Segments[doc.Segment].Id, doc.Doc) ?? -1;
    }

    public void Dispose()
    {
        Content.Stop(TimeSpan.FromMilliseconds(500));
    }
}

internal static class VolumeKeysAccess
{
    public static bool TryUnc(string path, out (string Key, string RelativePrefix, string DisplayRoot) result) => VolumeKeys.TryUnc(path, out result);
}
