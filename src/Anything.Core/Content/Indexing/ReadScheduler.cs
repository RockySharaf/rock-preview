using System.Collections.Concurrent;
using System.Diagnostics;
using Anything.Core.Content.Store;
using Anything.Core.Indexing;

namespace Anything.Core.Content.Indexing;

/// <summary>One file handed to a reader lane, with everything the follower worked out about it under the index lock.</summary>
internal sealed class ReadJob
{
    /// <summary>Set by the scheduler: ties a watchdog cancel to this read only.</summary>
    public long Seq;
    public required int FileId;
    public required long Generation;
    public required ulong Key;
    public required ushort Volume;
    public required string RelativePath;
    public required string RootName;
    public required string RootPath;
    /// <summary>The share's throttle and back-off key (the volume key of a network root).</summary>
    public required string Share;
    public required bool Network;
    public required ExtractRequest Request;
    /// <summary>Stat first, and read only when the size or date differs from the stored row's (unknown dates, CloudOnly).</summary>
    public bool StatFirst;
    /// <summary>A live stored row for the key: its stamp, status and flags.</summary>
    public bool HadRow;
    public long StoredSize;
    public long StoredModified;
    public DocStatus StoredStatus;
    public DocFlags StoredFlags;
    public byte StoredVersion;
    /// <summary>The name index said the file is encrypted by Windows (EFS).</summary>
    public bool Encrypted;
    public long Priority;
    /// <summary>Bytes the source waited on the share's throttle for during the read (charged already; the rest is charged after).</summary>
    public long ThrottledBytes;
    /// <summary>Milliseconds the read spent waiting on the throttle (the watchdog leaves them out, as the reader's time limit does).</summary>
    public long ThrottledMs;
    /// <summary>When the throttle wait going on now started (the scheduler's clock); 0 while none is.</summary>
    public long ThrottleSince;
}

/// <summary>How a read ended.</summary>
internal enum ReadResultKind : byte
{
    /// <summary>Read (any status, problems included): record it.</summary>
    Read,
    /// <summary>Stat'ed first and unchanged: nothing to do.</summary>
    Unchanged,
    /// <summary>The file is gone (stat failed).</summary>
    Vanished,
    /// <summary>Temporary trouble: try again later, record nothing.</summary>
    Retry,
    /// <summary>The drive or share didn't answer: back its root off.</summary>
    Offline,
    /// <summary>The watchdog had to free or abandon the read and the root still answers: record TimedOut.</summary>
    TimedOut,
    /// <summary>Stopped while reading: queue it again next time.</summary>
    Cancelled,
}

/// <summary>A finished read.</summary>
internal sealed record ReadOutcome(ReadJob Job, ReadResultKind Kind, ContentRead Read, long WallMs, TimeSpan CpuTime, bool Abandoned = false);

/// <summary>Switches of the scheduler.</summary>
internal sealed record ReadSchedulerOptions
{
    /// <summary>Reads run on the caller's thread when started (tests): no threads, no watchdog, no sleeps.</summary>
    public bool Inline { get; init; }
    /// <summary>Extra wait after each file (the demo's pace).</summary>
    public int PaceMs { get; init; }
    /// <summary>The user is active: after each file, wait as long as it took (50% duty cycle).</summary>
    public Func<bool> UserActive { get; init; } = () => false;
    /// <summary>Wakes the follower when a result is ready.</summary>
    public Action Wake { get; init; } = () => { };
    /// <summary>Milliseconds clock.</summary>
    public Func<long> ClockMs { get; init; } = () => Environment.TickCount64;
    /// <summary>How often the watchdog looks at the lanes.</summary>
    public int WatchdogPeriodMs { get; init; } = 1000;
    /// <summary>Extra time past a read's limit before its I/O is cancelled (spec: 5 s).</summary>
    public int CancelGraceMs { get; init; } = 5000;
    /// <summary>Extra time past twice the limit before the thread is abandoned (spec: 30 s).</summary>
    public int AbandonGraceMs { get; init; } = 30_000;
    /// <summary>Answers whether a root still lists (0 = yes, else the Win32 error); FolderScanner.ProbeRoot by default.</summary>
    public Func<string, CancellationToken, int> ProbeRoot { get; init; } = FolderScanner.ProbeRoot;
}

