namespace Anything.Core.Content.Store;

/// <summary>
/// The store at one commit (ref-counted). Segments are in id order (the newest last); each view holds a reference on every
/// segment it lists, so a merged-away segment stays mapped until the last view that lists it is released. The creator
/// (the store) holds one reference and the store's own code takes more with <see cref="TryRetain"/>; holders outside the
/// store never see this object, only a <see cref="StoreLease"/> each (<see cref="Lease"/>), so a holder that disposes
/// once too often can give back only its own reference, never the store's or another holder's.
/// </summary>
internal sealed class StoreView
{
    readonly SegmentReader[] _segments;
    readonly Manifest.SegmentEntry[] _entries;
    int _refs = 1;

    /// <summary>A view over opened segments and their manifest entries (same order); takes a reference on each segment.</summary>
    internal StoreView(long generation, IReadOnlyList<SegmentReader> segments, IReadOnlyList<Manifest.SegmentEntry> entries,
        IReadOnlyList<VolumeEntry> volumes)
    {
        if (segments.Count != entries.Count) throw new ArgumentException("Every segment needs its entry.", nameof(entries));
        CommitGeneration = generation;
        _segments = [.. segments];
        _entries = [.. entries];
        Volumes = [.. volumes];
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Id != _entries[i].Id || _segments[i].DocCount != _entries[i].DocCount)
            {
                for (int k = 0; k < i; k++) _segments[k].Release();
                throw new ArgumentException("A segment doesn't match its entry.", nameof(entries));
            }
            if (!_segments[i].TryRetain())
            {
                for (int k = 0; k < i; k++) _segments[k].Release();
                throw new InvalidOperationException("A segment was released before its view was built.");
            }
        }
        Stats = LiveStats(_segments, _entries);
    }

    /// <summary>An empty view (nothing indexed, or the store is off).</summary>
    internal static StoreView Empty(long generation, IReadOnlyList<VolumeEntry>? volumes = null) => new(generation, [], [], volumes ?? []);

    /// <summary>
    /// Commit generation of this view: the manifest's, or above it when opened (no two views of the process share one; a
    /// commit writes its own generation into the manifest).
    /// </summary>
    public long CommitGeneration { get; }
    public IReadOnlyList<IContentSegment> Segments => _segments;
    /// <summary>The segments as readers (store internals).</summary>
    internal IReadOnlyList<SegmentReader> Readers => _segments;
    /// <summary>The manifest entries of the segments (deletions), same order.</summary>
    internal IReadOnlyList<Manifest.SegmentEntry> Entries => _entries;
    public IReadOnlyList<VolumeEntry> Volumes { get; }
    public IReadOnlyDictionary<ContentKind, KindStats> Stats { get; }

    /// <summary>Live documents in the view.</summary>
    internal long LiveDocs
    {
        get
        {
            long n = 0;
            for (int i = 0; i < _entries.Length; i++) n += _entries[i].DocCount - _entries[i].DeletedCount;
            return n;
        }
    }

    /// <summary>The ordinal of a segment id, or -1.</summary>
    internal int OrdinalOf(uint id)
    {
        for (int i = 0; i < _segments.Length; i++) if (_segments[i].Id == id) return i;
        return -1;
    }

    /// <summary>Deleted, or not a document of this view.</summary>
    public bool IsDeleted(DocRef doc)
        => (uint)doc.Segment >= (uint)_segments.Length || (uint)doc.Doc >= (uint)_entries[doc.Segment].DocCount || _entries[doc.Segment].IsDeleted(doc.Doc);

    /// <summary>The live document for a key (newest segment wins), or null.</summary>
    public DocRef? Find(ulong key)
    {
        for (int s = _segments.Length - 1; s >= 0; s--)
        {
            int doc = _segments[s].Find(key);
            if (doc >= 0 && !_entries[s].IsDeleted(doc)) return new DocRef(s, doc);
        }
        return null;
    }

    /// <summary>Live rows in key order across segments (k-way merge), for reconcile; a key in two segments yields the newest.</summary>
    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey()
    {
        var heap = new PriorityQueue<(int Segment, int Doc), (ulong Key, int Order)>();
        var next = new int[_segments.Length];
        for (int s = 0; s < _segments.Length; s++) Push(heap, next, s);
        bool any = false;
        ulong last = 0;
        while (heap.TryDequeue(out var item, out var priority))
        {
            Push(heap, next, item.Segment);
            if (any && priority.Key == last) continue; // an older copy of a key already yielded from a newer segment
            any = true;
            last = priority.Key;
            yield return (new DocRef(item.Segment, item.Doc), _segments[item.Segment].Row(item.Doc));
        }
    }

    void Push(PriorityQueue<(int Segment, int Doc), (ulong Key, int Order)> heap, int[] next, int s)
    {
        var entry = _entries[s];
        int doc = next[s];
        while (doc < entry.DocCount && entry.IsDeleted(doc)) doc++;
        if (doc >= entry.DocCount)
        {
            next[s] = doc;
            return;
        }
        next[s] = doc + 1;
        // Newest segment first on a tie (higher ordinal = lower order value).
        heap.Enqueue((s, doc), (_segments[s].KeyAt(doc), -s));
    }

    static IReadOnlyDictionary<ContentKind, KindStats> LiveStats(SegmentReader[] segments, Manifest.SegmentEntry[] entries)
    {
        var totals = new Dictionary<ContentKind, KindStats>();
        for (int i = 0; i < segments.Length; i++)
            foreach (var (kind, stats) in segments[i].Stats)
            {
                var live = KindStatsMath.Subtract(stats, entries[i].DeletedStats.GetValueOrDefault(kind));
                totals[kind] = KindStatsMath.Add(totals.GetValueOrDefault(kind), live);
            }
        foreach (var kind in totals.Where(t => t.Value.Docs == 0).Select(t => t.Key).ToList()) totals.Remove(kind);
        return totals;
    }

    /// <summary>Whether every reference was released.</summary>
    internal bool IsReleased => Volatile.Read(ref _refs) <= 0;

    /// <summary>Adds a reference unless the last one is gone (a CAS loop that refuses at 0).</summary>
    internal bool TryRetain()
    {
        for (int refs = Volatile.Read(ref _refs); refs > 0; refs = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        return false;
    }

    /// <summary>A new holder's lease on this view (one more reference); null when the last reference is gone.</summary>
    internal StoreLease? Lease() => TryRetain() ? new StoreLease(this) : null;

    /// <summary>Releases one reference taken by the creator or <see cref="TryRetain"/>; the last one releases the segments.</summary>
    internal void Release()
    {
        int refs = Interlocked.Decrement(ref _refs);
        if (refs == 0) foreach (var s in _segments) s.Release();
        else if (refs < 0) Interlocked.Exchange(ref _refs, 0); // released once too often (the store's bug): never release twice
    }
}

