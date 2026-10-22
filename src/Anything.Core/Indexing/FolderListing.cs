using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anything.Core.Indexing;

/// <summary>Where a <see cref="FolderListing"/> stands: reading its folders, read (and kept current), or none of them readable.</summary>
public enum ListingState { Reading, Ready, Unavailable }

/// <summary>
/// Folders read straight from the disk into a private index of their own, for a folder the main index doesn't hold
/// (left out in Settings › Folders, its drive turned off, not indexed yet). Each is read like a folder listed under
/// Settings › Folders: rules that leave out the folder itself or a folder above it don't apply, every rule about what is
/// inside it does. Then a <see cref="FileSystemWatcher"/> keeps it current, and every 30 seconds the listing does for its
/// folders what the index does for its roots (<see cref="CheckRoots"/>). Made by the index manager
/// (<see cref="IIndexManager.CreateListing"/>), it lets go of a drive being ejected as the index does. Its own
/// <see cref="FileIndex"/> and search engine leave the main index, and the main engine's path and sort caches, alone.
/// </summary>
public sealed partial class FolderListing : IDisposable, IIndexerHost
{
    /// <summary><see cref="Changed"/> is raised at most this often.</summary>
    const int ChangedIntervalMs = 150;
    /// <summary>How often the folders are looked at for what a watcher doesn't do (<see cref="CheckRoots"/>).</summary>
    const int CheckIntervalMs = 30_000;
    /// <summary>A drive let go of for an eject is taken back after this long, if Windows hasn't said the eject failed.</summary>
    const int ReleasedMs = 60_000;

    /// <summary>One listed folder: its indexer, and how reading it went (set under the listing's gate).</summary>
    sealed class ListedRoot(FolderIndexer indexer)
    {
        public readonly FolderIndexer Indexer = indexer;
        /// <summary>Its first read is over (it may have found the folder unreadable).</summary>
        public volatile bool Done;
        /// <summary>Why it can't be read; null when it can.</summary>
        public volatile string? Problem;
        /// <summary><see cref="Done"/> and <see cref="Problem"/> from before it was let go of for an eject, put back if the eject doesn't happen.</summary>
        public bool DoneBefore;
        public string? ProblemBefore;
        /// <summary>Set once <see cref="Start"/> has watched it for an eject: it isn't read before.</summary>
        public volatile bool Begun;
        /// <summary>Lets go of its drive when Windows asks to eject it (set under the watch gate); null when not watched.</summary>
        public IDisposable? Watch;
        /// <summary>An eject watch is being set up, outside the watch gate (set under it).</summary>
        public bool WatchStarting;
        /// <summary>Bumped when its watch ends (under the watch gate): one being set up meanwhile is thrown away, not kept.</summary>
        public int WatchEpoch;
        /// <summary>1 while a look at whether it can be read again runs.</summary>
        public int Probing;
    }

    readonly ListedRoot[] _roots;
    readonly ExclusionFilter _filter;
    readonly DeviceWatcher? _devices;
    /// <summary>The app's run history, which the index's own copy follows; null for none.</summary>
    readonly RunHistory? _runSource;
    readonly int _networkRescanMinutes;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    readonly object _raiseGate = new();
    readonly object _watchGate = new();
    readonly object _checkGate = new();
    readonly Timer _changedTimer;
    readonly Timer _checkTimer;
    volatile ListingState _state = ListingState.Reading;
    volatile string? _problem;
    volatile bool _started;
    volatile bool _disposed;
    int _changedQueued;
    long _lastChanged;
    /// <summary>Reads running: the index is compacted only when none is, since a first read holds ids in flight.</summary>
    int _reading;
    int _readsDone;

    /// <param name="folders">The folders to list; repeats and folders inside another one listed are dropped.</param>
    /// <param name="settings">The exclusions, the optional information to keep (date accessed, folder sizes, recent changes) and how often shares are read again.</param>
    /// <param name="runHistory">
    /// The app's run history, so Run Count and runcount: work in the listing too. The index gets a copy that follows it
    /// until <see cref="Dispose"/>: one history matched against two indexes in turn would match every name again each
    /// time, and the main index's Run Count order would be sorted from scratch.
    /// </param>
    public FolderListing(IReadOnlyList<string> folders, IndexSettings settings, RunHistory? runHistory = null)
        : this(folders, settings, runHistory, devices: null) { }

