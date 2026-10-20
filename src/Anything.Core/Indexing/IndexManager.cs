using System.Security.Principal;

namespace Anything.Core.Indexing;

/// <summary>
/// Owns the <see cref="FileIndex"/>: loads the database, indexes every drive the plan names (<see cref="IndexPlan"/>:
/// NTFS from the MFT + USN journal, anything else by scanning with FindFirstFileEx + FileSystemWatcher), applies
/// exclusions, and saves the database. While running it follows the drives: USB drives and mapped network drives
/// that come and go are added and dropped, network roots are rescanned on a schedule and kept while offline, and
/// drives being ejected are let go of first.
/// </summary>
public sealed class IndexManager : IIndexManager, IIndexerHost
{
    const int DriveTickMs = 3000;
    /// <summary>Drive readiness (a locked drive unlocked, a card put in a reader) has no event: look again this often.</summary>
    const int FullDriveCheckMs = 30_000;
    /// <summary>How often an offline root is checked for being back.</summary>
    const int OfflineProbeMs = 60_000;
    /// <summary>A scan that failed (the service restarted mid-read, say) is tried again after this long.</summary>
    const int RetryFailedScanMs = 120_000;
    /// <summary>
    /// How long stopping waits for a root's task. Probes and scans give up quickly when cancelled; one stuck in a slow
    /// network call is left behind, and whatever it reads later is thrown away (it checks the index generation).
    /// </summary>
    static readonly TimeSpan RootTaskWait = TimeSpan.FromSeconds(3);

    /// <summary>Scans running for one Start/Rebuild; a new run gets a new one, so scans left behind can't skew its count.</summary>
    sealed class ScanCounter { public int Active; }

    readonly object _gate = new();
    readonly object _saveGate = new();
    readonly List<RootIndexer> _roots = [];
    readonly Dictionary<RootIndexer, Task> _rescans = [];
    readonly Dictionary<RootIndexer, IDisposable> _removalWatches = [];
    readonly Dictionary<RootIndexer, long> _lastProbe = [];
    readonly Dictionary<RootIndexer, long> _lastWatchAttempt = [];
    readonly Timer _tick;
    readonly Timer _driveTick;
    /// <summary>The object Start/Apply was given (Settings edits it in place) and the snapshot the index works from.</summary>
    IndexSettings _live = new();
    IndexSettings _settings = new();
    ExclusionFilter _filter = ExclusionFilter.None;
    CancellationTokenSource _cts = new();
    Task? _worker;
    DeviceWatcher? _devices;
    ScanCounter _scans = new();
    uint _planMask;
    long _lastFullPlanTicks;
    volatile IndexState _state = IndexState.Idle;
    volatile string _status = "";
    volatile RootIndexer? _scanningRoot;
    volatile bool _fallbackMode;
    volatile bool _usesService;
    volatile bool _loaded;
    /// <summary>The startup (or rebuild) pass has attached or scanned every root it planned; drives may change from here.</summary>
    volatile bool _settled;
    volatile bool _replanRequested;
    volatile bool _releasePending;
    volatile bool _unsaved;
    /// <summary>Settings handed to <see cref="Apply"/>, applied by the drive tick once the startup pass has settled.</summary>
    IndexSettings? _pendingApply;
    int _changePending;
    int _driveTickBusy;
    bool? _serviceAvailable;
    bool _disposed;
    bool _started;

    public IndexManager()
    {
        IsElevated = CheckElevated();
        _tick = new Timer(_ => OnTick(), null, 250, 250);
        _driveTick = new Timer(_ => OnDriveTick(), null, DriveTickMs, DriveTickMs);
    }

    public FileIndex Index { get; } = new();
    public IndexState State => _state;
    public bool IsElevated { get; }
    public bool UsesService => _usesService;
    public event Action? StateChanged;

    /// <summary>Exceptions from background work, for diagnostics (last one wins).</summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Test hooks: where drives come from, and whether to listen for device notifications. The index never asks network
    /// servers whether they answer: it keeps network drives either way, and finds out when it scans them.
    /// </summary>
    internal Func<IReadOnlyList<DriveEntry>> DriveSource { get; set; } = () => Drives.List(probeNetwork: false);
    internal Func<uint> LetterSource { get; set; } = Drives.LetterMask;
    internal bool WatchDevices { get; set; } = true;

    ExclusionFilter IIndexerHost.Filter => _filter;

