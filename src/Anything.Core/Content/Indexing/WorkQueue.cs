namespace Anything.Core.Content.Indexing;

/// <summary>
/// Files waiting to be read, by priority (most opened, newest, extractor upgrades, OCR later), local and network apart so
/// the two lanes run side by side; network files round-robin over shares. It holds FileIndex ids of the follower's
/// generation and a priority only (12 bytes a file): the key, path and stamp are worked out again at dispatch, so a
/// queue of 850,000 files costs about 10 MB. A bitset says which ids are queued, so duplicates and removals are cheap
/// (a removed id's heap entry is skipped when it comes up).
/// </summary>
internal sealed class WorkQueue
{
    /// <summary>Queue levels: most opened first, then newest, then extractor upgrades, then OCR (reserved).</summary>
    public const int Opened = 0, Newest = 1, Upgrade = 2, Ocr = 3;
    const int LevelShift = 58;
    const long MaxTime = (1L << LevelShift) - 1;

    readonly Heap _local = new();
    readonly Dictionary<string, Heap> _network = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _shares = [];
    int _next;
    ulong[] _queued = new ulong[1024];
    ulong[] _isNetwork = new ulong[1024];
    int _localCount, _networkCount;

    /// <summary>The priority of a file at a level, newer (by FILETIME) first within it; lower comes out sooner.</summary>
    public static long Priority(int level, long recencyFileTime)
        => ((long)Math.Clamp(level, 0, 3) << LevelShift) | (MaxTime - Math.Clamp(recencyFileTime, 0, MaxTime));

    /// <summary>The level of a priority.</summary>
    public static int LevelOf(long priority) => (int)(priority >> LevelShift);

    /// <summary>Files queued.</summary>
    public int Count => _localCount + _networkCount;

    /// <summary>Local files queued.</summary>
    public int LocalCount => _localCount;

    /// <summary>Network files queued.</summary>
    public int NetworkCount => _networkCount;

    /// <summary>Whether an id is queued.</summary>
    public bool Contains(int id) => Bit(_queued, id);

    /// <summary>
    /// Queues a file (network = its root's name, null for local). An id already queued keeps its place, unless the new
    /// priority is the most-opened level, which moves it up.
    /// </summary>
    public void Enqueue(int id, long priority, string? networkRoot)
    {
        if (id < 0) return;
        bool had = Contains(id);
        if (had && LevelOf(priority) != Opened) return;
        if (!had)
        {
            Grow(id);
            Set(_queued, id, true);
            Set(_isNetwork, id, networkRoot != null);
            if (networkRoot != null) _networkCount++; else _localCount++;
        }
        Heap heap;
        if (networkRoot == null) heap = _local;
        else if (!_network.TryGetValue(networkRoot, out heap!))
        {
            heap = new Heap();
            _network[networkRoot] = heap;
            _shares.Add(networkRoot);
        }
        heap.Push(priority, id);
    }

    /// <summary>Takes the next local file; false when none.</summary>
    public bool TryDequeueLocal(out int id, out long priority) => TryPop(_local, out id, out priority);

    /// <summary>Takes the next network file, round-robin over the shares that <paramref name="ready"/> allows; false when none.</summary>
    public bool TryDequeueNetwork(Func<string, bool> ready, out int id, out long priority, out string share)
    {
        for (int tried = 0; tried < _shares.Count; tried++)
        {
            int i = (_next + tried) % _shares.Count;
            string s = _shares[i];
            var heap = _network[s];
            if (heap.Entries == 0 || !ready(s)) continue;
            if (TryPop(heap, out id, out priority))
            {
                _next = (i + 1) % _shares.Count;
                share = s;
                return true;
            }
        }
        id = -1;
        priority = 0;
        share = "";
        return false;
    }

    /// <summary>Whether a share may have files queued (stale entries are dropped when it is asked for one).</summary>
    public bool HasNetwork(string share) => _network.TryGetValue(share, out var h) && h.Entries > 0;

    /// <summary>Unqueues an id (its heap entry is skipped later).</summary>
    public void Remove(int id)
    {
        if (!Contains(id)) return;
        Set(_queued, id, false);
        if (Bit(_isNetwork, id)) _networkCount--; else _localCount--;
    }

    /// <summary>Translates every id through a FileIndex.Compact remap; removed ids leave the queue.</summary>
    public void Remap(int[] remap)
    {
        var oldQueued = _queued;
        _queued = new ulong[Math.Max(16, (remap.Length >> 6) + 1)];
        _isNetwork = new ulong[_queued.Length];
        _localCount = _networkCount = 0;
        _local.Remap(remap, oldQueued, this, network: false);
        foreach (var heap in _network.Values) heap.Remap(remap, oldQueued, this, network: true);
    }

