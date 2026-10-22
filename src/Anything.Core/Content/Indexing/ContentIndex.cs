using Anything.Core.Content.Extract;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// The facade: ContentSource for searches (FileIndex.Content) and IContentIndex for the app. It owns the follower while
/// a mode is on, attaches it to the name index (Changed, Compacted and StateChanged handlers that are O(1) and never
/// block), and turns the follower's progress into throttled events. Every entry point copies the settings before it
/// returns; lifecycle changes (Start, Apply) run in order on a background chain and are ignored after Stop.
/// </summary>
internal sealed class ContentIndex : ContentSource, IContentIndex, IFollowerHost
{
    readonly Func<IContentStore> _openStore;
    readonly IContentSource _source;
    readonly IRootGate? _givenGate;
    readonly IPowerPolicy _power;
    readonly ContentIndexOptions _options;
    readonly DocSetCache _cache = new();
    readonly OnDemandMap _onDemand = new();
    readonly object _gate = new();
    readonly Timer _statusTimer, _contentTimer;
    IIndexManager? _names;
    FileIndex? _index;
    ContentFollower? _follower;
    IRootGate? _rootGate;
    ContentSettings _settings = new();
    // The last status of a follower that gave up (errors in a row), shown until a new one starts.
    ContentStatus? _failedStatus;
    bool _started, _stopped, _deleting, _keptKnown;
    KeptIndex? _kept;
    Task _control = Task.CompletedTask;
    long _lastStatusRaise = -1_000_000;
    int _statusArmed, _contentArmed, _prefetchRaising;
    (long At, ulong Fingerprint, ContentCensus Census)? _census;

    /// <summary>An index over the store openStore makes, reading through source.</summary>
    public ContentIndex(Func<IContentStore> openStore, IContentSource source, IRootGate? roots, IPowerPolicy power, ContentIndexOptions options)
    {
        _openStore = openStore;
        _source = source;
        _givenGate = roots;
        _power = power;
        _options = options;
        _statusTimer = new Timer(_ => { Volatile.Write(ref _statusArmed, 0); RaiseStatus(); });
        _contentTimer = new Timer(_ => { Volatile.Write(ref _contentArmed, 0); Raise(ContentChanged); });
    }

    /// <summary>The follower while a mode is on (tests drive it with RunOnce when ManualLoop).</summary>
    internal ContentFollower? Follower => Volatile.Read(ref _follower);

    /// <summary>Lifecycle changes still running (tests wait for it).</summary>
    internal Task Settled => _control;

    // ───────────────────────────── ContentSource ─────────────────────────────

    internal override ContentMode Mode => Follower?.Settings.Mode ?? ContentMode.NamesOnly;

    internal override ContentView? AcquireView() => Follower is { } f ? Acquire(ref f.Published) : null;

    internal override DocIdMap? MapFor(FileIndex index, ContentView view) => MapFor(index, view, RootAliases.None);

    internal override DocIdMap? MapFor(FileIndex index, ContentView view, RootAliases aliases)
    {
        if (ReferenceEquals(index, _index))
        {
            long generation = index.Generation;
            if (view.Map.Generation == generation) return view.Map;
            // A newer view may already be translated (Compact) or re-mapped: its map is keyed by segment id, so documents
            // of segments merged away are simply not found.
            if (Follower is { } f && Volatile.Read(ref f.Published) is { } current && current.Map.Generation == generation) return current.Map;
            return null;
        }
        // Another index (a folder listing, through ShareWith): ids mapped on demand, per generation. Its folders are placed
        // through the follower's snapshot of the roots only (VolumeKeys would call Win32 under the listing's read lock):
        // under the first of their names a root holds (a SUBST letter's listing under the folder it shows, a mapped letter
        // under itself or its share), and folder by folder below a listed folder that no root holds but one is in under
        // any of its names (a listing of E: while E:\Data is the root).
        var roots = Follower?.Roots ?? RootSnapshot.Empty;
        if (aliases.Count == 0) return _onDemand.For(index, view, roots.Resolve, roots.HoldsRootBelow, aliases);
        return _onDemand.For(index, view, path => aliases.Resolve(path, roots.Resolve), path => aliases.HoldsRootBelow(path, roots.HoldsRootBelow), aliases);
    }