    public string StatusText
    {
        get
        {
            var root = _scanningRoot;
            if (root != null && _state == IndexState.Scanning)
            {
                long n = root.ProgressCount;
                return _fallbackMode && root.Kind == RootKind.Folder && root.RootPath.Length == 3 && root.DriveKind == DriveKind.Fixed
                    ? $"Instant indexing is off: scanning folders ({root.RootPath}, {n:N0} objects)"
                    : $"Scanning {root.RootPath} ({n:N0} objects)";
            }
            return _status;
        }
    }

    static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Start(IndexSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartNoLock(settings, forceRescan: false);
        }
    }

    void StartNoLock(IndexSettings settings, bool forceRescan)
    {
        if (_started)
        {
            // Restart with new settings: persist what we have, then reload from the database.
            StopAllNoLock(saveFirst: true);
            Index.Clear();
            _loaded = false;
        }
        _started = true;
        _live = settings;
        _settings = settings.Clone();
        _pendingApply = null;
        _filter = new ExclusionFilter(_settings);
        EnsureDeviceWatcher();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _worker = Task.Run(() => Run(loadDatabase: true, forceRescan, ct), ct);
    }

    /// <summary>
    /// Changed settings, applied to the running index without starting over: drives and folders switched on or off
    /// are added or dropped, and changed exclusions rescan each root in the background, swapping in the new copy when
    /// done (so everything stays searchable meanwhile and offline network drives keep what they had). Returns at once;
    /// during the startup pass the change waits until it is done.
    /// </summary>
    public void Apply(IndexSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Just recorded, without taking _gate: a re-plan holding it can take a few seconds, and the caller is the UI.
        // A Rebuild racing with this rebuilds from the same settings, so applying them once more is harmless.
        if (Volatile.Read(ref _started) && string.Equals(settings.DatabasePath, Volatile.Read(ref _settings).DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _live, settings);
            Interlocked.Exchange(ref _pendingApply, settings.Clone());
            Task.Run(OnDriveTick); // now rather than on the next tick (off the caller's thread: it may list drives)
            return;
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartNoLock(settings, forceRescan: false);
        }
    }

    /// <summary>Applies <see cref="Apply"/>'s settings (on the drive tick, once settled).</summary>
    void ApplyPending()
    {
        bool exclusionsChanged;
        lock (_gate)
        {
            var next = Interlocked.Exchange(ref _pendingApply, null);
            if (next == null || _disposed || !_settled) { if (next != null) _pendingApply ??= next; return; }
            exclusionsChanged = !SameExclusions(_settings, next);
            _settings = next;
            if (exclusionsChanged) _filter = new ExclusionFilter(next);
        }
        Reconcile(drivesChanged: true);
        if (!exclusionsChanged) return;
        // Roots whose entries were built with other rules (their stamp says which) are rescanned in place.
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        foreach (var root in roots)
            if (!root.Released && root.RulesStamp != root.Filter.Stamp)
                ((IIndexerHost)this).RequestRescan(root, "exclusions changed");
    }

    static bool SameExclusions(IndexSettings a, IndexSettings b) =>
        a.ExcludeHidden == b.ExcludeHidden && a.ExcludeSystem == b.ExcludeSystem
        && a.ExcludeFolders.SequenceEqual(b.ExcludeFolders, StringComparer.OrdinalIgnoreCase)
        && a.ExcludePatterns.SequenceEqual(b.ExcludePatterns, StringComparer.OrdinalIgnoreCase);

    /// <summary>Rescans everything from the start, with the settings Start or Apply was given as they are now.</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopAllNoLock(saveFirst: false);
            _started = true;
            Index.Clear();
            _loaded = false;
            _settings = _live.Clone();
            _pendingApply = null;
            _filter = new ExclusionFilter(_settings);
            EnsureDeviceWatcher();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetState(IndexState.Scanning, "Rebuilding...");
            _worker = Task.Run(() => Run(loadDatabase: false, forceRescan: false, ct), ct);
        }
    }

    void EnsureDeviceWatcher()
    {
        if (_devices != null || !WatchDevices) return;
        try
        {
            _devices = new DeviceWatcher();
            _devices.VolumesChanged += () => _replanRequested = true;
        }
        catch (Exception ex) { LastError = ex; }
    }

    /// <summary>Cancels background work and stops all monitors (caller holds _gate).</summary>
    void StopAllNoLock(bool saveFirst)
    {
        _settled = false;
        _cts.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(30)); } catch { }
        WaitForRootTasks();
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        foreach (var r in roots) { try { r.Lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        foreach (var r in roots) { try { r.StopMonitoring(); } catch { } }
        if (saveFirst) TrySave();
        lock (_saveGate)
        {
            // From here the index no longer matches the database: nothing may save until the next run has attached its roots.
            _loaded = false;
            lock (_roots) _roots.Clear();
        }
        foreach (var r in roots) { UnwatchRemoval(r); try { r.Dispose(); } catch { } }
        lock (_lastProbe) _lastProbe.Clear();
        lock (_lastWatchAttempt) _lastWatchAttempt.Clear();
        lock (_rescans) _rescans.Clear(); // tasks left behind finish on their own; they no longer count
        _worker = null;
        _scanningRoot = null;
        _scans = new ScanCounter();
    }

    void WaitForRootTasks()
    {
        Task[] rescans;
        lock (_rescans) rescans = [.. _rescans.Values];
        try { Task.WaitAll(rescans, RootTaskWait); } catch { }
    }

    void SetState(IndexState state, string status)
    {
        _state = state;
        _status = status;
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <summary>Ready once the startup pass is done and no scan runs or waits to start any more.</summary>
    void SettleState()
    {
        if (!_settled || Volatile.Read(ref _scans.Active) != 0) return;
        lock (_rescans) if (_rescans.Count > 0) return; // a root task queued but not scanning yet

        SetState(IndexState.Ready, _fallbackMode ? "Indexing requires administrator: scanning folders" : "");
    }

    void Run(bool loadDatabase, bool forceRescan, CancellationToken ct)
    {
        try
        {
            var settings = _settings;
            var loaded = new List<LoadedRoot>();
            if (loadDatabase)
            {
                SetState(IndexState.Loading, "Loading database...");
                loaded = IndexDatabase.Load(settings.DatabasePath, Index) ?? [];
            }
            ct.ThrowIfCancellationRequested();

            // Decide the roots and match them with what the database had. Drives are listed even when whole drives
            // aren't indexed: they tell which added folders are on a network or USB drive.
            _planMask = LetterSource();
            var plan = IndexPlan.Plan(settings, DriveSource(), _filter);
            _lastFullPlanTicks = Environment.TickCount64;
            _fallbackMode = false;
            _usesService = false;
            _serviceAvailable = null;
            var pending = new List<RootIndexer>();
            var background = new List<RootIndexer>();
            foreach (var planned in plan)
            {
                ct.ThrowIfCancellationRequested();
                var root = CreateRoot(planned);
                var match = loaded.FirstOrDefault(l => string.Equals(RootIndexer.NormalizeRootName(l.Path), root.RootName, StringComparison.OrdinalIgnoreCase));
                bool current = false;
                if (match != null)
                {
                    loaded.Remove(match);
                    try { current = root.AttachLoaded(match) && !forceRescan; } catch { current = false; }
                    // Built with other exclusions (changed while the app was closed, or a rescan for them interrupted):
                    // not current, whatever the journal says. 0: a database from before stamps.
                    if (match.RulesStamp != 0 && match.RulesStamp != root.Filter.Stamp) current = false;
                    // A database from before stamps is taken as built with today's rules, as "current" assumes: from now
                    // on an exclusion rescan cut short leaves a real mismatch behind instead of "unknown".
                    root.RulesStamp = match.RulesStamp != 0 ? match.RulesStamp : root.Filter.Stamp;
                }
                AddRoot(root);
                // Scanned roots go to their own tasks: a network share can take long or not answer at all, and a big
                // exFAT or ReFS drive has no journal to catch up from, so neither holds up the NTFS drives.
                if (root is FolderIndexer) background.Add(root);
                else if (current) root.StartMonitoring(); // replays the journal from the saved USN (catch-up)
                else pending.Add(root);
            }
            // Roots that are no longer configured.
            foreach (var stale in loaded) Index.Remove(stale.RootId);
            // Every planned root holds its entries now: from here a save writes everything that was loaded.
            _loaded = true;
            MarkChanged();

            foreach (var root in background) StartScannedRoot(root, startup: !forceRescan);

            if (pending.Count > 0) SetState(IndexState.Scanning, "Scanning...");
            foreach (var root in pending)
            {
                ct.ThrowIfCancellationRequested();
                ScanRoot(root, ct);
            }
            if ((pending.Count > 0 || loadDatabase) && !ct.IsCancellationRequested) TrySave();
            _settled = true;
            if (Volatile.Read(ref _scans.Active) > 0 || background.Count > 0 && _state == IndexState.Loading)
                SetState(IndexState.Scanning, "Scanning...");
            SettleState();
            // Rescans replace whole roots, leaving the old copies as removed slots; and loading/scanning leaves
            // large temporary buffers behind. Reclaim both now that indexing has settled.
            CompactIfWorthwhile(releaseMemory: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex;
            SetState(IndexState.Error, "Indexing failed: " + ex.Message);
        }
    }

    /// <summary>Makes the indexer for one planned root (NTFS through the service or as administrator, else scanning).</summary>
    RootIndexer CreateRoot(PlannedRoot p)
    {
        if (p.Volume)
        {
            var source = OpenVolume(p.Name);
            if (source != null)
            {
                if (source is ServiceVolumeSource) _usesService = true;
                return new NtfsVolumeIndexer(this, p.Name, source) { DriveKind = p.Kind, Plan = p };
            }
            // A fixed drive scanned instead is what the "turn on instant indexing" bar is about; a USB drive that an
            // older service won't read is just scanned.
            if (!IsElevated && p.Kind == DriveKind.Fixed) _fallbackMode = true;
        }
        return new FolderIndexer(this, p.Name, p.Explicit) { DriveKind = p.Kind, IsNetwork = p.Network, Plan = p };
    }

    /// <summary>An NTFS volume's records: directly as administrator, else through the Rock Preview service (checked once).</summary>
    IVolumeSource? OpenVolume(string drive)
    {
        _serviceAvailable ??= !IsElevated && VolumeService.IsRunning();
        return IsElevated ? LocalVolumeSource.TryOpen(drive) : _serviceAvailable == true ? ServiceVolumeSource.TryConnect(drive, 3000) : null;
    }

    /// <summary>Adds a root under the save lock, so a compaction or save sees it with consistent ids.</summary>
    void AddRoot(RootIndexer root)
    {
        lock (_saveGate) lock (_roots) _roots.Add(root);
        WatchRemoval(root);
    }

    /// <summary>
    /// Scans one root. Never twice at once for the same root: a request that comes while it scans (a watcher overflow)
    /// makes the running scan go once more when it finishes.
    /// </summary>
    void ScanRoot(RootIndexer root, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref root.ScanBusy, 1, 0) != 0)
        {
            root.RescanPending = true;
            return;
        }
        var counter = _scans;
        Interlocked.Increment(ref counter.Active);
        root.ScanIdle.Reset();
        _scanningRoot = root;
        try { StateChanged?.Invoke(); } catch { }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, root.Lifetime.Token);
            ulong rules;
            do
            {
                root.RescanPending = false;
                rules = root.Filter.Stamp;
                long before = root.LastScanUtc;
                root.Scan(linked.Token);
                // A scan that completed (not one that found the root offline) built the entries with these rules.
                if (root.LastScanUtc != before) root.RulesStamp = rules;
                root.RetryAtTicks = 0;
                _unsaved = true;
            }
            // Again if asked meanwhile, or if the exclusions changed while it ran (changed, then changed back, say).
            while ((root.RescanPending || (root.Status == RootStatus.Ready && root.Filter.Stamp != rules)) && !linked.IsCancellationRequested);
            if (!linked.IsCancellationRequested) root.StartMonitoring();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { /* this root was dropped or released */ }
        catch (Exception ex)
        {
            LastError = ex;
            root.Detail = ex.Message;
            // Keep what it has (and its journal, if that still works), and try again in a while.
            root.RetryAtTicks = Environment.TickCount64 + RetryFailedScanMs;
            try { if (!root.Lifetime.IsCancellationRequested) root.StartMonitoring(); } catch { }
        }
        finally
        {
            if (_scanningRoot == root) _scanningRoot = null;
            Interlocked.Decrement(ref counter.Active);
            Volatile.Write(ref root.ScanBusy, 0);
            root.ScanIdle.Set();
            MarkChanged();
        }
    }

    // ---- background work per root ----

    /// <summary>
    /// Runs <paramref name="work"/> for one root on its own task, unless that root already has one; then a
    /// <paramref name="rescan"/> is remembered and run when the current task ends.
    /// </summary>
    bool RunRootTask(RootIndexer root, string? status, Action<CancellationToken> work, bool rescan = false)
    {
        // Not under _gate: the indexing worker gets here while Start or Rebuild may hold it, waiting for the worker.
        if (_disposed || root.Lifetime.IsCancellationRequested) return false;
        CancellationToken ct;
        try { ct = _cts.Token; }
        catch (ObjectDisposedException) { return false; }
        lock (_rescans)
        {
            if (ct.IsCancellationRequested) return false;
            if (_rescans.ContainsKey(root))
            {
                if (rescan) root.RescanPending = true;
                return false;
            }
            var task = Task.Run(() =>
            {
                try
                {
                    if (status != null) SetState(IndexState.Scanning, status);
                    work(ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { LastError = ex; }
                finally
                {
                    lock (_rescans)
                    {
                        if (_rescans.TryGetValue(root, out var t) && t.Id == Task.CurrentId) _rescans.Remove(root);
                    }
                    if (!ct.IsCancellationRequested && root.RescanPending && !root.Lifetime.IsCancellationRequested)
                        RunRootTask(root, $"Rescanning {root.RootPath}", c => ScanRoot(root, c), rescan: true);
                    else if (!ct.IsCancellationRequested) AfterRootWork();
                }
            });
            _rescans[root] = task;
        }
        return true;
    }

    /// <summary>When the last root task is done: Ready, then save what the scans found and reclaim memory.</summary>
    void AfterRootWork()
    {
        if (!_settled || Volatile.Read(ref _scans.Active) != 0) return;
        lock (_rescans) if (_rescans.Count > 0) return; // others still running (this one has left already)
        SettleState();
        try
        {
            CompactIfWorthwhile(releaseMemory: _releasePending);
            if (_unsaved) TrySave();
        }
        catch (Exception ex) { LastError = ex; }
    }

    /// <summary>
    /// A scanned root (network share, non-NTFS drive, added folder) at startup or when added. A network root scanned
    /// recently resumes its loaded entries after checking the share answers; anything else is (re)scanned, keeping
    /// what it had searchable meanwhile. Off the indexing thread, since an unreachable server can take long to answer.
    /// </summary>
    void StartScannedRoot(RootIndexer root, bool startup)
    {
        RunRootTask(root, null, ct =>
        {
            if (startup && root.IsNetwork && root is FolderIndexer f && f.ScannedWithin(RescanInterval(fallback: TimeSpan.FromHours(1)))
                && (root.RulesStamp == 0 || root.RulesStamp == root.Filter.Stamp) && f.ResumeLoaded(ct))
            {
                MarkChanged();
                return;
            }
            SetState(IndexState.Scanning, $"Scanning {root.RootPath}");
            ScanRoot(root, ct);
        });
    }

    TimeSpan RescanInterval(TimeSpan fallback) =>
        _settings.NetworkRescanMinutes > 0 ? TimeSpan.FromMinutes(_settings.NetworkRescanMinutes) : fallback;

    // ---- drives coming and going ----

    void OnDriveTick()
    {
        if (Interlocked.Exchange(ref _driveTickBusy, 1) != 0) return;
        try
        {
            if (!_settled || _disposed) return;
            if (Volatile.Read(ref _pendingApply) != null) ApplyPending();
            uint mask = LetterSource();
            long now = Environment.TickCount64;
            bool changed = mask != _planMask || _replanRequested;
            if (changed || now - _lastFullPlanTicks >= FullDriveCheckMs || AnyReleased())
            {
                _replanRequested = false;
                _planMask = mask;
                _lastFullPlanTicks = now;
                Reconcile(drivesChanged: changed);
            }
            ScheduleRootWork(now);
        }
        catch (Exception ex) { LastError = ex; }
        finally { Volatile.Write(ref _driveTickBusy, 0); }
    }

    bool AnyReleased()
    {
        lock (_roots) return _roots.Exists(r => r.Released && (r.EjectFailed || Environment.TickCount64 - r.ReleasedAtTicks > 60_000));
    }

    /// <summary>Brings the roots in line with the drives there are now: new ones are scanned, gone ones dropped.</summary>
    void Reconcile(bool drivesChanged)
    {
        var settings = _settings;
        var plan = IndexPlan.Plan(settings, DriveSource(), _filter);
        lock (_gate)
        {
            if (_disposed || !_settled || !ReferenceEquals(settings, _settings)) return;
            RootIndexer[] current;
            lock (_roots) current = [.. _roots];
            var drop = new List<RootIndexer>();
            var add = new List<PlannedRoot>();
            var revive = new List<RootIndexer>();
            long now = Environment.TickCount64;
            foreach (var r in current)
            {
                var p = plan.FirstOrDefault(x => string.Equals(x.Name, r.RootName, StringComparison.OrdinalIgnoreCase));
                if (p == null) { drop.Add(r); continue; } // its drive is gone, or Settings no longer wants it
                if (r.Plan != null && !SameShape(r.Plan, p)) { drop.Add(r); continue; } // reformatted, now a network share...
                if (r.Released)
                {
                    // Let go of for an eject or a volume lock: wait until Windows says it didn't happen (or a minute
                    // passes) before touching the drive again, or the new handles would block it. Then pick up where it
                    // was (a failed "chkdsk C: /f" lock must not mean reading all of C: again).
                    if (r.EjectFailed || now - r.ReleasedAtTicks > 60_000)
                    {
                        IVolumeSource? source = r is NtfsVolumeIndexer ? OpenVolume(r.RootName) : null;
                        if ((r is not NtfsVolumeIndexer || source != null) && r.Revive(source)) revive.Add(r);
                        else { source?.Dispose(); drop.Add(r); }
                    }
                }
            }
            foreach (var p in plan)
            {
                bool have = current.Any(r => !drop.Contains(r) && string.Equals(r.RootName, p.Name, StringComparison.OrdinalIgnoreCase));
                if (!have) add.Add(p);
            }
            // Drives changed: an offline folder (on a USB drive just plugged back in, say) is worth checking now.
            if (drivesChanged || drop.Count > 0 || add.Count > 0) lock (_lastProbe) _lastProbe.Clear();
            foreach (var r in revive)
            {
                UnwatchRemoval(r);
                WatchRemoval(r);
                if (r is NtfsVolumeIndexer)
                {
                    r.StartMonitoring(); // resumes the journal from where it stopped
                    // A rescan for changed exclusions that the release cut short (or that skipped it) is still owed.
                    if (r.RulesStamp != r.Filter.Stamp) ((IIndexerHost)this).RequestRescan(r, "exclusions changed");
                }
                else ((IIndexerHost)this).RequestRescan(r, "the eject didn't happen");
            }
            if (drop.Count == 0 && add.Count == 0) return;
            foreach (var r in drop) DropRoot(r);
            foreach (var p in add)
            {
                var root = CreateRoot(p);
                AddRoot(root);
                if (root is FolderIndexer) StartScannedRoot(root, startup: false);
                else RunRootTask(root, $"Scanning {root.RootPath} (new drive)", ct => ScanRoot(root, ct));
            }
            MarkChanged();
        }
    }

    static bool SameShape(PlannedRoot a, PlannedRoot b) => a with { Ready = b.Ready } == b;

    /// <summary>Stops a root and takes its entries out of the index (its drive went away or is no longer wanted).</summary>
    void DropRoot(RootIndexer root)
    {
        UnwatchRemoval(root);
        try { root.Lifetime.Cancel(); } catch (ObjectDisposedException) { }
        Task? running;
        lock (_rescans) _rescans.TryGetValue(root, out running);
        try { running?.Wait(RootTaskWait); } catch { }
        try { root.StopMonitoring(); } catch { }
        // The root stays in the list until its entries are gone, so a compaction meanwhile renumbers its ids too.
        lock (_saveGate)
        {
            lock (root.Sync)
            {
                if (root.RootId >= 0) Index.Remove(root.RootId);
            }
            lock (_roots) _roots.Remove(root);
        }
        lock (_lastProbe) _lastProbe.Remove(root);
        lock (_lastWatchAttempt) _lastWatchAttempt.Remove(root);
        try { root.Dispose(); } catch { }
    }

    /// <summary>
    /// Per scanned root: a quiet check whether an offline one is back, a retry of a scan that failed, and for network
    /// roots a scheduled rescan when due.
    /// </summary>
    void ScheduleRootWork(long now)
    {
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots.Where(r => !r.Released)];
        foreach (var root in roots)
        {
            EnsureRemovalWatch(root, now);
            lock (_rescans) if (_rescans.ContainsKey(root)) continue;
            if (root is FolderIndexer due && due.WatcherDue)
                ((IIndexerHost)this).RequestRescan(root, "watching for changes again"); // changes went unseen meanwhile
            else if (root.RetryAtTicks != 0 && now >= root.RetryAtTicks)
            {
                root.RetryAtTicks = 0;
                RunRootTask(root, $"Rescanning {root.RootPath} (trying again)", ct => ScanRoot(root, ct), rescan: true);
            }
            else if (root.Status == RootStatus.Offline && root is FolderIndexer folder)
            {
                lock (_lastProbe)
                {
                    if (_lastProbe.TryGetValue(root, out long last) && now - last < OfflineProbeMs) continue;
                    _lastProbe[root] = now;
                }
                RunRootTask(root, null, ct =>
                {
                    if (!folder.IsReachable(ct) || ct.IsCancellationRequested) return;
                    SetState(IndexState.Scanning, $"Rescanning {root.RootPath} (back online)");
                    ScanRoot(root, ct);
                });
            }
            else if (root.IsNetwork && root.Status == RootStatus.Ready && _settings.NetworkRescanMinutes > 0 && root.LastScanUtc > 0)
            {
                var since = DateTime.UtcNow - DateTime.FromFileTimeUtc(root.LastScanUtc);
                // A scan time in the future (the clock was wrong, then corrected) counts as due.
                if (since < TimeSpan.Zero || since >= TimeSpan.FromMinutes(_settings.NetworkRescanMinutes))
                    RunRootTask(root, $"Rescanning {root.RootPath} (checking for changes)", ct => ScanRoot(root, ct), rescan: true);
            }
        }
    }

    /// <summary>For drives that can be ejected: close every handle when Windows asks, so Safely Remove works.</summary>
    void WatchRemoval(RootIndexer root)
    {
        // Any local root, folders added from Settings too: a handle anywhere on a drive blocks its eject, and USB hard
        // disks report as fixed.
        if (_devices == null || root.IsNetwork) return;
        var watch = _devices.WatchRemoval(root.RootPath,
            release: () =>
            {
                root.ReleasedAtTicks = Environment.TickCount64;
                try { root.Lifetime.Cancel(); } catch (ObjectDisposedException) { }
                // A scan in progress closes its find handles once it sees the cancel; give it a moment to.
                root.ScanIdle.Wait(5000);
                root.Release();
            },
            ejectFailed: () =>
            {
                root.EjectFailed = true;
                _replanRequested = true;
            });
        if (watch == null) return;
        lock (_removalWatches) _removalWatches[root] = watch;
    }

    /// <summary>
    /// A local root whose eject watch couldn't be set up (its card reader was empty, its BitLocker drive locked) gets
    /// one now that it answers, at most every 30 seconds.
    /// </summary>
    void EnsureRemovalWatch(RootIndexer root, long now)
    {
        if (_devices == null || root.IsNetwork || root.Released || root.Status != RootStatus.Ready) return;
        lock (_removalWatches) if (_removalWatches.ContainsKey(root)) return;
        lock (_lastWatchAttempt)
        {
            if (_lastWatchAttempt.TryGetValue(root, out long last) && now - last < 30_000) return;
            _lastWatchAttempt[root] = now;
        }
        WatchRemoval(root);
    }

    void UnwatchRemoval(RootIndexer root)
    {
        IDisposable? watch;
        lock (_removalWatches)
        {
            if (!_removalWatches.Remove(root, out watch)) return;
        }
        try { watch.Dispose(); } catch { }
    }

    // ---- what Settings shows ----

    public IReadOnlyList<IndexedRootInfo> GetRoots()
    {
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        var counts = Index.CountByRoot();
        var list = new List<IndexedRootInfo>(roots.Length);
        foreach (var r in roots)
        {
            int items = r.RootId >= 0 ? counts.GetValueOrDefault(r.RootId) : 0;
            list.Add(new IndexedRootInfo(r.RootPath, r.DriveKind, r.IsNetwork, r.Method, r.Status, items, r.Detail));
        }
        return list;
    }

    // ---- IIndexerHost ----

    void IIndexerHost.MarkChanged() => MarkChanged();

    void MarkChanged() => Interlocked.Exchange(ref _changePending, 1);

    void IIndexerHost.ReportProgress(RootIndexer root, long objects) { }

    void IIndexerHost.RequestRescan(RootIndexer root, string reason)
    {
        if (root.Lifetime.IsCancellationRequested) return;
        if (Volatile.Read(ref root.ScanBusy) != 0) { root.RescanPending = true; return; }
        RunRootTask(root, $"Rescanning {root.RootPath} ({reason})", ct => ScanRoot(root, ct), rescan: true);
    }

    void OnTick()
    {
        if (Interlocked.Exchange(ref _changePending, 0) != 0)
        {
            try { Index.RaiseChanged(); } catch { }
        }
        if (_state is IndexState.Scanning or IndexState.Loading)
        {
            try { StateChanged?.Invoke(); } catch { }
        }
    }

    // ---- memory ----

    /// <summary>
    /// Renumbers the index without its removed slots once they make up a meaningful share, translating every
    /// root's stored ids under the same locks <see cref="Save"/> uses. Skipped while any scan is running.
    /// </summary>
    void CompactIfWorthwhile(bool releaseMemory)
    {
        if (Volatile.Read(ref _scans.Active) != 0)
        {
            _releasePending |= releaseMemory; // do it when the scans are done
            return;
        }
        int dead = Index.DeadCount;
        if (dead > 50_000 && dead > Index.Count / 16)
        {
            lock (_saveGate)
            {
                // Under the save lock: roots are added and dropped under it too, so this list matches the ids.
                RootIndexer[] roots;
                lock (_roots) roots = [.. _roots];
                var taken = new List<object>();
                try
                {
                    foreach (var r in roots) { Monitor.Enter(r.Sync); taken.Add(r.Sync); }
                    if (Volatile.Read(ref _scans.Active) == 0)
                    {
                        var remap = Index.Compact();
                        foreach (var r in roots) r.Remap(remap);
                    }
                }
                finally
                {
                    for (int i = taken.Count - 1; i >= 0; i--) Monitor.Exit(taken[i]);
                }
            }
            releaseMemory = true;
        }
        if (releaseMemory)
        {
            _releasePending = false;
            ReleaseMemory();
        }
    }

    /// <summary>Full blocking GC that also compacts the large object heap and decommits free memory.</summary>
    static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // ---- persistence ----

    public void Save()
    {
        if (!_loaded) return; // never overwrite a database we have not read yet
        lock (_saveGate)
        {
            if (!_loaded) return; // stopped meanwhile: the roots may be gone already
            RootIndexer[] roots;
            lock (_roots) roots = [.. _roots];
            // Lock order: every root's Sync, then the index lock (taken inside IndexDatabase.Save).
            var taken = new List<object>();
            try
            {
                foreach (var r in roots) { Monitor.Enter(r.Sync); taken.Add(r.Sync); }
                int count = Index.Count;
                var infos = new List<DbRoot>(roots.Length);
                foreach (var r in roots)
                {
                    if (r.RootId < 0 || !Index.IsAlive(r.RootId)) continue;
                    var (jid, usn) = r.JournalPosition;
                    infos.Add(new DbRoot
                    {
                        Path = r.RootName,
                        Kind = r.Kind,
                        JournalId = jid,
                        NextUsn = usn,
                        RulesStamp = r.RulesStamp,
                        RootId = r.RootId,
                        IdToFrn = r.Kind == RootKind.Ntfs ? r.BuildIdToFrn(count) : null,
                    });
                }
                _unsaved = false;
                IndexDatabase.Save(_settings.DatabasePath, Index, infos);
            }
            finally
            {
                for (int i = taken.Count - 1; i >= 0; i--) Monitor.Exit(taken[i]);
            }
        }
    }

    void TrySave()
    {
        try { Save(); } catch (Exception ex) { LastError = ex; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settled = false;
            _cts.Cancel();
            try { _worker?.Wait(TimeSpan.FromSeconds(30)); } catch { }
            WaitForRootTasks();
            RootIndexer[] roots;
            lock (_roots) roots = [.. _roots];
            foreach (var r in roots) { try { r.Lifetime.Cancel(); } catch (ObjectDisposedException) { } }
            foreach (var r in roots) { try { r.StopMonitoring(); } catch { } }
            TrySave();
            foreach (var r in roots) { UnwatchRemoval(r); try { r.Dispose(); } catch { } }
            lock (_saveGate)
            {
                _loaded = false;
                lock (_roots) _roots.Clear();
            }
        }
        _driveTick.Dispose();
        _tick.Dispose();
        try { _devices?.Dispose(); } catch { }
        _cts.Dispose();
    }

    /// <summary>Test hook: applies queued folder-watcher events immediately.</summary>
    internal void FlushPendingChanges()
    {
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        foreach (var r in roots) if (r is FolderIndexer f) f.ProcessPending();
    }

    /// <summary>Test hook: looks at the drives now instead of on the next tick.</summary>
    internal void CheckDrivesNow()
    {
        _replanRequested = true;
        OnDriveTick();
    }

    /// <summary>Test hook: waits until no root task is running.</summary>
    internal bool WaitForBackgroundWork(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Task[] running;
            lock (_rescans) running = [.. _rescans.Values];
            if (running.Length == 0 && (_worker?.IsCompleted ?? true)) return true;
            try { Task.WaitAll(running, 100); } catch { }
            Thread.Sleep(10);
        }
        return false;
    }
}