    /// <param name="devices">The device notifications that say a drive is being ejected (the index manager's); null for none.</param>
    /// <param name="kindOf">The kind of drive a folder is on (tests pass their own).</param>
    internal FolderListing(IReadOnlyList<string> folders, IndexSettings settings, RunHistory? runHistory, DeviceWatcher? devices,
        Func<string, DriveKind>? kindOf = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(settings);
        // Before anything is added, as the main index is configured.
        Index.Configure(settings.IndexDateAccessed, settings.IndexFolderSizes, settings.IndexRecentChanges);
        _filter = new ExclusionFilter(settings);
        _devices = devices;
        _networkRescanMinutes = settings.NetworkRescanMinutes;
        var names = TopFolders(folders);
        if (names.Count == 0) throw new ArgumentException("There is no folder to list.", nameof(folders));
        kindOf ??= KindOfDrive;
        _roots = [.. names.Select(n => new ListedRoot(CreateRoot(n, kindOf(n))))];
        Folders = [.. _roots.Select(r => r.Indexer.RootPath)];
        _changedTimer = new Timer(_ => RaiseChanged());
        _checkTimer = new Timer(_ => CheckIfIdle());
        Index.Changed += QueueChanged;
        if (runHistory != null)
        {
            // Followed first, so a run counted while it is copied isn't missed.
            _runSource = runHistory;
            runHistory.Changed += FollowRunHistory;
            Index.RunHistory.CopyFrom(runHistory);
        }
    }

    void FollowRunHistory()
    {
        if (!_disposed && _runSource != null) Index.RunHistory.CopyFrom(_runSource);
    }

    /// <summary>The listing's own index: the folders listed and everything in them the rules keep.</summary>
    public FileIndex Index { get; } = new();

    /// <summary>
    /// The engine to search <see cref="Index"/> with. Its own: one engine searching two indexes in turn would rebuild its
    /// path and sort caches every time.
    /// </summary>
    public ISearchEngine Engine { get; } = new Search.SearchEngine();

    /// <summary>The folders listed ("C:\" or "D:\Data"), without repeats or folders inside another one listed.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// Reading until every folder was read once or found unreadable; then Ready, or Unavailable when none of them can be
    /// read (<see cref="Problem"/> says why). A folder read before that went away (a share, a drive ejected) keeps what
    /// was read, so <see cref="Found"/> can be above 0 while Unavailable: what was found is still worth showing, and the
    /// reason is for when there is nothing to show.
    /// </summary>
    public ListingState State => _state;

    /// <summary>Why the first folder that can't be read can't be, in Windows' words; null when every folder can.</summary>
    public string? Problem => _problem;

    /// <summary>Files and folders found: counted as they are read, then what the listing holds (the folders listed not counted).</summary>
    public long Found
    {
        get
        {
            long n = 0;
            if (_state == ListingState.Reading)
            {
                foreach (var root in _roots) n += root.Indexer.ProgressCount;
                return n;
            }
            foreach (var root in _roots)
                if (Index.IsAlive(root.Indexer.RootId)) n++;
            return Math.Max(0, Index.LiveCount - n);
        }
    }

    /// <summary>Raised on a worker thread, at most every 150 ms, after entries were added, removed or changed.</summary>
    public event Action? Changed;

