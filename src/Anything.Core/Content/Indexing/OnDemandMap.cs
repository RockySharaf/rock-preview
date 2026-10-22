using System.Runtime.CompilerServices;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Indexing;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// Ids of another FileIndex's entries (folder listings through ShareWith, the CLI's loaded database) mapped on demand. Maps
/// live in a ConditionalWeakTable keyed by the index, so a closed listing's index is never kept alive: no Compacted
/// subscription is needed. A map is built again when the index's Generation or PathVersion, or the view's commit, moves;
/// when only entries changed (a download landed, a file was saved), just the touched ids are mapped again. Building walks
/// the index's live files (keys chained per folder, roots resolved by the caller); a listing much smaller than the store
/// is bound by key lookups (binary searches), a large one by a merge-join with the view's key-ordered rows. Called under
/// that index's read lock (MapFor's contract).
/// </summary>
/// <param name="sameStampOnly">
/// A file is bound only to a document read at its size and date (the follower's SameStamp rule): a listing's folders
/// aren't followed, so a file changed since it was read would match on words it no longer has for as long as the tab
/// lives. Unbound, it matches by name until the follower reads it again.
/// </param>
internal sealed class OnDemandMap(bool sameStampOnly = true)
{
    /// <summary>A listing with fewer live files than 1/32 of the store's documents is bound by lookups, not by a join of every row.</summary>
    const int LookupRatio = 32;
    /// <summary>More touched ids than this, and the map is built again instead of patched.</summary>
    const int MaxPatch = 4096;
    /// <summary>Listings up to this many bound files keep their bindings, so a change can be patched.</summary>
    const int MaxTracked = 65_536;

    sealed class Entry(long generation, long pathVersion, long sequence, long commit, DocIdMap map, Dictionary<int, (uint Segment, int Doc)>? bound)
    {
        public readonly long Generation = generation, PathVersion = pathVersion, Sequence = sequence, Commit = commit;
        public readonly DocIdMap Map = map;
        /// <summary>The document bound to each id (what a patch unbinds); null = built again on any change.</summary>
        public readonly Dictionary<int, (uint Segment, int Doc)>? Bound = bound;
    }

    readonly ConditionalWeakTable<FileIndex, Entry> _maps = [];

    /// <summary>Maps built from scratch so far (tests).</summary>
    internal int Builds { get; private set; }

    /// <summary>Maps patched for touched ids so far (tests).</summary>
    internal int Patches { get; private set; }

    /// <summary>
    /// The map for an index and a view. <paramref name="resolve"/> turns a folder's full path (a root entry's name) into
    /// its volume key and the path below the volume; null when no root holds it. A root entry it can't resolve maps
    /// nothing, unless <paramref name="holdsRootBelow"/> says a root lies below it (a listing of E: while the text index
    /// has E:\Data): its folders are then resolved by their full paths, each once, and a branch no root is at or below
    /// is skipped whole.
    /// </summary>
    public DocIdMap For(FileIndex index, ContentView view, Func<string, (string Key, string Prefix)?> resolve, Func<string, bool>? holdsRootBelow = null)
    {
        var roots = new Roots(resolve, holdsRootBelow);
        long generation = index.Generation, pathVersion = index.PathVersion, sequence = index.ChangeSequence, commit = view.Store.CommitGeneration;
        if (_maps.TryGetValue(index, out var e) && e.Generation == generation && e.PathVersion == pathVersion && e.Commit == commit)
        {
            if (e.Sequence == sequence) return e.Map;
            if (e.Bound != null && Patch(index, view.Store, roots, e, sequence, sameStampOnly) is { } patched)
            {
                _maps.AddOrUpdate(index, patched);
                Patches++;
                return patched.Map;
            }
        }
        var built = Build(index, view.Store, roots, generation, pathVersion, sequence, commit, sameStampOnly);
        _maps.AddOrUpdate(index, built);
        Builds++;
        return built.Map;
    }

    /// <summary>How folders are placed in the store's keys (For's two delegates).</summary>
    readonly record struct Roots(Func<string, (string Key, string Prefix)?> Resolve, Func<string, bool>? HoldsRootBelow);

    /// <summary>
    /// Where a folder is in the store's keys: its key state, or, when no root holds it but one lies below it, its full path
    /// (its subfolders are resolved one by one); neither when nothing below it can be in the store.
    /// </summary>
    readonly record struct Place(ulong? State, string? Path);

