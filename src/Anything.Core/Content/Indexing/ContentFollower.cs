using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Store;

namespace Anything.Core.Content.Indexing;

/// <summary>What the follower tells the facade.</summary>
internal interface IFollowerHost
{
    /// <summary>Phase, progress or counts may have changed (the facade throttles StatusChanged to once a second).</summary>
    void StatusMaybeChanged();
    /// <summary>
    /// A commit or merge made new words searchable: the facade debounces ContentChanged by 3 s, or by 60 s while a long
    /// first pass commits every few seconds (<paramref name="longPass"/>), so open windows don't search again all the time.
    /// </summary>
    void Committed(bool longPass);
    /// <summary>
    /// Stored documents became findable without a commit (a reconcile bound them, the index caught up, a rescan's new ids
    /// were bound): searches that reported ContentPending meanwhile are stale, so the facade raises ContentChanged at once
    /// (coalesced, not on the commit debounce).
    /// </summary>
    void Linked();
    /// <summary>
    /// The follower stopped on its own (errors in a row): the facade detaches its handlers from the name index and lets
    /// go of it, so the next Apply, Rebuild or Read problem files again starts a new one.
    /// </summary>
    void Stopped(ContentFollower follower);
    /// <summary>
    /// The follower let go of its store (a Rebuild deletes it): the facade drops the document sets searches cached and
    /// cancels their lookups, which hold views of that store (DocSetCache.Clear), so its files can be deleted.
    /// </summary>
    void StoreReleased();
}

/// <summary>
/// Follows FileIndex, reconciles, schedules reads, commits and merges on its own thread ("Rock Preview content follower",
/// background mode). The name index is its only change feed: it reads FileIndex under the read lock in slices of at
/// most 16,384 ids, re-checking Generation after each, never doing file I/O or taking a root's Sync there. Its own state
/// is keyed by FileIndex id of one generation (<see cref="_gen"/>): ids stay in that generation until the Compacted
/// remaps queued by the facade's handler are drained here (everything translated together), or a Clear makes a full
/// reconcile rebuild them. Commands from the facade (Apply, Pause, Resume, Rebuild, RetryProblems, Checkpoint) are queued
/// and run in order on its thread; after Stop they are ignored. <see cref="RunOnce"/> runs one iteration on the caller's
/// thread (tests).
/// </summary>
internal sealed class ContentFollower : IDisposable
{
    /// <summary>The most ids one read-lock slice covers (the adaptive slice starts here and shrinks when slices run long).</summary>
    const int Slice = 16_384;
    /// <summary>The slice of the loops that do more per id (touched ids, renamed folders, orphan names).</summary>
    const int SmallSlice = 4096;
    const long DebounceMs = 2000;
    const long DeleteGraceMs = 10_000;
    const int CommitDocs = 2000;
    const long CommitBytes = 16L << 20;
    const long CommitBusyMs = 300_000;
    const long CommitIdleMs = 2000;
    const long OpenedRefreshMs = 600_000;
    const int RekeyChunk = 512;
    const long PurgeAfterMs = 90L * 24 * 3600 * 1000;
    const long MergeCheckMs = 60_000;
    const long HistogramEveryMs = 10_000;

    readonly IIndexManager _names;
    readonly FileIndex _index;
    readonly Func<IContentStore> _openStore;
    readonly IContentSource _source;
    readonly IRootGate _gate;
    readonly IPowerPolicy _power;
    readonly ContentIndexOptions _options;
    readonly IFollowerHost _host;
    readonly Func<long> _clockMs;
    readonly Func<DateTime> _utcNow;
    readonly Func<string, (string Key, string RelativePrefix, string DisplayRoot)> _resolveVolume;
    readonly string _contentFolder;
    readonly bool _inline;

    readonly CancellationTokenSource _cts = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ConcurrentQueue<Command> _commands = new();
    readonly ConcurrentQueue<(int[] Remap, long Generation)> _compacts = new();
    readonly object _stopGate = new();
    Thread? _thread;
    volatile bool _stopping;
    bool _shutDown, _background, _noReading;
    long _stopDeadline = long.MaxValue;
    int _dirty = 1;

    ContentSettings _settings;
    ContentScope _scope;
    readonly RootTable _roots = new();
    readonly FolderWalker _walker = new();
    IContentStore? _store;
    IStoreView? _view;
    ContentView? _published;
    DocIdMap _map = DocIdMap.Empty(-1);
    long _gen = -1, _seq, _pathVersion;
    CrashMarker? _marker;
    ReadScheduler? _reader;

    // A full reconcile works the queue out again into a new one, put in its place when it is done (no staging list of
    // every file to read in between).
    WorkQueue _queue = new();
    readonly SettleTable _settle = new();
    readonly RetryTable _retries = new();
    readonly RepeatLimiter _limiter = new();
    readonly MassDeleteGuard _guard = new();
    readonly Dictionary<ulong, PendingDelete> _pendingDeletes = [];
    readonly Dictionary<int, ulong> _rekeys = [];
    readonly Dictionary<ulong, MarkerEntry> _crashed = [];
    readonly Dictionary<string, List<int>> _parked = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ReadJob> _inFlight = [];
    HashSet<ulong> _opened = [];
    long _openedAt = long.MinValue;

    // The batch: everything waiting for the next commit, kept here (the store's own batch is made only to commit it), so a
    // commit that fails loses nothing and is tried again later. Adds and copies are in position order; a newer version of
    // a key replaces its waiting one in place. Copies and deletes name documents of _view (no merge runs while it waits).
    readonly List<PendingAdd> _adds = [];
    readonly Dictionary<ulong, PendingAdd> _batchKeys = [];
    readonly Dictionary<int, PendingAdd> _batchById = [];
    readonly List<DocRef> _batchDeletes = [];
    readonly HashSet<DocRef> _batchDeleteSet = [];
    long _batchRaw, _batchStartMs;
    // Words that must leave search now (a scope removal's deletes, a file marked "don't index contents"): the batch is
    // committed at the next step, whatever the cadence.
    bool _commitSoon;
    double _commitBytesPerMs = 10_000;
    // Commits that failed in a row, and when the next try is due (10 s, 1 min, 5 min, then every 15 min).
    int _commitFailures;
    long _commitRetryAt;
    /// <summary>Waits before each new try of a commit that failed (the last one repeats).</summary>
    static readonly long[] CommitRetryMs = [10_000, 60_000, 300_000, 900_000];

    // A Rebuild in progress: the store whose files are being deleted (once its DeleteAll ran, later tries only finish what
    // it left), and when to try again; then the reopen. A store that couldn't start deleting (open read-only: another
    // program holds the writer lock, a file couldn't be read) is opened again for each try (_doomedBlocked meanwhile).
    IContentStore? _doomed;
    bool _doomedStarted, _doomedBlocked;
    // "Read problem files again" asked while the store was read-only: done once a store that can be written is open.
    bool _retryProblemsPending;
    // The crash marker another copy held (or that couldn't be written) when the follower opened: opened again on a timer.
    long _markerRetryAt;
    int _markerAttempts;
    long _deleteRetryAt, _reopenAt;
    int _deleteAttempts, _reopenAttempts;
    // A store that opened read-only because a file was in use or a share didn't answer (IsUnavailable) is opened again on
    // a back-off timer. A damaged segment (HasDamage) is dropped by an otherwise empty commit, then its files are read
    // again; the idle scrub verifies one segment at a time once after open.
    long _unavailableRetryAt, _damageRetryAt, _lastScrub;
    int _unavailableAttempts;
    bool _scrubbed;
    /// <summary>Waits before each new open of a store that was unavailable (the last one repeats).</summary>
    static readonly long[] UnavailableRetryMs = [30_000, 60_000, 300_000];
    /// <summary>How often the idle scrub verifies the next segment, and a failed drop of a damaged one is tried again.</summary>
    const long ScrubEveryMs = 10_000;
    /// <summary>Waits before each new try of a Rebuild's delete, or of opening the store again (the last one repeats).</summary>
    static readonly long[] DeleteRetryMs = [2000, 10_000, 30_000];

    bool _isOpen, _reconcilePending = true, _reconciling, _scopeChanged = true, _firstReconcileDone, _deleting;
    // Times start at 0: the follower clock starts at 1,000,000 ms, so "now - 0" is never short of an interval (MinValue would overflow).
    long _lastStepMs, _reconcileAt = long.MaxValue, _queueEmptySince = long.MinValue, _lastMergeCheck;
    long _rekeyNotBefore, _pausedUntil, _lastHistogram, _histogramCommit = -1;
    int _mergeCheckedCommits = -1;
    readonly HashSet<ushort> _orphanPasses = [];
    readonly Dictionary<int, bool> _gateReady = [];
    // Volumes whose orphans a reconcile kept because their root wasn't Ready and complete (or no root held them): an
    // orphan-only pass decides them when one of their roots becomes ready.
    readonly HashSet<ushort> _orphansKept = [];
    PowerState _powerState = PowerState.Unlimited;
    long _powerAt;
    int _inScope;
    readonly Queue<long> _readTimes = new();
    long _readingSince = long.MinValue;
    string? _error;
    // A commit that failed: shown as the reason while reading goes on; the next good commit clears it.
    string? _saveTrouble;
    Dictionary<DocStatus, int> _histogram = [];
    ContentStatus _status = new(ContentPhase.Preparing, 0, 0, 0, 0, 0, null, null, false);

    /// <summary>The longest read-lock hold so far, in milliseconds (tests).</summary>
    internal long MaxReadLockMs { get; private set; }
    /// <summary>Full reconciles run (tests).</summary>
    internal int FullReconciles { get; private set; }
    /// <summary>Reconciles of one volume run (a root that completed, a root rescanned; tests).</summary>
    internal int VolumeReconciles { get; private set; }
    /// <summary>More touched ids than this in one step take a reconcile instead (tests lower it).</summary>
    internal int MaxTouched { get; set; } = 100_000;

    /// <summary>Starts the measurements again (tests).</summary>
    internal void ResetMeasurements() => MaxReadLockMs = 0;
    /// <summary>Files read (tests).</summary>
    internal int Reads { get; private set; }
    /// <summary>Commits made (tests).</summary>
    internal int Commits { get; private set; }

    /// <summary>A follower of names.Index; nothing happens until <see cref="Start"/> or <see cref="RunOnce"/>.</summary>
    public ContentFollower(IIndexManager names, ContentSettings settings, Func<IContentStore> openStore, IContentSource source,
        IRootGate gate, IPowerPolicy power, ContentIndexOptions options, IFollowerHost host)
    {
        _names = names;
        _index = names.Index;
        _settings = settings;
        _openStore = openStore;
        _source = source;
        _gate = gate;
        _power = options.IgnorePower ? NoPowerPolicy.Instance : power;
        _options = options;
        _host = host;
        _inline = options.InlineReads || options.ManualLoop;
        _contentFolder = options.Folder ?? "";
        if (options.Clock is { } clock)
        {
            var epoch = clock();
            _clockMs = () => (long)(clock() - epoch).TotalMilliseconds + 1_000_000;
            _utcNow = clock;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            _clockMs = () => sw.ElapsedMilliseconds + 1_000_000;
            _utcNow = () => DateTime.UtcNow;
        }
        _resolveVolume = options.VolumeResolver ?? VolumeKeys.For;
        // Resolved for real on the follower thread (Open): volume names come from Win32.
        _scope = ContentScope.Build(settings, options.AllRootsInScope, [], _ => null);
    }

    /// <summary>The published view's slot (the facade's AcquireView reads it with TryAddRef; its Compacted handler swaps it).</summary>
    internal ref ContentView? Published => ref _published;

    /// <summary>The status now (cheap, any thread).</summary>
    public ContentStatus Status => Volatile.Read(ref _status);

    /// <summary>A full reconcile is pending or running (searches report ContentPending).</summary>
    public bool CatchingUp => Volatile.Read(ref _reconcilePending) || Volatile.Read(ref _reconciling) || !Volatile.Read(ref _firstReconcileDone);

    /// <summary>The roots for path → key on other threads.</summary>
    public RootSnapshot Roots => _roots.Snapshot;

    /// <summary>The scope in force (immutable).</summary>
    public ContentScope Scope => Volatile.Read(ref _scope);

    /// <summary>The settings followed (a copy nobody edits).</summary>
    public ContentSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Counts by status of the live documents (Settings).</summary>
    public IReadOnlyDictionary<DocStatus, int> CountsByStatus => Volatile.Read(ref _histogram);

    /// <summary>Documents bound to their files now (tests).</summary>
    internal DocIdMap Map => _map;

    /// <summary>The follower's own store view (tests).</summary>
    internal IStoreView? View => _view;

    /// <summary>The reader lanes (tests).</summary>
    internal ReadScheduler? Reader => _reader;

    /// <summary>Files waiting to be read or re-keyed (tests).</summary>
    internal int Waiting => _queue.Count + _settle.Count + _inFlight.Count + _rekeys.Count + _parked.Values.Sum(p => p.Count);

    /// <summary>Documents waiting for their grace to run out (tests).</summary>
    internal int PendingDeletes => _pendingDeletes.Count;

    /// <summary>Re-keys recorded and not done yet (tests).</summary>
    internal int PendingRekeys => _rekeys.Count;

    /// <summary>Compact remaps queued and not drained yet (tests).</summary>
    internal int QueuedCompacts => _compacts.Count;

    /// <summary>Volumes whose deletes the mass-delete guard holds back (tests).</summary>
    internal int GuardedVolumes => _guard.Postponed;

    /// <summary>Starts the follower thread.</summary>
    public void Start()
    {
        lock (_stopGate)
        {
            if (_stopping || _thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Rock Preview content follower" };
            _thread.Start();
        }
    }

    /// <summary>Something changed (index, roots, power): O(1), no lock, safe from any thread and inside any lock.</summary>
    public void MarkDirty()
    {
        Interlocked.Exchange(ref _dirty, 1);
        Wake();
    }

    /// <summary>Wakes the thread without marking the index changed.</summary>
    public void Wake()
    {
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>A Compact ran (the facade's handler, inside Compact's write lock): queued, never blocks; nothing once stopping.</summary>
    public void Compacted(int[] remap, long newGeneration)
    {
        // A stopped follower never drains the queue: each remap (4 bytes an entry) would stay for good.
        if (_stopping || Volatile.Read(ref _shutDown)) return;
        _compacts.Enqueue((remap, newGeneration));
        Wake();
    }

    /// <summary>Queues a command for the follower thread; ignored after Stop (false: the follower is stopping or gave up).</summary>
    public bool Post(Command command)
    {
        if (_stopping) { command.Done?.Set(); return false; }
        _commands.Enqueue(command);
        Wake();
        return true;
    }

    /// <summary>Stopping, stopped or given up: commands are ignored from now on.</summary>
    internal bool Stopping => _stopping;

    /// <summary>
    /// Stops within the wait: cancels reading, commits what fits, releases files. Idempotent and final; past the wait the
    /// thread is left to end on its own (a background thread; a commit is atomic either way).
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        Thread? thread;
        lock (_stopGate)
        {
            if (_shutDown) return;
            if (!_stopping) _stopDeadline = _clockMs() + (long)wait.TotalMilliseconds;
            _stopping = true;
            thread = _thread;
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _reader?.Cancel();
        Wake();
        if (thread == null) Shutdown();
        else if (thread != Thread.CurrentThread) thread.Join(wait);
        while (_commands.TryDequeue(out var c)) c.Done?.Set();
    }

    /// <summary>Stops (800 ms).</summary>
    public void Dispose() => Stop(TimeSpan.FromMilliseconds(800));

    /// <summary>
    /// Waits, at most <paramref name="wait"/>, for the thread to end: a follower that gave up commits its last batch and
    /// lets go of the store and the crash marker on its way out (Stop returns at once then, as it is already stopping).
    /// </summary>
    internal void WaitForExit(TimeSpan wait)
    {
        Thread? thread;
        lock (_stopGate) thread = _thread;
        if (thread != null && thread != Thread.CurrentThread) thread.Join(wait);
    }

    // ───────────────────────────── loop ─────────────────────────────

    void Loop()
    {
        _background = ContentNative.EnterBackgroundMode();
        using var quiet = Anything.Core.Indexing.Drives.QuietErrors();
        bool gaveUp = false;
        try
        {
            int failures = 0;
            while (!_stopping)
            {
                bool more;
                try
                {
                    more = Iterate();
                    failures = 0;
                }
                catch (OperationCanceledException) when (_stopping) { break; }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A bug or a store fault in one iteration: what was in flight is dropped and a full reconcile (index
                    // only) works the state out again. Five in a row stop the follower for the session.
                    if (++failures >= 5)
                    {
                        _error = "Search inside files stopped after an error (" + e.GetType().Name + ")";
                        PublishStatus();
                        gaveUp = true;
                        break;
                    }
                    try { DiscardBatch(); } catch (Exception x) when (x is not OutOfMemoryException) { }
                    _reconcilePending = true;
                    BackOff(failures);
                    continue;
                }
                if (_stopping) break;
                long now = _clockMs();
                long wait = more ? 0 : Math.Clamp(NextDeadline(now) - now, 10, 2000);
                if (wait > 0) _wake.WaitOne((int)wait);
            }
        }
        catch (OperationCanceledException) when (_stopping) { }
        finally
        {
            if (gaveUp)
            {
                // Final, as a Stop is: commands are ignored from now on, and Compacted queues nothing more.
                lock (_stopGate) _stopping = true;
            }
            Shutdown();
            ContentNative.LeaveBackgroundMode();
            if (gaveUp)
            {
                while (_commands.TryDequeue(out var c)) c.Done?.Set();
                try { _host.Stopped(this); } catch (Exception e) when (e is not OutOfMemoryException) { }
            }
        }
    }

    /// <summary>
    /// The wait after a failed iteration: 5 s times the failures in a row, waited out in full (the index's changes, reader
    /// results and gate changes wake the thread all the time, so a fault lasting a second would otherwise use up every try).
    /// A command (Rebuild, Apply) or a Stop ends it early.
    /// </summary>
    void BackOff(int failures)
    {
        long until = Environment.TickCount64 + (long)_options.FailureBackOffMs * failures;
        while (!_stopping && _commands.IsEmpty)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0) return;
            _wake.WaitOne((int)Math.Min(left, int.MaxValue));
        }
    }

    /// <summary>One iteration on the caller's thread (tests drive the follower with it); true when there is more to do now.</summary>
    internal bool RunOnce()
    {
        if (_stopping) return false;
        return Iterate();
    }

    /// <summary>Iterates until nothing is left to do now (tests); returns the iterations run.</summary>
    internal int RunUntilIdle(int max = 10_000)
    {
        int n = 0;
        while (n < max && RunOnce()) n++;
        return n;
    }

    bool Iterate()
    {
        long now = _clockMs();
        bool more = DrainCommands(now);
        if (_stopping) return false;
        if (!_isOpen && !Open()) return false;
        // A Rebuild deletes the store on a timer until it can (a search may still map a segment), then opens it again on
        // one; the status says why meanwhile.
        if (_deleting && !FinishDeleting(now)) { PublishStatus(); return more; }
        if (_store == null && !Reopen(now)) { PublishStatus(); return more; }
        if (_view == null) { PublishStatus(); return more; }
        if (_store is { IsUnavailable: true } && !RetryUnavailable(now)) { PublishStatus(); return more; }
        if (_noReading) RetryMarker(now);
        if (_retryProblemsPending && _store is { IsReadOnly: false }) RetryProblems();
        DrainCompacts();
        PollPower(now);
        // A root some lookup met for the first time (its folders count as out until it is resolved).
        if (_walker.UnknownRoots.Count > 0) ResolveRoots();
        if (_reconcilePending || _reconcileAt <= now)
        {
            if (FullReconcile(now, volumeFilter: -1)) more = true;
        }
        else
        {
            bool dirty = Interlocked.Exchange(ref _dirty, 0) == 1;
            if (dirty || now - _lastStepMs >= DebounceMs)
            {
                // Coalesced: at most one step every 2 s, however often the index changes (a test clock steps at once).
                if (_options.Clock != null || now - _lastStepMs >= DebounceMs)
                {
                    _lastStepMs = now;
                    Incremental(now);
                }
                else Interlocked.Exchange(ref _dirty, 1);
            }
        }
        if (!_reconcilePending) more |= OrphanPasses(now);
        more |= ProcessResults(now);
        ExpirePendingDeletes(now);
        PromoteSettled(now);
        PromoteRetries(now);
        more |= ProcessRekeys(now);
        more |= Dispatch(now);
        more |= ProcessResults(now);
        MaybeCommit(now);
        MaybeMerge(now);
        MaybeScrub(now);
        // Nothing being read: a commit since the last count is counted now, so "Up to date" says what the index holds.
        RecountIfDue(now, force: _queue.Count + _inFlight.Count + _parked.Values.Sum(p => p.Count) == 0);
        PublishStatus();
        return more;
    }