    /// <summary>Empties the queue (after Clear: a full reconcile queues again).</summary>
    public void Clear()
    {
        _local.Clear();
        _network.Clear();
        _shares.Clear();
        Array.Clear(_queued);
        Array.Clear(_isNetwork);
        _localCount = _networkCount = 0;
    }

    bool TryPop(Heap heap, out int id, out long priority)
    {
        while (heap.Pop(out priority, out id))
        {
            if (!Contains(id)) continue;
            Remove(id);
            return true;
        }
        id = -1;
        priority = 0;
        return false;
    }

    void Grow(int id)
    {
        int need = (id >> 6) + 1;
        if (need <= _queued.Length) return;
        int size = Math.Max(need, _queued.Length * 2);
        Array.Resize(ref _queued, size);
        Array.Resize(ref _isNetwork, size);
    }

    static bool Bit(ulong[] bits, int id) => (uint)id < (uint)(bits.Length << 6) && (bits[id >> 6] & (1UL << id)) != 0;

    static void Set(ulong[] bits, int id, bool on)
    {
        if (on) bits[id >> 6] |= 1UL << id;
        else bits[id >> 6] &= ~(1UL << id);
    }

    /// <summary>A binary min-heap of (priority, id) in two parallel arrays.</summary>
    sealed class Heap
    {
        long[] _prio = new long[64];
        int[] _ids = new int[64];
        int _n;

        /// <summary>Heap entries, stale ones included.</summary>
        public int Entries => _n;

        public void Push(long priority, int id)
        {
            if (_n == _prio.Length)
            {
                Array.Resize(ref _prio, _n * 2);
                Array.Resize(ref _ids, _n * 2);
            }
            int i = _n++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_prio[parent] <= priority) break;
                _prio[i] = _prio[parent];
                _ids[i] = _ids[parent];
                i = parent;
            }
            _prio[i] = priority;
            _ids[i] = id;
        }

        public bool Pop(out long priority, out int id)
        {
            if (_n == 0) { priority = 0; id = -1; return false; }
            priority = _prio[0];
            id = _ids[0];
            _n--;
            if (_n > 0) Sift(_prio[_n], _ids[_n], 0);
            else if (_prio.Length > 4096) { _prio = new long[64]; _ids = new int[64]; }
            return true;
        }

        void Sift(long p, int v, int i)
        {
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= _n) break;
                if (child + 1 < _n && _prio[child + 1] < _prio[child]) child++;
                if (_prio[child] >= p) break;
                _prio[i] = _prio[child];
                _ids[i] = _ids[child];
                i = child;
            }
            _prio[i] = p;
            _ids[i] = v;
        }

        public void Remap(int[] remap, ulong[] oldQueued, WorkQueue q, bool network)
        {
            int k = 0;
            for (int i = 0; i < _n; i++)
            {
                int id = _ids[i];
                int to = (uint)id < (uint)remap.Length ? remap[id] : -1;
                if (!Bit(oldQueued, id) || to < 0 || q.Contains(to)) continue;
                q.Grow(to);
                Set(q._queued, to, true);
                Set(q._isNetwork, to, network);
                if (network) q._networkCount++; else q._localCount++;
                _prio[k] = _prio[i];
                _ids[k] = to;
                k++;
            }
            _n = k;
            for (int i = (_n >> 1) - 1; i >= 0; i--) Sift(_prio[i], _ids[i], i);
        }

        public void Clear() => _n = 0;
    }
}

/// <summary>How a settling file is to be handled when it comes due.</summary>
[Flags]
internal enum SettleKind : byte { None = 0, New = 1, Changed = 2, StatFirst = 4 }

/// <summary>
/// Files touched recently wait here until they have been left alone for 5 s (a file being written, a safe-save), then
/// go to the queue; keyed by FileIndex id of the follower's generation.
/// </summary>
internal sealed class SettleTable
{
    /// <summary>How long a file must be left alone before it is read.</summary>
    public const int SettleMs = 5000;
    readonly Dictionary<int, (long Due, SettleKind Kind)> _items = [];

    /// <summary>Files settling.</summary>
    public int Count => _items.Count;

    /// <summary>A touch: (re)starts the file's settle time; kinds add up.</summary>
    public void Touch(int id, long nowMs, SettleKind kind, long notBeforeMs = 0)
    {
        var kinds = _items.TryGetValue(id, out var had) ? had.Kind | kind : kind;
        _items[id] = (Math.Max(nowMs + SettleMs, notBeforeMs), kinds);
    }

    /// <summary>Forgets a file.</summary>
    public void Remove(int id) => _items.Remove(id);

    /// <summary>Whether a file is settling.</summary>
    public bool Contains(int id) => _items.ContainsKey(id);

    /// <summary>The files that came due, removed from the table.</summary>
    public List<(int Id, SettleKind Kind)> TakeDue(long nowMs)
    {
        var due = new List<(int, SettleKind)>();
        foreach (var (id, s) in _items)
            if (s.Due <= nowMs) due.Add((id, s.Kind));
        foreach (var (id, _) in due) _items.Remove(id);
        return due;
    }