    /// <summary>The map made from scratch (lookups for a small listing, a merge-join for a large one).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static Entry Build(FileIndex index, IStoreView store, Roots roots, long generation, long pathVersion, long sequence, long commit,
        bool sameStampOnly)
    {
        var builder = DocIdMap.Empty(generation).ToBuilder();
        long storeDocs = 0;
        foreach (var seg in store.Segments)
        {
            builder.AddSegment(seg.Id, seg.DocCount);
            storeDocs += seg.DocCount;
        }
        int count = index.Count;
        var states = new Dictionary<int, Place>();
        var chain = new List<int>(32);
        var keys = new List<(ulong Key, int Id)>();
        for (int id = 0; id < count; id++)
        {
            if (!index.IsAlive(id) || index.IsDirectory(id)) continue;
            int parent = index.Parent(id);
            if (parent == FileIndex.NoParent || FolderState(index, parent, states, chain, roots) is not { } state) continue;
            keys.Add((ContentKeys.Continue(state, index.Name(id)), id));
        }
        var bound = keys.Count <= MaxTracked ? new Dictionary<int, (uint, int)>() : null;
        if (keys.Count > 0 && keys.Count * (long)LookupRatio < storeDocs)
        {
            // A folder of a few thousand files over a store of hundreds of thousands of documents: binary searches.
            foreach (var (key, id) in keys)
                if (store.Find(key) is { } doc && (!sameStampOnly || SameStamp(store.Segments[doc.Segment].Row(doc.Doc), index, id)))
                {
                    uint seg = store.Segments[doc.Segment].Id;
                    builder.Set(seg, doc.Doc, id);
                    bound?.Add(id, (seg, doc.Doc));
                }
        }
        else if (keys.Count > 0)
        {
            keys.Sort((a, b) => a.Key.CompareTo(b.Key));
            int i = 0;
            foreach (var (doc, row) in store.RowsByKey())
            {
                while (i < keys.Count && keys[i].Key < row.Key) i++;
                if (i >= keys.Count) break;
                if (keys[i].Key != row.Key) continue;
                if (sameStampOnly && !SameStamp(row, index, keys[i].Id)) continue;
                uint seg = store.Segments[doc.Segment].Id;
                builder.Set(seg, doc.Doc, keys[i].Id);
                bound?.TryAdd(keys[i].Id, (seg, doc.Doc));
            }
        }
        return new Entry(generation, pathVersion, sequence, commit, builder.Build(generation), bound);
    }

    /// <summary>The map moved along for the ids touched since it was made (unbound, then bound by key again); null = build it again.</summary>
    static Entry? Patch(FileIndex index, IStoreView store, Roots roots, Entry e, long sequence, bool sameStampOnly)
    {
        var touched = new List<int>();
        if (!index.TryGetChangesSince(e.Sequence, touched) || touched.Count > MaxPatch) return null;
        var builder = e.Map.ToBuilder();
        var bound = new Dictionary<int, (uint Segment, int Doc)>(e.Bound!);
        var ids = touched.Distinct().ToList();
        // Unbind first, then bind: a new id that takes a dead one's document (safe-save) keeps it.
        foreach (int id in ids)
            if (bound.Remove(id, out var old)) builder.Set(old.Segment, old.Doc, -1);
        var states = new Dictionary<int, Place>();
        var chain = new List<int>(32);
        int count = index.Count;
        foreach (int id in ids)
        {
            if ((uint)id >= (uint)count || !index.IsAlive(id) || index.IsDirectory(id)) continue;
            int parent = index.Parent(id);
            if (parent == FileIndex.NoParent || FolderState(index, parent, states, chain, roots) is not { } state) continue;
            if (store.Find(ContentKeys.Continue(state, index.Name(id))) is not { } doc) continue;
            // Saved since it was read (a new size or date): its old words are not its own any more.
            if (sameStampOnly && !SameStamp(store.Segments[doc.Segment].Row(doc.Doc), index, id)) continue;
            uint seg = store.Segments[doc.Segment].Id;
            builder.Set(seg, doc.Doc, id);
            bound[id] = (seg, doc.Doc);
        }
        if (bound.Count > MaxTracked) return null;
        return new Entry(e.Generation, e.PathVersion, sequence, e.Commit, builder.Build(e.Generation), bound);
    }

    /// <summary>The document was read at the entry's size and date (the follower's SameStamp): an unknown date never is.</summary>
    static bool SameStamp(in DocRow row, FileIndex index, int id)
    {
        long modified = index.Modified(id);
        return modified != 0 && row.Size == index.Size(id) && row.Modified == modified;
    }

    static ulong? FolderState(FileIndex index, int folder, Dictionary<int, Place> states, List<int> chain, Roots roots)
    {
        if (states.TryGetValue(folder, out var known)) return known.State;
        chain.Clear();
        int cur = folder;
        Place place = default;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (states.TryGetValue(cur, out var s)) { place = s; break; }
            int parent = index.Parent(cur);
            if (parent == FileIndex.NoParent)
            {
                place = Locate(index.Name(cur), roots);
                states[cur] = place;
                break;
            }
            chain.Add(cur);
            if ((uint)parent >= (uint)index.Count) { place = default; break; }
            cur = parent;
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            int f = chain[i];
            place = place.State is { } p ? new Place(ContentKeys.Continue(p, index.Name(f)), null)
                : place.Path is { } path ? Locate(path + "\\" + index.Name(f), roots)
                : default;
            states[f] = place;
        }
        return place.State;
    }

    /// <summary>A folder no parent's key reaches, by its full path: in a root, above one (looked into), or outside every root.</summary>
    static Place Locate(string path, Roots roots) =>
        roots.Resolve(path) is { } r ? new Place(ContentKeys.Of(r.Key, r.Prefix), null)
        : roots.HoldsRootBelow?.Invoke(path) == true ? new Place(null, path.TrimEnd('\\'))
        : default;
}