/// <summary>
/// The reader lanes (local, network): one file at a time each, in background mode, with a watchdog, the duty cycle, a
/// per-share throttle and back-off. The watchdog checks each lane every second: at the read's time limit + 5 s it cancels
/// the thread's synchronous I/O (OpenThread + CancelSynchronousIo, tied to that read's sequence number, so a late cancel
/// never hits the next file) and when the read then fails, the root is probed: gone → Offline (5-minute back-off), still
/// there → TimedOut. At twice the limit + 30 s the thread is abandoned (a managed loop can't be killed), the file is
/// recorded TimedOut and a fresh thread takes over. Abandoned threads are counted per lane and per root: two in the local
/// lane stop local reading; two on one share stop that share, and four stop the network lane.
/// </summary>
internal sealed class ReadScheduler : IDisposable
{
    /// <summary>Abandoned threads that stop the local lane.</summary>
    public const int MaxAbandonedLocal = 2;
    /// <summary>Abandoned threads on one share that stop it for the session.</summary>
    public const int MaxAbandonedPerRoot = 2;
    /// <summary>Abandoned threads that stop the network lane.</summary>
    public const int MaxAbandonedNetwork = 4;
    /// <summary>How long a root that didn't answer is left alone.</summary>
    public const long BackOffMs = 300_000;

    readonly IContentSource _source;
    readonly CrashMarker _marker;
    readonly ReadSchedulerOptions _options;
    readonly CancellationTokenSource _cts = new();
    readonly Lane _local, _network;
    readonly Timer? _watchdog;
    readonly object _gate = new();
    readonly Dictionary<string, long> _backOff = new(StringComparer.OrdinalIgnoreCase);
    readonly ShareThrottle _throttle;
    readonly Dictionary<string, int> _abandonedByRoot = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _stoppedRoots = new(StringComparer.OrdinalIgnoreCase);
    long _seq;
    bool _disposed;

    /// <summary>Lanes over a source; threads start with the first job.</summary>
    public ReadScheduler(IContentSource source, CrashMarker marker, ReadSchedulerOptions options)
    {
        _source = source;
        _marker = marker;
        _options = options;
        _throttle = new ShareThrottle(options.ClockMs);
        _local = new Lane(this, network: false);
        _network = new Lane(this, network: true);
        if (!options.Inline) _watchdog = new Timer(_ => Watch(), null, options.WatchdogPeriodMs, options.WatchdogPeriodMs);
    }

    /// <summary>Finished reads, for the follower.</summary>
    public ConcurrentQueue<ReadOutcome> Results { get; } = new();

    /// <summary>Throttle for network shares, bytes per second (8 MB/s by default, at least 64 KB/s).</summary>
    public long NetworkBytesPerSecond
    {
        get => _throttle.BytesPerSecond;
        set => _throttle.BytesPerSecond = value;
    }

    /// <summary>Files being read now.</summary>
    public int InFlight => (_local.Busy ? 1 : 0) + (_network.Busy ? 1 : 0);

    /// <summary>Why the local lane stopped for the session; null while it reads.</summary>
    public string? LocalStopped => _local.Stopped;

    /// <summary>Why the network lane stopped for the session; null while it reads.</summary>
    public string? NetworkStopped => _network.Stopped;

    /// <summary>Threads abandoned so far (tests, status).</summary>
    public int AbandonedThreads => _local.Abandoned + _network.Abandoned;

    /// <summary>Whether a lane can take a file now.</summary>
    public bool CanStart(bool network)
    {
        var lane = network ? _network : _local;
        return !_disposed && lane.Stopped == null && !lane.Busy;
    }

    /// <summary>Whether a share may be read now: not stopped, not backed off, and its throttle has caught up.</summary>
    public bool ShareReady(string share, long nowMs)
    {
        lock (_gate)
        {
            if (_stoppedRoots.Contains(share)) return false;
            if (_backOff.TryGetValue(share, out long until) && until > nowMs) return false;
        }
        // One second of burst allowed, then the share waits for its average to come back to the limit.
        return _throttle.Ready(share, nowMs);
    }

    /// <summary>Leaves a root alone for 5 minutes (it didn't answer).</summary>
    public void BackOff(string share, long nowMs)
    {
        lock (_gate) _backOff[share] = nowMs + BackOffMs;
    }

    /// <summary>Whether a share is backed off now.</summary>
    public bool IsBackedOff(string share, long nowMs)
    {
        lock (_gate) return _backOff.TryGetValue(share, out long until) && until > nowMs;
    }

    /// <summary>Whether a share stopped for the session (its reads hung twice).</summary>
    public bool IsStopped(string share)
    {
        lock (_gate) return _stoppedRoots.Contains(share);
    }

    /// <summary>
    /// Charges bytes read from a share against its throttle after the read: the part the source didn't already wait for
    /// through the request's throttle hook (sources that read no real files never call it).
    /// </summary>
    public void Charge(string share, long bytes, long nowMs) => _throttle.Charge(share, bytes, nowMs);