    internal override bool Holds(string fullPath) => Follower is { } f && f.Roots.Resolve(fullPath) != null;

    internal override DocSetCache Cache => _cache;

    internal override void PrefetchCompleted()
    {
        // At once, coalesced (not on the commit debounce).
        if (Interlocked.Exchange(ref _prefetchRaising, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Volatile.Write(ref _prefetchRaising, 0);
            Raise(ContentChanged);
        });
    }

    /// <summary>A reconcile is pending or running, or the index moved past the published map (a Clear the follower hasn't seen yet).</summary>
    internal override bool CatchingUp
    {
        get
        {
            if (Follower is not { } f) return false;
            if (f.CatchingUp) return true;
            return Volatile.Read(ref f.Published) is { } view && _index != null && view.Map.Generation != _index.Generation;
        }
    }

    // ───────────────────────────── IContentIndex ─────────────────────────────

    public ContentStatus Status
    {
        get
        {
            if (Follower is { } f) return f.Status;
            if (Volatile.Read(ref _deleting)) return ContentStatus.Off with { Reason = "finishing deleting…" };
            // A follower that gave up: its last status (Stopped, with the reason) until a new one starts.
            if (Volatile.Read(ref _failedStatus) is { } failed) return failed;
            return ContentStatus.Off;
        }
    }

    public event Action? StatusChanged;
    public event Action? ContentChanged;

    public KeptIndex? Kept
    {
        get
        {
            ContentSettings settings;
            lock (_gate)
            {
                if (!_started || _stopped) return null;
                settings = _settings;
                if (settings.ModeExplicit || settings.Mode != ContentMode.NamesOnly) return null;
                if (_keptKnown) return _kept;
            }
            var kept = FindKept();
            lock (_gate)
            {
                _kept = kept;
                _keptKnown = true;
            }
            return kept;
        }
    }

    KeptIndex? FindKept()
    {
        if (_options.Folder is not { } folder || _options.OpenReadOnly is not { } open) return null;
        try
        {
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "manifest*").Any()) return null;
            using var store = open();
            using var view = store.Acquire();
            if (view.Segments.Count == 0) return null;
            var mode = store.RecordedMode == ContentMode.NamesOnly ? ContentMode.Documents : store.RecordedMode;
            return new KeptIndex(mode, store.BytesOnDisk);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public void Start(IIndexManager names, ContentSettings settings)
    {
        var s = settings.Clone();
        lock (_gate)
        {
            if (_started || _stopped) return;
            _started = true;
            _names = names;
            _index = names.Index;
            _settings = s;
        }
        Control(() => Transition(s));
    }

    public void Apply(ContentSettings settings)
    {
        var s = settings.Clone();
        lock (_gate)
        {
            if (!_started || _stopped) return;
            _settings = s;
            _keptKnown = false;
        }
        Control(() => Transition(s));
    }

    public void Pause(TimeSpan? forHowLong)
    {
        // Kept here too: a follower started later (names only and back, a restart after it gave up, a Pause while none
        // ran) is paused for what is left of it.
        var now = UtcNow();
        lock (_gate) _pausedUntil = forHowLong is { } d && d < DateTime.MaxValue - now ? now + d : DateTime.MaxValue;
        Follower?.Post(new Command(CommandKind.Pause) { Pause = forHowLong });
    }

    public void Resume()
    {
        lock (_gate) _pausedUntil = null;
        Follower?.Post(new Command(CommandKind.Resume));
    }

    /// <summary>The user's pause: until when (UTC; MaxValue = until Resume or restart), null when none.</summary>
    DateTime? _pausedUntil;

    DateTime UtcNow() => _options.Clock?.Invoke() ?? DateTime.UtcNow;

    /// <summary>The user's pause for a new follower (what is left of it), or null when there is none or it ran out. Under _gate.</summary>
    Command? PauseForNewFollower()
    {
        if (_pausedUntil is not { } until) return null;
        if (until == DateTime.MaxValue) return new Command(CommandKind.Pause) { Pause = null };
        var left = until - UtcNow();
        if (left <= TimeSpan.Zero) { _pausedUntil = null; return null; }
        return new Command(CommandKind.Pause) { Pause = left };
    }