    long NextDeadline(long now)
    {
        long next = now + 2000;
        next = Math.Min(next, _settle.NextDue());
        next = Math.Min(next, _retries.NextDue());
        next = Math.Min(next, _reconcileAt);
        foreach (var p in _pendingDeletes.Values) next = Math.Min(next, p.Due);
        if (HasBatch) next = Math.Min(next, Math.Max(_batchStartMs + CommitBusyMs, _commitRetryAt));
        if (_queueEmptySince != long.MinValue && HasBatch) next = Math.Min(next, Math.Max(_queueEmptySince + CommitIdleMs, _commitRetryAt));
        if (_rekeys.Count > 0) next = Math.Min(next, Math.Max(now + 10, Math.Max(_rekeyNotBefore, _commitRetryAt)));
        if (_deleting) next = Math.Min(next, Math.Max(now + 10, _deleteRetryAt));
        if (_store == null && _isOpen) next = Math.Min(next, Math.Max(now + 10, _reopenAt));
        if (_unavailableRetryAt > 0) next = Math.Min(next, Math.Max(now + 10, _unavailableRetryAt));
        if (_noReading && _markerRetryAt > 0) next = Math.Min(next, Math.Max(now + 10, _markerRetryAt));
        return next;
    }

    // ───────────────────────────── open, commands, shutdown ─────────────────────────────

    /// <summary>Reasons shown when the store can't be opened, was made by a newer version, or can't be written for now.</summary>
    const string CouldntOpen = "The text index couldn't be opened", NewerVersion = "made by a newer version: Rebuild text index to use it here",
        InUse = "The text index is in use by another program (trying again)", AnotherCopy = "Another copy of the app is reading inside files";

    /// <summary>Why a store that opened read-only can't be written (it is never written, so nothing is read for it either).</summary>
    static string ReadOnlyReason(IContentStore store) => store.IsNewerFormat ? NewerVersion : store.IsUnavailable ? InUse : AnotherCopy;

