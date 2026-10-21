using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Anything.Core;

/// <summary>
/// In-memory file name index stored as parallel arrays (struct-of-arrays) so millions of
/// entries stay compact. Ids are stable for the life of the index; removed slots are marked
/// dead and skipped. Volume/folder roots have <see cref="NoParent"/> as parent and their
/// name is the root path without a trailing separator (for example "C:" or "\\server\share").
/// All public members are thread-safe: mutators take the write lock, accessors are lock-free
/// reads of array slots (callers that need a consistent snapshot hold <see cref="Lock"/> for reading).
/// Like Everything, some information is optional (Settings › Indexing): date accessed (8 bytes per entry, only while
/// on), folder sizes (kept in the folders' own size slots, so free) and recent changes (only the entries changed).
/// </summary>
public sealed class FileIndex
{
    public const int NoParent = -1;

    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);

    string[] _names = new string[1024];
    int[] _parent = new int[1024];
    long[] _size = new long[1024];
    long[] _modified = new long[1024];
    long[] _created = new long[1024];
    FileAttributes[] _attrs = new FileAttributes[1024];
    bool[] _alive = new bool[1024];
    // Live children per entry, so removing an empty folder (what the change journal reports for every deleted folder,
    // children first) is instant instead of a pass over millions of entries.
    int[] _kids = new int[1024];
    // Date accessed: allocated only while it is indexed.
    long[]? _accessed;
    // Date Recently Changed: when each entry was last seen created, changed, moved or renamed (null = not recorded).
    // Only entries that changed are in it; read without the lock (the result list does), hence concurrent.
    ConcurrentDictionary<int, long>? _recent;
    // Folder sizes: a folder's size slot holds the total of the files below it, kept current on every change.
    bool _folderSizes;
    // Name interning: about two thirds of all names on a typical disk are repeats ("index.js", "desktop.ini",
    // "#", "LICENSE"...), so every distinct name is stored once and shared.
    HashSet<string> _pool = new(StringComparer.Ordinal);
    int _count;
    int _liveCount;
    long _version;
    long _generation;
    long _pathVersion;

    /// <summary>Number of allocated slots (live and dead). Iterate 0..Count-1 and check <see cref="IsAlive"/>.</summary>
    public int Count => Volatile.Read(ref _count);

    public int LiveCount => Volatile.Read(ref _liveCount);

    /// <summary>Incremented on every mutation; lets caches and views detect changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Incremented by <see cref="Compact"/>, which renumbers ids. Ids obtained under an older generation must not be
    /// used to read entries; views re-query instead.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Incremented when a folder is renamed or moved, which changes the path of everything below it without touching
    /// those entries. (A folder's size changing touches it too: that alone changes no path.)
    /// </summary>
    public long PathVersion => Interlocked.Read(ref _pathVersion);

    /// <summary>Slots allocated but not used by live entries (removed entries awaiting <see cref="Compact"/>).</summary>
    public int DeadCount => Count - LiveCount;

    // Change log: ids touched by each mutation, so consumers (the search engine's presorted orders) can update
    // incrementally instead of re-sorting millions of entries. Bounded: on overflow it restarts and consumers
    // that fell behind rebuild.
    readonly List<int> _log = [];
    long _logBase;
    const int MaxLog = 1 << 19;

    void Touch(int id)
    {
        if (_log.Count >= MaxLog) { _logBase += _log.Count; _log.Clear(); }
        _log.Add(id);
    }

    /// <summary>Position in the change log after the latest mutation. Read while holding <see cref="Lock"/>.</summary>
    public long ChangeSequence => _logBase + _log.Count;

    /// <summary>
    /// Adds the ids touched since <paramref name="sequence"/> (with repeats) to <paramref name="into"/>. Returns false
    /// when that history is no longer available. Call while holding <see cref="Lock"/> (read or write).
    /// </summary>
    public bool TryGetChangesSince(long sequence, List<int> into)
    {
        long end = ChangeSequence;
        if (sequence < _logBase || sequence > end) return false;
        for (long i = sequence - _logBase; i < _log.Count; i++) into.Add(_log[(int)i]);
        return true;
    }

    string Intern(string name)
    {
        if (_pool.TryGetValue(name, out var shared)) return shared;
        _pool.Add(name);
        return name;
    }

    /// <summary>Raised after a mutation batch (outside the lock). Subscribers should debounce.</summary>
    public event Action? Changed;

    /// <summary>Run counts and dates of the files opened from the app (Run Count, Date Run, runcount:, dr:).</summary>
    public RunHistory RunHistory { get; set; } = new();

    /// <summary>Whether date accessed is indexed (<see cref="Accessed"/> is 0 for every entry while it isn't).</summary>
    public bool HasDateAccessed => _accessed != null;

    /// <summary>Whether recent changes are recorded (Date Recently Changed, rc:).</summary>
    public bool RecordsRecentChanges => _recent != null;

    /// <summary>Whether folders carry the total size of the files below them (otherwise their size is -1).</summary>
    public bool HasFolderSizes => _folderSizes;

    /// <summary>Turns the optional information on or off, keeping the entries. Folder sizes are worked out at once.</summary>
    public void Configure(bool dateAccessed, bool folderSizes, bool recentChanges)
    {
        Lock.EnterWriteLock();
        try
        {
            bool changed = false;
            if (dateAccessed != (_accessed != null))
            {
                // Turned on: unknown (0) until the entries are read again.
                _accessed = dateAccessed ? new long[_names.Length] : null;
                changed = true;
            }
            if (recentChanges != (_recent != null))
            {
                _recent = recentChanges ? new ConcurrentDictionary<int, long>() : null;
                changed = true;
            }
            if (folderSizes != _folderSizes)
            {
                _folderSizes = folderSizes;
                ComputeFolderSizes();
                changed = true;
            }
            if (!changed) return;
            // Values changed everywhere: consumers of the change log (presorted orders) start over.
            ResetChangeLog();
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    void ResetChangeLog()
    {
        _logBase += _log.Count + 1;
        _log.Clear();
    }

    /// <summary>Sets every folder's size from the files below it (or back to unknown when folder sizes are off).</summary>
    void ComputeFolderSizes()
    {
        int n = _count;
        for (int i = 0; i < n; i++)
            if (_alive[i] && (_attrs[i] & FileAttributes.Directory) != 0) _size[i] = _folderSizes ? 0 : -1;
        if (!_folderSizes) return;
        for (int i = 0; i < n; i++)
            if (_alive[i] && (_attrs[i] & FileAttributes.Directory) == 0 && _size[i] > 0) AddToAncestors(_parent[i], _size[i], touch: false);
    }

    /// <summary>What an entry adds to its folders' sizes: a file's size or a folder's total (unknown counts as 0).</summary>
    long FolderSizeShare(int id) => Math.Max(0, _size[id]);

    /// <summary>Adds <paramref name="delta"/> to the size of <paramref name="folder"/> and of every folder above it.</summary>
    void AddToAncestors(int folder, long delta, bool touch = true)
    {
        if (delta == 0) return;
        for (int cur = folder, guard = 0; (uint)cur < (uint)_count && guard < 4096; cur = _parent[cur], guard++)
        {
            _size[cur] += delta;
            if (touch) Touch(cur);
        }
    }

    public int AddRoot(string rootPath, FileAttributes attributes = FileAttributes.Directory)
        => Add(rootPath.TrimEnd('\\', '/'), NoParent, attributes | FileAttributes.Directory, -1, 0, 0);

    /// <summary>
    /// Adds one entry. Sizes are bytes (-1 = unknown; a folder's size is worked out, whatever is passed); dates are
    /// FILETIME UTC (0 = unknown). Date accessed is kept only while it is indexed.
    /// </summary>
    public int Add(string name, int parent, FileAttributes attributes, long size, long modifiedFileTimeUtc, long createdFileTimeUtc,
        long accessedFileTimeUtc = 0)
    {
        Lock.EnterWriteLock();
        try
        {
            int id = AddNoLock(name, parent, attributes, size, modifiedFileTimeUtc, createdFileTimeUtc, accessedFileTimeUtc);
            return id;
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Fast bulk insertion for initial scans. The callback receives an adder that skips per-item locking.</summary>
    public void BulkAdd(Action<BulkAdder> fill)
    {
        Lock.EnterWriteLock();
        try { fill(new BulkAdder(this)); }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    public readonly struct BulkAdder
    {
        readonly FileIndex _index;
        internal BulkAdder(FileIndex index) => _index = index;

        public int Add(string name, int parent, FileAttributes attributes, long size, long modifiedFileTimeUtc, long createdFileTimeUtc,
            long accessedFileTimeUtc = 0)
            => _index.AddNoLock(name, parent, attributes, size, modifiedFileTimeUtc, createdFileTimeUtc, accessedFileTimeUtc);

        /// <summary>Re-parents an entry inside a bulk operation (used when a child is seen before its parent).</summary>
        public void SetParent(int id, int parent)
        {
            if ((_index._attrs[id] & FileAttributes.Directory) != 0) Interlocked.Increment(ref _index._pathVersion);
            _index.Reparent(id, parent);
            _index.Touch(id);
        }
    }

    int AddNoLock(string name, int parent, FileAttributes attributes, long size, long modified, long created, long accessed)
    {
        if (_count == _names.Length) Grow();
        int id = _count;
        _names[id] = Intern(name);
        _parent[id] = parent;
        _kids[id] = 0;
        if ((uint)parent < (uint)id) _kids[parent]++;
        _attrs[id] = attributes;
        bool dir = (attributes & FileAttributes.Directory) != 0;
        // A folder's size is its files' total (none yet), or unknown without folder sizes.
        _size[id] = dir ? (_folderSizes ? 0 : -1) : size;
        _modified[id] = modified;
        _created[id] = created;
        if (_accessed != null) _accessed[id] = accessed;
        _alive[id] = true;
        Touch(id);
        if (_folderSizes && !dir && size > 0 && (uint)parent < (uint)id) AddToAncestors(parent, size);
        Volatile.Write(ref _count, id + 1);
        _liveCount++;
        Interlocked.Increment(ref _version);
        return id;
    }

    /// <summary>Moves an entry under another parent, keeping the child counts right (caller holds the write lock).</summary>
    void Reparent(int id, int parent)
    {
        int old = _parent[id];
        if (old == parent) return;
        if ((uint)old < (uint)_count && _kids[old] > 0) _kids[old]--;
        if ((uint)parent < (uint)_count) _kids[parent]++;
        if (_folderSizes)
        {
            // Its size (all of a folder's total) moves from the old folders to the new ones.
            long share = FolderSizeShare(id);
            AddToAncestors(old, -share);
            AddToAncestors(parent, share);
        }
        _parent[id] = parent;
    }

    // Grow by 50% (not 100%) once large: big indexes would otherwise carry up to 2x unused capacity.
    void Grow() => Resize(_names.Length < 1 << 20 ? _names.Length * 2 : _names.Length + _names.Length / 2);

    /// <summary>Marks an entry and (if it is a folder) all of its descendants as removed.</summary>
    public void Remove(int id)
    {
        Lock.EnterWriteLock();
        try
        {
            if (!IsAliveNoCheck(id)) return;
            int parent = _parent[id];
            if ((uint)parent < (uint)_count && _kids[parent] > 0) _kids[parent]--;
            if (_folderSizes) AddToAncestors(parent, -FolderSizeShare(id));
            if ((_attrs[id] & FileAttributes.Directory) == 0 || _kids[id] == 0)
            {
                // A file, or a folder with nothing in it: just this entry.
                _alive[id] = false;
                _names[id] = null!; // release the name now; the slot itself is reclaimed by Compact
                _liveCount--;
                Touch(id);
                _recent?.TryRemove(id, out _);
            }
            else
            {
                // One linear pass: resolve each entry's ancestry once, memoizing per slot
                // (0 = unknown, 1 = inside the removed subtree, 2 = outside).
                var state = new byte[_count];
                state[id] = 1;
                var chain = new List<int>(64);
                for (int i = 0; i < _count; i++)
                {
                    if (state[i] != 0) continue;
                    chain.Clear();
                    int cur = i;
                    byte verdict = 2;
                    while (cur != NoParent && (uint)cur < (uint)_count)
                    {
                        if (state[cur] != 0) { verdict = state[cur]; break; }
                        chain.Add(cur);
                        if (chain.Count > 4096) break; // corrupt parent cycle: treat as outside
                        cur = _parent[cur];
                    }
                    foreach (int c in chain) state[c] = verdict;
                }
                for (int i = 0; i < _count; i++)
                    if (state[i] == 1 && _alive[i]) { _alive[i] = false; _names[i] = null!; _liveCount--; Touch(i); }
                if (_recent is { IsEmpty: false } recent)
                    foreach (int key in recent.Keys)
                        if (!IsAliveNoCheck(key)) recent.TryRemove(key, out _);
            }
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    public void Rename(int id, string newName, int newParent)
    {
        Lock.EnterWriteLock();
        try
        {
            if (!IsAliveNoCheck(id)) return;
            if ((_attrs[id] & FileAttributes.Directory) != 0) Interlocked.Increment(ref _pathVersion);
            _names[id] = Intern(newName);
            Reparent(id, newParent);
            Touch(id);
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    /// <summary>Updates metadata; null arguments leave the value unchanged. A folder's size is worked out, not set.</summary>
    public void Update(int id, FileAttributes? attributes = null, long? size = null, long? modifiedFileTimeUtc = null, long? createdFileTimeUtc = null,
        long? accessedFileTimeUtc = null)
    {
        Lock.EnterWriteLock();
        try
        {
            if (!IsAliveNoCheck(id)) return;
            if (attributes is { } a) _attrs[id] = a;
            if (size is { } s && (_attrs[id] & FileAttributes.Directory) == 0)
            {
                if (_folderSizes) AddToAncestors(_parent[id], Math.Max(0, s) - FolderSizeShare(id));
                _size[id] = s;
            }
            if (modifiedFileTimeUtc is { } m) _modified[id] = m;
            if (createdFileTimeUtc is { } c) _created[id] = c;
            if (accessedFileTimeUtc is { } ac && _accessed != null) _accessed[id] = ac;
            Touch(id);
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    /// <summary>
    /// Records that the entry was created, changed, moved or renamed at <paramref name="fileTimeUtc"/> (FILETIME UTC),
    /// keeping the latest time. Nothing happens while recent changes aren't recorded.
    /// </summary>
    public void SetRecentChange(int id, long fileTimeUtc) => SetRecentChanges([(id, fileTimeUtc)]);

    /// <summary><see cref="SetRecentChange"/> for many entries under one lock.</summary>
    public void SetRecentChanges(IReadOnlyList<(int Id, long FileTimeUtc)> changes)
    {
        if (_recent == null || changes.Count == 0) return;
        Lock.EnterWriteLock();
        try
        {
            var recent = _recent;
            if (recent == null) return;
            bool any = false;
            foreach (var (id, time) in changes)
            {
                if (!IsAliveNoCheck(id) || time <= 0) continue;
                if (recent.TryGetValue(id, out long had) && had >= time) continue;
                recent[id] = time;
                Touch(id);
                any = true;
            }
            if (any) Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
    }

    public void Clear()
    {
        Lock.EnterWriteLock();
        try
        {
            Array.Clear(_alive, 0, _count);
            Array.Clear(_names, 0, _count);
            Array.Clear(_kids, 0, _count);
            _recent?.Clear();
            _count = 0;
            _liveCount = 0;
            _pool = new HashSet<string>(StringComparer.Ordinal);
            _logBase += _log.Count;
            _log.Clear();
            Interlocked.Increment(ref _version);
            Interlocked.Increment(ref _generation);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    /// <summary>Live entries under each root (root id → count, the root included), in one pass over the index.</summary>
    public Dictionary<int, int> CountByRoot()
    {
        var counts = new Dictionary<int, int>();
        Lock.EnterReadLock();
        try
        {
            int n = _count;
            // Root id + 1 per slot once known (0 = not resolved yet, -1 = orphaned), so each chain is walked once.
            var rootOf = new int[n];
            var chain = new List<int>(64);
            for (int i = 0; i < n; i++)
            {
                if (!_alive[i]) continue;
                int root = rootOf[i];
                if (root == 0)
                {
                    chain.Clear();
                    int cur = i;
                    root = -1;
                    while ((uint)cur < (uint)n && chain.Count <= 4096)
                    {
                        if (rootOf[cur] != 0) { root = rootOf[cur]; break; }
                        chain.Add(cur);
                        int parent = _parent[cur];
                        if (parent == NoParent) { root = cur + 1; break; }
                        cur = parent;
                    }
                    foreach (int c in chain) rootOf[c] = root;
                }
                if (root > 0) counts[root - 1] = counts.GetValueOrDefault(root - 1) + 1;
            }
        }
        finally { Lock.ExitReadLock(); }
        return counts;
    }

    /// <summary>Pre-sizes the arrays for <paramref name="capacity"/> slots (exact size, no doubling slack).</summary>
    public void EnsureCapacity(int capacity)
    {
        Lock.EnterWriteLock();
        try { if (capacity > _names.Length) Resize(capacity); }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Drops removed slots and renumbers the live entries densely, preserving their order. Returns the map from
    /// old id to new id (-1 for removed entries) so owners of ids can translate them; callers must hold every
    /// structure that stores ids (the indexers' maps) consistent with this call. Increments <see cref="Generation"/>.
    /// </summary>
    public int[] Compact()
    {
        int[] remap;
        Lock.EnterWriteLock();
        try
        {
            int n = _count;
            remap = new int[n];
            int k = 0;
            for (int i = 0; i < n; i++) remap[i] = _alive[i] ? k++ : -1;
            var pool = new HashSet<string>(k / 3 + 16, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                int j = remap[i];
                if (j < 0) continue;
                int p = _parent[i];
                _parent[j] = p == NoParent || (uint)p >= (uint)n ? NoParent : remap[p];
                string name = _names[i];
                if (!pool.TryGetValue(name, out var shared)) pool.Add(shared = name);
                _names[j] = shared;
                _size[j] = _size[i];
                _modified[j] = _modified[i];
                _created[j] = _created[i];
                if (_accessed != null) _accessed[j] = _accessed[i];
                _attrs[j] = _attrs[i];
                _alive[j] = true;
            }
            Array.Clear(_names, k, n - k);
            Array.Clear(_alive, k, n - k);
            // Counted again for the new ids (removed subtrees left their own counts behind).
            Array.Clear(_kids, 0, n);
            for (int j = 0; j < k; j++)
                if ((uint)_parent[j] < (uint)k) _kids[_parent[j]]++;
            _count = k;
            _liveCount = k;
            _pool = pool;
            if (_recent is { IsEmpty: false } recent)
            {
                var moved = new ConcurrentDictionary<int, long>();
                foreach (var (id, time) in recent)
                    if ((uint)id < (uint)n && remap[id] >= 0) moved[remap[id]] = time;
                _recent = moved;
            }
            _logBase += _log.Count;
            _log.Clear();
            Resize(Math.Max(1024, k + k / 32)); // ~3% headroom for live additions
            Interlocked.Increment(ref _version);
            Interlocked.Increment(ref _generation);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
        return remap;
    }

    void Resize(int n)
    {
        Array.Resize(ref _names, n);
        Array.Resize(ref _parent, n);
        Array.Resize(ref _size, n);
        Array.Resize(ref _modified, n);
        Array.Resize(ref _created, n);
        if (_accessed != null) Array.Resize(ref _accessed, n);
        Array.Resize(ref _attrs, n);
        Array.Resize(ref _alive, n);
        Array.Resize(ref _kids, n);
    }

    /// <summary>Raise <see cref="Changed"/> explicitly (for callers batching several mutations).</summary>
    public void RaiseChanged() => Changed?.Invoke();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool IsAliveNoCheck(int id) => (uint)id < (uint)_count && _alive[id];

    public bool IsAlive(int id) => IsAliveNoCheck(id);
    /// <summary>Name of the entry ("" for a removed entry).</summary>
    public string Name(int id) => _names[id] ?? "";
    public int Parent(int id) => _parent[id];
    public long Size(int id) => _size[id];
    public long Modified(int id) => _modified[id];
    public long Created(int id) => _created[id];
    /// <summary>Date accessed (FILETIME UTC); 0 when unknown or not indexed (<see cref="HasDateAccessed"/>).</summary>
    public long Accessed(int id)
    {
        var a = _accessed;
        return a != null && (uint)id < (uint)a.Length ? a[id] : 0;
    }
    /// <summary>When the entry was last seen created, changed, moved or renamed (FILETIME UTC); 0 = not since recording began.</summary>
    public long RecentChange(int id) => _recent is { } r && r.TryGetValue(id, out long t) ? t : 0;

    /// <summary>Every entry with a recent change (a copy).</summary>
    public KeyValuePair<int, long>[] RecentChanges() => _recent?.ToArray() ?? [];
    public FileAttributes Attributes(int id) => _attrs[id];
    public bool IsDirectory(int id) => (_attrs[id] & FileAttributes.Directory) != 0;

    /// <summary>Extension without the dot, lower-cased; empty for folders and extension-less names.</summary>
    public string Extension(int id)
    {
        if (IsDirectory(id)) return "";
        string n = _names[id] ?? "";
        int dot = n.LastIndexOf('.');
        return dot <= 0 || dot == n.Length - 1 ? "" : n[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Full path of the entry's containing folder ("" for roots).</summary>
    public string GetParentPath(int id)
    {
        int p = _parent[id];
        return p == NoParent ? "" : GetFullPath(p);
    }

    /// <summary>Full path of the entry. Roots render as "C:\" style; children as "C:\dir\file".</summary>
    public string GetFullPath(int id)
    {
        Span<int> chain = stackalloc int[256];
        int depth = 0;
        for (int cur = id; cur != NoParent && depth < chain.Length; cur = _parent[cur])
            chain[depth++] = cur;
        if (depth == 1) return _names[id] + "\\";
        var sb = new System.Text.StringBuilder(260);
        for (int i = depth - 1; i >= 0; i--)
        {
            sb.Append(_names[chain[i]] ?? "");
            if (i > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    /// <summary>Depth below the root (roots are 0).</summary>
    public int Depth(int id)
    {
        int d = 0;
        for (int cur = _parent[id]; cur != NoParent; cur = _parent[cur]) d++;
        return d;
    }
}
