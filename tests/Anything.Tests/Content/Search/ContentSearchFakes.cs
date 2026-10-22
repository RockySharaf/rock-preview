using System.Text;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Anything.Tests.Search;

namespace Anything.Tests.Content.Search;

/// <summary>
/// Heavy or timing-sensitive search tests (300K-document fakes, lookups sleeping on purpose, many threads): they run
/// alone, after the parallel tests, so neither their timings nor other tests' timings depend on each other's load.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ContentSearchSerial
{
    public const string Name = "Content search, alone";
}

/// <summary>One document of a fake segment: its row, stored text and volume-relative path.</summary>
internal sealed record FakeDoc(string Path, DocRow Row, StoredDoc Stored);

/// <summary>
/// An in-memory segment whose dictionary is built with ContentTokenizer from the stored text, as the real writer does
/// (body words, field words plain and field-scoped), or a real segment of a ContentStore passed through (the integration
/// fixture): either way it counts decompressions and dictionary scans, and lets tests slow down or interrupt reads.
/// </summary>
internal sealed class FakeSegment : IContentSegment
{
    readonly FakeDoc[] _docs;
    readonly byte[][] _terms = [];
    readonly int[][] _postings = [];
    readonly (int Doc, PictureInfo Info)[] _pictures = [];
    IContentSegment? _inner;
    int? _tokenizerVersion;
    int _reads, _termScans;

    public FakeSegment(uint id, IReadOnlyList<FakeDoc> docs)
    {
        Id = id;
        _docs = [.. docs];
        var byTerm = new SortedDictionary<byte[], List<int>>(ByteOrder.Instance);
        for (int d = 0; d < _docs.Length; d++)
        {
            var sink = new DistinctSink();
            ContentTokenizer.Tokenize(_docs[d].Stored.Body, sink);
            foreach (var (field, value) in _docs[d].Stored.Fields) ContentTokenizer.TokenizeField(field, value, sink);
            foreach (var t in sink.Terms)
            {
                var bytes = Encoding.UTF8.GetBytes(t);
                if (!byTerm.TryGetValue(bytes, out var list)) byTerm[bytes] = list = [];
                list.Add(d);
            }
        }
        _terms = [.. byTerm.Keys];
        _postings = [.. byTerm.Values.Select(l => l.ToArray())];
        _pictures = [.. _docs.Select((doc, i) => (i, doc.Stored.Picture)).Where(p => p.Picture != null).Select(p => (p.i, p.Picture!.Value))];
    }

    /// <summary>A real store segment passed through, with the same counters and hooks; docs are what was committed into it.</summary>
    public FakeSegment(IContentSegment inner, IReadOnlyList<FakeDoc> docs)
    {
        Id = inner.Id;
        _inner = inner;
        _docs = [.. docs];
    }

    /// <summary>The real segment behind this one (null for a fake).</summary>
    public IContentSegment? Inner => _inner;

    /// <summary>Points a pass-through at the same segment as held by a newer view (its counters are kept).</summary>
    internal void Rebind(IContentSegment inner)
    {
        if (inner.Id != Id) throw new ArgumentException("Another segment.", nameof(inner));
        _inner = inner;
    }

    /// <summary>Called before each stored-text read (tests block, count or cancel there).</summary>
    public Action<int>? OnRead { get; set; }
    /// <summary>Called before each dictionary range scan.</summary>
    public Action? OnTerms { get; set; }
    /// <summary>Stored texts read so far.</summary>
    public int Reads => Volatile.Read(ref _reads);
    /// <summary>Dictionary range scans and exact lookups so far.</summary>
    public int TermScans => Volatile.Read(ref _termScans);
    public IReadOnlyList<FakeDoc> Docs => _docs;

    public uint Id { get; }
    public int DocCount => _inner?.DocCount ?? _docs.Length;
    /// <summary>The word rules' version: the real segment's unless a test says otherwise.</summary>
    public int TokenizerVersion
    {
        get => _tokenizerVersion ?? _inner?.TokenizerVersion ?? ContentTokenizer.Version;
        init => _tokenizerVersion = value;
    }
    /// <summary>A version set by the test (null = the segment's own).</summary>
    internal int? TokenizerVersionSet => _tokenizerVersion;
    long? _created;
    public long CreatedUtc
    {
        get => _created ?? _inner?.CreatedUtc ?? DateTime.UtcNow.ToFileTimeUtc();
        init => _created = value;
    }
    public DocRow Row(int doc) => _inner?.Row(doc) ?? _docs[doc].Row;
    public int Find(ulong key) => _inner?.Find(key) ?? Array.FindIndex(_docs, d => d.Row.Key == key);

