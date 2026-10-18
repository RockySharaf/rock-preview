using System.Security.Principal;

namespace Anything.Core.Indexing;

/// <summary>
/// Owns the <see cref="FileIndex"/>: loads the database, (re)scans NTFS volumes (raw MFT + USN journal when elevated)
/// and folders (FindFirstFileEx + FileSystemWatcher), applies exclusions, and saves the database.
/// </summary>
public sealed class IndexManager : IIndexManager, IIndexerHost
{
    readonly object _gate = new();
    readonly object _saveGate = new();
    readonly List<RootIndexer> _roots = [];
    readonly Dictionary<RootIndexer, Task> _rescans = [];
    readonly Timer _tick;
    IndexSettings _settings = new();
    ExclusionFilter _filter = ExclusionFilter.None;
    CancellationTokenSource _cts = new();
    Task? _worker;
    volatile IndexState _state = IndexState.Idle;
    volatile string _status = "";
    volatile RootIndexer? _scanningRoot;
    volatile bool _fallbackMode;
    volatile bool _loaded;
    int _changePending;
    int _activeScans;
    bool _disposed;
    bool _started;

    public IndexManager()
    {
        IsElevated = CheckElevated();
        _tick = new Timer(_ => OnTick(), null, 250, 250);
    }

    public FileIndex Index { get; } = new();
    public IndexState State => _state;
    public bool IsElevated { get; }
    public event Action? StateChanged;

    /// <summary>Exceptions from background work, for diagnostics (last one wins).</summary>
    public Exception? LastError { get; private set; }

    ExclusionFilter IIndexerHost.Filter => _filter;