    public void Rebuild() => PostOrRestart(new Command(CommandKind.Rebuild));

    public void RetryProblems() => PostOrRestart(new Command(CommandKind.RetryProblems));

    /// <summary>
    /// A command for the follower; when it gave up (or is giving up), a new follower is started first (it opens the store
    /// again) and the command goes to that one: the user's way out after "stopped after an error".
    /// </summary>
    void PostOrRestart(Command command)
    {
        if (Follower is { } f && f.Post(command)) return;
        ContentSettings s;
        lock (_gate)
        {
            if (!_started || _stopped) return;
            s = _settings;
        }
        if (s.Mode == ContentMode.NamesOnly) return;
        Control(() =>
        {
            Transition(s);
            Follower?.Post(command);
        });
    }

    public IReadOnlyDictionary<DocStatus, int> CountsByStatus()
    {
        var counts = new Dictionary<DocStatus, int>();
        if (Follower is { } f)
            foreach (var (status, n) in f.CountsByStatus)
                if (status != DocStatus.Ok && n > 0) counts[status] = n;
        return counts;
    }

    public ContentSnippet? GetSnippet(string fullPath, ContentHighlighter highlighter)
    {
        if (Follower is not { } f || !f.Roots.TryKeyOf(fullPath, out ulong key, out string volumeKey, out string rel, out string display)) return null;
        // A folder just taken out of the scope: its documents' deletes may still wait for the next commit (or never be
        // written, on a read-only store), but its text is never shown again (rows that match by name, the preview, export).
        if (!f.Scope.InScope(volumeKey, rel, display, out _)) return null;
        using var view = AcquireView();
        if (view == null) return null;
        try
        {
            if (view.Store.Find(key) is not { } doc) return null;
            var seg = view.Store.Segments[doc.Segment];
            var row = seg.Row(doc.Doc);
            if (row.Status is not (DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText)) return null;
            if (seg.ReadStored(doc.Doc) is not { } stored) return null;
            PictureInfo? picture = seg.TryGetPicture(doc.Doc, out var info) ? info : null;
            var ext = ContentTypes.ExtensionOf(Path.GetFileName(fullPath.AsSpan()));
            return SnippetFormatter.Format(stored, row.Kind, ext, row.Pages, picture, highlighter);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public DocStatus? GetStatus(string fullPath)
    {
        if (Follower is not { } f || !f.Roots.TryKeyOf(fullPath, out ulong key, out string volumeKey, out string rel, out string display)) return null;
        if (!f.Scope.InScope(volumeKey, rel, display, out _)) return null;
        using var view = AcquireView();
        if (view == null) return null;
        try
        {
            if (view.Store.Find(key) is not { } doc) return null;
            var status = view.Store.Segments[doc.Segment].Row(doc.Doc).Status;
            // NoText was read (a scan, a picture, a layout with only its details): nothing left out, no problem, so no
            // reason to show in a row that matched by name.
            return status is DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText ? null : status;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public Task<IReadOnlyList<PageBox>> LocateOnPdfPageAsync(string fullPath, int pageIndex, ContentHighlighter highlighter, CancellationToken ct)
    {
        IReadOnlyList<PageBox> none = [];
        if (Follower == null || ReadsNothing) return Task.FromResult(none);
        return Task.Run(() => OnDemand(fullPath, () =>
        {
            var budget = new ExtractBudget(ExtractLimits.Local, ct);
            FileByteSource.TryOpen(fullPath, budget, out var source, out _);
            if (source == null) return none;
            using (source) return PdfPageText.FindBoxes(source, pageIndex, s => highlighter.Find(s), budget);
        }, none), ct);
    }

    public Task<(string Text, int FirstLine)?> LoadTextWindowAsync(string fullPath, int line, int maxBytes, CancellationToken ct)
    {
        if (Follower == null || ReadsNothing) return Task.FromResult<(string, int)?>(null);
        return Task.Run(() => OnDemand(fullPath, () => TextWindow.LoadAsync(fullPath, line, maxBytes, ct).GetAwaiter().GetResult(), null), ct);
    }

    /// <summary>
    /// Running as administrator reads nothing, as the follower doesn't: the users' files are never parsed with that token
    /// (estimates use the priors and the index's statistics, page boxes and text windows fall back to the plain preview).
    /// </summary>
    bool ReadsNothing => !_options.IgnoreElevation && _names?.IsElevated == true;

    /// <summary>
    /// An on-demand read (page boxes, a text window) inside a crash-marker slot of its own: a file that took the app down
    /// that way once is never read on demand again (the caller falls back to the banner). Nothing is read while elevated,
    /// nor without crash protection (an index on disk whose marker another copy holds).
    /// </summary>
    T OnDemand<T>(string fullPath, Func<T> read, T fallback)
    {
        if (ReadsNothing) return fallback;
        var f = Follower;
        var marker = f?.Marker;
        ulong key = f != null && f.Roots.TryKeyOf(fullPath, out ulong k, out _, out _, out _) ? k : ContentKeys.Of("path:", fullPath.ToUpperInvariant());
        if (marker == null || marker.IsDisposed || !marker.HasFile && _options.Folder != null
            || marker.Carried.Any(e => e.Key == key && e.Lane == MarkerLane.OnDemand)) return fallback;
        // No free slot (or the marker closed with its follower): nothing is read without crash protection.
        int slot = marker.Acquire();
        if (slot < 0) return fallback;
        uint epoch = marker.Enter(slot, new MarkerEntry(key, 0, MarkerLane.OnDemand, 0, 0));
        try
        {
            using var quiet = Anything.Core.Indexing.Drives.QuietErrors();
            return read();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { return fallback; }
        finally
        {
            marker.Leave(slot, epoch);
            marker.Release(slot);
        }
    }

    public Task<ContentEstimate> EstimateAsync(ContentSettings settings, CancellationToken ct) => Estimate(settings, sample: true, ct);

    public Task<ContentEstimate> EstimateWithoutReadingAsync(ContentSettings settings, CancellationToken ct) => Estimate(settings, sample: false, ct);

    Task<ContentEstimate> Estimate(ContentSettings settings, bool sample, CancellationToken ct)
    {
        var s = settings.Clone();
        var index = _index;
        var follower = Follower;
        return Task.Run(() =>
        {
            // The census holds the name index's read lock (in slices) at normal priority: a writer waiting behind a
            // background-priority holder would keep every search waiting too. Only the calibration runs in background mode.
            var census = CensusOf(index, s, ct);
            var calibration = CalibrationFor(census, follower, ct, sample);
            return ContentEstimator.Estimate(census, calibration, s.Mode, s);
        }, ct);
    }

    public Task<ContentCalibration> CalibrateAsync(ContentCensus census, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(census);
        var follower = Follower;
        return Task.Run(() => CalibrationFor(census, follower, ct), ct);
    }

    ContentCensus CensusOf(FileIndex? index, ContentSettings settings, CancellationToken ct)
    {
        if (index == null) return ContentEstimator.EmptyCensus(complete: false);
        // The same census serves every choice (it counts every kind); cached for 60 s.
        ulong fingerprint = ContentEstimator.ScopeFingerprint(settings);
        var cached = _census;
        long now = Environment.TickCount64;
        if (cached is { } c && c.Fingerprint == fingerprint && now - c.At < 60_000) return c.Census;
        // Taken before counting: a name index still loading or scanning (a share added a moment ago, a rescan) holds only
        // part of what it will, so the counts are "at least" ones, and they aren't kept (the next estimate counts again).
        bool namesReady = _names?.State == IndexState.Ready;
        var census = ContentEstimator.CountIndex(index, settings, _options.AllRootsInScope, ct);
        if (!namesReady) return census with { Complete = false };
        _census = (now, fingerprint, census);
        return census;
    }

    /// <summary>The calibration a census is priced with; with <paramref name="sample"/> false no file is read (statistics and priors only).</summary>
    ContentCalibration CalibrationFor(ContentCensus census, ContentFollower? follower, CancellationToken ct, bool sample = true)
    {
        // Kinds the index already holds 200 or more documents of: its own statistics; the rest a short real sample.
        IReadOnlyDictionary<ContentKind, KindStats> stats = new Dictionary<ContentKind, KindStats>();
        using (var view = AcquireView()) if (view != null) stats = view.Store.Stats;
        // Never a file the deny-list protects, whatever the census handed over.
        var sampleCensus = census with
        {
            Samples = census.Samples.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value.Where(path => !ContentTypes.IsDeniedFile(Path.GetFileName(path.AsSpan())))]),
        };
        if (follower != null)
        {
            // Settings' calibration never reads a file recorded Crashed, TimedOut or Damaged.
            var roots = follower.Roots;
            using var view = AcquireView();
            if (view != null)
            {
                var samples = new Dictionary<ContentKind, IReadOnlyList<string>>();
                foreach (var (kind, paths) in sampleCensus.Samples)
                    samples[kind] = [.. paths.Where(p => !roots.TryKeyOf(p, out ulong key, out _, out _, out _) || view.Store.Find(key) is not { } d
                        || view.Store.Segments[d.Segment].Row(d.Doc).Status is not (DocStatus.Crashed or DocStatus.TimedOut or DocStatus.Damaged))];
                sampleCensus = sampleCensus with { Samples = samples };
            }
        }
        // Running as administrator reads nothing (ReadsNothing): the figures come from the priors and the index's own statistics.
        bool needSample = sample && !ReadsNothing && census.Kinds.Any(p => p.Value.Files > 0 && stats.GetValueOrDefault(p.Key).Docs < ContentEstimator.IndexStatsMinimum);
        var measured = ContentEstimator.Priors;
        if (needSample)
        {
            // In the index's own crash marker (an on-demand slot, keyed by document key): a sample that takes the app down
            // is recorded Crashed by the index at the next start and never read again, by the index or here. An index in
            // memory (the demo) uses a marker of its own with no file: never Setup's file under %TEMP%.
            var marker = CalibrationMarker(follower?.Marker, inMemory: _options.Folder == null);
            var roots = follower?.Roots;
            Func<string, ulong>? keyOf = roots == null ? null
                : p => roots.TryKeyOf(p, out ulong key, out _, out _, out _) ? key : ContentKeys.Of("path:", p.ToUpperInvariant());
            bool background = ContentNative.EnterBackgroundMode();
            try
            {
                measured = ContentEstimator.Calibrate(sampleCensus, _source, TimeSpan.FromSeconds(3), maxNetworkSamples: 10, ct,
                    marker: marker, keyOf: keyOf);
            }
            finally { if (background) ContentNative.LeaveBackgroundMode(); }
        }
        return ContentEstimator.WithIndexStats(measured, stats);
    }

    /// <summary>
    /// The crash marker the calibration reads in: the index's own when it has a file; for an index in memory (the demo) one
    /// of its own with no file; otherwise null, and Calibrate falls back to its file under %TEMP% (an on-disk index whose
    /// marker is gone). The demo must never touch that file, which Setup's calibration uses.
    /// </summary>
    internal static CrashMarker? CalibrationMarker(CrashMarker? own, bool inMemory)
        => own is { IsDisposed: false, HasFile: true } ? own : inMemory ? CrashMarker.InMemory() : null;

    /// <summary>
    /// The session may end (SessionEnding): the crash marker's slots are zeroed at once on this thread (a logoff that goes
    /// ahead must never blame the file a reader is in, whatever the follower is busy with), then the follower commits what
    /// fits within the wait. Reading goes on: the logoff can still be cancelled.
    /// </summary>
    public void Checkpoint(TimeSpan wait)
    {
        if (Follower is not { } f) return;
        f.Marker?.ClearLive();
        using var done = new ManualResetEventSlim(false);
        f.Post(new Command(CommandKind.Checkpoint) { Deadline = f.NowMs + (long)wait.TotalMilliseconds, Done = done });
        if (!_options.ManualLoop) done.Wait(wait);
    }

    public void Stop(TimeSpan wait)
    {
        ContentFollower? follower;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            follower = _follower;
        }
        Detach();
        follower?.Stop(wait);
        _cache.Clear();
        (_rootGate as IDisposable)?.Dispose();
        _statusTimer.Dispose();
        _contentTimer.Dispose();
    }

    public void Dispose() => Stop(TimeSpan.FromMilliseconds(800));

    // ───────────────────────────── lifecycle ─────────────────────────────

    void Control(Action action)
    {
        if (_options.ManualLoop) { action(); return; }
        lock (_gate)
            _control = _control.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    void Transition(ContentSettings s)
    {
        if (s.Mode == ContentMode.NamesOnly)
        {
            ContentFollower? old;
            IRootGate? oldGate;
            lock (_gate)
            {
                if (_stopped) return;
                old = _follower;
                _follower = null;
                oldGate = _rootGate;
                _rootGate = null;
                _failedStatus = null;
            }
            if (old != null)
            {
                Detach();
                old.Stop(TimeSpan.FromSeconds(2));
            }
            (oldGate as IDisposable)?.Dispose();
            // Searches' cached sets and lookups hold views of the old store: dropped, so its files can go.
            _cache.Clear();
            // Only an explicit choice deletes; an unset mode (an older build dropped the key) keeps the index, unfollowed.
            if (s.ModeExplicit) DeleteOnDisk();
            RaiseStatus();
            return;
        }
        ContentFollower? follower, dead = null;
        IRootGate? deadGate = null;
        lock (_gate)
        {
            if (_stopped || _names == null) return;
            follower = _follower;
            if (follower is { Stopping: true })
            {
                // It gave up (its Stopped call may not have come yet): let go of it; a new one starts below.
                dead = follower;
                deadGate = _rootGate;
                _follower = null;
                _rootGate = null;
                follower = null;
            }
        }
        if (dead != null)
        {
            Detach();
            (deadGate as IDisposable)?.Dispose();
            _cache.Clear();
            // It may still be committing its last batch with the store's writer lock and the crash marker in hand: the new
            // one waits (a while) for them, rather than opening both read-only and reading nothing.
            dead.WaitForExit(TimeSpan.FromSeconds(5));
        }
        if (follower != null)
        {
            follower.Post(new Command(CommandKind.Apply) { Settings = s });
            return;
        }
        var names = _names!;
        var gate = _givenGate ?? (_options.AllRootsInScope ? new AllRootsReadyGate(names) : new RootGate(names));
        follower = new ContentFollower(names, s, _openStore, _source, gate, _power, _options, this);
        Command? pause;
        lock (_gate)
        {
            if (_stopped) { (gate as IDisposable)?.Dispose(); return; }
            _follower = follower;
            _rootGate = gate;
            _failedStatus = null;
            pause = PauseForNewFollower();
        }
        // Before its first iteration (a Pause needs no open store): nothing is read while the user's pause lasts.
        if (pause != null) follower.Post(pause);
        if (gate is RootGate rg) rg.Changed += follower.Wake;
        Attach();
        if (!_options.ManualLoop) follower.Start();
    }

    void Attach()
    {
        var index = _index!;
        index.Content = this;
        index.Changed += OnChanged;
        index.Compacted += OnCompacted;
        _names!.StateChanged += OnChanged;
    }

    /// <summary>Handlers go before anything they touch is disposed; after this they are no-ops.</summary>
    void Detach()
    {
        var index = _index;
        if (index == null) return;
        index.Changed -= OnChanged;
        index.Compacted -= OnCompacted;
        if (_names != null) _names.StateChanged -= OnChanged;
        if (ReferenceEquals(index.Content, this)) index.Content = null;
    }

    /// <summary>
    /// Names only, chosen: the text index's files are deleted, and only those. The folder is "Content" beside the name
    /// database, wherever database_path moved it, so it may be a folder of the user's own: one that holds no text index is
    /// never opened (a store would lock and write it), and the folder itself goes only once nothing else is left in it.
    /// </summary>
    void DeleteOnDisk()
    {
        if (_options.Folder is not { } folder || !ContentStore.HoldsIndex(folder)) return;
        Volatile.Write(ref _deleting, true);
        RaiseStatus();
        try
        {
            // Retried for a while: a mapped segment a search still holds is deleted once it is released.
            for (int attempt = 0; attempt < 120; attempt++)
            {
                try
                {
                    // Through a store while a manifest or segments are left (it waits for searches' leases); once it is
                    // closed (its writer lock went with it), what is left of it goes, nothing else.
                    bool done = !ContentStore.HoldsIndex(folder);
                    if (!done) using (var store = _openStore()) done = store.DeleteAll();
                    if (done && ContentStore.DeleteIndexFiles(folder)) return;
                }
                catch (Exception e) when (e is not OutOfMemoryException) { }
                lock (_gate) if (_stopped) return;
                Thread.Sleep(attempt < 5 ? 500 : 5000);
            }
        }
        finally
        {
            Volatile.Write(ref _deleting, false);
            lock (_gate) { _keptKnown = false; }
            RaiseStatus();
        }
    }

    void OnChanged() => Follower?.MarkDirty();

    /// <summary>
    /// Inside Compact's write lock: queues the remap for the follower and installs the published view with its map
    /// translated (a pure function of the immutable map, swapped by CAS); never waits for anything. On any failure the
    /// map simply stays stale (MapFor answers null) until the follower catches up.
    /// </summary>
    void OnCompacted(int[] remap, long newGeneration)
    {
        if (Follower is not { } f) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timed = new Timed(this, started);
        try
        {
            f.Compacted(remap, newGeneration);
            ref var slot = ref f.Published;
            var current = Volatile.Read(ref slot);
            if (current == null || current.Map.Generation != newGeneration - 1) return;
            var map = CompactRemap.Translate(current.Map, remap, newGeneration);
            if (!current.Store.TryAddRef()) return;
            var translated = new ContentView(current.Store, map, current.Mode);
            if (Interlocked.CompareExchange(ref slot, translated, current) == current) current.Dispose();
            else translated.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    /// <summary>How long the last Compacted handler held Compact's write lock, in milliseconds (tests).</summary>
    internal double LastCompactedMs { get; private set; }

    readonly struct Timed(ContentIndex owner, long started) : IDisposable
    {
        public void Dispose() => owner.LastCompactedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // ───────────────────────────── IFollowerHost ─────────────────────────────

    void IFollowerHost.StatusMaybeChanged() => RaiseStatusThrottled();

    void IFollowerHost.Stopped(ContentFollower follower)
    {
        // The follower gave up (errors in a row): its handlers come off the name index (a Compact would otherwise queue its
        // whole remap for a thread that is gone), the gate stops listening, and it is let go: its status stays, with the
        // reason, until the next Apply, Rebuild or Read problem files again starts a new one (which opens the store again).
        IRootGate? gate;
        lock (_gate)
        {
            if (_stopped || !ReferenceEquals(_follower, follower)) return;
            gate = _rootGate;
            _rootGate = null;
            // The status first: Status reads the follower without the lock, then this.
            Volatile.Write(ref _failedStatus, follower.Status);
            Volatile.Write(ref _follower, null);
        }
        Detach();
        (gate as IDisposable)?.Dispose();
        _cache.Clear();
        RaiseStatus();
    }

    void IFollowerHost.Linked() => PrefetchCompleted();

    void IFollowerHost.StoreReleased() => _cache.Clear();

    void IFollowerHost.Committed(bool longPass)
    {
        // Debounced: 3 s, or 60 s while a long first pass commits every few seconds (open windows would otherwise search
        // again twice a minute for days).
        if (Interlocked.Exchange(ref _contentArmed, 1) != 0) return;
        try { _contentTimer.Change(longPass ? 60_000 : 3000, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    void RaiseStatusThrottled()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastStatusRaise);
        if (now - last >= 1000) { RaiseStatus(); return; }
        if (Interlocked.Exchange(ref _statusArmed, 1) != 0) return;
        try { _statusTimer.Change(Math.Max(1, 1000 - (now - last)), Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    void RaiseStatus()
    {
        Interlocked.Exchange(ref _lastStatusRaise, Environment.TickCount64);
        Raise(StatusChanged);
    }

    static void Raise(Action? handler)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
            try { ((Action)d)(); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}

/// <summary>A DocIdMap moved through a FileIndex.Compact remap, as cheaply as the remap allows (the Compacted handler runs under Compact's write lock).</summary>
internal static class CompactRemap
{
    /// <summary>
    /// The map translated for the new generation. Compact keeps the order of live entries, so every id below the first
    /// removed slot keeps its value: when few mapped ids are at or above it (entries removed after most documents' files,
    /// the usual end of a rescan), only those are set; otherwise every chunk is translated at once. One sequential pass
    /// over the remap's unchanged head and one over the map's ids, instead of a random read of the remap per document.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static DocIdMap Translate(DocIdMap map, int[] remap, long generation)
    {
        int firstMoved = 0;
        while (firstMoved < remap.Length && remap[firstMoved] == firstMoved) firstMoved++;
        long mapped = 0, moved = 0;
        foreach (uint segmentId in map.SegmentIdList)
        {
            if (map.Segment(segmentId) is not { } segment) continue;
            foreach (var chunk in segment.Chunks)
            {
                if (chunk == null) continue;
                foreach (int id in chunk)
                {
                    if (id < 0) continue;
                    mapped++;
                    if (id >= firstMoved) moved++;
                }
            }
        }
        var b = map.ToBuilder();
        if (moved * 4 > mapped) b.RemapIds(remap);
        else if (moved > 0)
            foreach (uint segmentId in map.SegmentIdList)
            {
                if (map.Segment(segmentId) is not { } segment) continue;
                var chunks = segment.Chunks;
                for (int c = 0; c < chunks.Length; c++)
                {
                    if (chunks[c] is not { } chunk) continue;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        int id = chunk[i];
                        if (id >= firstMoved) b.Set(segmentId, (c << DocIdMap.ChunkBits) + i, (uint)id < (uint)remap.Length ? remap[id] : -1);
                    }
                }
            }
        return b.Build(generation);
    }
}

/// <summary>Demo and test switches.</summary>
internal sealed record ContentIndexOptions(bool AllRootsInScope = false, bool IgnorePower = false, bool IgnoreElevation = false,
    int PaceMs = 0, TimeSpan? CommitAfter = null)
{
    /// <summary>The content folder on disk (crash marker, the deny-list, free space, Kept); null in memory.</summary>
    public string? Folder { get; init; }
    /// <summary>Opens the store read-only (Kept); null in memory.</summary>
    public Func<IContentStore>? OpenReadOnly { get; init; }
    /// <summary>The clock (tests); UTC.</summary>
    public Func<DateTime>? Clock { get; init; }
    /// <summary>Root name → volume key, prefix and display root (tests); VolumeKeys.For by default.</summary>
    public Func<string, (string Key, string RelativePrefix, string DisplayRoot)>? VolumeResolver { get; init; }
    /// <summary>Reads run on the follower's own thread when started (tests).</summary>
    public bool InlineReads { get; init; }
    /// <summary>No follower thread: tests drive it with RunOnce; reads inline.</summary>
    public bool ManualLoop { get; init; }
    /// <summary>The most-opened list's Recent Items targets (tests); the real folder by default (on-disk indexes only).</summary>
    public Func<IReadOnlyList<string>>? RecentItems { get; init; }
    /// <summary>Whether a root still lists after the watchdog cancelled a read (tests); FolderScanner.ProbeRoot by default.</summary>
    public Func<string, CancellationToken, int>? ProbeRoot { get; init; }
    /// <summary>
    /// A re-key pass that moves 30% or more of a segment with more live documents than this rewrites that segment once
    /// (IContentStore.Rekey); a smaller segment is rewritten by the commit itself (ContentStore.RewriteInCommitDocs).
    /// </summary>
    public int RekeyInPlaceAbove { get; init; } = ContentStore.RewriteInCommitDocs;
    /// <summary>The follower's wait after a failed iteration, times the failures in a row (tests shorten it).</summary>
    public int FailureBackOffMs { get; init; } = 5000;
}