    public void Terms(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, TermVisitor visit, CancellationToken ct)
    {
        Interlocked.Increment(ref _termScans);
        OnTerms?.Invoke();
        if (_inner != null) { _inner.Terms(from, to, visit, ct); return; }
        int i = LowerBound(from);
        for (; i < _terms.Length && (to.IsEmpty || ByteOrder.Compare(_terms[i], to) < 0); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!visit(_terms[i], Postings(i))) return;
        }
    }

    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermPostings postings)
    {
        Interlocked.Increment(ref _termScans);
        OnTerms?.Invoke();
        if (_inner != null) return _inner.TryGetTerm(term, out postings);
        int i = LowerBound(term);
        if (i < _terms.Length && ByteOrder.Compare(_terms[i], term) == 0) { postings = Postings(i); return true; }
        postings = default;
        return false;
    }

    TermPostings Postings(int i)
    {
        var p = _postings[i];
        return new TermPostings(p.Length, i, p.Length > 0 ? p[0] : 0, p.Length > 1 ? p[1] : 0);
    }

    int LowerBound(ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = _terms.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (ByteOrder.Compare(_terms[mid], key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public void ReadPostings(in TermPostings postings, int segmentOrdinal, DocSetBuilder into, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_inner != null) { _inner.ReadPostings(postings, segmentOrdinal, into, ct); return; }
        into.AddSorted(segmentOrdinal, _postings[(int)postings.Offset]);
    }

    /// <summary>Called for every decompression (the fixture counts those made while its search holds the read lock).</summary>
    public Action? OnDecompress { get; set; }

    public StoredDoc? ReadStored(int doc)
    {
        OnRead?.Invoke(doc);
        Interlocked.Increment(ref _reads);
        OnDecompress?.Invoke();
        return _inner != null ? _inner.ReadStored(doc) : _docs[doc].Stored;
    }

    public ReadOnlyMemory<byte> ReadStoredRaw(int doc) => _inner?.ReadStoredRaw(doc) ?? Encoding.UTF8.GetBytes(_docs[doc].Stored.Body);
    public string? ReadPath(int doc) => _inner != null ? _inner.ReadPath(doc) : _docs[doc].Path;
    public int PictureCount => _inner?.PictureCount ?? _pictures.Length;
    public (int Doc, PictureInfo Info) Picture(int i) => _inner?.Picture(i) ?? _pictures[i];

    public bool TryGetPicture(int doc, out PictureInfo info)
    {
        if (_inner != null) return _inner.TryGetPicture(doc, out info);
        foreach (var (d, i) in _pictures) if (d == doc) { info = i; return true; }
        info = default;
        return false;
    }

    sealed class DistinctSink : ITokenSink
    {
        public readonly HashSet<string> Terms = new(StringComparer.Ordinal);
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind) => Terms.Add(folded.ToString());
    }
}

/// <summary>Byte-wise order of UTF-8 terms (the dictionary's order).</summary>
internal sealed class ByteOrder : IComparer<byte[]>
{
    public static readonly ByteOrder Instance = new();
    public int Compare(byte[]? a, byte[]? b) => Compare(a, (ReadOnlySpan<byte>)b);
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

/// <summary>
/// A store view over fake segments with deletions; counts its references. Over a real store's view (the integration
/// fixture) its segments pass through to the real ones, deletions are the real view's plus any the test adds, and the real
/// view is released with the last reference.
/// </summary>
internal sealed class FakeStoreView(IReadOnlyList<FakeSegment> segments, long commitGeneration, IStoreView? inner = null) : IStoreView
{
    int _refs = 1;
    public HashSet<DocRef> Deleted { get; } = [];
    public bool Released => Volatile.Read(ref _refs) <= 0;
    public IReadOnlyList<FakeSegment> FakeSegments => segments;
    /// <summary>The real view behind this one (null for fakes).</summary>
    public IStoreView? Inner => inner;