    public string StatusText
    {
        get
        {
            var root = _scanningRoot;
            if (root != null && _state == IndexState.Scanning)
            {
                long n = root switch { NtfsVolumeIndexer v => v.ProgressCount, FolderIndexer f => f.ProgressCount, _ => 0 };
                return _fallbackMode && root.Kind == RootKind.Folder && root.RootPath.Length == 3
                    ? $"Indexing requires administrator: scanning folders ({root.RootPath}, {n:N0} objects)"
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
            if (_started)
            {
                // Restart with new settings: persist what we have, then reload from the database.
                StopAllNoLock(saveFirst: true);
                Index.Clear();
                _loaded = false;
            }
            _started = true;
            _settings = settings;
            _filter = new ExclusionFilter(settings);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _worker = Task.Run(() => Run(loadDatabase: true, ct), ct);
        }
    }

    public void Rebuild()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopAllNoLock(saveFirst: false);
            _started = true;
            Index.Clear();
            _loaded = false;
            _filter = new ExclusionFilter(_settings);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetState(IndexState.Scanning, "Rebuilding...");
            _worker = Task.Run(() => Run(loadDatabase: false, ct), ct);
        }
    }

    /// <summary>Cancels background work and stops all monitors (caller holds _gate).</summary>
    void StopAllNoLock(bool saveFirst)
    {
        _cts.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(30)); } catch { }
        Task[] rescans;
        lock (_rescans) rescans = [.. _rescans.Values];
        try { Task.WaitAll(rescans, TimeSpan.FromSeconds(30)); } catch { }
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        foreach (var r in roots) { try { r.StopMonitoring(); } catch { } }
        if (saveFirst) TrySave();
        lock (_roots) _roots.Clear();
        foreach (var r in roots) { try { r.Dispose(); } catch { } }
        _worker = null;
        _scanningRoot = null;
        Interlocked.Exchange(ref _activeScans, 0);
    }

    void SetState(IndexState state, string status)
    {
        _state = state;
        _status = status;
        try { StateChanged?.Invoke(); } catch { }
    }

    void Run(bool loadDatabase, CancellationToken ct)
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
            _loaded = true;
            ct.ThrowIfCancellationRequested();

            // Decide the roots and match them with what the database had.
            var roots = CreateRoots(settings);
            var pending = new List<RootIndexer>();
            foreach (var root in roots)
            {
                var match = loaded.FirstOrDefault(l => string.Equals(RootIndexer.NormalizeRootName(l.Path), root.RootName, StringComparison.OrdinalIgnoreCase));
                bool current = false;
                if (match != null)
                {
                    loaded.Remove(match);
                    try { current = root.AttachLoaded(match); } catch { current = false; }
                }
                lock (_roots) _roots.Add(root);
                if (current)
                {
                    root.StartMonitoring(); // replays the journal from the saved USN (catch-up)
                }
                else pending.Add(root);
            }
            // Roots that are no longer configured.
            foreach (var stale in loaded) Index.Remove(stale.RootId);
            MarkChanged();

            if (pending.Count > 0) SetState(IndexState.Scanning, "Scanning...");
            foreach (var root in pending)
            {
                ct.ThrowIfCancellationRequested();
                ScanRoot(root, ct);
            }
            SetState(IndexState.Ready, _fallbackMode ? "Indexing requires administrator: scanning folders" : "");
            if (pending.Count > 0 || loadDatabase) TrySave();
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

    void ScanRoot(RootIndexer root, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeScans);
        _scanningRoot = root;
        try { StateChanged?.Invoke(); } catch { }
        try
        {
            root.Scan(ct);
            root.StartMonitoring();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastError = ex; }
        finally
        {
            if (_scanningRoot == root) _scanningRoot = null;
            Interlocked.Decrement(ref _activeScans);
            MarkChanged();
        }
    }

    List<RootIndexer> CreateRoots(IndexSettings settings)
    {
        var roots = new List<RootIndexer>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _fallbackMode = false;
        if (settings.IndexNtfsVolumes)
        {
            IEnumerable<string> volumes = settings.Volumes.Count > 0 ? settings.Volumes : FixedNtfsDrives();
            foreach (var v in volumes)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                string name = RootIndexer.NormalizeRootName(v);
                if (!names.Add(name)) continue;
                bool ntfs = IsNtfs(name);
                if (ntfs && IsElevated)
                {
                    var vol = new NtfsVolumeIndexer(this, name);
                    if (vol.TryOpen()) { roots.Add(vol); continue; }
                    vol.Dispose();
                }
                if (ntfs && !IsElevated) _fallbackMode = true;
                if (Directory.Exists(name.Length == 2 ? name + "\\" : name)) roots.Add(new FolderIndexer(this, name));
            }
        }
        foreach (var f in settings.Folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string name = RootIndexer.NormalizeRootName(f);
            // Skip folders already covered by another root.
            if (names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase) || name.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            names.Add(name);
            roots.Add(new FolderIndexer(this, name));
        }
        return roots;
    }

    static IEnumerable<string> FixedNtfsDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS"; } catch { ok = false; }
            if (ok) yield return d.Name;
        }
    }

    static bool IsNtfs(string drive)
    {
        if (drive.Length != 2 || drive[1] != ':') return false;
        try { var d = new DriveInfo(drive); return d.IsReady && d.DriveFormat == "NTFS"; } catch { return false; }
    }

    // ---- IIndexerHost ----

    void IIndexerHost.MarkChanged() => MarkChanged();

    void MarkChanged() => Interlocked.Exchange(ref _changePending, 1);

    void IIndexerHost.ReportProgress(RootIndexer root, long objects) { }

    void IIndexerHost.RequestRescan(RootIndexer root, string reason)
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (_disposed) return;
            ct = _cts.Token;
        }
        lock (_rescans)
        {
            if (_rescans.ContainsKey(root)) return;
            var task = Task.Run(() =>
            {
                try
                {
                    SetState(IndexState.Scanning, $"Rescanning {root.RootPath} ({reason})");
                    ScanRoot(root, ct);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_rescans) _rescans.Remove(root);
                    if (!ct.IsCancellationRequested && Volatile.Read(ref _activeScans) == 0 && _worker is { IsCompleted: true })
                    {
                        SetState(IndexState.Ready, "");
                        try { CompactIfWorthwhile(releaseMemory: false); } catch (Exception ex) { LastError = ex; }
                    }
                }
            });
            _rescans[root] = task;
        }
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
        if (Volatile.Read(ref _activeScans) != 0) return;
        int dead = Index.DeadCount;
        if (dead > 50_000 && dead > Index.Count / 16)
        {
            RootIndexer[] roots;
            lock (_roots) roots = [.. _roots];
            lock (_saveGate)
            {
                var taken = new List<object>();
                try
                {
                    foreach (var r in roots) { Monitor.Enter(r.Sync); taken.Add(r.Sync); }
                    if (Volatile.Read(ref _activeScans) == 0)
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
        if (releaseMemory) ReleaseMemory();
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
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        lock (_saveGate)
        {
            // Lock order: every root's Sync, then the index lock (taken inside IndexDatabase.Save).
            var taken = new List<object>();
            try
            {
                foreach (var r in roots) { Monitor.Enter(r.Sync); taken.Add(r.Sync); }
                int count = Index.Count;
                var infos = new List<DbRoot>(roots.Length);
                foreach (var r in roots)
                {
                    if (r.RootId < 0) continue;
                    var (jid, usn) = r.JournalPosition;
                    infos.Add(new DbRoot
                    {
                        Path = r.RootName,
                        Kind = r.Kind,
                        JournalId = jid,
                        NextUsn = usn,
                        RootId = r.RootId,
                        IdToFrn = r.Kind == RootKind.Ntfs ? r.BuildIdToFrn(count) : null,
                    });
                }
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
            _cts.Cancel();
            try { _worker?.Wait(TimeSpan.FromSeconds(30)); } catch { }
            Task[] rescans;
            lock (_rescans) rescans = [.. _rescans.Values];
            try { Task.WaitAll(rescans, TimeSpan.FromSeconds(30)); } catch { }
            RootIndexer[] roots;
            lock (_roots) roots = [.. _roots];
            foreach (var r in roots) { try { r.StopMonitoring(); } catch { } }
            TrySave();
            foreach (var r in roots) { try { r.Dispose(); } catch { } }
            lock (_roots) _roots.Clear();
        }
        _tick.Dispose();
        _cts.Dispose();
    }

    /// <summary>Test hook: applies queued folder-watcher events immediately.</summary>
    internal void FlushPendingChanges()
    {
        RootIndexer[] roots;
        lock (_roots) roots = [.. _roots];
        foreach (var r in roots) if (r is FolderIndexer f) f.ProcessPending();
    }
}