    bool Open()
    {
        _error = null;
        try { _store = _openStore(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _error = CouldntOpen;
            PublishStatus();
            return false;
        }
        _view = _store.Acquire();
        if (!_store.IsReadOnly) _store.RecordMode(_settings.Mode);
        // Made by a newer version, in use, or another writer has it: it is never written, so nothing is read for it either.
        else _error = ReadOnlyReason(_store);
        IReadOnlyList<MarkerEntry> leftovers = [];
        _marker = _options.Folder is { } folder ? CrashMarker.Open(Path.Combine(folder, ContentStore.MarkerName), out leftovers) : CrashMarker.InMemory();
        if (_marker == null)
        {
            // Another copy holds it, or the folder can't be written: no crash protection, so nothing is read.
            _error = AnotherCopy;
            _marker = CrashMarker.InMemory();
            _noReading = true;
        }
        RecordLeftovers(leftovers);
        _reader = NewReader(_marker);
        Volatile.Write(ref _scope, BuildScope(_settings));
        WarmUp();
        _isOpen = true;
        return true;
    }

    /// <summary>
    /// The readers' culprits, and the calibration's (an on-demand read with the file's stamp, keyed by document key):
    /// recorded Crashed, never read again until they change. Page boxes and text windows (no stamp) only skip their own
    /// on-demand reads.
    /// </summary>
    void RecordLeftovers(IReadOnlyList<MarkerEntry> leftovers)
    {
        foreach (var e in leftovers)
            if (e.Lane != MarkerLane.OnDemand || e.Modified != 0) _crashed[e.Key] = e;
    }

    /// <summary>The reader lanes over a marker (they take its slots when their threads start).</summary>
    ReadScheduler NewReader(CrashMarker marker) => new(_source, marker, new ReadSchedulerOptions
    {
        Inline = _inline,
        PaceMs = _options.PaceMs,
        UserActive = () => !_options.IgnorePower && PowerRules.UserActive(_powerState),
        Wake = Wake,
        ClockMs = _clockMs,
        ProbeRoot = _options.ProbeRoot ?? Anything.Core.Indexing.FolderScanner.ProbeRoot,
    })
    { NetworkBytesPerSecond = Math.Max(64, _settings.NetworkKilobytesPerSecond) * 1024L };

    /// <summary>
    /// The crash marker couldn't be opened when the follower opened (another copy holds it, or the folder can't be written),
    /// so nothing is read: it is opened again after 30 s, 1 min, then every 5 min. Once it opens, the reader lanes are made
    /// again over it (nothing is in flight: nothing was dispatched), its leftovers are recorded as at open, and reading starts.
    /// </summary>
    void RetryMarker(long now)
    {
        if (_options.Folder is not { } folder) return;
        if (_markerRetryAt == 0)
        {
            _markerRetryAt = now + UnavailableRetryMs[Math.Min(_markerAttempts++, UnavailableRetryMs.Length - 1)];
            return;
        }
        if (now < _markerRetryAt) return;
        _markerRetryAt = 0;
        var marker = CrashMarker.Open(Path.Combine(folder, ContentStore.MarkerName), out var leftovers);
        if (marker == null) return; // the next call schedules the next try
        _markerAttempts = 0;
        var old = _marker;
        _reader?.Dispose();
        Volatile.Write(ref _marker, marker);
        old?.Dispose();
        RecordLeftovers(leftovers);
        _reader = NewReader(marker);
        _noReading = false;
        if (_error == AnotherCopy) _error = _store is { IsReadOnly: true } store ? ReadOnlyReason(store) : null;
        _reconcilePending = true;
    }

    /// <summary>
    /// Runs the walker, the scope rules and the read policy once on a tiny index of its own, so their first-use costs (JIT,
    /// static tables) are paid outside the name index's lock rather than inside the first slice.
    /// </summary>
    void WarmUp()
    {
        try
        {
            var index = new FileIndex();
            int root = index.AddRoot("W:");
            int folder = root;
            // A few levels deep, as real folders are: the walker's chain code runs too, not only its memo.
            foreach (var name in (string[])["Users", "Me", "Docs", "2026"]) folder = index.Add(name, folder, FileAttributes.Directory, -1, 0, 0);
            int file = index.Add("warm.txt", folder, FileAttributes.Archive, 1, 1, 0);
            index.Add("warm.dll", folder, FileAttributes.Archive, 1, 1, 0);
            var roots = new RootTable();
            roots.Add(["W:"], _ => ("path:W:", "", @"W:\"), (_, _) => 0, _scope);
            var walker = new FolderWalker();
            walker.Reset(index, _scope, roots);
            for (int id = 0; id < index.Count; id++) walker.TryKeyOf(id, out _, out _, out _);
            walker.RelativeBelowRoot(file);
            var keys = new ulong[] { 3, 1, 2 };
            Array.Sort(keys, new[] { 0, 1, 2 });
            ReadPolicy.Decide(new DocRow(1, 1, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 0, DocFlags.None), 1, 1, FileAttributes.Archive, "warm.txt",
                _settings, 0, 0, fullReconcile: true);
            ContentTypes.CandidateKind("warm.pdf", ContentKinds.All, ContentMode.DocumentsAndPictures, null);
            // The Compacted handler translates the map inside Compact's write lock: its code should be compiled by then
            // (both ways: a few ids set, every chunk translated), loops included (a few thousand ids, so they are compiled
            // on the stack here rather than at the first real Compact).
            const int Warm = 5000;
            var b = DocIdMap.Empty(0).ToBuilder();
            b.AddSegment(1, Warm);
            for (int i = 0; i < Warm; i++) b.Set(1, i, i);
            var map = b.Build(0);
            var tail = new int[Warm];
            for (int i = 0; i < Warm; i++) tail[i] = i < Warm - 1 ? i : -1;
            CompactRemap.Translate(map, tail, 1);
            var all = new int[Warm];
            for (int i = 0; i < Warm; i++) all[i] = i - 1;
            CompactRemap.Translate(map, all, 1);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    /// <summary>The marker the follower's readers use (the facade's on-demand reads share it).</summary>
    internal CrashMarker? Marker => Volatile.Read(ref _marker);

    bool DrainCommands(long now)
    {
        bool any = false;
        List<Command>? deferred = null;
        while (_commands.TryDequeue(out var c))
        {
            any = true;
            if (!_stopping && !_isOpen && c.Kind != CommandKind.Pause && c.Kind != CommandKind.Resume && !Open())
            {
                // The store can't be opened now: the command waits for the next try.
                (deferred ??= []).Add(c);
                continue;
            }
            try
            {
                if (_stopping) continue;
                switch (c.Kind)
                {
                    case CommandKind.Apply: ApplySettings(c.Settings!); break;
                    case CommandKind.Pause: _pausedUntil = c.Pause is { } p ? now + (long)p.TotalMilliseconds : long.MaxValue; break;
                    case CommandKind.Resume: _pausedUntil = 0; break;
                    case CommandKind.Rebuild: Rebuild(); break;
                    case CommandKind.RetryProblems: RetryProblems(); break;
                    case CommandKind.Checkpoint: Checkpoint(c.Deadline); break;
                }
            }
            finally { c.Done?.Set(); }
        }
        if (deferred != null)
            foreach (var c in deferred) _commands.Enqueue(c);
        return any && deferred == null;
    }

    void ApplySettings(ContentSettings settings)
    {
        var old = _settings;
        Volatile.Write(ref _settings, settings);
        var scope = BuildScope(settings);
        bool scopeChanged = scope.Fingerprint != _scope.Fingerprint;
        Volatile.Write(ref _scope, scope);
        _roots.Rescope(scope);
        _walker.Reset(_index, scope, _roots);
        if (_reader != null) _reader.NetworkBytesPerSecond = Math.Max(64, settings.NetworkKilobytesPerSecond) * 1024L;
        if (_store is { IsReadOnly: false } && old.Mode != settings.Mode) _store.RecordMode(settings.Mode);
        // Index-only, seconds. A scope change checks stored documents' paths (deletes at once, offline volumes too); other
        // changes (encrypted files allowed, a larger size limit) let the reconcile queue what they now allow.
        _scopeChanged |= scopeChanged;
        _reconcilePending = true;
    }

    /// <summary>
    /// "Rebuild text index": everything waiting is dropped and the store is deleted, then opened again empty and every
    /// file in scope is read again. The delete is retried on the follower's timer while it fails (a search still maps a
    /// segment, a scanner holds a file), with "finishing deleting…" in the status; the old store is never used again.
    /// </summary>
    void Rebuild()
    {
        DiscardBatch();
        _queue.Clear();
        _settle.Clear();
        _rekeys.Clear();
        _pendingDeletes.Clear();
        _parked.Clear();
        _retries.Clear();
        _crashed.Clear();
        _orphansKept.Clear();
        _marker?.ClearCarried();
        _commitFailures = 0;
        _commitRetryAt = 0;
        _saveTrouble = null;
        _retryProblemsPending = false; // everything is read again anyway
        if (_deleting) return; // already on its way
        ReleaseView();
        _host.StoreReleased();
        _doomed = _store;
        _doomedStarted = _doomedBlocked = false;
        _store = null;
        _deleting = true;
        _deleteAttempts = 0;
        _deleteRetryAt = 0;
        if (_error == CouldntOpen) _error = null;
        PublishStatus();
    }

    /// <summary>
    /// Tries the Rebuild's delete again when it is due; true once it is done (the store is then opened again). Done means a
    /// store that can be written deleted everything: one that opened read-only (another program holds the writer lock, a
    /// file couldn't be read) refuses to delete, so it is let go and opened again at the next try, until it can be.
    /// </summary>
    bool FinishDeleting(long now)
    {
        if (now < _deleteRetryAt) return false;
        bool deleted;
        try
        {
            // A store that couldn't be opened again after an earlier delete, or couldn't delete: opened just to delete it.
            if (_doomed == null)
            {
                _doomed = _openStore();
                _doomedStarted = false;
            }
            // The first try deletes everything; later ones retry only the files it couldn't delete (DeleteAll again would
            // also delete whatever was committed since: nothing is, but that is the store's contract).
            if (_doomedStarted) deleted = _doomed.FinishDeleting();
            else
            {
                deleted = _doomed.DeleteAll();
                _doomedStarted = true;
            }
            _doomedBlocked = false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            deleted = false;
            if (!_doomedStarted && _doomed != null)
            {
                // It deleted nothing (read-only: its FinishDeleting would only say "nothing pending"): opened again next time.
                _doomedBlocked = true;
                try { _doomed.Dispose(); } catch (Exception x) when (x is not OutOfMemoryException) { }
                _doomed = null;
            }
        }
        if (!deleted)
        {
            _deleteRetryAt = now + DeleteRetryMs[Math.Min(_deleteAttempts++, DeleteRetryMs.Length - 1)];
            return false;
        }
        try { _doomed?.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        _doomed = null;
        _deleting = false;
        _doomedBlocked = false;
        _deleteAttempts = 0;
        _reopenAt = 0;
        _reopenAttempts = 0;
        return true;
    }

    /// <summary>
    /// Opens the store again after a Rebuild deleted it; while it can't be (a scanner or another process holds the
    /// folder), tried again on a back-off timer with the reason in the status. The marker and the reader lanes stay.
    /// </summary>
    bool Reopen(long now)
    {
        if (now < _reopenAt) return false;
        IContentStore store;
        try { store = _openStore(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _error = CouldntOpen;
            _reopenAt = now + DeleteRetryMs[Math.Min(_reopenAttempts++, DeleteRetryMs.Length - 1)];
            return false;
        }
        _reopenAttempts = 0;
        // The store's own reasons go with the old store (the marker's "another copy" stays while nothing may be read).
        if (_error is CouldntOpen or NewerVersion or InUse || _error == AnotherCopy && !_noReading) _error = null;
        _store = store;
        _view = store.Acquire();
        if (!store.IsReadOnly) store.RecordMode(_settings.Mode);
        else _error = ReadOnlyReason(store);
        _roots.Revolume(VolumeIndex);
        _walker.Reset(_index, _scope, _roots);
        _map = EmptyMap(_gen);
        _histogram = [];
        _scopeChanged = true;
        _reconcilePending = true;
        Volatile.Write(ref _firstReconcileDone, false);
        return true;
    }

    void RetryProblems()
    {
        // A store that can't be written now (in use, still deleting) would keep its problem records while the culprits were
        // forgotten: done once one that can be written is open (a store made by a newer version never is: Rebuild).
        _retryProblemsPending = _store is null or { IsReadOnly: true };
        if (_retryProblemsPending) return;
        _crashed.Clear();
        _marker?.ClearCarried();
        _retries.Clear();
        // Crashed records still waiting for their commit are dropped too: those files are read again.
        foreach (var add in _adds.Where(a => a.Crashed).ToArray()) RemoveAdd(add);
        if (_view == null) return;
        CommitBatch();
        foreach (var (doc, row) in _view.RowsByKey())
            if (IsProblem(row.Status)) Delete(doc, row.Key);
        CommitBatch();
        _reconcilePending = true;
    }

    void Checkpoint(long deadline)
    {
        // The marker part never waits for the follower (the facade clears it at once too); this commits what fits.
        if (HasBatch && FitsBefore(deadline)) CommitBatch(force: true);
        _marker?.ClearLive();
    }

    bool FitsBefore(long deadline)
    {
        if (!HasBatch) return true;
        double ms = _batchRaw / Math.Max(1, _commitBytesPerMs) + 50;
        return _clockMs() + ms <= deadline;
    }

    void Shutdown()
    {
        lock (_stopGate)
        {
            if (_shutDown) return;
            _shutDown = true;
        }
        try
        {
            _reader?.Dispose();
            while (_compacts.TryDequeue(out _)) { }
            if (HasBatch && _store is { IsReadOnly: false } && FitsBefore(_stopDeadline))
            {
                try { CommitBatch(force: true); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
            DiscardBatch();
        }
        finally
        {
            _marker?.Dispose();
            ReleaseView();
            try { _store?.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            try { _doomed?.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
            _store = null;
            _doomed = null;
            lock (_stopGate) _thread = null;
            Volatile.Write(ref _status, Status with { Phase = ContentPhase.Stopped, Remaining = null });
            _host.StatusMaybeChanged();
        }
    }

    void ReleaseView()
    {
        Interlocked.Exchange(ref _published, null)?.Dispose();
        _view?.Dispose();
        _view = null;
    }

    // ───────────────────────────── generations ─────────────────────────────

    /// <summary>
    /// Translates the follower's id-keyed state through the Compact remaps queued by the facade's handler, all of it
    /// together; a gap (a Clear in between) drops the ids and asks for a full reconcile.
    /// </summary>
    void DrainCompacts()
    {
        while (true)
        {
            while (_compacts.TryDequeue(out var c))
            {
                if (_gen >= 0 && c.Generation - 1 == _gen) Translate(c.Remap, c.Generation);
                else _gen = -1;
            }
            if (_gen >= 0 && _index.Generation == _gen) return;
            // Under the read lock, Compact's handler has queued its remap before the moved generation can be seen.
            long gen;
            bool queued;
            LockRead();
            try
            {
                gen = _index.Generation;
                queued = !_compacts.IsEmpty;
            }
            finally { UnlockRead(); }
            if (queued) continue;
            if (gen != _gen) Cleared(gen);
            return;
        }
    }

    void Translate(int[] remap, long generation)
    {
        _map = CompactRemap.Translate(_map, remap, generation);
        _queue.Remap(remap);
        _settle.Remap(remap);
        _retries.Remap(remap);
        RemapKeys(_rekeys, remap);
        _batchById.Clear();
        foreach (var add in _adds)
            if ((add.FileId = Translate(remap, add.FileId)) >= 0) _batchById[add.FileId] = add;
        foreach (var job in _inFlight)
            if (job.Generation == _gen)
            {
                job.FileId = Translate(remap, job.FileId);
                job.Generation = generation;
            }
        foreach (var list in _parked.Values)
            for (int i = list.Count - 1; i >= 0; i--)
                if ((list[i] = Translate(remap, list[i])) < 0) list.RemoveAt(i);
        foreach (var key in _pendingDeletes.Keys.ToArray())
            _pendingDeletes[key] = _pendingDeletes[key] with { DeadId = Translate(remap, _pendingDeletes[key].DeadId) };
        _gen = generation;
        _walker.Reset(_index, _scope, _roots);
    }

    /// <summary>The index was cleared (or is first seen): every id-keyed thing goes; a full reconcile binds by key again. No deletes.</summary>
    void Cleared(long generation)
    {
        _queue.Clear();
        _settle.Clear();
        _retries.ForgetIds();
        _rekeys.Clear();
        _parked.Clear();
        _pendingDeletes.Clear();
        foreach (var add in _adds) add.FileId = -1;
        _batchById.Clear();
        _gateReady.Clear();
        _map = EmptyMap(generation);
        _gen = generation;
        _walker.Reset(_index, _scope, _roots);
        _reconcilePending = true;
    }

    static int Translate(int[] remap, int id) => (uint)id < (uint)remap.Length ? remap[id] : -1;

    static void RemapKeys<T>(Dictionary<int, T> d, int[] remap)
    {
        var items = d.ToArray();
        d.Clear();
        foreach (var (id, v) in items)
            if (Translate(remap, id) is >= 0 and var to) d[to] = v;
    }

    DocIdMap EmptyMap(long generation)
    {
        var b = DocIdMap.Empty(generation).ToBuilder();
        if (_view != null)
            foreach (var seg in _view.Segments) b.AddSegment(seg.Id, seg.DocCount);
        return b.Build(generation);
    }

    /// <summary>
    /// Publishes the store view with the follower's map under the index's read lock, so it can't cross Compact's handler;
    /// a generation that moved meanwhile is translated first.
    /// </summary>
    void Publish()
    {
        if (_view == null) return;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            bool done = false;
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation == _map.Generation && _compacts.IsEmpty)
                {
                    if (_view.TryAddRef()) Interlocked.Exchange(ref _published, new ContentView(_view, _map, _settings.Mode))?.Dispose();
                    done = true;
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "publish");
            }
            if (done) return;
            DrainCompacts();
        }
        // Still moving: publish anyway so the new store view is searched; MapFor answers null until the reconcile.
        if (_view.TryAddRef()) Interlocked.Exchange(ref _published, new ContentView(_view, _map, _settings.Mode))?.Dispose();
    }

    /// <summary>
    /// Takes the name index's read lock for one slice, out of background mode while it is held: a thread at background CPU
    /// and I/O priority holding the lock would keep the journal's writer (and every search behind it) waiting. The methods
    /// that loop under it are compiled fully at their first call (AggressiveOptimization), so the JIT never compiles their
    /// loops on the stack while the lock is held.
    /// </summary>
    void LockRead()
    {
        if (_background) ContentNative.LeaveBackgroundMode();
        _index.Lock.EnterReadLock();
    }

    /// <summary>Releases the slice's read lock and goes back to background mode.</summary>
    void UnlockRead()
    {
        _index.Lock.ExitReadLock();
        if (_background) ContentNative.EnterBackgroundMode();
    }

    void NoteHold(Stopwatch sw, string site)
    {
        HoldLog?.Add(sw.Elapsed.TotalMilliseconds);
        long ms = sw.ElapsedMilliseconds;
        if (ms <= MaxReadLockMs) return;
        MaxReadLockMs = ms;
        MaxReadLockSite = site;
    }

    /// <summary>Where the longest read-lock hold was (tests).</summary>
    internal string? MaxReadLockSite { get; private set; }

    /// <summary>Every read-lock hold in milliseconds while set (tests: the distribution, not only the worst).</summary>
    internal List<double>? HoldLog { get; set; }

    // ───────────────────────────── scope and roots ─────────────────────────────

    ContentScope BuildScope(ContentSettings settings)
    {
        var denied = new List<string>();
        if (_contentFolder.Length > 0) denied.Add(_contentFolder);
        var snapshot = _roots.Snapshot;
        IReadOnlyList<VolumeEntry> volumes = [];
        try { if (_view != null) volumes = _view.Volumes; }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return ContentScope.Build(settings, _options.AllRootsInScope, denied, folder =>
        {
            if (snapshot.Resolve(folder) is { } r) return r;
            var (key, prefix, display) = _resolveVolume(folder);
            // A drive that is unplugged now (or a mapped letter that is disconnected) resolves only to a fallback key, while
            // its documents were stored under the key it had when it was there: the store's volume table remembers that by
            // the drive's display root. Without it, the folder would leave the scope and its documents be deleted.
            if (key.StartsWith("path:", StringComparison.OrdinalIgnoreCase) && StoredKeyOf(volumes, display) is { } stored) return (stored, prefix);
            return (key, prefix);
        });
    }

    /// <summary>The key a volume shown at this root ("E:\") was stored under (not a fallback key; the one seen last); null when none.</summary>
    static string? StoredKeyOf(IReadOnlyList<VolumeEntry> volumes, string display)
    {
        VolumeEntry? best = null;
        string root = display.TrimEnd('\\');
        foreach (var v in volumes)
            if (!v.Key.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.DisplayRoot.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
                && (best == null || v.LastSeenUtc > best.LastSeenUtc))
                best = v;
        return best?.Key;
    }

    ushort VolumeIndex(string key, string display)
    {
        if (_store == null || _store.IsReadOnly) return 0;
        try { return _store.VolumeIndex(key, display); }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0; }
    }

    /// <summary>
    /// Resolves root names the walker met (Win32, outside the lock); a new root can change include states, so the scope is
    /// rebuilt. The walker is reset either way: it remembered those roots' folders as out until now.
    /// </summary>
    bool ResolveRoots()
    {
        if (_walker.UnknownRoots.Count == 0) return false;
        var names = _walker.UnknownRoots.ToArray();
        _walker.UnknownRoots.Clear();
        if (!_roots.Add(names, _resolveVolume, VolumeIndex, _scope))
        {
            _walker.Reset(_index, _scope, _roots);
            return true;
        }
        var scope = BuildScope(_settings);
        if (scope.Fingerprint != _scope.Fingerprint)
        {
            Volatile.Write(ref _scope, scope);
            _roots.Rescope(scope);
        }
        _walker.Reset(_index, _scope, _roots);
        // Most-opened paths become keys through the roots: worked out again now that more roots are known.
        RefreshOpened(_clockMs());
        return true;
    }

    // ───────────────────────────── full reconcile ─────────────────────────────

    /// <summary>
    /// The index-only merge-join of every candidate in scope with the store's rows (§2): binds, queues what changed or must
    /// be read again, re-keys moved files from orphans, deletes what is gone (out of scope; or on a volume whose root is
    /// Ready and complete, behind the mass-delete guard; or unseen for 90 days) and keeps the rest unmapped. With a volume
    /// filter it is the orphan-only pass of one volume whose root just completed. False when it couldn't run now.
    /// </summary>
    bool FullReconcile(long now, int volumeFilter)
    {
        if (_names.State == IndexState.Loading || _view == null) return false;
        if (_gen < 0) { DrainCompacts(); if (_gen < 0) return false; }
        _reconciling = true;
        try
        {
            // The gate is not asked to count again here: it refreshes on the names' Scanning↔Ready edges (at most once a
            // minute) and says "not complete" by itself while what it holds may be older than the index.
            // Words still in the batch have no place to bind yet: commit them first (when the store takes it; otherwise
            // the join leaves them to their commit).
            if (HasBatch) CommitBatch();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var outcome = ReconcileOnce(now, volumeFilter);
                if (outcome == ReconcileOutcome.Done)
                {
                    if (volumeFilter < 0)
                    {
                        FullReconciles++;
                        _reconcilePending = false;
                        _scopeChanged = false;
                        Volatile.Write(ref _firstReconcileDone, true);
                        _reconcileAt = _guard.RecheckAt;
                    }
                    // Deletes the guard holds back are confirmed (or not) by a reconcile after the wait.
                    else
                    {
                        VolumeReconciles++;
                        _reconcileAt = Math.Min(_reconcileAt, _guard.RecheckAt);
                    }
                    // The index caught up (often without a read or a commit: an index already up to date binds by key):
                    // windows that searched while it was catching up search again.
                    _host.Linked();
                    return true;
                }
                DrainCompacts();
                if (_gen < 0) return false;
            }
            return false;
        }
        finally { _reconciling = false; }
    }

    enum ReconcileOutcome { Done, Restart }

    /// <summary>Documents the store's segments hold (deleted ones included): a first guess at the candidates in scope.</summary>
    int StoredDocs()
    {
        long docs = 0;
        try { foreach (var s in _view?.Segments ?? []) docs += s.DocCount; }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return (int)Math.Min(docs, int.MaxValue / 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    ReconcileOutcome ReconcileOnce(long now, int volumeFilter)
    {
        long gen = _gen;
        // Sized from the last reconcile, or at the first one of a session from what the store holds (12 bytes a candidate,
        // no copy in the usual case); grown by half when short.
        int expected = Math.Max(_inScope, StoredDocs());
        var candidates = new CandidateArrays(Math.Max(1024, expected + expected / 16 + 1024));
        var rootIds = new List<int>();
        var rootsWithEntries = new HashSet<int>();
        long seq = 0, pathVersion = 0;
        bool first = true;
        _walker.Reset(_index, _scope, _roots);
        // Each slice collects into fixed buffers under the lock; the big arrays grow outside it. Slices adapt to how long
        // they take (first-run code, a slow PC), so no hold runs long whatever the cause.
        var sliceKeys = new ulong[Slice];
        var sliceIds = new int[Slice];
        // Small at first: the first slices of a session run cold code (and the walker's memo is empty).
        var slice = new AdaptiveSlice(Slice, start: 256);
        for (int start = 0, size = slice.Size; ; start += size, size = slice.Size)
        {
            _cts.Token.ThrowIfCancellationRequested();
            int found = 0;
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != gen) return ReconcileOutcome.Restart;
                if (first)
                {
                    seq = _index.ChangeSequence;
                    pathVersion = _index.PathVersion;
                    first = false;
                }
                int count = _index.Count;
                if (start >= count) break;
                int end = Math.Min(count, start + size);
                for (int id = start; id < end; id++)
                {
                    if (!_index.IsAlive(id)) continue;
                    int parent = _index.Parent(id);
                    if (parent == FileIndex.NoParent) { rootIds.Add(id); continue; }
                    if ((uint)parent < (uint)count && _index.Parent(parent) == FileIndex.NoParent) rootsWithEntries.Add(parent);
                    if (_index.IsDirectory(id)) continue;
                    // One volume's pass: other volumes' files are passed over by their folder (memoised), keys never made.
                    if (volumeFilter >= 0 && (!_walker.TryResolve(parent, out var pf) || _roots[pf.Slot].Volume != volumeFilter)) continue;
                    if (!_walker.TryKeyOf(id, out ulong key, out var folder, out _)) continue;
                    if (volumeFilter >= 0 && _roots[folder.Slot].Volume != volumeFilter) continue;
                    sliceKeys[found] = key;
                    sliceIds[found] = id;
                    found++;
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "collect");
            }
            slice.Took(sw.Elapsed.TotalMilliseconds);
            for (int k = 0; k < found; k++) candidates.Add(sliceKeys[k], sliceIds[k]);
        }
        if (_walker.UnknownRoots.Count > 0 && ResolveRoots()) return ReconcileOutcome.Restart;

        // Roots: entries from this walk, status from the gate. Nothing counts as complete while the names are still loading
        // or scanning (a Clear's rescan fills roots progressively; the gate may not have counted since).
        var holders = new Dictionary<ushort, List<(RootInfo Root, bool Ready)>>();
        var heldVolumes = new HashSet<ushort>();
        LockRead();
        try
        {
            if (_index.Generation != gen) return ReconcileOutcome.Restart;
            foreach (int r in rootIds)
            {
                int slot = _roots.SlotOf(_index.Name(r));
                if (slot < 0) continue;
                var info = _roots[slot];
                bool entries = rootsWithEntries.Contains(r);
                bool ready = entries && RootReady(info);
                if (!holders.TryGetValue(info.Volume, out var list)) holders[info.Volume] = list = [];
                list.Add((info, ready));
                if (entries) heldVolumes.Add(info.Volume);
            }
        }
        finally { UnlockRead(); }

        // Sort by key; the same file reached through two roots keeps its lowest id.
        int n = candidates.Count;
        var sortedKeys = candidates.Keys;
        var sortedIds = candidates.Ids;
        Array.Sort(sortedKeys, sortedIds, 0, n);
        int unique = 0;
        for (int i = 0; i < n; i++)
        {
            if (unique > 0 && sortedKeys[unique - 1] == sortedKeys[i])
            {
                if (sortedIds[i] < sortedIds[unique - 1]) sortedIds[unique - 1] = sortedIds[i];
                continue;
            }
            sortedKeys[unique] = sortedKeys[i];
            sortedIds[unique] = sortedIds[i];
            unique++;
        }
        n = unique;

        var view = _view!;
        var segmentIds = view.Segments.Select(s => s.Id).ToArray();
        DocIdMap.Builder builder;
        if (volumeFilter < 0) builder = EmptyMap(gen).ToBuilder();
        else
        {
            builder = _map.ToBuilder();
            foreach (var seg in view.Segments)
                if (!builder.HasSegment(seg.Id)) builder.AddSegment(seg.Id, seg.DocCount);
        }
        var cursor = new StampCursor(this, sortedKeys, sortedIds, n, gen);
        var reading = _inFlight.Select(j => j.Key).ToHashSet();
        // A full pass queues into a new queue (this one is the to-do list until it is done); one volume's pass adds to it.
        var queue = volumeFilter < 0 ? new WorkQueue() : _queue;
        var sink = new FullSink(this, cursor, builder, segmentIds, now, reading, queue, n, filtered: volumeFilter >= 0);
        IEnumerable<(DocRef, DocRow)> rows = view.RowsByKey();
        if (volumeFilter >= 0) rows = rows.Where(r => r.Item2.Volume == volumeFilter);
        try { Reconciler.Join(n, i => sortedKeys[i], rows, sink, _cts.Token); }
        catch (GenerationMovedException) { return ReconcileOutcome.Restart; }

        // Orphans: re-key moved files (the one orphan with the candidate's name, size and date), then decide the rest.
        var rekeys = MatchOrphans(sink, cursor, view, gen);
        if (rekeys == null) return ReconcileOutcome.Restart;
        var deletes = new Dictionary<ushort, List<(ulong Key, DocRef Doc)>>();
        var forced = new List<(ulong Key, DocRef Doc)>();
        var kept = new HashSet<ushort>();
        foreach (var doc in sink.Orphans)
        {
            if (rekeys.Taken.Contains(doc)) continue;
            var row = view.Segments[doc.Segment].Row(doc.Doc);
            if (_pendingDeletes.ContainsKey(row.Key)) continue; // its grace runs out on its own
            switch (OrphanDecision(view, doc, row, holders))
            {
                case OrphanFate.DeleteNow: forced.Add((row.Key, doc)); break;
                case OrphanFate.MayDelete:
                    if (!deletes.TryGetValue(row.Volume, out var list)) deletes[row.Volume] = list = [];
                    list.Add((row.Key, doc));
                    break;
                default: kept.Add(row.Volume); break;
            }
        }
        // The names may have gone back to scanning during the join: then nothing of theirs is complete any more.
        if (deletes.Count > 0 && _names.State != IndexState.Ready)
        {
            kept.UnionWith(deletes.Keys);
            deletes.Clear();
        }

        // Apply: the map binds every matched document; the queue is the to-do list worked out again.
        _map = builder.Build(gen);
        if (volumeFilter < 0)
        {
            _seq = seq;
            _pathVersion = pathVersion;
            _queue = queue;
            _inScope = n;
            _histogram = sink.Histogram;
            _pictureRows = sink.Pictures;
            _histogramCommit = view.CommitGeneration;
            _lastHistogram = now;
            _orphansKept.Clear();
        }
        else _orphansKept.Remove((ushort)volumeFilter);
        _orphansKept.UnionWith(kept);
        foreach (var (doc, fileId) in rekeys.Pairs)
            _rekeys[fileId] = view.Segments[doc.Segment].Row(doc.Doc).Key;
        foreach (var (key, doc) in forced) Delete(doc, key);
        // A folder taken out of the scope (or a volume unseen for 90 days): its words leave the store now, not at the next
        // commit of a long first pass ("leave the text index now", Settings says).
        if (forced.Count > 0) _commitSoon = true;
        foreach (var (volume, list) in deletes)
            foreach (var doc in _guard.Filter(volume, sink.VolumeDocs.GetValueOrDefault(volume), list.Select(d => (d.Key, d.Doc)).ToList(), now))
                Delete(doc, view.Segments[doc.Segment].Row(doc.Doc).Key);
        foreach (var volume in sink.VolumeDocs.Keys)
            if (!deletes.ContainsKey(volume)) _guard.Clear(volume);
        foreach (var volume in heldVolumes) SeenVolume(volume);
        foreach (var (key, entry, fileId) in sink.Crashes) RecordCrash(key, entry, fileId);
        Publish();
        if (_queue.Count == 0) _queueEmptySince = now;
        return ReconcileOutcome.Done;
    }

    enum OrphanFate { Keep, MayDelete, DeleteNow }

    OrphanFate OrphanDecision(IStoreView view, DocRef doc, in DocRow row, Dictionary<ushort, List<(RootInfo Root, bool Ready)>> holders)
    {
        VolumeEntry? volume = null;
        foreach (var v in view.Volumes) if (v.Index == row.Volume) { volume = v; break; }
        if (_scopeChanged && volume != null)
        {
            string? path = view.Segments[doc.Segment].ReadPath(doc.Doc);
            if (path != null && !_scope.InScope(volume.Key, path, volume.DisplayRoot, out _)) return OrphanFate.DeleteNow;
        }
        if (!holders.TryGetValue(row.Volume, out var list) || list.Count == 0)
        {
            // No root holds the volume (an unplugged stick, an unmapped share): kept, until unseen for 90 days.
            if (volume != null && volume.LastSeenUtc > DateTime.MinValue && _utcNow() - volume.LastSeenUtc > TimeSpan.FromMilliseconds(PurgeAfterMs))
                return OrphanFate.DeleteNow;
            return OrphanFate.Keep;
        }
        if (!list.Exists(h => h.Ready)) return OrphanFate.Keep;
        // A whole-volume root that is complete lists everything on the volume.
        if (list.Exists(h => h.Ready && h.Root.Prefix.Length == 0)) return OrphanFate.MayDelete;
        // Otherwise the document's own root (the longest prefix holding it) decides.
        string? rel = view.Segments[doc.Segment].ReadPath(doc.Doc);
        if (rel == null) return OrphanFate.Keep;
        (RootInfo Root, bool Ready)? best = null;
        foreach (var h in list)
            if (h.Root.Prefix.Length == 0 || ContentSettings.IsInside(rel, h.Root.Prefix))
                if (best == null || h.Root.Prefix.Length > best.Value.Root.Prefix.Length) best = h;
        return best is { Ready: true } ? OrphanFate.MayDelete : OrphanFate.Keep;
    }

    void SeenVolume(ushort volume)
    {
        if (_store == null || _store.IsReadOnly) return;
        try { _store.SeenVolume(volume, _utcNow()); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    sealed record OrphanMatches(List<(DocRef Doc, int FileId)> Pairs, HashSet<DocRef> Taken);

    /// <summary>
    /// Moved files: each orphan whose name, size and date one unmatched candidate alone has is re-keyed to it. The smaller
    /// side's stamps (size and date) come first, without names or paths: usually the orphans' (a few files moved while
    /// the app was off), while the candidates not stored yet may be a whole first pass; only candidates and orphans with
    /// a stamp the other side has are looked at further, so the cost follows the moved files, not the files still to read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    OrphanMatches? MatchOrphans(FullSink sink, StampCursor cursor, IStoreView view, long gen)
    {
        var result = new OrphanMatches([], []);
        if (sink.UnmatchedCount == 0 || sink.Orphans.Count == 0) return result;
        HashSet<(long Size, long Modified)> stamps = [];
        if (sink.Orphans.Count <= sink.UnmatchedCount)
            foreach (var doc in sink.Orphans)
            {
                var row = view.Segments[doc.Segment].Row(doc.Doc);
                if (row.Modified != 0) stamps.Add((row.Size, row.Modified));
            }
        else if (!UnmatchedStamps(sink, cursor, gen, null, (i, id) => { if (_index.Modified(id) != 0) stamps.Add((_index.Size(id), _index.Modified(id))); }))
            return null;
        if (stamps.Count == 0) return result;
        // The candidates with one of those stamps, with their names (the cursor's chunks have moved on), in slices.
        var cands = new List<(int Index, uint NameHash, long Size, long Modified, string Name)>();
        if (!UnmatchedStamps(sink, cursor, gen, stamps, (i, id) =>
            {
                string name = _index.Name(id);
                cands.Add((i, ContentKeys.NameHash(name), _index.Size(id), _index.Modified(id), name));
            }))
            return null;
        if (cands.Count == 0) return result;
        // Only orphans with some such candidate's size and date have their path read.
        var candStamps = cands.Select(c => (c.Size, c.Modified)).ToHashSet();
        var orphans = new List<(DocRef Doc, uint NameHash, long Size, long Modified, string Leaf)>();
        foreach (var doc in sink.Orphans)
        {
            var seg = view.Segments[doc.Segment];
            var row = seg.Row(doc.Doc);
            if (!candStamps.Contains((row.Size, row.Modified))) continue;
            if (seg.ReadPath(doc.Doc) is not { } path) continue;
            string leaf = Path.GetFileName(path);
            orphans.Add((doc, ContentKeys.NameHash(leaf), row.Size, row.Modified, leaf));
        }
        if (orphans.Count == 0) return result;
        var matches = Reconciler.MatchOrphans([.. cands.Select(c => (c.NameHash, c.Size, c.Modified))], [.. orphans.Select(o => (o.NameHash, o.Size, o.Modified))]);
        foreach (var (ci, oi) in matches)
        {
            if (!string.Equals(cands[ci].Name, orphans[oi].Leaf, StringComparison.OrdinalIgnoreCase)) continue; // a hash collision
            int id = cursor.Id(cands[ci].Index);
            result.Pairs.Add((orphans[oi].Doc, id));
            result.Taken.Add(orphans[oi].Doc);
            sink.Queue.Remove(id); // its words come with the re-key
        }
        return result;
    }

    /// <summary>
    /// Visits the unmatched candidates that are still alive (and, given stamps, have one of them) under the read lock, in
    /// slices; false when the generation moved meanwhile.
    /// </summary>
    bool UnmatchedStamps(FullSink sink, StampCursor cursor, long gen, HashSet<(long Size, long Modified)>? stamps, Action<int, int> visit)
    {
        int next = sink.NextUnmatched(0);
        while (next >= 0)
        {
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != gen) return false;
                for (int k = 0; k < SmallSlice && next >= 0; k++, next = sink.NextUnmatched(next + 1))
                {
                    int id = cursor.Id(next);
                    if (!_index.IsAlive(id)) continue;
                    if (stamps != null)
                    {
                        long modified = _index.Modified(id);
                        if (modified == 0 || !stamps.Contains((_index.Size(id), modified))) continue;
                    }
                    visit(next, id);
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "orphans");
            }
        }
        return true;
    }

    /// <summary>The candidates' stamps and roots, fetched 16,384 at a time under the read lock as the join reaches them.</summary>
    sealed class StampCursor(ContentFollower f, ulong[] keys, int[] ids, int count, long generation)
    {
        readonly long[] _size = new long[Slice];
        readonly long[] _modified = new long[Slice];
        readonly long[] _recent = new long[Slice];
        readonly FileAttributes[] _attrs = new FileAttributes[Slice];
        readonly string[] _names = new string[Slice];
        readonly int[] _slots = new int[Slice];
        AdaptiveSlice _slice = new(Slice, start: 256);
        int _start = -1, _end = -1;

        public ulong Key(int i) => keys[i];
        public int Id(int i) => ids[i];

        /// <summary>The candidate's stamp, attributes, name and root slot; false when it died meanwhile.</summary>
        public bool Info(int i, out long size, out long modified, out long recent, out FileAttributes attrs, out string name, out int slot)
        {
            if (i < _start || i >= _end) Fetch(i);
            int k = i - _start;
            size = _size[k];
            modified = _modified[k];
            recent = _recent[k];
            attrs = _attrs[k];
            name = _names[k];
            slot = _slots[k];
            return slot >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Fetch(int start)
        {
            var index = f._index;
            var sw = Stopwatch.StartNew();
            f.LockRead();
            int end = Math.Min(count, start + _slice.Size);
            try
            {
                if (index.Generation != generation) throw new GenerationMovedException();
                for (int i = start; i < end; i++)
                {
                    int k = i - start, id = ids[i];
                    // Dead, or no longer this key (renamed since the walk): the join treats it as gone; the change log has it.
                    if (!f._walker.TryKeyOf(id, out ulong key, out var folder, out _) || key != keys[i]) { _slots[k] = -1; continue; }
                    _slots[k] = folder.Slot;
                    _size[k] = index.Size(id);
                    _modified[k] = index.Modified(id);
                    _recent[k] = index.RecentChange(id);
                    _attrs[k] = index.Attributes(id);
                    _names[k] = index.Name(id);
                }
            }
            finally
            {
                f.UnlockRead();
                f.NoteHold(sw, "stamps");
            }
            _slice.Took(sw.Elapsed.TotalMilliseconds);
            _start = start;
            _end = end;
        }
    }

    sealed class GenerationMovedException : Exception;

    /// <summary>The full reconcile's join decisions.</summary>
    sealed class FullSink(ContentFollower f, StampCursor cursor, DocIdMap.Builder builder, uint[] segmentIds, long now, HashSet<ulong> reading,
        WorkQueue queue, int candidates, bool filtered) : IJoinSink
    {
        /// <summary>The files to read, queued as the join finds them (12 bytes a file; files settling or waiting in the batch left out).</summary>
        public readonly WorkQueue Queue = queue;
        // The candidates no stored row has (by position in the sorted candidates): one bit each, for the orphans' match.
        readonly ulong[] _unmatched = new ulong[(candidates + 63) >> 6];
        public readonly List<DocRef> Orphans = [];
        public readonly Dictionary<ushort, int> VolumeDocs = [];
        public readonly Dictionary<DocStatus, int> Histogram = [];
        /// <summary>Pictures among the rows counted in Histogram.</summary>
        public int Pictures;
        public readonly List<(ulong Key, MarkerEntry Entry, int FileId)> Crashes = [];
        readonly long _nowFileTime = f._utcNow().ToFileTimeUtc();

        /// <summary>Candidates no stored row has.</summary>
        public int UnmatchedCount { get; private set; }

        /// <summary>The first unmatched candidate at or after a position; -1 when none.</summary>
        public int NextUnmatched(int from)
        {
            if ((uint)from >= (uint)candidates) return -1;
            int word = from >> 6;
            ulong bits = _unmatched[word] & (ulong.MaxValue << (from & 63));
            while (true)
            {
                if (bits != 0) return (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (++word >= _unmatched.Length) return -1;
                bits = _unmatched[word];
            }
        }

        public void Matched(int candidate, DocRef doc, in DocRow row)
        {
            Count(row);
            if (!cursor.Info(candidate, out long size, out long modified, out long recent, out var attrs, out string name, out int slot))
            {
                Orphan(doc);
                return;
            }
            int id = cursor.Id(candidate);
            builder.Set(segmentIds[doc.Segment], doc.Doc, id);
            if (reading.Contains(row.Key)) return;
            // Its newer words wait in the batch: under another key (renamed back before the commit) a re-key moves them
            // here; under this key they are read again only when the file changed since.
            if (f._batchById.TryGetValue(id, out var waiting) && waiting.Row.Key != row.Key) { f._rekeys[id] = waiting.Row.Key; return; }
            if (f._batchKeys.TryGetValue(row.Key, out var pending) && SameStamp(pending.Row, size, modified))
            {
                // Words read for it under an id that has gone since (a Clear): this file takes them.
                if (pending.FileId < 0) f.Adopt(pending, id);
                return;
            }
            if (f._crashed.TryGetValue(row.Key, out var crash))
            {
                if (crash.Size == size && crash.Modified == modified) { Crashes.Add((row.Key, crash, id)); return; }
                f.ForgetCrash(row.Key);
            }
            if (f._retries.Waiting(row.Key, now)) return;
            var segment = f._view!.Segments[doc.Segment];
            var reason = ReadPolicy.Decide(row, size, modified, attrs, name, f._settings, segment.CreatedUtc, _nowFileTime, fullReconcile: true);
            if (reason.Drop is { } drop) f.DropText(row, segment.ReadPath(doc.Doc), drop, size, modified, name, id);
            else if (reason.Read) Add(id, slot, reason.Level, row.Key, Math.Max(modified, recent));
        }

        public void CandidateOnly(int candidate)
        {
            if (!cursor.Info(candidate, out long size, out long modified, out long recent, out _, out _, out int slot)) return;
            ulong key = cursor.Key(candidate);
            int id = cursor.Id(candidate);
            if (reading.Contains(key)) return;
            // A pending re-key brings its words; words waiting in the batch under its old key are moved by one.
            if (f._rekeys.ContainsKey(id)) return;
            if (f._batchById.TryGetValue(id, out var waiting) && waiting.Row.Key != key) { f._rekeys[id] = waiting.Row.Key; return; }
            if (f._batchKeys.TryGetValue(key, out var pending) && SameStamp(pending.Row, size, modified))
            {
                if (pending.FileId < 0) f.Adopt(pending, id);
                return;
            }
            if (f._crashed.TryGetValue(key, out var crash))
            {
                if (crash.Size == size && crash.Modified == modified) { Crashes.Add((key, crash, id)); return; }
                f.ForgetCrash(key);
            }
            _unmatched[candidate >> 6] |= 1UL << candidate;
            UnmatchedCount++;
            if (f._retries.Waiting(key, now)) return;
            Add(id, slot, WorkQueue.Newest, key, Math.Max(modified, recent));
        }

        public void RowOnly(DocRef doc, in DocRow row)
        {
            Count(row);
            Orphan(doc);
        }

        /// <summary>A stored document no candidate has; one a pending re-key moves (its file was renamed) stays bound to that file.</summary>
        void Orphan(DocRef doc)
        {
            uint segment = segmentIds[doc.Segment];
            int holder = f._map.FileId(segment, doc.Doc);
            if (holder >= 0 && f._rekeys.ContainsKey(holder))
            {
                builder.Set(segment, doc.Doc, holder);
                return;
            }
            // An orphan-only pass starts from the map as it is: the orphan's old binding goes (its id died or moved on).
            if (filtered && holder >= 0) builder.Set(segment, doc.Doc, -1);
            Orphans.Add(doc);
        }

        void Count(in DocRow row)
        {
            VolumeDocs[row.Volume] = VolumeDocs.GetValueOrDefault(row.Volume) + 1;
            Histogram[row.Status] = Histogram.GetValueOrDefault(row.Status) + 1;
            if (row.Kind == ContentKind.Picture) Pictures++;
        }

        void Add(int id, int slot, int level, ulong key, long recency)
        {
            // A file settling is queued when it has been left alone; one whose words wait in the batch is read already.
            if (f._settle.Contains(id) || f._batchById.ContainsKey(id)) return;
            if (f._opened.Contains(key)) level = WorkQueue.Opened;
            var root = f._roots[slot];
            Queue.Enqueue(id, WorkQueue.Priority(level, recency), root.Network ? root.Share : null);
        }
    }

    /// <summary>
    /// A root's documents may be judged against its entries: the names are Ready (not loading or scanning: a Clear's
    /// rescan fills roots progressively) and the gate says the root is Ready and complete.
    /// </summary>
    bool RootReady(RootInfo info)
        => _names.State == IndexState.Ready && _gate.StateOf(info.Name) is { Status: RootStatus.Ready, Complete: true, HasEntries: true };

    static bool SameStamp(in DocRow row, long size, long modified) => modified != 0 && row.Size == size && row.Modified == modified;

    bool OrphanPasses(long now)
    {
        // A root that has just become Ready and complete: its volume's orphans are decided now, when a reconcile kept some
        // because the root wasn't (or no root held the volume). The pass itself checks the root's entries in its own walk.
        for (int slot = 0; slot < _roots.Count; slot++)
        {
            var info = _roots[slot];
            bool ready = RootReady(info);
            bool known = _gateReady.TryGetValue(slot, out bool was);
            _gateReady[slot] = ready;
            if (ready && (!known || !was) && _orphansKept.Contains(info.Volume)) _orphanPasses.Add(info.Volume);
        }
        bool any = false;
        foreach (var volume in _orphanPasses.ToArray())
        {
            if (FullReconcile(now, volume)) any = true;
            _orphanPasses.Remove(volume);
        }
        return any;
    }

    // ───────────────────────────── incremental step ─────────────────────────────

    readonly List<int> _touched = [];
    readonly List<int> _chain = [];
    ulong[] _touchedBits = new ulong[1024];

    /// <summary>
    /// The incremental step (§2): the ids touched since the last step, snapshot in 16,384-id slices under the read lock,
    /// then handled outside it, live ids before dead ones; renamed folders re-key the documents below them from stored text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Incremental(long now)
    {
        if (_view == null || _gen < 0) return;
        _touched.Clear();
        bool pathMoved;
        var sw = Stopwatch.StartNew();
        LockRead();
        try
        {
            if (_index.Generation != _gen) return;
            if (!_index.TryGetChangesSince(_seq, _touched))
            {
                _seq = _index.ChangeSequence;
                _reconcilePending = true;
                return;
            }
            _seq = _index.ChangeSequence;
            pathMoved = _index.PathVersion != _pathVersion;
            _pathVersion = _index.PathVersion;
        }
        finally
        {
            UnlockRead();
            NoteHold(sw, "changes");
        }
        if (_touched.Count == 0) return;
        if (_touched.Count > MaxTouched)
        {
            // A whole-root rescan (its new copy added, the old one removed) reconciles only that root's volume; anything
            // else this big takes a full reconcile.
            if (!OneRootRescanned(now)) _reconcilePending = true;
            return;
        }
        _touched.Sort();
        int u = 0;
        for (int i = 0; i < _touched.Count; i++)
            if (u == 0 || _touched[u - 1] != _touched[i]) _touched[u++] = _touched[i];
        _touched.RemoveRange(u, _touched.Count - u);
        if (pathMoved) _walker.Reset(_index, _scope, _roots);

        var snaps = new List<Snap>(_touched.Count);
        if (!Snapshot(_touched, snaps) || _walker.UnknownRoots.Count > 0 && ResolveRoots() && !Resnap(_touched, snaps))
        {
            // The generation moved under the step: its ids can't be used; a full reconcile covers them.
            _reconcilePending = true;
            return;
        }
        // Documents bound to the touched ids: one sweep of the map over a bitset (no resident reverse map). Words still
        // waiting in the batch need no commit first: they are found by id and by key, and moved or replaced in place.
        var bound = BoundTo(_touched);
        var view = _view!;
        var ordinal = OrdinalsOf(view);
        var renamed = new List<int>();
        var builder = _map.ToBuilder();
        bool mapChanged = false;
        var dead = new HashSet<int>();
        foreach (var s in snaps) if (!s.Alive) dead.Add(s.Id);
        // Live ids before dead ones; among live ones, those bound to a document first, so a file renamed away records
        // its re-key before a new file under its old name could take its document (that one is read as new instead).
        var order = snaps.Where(s => s.Alive && bound.ContainsKey(s.Id))
            .Concat(snaps.Where(s => s.Alive && !bound.ContainsKey(s.Id)))
            .Concat(snaps.Where(s => !s.Alive)).ToList();
        foreach (var s in order)
        {
            if (s.Alive && s.IsDirectory)
            {
                if (pathMoved) renamed.Add(s.Id);
                continue;
            }
            if (!s.Alive)
            {
                // A candidate counted in scope and not bound yet (settling, queued, being read or read and waiting for
                // the commit) leaves the count with its file; a bound one leaves it when its delete is applied.
                bool counted = !bound.ContainsKey(s.Id)
                    && (_settle.Contains(s.Id) || _queue.Contains(s.Id) || _batchById.ContainsKey(s.Id) || _inFlight.Exists(j => j.FileId == s.Id));
                if (counted && s.RootAlive) _inScope = Math.Max(0, _inScope - 1);
                _settle.Remove(s.Id);
                _queue.Remove(s.Id);
                _rekeys.Remove(s.Id);
                // Its words waiting in the batch go with the file (a real delete), or stay unbound with its root (an
                // unplugged drive, a rescan's old copy) for its key to bind again.
                if (_batchById.Remove(s.Id, out var waiting))
                {
                    if (s.RootAlive) RemoveAdd(waiting);
                    else waiting.FileId = -1;
                }
                // Only while the document is still this id's: a live id handled above may have taken it (a rescan, safe-save).
                if (bound.TryGetValue(s.Id, out var b) && ordinal.TryGetValue(b.Segment, out int ord) && builder.FileId(b.Segment, b.Doc) == s.Id)
                {
                    var row = view.Segments[ord].Row(b.Doc);
                    if (s.RootAlive) _pendingDeletes[row.Key] = new PendingDelete(s.Id, now + DeleteGraceMs);
                    else
                    {
                        // Its root was dropped (an unplugged drive, a rescan's old copy): kept, unbound, until a root of its
                        // volume is ready and complete again (then an orphan-only pass decides it).
                        builder.Set(b.Segment, b.Doc, -1);
                        mapChanged = true;
                        _orphansKept.Add(row.Volume);
                    }
                }
                continue;
            }
            _batchById.TryGetValue(s.Id, out var mine);
            if (bound.TryGetValue(s.Id, out var bd) && ordinal.TryGetValue(bd.Segment, out int bo))
            {
                var row = view.Segments[bo].Row(bd.Doc);
                if (!s.InScope)
                {
                    _pendingDeletes[row.Key] = new PendingDelete(s.Id, now + DeleteGraceMs);
                    if (mine != null) RemoveAdd(mine);
                    continue;
                }
                // Back in scope within the grace (a.txt → a.txt.bak → a.txt, a folder moved out and back): the delete
                // waiting for it is called off.
                CancelPendingDelete(row.Key, s.Id);
                // Renamed (the stored words, or newer ones waiting in the batch, are under another key): re-keyed; the
                // re-key finds the waiting version by id, and the stored one by its key.
                if (row.Key != s.Key || mine != null && mine.Row.Key != s.Key) RecordRekey(s, row.Key, mine?.Row ?? row, now);
                else if (mine != null) ConsiderWaiting(s, mine, now);
                else Consider(s, row, view.Segments[bo], bd.Doc, now);
                continue;
            }
            if (!s.InScope)
            {
                // Words read for it and never committed leave with it, and so does its place in the count and the queue.
                bool counted = mine != null || _settle.Contains(s.Id) || _queue.Contains(s.Id);
                if (counted) _inScope = Math.Max(0, _inScope - 1);
                _settle.Remove(s.Id);
                _queue.Remove(s.Id);
                if (mine != null) RemoveAdd(mine);
                continue;
            }
            if (mine != null)
            {
                // Its words wait in the batch: renamed since, a re-key moves them; otherwise it may have changed again.
                if (mine.Row.Key != s.Key) RecordRekey(s, mine.Row.Key, mine.Row, now);
                else ConsiderWaiting(s, mine, now);
                continue;
            }
            if (_batchKeys.TryGetValue(s.Key, out var left) && (left.FileId < 0 || dead.Contains(left.FileId)))
            {
                // Words read for this path under an id that died (a rescan's old copy, a safe-save): this file takes them.
                Adopt(left, s.Id);
                _pendingDeletes.Remove(s.Key);
                ConsiderWaiting(s, left, now);
                continue;
            }
            if (view.Find(s.Key) is { } found)
            {
                uint segId = view.Segments[found.Segment].Id;
                int holder = _map.FileId(segId, found.Doc);
                if (holder >= 0 && holder != s.Id && _rekeys.ContainsKey(holder))
                {
                    // The document is another file's, renamed away (its re-key moves it): this file is new.
                    NewFile(s, now);
                    continue;
                }
                // Its document is stored (a rescan's new id, a re-plugged drive, safe-save): bind it, no read.
                var row = view.Segments[found.Segment].Row(found.Doc);
                builder.Set(segId, found.Doc, s.Id);
                mapChanged = true;
                _pendingDeletes.Remove(s.Key);
                Consider(s, row, view.Segments[found.Segment], found.Doc, now);
            }
            else NewFile(s, now);
        }
        if (mapChanged) PublishBinds(builder);
        if (renamed.Count > 0)
        {
            bool? matter = RenamesMatter(renamed);
            if (matter == null || matter == true && !RenamedFolders(renamed, now)) _reconcilePending = true;
        }
    }

    /// <summary>Passes over every name for renamed folders (tests).</summary>
    internal int RenamedFolderPasses { get; private set; }

    /// <summary>
    /// Whether renamed or moved folders can change anything (PathVersion moves for any folder on any indexed drive): one
    /// whose new place is in or above the scope may hold files to bind, queue or re-key, so every name is looked at; one
    /// outside it only documents bound below it (moved out of the scope), or words waiting in the batch for such files,
    /// which are found through the bound ids alone (O(mapped documents)). A folder renamed where no document is costs no
    /// pass over the names then. Null when the generation moved.
    /// </summary>
    bool? RenamesMatter(List<int> folders)
    {
        int count;
        var sw = Stopwatch.StartNew();
        LockRead();
        try
        {
            if (_index.Generation != _gen) return null;
            foreach (int f in folders)
                if (_index.IsAlive(f) && _walker.TryResolve(f, out _)) return true;
            // A root met for the first time counts as out until it is resolved: the pass over the names resolves it.
            if (_walker.UnknownRoots.Count > 0) return true;
            count = _index.Count;
            if (_belowMemo.Length < count) Array.Resize(ref _belowMemo, Math.Max(count, _belowMemo.Length + _belowMemo.Length / 2));
            Array.Clear(_belowMemo, 0, count);
            foreach (int f in folders) if ((uint)f < (uint)count) _belowMemo[f] = 1;
            foreach (int id in _batchById.Keys)
                if ((uint)id < (uint)count && Below(id, count)) return true;
        }
        finally
        {
            UnlockRead();
            NoteHold(sw, "renamed");
        }
        foreach (uint segId in _map.SegmentIdList)
        {
            if (_map.Segment(segId) is not { } seg) continue;
            foreach (var chunk in seg.Chunks)
            {
                if (chunk == null) continue;
                sw.Restart();
                LockRead();
                try
                {
                    if (_index.Generation != _gen) return null;
                    foreach (int id in chunk)
                        if ((uint)id < (uint)count && Below(id, count)) return true;
                }
                finally
                {
                    UnlockRead();
                    NoteHold(sw, "renamed");
                }
            }
        }
        return false;
    }

    void NewFile(in Snap s, long now)
    {
        if (!_batchKeys.ContainsKey(s.Key) && !_settle.Contains(s.Id) && !_queue.Contains(s.Id))
        {
            _settle.Touch(s.Id, now, SettleKind.New);
            _inScope++;
        }
        else if (_settle.Contains(s.Id)) _settle.Touch(s.Id, now, SettleKind.New);
    }

    /// <summary>A delete waiting for its grace is called off: the file it was for is back (in scope, same name).</summary>
    void CancelPendingDelete(ulong key, int id)
    {
        if (_pendingDeletes.TryGetValue(key, out var pd) && pd.DeadId == id) _pendingDeletes.Remove(key);
    }

    /// <summary>
    /// A file whose words are stored (or waiting) under another key: re-keyed from them later. Renamed and changed in the
    /// same step, or with an unknown date, it is also read again (stat first) once the re-key has moved its words.
    /// </summary>
    void RecordRekey(in Snap s, ulong oldKey, in DocRow stamp, long now) => RecordRekey(s.Id, s.Key, s.Size, s.Modified, oldKey, stamp, now);

    void RecordRekey(int id, ulong newKey, long size, long modified, ulong oldKey, in DocRow stamp, long now)
    {
        _rekeys[id] = oldKey;
        if (modified == 0 || size != stamp.Size || modified != stamp.Modified)
            _settle.Touch(id, now, SettleKind.Changed | SettleKind.StatFirst, _limiter.NotBefore(newKey));
    }

    /// <summary>Consider for a file whose newest words wait in the batch: their row is the stamp to compare with.</summary>
    void ConsiderWaiting(in Snap s, PendingAdd waiting, long now)
    {
        ulong key = s.Key;
        if (_crashed.ContainsKey(key) || _inFlight.Exists(j => j.Key == key)) return;
        long fresh = _utcNow().ToFileTimeUtc();
        var reason = ReadPolicy.Decide(waiting.Row, s.Size, s.Modified, s.Attributes, s.Name, _settings, fresh, fresh, fullReconcile: false);
        if (reason.Drop is { } drop) { DropText(waiting.Row, waiting.Path, drop, s.Size, s.Modified, s.Name, s.Id); return; }
        if (!reason.Read) return;
        _settle.Touch(s.Id, now, SettleKind.Changed | (reason.StatFirst ? SettleKind.StatFirst : 0), _limiter.NotBefore(key));
    }

    /// <summary>A file takes over words waiting in the batch for its path under an id that died.</summary>
    void Adopt(PendingAdd add, int fileId)
    {
        if (add.FileId >= 0 && _batchById.TryGetValue(add.FileId, out var was) && was == add) _batchById.Remove(add.FileId);
        add.FileId = fileId;
        _batchById[fileId] = add;
    }

    /// <summary>
    /// A step with more touched ids than a step takes: when every live one is below one root (a rescan added that root's
    /// new copy and removed the old one), the dead ids let their documents go as a step would (unbound with their dropped
    /// root, or a pending delete when their root lives), and only the root's volume is reconciled (its rows joined with
    /// its candidates; other volumes' bindings are left as they are). False when the step isn't that: a full reconcile.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    bool OneRootRescanned(long now)
    {
        var rootOf = new Dictionary<int, int>();
        var dead = new List<int>();
        var deadRootAlive = new HashSet<int>();
        int liveRoot = -1;
        string rootName = "";
        for (int s = 0; s < _touched.Count; s += Slice)
        {
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != _gen) return false;
                int count = _index.Count;
                for (int k = s; k < Math.Min(_touched.Count, s + Slice); k++)
                {
                    int id = _touched[k];
                    if ((uint)id >= (uint)count) continue;
                    int root = RootOf(id, count, rootOf);
                    if (_index.IsAlive(id))
                    {
                        if (root < 0 || liveRoot >= 0 && root != liveRoot) return false;
                        liveRoot = root;
                    }
                    else
                    {
                        dead.Add(id);
                        if (root >= 0 && _index.IsAlive(root)) deadRootAlive.Add(id);
                    }
                }
                if (liveRoot >= 0) rootName = _index.Name(liveRoot);
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "rescan");
            }
        }
        int slot = liveRoot >= 0 ? _roots.SlotOf(rootName) : -1;
        if (slot < 0) return false; // nothing alive, or a root met for the first time: the full reconcile resolves it
        ushort volume = _roots[slot].Volume;
        dead.Sort();
        var bound = BoundTo(dead);
        var view = _view!;
        var ordinal = OrdinalsOf(view);
        var builder = _map.ToBuilder();
        bool mapChanged = false;
        foreach (int id in dead)
        {
            _settle.Remove(id);
            _queue.Remove(id);
            _rekeys.Remove(id);
            bool rootAlive = deadRootAlive.Contains(id);
            if (_batchById.Remove(id, out var waiting))
            {
                if (rootAlive) RemoveAdd(waiting);
                else waiting.FileId = -1;
            }
            if (!bound.TryGetValue(id, out var b) || !ordinal.TryGetValue(b.Segment, out int ord) || builder.FileId(b.Segment, b.Doc) != id) continue;
            var row = view.Segments[ord].Row(b.Doc);
            if (rootAlive) _pendingDeletes[row.Key] = new PendingDelete(id, now + DeleteGraceMs);
            else
            {
                builder.Set(b.Segment, b.Doc, -1);
                mapChanged = true;
                _orphansKept.Add(row.Volume);
            }
        }
        if (mapChanged) PublishBinds(builder);
        if (!FullReconcile(now, volume)) _reconcilePending = true;
        return true;
    }

    /// <summary>The root of an entry (dead ones too: their parents stay), memoised per folder; -1 when the chain breaks.</summary>
    int RootOf(int id, int count, Dictionary<int, int> memo)
    {
        int cur = id;
        _chain.Clear();
        int root = -1;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (memo.TryGetValue(cur, out int known)) { root = known; break; }
            int parent = _index.Parent(cur);
            if (parent == FileIndex.NoParent) { root = cur; break; }
            if ((uint)parent >= (uint)count) break;
            if (cur != id) _chain.Add(cur);
            cur = parent;
        }
        foreach (int f in _chain) memo[f] = root;
        return root;
    }

    bool Resnap(List<int> touched, List<Snap> snaps)
    {
        snaps.Clear();
        return Snapshot(touched, snaps);
    }

    void Consider(in Snap s, in DocRow row, IContentSegment segment, int doc, long now)
    {
        ulong key = s.Key;
        if (_crashed.ContainsKey(key) || _inFlight.Exists(j => j.Key == key)) return;
        var reason = ReadPolicy.Decide(row, s.Size, s.Modified, s.Attributes, s.Name, _settings, segment.CreatedUtc, _utcNow().ToFileTimeUtc(), fullReconcile: false);
        if (reason.Drop is { } drop) { DropText(row, segment.ReadPath(doc), drop, s.Size, s.Modified, s.Name, s.Id); return; }
        if (!reason.Read) return;
        _settle.Touch(s.Id, now, SettleKind.Changed | (reason.StatFirst ? SettleKind.StatFirst : 0), _limiter.NotBefore(s.Key));
    }

    /// <summary>
    /// A stored document whose file Windows now marks "don't index contents" (or encrypted, while encrypted files aren't
    /// read): a text-less record with that status replaces it in the batch, without reading the file and whatever pauses
    /// reading, and the batch is committed at the next step (privacy: its words leave search at once).
    /// </summary>
    void DropText(in DocRow row, string? relativePath, DocStatus status, long size, long modified, string name, int fileId)
    {
        if (_store == null || _store.IsReadOnly || relativePath == null) return;
        byte version = ContentReaders.CurrentVersion(ContentTypes.ExtensionOf(name), row.Kind);
        bool known = modified != 0;
        var dropped = new DocRow(row.Key, known ? modified : row.Modified, known ? size : row.Size, row.Volume, 0, row.Kind, status, version, DocFlags.None);
        AddToBatch(dropped, relativePath, new StoredDoc("", [], null), 0, 0, fileId);
        _commitSoon = true;
    }

    /// <summary>What a touched id looked like under the lock.</summary>
    readonly record struct Snap(int Id, bool Alive, bool IsDirectory, bool InScope, bool RootAlive, ulong Key, long Size, long Modified,
        long Recent, FileAttributes Attributes, string Name, int Slot);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    bool Snapshot(List<int> touched, List<Snap> into)
    {
        for (int start = 0; start < touched.Count; start += SmallSlice)
        {
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != _gen) return false;
                int count = _index.Count;
                int end = Math.Min(touched.Count, start + SmallSlice);
                for (int k = start; k < end; k++)
                {
                    int id = touched[k];
                    if ((uint)id >= (uint)count) continue;
                    if (!_index.IsAlive(id))
                    {
                        into.Add(new Snap(id, false, false, false, RootAliveOf(id, count), 0, 0, 0, 0, 0, "", -1));
                        continue;
                    }
                    if (_index.IsDirectory(id))
                    {
                        into.Add(new Snap(id, true, true, false, true, 0, 0, 0, 0, _index.Attributes(id), "", -1));
                        continue;
                    }
                    bool inScope = _walker.TryKeyOf(id, out ulong key, out var folder, out _);
                    into.Add(new Snap(id, true, false, inScope, true, key, _index.Size(id), _index.Modified(id), _index.RecentChange(id),
                        _index.Attributes(id), _index.Name(id), inScope ? folder.Slot : -1));
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "snapshot");
            }
        }
        return true;
    }

    /// <summary>Whether a dead entry's root is still alive (a real delete), or went with it (a dropped root: its documents are kept).</summary>
    bool RootAliveOf(int id, int count)
    {
        int cur = id;
        for (int guard = 0; guard < 4096; guard++)
        {
            int parent = _index.Parent(cur);
            if (parent == FileIndex.NoParent) return _index.IsAlive(cur);
            if ((uint)parent >= (uint)count) return false;
            cur = parent;
        }
        return false;
    }

    /// <summary>The documents bound to these ids (sorted): (segment id, local doc) per id, from one sweep of the map.</summary>
    Dictionary<int, (uint Segment, int Doc)> BoundTo(List<int> sortedIds)
    {
        var result = new Dictionary<int, (uint, int)>();
        if (sortedIds.Count == 0) return result;
        int max = sortedIds[^1];
        int words = (max >> 6) + 1;
        if (_touchedBits.Length < words) _touchedBits = new ulong[Math.Max(words, _touchedBits.Length * 2)];
        else Array.Clear(_touchedBits, 0, words);
        foreach (int id in sortedIds) if (id >= 0) _touchedBits[id >> 6] |= 1UL << id;
        foreach (uint segId in _map.SegmentIdList)
        {
            if (_map.Segment(segId) is not { } seg) continue;
            var chunks = seg.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                if (chunks[c] is not { } chunk) continue;
                int baseDoc = c << DocIdMap.ChunkBits;
                for (int i = 0; i < chunk.Length; i++)
                {
                    int id = chunk[i];
                    if ((uint)id <= (uint)max && (_touchedBits[id >> 6] & (1UL << id)) != 0) result[id] = (segId, baseDoc + i);
                }
            }
        }
        return result;
    }

    static Dictionary<uint, int> OrdinalsOf(IStoreView view)
    {
        var d = new Dictionary<uint, int>(view.Segments.Count);
        for (int i = 0; i < view.Segments.Count; i++) d[view.Segments[i].Id] = i;
        return d;
    }

    sbyte[] _belowMemo = [];

    /// <summary>
    /// Folders renamed or moved (PathVersion moved) change the path of everything below them without touching it. Every
    /// file's ancestors are walked once (a per-pass sbyte memo: below a renamed folder or not), in slices. Documents below
    /// one whose key changed are re-keyed from stored text, or deleted when they left the scope; files below one that came
    /// into the scope (a folder moved in) are bound by key when stored, else queued. Nothing already stored is read again.
    /// False when the generation moved.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    bool RenamedFolders(List<int> folders, long now, bool again = false)
    {
        if (_view == null) return true;
        RenamedFolderPasses++;
        var view = _view;
        var ordinal = OrdinalsOf(view);
        var below = new List<(int FileId, bool InScope, ulong Key, long Size, long Modified)>();
        bool first = true;
        for (int start = 0; ; start += SmallSlice)
        {
            var sw = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != _gen) return false;
                int count = _index.Count;
                if (_belowMemo.Length < count) Array.Resize(ref _belowMemo, Math.Max(count, _belowMemo.Length + _belowMemo.Length / 2));
                if (first)
                {
                    Array.Clear(_belowMemo, 0, count);
                    foreach (int f in folders) if ((uint)f < (uint)count) _belowMemo[f] = 1;
                    first = false;
                }
                if (start >= count) break;
                for (int id = start; id < Math.Min(count, start + SmallSlice); id++)
                {
                    if (!_index.IsAlive(id) || _index.IsDirectory(id) || !Below(id, count)) continue;
                    bool inScope = _walker.TryKeyOf(id, out ulong key, out _, out _);
                    below.Add((id, inScope, key, _index.Size(id), _index.Modified(id)));
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(sw, "renamed");
            }
        }
        // A root met for the first time made its folders look out of scope: resolved, then the pass runs once more.
        if (_walker.UnknownRoots.Count > 0 && ResolveRoots()) return !again && RenamedFolders(folders, now, again: true);
        if (below.Count == 0) return true;
        var bound = BoundTo([.. below.Select(b => b.FileId)]);
        var builder = _map.ToBuilder();
        bool mapChanged = false;
        // Bound files first: a re-key is recorded before a file now under its old path could take its document.
        foreach (var (id, inScope, key, size, modified) in below.Where(b => bound.ContainsKey(b.FileId)).Concat(below.Where(b => !bound.ContainsKey(b.FileId))))
        {
            _batchById.TryGetValue(id, out var mine);
            if (bound.TryGetValue(id, out var b) && ordinal.TryGetValue(b.Segment, out int ord))
            {
                var row = view.Segments[ord].Row(b.Doc);
                if (!inScope)
                {
                    _pendingDeletes[row.Key] = new PendingDelete(id, now + DeleteGraceMs);
                    if (mine != null) RemoveAdd(mine);
                    continue;
                }
                // Back in scope within the grace (moved out of My folders and back): its delete is called off.
                CancelPendingDelete(row.Key, id);
                if (row.Key != key || mine != null && mine.Row.Key != key) RecordRekey(id, key, size, modified, row.Key, mine?.Row ?? row, now);
                continue;
            }
            if (!inScope)
            {
                if (mine != null) RemoveAdd(mine);
                continue;
            }
            if (mine != null)
            {
                if (mine.Row.Key != key) RecordRekey(id, key, size, modified, mine.Row.Key, mine.Row, now);
                continue;
            }
            if (_batchKeys.ContainsKey(key) || _queue.Contains(id) || _settle.Contains(id)) continue;
            if (view.Find(key) is { } found)
            {
                uint segId = view.Segments[found.Segment].Id;
                int holder = _map.FileId(segId, found.Doc);
                if (holder >= 0 && holder != id && _rekeys.ContainsKey(holder))
                {
                    // Another file's document, which its own re-key moves away: this one is new.
                    _settle.Touch(id, now, SettleKind.New);
                    _inScope++;
                    continue;
                }
                builder.Set(segId, found.Doc, id);
                mapChanged = true;
                _pendingDeletes.Remove(key);
                var row = view.Segments[found.Segment].Row(found.Doc);
                if (row.Size != size || row.Modified != modified || modified == 0) _settle.Touch(id, now, SettleKind.Changed | SettleKind.StatFirst);
            }
            else
            {
                _settle.Touch(id, now, SettleKind.New);
                _inScope++;
            }
        }
        if (mapChanged) PublishBinds(builder);
        return true;
    }