    public long CommitGeneration => commitGeneration;
    public IReadOnlyList<IContentSegment> Segments => segments;
    public IReadOnlyList<VolumeEntry> Volumes => inner?.Volumes ?? [new VolumeEntry(0, "vol:test", "C:\\", DateTime.UtcNow)];
    /// <summary>Called for every deletion test (Prepare maps documents through it, under the index lock).</summary>
    public Action? OnIsDeleted { get; set; }

    public bool IsDeleted(DocRef doc)
    {
        OnIsDeleted?.Invoke();
        return Deleted.Contains(doc) || inner != null && inner.IsDeleted(doc);
    }

    public DocRef? Find(ulong key)
    {
        for (int s = segments.Count - 1; s >= 0; s--)
        {
            int d = segments[s].Find(key);
            if (d >= 0 && !IsDeleted(new DocRef(s, d))) return new DocRef(s, d);
        }
        return null;
    }

    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey()
        => segments.SelectMany((seg, s) => Enumerable.Range(0, seg.DocCount).Select(d => (new DocRef(s, d), seg.Row(d))))
            .Where(r => !IsDeleted(r.Item1)).OrderBy(r => r.Item2.Key);

    public IReadOnlyDictionary<ContentKind, KindStats> Stats => inner?.Stats ?? EmptyStats;
    static readonly IReadOnlyDictionary<ContentKind, KindStats> EmptyStats = new Dictionary<ContentKind, KindStats>();
    public IStoreView AddRef() => TryAddRef() ? this : throw new ObjectDisposedException(nameof(FakeStoreView));

    public bool TryAddRef()
    {
        for (int r = Volatile.Read(ref _refs); r > 0; r = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return true;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refs) == 0) inner?.Dispose();
    }
}

/// <summary>The text index as a search sees it, over a published fake view; counts finished lookups.</summary>
internal sealed class FakeContentSource : ContentSource
{
    ContentView? _current;
    int _completed;
    readonly SemaphoreSlim _signal = new(0);

    public FakeContentSource(DocSetCache? cache = null) => Cache = cache ?? new DocSetCache();

    public ContentMode ModeValue { get; set; } = ContentMode.DocumentsAndPictures;
    public bool CatchingUpValue { get; set; }
    /// <summary>Overrides the map (null result = stale).</summary>
    public Func<FileIndex, ContentView, DocIdMap?>? MapOverride { get; set; }
    public int Completed => Volatile.Read(ref _completed);
    public ContentView? Current => Volatile.Read(ref _current);

    /// <summary>Publishes a view (the source holds the creator's reference) and releases the one before.</summary>
    public void Publish(ContentView? view)
    {
        Volatile.Write(ref _translated, null);
        Interlocked.Exchange(ref _current, view)?.Dispose();
    }

    /// <summary>Waits for the next finished background lookup.</summary>
    public bool WaitCompleted(TimeSpan timeout) => _signal.Wait(timeout);

    DocIdMap? _translated;
    FileIndex? _attached;

    /// <summary>Follows the index's Compact the way the real index does: the published map is translated through the remap (pure, no wait).</summary>
    public void Attach(FileIndex index)
    {
        if (ReferenceEquals(_attached, index)) return;
        _attached = index;
        index.Compacted += (remap, generation) =>
        {
            if (Current is not { } view) return;
            var b = (Volatile.Read(ref _translated) ?? view.Map).ToBuilder();
            b.RemapIds(remap);
            Volatile.Write(ref _translated, b.Build(generation));
        };
    }

    internal override ContentMode Mode => ModeValue;
    internal override ContentView? AcquireView() => Acquire(ref _current);
    internal override DocIdMap? MapFor(FileIndex index, ContentView view)
    {
        if (MapOverride != null) return MapOverride(index, view);
        if (view.Map.Generation == index.Generation) return view.Map;
        // The view's map is older than the index: a map translated since (keyed by segment id) answers for it.
        return Volatile.Read(ref _translated) is { } t && t.Generation == index.Generation ? t : null;
    }
    internal override DocSetCache Cache { get; }
    internal override void PrefetchCompleted()
    {
        Interlocked.Increment(ref _completed);
        _signal.Release();
    }
    internal override bool CatchingUp => CatchingUpValue;
}

