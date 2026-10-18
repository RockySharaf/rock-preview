using System.Runtime.CompilerServices;

namespace Anything.Core;

/// <summary>
/// In-memory file name index stored as parallel arrays (struct-of-arrays) so millions of
/// entries stay compact. Ids are stable for the life of the index; removed slots are marked
/// dead and skipped. Volume/folder roots have <see cref="NoParent"/> as parent and their
/// name is the root path without a trailing separator (for example "C:" or "\\server\share").
/// All public members are thread-safe: mutators take the write lock, accessors are lock-free
/// reads of array slots (callers that need a consistent snapshot hold <see cref="Lock"/> for reading).
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
    // Name interning: about two thirds of all names on a typical disk are repeats ("index.js", "desktop.ini",
    // "#", "LICENSE"...), so every distinct name is stored once and shared.
    HashSet<string> _pool = new(StringComparer.Ordinal);
    int _count;
    int _liveCount;
    long _version;
    long _generation;

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

    public int AddRoot(string rootPath, FileAttributes attributes = FileAttributes.Directory)
        => Add(rootPath.TrimEnd('\\', '/'), NoParent, attributes | FileAttributes.Directory, -1, 0, 0);

    /// <summary>Adds one entry. Sizes are bytes (-1 = unknown, used for folders); dates are FILETIME UTC (0 = unknown).</summary>
    public int Add(string name, int parent, FileAttributes attributes, long size, long modifiedFileTimeUtc, long createdFileTimeUtc)
    {
        Lock.EnterWriteLock();
        try
        {
            int id = AddNoLock(name, parent, attributes, size, modifiedFileTimeUtc, createdFileTimeUtc);
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

        public int Add(string name, int parent, FileAttributes attributes, long size, long modifiedFileTimeUtc, long createdFileTimeUtc)
            => _index.AddNoLock(name, parent, attributes, size, modifiedFileTimeUtc, createdFileTimeUtc);

        /// <summary>Re-parents an entry inside a bulk operation (used when a child is seen before its parent).</summary>
        public void SetParent(int id, int parent)
        {
            _index._parent[id] = parent;
            _index.Touch(id);
        }
    }

    int AddNoLock(string name, int parent, FileAttributes attributes, long size, long modified, long created)
    {
        if (_count == _names.Length) Grow();
        int id = _count;
        _names[id] = Intern(name);
        _parent[id] = parent;
        _attrs[id] = attributes;
        _size[id] = (attributes & FileAttributes.Directory) != 0 && size == 0 ? -1 : size;
        _modified[id] = modified;
        _created[id] = created;
        _alive[id] = true;
        Touch(id);
        Volatile.Write(ref _count, id + 1);
        _liveCount++;
        Interlocked.Increment(ref _version);
        return id;
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
            if ((_attrs[id] & FileAttributes.Directory) == 0)
            {
                _alive[id] = false;
                _names[id] = null!; // release the name now; the slot itself is reclaimed by Compact
                _liveCount--;
                Touch(id);
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
            _names[id] = Intern(newName);
            _parent[id] = newParent;
            Touch(id);
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    /// <summary>Updates metadata; null arguments leave the value unchanged.</summary>
    public void Update(int id, FileAttributes? attributes = null, long? size = null, long? modifiedFileTimeUtc = null, long? createdFileTimeUtc = null)
    {
        Lock.EnterWriteLock();
        try
        {
            if (!IsAliveNoCheck(id)) return;
            if (attributes is { } a) _attrs[id] = a;
            if (size is { } s) _size[id] = s;
            if (modifiedFileTimeUtc is { } m) _modified[id] = m;
            if (createdFileTimeUtc is { } c) _created[id] = c;
            Touch(id);
            Interlocked.Increment(ref _version);
        }
        finally { Lock.ExitWriteLock(); }
        RaiseChanged();
    }

    public void Clear()
    {
        Lock.EnterWriteLock();
        try
        {
            Array.Clear(_alive, 0, _count);
            Array.Clear(_names, 0, _count);
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
                _attrs[j] = _attrs[i];
                _alive[j] = true;
            }
            Array.Clear(_names, k, n - k);
            Array.Clear(_alive, k, n - k);
            _count = k;
            _liveCount = k;
            _pool = pool;
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
        Array.Resize(ref _attrs, n);
        Array.Resize(ref _alive, n);
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
