namespace Anything.Core.Thumbs;

/// <summary>
/// One picture the grid asks for: the file (path as shown, any case), its stamp (<see cref="ThumbStamp.Unknown"/> when
/// the index had none), the size it is drawn at in the grid, and why it is wanted. <paramref name="Index"/> is the item's
/// place in the grid's list (-1 when not known), carried through to the job for the caller's own use.
/// </summary>
public readonly record struct ThumbRequest(string Path, ThumbStamp Stamp, int DisplaySize, ThumbPriority Priority, int Index = -1)
{
    /// <summary>The key of this request (path, stamp and the size class of its display size).</summary>
    public ThumbKey Key => ThumbKey.Of(Path, Stamp, DisplaySize);
}

/// <summary>
/// One unit of work: one key, however many cells or display sizes asked for it. The queue owns the job's state; a worker
/// that took it reads <see cref="Key"/>, <see cref="Path"/> and <see cref="Stamp"/> (fixed) and asks the queue for the
/// display sizes when it is done (<see cref="ThumbQueue.Complete"/>), because more may be asked for while it runs.
/// </summary>
public sealed class ThumbJob
{
    internal readonly List<int> Sizes = [];
    internal long Rank;
    internal long Sequence;

    internal ThumbJob(ThumbKey key, string path, ThumbStamp stamp)
    {
        Key = key;
        Path = path;
        Stamp = stamp;
    }

    /// <summary>The picture's key.</summary>
    public ThumbKey Key { get; }
    /// <summary>The file's path as the grid gave it (case kept, for opening the file).</summary>
    public string Path { get; }
    /// <summary>The stamp the grid gave (may be unknown: then the worker stats the file).</summary>
    public ThumbStamp Stamp { get; }
    /// <summary>
    /// The best priority any request for it has now. It can change until the job is taken, and afterwards only for the
    /// better (an idle job a viewport asks for while it runs becomes wanted on screen).
    /// </summary>
    public ThumbPriority Priority { get; internal set; }
    /// <summary>The list number of the request that last asked for it.</summary>
    public int ListNumber { get; internal set; }
    /// <summary>The list index of the request that last placed it, -1 when not known.</summary>
    public int Index { get; internal set; } = -1;
    /// <summary>Whether a worker took it (a started job is never dropped: its result goes to the store).</summary>
    public bool Started { get; internal set; }
}

/// <summary>
/// The request order: which picture to make next. The grid replaces the plan with each viewport
/// (<see cref="SubmitViewport"/>) and adds idle work (<see cref="SubmitIdle"/>); workers take the best job
/// (<see cref="TryTake"/>) and report it done (<see cref="Complete"/>). Jobs come out by priority (visible, ahead, behind,
/// idle), then in the order they were submitted. A new viewport drops the work not yet started for keys it no longer
/// holds (idle work included); a new list number drops everything not started. One job per key: a key asked for again,
/// even while a worker has it, joins that job and adds its display size. Thread-safe; every method is short and never
/// waits for a worker. <see cref="Changed"/> is raised (outside the lock, on the caller's thread) after any change.
/// </summary>
public sealed class ThumbQueue
{
    readonly object _gate = new();
    readonly Dictionary<ThumbKey, ThumbJob> _pending = [];
    readonly Dictionary<ThumbKey, ThumbJob> _running = [];
    readonly SortedSet<ThumbJob> _order = new(Comparer<ThumbJob>.Create(Compare));
    long _sequence;
    long _idleRank;
    int _list;
    bool _allowIdle;

    /// <summary>
    /// A queue that takes idle work when <paramref name="allowIdle"/> is true: with "Instant thumbnails" on. Off (the
    /// default setting) there is nowhere to keep pictures nobody is looking at, so the queue runs without the idle step.
    /// </summary>
    public ThumbQueue(bool allowIdle = true) => _allowIdle = allowIdle;

    /// <summary>Raised after jobs were added, dropped or finished (on the thread that made the change, outside the lock).</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether <see cref="SubmitIdle"/> takes work (the setting turned on or off while the program runs). Turning it off
    /// drops the idle work not yet started; started jobs finish.
    /// </summary>
    public bool AllowIdle
    {
        get { lock (_gate) return _allowIdle; }
        set
        {
            bool dropped = false;
            lock (_gate)
            {
                _allowIdle = value;
                if (!value)
                    foreach (var idle in _pending.Values.Where(j => j.Priority == ThumbPriority.Idle).ToList())
                    {
                        RemovePendingLocked(idle);
                        dropped = true;
                    }
            }
            if (dropped) Changed?.Invoke();
        }
    }