    /// <summary>When the next file comes due; long.MaxValue when none.</summary>
    public long NextDue()
    {
        long next = long.MaxValue;
        foreach (var s in _items.Values) next = Math.Min(next, s.Due);
        return next;
    }

    /// <summary>Translates ids through a FileIndex.Compact remap.</summary>
    public void Remap(int[] remap)
    {
        var old = _items.ToArray();
        _items.Clear();
        foreach (var (id, s) in old)
            if ((uint)id < (uint)remap.Length && remap[id] >= 0) _items[remap[id]] = s;
    }

    /// <summary>Forgets everything (Clear).</summary>
    public void Clear() => _items.Clear();
}

/// <summary>
/// Temporary trouble (in use, changed while read, offline, a reader exception): nothing is recorded, and the file is
/// tried again after 1 minute, 10 minutes and 1 hour, then not before the next session (§10). Keyed by document key, so
/// it survives a Compact or Clear; the id (of the follower's generation) is kept to queue it again when due.
/// </summary>
internal sealed class RetryTable
{
    static readonly long[] Delays = [60_000, 600_000, 3_600_000];
    readonly Dictionary<ulong, (int Attempts, long Due, int FileId)> _items = [];

    /// <summary>Files waiting to be tried again.</summary>
    public int Count => _items.Count;

    /// <summary>Records one more failure; returns when the file may be tried again (long.MaxValue = next session).</summary>
    public long Fail(ulong key, int fileId, long nowMs)
    {
        int attempts = _items.TryGetValue(key, out var had) ? had.Attempts + 1 : 1;
        long due = attempts <= Delays.Length ? nowMs + Delays[attempts - 1] : long.MaxValue;
        _items[key] = (attempts, due, fileId);
        return due;
    }

    /// <summary>The file was read (or recorded): forgets its failures.</summary>
    public void Succeeded(ulong key) => _items.Remove(key);

    /// <summary>Whether a file must not be read yet.</summary>
    public bool Waiting(ulong key, long nowMs) => _items.TryGetValue(key, out var r) && r.Due > nowMs;

    /// <summary>Attempts so far.</summary>
    public int Attempts(ulong key) => _items.TryGetValue(key, out var r) ? r.Attempts : 0;

    /// <summary>Files due again (their entry stays, with its attempts, until they succeed or fail again).</summary>
    public List<(ulong Key, int FileId)> TakeDue(long nowMs)
    {
        var due = new List<(ulong, int)>();
        foreach (var (key, r) in _items)
            if (r.Due <= nowMs && r.FileId >= 0) due.Add((key, r.FileId));
        foreach (var (key, _) in due) _items[key] = _items[key] with { FileId = -1 };
        return due;
    }

    /// <summary>When the next file comes due; long.MaxValue when none.</summary>
    public long NextDue()
    {
        long next = long.MaxValue;
        foreach (var r in _items.Values) if (r.FileId >= 0) next = Math.Min(next, r.Due);
        return next;
    }

    /// <summary>Translates ids through a FileIndex.Compact remap.</summary>
    public void Remap(int[] remap)
    {
        foreach (var key in _items.Keys.ToArray())
        {
            var r = _items[key];
            int to = (uint)r.FileId < (uint)remap.Length ? remap[r.FileId] : -1;
            _items[key] = r with { FileId = to };
        }
    }

    /// <summary>Ids are meaningless after a Clear: keeps the back-off by key, drops the ids (the reconcile finds the files again).</summary>
    public void ForgetIds()
    {
        foreach (var key in _items.Keys.ToArray()) _items[key] = _items[key] with { FileId = -1 };
    }

    /// <summary>Forgets everything ("Read problem files again").</summary>
    public void Clear() => _items.Clear();
}

/// <summary>A file that keeps changing is read at most once every 10 minutes (keyed by document key).</summary>
internal sealed class RepeatLimiter
{
    /// <summary>The least time between two reads of one file.</summary>
    public const long IntervalMs = 600_000;
    readonly Dictionary<ulong, long> _lastRead = [];

    /// <summary>The earliest time the file may be read again (0 = now).</summary>
    public long NotBefore(ulong key) => _lastRead.TryGetValue(key, out long last) ? last + IntervalMs : 0;

    /// <summary>The file was read now.</summary>
    public void Read(ulong key, long nowMs)
    {
        _lastRead[key] = nowMs;
        if (_lastRead.Count > 4096) Prune(nowMs);
    }

    /// <summary>Drops reads older than the interval.</summary>
    public void Prune(long nowMs)
    {
        foreach (var key in _lastRead.Where(p => p.Value + IntervalMs <= nowMs).Select(p => p.Key).ToArray()) _lastRead.Remove(key);
    }
}