/// <summary>
/// A FileIndex tree with a text index attached: documents are added by path (the file is created in the tree), grouped
/// in segments, then published as one view whose id map binds each document to its file. On a real store (the
/// integration fixture, <see cref="OnStore"/>) each group of documents is one commit of a ContentStore, deletes and merges
/// are the store's own, and the view's segments pass through to the store's.
/// </summary>
internal sealed class ContentFixture : IDisposable
{
    public static readonly DateTime Now = new(2024, 5, 15, 12, 0, 0, DateTimeKind.Local);
    const string VolumeKey = "vol:test";

    public TreeBuilder Tree { get; } = new();
    // A long plain-word budget: results don't depend on how busy the machine is (budget tests set their own).
    public SearchEngine Engine { get; } = new() { Clock = () => Now, ContentBudget = TimeSpan.FromSeconds(30) };
    public FakeContentSource Source { get; }
    readonly List<List<(int FileId, FakeDoc Doc)>> _segments = [[]];
    long _commit;
    int _lockHolders, _underLock;

    // Store mode: the store, its folder, what each fixture group committed so far, the documents' bindings and the
    // pass-through segments (kept across publishes so their counters add up).
    readonly Store.TempFolder? _folder;
    readonly ushort _volume;
    readonly List<int> _committed = [0];
    readonly Dictionary<(uint Segment, int Doc), int> _bindings = [];
    readonly Dictionary<uint, List<FakeDoc>> _docsOf = [];
    readonly Dictionary<uint, FakeSegment> _passThrough = [];

    public ContentFixture(DocSetCache? cache = null) : this(cache, null, null) { }

    ContentFixture(DocSetCache? cache, IContentStore? store, Store.TempFolder? folder)
    {
        Source = new FakeContentSource(cache);
        RealStore = store;
        _folder = folder;
        if (store != null) _volume = store.VolumeIndex(VolumeKey, "C:\\");
        Engine.Probe = stage =>
        {
            if (stage == "locked") Interlocked.Increment(ref _lockHolders);
            else if (stage == "unlocking") Interlocked.Decrement(ref _lockHolders);
            Probe?.Invoke(stage);
        };
    }

    /// <summary>A fixture over a real ContentStore: in memory, or in a temporary folder on disk (deleted on Dispose).</summary>
    public static ContentFixture OnStore(bool onDisk, DocSetCache? cache = null)
    {
        var folder = onDisk ? new Store.TempFolder() : null;
        return new ContentFixture(cache, ContentStore.Open(folder?.Path), folder);
    }

    /// <summary>The real store behind the view (null for fakes).</summary>
    public IContentStore? RealStore { get; }
    /// <summary>The real store's folder (null for fakes and in memory).</summary>
    public string? FolderPath => _folder?.Path;

    /// <summary>Called at the engine's probe points after the fixture's own bookkeeping.</summary>
    public Action<string>? Probe { get; set; }
    /// <summary>Stored texts decompressed while this fixture's searches held the index read lock (must stay 0).</summary>
    public int DecompressedUnderLock => Volatile.Read(ref _underLock);

    public FileIndex Index => Tree.Index;
    public FakeStoreView? View { get; private set; }

    /// <summary>Starts the next segment (documents added after this go into it).</summary>
    public void NextSegment()
    {
        _segments.Add([]);
        _committed.Add(0);
    }

    /// <summary>The document key of a path ("C:\Docs\a.txt" → vol:test, "Docs\a.txt").</summary>
    public static ulong KeyOf(string path) => ContentKeys.Of(VolumeKey, path.AsSpan(3));