    /// <summary>The list number of the last viewport.</summary>
    public int ListNumber { get { lock (_gate) return _list; } }

    /// <summary>Jobs waiting for a worker.</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Jobs a worker has.</summary>
    public int RunningCount { get { lock (_gate) return _running.Count; } }

    /// <summary>Nothing waits and nothing runs: everything submitted is done.</summary>
    public bool IsIdle { get { lock (_gate) return _pending.Count == 0 && _running.Count == 0; } }

    /// <summary>Whether idle work waits (the order puts it last).</summary>
    public bool HasIdleWaiting { get { lock (_gate) return _order.Count > 0 && _order.Max!.Priority == ThumbPriority.Idle; } }

    /// <summary>
    /// Replaces the plan with <paramref name="ordered"/> (the grid's order: visible in reading order, ahead nearest first,
    /// behind). Jobs come out by the requests' priority, then by their place in this list. Work not started for keys
    /// not in the list is dropped; with a new <paramref name="listNumber"/> all work not started is dropped first.
    /// </summary>
    public void SubmitViewport(int listNumber, IReadOnlyList<ThumbRequest> ordered)
    {
        lock (_gate)
        {
            if (listNumber != _list) DropPendingLocked();
            _list = listNumber;
            var kept = new HashSet<ThumbKey>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                var key = r.Key;
                if (_running.TryGetValue(key, out var running))
                {
                    AddSize(running, r.DisplaySize);
                    running.ListNumber = listNumber;
                    // Wanted on screen now: a job begun as idle warming must be shown when it is done.
                    if (r.Priority < running.Priority) running.Priority = r.Priority;
                    continue;
                }
                if (_pending.TryGetValue(key, out var job))
                {
                    if (kept.Contains(key))
                    {
                        // Asked twice in one viewport (two display sizes in one class): one job serves both.
                        AddSize(job, r.DisplaySize);
                        if (r.Priority < job.Priority) Reorder(job, r.Priority, job.Rank, r.Index);
                        continue;
                    }
                    job.Sizes.Clear();
                    job.Sizes.Add(r.DisplaySize);
                    job.ListNumber = listNumber;
                    Reorder(job, r.Priority, i, r.Index);
                }
                else Add(NewJob(r, key, listNumber), r.Priority, i);
                kept.Add(key);
            }
            if (_pending.Count > kept.Count)
                foreach (var stale in _pending.Values.Where(j => !kept.Contains(j.Key)).ToList()) RemovePendingLocked(stale);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds idle work for list <paramref name="listNumber"/>, after everything else, in the order given. Ignored when the
    /// list is no longer the current one or idle work is not allowed (<see cref="AllowIdle"/>); keys already waiting or
    /// running are left as they are. Returns how many jobs were added.
    /// </summary>
    public int SubmitIdle(int listNumber, IReadOnlyList<ThumbRequest> requests)
    {
        int added = 0;
        lock (_gate)
        {
            if (listNumber != _list || !_allowIdle) return 0;
            foreach (var r in requests)
            {
                var key = r.Key;
                if (_running.TryGetValue(key, out var running)) { AddSize(running, r.DisplaySize); continue; }
                if (_pending.TryGetValue(key, out var job)) { AddSize(job, r.DisplaySize); continue; }
                Add(NewJob(r, key, listNumber), ThumbPriority.Idle, _idleRank++);
                added++;
            }
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// Takes the best waiting job whose priority is <paramref name="worst"/> or better and that
    /// <paramref name="accept"/> takes (a lane's filter: called under the queue's lock, so it must be quick and must not
    /// call back into the queue). The job then counts as running until <see cref="Complete"/>.
    /// </summary>
    public bool TryTake(ThumbPriority worst, Func<ThumbJob, bool>? accept, out ThumbJob job)
    {
        lock (_gate)
        {
            foreach (var j in _order)
            {
                if (j.Priority > worst) break;
                if (accept != null && !accept(j)) continue;
                _order.Remove(j);
                _pending.Remove(j.Key);
                j.Started = true;
                _running[j.Key] = j;
                job = j;
                return true;
            }
        }
        job = null!;
        return false;
    }

    /// <summary>Takes the best waiting job of any priority.</summary>
    public bool TryTake(out ThumbJob job) => TryTake(ThumbPriority.Idle, null, out job);

    /// <summary>
    /// A worker finished <paramref name="job"/> (made the picture, failed, or gave up): it stops running, and the display
    /// sizes wanted from it now come back (every size asked for, also while it ran; distinct, in the order asked).
    /// </summary>
    public IReadOnlyList<int> Complete(ThumbJob job)
    {
        int[] sizes;
        lock (_gate)
        {
            if (_running.TryGetValue(job.Key, out var j) && ReferenceEquals(j, job)) _running.Remove(job.Key);
            sizes = [.. job.Sizes];
        }
        Changed?.Invoke();
        return sizes;
    }

    /// <summary>
    /// Finishes <paramref name="job"/> only if it is still idle warming (nobody asked for it on screen): true when it was
    /// finished. False when a viewport asked for it meanwhile (it stays running, and the worker must answer it as wanted
    /// work): checked and finished under one lock, so a request that joins it at that very moment is never left without
    /// an answer.
    /// </summary>
    public bool CompleteIfIdle(ThumbJob job)
    {
        lock (_gate)
        {
            if (job.Priority != ThumbPriority.Idle) return false;
            if (_running.TryGetValue(job.Key, out var j) && ReferenceEquals(j, job)) _running.Remove(job.Key);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The display sizes wanted from <paramref name="job"/> so far (a copy, distinct, in the order asked), for a stand-in
    /// shown while the job still runs; <see cref="Complete"/> gives the final list.
    /// </summary>
    public IReadOnlyList<int> SizesOf(ThumbJob job)
    {
        lock (_gate) return [.. job.Sizes];
    }

    /// <summary>Drops every job not started (the grid closed, or the store was cleared). Running jobs finish.</summary>
    public void Clear()
    {
        lock (_gate) DropPendingLocked();
        Changed?.Invoke();
    }

    /// <summary>The waiting jobs, best first (a copy, for tests and diagnostics).</summary>
    public IReadOnlyList<ThumbJob> Snapshot() { lock (_gate) return [.. _order]; }

    /// <summary>Whether a job for <paramref name="key"/> waits for a worker (not started: a new viewport may still drop it).</summary>
    public bool IsWaiting(in ThumbKey key) { lock (_gate) return _pending.ContainsKey(key); }

    /// <summary>Whether <paramref name="job"/> itself is running (taken and not yet completed).</summary>
    public bool IsRunning(ThumbJob job)
    {
        lock (_gate) return _running.TryGetValue(job.Key, out var j) && ReferenceEquals(j, job);
    }

    /// <summary>Whether a job for <paramref name="key"/> waits or runs.</summary>
    public bool Contains(in ThumbKey key) { lock (_gate) return _pending.ContainsKey(key) || _running.ContainsKey(key); }

    ThumbJob NewJob(in ThumbRequest r, in ThumbKey key, int listNumber)
    {
        var job = new ThumbJob(key, r.Path, r.Stamp) { ListNumber = listNumber, Index = r.Index, Sequence = ++_sequence };
        job.Sizes.Add(r.DisplaySize);
        return job;
    }

    void Add(ThumbJob job, ThumbPriority priority, long rank)
    {
        job.Priority = priority;
        job.Rank = rank;
        _pending[job.Key] = job;
        _order.Add(job);
    }

    void Reorder(ThumbJob job, ThumbPriority priority, long rank, int index)
    {
        _order.Remove(job);
        job.Priority = priority;
        job.Rank = rank;
        job.Index = index;
        job.Sequence = ++_sequence;
        _order.Add(job);
    }

    void RemovePendingLocked(ThumbJob job)
    {
        _order.Remove(job);
        _pending.Remove(job.Key);
    }

    void DropPendingLocked()
    {
        _order.Clear();
        _pending.Clear();
    }

    static void AddSize(ThumbJob job, int size)
    {
        if (!job.Sizes.Contains(size)) job.Sizes.Add(size);
    }

    static int Compare(ThumbJob? a, ThumbJob? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int c = a.Priority.CompareTo(b.Priority);
        if (c != 0) return c;
        c = a.Rank.CompareTo(b.Rank);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }
}