    /// <summary>Raised on a worker thread when <see cref="State"/> or <see cref="Problem"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Starts reading every folder, each on a thread of its own, then watching it for changes. Returns at once: each
    /// folder's eject watch is set up first on a pool thread, as that waits for the device notification thread (busy for
    /// seconds while another drive is let go of), and a read begun before its watch would hold a drive being ejected.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var root in _roots)
            {
                try { WatchRemoval(root); } catch (Exception) { /* the next check tries again */ }
                root.Begun = true;
                try { Read(root); }
                catch (Exception ex) { SetRoot(root, done: true, ex.Message); } // no thread to read it on: the next check tries again
            }
        });
        try { _checkTimer.Change(CheckIntervalMs, CheckIntervalMs); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Reads every folder again in the background, swapping in each new copy when done (a folder that couldn't be read is
    /// tried again; one let go of for an eject is read once it is taken back; one not read yet, by its first read). Nothing
    /// before <see cref="Start"/> or after <see cref="Dispose"/>.
    /// </summary>
    public void Rescan()
    {
        if (!_started || _disposed) return;
        foreach (var root in _roots) Read(root);
    }

    /// <summary>
    /// Stops reading and watching, and lets go of the drives. No event is raised once it returns: it waits for a handler
    /// already running, and otherwise returns at once (the folders' monitors, which can take seconds to stop, are stopped
    /// on a thread-pool thread).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Changed = null;
        StateChanged = null;
        // Raising checks for Dispose under this gate: a handler already running returns first, and none starts after.
        lock (_raiseGate) { }
        Index.Changed -= QueueChanged;
        if (_runSource != null) _runSource.Changed -= FollowRunHistory;
        _cts.Cancel(); // not disposed: a read still stuck in a slow network call checks it when it wakes up
        _changedTimer.Dispose();
        _checkTimer.Dispose();
        lock (_watchGate)
            foreach (var root in _roots) Unwatch(root);
        var roots = _roots;
        Stopped = Task.Run(() =>
        {
            foreach (var root in roots) { try { root.Indexer.Dispose(); } catch { } }
        });
    }

    /// <summary>The folders normalized, without repeats or folders inside another one listed (whose entries it has already).</summary>
    static List<string> TopFolders(IReadOnlyList<string> folders)
    {
        var names = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(RootIndexer.NormalizeRootName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.FindAll(n => !names.Exists(other => IsInside(n, other)));
    }

    static bool IsInside(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] == '\\';

    /// <summary>A folder's indexer, read as one listed under Settings › Folders (on the network: kept while the share is away).</summary>
    FolderIndexer CreateRoot(string name, DriveKind kind) =>
        new(this, name, explicitRoot: true) { DriveKind = kind, IsNetwork = kind == DriveKind.Network };

    /// <summary>
    /// The kind of drive a folder is on: a share is on the network, a mapped drive too (its server isn't asked); anything
    /// else is on the volume that holds it, which for a volume mounted in a folder (a USB disk at C:\Mount\USB) isn't the
    /// drive around it. \\?\ and \\.\ paths are local.
    /// </summary>
    static DriveKind KindOfDrive(string folder)
    {
        if (IndexPlan.IsUnc(folder)) return DriveKind.Network;
        string drive = Path.GetPathRoot(folder) is { Length: > 0 } root ? root : folder;
        var kind = Drives.KindOf(drive);
        if (kind == DriveKind.Network) return kind;
        return VolumeOf(folder) is { } volume && !string.Equals(volume, drive, StringComparison.OrdinalIgnoreCase) ? Drives.KindOf(volume) : kind;
    }

    /// <summary>The root of the volume that holds <paramref name="path"/> ("C:\", "C:\Mount\USB\"), or null.</summary>
    static unsafe string? VolumeOf(string path)
    {
        const int size = 1024;
        char* buffer = stackalloc char[size];
        using var quiet = Drives.QuietErrors(); // no "Insert a disk" box for an emptied card reader
        return GetVolumePathName(path, buffer, size) ? new string(buffer) : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, int bufferLength);

    // ---- reading ----

    /// <summary>
    /// Reads one folder on a thread of its own. Never twice at once: a request that comes while it is read (a watcher
    /// overflow during the first read, say) makes the running read go once more when it finishes, so the first read's
    /// entries are never swapped out from under it. Not while it is let go of for an eject: it is read when taken back.
    /// Nor before <see cref="Start"/> has watched it for an eject (a refresh just after Start, say): a read would hold its
    /// drive with nothing to let go of it, and its first read, coming once it is watched, reads what was asked for.
    /// </summary>
    void Read(ListedRoot root)
    {
        var r = root.Indexer;
        if (_disposed || r.Released || r.Lifetime.IsCancellationRequested) return;
        // Asked for first: a read just finishing then sees it and goes once more, so the request is never lost.
        r.RescanPending = true;
        if (!root.Begun || Interlocked.CompareExchange(ref r.ScanBusy, 1, 0) != 0) return;
        r.ScanIdle.Reset();
        Interlocked.Increment(ref _reading);
        try
        {
            new Thread(() => ReadLoop(root)) { IsBackground = true, Name = "Rock Preview folder listing " + r.RootPath }.Start();
        }
        catch
        {
            Interlocked.Decrement(ref _reading);
            Interlocked.Exchange(ref r.ScanBusy, 0);
            r.ScanIdle.Set();
            throw;
        }
    }

    void ReadLoop(ListedRoot root)
    {
        var r = root.Indexer;
        try
        {
            do
            {
                r.ScanIdle.Reset();
                try
                {
                    // Stopped by Dispose, and by an eject letting go of the drive (which waits for the read to close its handles).
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, r.Lifetime.Token);
                    var ct = linked.Token;
                    while (r.RescanPending && !ct.IsCancellationRequested)
                    {
                        r.RescanPending = false;
                        ReadOnce(root, ct);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    // A full fence before the flag is read again below: a request made meanwhile either finds no read
                    // running and starts one, or is seen there.
                    Interlocked.Exchange(ref r.ScanBusy, 0);
                    r.ScanIdle.Set();
                }
            }
            // Asked for again just as it finished: once more, unless another read has started meanwhile.
            while (!_disposed && !r.Released && !r.Lifetime.IsCancellationRequested && r.RescanPending
                   && Interlocked.CompareExchange(ref r.ScanBusy, 1, 0) == 0);
        }
        finally
        {
            if (Interlocked.Decrement(ref _reading) == 0)
            {
                try { CompactIfWorthwhile(); } catch (Exception) { /* tried again on the next check */ }
            }
        }
    }

    void ReadOnce(ListedRoot root, CancellationToken ct)
    {
        string? problem;
        try { problem = ReadFolder(root, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // What an earlier read found stays, and so does why it couldn't be read; a folder never read says why.
            problem = root.Indexer.RootId < 0 ? ex.Message : root.Problem;
        }
        SetRoot(root, done: true, problem);
        Interlocked.Increment(ref _readsDone);
    }

    /// <summary>
    /// Reads one folder (the first time progressively, so entries show while it runs; later by a full scan swapped in when
    /// done) and watches it. Returns why it can't be read, or null.
    /// </summary>
    string? ReadFolder(ListedRoot root, CancellationToken ct)
    {
        var r = root.Indexer;
        // Tried again after it couldn't be read: reading, while asking takes (up to half a minute for a share that's gone).
        if (root.Done && root.Problem != null) SetRoot(root, done: false, root.Problem);
        int error = FolderScanner.ProbeRoot(r.RootName, ct);
        // A first read would show a folder it can't list as an empty one: say why instead.
        if (error != 0 && r.RootId < 0) return Message(error);
        // Back after it couldn't be read: reading it now.
        if (error == 0 && r.RootId < 0 && root.Problem != null) SetRoot(root, done: false, problem: null);
        r.Scan(ct);
        ct.ThrowIfCancellationRequested();
        // The share or drive went away, partway or before the scan reached it (which then took its entry out again): what
        // was read stays, unwatched until it answers again.
        if (r.Status == RootStatus.Offline || r.RootId < 0) return Message(r.OfflineError != 0 ? r.OfflineError : 59 /* ERROR_UNEXP_NET_ERR */);
        r.StartMonitoring();
        // Asked again, as the scan's own answer isn't kept: gone or locked since the check, the scan holds just the folder
        // itself, which must say why rather than look empty; briefly away at the check, it has no problem now.
        int now = FolderScanner.ProbeRoot(r.RootName, ct);
        return now != 0 ? Message(now) : null;
    }

    static string Message(int error) => new Win32Exception(error).Message;

    /// <summary>Records how reading one folder went, then works out the listing's state from every folder's.</summary>
    void SetRoot(ListedRoot root, bool done, string? problem)
    {
        lock (_gate)
        {
            if (_disposed) return;
            root.Done = done;
            root.Problem = problem;
            var state = Array.Exists(_roots, r => !r.Done) ? ListingState.Reading
                : Array.TrueForAll(_roots, r => r.Problem != null) ? ListingState.Unavailable
                : ListingState.Ready;
            string? first = Array.Find(_roots, r => r.Problem != null)?.Problem;
            if (state == _state && first == _problem) return;
            _state = state;
            _problem = first;
        }
        // Under the gate Dispose waits for, as Changed is.
        lock (_raiseGate)
        {
            if (_disposed) return;
            try { StateChanged?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Rescans swap in a new copy and leave the old one behind as removed slots: once those outnumber the entries, the
    /// index is renumbered without them, translating every folder's stored ids under its lock (as the index manager
    /// does). Never while a read runs: a first read holds the ids of the folders it is still listing. The index's
    /// generation changes, so ids from before are known to be stale (and Changed has views search again).
    /// </summary>
    void CompactIfWorthwhile()
    {
        if (_disposed || Volatile.Read(ref _reading) != 0 || Index.DeadCount <= Index.LiveCount) return;
        var taken = new List<object>(_roots.Length);
        try
        {
            foreach (var root in _roots) { Monitor.Enter(root.Indexer.Sync); taken.Add(root.Indexer.Sync); }
            // A read started meanwhile waits for these locks before it reads the index's generation.
            if (Volatile.Read(ref _reading) != 0 || Index.DeadCount <= Index.LiveCount) return;
            var remap = Index.Compact();
            foreach (var root in _roots) root.Indexer.Remap(remap);
        }
        finally
        {
            for (int i = taken.Count - 1; i >= 0; i--) Monitor.Exit(taken[i]);
        }
    }

    // ---- what a watcher doesn't do ----

    /// <summary>
    /// Every 30 seconds, what the index does for its roots: a folder whose watcher was turned off after repeated errors is
    /// read again once that pause is over, which watches it again; one that couldn't be read is read once it answers; a
    /// share is read again once its last read is older than Settings' rescan interval (its change notifications can miss
    /// changes); a drive let go of for an eject is taken back once the eject didn't happen, or a minute on; and the
    /// removed slots rescans leave behind are reclaimed.
    /// </summary>
    void CheckRoots()
    {
        // Reentrant: the timer's check holds it already.
        lock (_checkGate)
        {
            if (!_started || _disposed) return;
            try
            {
                long now = Environment.TickCount64;
                foreach (var root in _roots)
                {
                    var r = root.Indexer;
                    if (r.Released)
                    {
                        if (r.EjectFailed || now - r.ReleasedAtTicks > ReleasedMs) Revive(root);
                        continue;
                    }
                    // An eject watch that couldn't be set up (its card reader was empty) or ended (its drive went away): again.
                    if (r.Status == RootStatus.Ready) WatchRemoval(root);
                    if (Volatile.Read(ref r.ScanBusy) != 0) continue;
                    if (r.WatcherDue) Read(root); // changes went unseen meanwhile
                    else if (r.Status == RootStatus.Offline || root.Problem != null) ReadIfBack(root);
                    else if (r.IsNetwork && r.Status == RootStatus.Ready && _networkRescanMinutes > 0 && r.LastScanUtc > 0)
                    {
                        var since = DateTime.UtcNow - DateTime.FromFileTimeUtc(r.LastScanUtc);
                        // A scan time in the future (the clock was wrong, then corrected) counts as due.
                        if (since < TimeSpan.Zero || since >= TimeSpan.FromMinutes(_networkRescanMinutes)) Read(root);
                    }
                }
                CompactIfWorthwhile();
            }
            catch (Exception) { /* looked at again on the next check */ }
        }
    }

    /// <summary>
    /// The timer's check, skipped while the last one still runs (a drive that stopped answering, say), as the index's drive
    /// tick is: otherwise every tick would park a pool thread on the gate until it answers.
    /// </summary>
    void CheckIfIdle()
    {
        if (!Monitor.TryEnter(_checkGate)) return;
        try { CheckRoots(); }
        finally { Monitor.Exit(_checkGate); }
    }

    /// <summary>
    /// A folder that couldn't be read is looked at on a pool thread (a share that doesn't answer can take half a minute to
    /// say so), and read once it can be. An offline share that answers without the folder is read too: that empties it.
    /// </summary>
    void ReadIfBack(ListedRoot root)
    {
        if (Interlocked.Exchange(ref root.Probing, 1) != 0) return;
        var r = root.Indexer;
        Task.Run(() =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, r.Lifetime.Token);
                bool back = r.Status == RootStatus.Offline ? r.IsReachable(linked.Token) : FolderScanner.ProbeRoot(r.RootName, linked.Token) == 0;
                if (back) Read(root);
            }
            catch (Exception) { /* looked at again on the next check */ }
            finally { Volatile.Write(ref root.Probing, 0); }
        });
    }

    // ---- drives being ejected ----

    /// <summary>
    /// For a drive that can be ejected (any local one, as the index does: a handle anywhere on a drive blocks its eject, and
    /// USB hard disks report as fixed): when Windows asks, the folder lets go of the drive (<see cref="LetGo"/>), so a
    /// tab showing it doesn't block Safely Remove. Nothing when there is a live watch already, or one is being set up.
    /// The watch is set up outside the watch gate (as a folder's watcher is made outside its lock): that waits for the
    /// device thread, which can be letting go of another drive for seconds, and Dispose must not wait behind it.
    /// </summary>
    void WatchRemoval(ListedRoot root)
    {
        var r = root.Indexer;
        if (_devices == null || r.IsNetwork) return;
        int epoch;
        lock (_watchGate)
        {
            // Windows ends a watch when its drive goes away or the eject is over: a new one is needed then.
            if (_disposed || r.Released || root.WatchStarting || root.Watch is DeviceWatcher.Watch { Notify: not 0 }) return;
            Unwatch(root);
            root.WatchStarting = true;
            epoch = root.WatchEpoch;
        }
        IDisposable? watch = null;
        try
        {
            using var quiet = Drives.QuietErrors(); // no "Insert a disk" box for an emptied card reader
            watch = _devices.WatchRemoval(r.RootPath,
                release: () => LetGo(root),
                ejectFailed: () =>
                {
                    r.EjectFailed = true;
                    // Now rather than on the next check, and off the thread Windows' notifications come on.
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { Revive(root); } catch (Exception) { /* the next check tries again */ }
                    });
                });
        }
        finally
        {
            lock (_watchGate)
            {
                root.WatchStarting = false;
                // Kept unless the listing was disposed or the watch ended meanwhile.
                if (!_disposed && epoch == root.WatchEpoch)
                {
                    root.Watch = watch;
                    watch = null;
                }
            }
            try { watch?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Windows is about to eject the folder's drive (or lock it): on the device thread, before Windows looks for open
    /// handles, the folder's read is stopped and its watcher closed. What was read stays, and the folder says it can't
    /// be read, rather than Reading for the minute until it is taken back or Ready with nothing watched: once the eject
    /// goes through, the drive is gone. An eject that doesn't happen (a program has a file open on it, or a lock is
    /// over) puts back how it was, then reads it again.
    /// </summary>
    void LetGo(ListedRoot root)
    {
        var r = root.Indexer;
        r.ReleasedAtTicks = Environment.TickCount64;
        try { r.Lifetime.Cancel(); } catch (ObjectDisposedException) { }
        // A read in progress closes its find handles once it sees the cancel; give it a moment to.
        r.ScanIdle.Wait(5000);
        r.Release();
        lock (_gate)
        {
            root.DoneBefore = root.Done;
            root.ProblemBefore = root.Problem;
        }
        SetRoot(root, done: true, root.Problem ?? Message(21 /* ERROR_NOT_READY */));
    }

    /// <summary>
    /// Takes back a drive let go of for an eject (that didn't happen, or a minute ago): watched again, and read again.
    /// When the eject didn't happen the drive never left, so the folder is first put back as it was: read before, it is
    /// read again quietly, not as Reading with the drive not ready. A minute on, it may really have gone, and says so
    /// while it is read.
    /// </summary>
    void Revive(ListedRoot root)
    {
        var r = root.Indexer;
        bool stayed;
        lock (_watchGate)
        {
            if (_disposed || !r.Released) return;
            stayed = r.EjectFailed;
            r.Revive(null);
            // A new watch: the old one has let go already, so it wouldn't let go for another eject.
            Unwatch(root);
        }
        if (stayed) SetRoot(root, root.DoneBefore, root.ProblemBefore);
        WatchRemoval(root);
        Read(root); // changes went unseen meanwhile
    }

    /// <summary>Ends a folder's eject watch, closing its handle on the drive (the caller holds the watch gate).</summary>
    static void Unwatch(ListedRoot root)
    {
        var watch = root.Watch;
        root.Watch = null;
        root.WatchEpoch++;
        try { watch?.Dispose(); } catch (Exception) { }
    }

    // ---- change events ----

    /// <summary>Raises <see cref="Changed"/> soon: at once after a quiet spell, else 150 ms after the last time.</summary>
    void QueueChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _changedQueued, 1) != 0) return;
        // Stopwatch, not TickCount64: that moves in 15 ms steps, so two events could come 135 ms apart.
        long wait = (long)Math.Ceiling(ChangedIntervalMs - Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChanged)).TotalMilliseconds);
        try { _changedTimer.Change(Math.Clamp(wait, 0, ChangedIntervalMs), Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    void RaiseChanged()
    {
        // One at a time: a slow handler delays the next one rather than running beside it. Dispose waits on this gate too.
        lock (_raiseGate)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _lastChanged, Stopwatch.GetTimestamp());
            // Cleared first, so a change made while the handlers run is raised again.
            Volatile.Write(ref _changedQueued, 0);
            try { Changed?.Invoke(); } catch { }
        }
    }

    // ---- IIndexerHost ----

    ExclusionFilter IIndexerHost.Filter => _filter;

    void IIndexerHost.MarkChanged() => QueueChanged();

    void IIndexerHost.RequestRescan(RootIndexer root, string reason)
    {
        foreach (var listed in _roots)
            if (ReferenceEquals(listed.Indexer, root)) Read(listed);
    }

    void IIndexerHost.ReportProgress(RootIndexer root, long objects) { }

    // ---- test hooks ----

    /// <summary>Test hook: the folders' indexers, in the order of <see cref="Folders"/>.</summary>
    internal FolderIndexer[] Indexers => [.. _roots.Select(r => r.Indexer)];

    /// <summary>Test hook: the eject watch of the folder at <paramref name="index"/> in <see cref="Folders"/>, or null.</summary>
    internal IDisposable? RemovalWatch(int index) => _roots[index].Watch;

    /// <summary>Test hook: reads finished so far (each folder's first read and every read again).</summary>
    internal int ReadsDone => Volatile.Read(ref _readsDone);

    /// <summary>Test hook: looks at the folders now instead of on the next check.</summary>
    internal void CheckRootsNow() => CheckRoots();

    /// <summary>Test hook: the folders' monitors stopping after <see cref="Dispose"/>; null before.</summary>
    internal Task? Stopped { get; private set; }

    // ---- a quick look ----

    /// <summary>
    /// Whether <paramref name="folder"/> holds a file the rules keep, found cheaply: folders are listed breadth first, at
    /// most <paramref name="maxFolders"/> of them (the folder itself included), stopping at the first such file. Rules
    /// that leave out the folder itself or a folder above it don't count, as for a folder listed.
    /// </summary>
    /// <param name="deep">Look in its subfolders too; otherwise only directly inside it.</param>
    /// <param name="counts">Which files count, by name (everyday files only, say); null for every file the rules keep.</param>
    /// <returns>Null when the folder itself can't be listed; false when no kept file was found within the limit.</returns>
    public static unsafe bool? HasFile(string folder, ExclusionFilter filter, bool deep, int maxFolders, CancellationToken ct,
                                       Func<string, bool>? counts = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(filter);
        string root = RootIndexer.NormalizeRootName(folder);
        var rules = filter.WithoutAncestorsOf(root);
        var queue = new Queue<string>();
        queue.Enqueue(root);
        using var quiet = Drives.QuietErrors(); // no "Insert a disk" box for an emptied card reader
        int seen = 0;
        for (int visited = 0; queue.Count > 0 && visited < Math.Max(1, maxFolders); visited++)
        {
            ct.ThrowIfCancellationRequested();
            string dir = queue.Dequeue();
            Win32FindData data;
            nint h = Native.FindFirstFileEx(Native.LongPath(dir + "\\*"), Native.FindExInfoBasic, &data, 0, 0, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.InvalidHandle)
            {
                // FILE_NOT_FOUND: an empty folder (or drive root). A subfolder that can't be read is skipped.
                int error = Marshal.GetLastPInvokeError();
                if (visited == 0 && error is not (Native.ERROR_FILE_NOT_FOUND or Native.ERROR_NO_MORE_FILES)) return null;
                continue;
            }
            try
            {
                do
                {
                    char* fn = data.FileName;
                    if (fn[0] == '.' && (fn[1] == '\0' || (fn[1] == '.' && fn[2] == '\0'))) continue;
                    if ((++seen & 1023) == 0) ct.ThrowIfCancellationRequested(); // one folder can hold a great many
                    var attrs = (FileAttributes)data.FileAttributes;
                    string name = new(fn);
                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    string? path = isDir ? dir + "\\" + name : null;
                    if (rules.Excludes(name, attrs, path)) continue;
                    if (!isDir)
                    {
                        if (counts == null || counts(name)) return true;
                        continue;
                    }
                    // Queued only while it will be looked in: this one, those queued and the next stay within the limit.
                    if (deep && (attrs & FileAttributes.ReparsePoint) == 0 && visited + queue.Count + 1 < maxFolders) queue.Enqueue(path!);
                }
                while (Native.FindNextFile(h, &data));
            }
            finally { Native.FindClose(h); }
        }
        return false;
    }
}