    /// <summary>Hands a file to its lane; false when the lane is busy or stopped. Inline: reads it now.</summary>
    public bool Start(ReadJob job)
    {
        var lane = job.Network ? _network : _local;
        if (_disposed || lane.Stopped != null) return false;
        job.Seq = Interlocked.Increment(ref _seq);
        if (_options.Inline)
        {
            int slot = _marker.Acquire();
            try { Results.Enqueue(Execute(job, slot)); }
            finally { if (slot >= 0) _marker.Release(slot); }
            return true;
        }
        return lane.Hand(job);
    }

    /// <summary>Cancels every read (Stop); lanes take nothing more.</summary>
    public void Cancel()
    {
        _disposed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _local.Interrupt();
        _network.Interrupt();
    }

    /// <summary>Cancels reads and stops the watchdog; stuck threads are left to end on their own (they are background threads).</summary>
    public void Dispose()
    {
        Cancel();
        _watchdog?.Dispose();
    }

    /// <summary>
    /// Reads one file on this thread: stat first when asked, then the source; classifies what came back. Without a marker
    /// slot nothing is read (no crash protection): the file is tried again later.
    /// </summary>
    ReadOutcome Execute(ReadJob job, int slot)
    {
        if (slot < 0) return new ReadOutcome(job, ReadResultKind.Retry, default, 0, TimeSpan.Zero);
        var entry = new MarkerEntry(job.Key, job.Volume, job.Network ? MarkerLane.Network : MarkerLane.Local, job.Request.ExpectedSize, job.Request.ExpectedModifiedUtc);
        uint epoch = _marker.Enter(slot, entry);
        var watch = Stopwatch.StartNew();
        var cpu0 = ContentNative.ThreadCpuTime();
        try
        {
            var ct = _cts.Token;
            ct.ThrowIfCancellationRequested();
            if (job.StatFirst)
            {
                if (!_source.TryStat(job.Request.FullPath, out long size, out long modified, out _))
                    return new ReadOutcome(job, ReadResultKind.Vanished, default, watch.ElapsedMilliseconds, TimeSpan.Zero);
                if (job.HadRow && size == job.StoredSize && modified == job.StoredModified && modified != 0)
                    return new ReadOutcome(job, ReadResultKind.Unchanged, new ContentRead(ExtractedDoc.Failed(job.Request.Kind, job.StoredStatus), size, modified, false, false),
                        watch.ElapsedMilliseconds, TimeSpan.Zero);
            }
            // Network reads wait on the share's bucket inside the read (keyed by the root's volume key, whatever the path's
            // spelling), so a big file is spread out rather than read at once; the wait is left out of the watchdog's clock.
            var request = job.Network ? job.Request with { Throttle = (_, bytes, token) => Throttle(job, bytes, token) } : job.Request;
            var read = _source.Read(request, ct);
            var kind = read.Offline ? ReadResultKind.Offline : read.Retry ? ReadResultKind.Retry : ReadResultKind.Read;
            return new ReadOutcome(job, kind, read, watch.ElapsedMilliseconds, ContentNative.ThreadCpuTime() - cpu0);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return new ReadOutcome(job, ReadResultKind.Cancelled, default, watch.ElapsedMilliseconds, TimeSpan.Zero);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The source promises not to throw for file problems; if it does anyway, nothing is recorded and the file is
            // tried again later (never a permanent problem record from a bug).
            return new ReadOutcome(job, ReadResultKind.Retry, default, watch.ElapsedMilliseconds, TimeSpan.Zero);
        }
        finally { _marker.Leave(slot, epoch); }
    }

    long Throttle(ReadJob job, long bytes, CancellationToken ct)
    {
        Interlocked.Add(ref job.ThrottledBytes, Math.Max(0, bytes));
        long since = _options.ClockMs();
        Interlocked.Exchange(ref job.ThrottleSince, since);
        long waited = 0;
        try { waited = _throttle.Wait(job.Share, bytes, ct); }
        finally
        {
            // The clock's view of the wait, so the watchdog's arithmetic stays in one clock.
            Interlocked.Add(ref job.ThrottledMs, waited > 0 ? Math.Max(0, _options.ClockMs() - since) : 0);
            Interlocked.Exchange(ref job.ThrottleSince, 0);
        }
        return waited;
    }