/// <summary>
/// A listing's folders under the names the main index knows them by (FolderCoverage.IndexedAs): "S:" is "C:\Work" when
/// S: is a SUBST of it, a junction is the folder it points to. Only strings: safe under any lock, on any thread.
/// </summary>
internal sealed class RootAliases
{
    readonly (string From, string To)[] _pairs;

    /// <summary>No aliases: every path stays as it is.</summary>
    public static RootAliases None { get; } = new(new Dictionary<string, string>());

    /// <summary>Listing folder → its indexed name, both normalized as root names; pairs that name the same folder twice are dropped.</summary>
    public RootAliases(IReadOnlyDictionary<string, string> aliases) => _pairs =
    [
        .. aliases.Select(p => (From: RootIndexer.NormalizeRootName(p.Key), To: RootIndexer.NormalizeRootName(p.Value)))
            .Where(p => p.From.Length > 0 && p.To.Length > 0 && !string.Equals(p.From, p.To, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.From.Length),
    ];

    /// <summary>Aliases kept.</summary>
    public int Count => _pairs.Length;

    /// <summary>The path under its indexed name; as given when no alias holds it.</summary>
    public string Indexed(string path)
    {
        foreach (var (from, to) in _pairs)
            if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (path.Length == from.Length || path[from.Length] == '\\'))
                return to + path[from.Length..];
        return path;
    }
}

/// <summary>
/// Another index's text index, as a folder listing sees it (ContentServices.ShareWith): every call goes to whatever the
/// main index has now, so a listing follows search inside files being turned on, off or rebuilt; ids are mapped on demand
/// by the main index's source (MapFor with the listing's index, and its folders' indexed names).
/// </summary>
internal sealed class SharedContentSource(FileIndex from, RootAliases aliases) : ContentSource
{
    readonly DocSetCache _fallback = new();

    /// <summary>The listing's folders the main index knows by other names (a SUBST letter's).</summary>
    public RootAliases Aliases => aliases;

    internal override ContentMode Mode => from.Content is { } c and not SharedContentSource ? c.Mode : ContentMode.NamesOnly;
    internal override ContentView? AcquireView() => from.Content is { } c and not SharedContentSource ? c.AcquireView() : null;
    internal override DocIdMap? MapFor(FileIndex index, ContentView view) => from.Content is { } c and not SharedContentSource ? c.MapFor(index, view, aliases) : null;
    internal override DocSetCache Cache => from.Content is { } c and not SharedContentSource ? c.Cache : _fallback;
    internal override void PrefetchCompleted() => from.Content?.PrefetchCompleted();
    internal override bool CatchingUp => from.Content is { } c and not SharedContentSource && c.CatchingUp;
}

/// <summary>
/// A text index opened read-only for searching (the CLI): one view of the store, ids mapped on demand for whatever index
/// it is attached to. Roots are resolved with VolumeKeys (the CLI is its own process). Nothing is ever written.
/// </summary>
internal sealed class ReadOnlyContentSource : ContentSource, IDisposable
{
    readonly IContentStore _store;
    // Bound by key alone, as before: the database and the store were saved at different moments, so a stamp that differs
    // is as likely the database's lag as the file's change.
    readonly OnDemandMap _maps = new(sameStampOnly: false);
    readonly DocSetCache _cache = new();
    readonly Dictionary<string, (string Key, string Prefix)?> _roots = new(StringComparer.OrdinalIgnoreCase);
    readonly ContentMode _mode;
    ContentView? _view;

    /// <summary>Takes over the store and publishes its current view once.</summary>
    public ReadOnlyContentSource(IContentStore store)
    {
        _store = store;
        _mode = store.RecordedMode == ContentMode.NamesOnly ? ContentMode.DocumentsAndPictures : store.RecordedMode;
        _view = new ContentView(store.Acquire(), DocIdMap.Empty(-1), _mode);
    }

    internal override ContentMode Mode => _mode;
    internal override ContentView? AcquireView() => Acquire(ref _view);

    internal override DocIdMap? MapFor(FileIndex index, ContentView view) => _maps.For(index, view, name =>
    {
        lock (_roots)
        {
            if (_roots.TryGetValue(name, out var r)) return r;
            var (key, prefix, _) = VolumeKeys.For(name);
            r = (key, prefix);
            _roots[name] = r;
            return r;
        }
    });

    internal override DocSetCache Cache => _cache;
    internal override void PrefetchCompleted() { }
    internal override bool CatchingUp => false;

    /// <summary>Releases the view and the store.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _view, null)?.Dispose();
        _store.Dispose();
    }
}