/// <summary>
/// One holder's reference to a store view: what Acquire, AddRef and StoreChange.View hand out. It forwards to the view and
/// gives its one reference back exactly once, however often it is disposed; after that its members throw
/// ObjectDisposedException. TryAddRef counts on this lease (each success needs one more Dispose of it); AddRef hands out a
/// new lease of its own.
/// </summary>
internal sealed class StoreLease : IStoreView
{
    readonly StoreView _view;
    int _refs = 1;

    /// <summary>Takes over one reference on the view.</summary>
    internal StoreLease(StoreView view) => _view = view;

    StoreView Live => Volatile.Read(ref _refs) > 0 ? _view : throw new ObjectDisposedException(nameof(IStoreView), "The store view was released.");

    /// <summary>The view (store internals and tests); throws once the lease is released.</summary>
    internal StoreView View => Live;
    /// <summary>Whether this lease gave its reference back.</summary>
    internal bool IsReleased => Volatile.Read(ref _refs) <= 0;

    /// <summary>The view's commit generation.</summary>
    public long CommitGeneration => Live.CommitGeneration;
    /// <summary>The view's segments, oldest first.</summary>
    public IReadOnlyList<IContentSegment> Segments => Live.Segments;
    /// <summary>The view's volume table.</summary>
    public IReadOnlyList<VolumeEntry> Volumes => Live.Volumes;
    /// <summary>Per-kind totals of the view's live documents.</summary>
    public IReadOnlyDictionary<ContentKind, KindStats> Stats => Live.Stats;
    /// <summary>Deleted, or not a document of the view.</summary>
    public bool IsDeleted(DocRef doc) => Live.IsDeleted(doc);
    /// <summary>The live document for a key (newest segment wins), or null.</summary>
    public DocRef? Find(ulong key) => Live.Find(key);
    /// <summary>Live rows in key order across segments, for reconcile.</summary>
    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey() => Live.RowsByKey();

    /// <summary>A new lease on the same view; the holder disposes it. Throws when this lease is released.</summary>
    public IStoreView AddRef() => Live.Lease() ?? throw new ObjectDisposedException(nameof(IStoreView), "The store view was released.");

    /// <summary>One more reference on this lease unless it is released (a CAS loop that refuses at 0).</summary>
    public bool TryAddRef()
    {
        for (int refs = Volatile.Read(ref _refs); refs > 0; refs = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        return false;
    }

    /// <summary>Gives back one reference of this lease; the last gives the view's reference back, once.</summary>
    public void Dispose()
    {
        int refs = Interlocked.Decrement(ref _refs);
        if (refs == 0) _view.Release();
        else if (refs < 0) Interlocked.Exchange(ref _refs, 0); // disposed once too often: a no-op
    }
}