    /// <summary>After the watchdog cancelled a read's I/O: the root gone → Offline, still answering → TimedOut.</summary>
    ReadOutcome AfterWatchdogCancel(ReadOutcome outcome)
    {
        int error;
        try { error = _options.ProbeRoot(outcome.Job.RootPath, _cts.Token); }
        catch (OperationCanceledException) { return outcome with { Kind = ReadResultKind.Cancelled }; }
        catch (Exception e) when (e is not OutOfMemoryException) { error = 59; }
        bool offline = error != 0 && FolderScanner.IsOfflineError(error);
        return outcome with { Kind = offline ? ReadResultKind.Offline : ReadResultKind.TimedOut };
    }

    void Watch()
    {
        if (_disposed) return;
        long now = _options.ClockMs();
        _local.Watch(now);
        _network.Watch(now);
    }

    void Abandoned(Lane lane, ReadJob job)
    {
        Results.Enqueue(new ReadOutcome(job, ReadResultKind.TimedOut, default, 0, TimeSpan.Zero, Abandoned: true));
        lane.Abandoned++;
        if (!lane.Network)
        {
            if (lane.Abandoned >= MaxAbandonedLocal) lane.Stopped = "Stopped reading after a file hung: " + Path.GetFileName(job.Request.FullPath);
        }
        else
        {
            lock (_gate)
            {
                int n = _abandonedByRoot.GetValueOrDefault(job.Share) + 1;
                _abandonedByRoot[job.Share] = n;
                if (n >= MaxAbandonedPerRoot) _stoppedRoots.Add(job.Share);
            }
            if (lane.Abandoned >= MaxAbandonedNetwork) lane.Stopped = "Stopped reading network files after a file hung: " + Path.GetFileName(job.Request.FullPath);
        }
        _options.Wake();
    }

    /// <summary>One lane: a thread instance at a time (a fresh one after an abandon), with one file in hand.</summary>
    sealed class Lane(ReadScheduler owner, bool network)
    {
        readonly object _gate = new();
        Worker? _worker;
        public readonly bool Network = network;
        public volatile string? Stopped;
        public int Abandoned;

        public bool Busy
        {
            get
            {
                Worker? w;
                lock (_gate) w = _worker;
                return w != null && w.IsBusy;
            }
        }

        public bool Hand(ReadJob job)
        {
            lock (_gate)
            {
                if (Stopped != null) return false;
                if (_worker is { IsBusy: true }) return false;
                _worker ??= new Worker(owner, this);
                lock (_worker.Gate) _worker.Pending = job;
                _worker.Signal.Set();
                return true;
            }
        }

        public void Interrupt()
        {
            Worker? w;
            lock (_gate) w = _worker;
            if (w == null) return;
            w.Signal.Set();
            w.CancelIo(w.ActiveSeq);
        }

        public void Watch(long now)
        {
            Worker? w;
            lock (_gate) w = _worker;
            if (w == null) return;
            ReadJob? job;
            long seq, start;
            int limit;
            lock (w.Gate)
            {
                if (w.ActiveSeq == 0 || w.Active == null) return;
                job = w.Active;
                seq = w.ActiveSeq;
                start = w.StartMs;
                limit = job.Request.Limits.MaxMilliseconds;
            }
            // Throttle waits don't count, the one going on now included: a read held to a share's speed limit isn't hung.
            long waiting = Interlocked.Read(ref job.ThrottleSince) is var since and > 0 ? Math.Max(0, now - since) : 0;
            long elapsed = now - start - Interlocked.Read(ref job.ThrottledMs) - waiting;
            var o = owner._options;
            if (elapsed > limit + o.CancelGraceMs) w.CancelIo(seq);
            if (elapsed > 2L * limit + o.AbandonGraceMs)
            {
                lock (w.Gate)
                {
                    if (w.ActiveSeq != seq || w.AbandonedSeq == seq) return;
                    w.AbandonedSeq = seq;
                }
                lock (_gate)
                {
                    if (ReferenceEquals(_worker, w)) _worker = null; // the next file gets a fresh thread
                }
                owner.Abandoned(this, job);
            }
        }

        public void Done(Worker w)
        {
            lock (_gate)
                if (ReferenceEquals(_worker, w)) _worker = null;
        }
    }

    /// <summary>One reader thread instance.</summary>
    sealed class Worker
    {
        readonly ReadScheduler _owner;
        readonly Lane _lane;
        public readonly object Gate = new();
        public readonly AutoResetEvent Signal = new(false);
        public ReadJob? Pending;
        public ReadJob? Active;
        public long ActiveSeq, AbandonedSeq, CancelledSeq, StartMs;
        /// <summary>The duty cycle's rest after a file ends then (scheduler clock; 0 = none): the lane takes no file meanwhile.</summary>
        public long RestUntil;
        uint _nativeId;
        // Rest owed for the duty cycle and for the demo's pace, taken once it adds up to MinRestMs.
        long _activeOwed, _paceOwed;
        const long MinRestMs = 50;