    /// <summary>A file (created when new) and its stored document.</summary>
    public int Doc(string path, string body, (DocField Field, string Value)[]? fields = null, PictureInfo? picture = null,
        DocStatus status = DocStatus.Ok, int pages = 0, ContentKind? kind = null, long size = 100)
    {
        int id = TryId(path) ?? Tree.File(path, size, TreeBuilder.Ft(2024, 1, 1));
        string name = path[(path.LastIndexOf('\\') + 1)..];
        var k = kind ?? (ContentTypes.KindOf(name) is var known and not ContentKind.None ? known : ContentKind.Text);
        var flags = (ContentTokenizer.HasWords(body) ? DocFlags.HasText : 0) | (picture != null ? DocFlags.HasPicture : 0);
        var row = new DocRow(KeyOf(path), 0, size, _volume, (ushort)pages, k, status, 1, flags);
        _segments[^1].Add((id, new FakeDoc(path, row, new StoredDoc(body, fields ?? [], picture))));
        return id;
    }

    int? TryId(string path)
    {
        try { return Tree.Id(path); }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>
    /// Builds the segments (ids 10, 20, 30 …) and publishes their view, attached to the index. On a real store, the
    /// documents added since the last publish are committed (one commit per group) and the store's current view is
    /// published; <paramref name="make"/> then only sets a new segment's word-rules version (or hands back a pass-through).
    /// </summary>
    public FakeStoreView Publish(Func<uint, IReadOnlyList<FakeDoc>, FakeSegment>? make = null)
    {
        if (RealStore != null) return PublishStore(make);
        var segments = new List<FakeSegment>();
        var builder = DocIdMap.Empty(Index.Generation).ToBuilder();
        for (int s = 0; s < _segments.Count; s++)
        {
            uint segId = (uint)(10 * (s + 1));
            var docs = _segments[s].Select(d => d.Doc).ToList();
            var segment = make?.Invoke(segId, docs) ?? new FakeSegment(segId, docs);
            segment.OnDecompress = () => { if (Volatile.Read(ref _lockHolders) > 0) Interlocked.Increment(ref _underLock); };
            segments.Add(segment);
            builder.AddSegment(segId, docs.Count);
            for (int d = 0; d < docs.Count; d++) builder.Set(segId, d, _segments[s][d].FileId);
        }
        var view = new FakeStoreView(segments, ++_commit);
        Source.Publish(new ContentView(view, builder.Build(Index.Generation), Source.ModeValue));
        Source.Attach(Index);
        Index.Content = Source;
        View = view;
        return view;
    }

    FakeStoreView PublishStore(Func<uint, IReadOnlyList<FakeDoc>, FakeSegment>? make)
    {
        var store = RealStore!;
        for (int s = 0; s < _segments.Count; s++)
        {
            var group = _segments[s];
            if (_committed[s] >= group.Count) continue;
            var adds = group.Skip(_committed[s]).ToList();
            _committed[s] = group.Count;
            using var batch = store.BeginBatch();
            foreach (var (_, doc) in adds) batch.Add(doc.Row, doc.Path[3..], doc.Stored);
            var change = batch.Commit();
            using (change.View)
            {
                Assert.NotNull(change.NewSegment);
                uint segment = change.NewSegment!.Value;
                Follow(change);
                _docsOf[segment] = [.. adds.Select(a => a.Doc)];
                for (int i = 0; i < adds.Count; i++)
                    if (change.LocalOfAdded[i] >= 0) _bindings[(segment, change.LocalOfAdded[i])] = adds[i].FileId;
            }
        }
        return PublishCurrent(make);
    }

    /// <summary>Moves bindings through a change's remaps (merges, rewrites) and drops those of segments gone, as the follower does.</summary>
    void Follow(StoreChange change)
    {
        var moved = new Dictionary<(uint Segment, int Doc), int>();
        foreach (var (old, oldToNew) in change.Remaps)
        {
            foreach (var ((segment, doc), fileId) in _bindings.Where(b => b.Key.Segment == old).ToList())
            {
                _bindings.Remove((segment, doc));
                if (doc < oldToNew.Length && oldToNew[doc] >= 0 && change.NewSegment is { } to) moved[(to, oldToNew[doc])] = fileId;
            }
            if (change.NewSegment is { } n && _docsOf.Remove(old, out var docs))
                _docsOf[n] = [.. (_docsOf.TryGetValue(n, out var have) ? have : []).Concat(docs)];
        }
        foreach (uint dropped in change.DroppedSegments)
        {
            _passThrough.Remove(dropped);
            foreach (var key in _bindings.Keys.Where(k => k.Segment == dropped).ToList()) _bindings.Remove(key);
        }
        foreach (var (key, fileId) in moved) _bindings[key] = fileId;
    }

    FakeStoreView PublishCurrent(Func<uint, IReadOnlyList<FakeDoc>, FakeSegment>? make)
    {
        var real = RealStore!.Acquire();
        var segments = new List<FakeSegment>();
        var builder = DocIdMap.Empty(Index.Generation).ToBuilder();
        foreach (var seg in real.Segments)
        {
            var docs = _docsOf.TryGetValue(seg.Id, out var d) ? d : [];
            if (_passThrough.TryGetValue(seg.Id, out var known)) known.Rebind(seg);
            else
            {
                var made = make?.Invoke(seg.Id, docs);
                known = made is { Inner: not null } ? made
                    : made?.TokenizerVersionSet is { } version ? new FakeSegment(seg, docs) { TokenizerVersion = version }
                    : new FakeSegment(seg, docs);
                known.OnDecompress = () => { if (Volatile.Read(ref _lockHolders) > 0) Interlocked.Increment(ref _underLock); };
                _passThrough[seg.Id] = known;
            }
            segments.Add(known);
            builder.AddSegment(seg.Id, seg.DocCount);
        }
        foreach (var ((segment, doc), fileId) in _bindings)
            if (builder.HasSegment(segment)) builder.Set(segment, doc, fileId);
        var view = new FakeStoreView(segments, real.CommitGeneration, real);
        Source.Publish(new ContentView(view, builder.Build(Index.Generation), Source.ModeValue));
        Source.Attach(Index);
        Index.Content = Source;
        View = view;
        return view;
    }

    /// <summary>Deletes a path's document: added to the fake view's deletions, or deleted and committed in the real store and published again.</summary>
    public void Delete(string path)
    {
        var view = View ?? throw new InvalidOperationException("Publish first.");
        var doc = view.Find(KeyOf(path)) ?? throw new KeyNotFoundException(path);
        if (RealStore == null) { view.Deleted.Add(doc); return; }
        using (var batch = RealStore.BeginBatch())
        {
            using (var latest = RealStore.Acquire()) batch.Delete(latest.Find(KeyOf(path)) ?? throw new KeyNotFoundException(path));
            var change = batch.Commit();
            using (change.View) Follow(change);
        }
        PublishCurrent(null);
    }

    /// <summary>Merges every segment of the real store into one (the store's own merge) and publishes the result.</summary>
    public StoreChange Merge()
    {
        var store = RealStore ?? throw new InvalidOperationException("Merges need a real store.");
        MergePlan plan;
        using (var v = store.Acquire()) plan = new MergePlan([.. v.Segments.Select(s => s.Id)], v.Segments.Sum(s => s.DocCount), false);
        var change = store.Merge(plan, CancellationToken.None);
        Follow(change);
        change.View.Dispose();
        PublishCurrent(null);
        return change;
    }

    public SearchResults Run(string query, SearchOptions? options = null, SortKey sort = SortKey.Name, bool descending = false,
        CancellationToken ct = default)
        => Engine.Search(Index, query, options ?? new SearchOptions(), sort, descending, ct);

    /// <summary>Full paths of the results, in order.</summary>
    public string[] Find(string query, SearchOptions? options = null, SortKey sort = SortKey.Name, bool descending = false)
    {
        var r = Run(query, options, sort, descending);
        Assert.Null(r.Error);
        return [.. r.Ids.Select(Index.GetFullPath)];
    }

    /// <summary>The results' paths, sorted (order-independent comparisons).</summary>
    public string[] Set(string query, SearchOptions? options = null) => [.. Find(query, options).Order(StringComparer.OrdinalIgnoreCase)];

    public static readonly SearchOptions Plain = new() { MatchContent = true };

    /// <summary>Lets the view go, then closes the store and deletes its folder (fakes: nothing to do).</summary>
    public void Dispose()
    {
        if (RealStore == null) return;
        if (ReferenceEquals(Index.Content, Source)) Index.Content = null;
        Source.Publish(null);
        Source.Cache.Clear();
        RealStore.Dispose();
        _folder?.Dispose();
    }
}
