namespace Anything.Core.Content.Indexing;

/// <summary>Root status for read and delete decisions (GetRoots now, StateOfRoot after the other session merges).</summary>
internal interface IRootGate
{
    /// <summary>Status, entries and completeness of a root by its name ("C:", "D:\Data", "\\server\share"); null = not held.</summary>
    RootGateState? StateOf(string rootName);
    /// <summary>Asks for a refresh (debounced, off-thread).</summary>
    void Refresh();
}

/// <summary>What the gate knows about one root.</summary>
/// <param name="HasEntries">The root has entries (a gate that can't tell says true: the follower's own walk decides).</param>
/// <param name="Complete">Its scan finished, so a stored document it doesn't list really is gone.</param>
internal readonly record struct RootGateState(RootStatus Status, bool HasEntries, bool Complete);

/// <summary>
/// The gate in this worktree, backed by <see cref="IIndexManager.GetRoots"/> (status only). GetRoots counts every entry
/// under the index's read lock, so it is called off-thread, only on the name index's Scanning↔Ready edges (and when the
/// index was cleared since the last count), at most once a minute; HasEntries is left to the follower's own walk. What it
/// counted describes the index of that moment: after an edge away from Ready (a rescan or a Rebuild starting, a load) or
/// a Clear, no root is complete until a count made after it says so. After the other session merges, this becomes
/// StateOfRoot (one file).
/// </summary>
internal sealed class RootGate : IRootGate, IDisposable
{
    /// <summary>The least time between two GetRoots calls.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    /// <summary>Statuses as counted, with the edge count and the index generation they were counted at (swapped as one).</summary>
    sealed record Counted(Dictionary<string, RootStatus> Statuses, long Edges, long Generation);

    readonly IIndexManager _names;
    readonly FileIndex _index;
    readonly Func<long> _clockMs;
    readonly object _gate = new();
    Counted _counted = new(new Dictionary<string, RootStatus>(StringComparer.OrdinalIgnoreCase), -1, -1);
    long _edges;
    long _loadedAt = long.MinValue;
    bool _loading, _again, _disposed;
    IndexState _lastState;
    Timer? _later;

    /// <summary>A gate over the manager's roots; loads once at once.</summary>
    public RootGate(IIndexManager names, Func<long>? clockMs = null)
    {
        _names = names;
        _index = names.Index;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _lastState = names.State;
        names.StateChanged += OnStateChanged;
        _index.Compacted += OnCompacted;
        Refresh();
    }

    /// <summary>How many times GetRoots was called (tests).</summary>
    internal int Loads { get; private set; }

    /// <summary>Raised after a refresh brought new statuses (the follower wakes).</summary>
    public event Action? Changed;

    /// <inheritdoc/>
    public RootGateState? StateOf(string rootName)
    {
        var counted = Volatile.Read(ref _counted);
        if (!counted.Statuses.TryGetValue(rootName.TrimEnd('\\'), out var status)) return null;
        return new RootGateState(status, HasEntries: true, Complete: status == RootStatus.Ready && Current(counted));
    }

    /// <summary>
    /// Whether a count still describes the index: no edge away from Ready since, and no Clear (a Compact is followed). A
    /// count older than a Clear asks for a new one (at most once a minute).
    /// </summary>
    bool Current(Counted counted)
    {
        if (counted.Edges != Interlocked.Read(ref _edges)) return false;
        if (counted.Generation == _index.Generation) return true;
        Refresh();
        return false;
    }

    /// <summary>Inside Compact's write lock: a count of the generation before stays current (ids moved, roots didn't). O(1).</summary>
    void OnCompacted(int[] remap, long newGeneration)
    {
        var counted = Volatile.Read(ref _counted);
        if (counted.Generation == newGeneration - 1)
            Interlocked.CompareExchange(ref _counted, counted with { Generation = newGeneration }, counted);
    }

    /// <inheritdoc/>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_loading) { _again = true; return; }
            long now = _clockMs();
            long wait = _loadedAt == long.MinValue ? 0 : _loadedAt + (long)MinInterval.TotalMilliseconds - now;
            if (wait > 0)
            {
                // Too soon after the last count: once, when the minute is up.
                _later ??= new Timer(_ => { lock (_gate) { _later?.Dispose(); _later = null; } Refresh(); }, null, wait, Timeout.Infinite);
                return;
            }
            _loading = true;
            _loadedAt = now;
        }
        Task.Run(Load);
    }

    /// <summary>Loads the statuses now, on the caller's thread (tests).</summary>
    internal void LoadNow()
    {
        lock (_gate) { _loading = true; _loadedAt = _clockMs(); }
        Load();
    }

    void Load()
    {
        try
        {
            // Taken before counting: an edge or a Clear while GetRoots runs leaves this count not current.
            long edges = Interlocked.Read(ref _edges), generation = _index.Generation;
            var statuses = new Dictionary<string, RootStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in _names.GetRoots())
                statuses[root.Path.TrimEnd('\\')] = root.Status;
            Volatile.Write(ref _counted, new Counted(statuses, edges, generation));
            Loads++;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        bool again;
        lock (_gate)
        {
            _loading = false;
            again = _again;
            _again = false;
        }
        try { Changed?.Invoke(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        if (again) Refresh();
    }

    void OnStateChanged()
    {
        // O(1) on the manager's thread: an edge away from Ready (a rescan or a Rebuild starting) or into Loading makes the
        // last count not current at once (no root is complete until a later count says so); only an edge into Ready (from
        // Scanning, a load or an error), Ready → Scanning, or the end of a load asks for a count.
        var state = _names.State;
        var last = _lastState;
        _lastState = state;
        if (last == IndexState.Ready && state != IndexState.Ready || state == IndexState.Loading && last != IndexState.Loading)
            Interlocked.Increment(ref _edges);
        if ((last != IndexState.Ready && state == IndexState.Ready) || (last == IndexState.Ready && state == IndexState.Scanning)
            || (last == IndexState.Loading && state != IndexState.Loading))
            Refresh();
    }

    /// <summary>Stops listening to the manager and the index.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _later?.Dispose();
            _later = null;
        }
        _names.StateChanged -= OnStateChanged;
        _index.Compacted -= OnCompacted;
    }
}

/// <summary>
/// The demo's gate: every root of the index is Ready, has entries and is complete once the names are Ready (the demo
/// manager lists no roots of its own).
/// </summary>
internal sealed class AllRootsReadyGate(IIndexManager names) : IRootGate
{
    /// <inheritdoc/>
    public RootGateState? StateOf(string rootName) => names.State == IndexState.Ready
        ? new RootGateState(RootStatus.Ready, true, true)
        : new RootGateState(RootStatus.Scanning, true, false);

    /// <inheritdoc/>
    public void Refresh() { }
}