        public Worker(ReadScheduler owner, Lane lane)
        {
            _owner = owner;
            _lane = lane;
            new Thread(Run) { IsBackground = true, Name = lane.Network ? "Rock Preview content network" : "Rock Preview content reader" }.Start();
        }

        /// <summary>A file is in hand or being read, or the lane rests after one (the duty cycle, the demo's pace).</summary>
        public bool IsBusy
        {
            get { lock (Gate) return Pending != null || ActiveSeq != 0 || RestUntil != 0 && RestUntil > _owner._options.ClockMs(); }
        }

        /// <summary>Frees a read stuck in synchronous I/O, only while it is still the read with this sequence number.</summary>
        public void CancelIo(long seq)
        {
            lock (Gate)
            {
                if (seq == 0 || ActiveSeq != seq || _nativeId == 0) return;
                CancelledSeq = seq;
                nint th = Native.OpenThread(Native.THREAD_TERMINATE, 0, _nativeId);
                if (th != 0)
                {
                    Native.CancelSynchronousIo(th);
                    Native.CloseHandle(th);
                }
            }
        }

        void Run()
        {
            _nativeId = Native.GetCurrentThreadId();
            ContentNative.EnterBackgroundMode();
            int slot = _owner._marker.Acquire();
            using var quiet = Drives.QuietErrors();
            try
            {
                while (!_owner._disposed)
                {
                    ReadJob? job;
                    lock (Gate)
                    {
                        job = Pending;
                        if (job != null)
                        {
                            Active = job;
                            ActiveSeq = job.Seq;
                            StartMs = _owner._options.ClockMs();
                            Pending = null;
                        }
                    }
                    if (job == null)
                    {
                        Signal.WaitOne(1000);
                        continue;
                    }
                    // Every slot was taken when this thread started (on-demand reads, threads left behind): one may be free now.
                    if (slot < 0) slot = _owner._marker.Acquire();
                    var outcome = _owner.Execute(job, slot);
                    // 50% duty cycle while the user is active: rest as long as the files took, and the demo's pace on top.
                    // Rest is saved up and taken once it reaches MinRestMs: a wait shorter than the timer's ~15 ms tick
                    // would cost a small file (a picture's header, 1-2 ms) many times what reading it took.
                    if (_owner._options.UserActive()) _activeOwed += outcome.WallMs; else _activeOwed = 0;
                    _paceOwed += _owner._options.PaceMs;
                    long rest = 0;
                    if (_activeOwed + _paceOwed >= MinRestMs)
                    {
                        rest = Math.Min(60_000, _activeOwed + _paceOwed);
                        _activeOwed = _paceOwed = 0;
                    }
                    bool abandoned, cancelled;
                    lock (Gate)
                    {
                        abandoned = AbandonedSeq == job.Seq;
                        cancelled = CancelledSeq == job.Seq;
                        ActiveSeq = 0;
                        Active = null;
                        // Busy until the rest is over, set before the follower hears of the result: the next file it
                        // hands over would otherwise end the rest at once.
                        if (!abandoned && rest > 0) RestUntil = _owner._options.ClockMs() + rest;
                    }
                    if (abandoned) return; // already recorded as TimedOut; a fresh thread has the lane
                    if (cancelled && outcome.Kind is not (ReadResultKind.Unchanged or ReadResultKind.Cancelled)
                        && !(outcome.Kind == ReadResultKind.Read && outcome.Read.Doc.Status is DocStatus.Ok or DocStatus.Truncated))
                        outcome = _owner.AfterWatchdogCancel(outcome);
                    _owner.Results.Enqueue(outcome);
                    _owner._options.Wake();
                    if (_owner._disposed) return;
                    if (rest > 0)
                    {
                        // Only a Cancel (Stop) ends the rest early: a file handed over meanwhile can't, as the lane is busy.
                        _owner._cts.Token.WaitHandle.WaitOne((int)rest);
                        lock (Gate) RestUntil = 0;
                        if (_owner._disposed) return; // cancelled (Stop): nobody to hand a file over any more
                        // The follower may have found the lane resting: it hands the next file over now.
                        _owner._options.Wake();
                    }
                }
            }
            finally
            {
                _owner._marker.Release(slot);
                _lane.Done(this);
                ContentNative.LeaveBackgroundMode();
            }
        }
    }
}