    /// <summary>A step's binds (or unbinds) published, without a commit: windows that searched just before search again.</summary>
    void PublishBinds(DocIdMap.Builder builder)
    {
        _map = builder.Build(_gen);
        Publish();
        _host.Linked();
    }

    bool Below(int id, int count)
    {
        // 1 = below a renamed folder, 2 = not; each chain resolved once.
        int cur = id;
        _chain.Clear();
        sbyte verdict = 2;
        for (int guard = 0; guard < 4096 && (uint)cur < (uint)count; guard++)
        {
            if (_belowMemo[cur] != 0) { verdict = _belowMemo[cur]; break; }
            _chain.Add(cur);
            cur = _index.Parent(cur);
            if (cur == FileIndex.NoParent) break;
        }
        foreach (int c in _chain) _belowMemo[c] = verdict;
        return verdict == 1;
    }

    // ───────────────────────────── settle, retries, pending deletes, re-keys ─────────────────────────────

    void PromoteSettled(long now)
    {
        var due = _settle.TakeDue(now);
        if (due.Count == 0) return;
        if (_view == null) { foreach (var (id, kind) in due) _settle.Touch(id, now, kind); return; }
        var view = _view;
        var snaps = new List<Snap>(due.Count);
        if (!Snapshot([.. due.Select(d => d.Id)], snaps))
        {
            // Old-generation ids: back in the table, which the next drain translates.
            foreach (var (id, kind) in due) _settle.Touch(id, now - SettleTable.SettleMs, kind);
            return;
        }
        if (_walker.UnknownRoots.Count > 0 && ResolveRoots() && !Resnap([.. due.Select(d => d.Id)], snaps))
        {
            foreach (var (id, kind) in due) _settle.Touch(id, now - SettleTable.SettleMs, kind);
            return;
        }
        var kinds = due.ToDictionary(d => d.Id, d => d.Kind);
        Dictionary<(long, long), List<ulong>>? pendingByStamp = null;
        foreach (var s in snaps)
        {
            if (!s.Alive || s.IsDirectory || !s.InScope) continue;
            if (_retries.Waiting(s.Key, now) || _crashed.ContainsKey(s.Key)) continue;
            // Words waiting in the batch with the file's stamp are current; a copy a re-key made (or a read) of an older
            // version is read again, stat first.
            if (_batchKeys.TryGetValue(s.Key, out var waiting) && SameStamp(waiting.Row, s.Size, s.Modified)) continue;
            var existing = view.Find(s.Key);
            if (existing == null && waiting == null && !_batchById.ContainsKey(s.Id) && _pendingDeletes.Count > 0)
            {
                pendingByStamp ??= PendingByStamp(view);
                if (TryRekeyFromPending(s, view, pendingByStamp)) continue;
            }
            long notBefore = _limiter.NotBefore(s.Key);
            if (existing != null && notBefore > now) { _settle.Touch(s.Id, now, kinds[s.Id], notBefore); continue; }
            int level = _opened.Contains(s.Key) ? WorkQueue.Opened : WorkQueue.Newest;
            _queue.Enqueue(s.Id, WorkQueue.Priority(level, Math.Max(s.Modified, s.Recent)), s.Slot >= 0 && _roots[s.Slot].Network ? _roots[s.Slot].Share : null);
        }
    }

    Dictionary<(long, long), List<ulong>> PendingByStamp(IStoreView view)
    {
        var d = new Dictionary<(long, long), List<ulong>>();
        foreach (var key in _pendingDeletes.Keys)
        {
            if (view.Find(key) is not { } doc) continue;
            var row = view.Segments[doc.Segment].Row(doc.Doc);
            if (row.Modified == 0) continue;
            if (!d.TryGetValue((row.Size, row.Modified), out var list)) d[(row.Size, row.Modified)] = list = [];
            list.Add(key);
        }
        return d;
    }

    /// <summary>A new file whose key isn't stored: the one pending delete with its name, size and date is its old self (a move across volumes).</summary>
    bool TryRekeyFromPending(in Snap s, IStoreView view, Dictionary<(long, long), List<ulong>> byStamp)
    {
        if (s.Modified == 0 || !byStamp.TryGetValue((s.Size, s.Modified), out var keys)) return false;
        ulong? match = null;
        foreach (ulong key in keys)
        {
            if (!_pendingDeletes.TryGetValue(key, out var pd) || view.Find(key) is not { } doc) continue;
            string leaf = Path.GetFileName(view.Segments[doc.Segment].ReadPath(doc.Doc) ?? "");
            if (!string.Equals(leaf, s.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (match != null) return false; // two candidates: read it instead of guessing
            match = key;
        }
        if (match is not { } old) return false;
        _pendingDeletes.Remove(old);
        keys.Remove(old);
        _rekeys[s.Id] = old;
        return true;
    }

    void PromoteRetries(long now)
    {
        foreach (var (_, fileId) in _retries.TakeDue(now))
            if (fileId >= 0) _settle.Touch(fileId, now - SettleTable.SettleMs, SettleKind.Changed);
        if (_parked.Count == 0 || _reader == null) return;
        foreach (var share in _parked.Keys.ToArray())
        {
            if (_reader.IsBackedOff(share, now) || !ShareOnline(share)) continue;
            foreach (int id in _parked[share]) _settle.Touch(id, now - SettleTable.SettleMs, SettleKind.Changed);
            _parked.Remove(share);
        }
    }

    /// <summary>
    /// Deletes whose grace ran out, each checked again under the read lock: the document stays when it was bound to a live
    /// file again (safe-save, a rescan's new id), or its own file is back in scope under the same name, or about to be
    /// re-keyed (renamed back into the scope).
    /// </summary>
    void ExpirePendingDeletes(long now)
    {
        if (_pendingDeletes.Count == 0 || _view == null) return;
        var due = _pendingDeletes.Where(p => p.Value.Due <= now).ToArray();
        if (due.Length == 0) return;
        var view = _view;
        var docs = new DocRef?[due.Length];
        var mapped = new int[due.Length];
        for (int i = 0; i < due.Length; i++)
        {
            docs[i] = view.Find(due[i].Key);
            mapped[i] = docs[i] is { } d ? _map.FileId(view.Segments[d.Segment].Id, d.Doc) : -1;
        }
        var keep = new bool[due.Length];
        var sw = Stopwatch.StartNew();
        LockRead();
        try
        {
            if (_index.Generation != _gen) return; // the drain translates them first
            for (int i = 0; i < due.Length; i++)
            {
                if (docs[i] == null) continue;
                var (key, pd) = due[i];
                if (mapped[i] >= 0 && mapped[i] != pd.DeadId && _index.IsAlive(mapped[i])) { keep[i] = true; continue; }
                if (pd.DeadId >= 0 && _index.IsAlive(pd.DeadId)
                    && (_rekeys.ContainsKey(pd.DeadId) || _walker.TryKeyOf(pd.DeadId, out ulong k, out _, out _) && k == key))
                    keep[i] = true;
            }
        }
        finally
        {
            UnlockRead();
            NoteHold(sw, "expire");
        }
        for (int i = 0; i < due.Length; i++)
        {
            _pendingDeletes.Remove(due[i].Key);
            if (docs[i] is not { } doc || keep[i]) continue;
            Delete(doc, due[i].Key);
            // Its file is gone or out of scope: one document fewer in scope (Status.Total), until a reconcile counts again.
            _inScope = Math.Max(0, _inScope - 1);
        }
    }

    /// <summary>The most re-keys one pass takes, chain members pulled in included.</summary>
    const int MaxRekeyPass = 8 * RekeyChunk;

    /// <summary>One pending re-key as the name index has it now.</summary>
    readonly record struct RekeyTarget(int Id, ulong OldKey, bool Alive, bool InScope, ulong Key, string Rel, int Slot, long Size, long Modified);

    /// <summary>
    /// Re-keys from stored text under the reader's duty cycle and power rules (not urgent: the document stays bound to its
    /// id meanwhile): the compressed text copied under the new key and path (AddCopy), plus the old document's deletion.
    /// A file's words are its own: the version waiting in the batch for it, else the document of the batch's view bound to
    /// it. Nothing is committed during a pass, so every copy reads the view the batch began from (where documents other
    /// re-keys delete are still there), and a chain (a swap, c→d then a→c, a numbered series shifted by one) is moved
    /// together: a re-key whose new name holds a document another pending re-key moves away pulls that one into the pass.
    /// A file renamed and changed (or with an unknown date) is also read again, stat first, once its words are moved.
    /// </summary>
    bool ProcessRekeys(long now)
    {
        if (_rekeys.Count == 0 || _view == null || _store == null || _store.IsReadOnly) return false;
        if (now < _rekeyNotBefore || _commitFailures > 0 && now < _commitRetryAt) return false;
        if (!_options.IgnorePower && PowerRules.MergePause(_powerState, _settings.ReadOnBattery) != null) return false;
        var sw = Stopwatch.StartNew();
        if (!RekeyProbe(now)) return false; // the generation moved: the drain translates them first
        if (_rekeys.Count == 0 || _view == null || _store == null || _store.IsReadOnly) return false;
        var view = _view;
        var targets = new List<RekeyTarget>();
        var taken = new HashSet<int>();
        var pull = _rekeys.Keys.Take(RekeyChunk).ToList();
        while (pull.Count > 0)
        {
            int from = targets.Count;
            if (!CollectRekeys(pull, taken, targets)) return false; // the generation moved: the drain translates them first
            var next = new HashSet<int>();
            for (int i = from; i < targets.Count && taken.Count + next.Count < MaxRekeyPass; i++)
            {
                if (!targets[i].InScope || view.Find(targets[i].Key) is not { } other) continue;
                int holder = _map.FileId(view.Segments[other.Segment].Id, other.Doc);
                if (holder >= 0 && holder != targets[i].Id && !taken.Contains(holder) && _rekeys.ContainsKey(holder)) next.Add(holder);
            }
            pull = [.. next];
        }
        // "Out of scope" may only mean "root not resolved yet": these are tried again with the root known.
        if (_walker.UnknownRoots.Count > 0 && ResolveRoots()) return true;
        foreach (var t in targets) _rekeys.Remove(t.Id);
        var sources = SourcesOf(targets, view);
        var passKeys = new HashSet<ulong>();
        for (int i = 0; i < targets.Count; i++) MoveOne(targets[i], sources[i], view, passKeys, now);
        if (!_options.IgnorePower && PowerRules.UserActive(_powerState)) _rekeyNotBefore = now + Math.Max(1, sw.ElapsedMilliseconds);
        return _rekeys.Count > 0 && _rekeyNotBefore <= now;
    }

    /// <summary>
    /// A big re-key (a folder holding much of a large segment renamed) rewrites that segment once rather than copying its
    /// documents chunk by chunk. Whenever enough re-keys arrived since the last look to move 30% of a large segment, all
    /// the waiting ones are looked at together; the batch names documents of the current view, so it is saved first. The
    /// re-keys left go through the batch in chunks as usual. False when the generation moved.
    /// </summary>
    bool RekeyProbe(long now)
    {
        _rekeyLow = Math.Min(_rekeyLow, _rekeys.Count);
        if ((_rekeys.Count - _rekeyLow) * 10L < (_options.RekeyInPlaceAbove + 1L) * 3L) return true;
        var all = new List<RekeyTarget>(_rekeys.Count);
        if (!CollectRekeys([.. _rekeys.Keys], [], all)) return false;
        if (_walker.UnknownRoots.Count > 0) return true; // the pass resolves them; looked at again after that
        _rekeyLow = _rekeys.Count;
        if (_view == null || BigRekeyGroup(all, _view) == null) return true;
        if (HasBatch && !CommitBatch(force: true)) return true; // not saved: they go through the batch as copies
        if (RekeyInPlace(all, now) is { } moved)
            foreach (int id in moved) _rekeys.Remove(id);
        _rekeyLow = _rekeys.Count;
        return true;
    }

    /// <summary>
    /// The segment (ordinal) whose plain moves among these re-keys reach 30% of its live documents, the most such moves first,
    /// when it has more live documents than the commit rewrites by itself; null when none. A plain move: the file is alive
    /// and in scope, its words are stored under its old key and bound to it, nothing waits in the batch for it, and
    /// nothing is stored or waiting under its new key (chains and clashes go through the batch).
    /// </summary>
    (int Ordinal, List<(RekeyTarget Target, DocRef Source)> Moves)? BigRekeyGroup(List<RekeyTarget> targets, IStoreView view)
    {
        if (_store == null || _store.IsReadOnly || targets.Count == 0) return null;
        Dictionary<int, List<(RekeyTarget, DocRef)>>? bySegment = null;
        foreach (var t in targets)
        {
            if (!t.Alive || !t.InScope || t.Key == t.OldKey || _batchById.ContainsKey(t.Id) || _batchKeys.ContainsKey(t.Key)) continue;
            if (view.Find(t.OldKey) is not { } src || view.Find(t.Key) != null) continue;
            if (_map.FileId(view.Segments[src.Segment].Id, src.Doc) != t.Id) continue;
            bySegment ??= [];
            if (!bySegment.TryGetValue(src.Segment, out var list)) bySegment[src.Segment] = list = [];
            list.Add((t, src));
        }
        if (bySegment == null) return null;
        (int, List<(RekeyTarget, DocRef)>)? best = null;
        foreach (var (ordinal, list) in bySegment)
        {
            var seg = view.Segments[ordinal];
            if (seg.DocCount <= _options.RekeyInPlaceAbove || list.Count < (best?.Item2.Count ?? 0)) continue;
            int live = 0;
            for (int d = 0; d < seg.DocCount; d++) if (!view.IsDeleted(new DocRef(ordinal, d))) live++;
            if (live > _options.RekeyInPlaceAbove && list.Count * 10L >= live * 3L) best = (ordinal, list);
        }
        return best;
    }

    /// <summary>
    /// Rewrites, one at a time, each segment a big re-key moves (BigRekeyGroup) with the moved documents under their new
    /// keys and paths (IContentStore.Rekey: stored text copied, never compressed again), binding them to their files and
    /// publishing; returns the ids moved, null when none was. Runs with no batch waiting, under the merge gates (a large
    /// segment only when idle on mains power) and the duty cycle; the store may decline (no room, damaged), and those
    /// files then go through the batch as copies.
    /// </summary>
    HashSet<int>? RekeyInPlace(List<RekeyTarget> targets, long now)
    {
        HashSet<int>? moved = null;
        bool allowLarge = _options.IgnorePower || PowerRules.AllowLargeMerge(_powerState);
        for (int guard = 0; guard < 64 && !HasBatch && _view != null && _store is { IsReadOnly: false }; guard++)
        {
            var view = _view;
            var rest = moved == null ? targets : targets.Where(t => !moved.Contains(t.Id)).ToList();
            if (BigRekeyGroup(rest, view) is not { } group) break;
            var seg = view.Segments[group.Ordinal];
            var moves = new List<(int Doc, DocRow Row, string Path)>(group.Moves.Count);
            foreach (var (t, src) in group.Moves)
                moves.Add((src.Doc, seg.Row(src.Doc) with { Key = t.Key, Volume = _roots[t.Slot].Volume }, t.Rel));
            StoreChange? change;
            try { change = _store.Rekey(seg.Id, moves, allowLarge, _cts.Token, DutyPace()); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is not OutOfMemoryException) { change = null; }
            if (change == null) break; // can't now: the batch's copies take them
            var binds = new (int Pos, int FileId)[group.Moves.Count];
            for (int i = 0; i < binds.Length; i++) binds[i] = (i, group.Moves[i].Target.Id);
            ApplyChange(_map.ToBuilder(), change, binds);
            view.Dispose();
            Commits++;
            RekeysInPlace++;
            _host.Committed(LongPass);
            moved ??= [];
            for (int i = 0; i < group.Moves.Count; i++)
            {
                var (t, _) = group.Moves[i];
                moved.Add(t.Id);
                CheckStamp(t, moves[i].Row, now);
            }
        }
        return moved;
    }

    /// <summary>Segments rewritten in place by a big re-key (tests).</summary>
    internal int RekeysInPlace { get; private set; }

    // The fewest re-keys waiting since the last look for a big one (RekeyProbe).
    int _rekeyLow;

    /// <summary>Between chunks of a merge or re-key: while the user is active, rests as long as the chunk took (50% duty cycle).</summary>
    Action<CancellationToken> DutyPace()
    {
        var chunk = Stopwatch.StartNew();
        return ct =>
        {
            if (!_options.IgnorePower && PowerRules.UserActive(_power.Now(_contentFolder)))
                ct.WaitHandle.WaitOne((int)Math.Clamp(chunk.ElapsedMilliseconds, 1, 5000));
            chunk.Restart();
        };
    }

    /// <summary>The re-keys of these ids as the index has them now, in read-lock slices; false when the generation moved.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    bool CollectRekeys(List<int> ids, HashSet<int> taken, List<RekeyTarget> into)
    {
        for (int s = 0; s < ids.Count; s += SmallSlice)
        {
            var watch = Stopwatch.StartNew();
            LockRead();
            try
            {
                if (_index.Generation != _gen) return false;
                for (int k = s; k < Math.Min(ids.Count, s + SmallSlice); k++)
                {
                    int id = ids[k];
                    if (!_rekeys.TryGetValue(id, out ulong oldKey) || !taken.Add(id)) continue;
                    if (!_index.IsAlive(id)) { into.Add(new RekeyTarget(id, oldKey, false, false, 0, "", -1, 0, 0)); continue; }
                    bool inScope = _walker.TryKeyOf(id, out ulong key, out var folder, out _);
                    into.Add(new RekeyTarget(id, oldKey, true, inScope, key, inScope ? RelativePath(folder.Slot, id) : "", inScope ? folder.Slot : -1,
                        _index.Size(id), _index.Modified(id)));
                }
            }
            finally
            {
                UnlockRead();
                NoteHold(watch, "rekey");
            }
        }
        return true;
    }

    /// <summary>
    /// Each re-key's stored source in the view: the document under its old key, unless a live other file holds that one
    /// (a commit put another file's words there meanwhile); otherwise the document bound to the file, whatever its key.
    /// </summary>
    DocRef?[] SourcesOf(List<RekeyTarget> targets, IStoreView view)
    {
        var result = new DocRef?[targets.Count];
        var atOld = new (DocRef Doc, int Holder)?[targets.Count];
        var holders = new List<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (!t.Alive || view.Find(t.OldKey) is not { } d) continue;
            int holder = _map.FileId(view.Segments[d.Segment].Id, d.Doc);
            if (holder < 0 || holder == t.Id) result[i] = d;
            else
            {
                atOld[i] = (d, holder);
                holders.Add(holder);
            }
        }
        if (holders.Count > 0)
        {
            // Held by a dead id (a pending delete, a rescan's old copy, an orphan matched by name and stamp): still its words.
            var alive = new HashSet<int>();
            LockRead();
            try
            {
                if (_index.Generation == _gen)
                    foreach (int h in holders) if (_index.IsAlive(h)) alive.Add(h);
            }
            finally { UnlockRead(); }
            for (int i = 0; i < targets.Count; i++)
                if (result[i] == null && atOld[i] is { } a && !alive.Contains(a.Holder)) result[i] = a.Doc;
        }
        var unresolved = new List<int>();
        for (int i = 0; i < targets.Count; i++) if (result[i] == null && targets[i].Alive) unresolved.Add(targets[i].Id);
        if (unresolved.Count == 0) return result;
        unresolved.Sort();
        var bound = BoundTo(unresolved);
        var ordinal = OrdinalsOf(view);
        for (int i = 0; i < targets.Count; i++)
            if (result[i] == null && bound.TryGetValue(targets[i].Id, out var b) && ordinal.TryGetValue(b.Segment, out int ord))
                result[i] = new DocRef(ord, b.Doc);
        return result;
    }

    /// <summary>One re-key (see <see cref="ProcessRekeys"/>).</summary>
    void MoveOne(in RekeyTarget t, DocRef? src, IStoreView view, HashSet<ulong> passKeys, long now)
    {
        if (!t.Alive) return; // its death is the change log's to handle
        _batchById.TryGetValue(t.Id, out var waiting);
        DocRow srcRow = src is { } sr ? view.Segments[sr.Segment].Row(sr.Doc) : default;
        if (!t.InScope)
        {
            if (waiting != null) RemoveAdd(waiting);
            if (src is { } gone) Delete(gone, srcRow.Key);
            return;
        }
        if (!passKeys.Add(t.Key)) return; // the same file through two roots: the first one moves it
        if (waiting != null)
        {
            // Its newest words wait in the batch (read before the rename, or copied by an earlier re-key): moved to the new
            // key in place; the stored version is superseded.
            if (src is { } stale && srcRow.Key != t.Key) Delete(stale, srcRow.Key);
            if (waiting.Row.Key != t.Key) MoveAdd(waiting, t);
            CheckStamp(t, waiting.Row, now);
            return;
        }
        if (src is not { } source)
        {
            // Nothing stored or waiting for it: it is read.
            if (!_settle.Contains(t.Id) && !_queue.Contains(t.Id)) _settle.Touch(t.Id, now, SettleKind.New);
            return;
        }
        if (srcRow.Key == t.Key) { CheckStamp(t, srcRow, now); return; } // its old name came back
        if (view.Find(t.Key) is { } other && other != source)
        {
            if (_map.FileId(view.Segments[other.Segment].Id, other.Doc) == t.Id)
            {
                // Its newer words are stored under the new name already (read after the rename): the old ones go.
                Delete(source, srcRow.Key);
                CheckStamp(t, view.Segments[other.Segment].Row(other.Doc), now);
                return;
            }
            // Another document under the new name (a deleted file's, or one another re-key in this pass copies away from
            // this same view) is replaced by this file's words.
            Delete(other, t.Key);
        }
        if (_batchKeys.TryGetValue(t.Key, out var clash)) RemoveAdd(clash);
        AddCopy(source, srcRow with { Key = t.Key, Volume = _roots[t.Slot].Volume }, t.Rel, t.Id);
        Delete(source, srcRow.Key);
        CheckStamp(t, srcRow, now);
    }

    /// <summary>A re-keyed file whose stamp isn't the one its words were read with (or is unknown): read again, stat first.</summary>
    void CheckStamp(in RekeyTarget t, in DocRow words, long now)
    {
        if (t.Modified == 0 || t.Size != words.Size || t.Modified != words.Modified)
            _settle.Touch(t.Id, now, SettleKind.Changed | SettleKind.StatFirst, _limiter.NotBefore(t.Key));
    }

    string RelativePath(int slot, int fileId)
    {
        var root = _roots[slot];
        string below = _walker.RelativeBelowRoot(fileId);
        return root.Prefix.Length == 0 ? below : below.Length == 0 ? root.Prefix : root.Prefix + "\\" + below;
    }

    string FullPath(int slot, int fileId)
    {
        var root = _roots[slot];
        string below = _walker.RelativeBelowRoot(fileId);
        return below.Length == 0 ? root.Name + "\\" : root.Name + "\\" + below;
    }

    // ───────────────────────────── reading ─────────────────────────────

    /// <summary>Why reading pauses now; null when it may go on.</summary>
    internal string? ReadPause(long now)
    {
        if (_pausedUntil > now) return "paused by you";
        // A commit that failed: what was read waits in the batch until it is saved; nothing more is read meanwhile.
        if (_commitFailures > 0 && _saveTrouble != null) return _saveTrouble;
        if (!_options.IgnorePower && PowerRules.ReadPause(_powerState, _settings.ReadOnBattery, elevated: false) is { } power) return power;
        if (!_options.IgnoreElevation && _names.IsElevated) return "running as administrator";
        return null;
    }

    /// <summary>The follower's clock now (tests compare deadlines with it).</summary>
    internal long NowMs => _clockMs();

    void PollPower(long now)
    {
        if (now - _powerAt >= 5000 || _options.Clock != null)
        {
            _powerAt = now;
            try { _powerState = _power.Now(_contentFolder); }
            catch (Exception e) when (e is not OutOfMemoryException) { _powerState = PowerState.Unlimited; }
        }
        if (_openedAt == long.MinValue || now - _openedAt >= OpenedRefreshMs) RefreshOpened(now);
    }

    /// <summary>Most opened: the run history plus the targets of the newest Recent Items shortcuts, as document keys.</summary>
    void RefreshOpened(long now)
    {
        _openedAt = now;
        var keys = new HashSet<ulong>();
        var roots = _roots.Snapshot;
        try
        {
            foreach (var (path, _) in _index.RunHistory.Entries())
                if (roots.TryKeyOf(path, out ulong k, out _, out _, out _)) keys.Add(k);
            var recent = _options.RecentItems?.Invoke() ?? (_options.Folder == null ? [] : RecentItems.Targets());
            foreach (var path in recent)
                if (roots.TryKeyOf(path, out ulong k, out _, out _, out _)) keys.Add(k);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        _opened = keys;
    }

    /// <summary>Asks for the most-opened list again (the root table grew; tests).</summary>
    internal void RefreshOpenedSoon() => _openedAt = long.MinValue;

    bool Dispatch(long now)
    {
        if (_reader == null || _view == null || _gen < 0 || _queue.Count == 0 || _noReading || _store is null or { IsReadOnly: true }) return false;
        if (ReadPause(now) != null) return false;
        bool any = false;
        int budget = _inline ? 256 : 4;
        while (budget-- > 0 && _queue.LocalCount > 0 && _reader.CanStart(network: false))
        {
            if (!_queue.TryDequeueLocal(out int id, out long priority)) break;
            if (!StartJob(id, priority, null, now)) break;
            any = true;
        }
        budget = _inline ? 256 : 4;
        while (budget-- > 0 && _queue.NetworkCount > 0 && _reader.CanStart(network: true))
        {
            if (!_queue.TryDequeueNetwork(s => _reader.ShareReady(s, now) && ShareOnline(s), out int id, out long priority, out string share)) break;
            if (!StartJob(id, priority, share, now)) break;
            any = true;
        }
        return any;
    }

    /// <summary>A share is readable when no root holding it is Offline (reads are gated per root, never on the global state).</summary>
    bool ShareOnline(string share)
    {
        foreach (var r in _roots.All)
            if (string.Equals(r.Share, share, StringComparison.OrdinalIgnoreCase) && _gate.StateOf(r.Name) is { Status: RootStatus.Offline })
                return false;
        return true;
    }

    /// <summary>Prepares and starts one queued file; false when dispatching must stop for now (the generation moved, the lane is busy).</summary>
    bool StartJob(int id, long priority, string? share, long now)
    {
        var (job, requeue) = PrepareJob(id, priority, now);
        if (requeue) { _queue.Enqueue(id, priority, share); return false; }
        if (job == null) return true;
        if (!_reader!.Start(job)) { _queue.Enqueue(id, priority, share); return false; }
        _inFlight.Add(job);
        if (_inline) ProcessResults(now);
        return true;
    }

    /// <summary>A queued id made into a job under the read lock: alive, in scope, its key, paths and stamp now.</summary>
    (ReadJob? Job, bool Requeue) PrepareJob(int id, long priority, long now)
    {
        string full, rel;
        ulong key;
        FolderState folder;
        ContentKind kind;
        long size, modified;
        FileAttributes attributes;
        var sw = Stopwatch.StartNew();
        LockRead();
        try
        {
            if (_index.Generation != _gen) return (null, true);
            if (!_walker.TryKeyOf(id, out key, out folder, out kind)) return (null, false);
            full = FullPath(folder.Slot, id);
            rel = RelativePath(folder.Slot, id);
            size = _index.Size(id);
            modified = _index.Modified(id);
            attributes = _index.Attributes(id);
        }
        finally
        {
            UnlockRead();
            NoteHold(sw, "prepare");
        }
        var root = _roots[folder.Slot];
        if (_reader!.IsStopped(root.Share)) return (null, false);
        if (_gate.StateOf(root.Name) is { Status: RootStatus.Offline } || _reader.IsBackedOff(root.Share, now))
        {
            Park(root.Share, id);
            return (null, false);
        }
        if (_crashed.TryGetValue(key, out var crash))
        {
            if (crash.Size == size && crash.Modified == modified) { RecordCrash(key, crash, id); return (null, false); }
            ForgetCrash(key);
        }
        if (_retries.Waiting(key, now) || _inFlight.Exists(j => j.Key == key)) return (null, false);
        // Words waiting in the batch for this key are the newest version: read again only when the stamp moved since
        // (stat first, as for a stored one); the result replaces them in place.
        _batchKeys.TryGetValue(key, out var waiting);
        if (waiting != null && SameStamp(waiting.Row, size, modified)) return (null, false);
        var existing = _view!.Find(key);
        DocRow row = default;
        if (waiting != null) row = waiting.Row;
        else if (existing is { } e) row = _view.Segments[e.Segment].Row(e.Doc);
        bool hadRow = waiting != null || existing != null;
        var settings = _settings;
        long maxBytes = kind == ContentKind.Picture || settings.MaxFileSizeMB <= 0 ? 0 : (long)settings.MaxFileSizeMB << 20;
        var request = new ExtractRequest(full, kind, size, modified, root.Network, settings.ReadEncrypted, maxBytes,
            root.Network ? ExtractLimits.Network : ExtractLimits.Local);
        bool changed = hadRow && (row.Size != size || row.Modified != modified);
        return (new ReadJob
        {
            FileId = id,
            Generation = _gen,
            Key = key,
            Volume = root.Volume,
            RelativePath = rel,
            RootName = root.Name,
            RootPath = root.Name,
            Share = root.Share,
            Network = root.Network,
            Request = request,
            HadRow = hadRow,
            StoredSize = row.Size,
            StoredModified = row.Modified,
            StoredStatus = row.Status,
            StoredFlags = row.Flags,
            StoredVersion = row.ExtractorVersion,
            Encrypted = (attributes & FileAttributes.Encrypted) != 0,
            // An unknown date, or a stamp that differs from the stored one: stat first and read only when the disk agrees.
            StatFirst = modified == 0 || changed,
            Priority = priority,
        }, false);
    }

    void Park(string share, int id)
    {
        if (!_parked.TryGetValue(share, out var list)) _parked[share] = list = [];
        if (list.Count < 1_000_000) list.Add(id);
    }

    bool ProcessResults(long now)
    {
        if (_reader == null || _reader.Results.IsEmpty) return false;
        var verify = new List<ReadOutcome>();
        while (_reader.Results.TryDequeue(out var o))
        {
            _inFlight.Remove(o.Job);
            var job = o.Job;
            // What the source didn't wait for through the request's throttle hook (it charged that as it read).
            if (job.Network && o.Read.Doc is { } charged) _reader.Charge(job.Share, charged.BytesRead - Interlocked.Read(ref job.ThrottledBytes), now);
            switch (o.Kind)
            {
                case ReadResultKind.Cancelled:
                    if (job.Generation == _gen && !_stopping) _queue.Enqueue(job.FileId, job.Priority, job.Network ? job.Share : null);
                    break;
                case ReadResultKind.Unchanged:
                    _retries.Succeeded(job.Key);
                    break;
                case ReadResultKind.Vanished:
                    break;
                case ReadResultKind.Retry:
                    _retries.Fail(job.Key, job.Generation == _gen ? job.FileId : -1, now);
                    break;
                case ReadResultKind.Offline:
                    _reader.BackOff(job.Share, now);
                    if (job.Generation == _gen) Park(job.Share, job.FileId);
                    break;
                case ReadResultKind.TimedOut:
                case ReadResultKind.Read:
                    verify.Add(o);
                    break;
            }
        }
        if (verify.Count == 0) return true;
        // Results are checked against the id, generation and key before they are added; a mismatch is dropped and the
        // follower looks at the file again. A generation that moved since the drain waits for the next one.
        var keysNow = new Dictionary<ReadJob, ulong?>();
        var sw = Stopwatch.StartNew();
        LockRead();
        try
        {
            if (_index.Generation != _gen)
            {
                foreach (var o in verify) { _reader.Results.Enqueue(o); _inFlight.Add(o.Job); }
                return true;
            }
            foreach (var o in verify)
                keysNow[o.Job] = o.Job.Generation == _gen && _walker.TryKeyOf(o.Job.FileId, out ulong key, out _, out _) ? key : null;
        }
        finally
        {
            UnlockRead();
            NoteHold(sw, "verify");
        }
        foreach (var o in verify)
        {
            var job = o.Job;
            var read = o.Kind == ReadResultKind.TimedOut
                ? new ContentRead(ExtractedDoc.Failed(job.Request.Kind, DocStatus.TimedOut), job.Request.ExpectedSize, job.Request.ExpectedModifiedUtc, false, false)
                : o.Read;
            Reads++;
            NoteRead(now);
            // Only a read caused by a change counts towards "at most once every 10 minutes" (the first read doesn't).
            if (job.HadRow && (job.StoredSize != job.Request.ExpectedSize || job.StoredModified != job.Request.ExpectedModifiedUtc))
                _limiter.Read(job.Key, now);
            if (job.Generation != _gen)
            {
                // A Clear came while it was read: keep the words unbound; the reconcile binds them by key.
                Record(job, read, fileId: -1);
                continue;
            }
            if (keysNow[job] != job.Key)
            {
                _settle.Touch(job.FileId, now, SettleKind.Changed);
                continue;
            }
            Record(job, read, job.FileId);
        }
        return true;
    }

    void NoteRead(long now)
    {
        if (_readingSince == long.MinValue) _readingSince = now;
        _readTimes.Enqueue(now);
        while (_readTimes.Count > 0 && _readTimes.Peek() < now - 600_000) _readTimes.Dequeue();
    }

    /// <summary>Adds what was read to the batch; nothing when the same left-out record would be written again.</summary>
    void Record(ReadJob job, ContentRead read, int fileId)
    {
        var doc = read.Doc;
        _retries.Succeeded(job.Key);
        ForgetCrash(job.Key);
        if (_store == null || _store.IsReadOnly) return;
        long size = read.Size >= 0 ? read.Size : job.Request.ExpectedSize;
        long modified = read.ModifiedUtc != 0 ? read.ModifiedUtc : job.Request.ExpectedModifiedUtc;
        bool nothingStored = doc.Status is DocStatus.CloudOnly or DocStatus.OptedOut or DocStatus.EfsEncrypted or DocStatus.PasswordProtected
            or DocStatus.TooBig or DocStatus.Damaged or DocStatus.Unsupported;
        if (job.HadRow && nothingStored && doc.Status == job.StoredStatus && doc.ExtractorVersion == job.StoredVersion
            && size == job.StoredSize && modified == job.StoredModified)
            return;
        var flags = DocFlags.None;
        if (doc.Status == DocStatus.Truncated) flags |= DocFlags.Truncated;
        if (doc.Picture != null) flags |= DocFlags.HasPicture;
        // An encrypted file's words, kept because Settings allows it: turning that off reads it again (and drops them).
        if (job.Encrypted && job.Request.ReadEncrypted && doc.Status is DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText)
            flags |= DocFlags.EncryptedOptIn;
        if (doc.Status == DocStatus.TimedOut)
            flags |= job.HadRow && job.StoredStatus == DocStatus.TimedOut && (job.StoredFlags & DocFlags.Attempt1) != 0
                ? DocFlags.Attempt1 | DocFlags.Attempt2 : DocFlags.Attempt1;
        var row = new DocRow(job.Key, modified, size, job.Volume, (ushort)Math.Clamp(doc.Pages, 0, ushort.MaxValue), job.Request.Kind,
            doc.Status, doc.ExtractorVersion, flags);
        AddToBatch(row, job.RelativePath, new StoredDoc(doc.Body, doc.Fields, doc.Picture), doc.BytesRead, doc.ElapsedMs, fileId);
    }

    /// <summary>
    /// The file a reader was in when the app last stopped unexpectedly: recorded Crashed (one strike), not read. Its culprit
    /// is forgotten only once the record is committed, so a commit that fails never lets the file be opened again.
    /// </summary>
    void RecordCrash(ulong key, MarkerEntry entry, int fileId)
    {
        if (_store == null || _store.IsReadOnly || _view == null) return;
        DocRow row;
        string rel;
        LockRead();
        try
        {
            if (_index.Generation != _gen || !_walker.TryKeyOf(fileId, out ulong k, out var folder, out var kind) || k != key) return;
            rel = RelativePath(folder.Slot, fileId);
            row = new DocRow(key, entry.Modified, entry.Size, _roots[folder.Slot].Volume, 0, kind, DocStatus.Crashed, 0, DocFlags.None);
        }
        finally { UnlockRead(); }
        AddToBatch(row, rel, new StoredDoc("", [], null), 0, 0, fileId, crashed: true);
    }

    void ForgetCrash(ulong key)
    {
        if (_crashed.Remove(key)) _marker?.Uncarry(key);
    }

    // ───────────────────────────── batch, commit, merge ─────────────────────────────

    /// <summary>Something waits for the next commit.</summary>
    bool HasBatch => _adds.Count > 0 || _batchDeletes.Count > 0;

    /// <summary>The batch's age counts from its first change.</summary>
    void NoteBatchStart()
    {
        if (!HasBatch) _batchStartMs = _clockMs();
    }

    /// <summary>Adds new words to the batch; a newer version of a key (or of the same file under another key) replaces the waiting one.</summary>
    void AddToBatch(in DocRow row, string relativePath, StoredDoc doc, long bytesRead, int elapsedMs, int fileId, bool crashed = false)
    {
        NoteBatchStart();
        long raw = RawBytesOf(doc);
        if (_batchKeys.TryGetValue(row.Key, out var add))
        {
            _batchRaw -= add.Raw;
            if (add.FileId >= 0 && _batchById.TryGetValue(add.FileId, out var was) && was == add) _batchById.Remove(add.FileId);
        }
        else
        {
            add = new PendingAdd();
            _adds.Add(add);
            _batchKeys[row.Key] = add;
        }
        add.Row = row;
        add.Path = relativePath;
        add.Doc = doc;
        add.Source = default;
        add.BytesRead = bytesRead;
        add.ElapsedMs = elapsedMs;
        add.Raw = raw;
        add.Crashed = crashed;
        add.FileId = fileId;
        if (fileId >= 0)
        {
            // The file's older waiting version under another key (it was renamed after that read) is superseded.
            if (_batchById.TryGetValue(fileId, out var older) && older != add) RemoveAdd(older);
            _batchById[fileId] = add;
        }
        _batchRaw += raw;
    }

    /// <summary>Adds a copy of a document of the batch's view under a new key and path (a re-key).</summary>
    void AddCopy(DocRef source, in DocRow row, string relativePath, int fileId)
    {
        NoteBatchStart();
        var add = new PendingAdd { Row = row, Path = relativePath, Source = source, FileId = fileId };
        _adds.Add(add);
        _batchKeys[row.Key] = add;
        if (fileId >= 0)
        {
            if (_batchById.TryGetValue(fileId, out var older) && older != add) RemoveAdd(older);
            _batchById[fileId] = add;
        }
    }

    /// <summary>Moves a waiting document to a re-keyed file's new key and path (whatever waited under that key goes).</summary>
    void MoveAdd(PendingAdd add, in RekeyTarget t)
    {
        if (_batchKeys.TryGetValue(add.Row.Key, out var was) && was == add) _batchKeys.Remove(add.Row.Key);
        if (_batchKeys.TryGetValue(t.Key, out var clash) && clash != add) RemoveAdd(clash);
        add.Row = add.Row with { Key = t.Key, Volume = _roots[t.Slot].Volume };
        add.Path = t.Rel;
        _batchKeys[t.Key] = add;
    }

    /// <summary>Takes a waiting document out of the batch (its file left, or a newer version replaced it).</summary>
    void RemoveAdd(PendingAdd add)
    {
        int i = _adds.IndexOf(add);
        if (i < 0) return;
        _adds.RemoveAt(i);
        if (_batchKeys.TryGetValue(add.Row.Key, out var byKey) && byKey == add) _batchKeys.Remove(add.Row.Key);
        if (add.FileId >= 0 && _batchById.TryGetValue(add.FileId, out var byId) && byId == add) _batchById.Remove(add.FileId);
        _batchRaw -= add.Raw;
    }

    static long RawBytesOf(StoredDoc doc)
    {
        long n = System.Text.Encoding.UTF8.GetByteCount(doc.Body);
        foreach (var (_, value) in doc.Fields) n += System.Text.Encoding.UTF8.GetByteCount(value);
        return n;
    }

    void Delete(DocRef doc, ulong key)
    {
        if (_store == null || _store.IsReadOnly) return;
        if (_batchDeleteSet.Contains(doc)) return;
        NoteBatchStart();
        _batchDeleteSet.Add(doc);
        _batchDeletes.Add(doc);
        _pendingDeletes.Remove(key);
    }

    void DiscardBatch()
    {
        _adds.Clear();
        _batchKeys.Clear();
        _batchById.Clear();
        _batchDeletes.Clear();
        _batchDeleteSet.Clear();
        _batchRaw = 0;
        _commitSoon = false;
    }

    /// <summary>
    /// Commit cadence: while files wait, at 2,000 documents, 16 MB of text or 5 minutes; 2 s after the queue empties
    /// (an edited file is searchable seconds after it is saved). ContentIndexOptions.CommitAfter replaces both (tests, demo).
    /// After a commit failed, only its own retry timer decides.
    /// </summary>
    void MaybeCommit(long now)
    {
        // Files that can't be read now (reading is paused) don't hold a commit back.
        int waiting = _inFlight.Count + _rekeys.Count + (ReadPause(now) == null ? _queue.Count + _settle.Count : 0);
        if (waiting > 0) _queueEmptySince = long.MinValue;
        else if (_queueEmptySince == long.MinValue) _queueEmptySince = now;
        if (!HasBatch) { _commitSoon = false; return; }
        bool due;
        if (_commitFailures > 0) due = now >= _commitRetryAt;
        else if (_options.CommitAfter is { } after)
            due = now - _batchStartMs >= after.TotalMilliseconds || waiting == 0 && now - _queueEmptySince >= Math.Min(after.TotalMilliseconds, CommitIdleMs);
        else if (waiting > 0) due = _adds.Count >= CommitDocs || _batchRaw >= CommitBytes || now - _batchStartMs >= CommitBusyMs;
        else due = now - _queueEmptySince >= CommitIdleMs;
        if (due || _commitSoon && _commitFailures == 0) CommitBatch();
    }

    /// <summary>
    /// Commits the batch (a store batch made from it, then Commit) and moves the map along: deleted and replaced documents
    /// unmapped, the new segment's documents bound to their ids through LocalOfAdded, dropped segments removed; then
    /// publishes. When the store throws, nothing is lost: the batch stays as it is and is tried again after 10 s, 1 min,
    /// 5 min, then every 15 min, with reading paused meanwhile (a store that keeps failing never has the same files read
    /// again and again). <paramref name="force"/> tries at once (a checkpoint or a stop). True when nothing waits any more.
    /// </summary>
    bool CommitBatch(bool force = false)
    {
        if (!HasBatch) return true;
        if (_store == null || _view == null || _store.IsReadOnly) return false;
        long now = _clockMs();
        if (!force && _commitFailures > 0 && now < _commitRetryAt) return false;
        var adds = _adds.ToArray();
        var deletes = _batchDeletes.ToArray();
        long raw = _batchRaw;
        var positions = new int[adds.Length];
        var sw = Stopwatch.StartNew();
        bool hadDamage = HasDamage();
        StoreChange change;
        IStoreBatch? batch = null;
        try
        {
            batch = _store.BeginBatch();
            for (int i = 0; i < adds.Length; i++)
            {
                var a = adds[i];
                positions[i] = a.Doc == null ? batch.AddCopy(a.Source, a.Row, a.Path) : batch.Add(a.Row, a.Path, a.Doc, a.BytesRead, a.ElapsedMs);
            }
            foreach (var d in deletes) batch.Delete(d);
            change = batch.Commit();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try { batch?.Dispose(); } catch (Exception x) when (x is not OutOfMemoryException) { }
            _commitRetryAt = now + CommitRetryMs[Math.Min(_commitFailures, CommitRetryMs.Length - 1)];
            _commitFailures++;
            _saveTrouble = "Couldn't save the text index (trying again)";
            return false;
        }
        try { batch.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        DiscardBatch();
        _commitFailures = 0;
        _commitRetryAt = 0;
        _saveTrouble = null;
        if (raw > 0 && sw.ElapsedMilliseconds > 0) _commitBytesPerMs = 0.7 * _commitBytesPerMs + 0.3 * (raw / (double)sw.ElapsedMilliseconds);
        // A damaged segment the commit dropped: its files are read again (the reconcile finds them without a stored row).
        if (hadDamage && change.DroppedSegments.Count > 0) _reconcilePending = true;
        var old = _view;
        var b = _map.ToBuilder();
        var oldIds = old.Segments.Select(s => s.Id).ToArray();
        foreach (var d in deletes)
            if ((uint)d.Segment < (uint)oldIds.Length && b.HasSegment(oldIds[d.Segment])) b.Set(oldIds[d.Segment], d.Doc, -1);
        var binds = new (int Pos, int FileId)[adds.Length];
        for (int i = 0; i < adds.Length; i++)
        {
            binds[i] = (positions[i], adds[i].FileId);
            if (old.Find(adds[i].Row.Key) is { } replaced && (uint)replaced.Segment < (uint)oldIds.Length && b.HasSegment(oldIds[replaced.Segment]))
                b.Set(oldIds[replaced.Segment], replaced.Doc, -1);
        }
        ApplyChange(b, change, binds);
        old.Dispose();
        Commits++;
        foreach (var a in adds)
            if (a.Crashed)
            {
                _crashed.Remove(a.Row.Key);
                _marker?.Uncarry(a.Row.Key);
            }
        _host.Committed(LongPass);
        return true;
    }

    /// <summary>Takes over the change's view (the caller owns that reference) and moves the map to it.</summary>
    void ApplyChange(DocIdMap.Builder b, StoreChange change, (int Pos, int FileId)[] binds)
    {
        _view = change.View;
        if (change.NewSegment is { } ns)
        {
            // Segments rewritten into the new one (a merge, or a commit whose copies moved most of a segment) carry their
            // ids over first; Merge replaces the new segment's entry, so the added documents are bound after it.
            int count = DocCountOf(change.View, ns);
            if (change.Remaps.Count > 0) b.Merge(change.Remaps, ns, count);
            else b.AddSegment(ns, count);
            foreach (var (pos, fileId) in binds)
                if (fileId >= 0 && (uint)pos < (uint)change.LocalOfAdded.Length && (uint)change.LocalOfAdded[pos] < (uint)count)
                    b.Set(ns, change.LocalOfAdded[pos], fileId);
        }
        // After the remaps: a rewritten segment is listed as dropped too, and its ids have moved by now.
        foreach (uint dropped in change.DroppedSegments)
            if (dropped != change.NewSegment) b.RemoveSegment(dropped);
        // Segments the view has and the map doesn't: known, unbound; segments gone from the view: dropped.
        var present = new HashSet<uint>();
        foreach (var seg in change.View.Segments)
        {
            present.Add(seg.Id);
            if (!b.HasSegment(seg.Id)) b.AddSegment(seg.Id, seg.DocCount);
        }
        foreach (uint id in _map.SegmentIdList)
            if (!present.Contains(id)) b.RemoveSegment(id);
        _map = b.Build(_gen);
        Publish();
    }

    static int DocCountOf(IStoreView view, uint segment)
    {
        foreach (var s in view.Segments) if (s.Id == segment) return s.DocCount;
        return 0;
    }

    /// <summary>
    /// A merge between batches when the policy has one due: large merges only when the user has been idle for 2 minutes and
    /// the PC is on mains power; between chunks a merge waits out the duty cycle while the user is active.
    /// </summary>
    void MaybeMerge(long now)
    {
        // Never while anything waits: the batch's copies and deletes name documents of this view.
        if (HasBatch || _store == null || _store.IsReadOnly || _view == null) return;
        if (now - _lastMergeCheck < MergeCheckMs && Commits == _mergeCheckedCommits) return;
        _lastMergeCheck = now;
        _mergeCheckedCommits = Commits;
        if (!_options.IgnorePower && PowerRules.MergePause(_powerState, _settings.ReadOnBattery) != null) return;
        bool allowLarge = _options.IgnorePower || PowerRules.AllowLargeMerge(_powerState);
        MergePlan? plan;
        try { plan = _store.PlanMerge(allowLarge); }
        catch (Exception e) when (e is not OutOfMemoryException) { return; }
        if (plan == null || plan.Large && !allowLarge) return;
        StoreChange change;
        try
        {
            change = _store.Merge(plan, _cts.Token, DutyPace());
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException) { return; }
        var old = _view;
        ApplyChange(_map.ToBuilder(), change, []);
        old.Dispose();
        _host.Committed(LongPass);
    }

    bool HasDamage()
    {
        try { return _store?.HasDamage ?? false; }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// Integrity between batches: a damaged segment (a CRC failure at open, while searching, or in the scrub) is dropped by
    /// an otherwise empty commit, and the reconcile then reads its files again; while nothing else is going on (the queue
    /// empty, the user idle), the idle scrub verifies one more segment every 10 s until each was checked once.
    /// </summary>
    void MaybeScrub(long now)
    {
        if (HasBatch || _store == null || _store.IsReadOnly || _view == null || _reconcilePending) return;
        if (HasDamage())
        {
            if (now >= _damageRetryAt) DropDamaged(now);
            return;
        }
        if (_scrubbed || _queue.Count > 0 || _inFlight.Count > 0 || now - _lastScrub < ScrubEveryMs) return;
        if (!_options.IgnorePower && PowerRules.UserActive(_powerState)) return;
        _lastScrub = now;
        try { _scrubbed = _store.Scrub(_cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException) { _scrubbed = true; }
    }

    void DropDamaged(long now)
    {
        StoreChange change;
        IStoreBatch? batch = null;
        try
        {
            batch = _store!.BeginBatch();
            change = batch.Commit();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try { batch?.Dispose(); } catch (Exception x) when (x is not OutOfMemoryException) { }
            _damageRetryAt = now + ScrubEveryMs;
            return;
        }
        try { batch.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        _damageRetryAt = now + ScrubEveryMs; // at most one such commit every 10 s, whatever the store says next
        var old = _view!;
        ApplyChange(_map.ToBuilder(), change, []);
        old.Dispose();
        Commits++;
        _reconcilePending = true;
        _host.Committed(LongPass);
    }

    /// <summary>
    /// A store that opened read-only for the session because a file was in use, denied or on a share that didn't answer:
    /// opened again after 30 s, 1 min, then every 5 min, until it can be written. True when the current store is usable
    /// for this iteration (still the read-only one meanwhile: searches go on, nothing is read).
    /// </summary>
    bool RetryUnavailable(long now)
    {
        if (_unavailableRetryAt == 0)
        {
            _unavailableRetryAt = now + UnavailableRetryMs[Math.Min(_unavailableAttempts++, UnavailableRetryMs.Length - 1)];
            return true;
        }
        if (now < _unavailableRetryAt) return true;
        _unavailableRetryAt = 0;
        DiscardBatch();
        ReleaseView();
        try { _store?.Dispose(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        _store = null;
        _reopenAt = 0;
        if (!Reopen(now)) return false;
        if (_store is not { IsUnavailable: true }) _unavailableAttempts = 0;
        return _view != null;
    }

    // ───────────────────────────── status ─────────────────────────────

    /// <summary>
    /// Counts the index's rows by status (Problems, NoText) and the pictures among them, at most every 10 s after a commit;
    /// <paramref name="force"/> counts after any commit (nothing is being read: the counts "Up to date" shows are final).
    /// </summary>
    void RecountIfDue(long now, bool force = false)
    {
        if (_view == null || !force && now - _lastHistogram < HistogramEveryMs || _view.CommitGeneration == _histogramCommit) return;
        _lastHistogram = now;
        _histogramCommit = _view.CommitGeneration;
        var h = new Dictionary<DocStatus, int>();
        int pictures = 0;
        foreach (var (_, row) in _view.RowsByKey())
        {
            h[row.Status] = h.GetValueOrDefault(row.Status) + 1;
            if (row.Kind == ContentKind.Picture) pictures++;
        }
        Volatile.Write(ref _histogram, h);
        _pictureRows = pictures;
    }

    /// <summary>Pictures among the index's rows at the last count (the histogram's).</summary>
    int _pictureRows;

    void PublishStatus()
    {
        long now = _clockMs();
        // Files still settling (changed less than 5 s ago) are watched, not read yet: alone they don't make the index
        // "reading" (a PC whose files keep changing, the demo's trickle, would otherwise never show it up to date).
        int working = _queue.Count + _inFlight.Count + _parked.Values.Sum(p => p.Count);
        int waiting = working + _settle.Count;
        int total = Math.Max(_inScope, waiting);
        int done = Math.Max(0, total - waiting);
        var h = Volatile.Read(ref _histogram);
        int problems = h.Where(p => IsProblem(p.Key)).Sum(p => p.Value);
        int noText = h.GetValueOrDefault(DocStatus.NoText);
        long bytes = 0;
        try { bytes = _store?.BytesOnDisk ?? 0; } catch (Exception e) when (e is not OutOfMemoryException) { }
        string? pause = ReadPause(now);
        ContentPhase phase;
        string? reason = null;
        if (_deleting) { phase = ContentPhase.Preparing; reason = _doomedBlocked ? "finishing deleting… (the text index is in use by another program)" : "finishing deleting…"; }
        // A store in use for now is tried again on a timer: paused, not stopped.
        else if (_error != null) { phase = _error == InUse ? ContentPhase.Paused : ContentPhase.Stopped; reason = _error; }
        else if (_saveTrouble != null) { phase = ContentPhase.Paused; reason = _saveTrouble; }
        else if (_reader?.LocalStopped is { } local && (_queue.LocalCount > 0 || _queue.NetworkCount == 0)) { phase = ContentPhase.Stopped; reason = local; }
        else if (_reader?.NetworkStopped is { } network && _queue.LocalCount == 0 && _queue.NetworkCount > 0) { phase = ContentPhase.Stopped; reason = network; }
        else if (CatchingUp) phase = ContentPhase.Preparing;
        else if (working > 0 && pause != null) { phase = ContentPhase.Paused; reason = pause; }
        // A pass whose last documents are read but not committed yet (MaybeCommit's 2 s after the queue empties): they
        // aren't searchable, so it is still reading. Files settling hold that commit back (up to 5 min in a busy folder),
        // and then the index stays up to date, as for the settling files themselves.
        else if (working > 0 || CommitsSoon(pause)) phase = ContentPhase.Reading;
        else phase = ContentPhase.UpToDate;
        if (phase == ContentPhase.UpToDate) done = total;
        if (phase is not (ContentPhase.Reading or ContentPhase.Paused)) _readingSince = long.MinValue;
        TimeSpan? remaining = null;
        // A 10-minute moving average, shown only once 2 minutes of reading are behind it.
        if (phase == ContentPhase.Reading && _readingSince != long.MinValue && now - _readingSince >= 120_000 && _readTimes.Count > 1)
        {
            double window = Math.Max(1, now - _readTimes.Peek());
            double perMs = _readTimes.Count / window;
            if (perMs > 0) remaining = TimeSpan.FromMilliseconds(waiting / perMs);
        }
        if (phase == ContentPhase.UpToDate && _lastPhase != ContentPhase.UpToDate && Reads - _readsAtUpToDate >= 2000 && !_inline)
        {
            // A first pass is done: give back what it used (the same compacting GC as IndexManager after its scans).
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        if (phase == ContentPhase.UpToDate) _readsAtUpToDate = Reads;
        _lastPhase = phase;
        bool withPictures = _settings.Mode == ContentMode.DocumentsAndPictures;
        var status = new ContentStatus(phase, done, total, problems, noText, bytes, remaining, reason, Linked: !CatchingUp && _published != null)
        {
            WithPictures = withPictures,
            Pictures = withPictures ? Math.Clamp(_pictureRows, 0, total) : 0,
        };
        var old = Volatile.Read(ref _status);
        Volatile.Write(ref _status, status);
        if (old != status) _host.StatusMaybeChanged();
    }

    /// <summary>Documents read wait for a commit that nothing holds back any more (MaybeCommit's idle rule): at most 2 s away.</summary>
    bool CommitsSoon(string? pause)
        => _adds.Count > 0 && _inFlight.Count + _rekeys.Count + (pause == null ? _queue.Count + _settle.Count : 0) == 0;

    ContentPhase _lastPhase = ContentPhase.Preparing;
    int _readsAtUpToDate;

    /// <summary>A first pass (or a rebuild) with many files still to read.</summary>
    bool LongPass => _queue.Count + _settle.Count > 1000;

    internal static bool IsProblem(DocStatus s) => s is DocStatus.PasswordProtected or DocStatus.TooBig or DocStatus.Damaged or DocStatus.Unsupported
        or DocStatus.AccessDenied or DocStatus.TimedOut or DocStatus.Crashed;

    /// <summary>A document whose file died (or left the scope), deleted when the grace runs out unless bound again.</summary>
    readonly record struct PendingDelete(int DeadId, long Due);

    /// <summary>A document waiting in the batch: new words (Add), or a stored document copied under a new key and path (a re-key).</summary>
    sealed class PendingAdd
    {
        /// <summary>Its row (the key it is committed under).</summary>
        public DocRow Row;
        /// <summary>Its volume-relative path.</summary>
        public string Path = "";
        /// <summary>The words; null for a copy of <see cref="Source"/>.</summary>
        public StoredDoc? Doc;
        /// <summary>The copied document of the batch's view (copies only).</summary>
        public DocRef Source;
        /// <summary>Bytes read (statistics) and raw UTF-8 bytes of the words (commit cadence).</summary>
        public long BytesRead, Raw;
        /// <summary>Time the read took (statistics).</summary>
        public int ElapsedMs;
        /// <summary>The id it is bound to at commit; -1 = none.</summary>
        public int FileId = -1;
        /// <summary>A Crashed record: its culprit is forgotten only once it is committed.</summary>
        public bool Crashed;
    }
}

/// <summary>What a command asks of the follower.</summary>
internal enum CommandKind : byte { Apply, Pause, Resume, Rebuild, RetryProblems, Checkpoint }

/// <summary>A command queued to the follower thread.</summary>
internal sealed record Command(CommandKind Kind)
{
    /// <summary>Apply: the new settings (a copy).</summary>
    public ContentSettings? Settings { get; init; }
    /// <summary>Pause: for how long (null = until Resume or restart).</summary>
    public TimeSpan? Pause { get; init; }
    /// <summary>Checkpoint: commit only what fits before this time (follower clock).</summary>
    public long Deadline { get; init; } = long.MaxValue;
    /// <summary>Set when the command ran (or was ignored).</summary>
    public ManualResetEventSlim? Done { get; init; }
}

/// <summary>
/// Whether a stored document must be read again (§10 table): its file changed (size or date; an unknown date is stat'ed
/// first), or its status allows a new try now: CloudOnly at each full reconcile, OptedOut and EFS when the attribute went
/// away (or encrypted files are allowed), TooBig when the size limit allows it, AccessDenied once its record is a day old,
/// TimedOut once more, and whatever an extractor upgrade reads again (last in the queue). Crashed waits for a change.
/// </summary>
internal static class ReadPolicy
{
    /// <summary>
    /// The decision: read or not, at which queue level, stat first or not; or <paramref name="Drop"/>: the stored words go
    /// at once, without reading the file, and a text-less record with this status takes their place.
    /// </summary>
    internal readonly record struct Reason(bool Read, int Level, bool StatFirst, DocStatus? Drop = null)
    {
        /// <summary>Nothing to read.</summary>
        public static Reason None => default;
    }

    const FileAttributes NotContentIndexed = (FileAttributes)0x2000, Encrypted = (FileAttributes)0x4000;
    const long DayTicks = 24L * 3600 * 10_000_000;

    /// <summary>Decides for one stored row and its file's stamp, attributes and name in the name index.</summary>
    public static Reason Decide(in DocRow row, long size, long modified, FileAttributes attributes, string name, ContentSettings settings,
        long segmentCreatedUtc, long nowFileTimeUtc, bool fullReconcile)
    {
        // Privacy: a file Windows marks "don't index contents" (or encrypts, while encrypted files aren't read) after it was
        // read loses its words at once, whatever pauses reading (setting either attribute changes neither size nor date,
        // and the file would not be opened for it anyway).
        if (row.Status is DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText)
        {
            if ((attributes & NotContentIndexed) != 0) return new Reason(false, 0, false, DocStatus.OptedOut);
            if ((attributes & Encrypted) != 0 && !settings.ReadEncrypted) return new Reason(false, 0, false, DocStatus.EfsEncrypted);
        }
        if (modified == 0) return new Reason(true, WorkQueue.Newest, StatFirst: true);
        if (size != row.Size || modified != row.Modified) return new Reason(true, WorkQueue.Newest, StatFirst: true);
        // Encrypted files' words are kept only while Settings allows it (privacy): still encrypted, they were dropped above;
        // decrypted since, the file is read again without the flag.
        if ((row.Flags & DocFlags.EncryptedOptIn) != 0 && !settings.ReadEncrypted) return new Reason(true, WorkQueue.Opened, false);
        switch (row.Status)
        {
            case DocStatus.CloudOnly:
                return fullReconcile ? new Reason(true, WorkQueue.Newest, false) : Reason.None;
            case DocStatus.OptedOut:
                return (attributes & NotContentIndexed) == 0 ? new Reason(true, WorkQueue.Newest, false) : Reason.None;
            case DocStatus.EfsEncrypted:
                return settings.ReadEncrypted || (attributes & Encrypted) == 0 ? new Reason(true, WorkQueue.Newest, false) : Reason.None;
            case DocStatus.TooBig:
                return row.Kind != ContentKind.Picture && settings.MaxFileSizeMB > 0 && row.Size > (long)settings.MaxFileSizeMB << 20
                    ? Reason.None : new Reason(true, WorkQueue.Newest, false);
            case DocStatus.AccessDenied:
                return nowFileTimeUtc - segmentCreatedUtc >= DayTicks ? new Reason(true, WorkQueue.Upgrade, false) : Reason.None;
            case DocStatus.TimedOut:
                return (row.Flags & DocFlags.Attempt2) == 0 ? new Reason(true, WorkQueue.Upgrade, false) : Reason.None;
            case DocStatus.Crashed:
                return Reason.None;
        }
        var reader = ReaderFor(ContentTypes.ExtensionOf(name), row.Kind);
        if (reader != null && reader.Version != row.ExtractorVersion && ExtractorChanges.ShouldReread(reader, row.ExtractorVersion, row.Status))
            return new Reason(true, WorkQueue.Upgrade, false);
        return Reason.None;
    }

    /// <summary>The reader that handles a stored document now (ContentReaders.ReaderFor allocates nothing; a reconcile asks for every document).</summary>
    internal static IContentReader? ReaderFor(ReadOnlySpan<char> extension, ContentKind kind) => ContentReaders.ReaderFor(extension, kind);
}

/// <summary>
/// How many ids the next read-lock slice covers: halved after a slice over 4 ms (first-run code, a busy or slow PC),
/// doubled after one under 1 ms, between 256 and the maximum; it starts at <paramref name="start"/> (1,024 by default).
/// </summary>
internal struct AdaptiveSlice(int max, int start = 1024)
{
    int _size = Math.Clamp(start, Math.Min(256, max), max);

    /// <summary>Ids for the next slice.</summary>
    public readonly int Size => _size;

    /// <summary>The last slice took this long.</summary>
    public void Took(double ms)
    {
        if (ms > 4) _size = Math.Max(256, _size / 2);
        else if (ms < 1) _size = Math.Min(max, _size * 2);
    }
}

/// <summary>The full reconcile's candidates as two parallel arrays (12 bytes each), grown by half when the estimate is short.</summary>
internal sealed class CandidateArrays(int capacity)
{
    /// <summary>Document keys (valid up to Count).</summary>
    public ulong[] Keys = new ulong[capacity];
    /// <summary>FileIndex ids, parallel to Keys.</summary>
    public int[] Ids = new int[capacity];
    /// <summary>Candidates added.</summary>
    public int Count;

    /// <summary>Appends one candidate.</summary>
    public void Add(ulong key, int id)
    {
        if (Count == Keys.Length)
        {
            int size = Math.Max(1024, Keys.Length + Keys.Length / 2);
            Array.Resize(ref Keys, size);
            Array.Resize(ref Ids, size);
        }
        Keys[Count] = key;
        Ids[Count] = id;
        Count++;
    }
}
