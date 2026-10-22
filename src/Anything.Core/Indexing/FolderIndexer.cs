using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Anything.Core.Indexing;

/// <summary>(parent id, name) key, compared case-insensitively.</summary>
internal readonly record struct ChildKey(int Parent, string Name);

internal sealed class ChildKeyComparer : IEqualityComparer<ChildKey>
{
    public static readonly ChildKeyComparer Instance = new();
    public bool Equals(ChildKey x, ChildKey y) => x.Parent == y.Parent && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode(ChildKey k) => HashCode.Combine(k.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
}

/// <summary>
/// Indexes a folder tree (or a whole non-NTFS / non-elevated drive) by directory scanning, and keeps it current
/// with a <see cref="FileSystemWatcher"/>. Paths are resolved to ids through a (parent, name) → id dictionary.
/// A root that can't be reached (a network share offline, a drive unplugged) keeps its indexed entries and is
/// marked <see cref="RootStatus.Offline"/> until a later scan succeeds.
/// </summary>
internal sealed class FolderIndexer : RootIndexer
{
    enum EventKind : byte { Created, Deleted, Changed, Renamed }
    readonly record struct FsEvent(EventKind Kind, string Path, string? OldPath);

    Dictionary<ChildKey, int> _children = new(ChildKeyComparer.Instance);
    readonly ConcurrentQueue<FsEvent> _events = new();
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    readonly StrongBox<long> _progress = new();
    FileSystemWatcher? _watcher;
    Thread? _thread;
    CancellationTokenSource? _cts;
    volatile bool _scanning;
    bool _disposed;
    // A share whose change notifications fail every time would otherwise be rescanned in a loop: after two watcher
    // errors close together, the watcher stays off for a while and scheduled rescans keep the root current.
    long _lastWatcherError; // 0 = none yet
    long _watcherRetryAfter;
    bool _creatingWatcher;
    /// <summary>Bumped by StopMonitoring: a watcher built meanwhile (outside the lock) is thrown away, not published.</summary>
    int _monitorEpoch;

    public FolderIndexer(IIndexerHost host, string rootPath, bool explicitRoot = false) : base(host, rootPath, explicitRoot) { }

    public override RootKind Kind => RootKind.Folder;

    public override long ProgressCount => Interlocked.Read(ref _progress.Value);

    /// <summary>
    /// Only once a scan finished: a first scan adds entries as it goes, and one cut short is saved with no scan time (as
    /// is the copy of a mapped drive since mapped to another share, which is scanned again).
    /// </summary>
    public override bool Complete => RootId >= 0 && LastScanUtc > 0;

    /// <summary>
    /// The database keeps when the last full scan finished, and for a mapped drive which share it was (the journal
    /// fields are otherwise unused for folders): Z: mapped to another share since must not come back with the old files.
    /// </summary>
    public override (ulong JournalId, long NextUsn) JournalPosition => (ShareStamp, LastScanUtc);

    ulong ShareStamp
    {
        get
        {
            if (Plan?.Share is not { Length: > 0 } share) return 0;
            ulong h = 14695981039346656037; // FNV-1a: stable across runs, unlike string.GetHashCode
            foreach (char c in share.ToUpperInvariant()) h = (h ^ c) * 1099511628211;
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>
    /// Whether an error at the root keeps the indexed entries (the root is offline) rather than emptying it (the folder
    /// was deleted, or a local folder can't be read). A network root empties only when its share answers and the folder
    /// is gone: a changed password or a refused connection says nothing about the files.
    /// </summary>
    bool KeepsEntries(int error) => FolderScanner.IsOffline(RootName, error) || (IsNetwork && error != 3);

    /// <summary>The drive (or connection) can break partway, so a broken listing must not replace the indexed copy.</summary>
    bool FailOnBrokenConnection => IsNetwork || DriveKind is DriveKind.Removable or DriveKind.Optical;

    string Prefix => RootName + "\\";

    public override bool AttachLoaded(LoadedRoot loaded)
    {
        lock (Sync)
        {
            var map = new Dictionary<ChildKey, int>(loaded.Ids.Length, ChildKeyComparer.Instance);
            Index.Lock.EnterReadLock();
            try
            {
                for (int i = 1; i < loaded.Ids.Length; i++)
                {
                    int id = loaded.Ids[i];
                    map[new ChildKey(Index.Parent(id), Index.Name(id))] = id;
                }
            }
            finally { Index.Lock.ExitReadLock(); }
            _children = map;
            RootId = loaded.RootId;
            HasDateAccessed = loaded.HasDateAccessed;
            LastScanUtc = loaded.JournalId == ShareStamp ? loaded.NextUsn : 0; // another share now: scan it, don't resume
        }
        return false; // no journal for folders: always rescan (entries stay searchable meanwhile)
    }

    /// <summary>Whether a loaded root was fully scanned less than <paramref name="age"/> ago.</summary>
    public bool ScannedWithin(TimeSpan age)
    {
        if (RootId < 0 || LastScanUtc <= 0) return false;
        var since = DateTime.UtcNow - DateTime.FromFileTimeUtc(LastScanUtc);
        return since >= TimeSpan.Zero && since < age;
    }

    /// <summary>
    /// For entries loaded from the database and scanned recently (a network root at startup): checks the root can be
    /// reached and watches it, without rescanning. Unreachable: keeps the entries and goes offline.
    /// </summary>
    /// <returns>False when it needs a scan after all (the root answers but the folder is gone, say).</returns>
    public bool ResumeLoaded(CancellationToken ct)
    {
        int error = FolderScanner.ProbeRoot(RootName, ct);
        if (error != 0)
        {
            if (!KeepsEntries(error)) return false;
            GoOffline(new RootUnavailableException(RootName, error, offline: true));
            return true;
        }
        Status = RootStatus.Ready;
        EnsureWatcher();
        return true;
    }

    /// <summary>Whether the root answers now (worth a scan). Cancelling returns at once, even on a slow network.</summary>
    public bool IsReachable(CancellationToken ct)
    {
        int error = FolderScanner.ProbeRoot(RootName, ct);
        return error == 0 || !KeepsEntries(error);
    }

    public override void Scan(CancellationToken ct)
    {
        _scanning = true;
        Status = RootStatus.Scanning;
        try
        {
            Interlocked.Exchange(ref _progress.Value, 0);
            Host.ReportProgress(this, 0);
            // The index this scan writes into: a scan the manager stopped waiting for must not write into the next one.
            long generation;
            lock (Sync) generation = Index.Generation;
            if (RootId < 0)
            {
                // Buffer changes that happen while we scan, once the root has answered.
                if (FolderScanner.ProbeRoot(RootName, ct) == 0) EnsureWatcher();
                ScanProgressively(ct, generation);
                Host.ReportProgress(this, ProgressCount);
                return;
            }
            ScannedDirectory tree;
            try
            {
                int reach = FolderScanner.ProbeRoot(RootName, ct);
                if (reach != 0) throw new RootUnavailableException(RootName, reach);
                EnsureWatcher();
                tree = FolderScanner.Scan(RootName, Filter, ct, _progress, failOnBrokenConnection: FailOnBrokenConnection);
            }
            catch (RootUnavailableException ex) when (ex.Offline || KeepsEntries(ex.Error))
            {
                // Keep what the index has; a later scan replaces it once the root is back.
                GoOffline(ex);
                return;
            }
            catch (RootUnavailableException)
            {
                tree = new ScannedDirectory(RootName); // the folder is gone or unreadable: nothing in it now
            }
            ct.ThrowIfCancellationRequested();
            Native.TryGetMetadata(RootPath, out _, out _, out long mod, out long created, out long accessed);
            lock (Sync)
            {
                var map = new Dictionary<ChildKey, int>((int)Math.Min(ProgressCount + 16, 50_000_000), ChildKeyComparer.Instance);
                int newRoot = -1;
                bool withAccessed = false;
                Index.BulkAdd(b =>
                {
                    // Checked under the index's write lock, which Clear takes too.
                    if (ct.IsCancellationRequested || Index.Generation != generation) return;
                    withAccessed = Index.HasDateAccessed;
                    newRoot = FolderScanner.AddTree(b, tree, RootName, FileIndex.NoParent, FileAttributes.Directory, mod, created,
                        (p, n, id) => map[new ChildKey(p, n)] = id, accessed);
                });
                if (newRoot < 0) throw new OperationCanceledException(ct);
                int old = RootId;
                RootId = newRoot;
                _children = map;
                HasDateAccessed = withAccessed;
                if (old >= 0 && old != newRoot)
                {
                    CarryRecentChanges(old, newRoot, map);
                    Index.Remove(old);
                }
            }
            ScanFinished();
            Host.ReportProgress(this, ProgressCount);
        }
        finally
        {
            _scanning = false;
            if (Status == RootStatus.Scanning) Status = RootStatus.Ready;
            Wake();
        }
    }

    /// <summary>
    /// A rescan made new entries (a network drive's are made again every hour); what changed before still did: their
    /// recent changes go to the entries at the same paths in the new copy.
    /// </summary>
    void CarryRecentChanges(int oldRoot, int newRoot, Dictionary<ChildKey, int> newMap)
    {
        var recent = Index.RecentChanges();
        if (recent.Length == 0) return;
        var moved = new List<(int, long)>();
        var parts = new List<string>();
        foreach (var (id, time) in recent)
        {
            parts.Clear();
            int cur = id;
            while (cur != oldRoot && cur != FileIndex.NoParent && parts.Count < 4096)
            {
                parts.Add(Index.Name(cur));
                cur = Index.Parent(cur);
            }
            if (cur != oldRoot) continue; // another root's
            int now = newRoot;
            for (int i = parts.Count - 1; i >= 0 && now >= 0; i--)
                now = newMap.TryGetValue(new ChildKey(now, parts[i]), out int next) ? next : -1;
            if (now >= 0) moved.Add((now, time));
        }
        Index.SetRecentChanges(moved);
    }

    void ScanFinished()
    {
        LastScanUtc = DateTime.UtcNow.ToFileTimeUtc();
        Status = RootStatus.Ready;
        Detail = null;
    }

    /// <summary>The Windows error that took it offline, so it can be told in Windows' own words (a folder listing does).</summary>
    public int OfflineError { get; private set; }

    void GoOffline(RootUnavailableException why)
    {
        StopWatcher();
        _events.Clear();
        OfflineError = why.Error;
        Status = RootStatus.Offline;
        Detail = why.Message;
    }

    /// <summary>
    /// First scan of a root with nothing loaded: add each folder's entries as soon as it is read so results appear
    /// while the scan runs (there is no previous copy to keep searchable, so no swap is needed).
    /// </summary>
    void ScanProgressively(CancellationToken ct, long generation)
    {
        Native.TryGetMetadata(RootPath, out _, out _, out long rootMod, out long rootCreated, out long rootAccessed);
        var map = new Dictionary<ChildKey, int>(ChildKeyComparer.Instance);
        int rootId;
        lock (Sync)
        {
            ct.ThrowIfCancellationRequested();
            rootId = Index.Add(RootName, FileIndex.NoParent, FileAttributes.Directory, -1, rootMod, rootCreated, rootAccessed);
            RootId = rootId;
            _children = map;
            HasDateAccessed = Index.HasDateAccessed;
        }
        try
        {
            FolderScanner.Scan(RootName, Filter, ct, _progress, dir =>
            {
                if (dir.Entries.Count == 0) return;
                lock (Sync)
                {
                    Index.BulkAdd(b =>
                    {
                        // A worker the scan stopped waiting for (cancelled, the root dropped, the index cleared) adds nothing.
                        if (ct.IsCancellationRequested || Index.Generation != generation) return;
                        foreach (var e in dir.Entries)
                        {
                            int id = b.Add(e.Name, dir.IndexId, e.Attributes, e.Size, e.Modified, e.Created, e.Accessed);
                            map[new ChildKey(dir.IndexId, e.Name)] = id;
                            if (e.Directory != null) e.Directory.IndexId = id;
                        }
                    });
                }
            }, rootId, FailOnBrokenConnection);
        }
        catch (RootUnavailableException ex) when (ex.Offline || KeepsEntries(ex.Error))
        {
            ct.ThrowIfCancellationRequested();
            lock (Sync)
            {
                // Never reached: nothing to keep. Broke partway: keep what was read until the next full scan.
                if (Interlocked.Read(ref _progress.Value) == 0 && RootId == rootId && Index.Generation == generation)
                {
                    Index.Remove(rootId);
                    RootId = -1;
                    _children = new Dictionary<ChildKey, int>(ChildKeyComparer.Instance);
                }
            }
            GoOffline(ex);
            return;
        }
        catch (RootUnavailableException) { /* gone or unreadable: just the root */ }
        ct.ThrowIfCancellationRequested();
        ScanFinished();
    }

    public override void Remap(int[] remap)
    {
        base.Remap(remap);
        var map = new Dictionary<ChildKey, int>(_children.Count, ChildKeyComparer.Instance);
        foreach (var (key, id) in _children)
        {
            int newId = RemapId(remap, id);
            int newParent = RemapId(remap, key.Parent);
            if (newId >= 0 && newParent >= 0) map[new ChildKey(newParent, key.Name)] = newId;
        }
        _children = map;
    }

    public override void StartMonitoring()
    {
        if (Status == RootStatus.Offline || Released) return;
        EnsureWatcher();
    }

    /// <summary>
    /// The watcher was turned off after repeated errors and that pause is over: the manager rescans the root (changes
    /// went unseen meanwhile), which watches it again.
    /// </summary>
    public bool WatcherDue
    {
        get
        {
            lock (_gate)
                return !_disposed && !Released && Status == RootStatus.Ready && _watcher == null && !_creatingWatcher
                       && _watcherRetryAfter != 0 && Environment.TickCount64 >= _watcherRetryAfter;
        }
    }

    void EnsureWatcher()
    {
        int epoch;
        lock (_gate)
        {
            if (_disposed || Released) return;
            if (_thread == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _thread = new Thread(() => ProcessLoop(token)) { IsBackground = true, Name = "Rock Preview folder monitor " + RootPath };
                _thread.Start();
            }
            if (_watcher != null || _creatingWatcher || Environment.TickCount64 < _watcherRetryAfter) return;
            if (_watcherRetryAfter != 0)
            {
                // The pause is over: one try. If it fails the root stays unwatched (rescans keep it current), and an
                // error soon after this new watcher starts goes straight back into a pause.
                _watcherRetryAfter = 0;
                _lastWatcherError = Environment.TickCount64;
            }
            _creatingWatcher = true;
            epoch = _monitorEpoch;
        }
        // Built outside the lock: on an unreachable share the constructor alone can take twenty seconds, and stopping
        // (exit, a settings change) must not wait for it.
        FileSystemWatcher? w = null;
        try
        {
            w = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime | NotifyFilters.Attributes,
            };
            w.Created += (_, e) => Enqueue(new FsEvent(EventKind.Created, e.FullPath, null));
            w.Deleted += (_, e) => Enqueue(new FsEvent(EventKind.Deleted, e.FullPath, null));
            w.Changed += (_, e) => Enqueue(new FsEvent(EventKind.Changed, e.FullPath, null));
            w.Renamed += (_, e) => Enqueue(new FsEvent(EventKind.Renamed, e.FullPath, e.OldFullPath));
            w.Error += (_, e) => OnWatcherError(e.GetException());
            w.EnableRaisingEvents = true;
        }
        catch
        {
            // Not watchable (e.g. some network shares): index stays static until the next rescan.
            w?.Dispose();
            w = null;
        }
        lock (_gate)
        {
            _creatingWatcher = false;
            if (w != null && epoch == _monitorEpoch && !_disposed && !Released && _watcher == null)
            {
                _watcher = w;
                w = null;
            }
        }
        w?.Dispose(); // stopped, released or disposed while it was being made
    }

    void Enqueue(FsEvent e)
    {
        _events.Enqueue(e);
        Wake();
    }

    void Wake()
    {
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    void OnWatcherError(Exception ex)
    {
        // Buffer overflow (or a lost watch, e.g. the network went away): events were dropped, so the only safe
        // recovery is a rescan (which finds the root offline if it is).
        _events.Clear();
        if (Released) return;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastWatcherError != 0 && now - _lastWatcherError < 10 * 60_000) _watcherRetryAfter = now + 10 * 60_000;
            _lastWatcherError = now;
        }
        StopWatcher();
        Host.RequestRescan(this, ex is InternalBufferOverflowException ? "change buffer overflow" : ex.Message);
    }

    /// <summary>Closes the watcher (and with it the handle on the folder), keeping the event thread.</summary>
    void StopWatcher()
    {
        FileSystemWatcher? w;
        lock (_gate)
        {
            w = _watcher;
            _watcher = null;
        }
        try { w?.Dispose(); } catch (Exception) { }
    }

    public override void StopMonitoring()
    {
        Thread? t;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _monitorEpoch++;
            _watcher?.Dispose();
            _watcher = null;
            cts = _cts;
            _cts = null;
            cts?.Cancel();
            t = _thread;
            _thread = null;
        }
        // A loop that didn't stop in time still waits on the token: leave it undisposed rather than crash that thread.
        if (t == null || t == Thread.CurrentThread || t.Join(5000)) cts?.Dispose();
    }

    public override void Dispose()
    {
        lock (_gate) _disposed = true;
        StopMonitoring();
    }

    void ProcessLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny([_wake, ct.WaitHandle]) == 1) return;
            // Let bursts accumulate so one batch covers them.
            if (ct.WaitHandle.WaitOne(100)) return;
            if (_scanning) continue; // queued events are applied after the scan swaps in
            ProcessPending(ct);
        }
    }

    /// <summary>Applies all queued watcher events (also called by tests through the manager).</summary>
    internal void ProcessPending() => ProcessPending(CancellationToken.None);

    /// <summary>
    /// Applies the queued events. Stops between events once monitoring stops (<paramref name="ct"/>) or the index was
    /// cleared or renumbered: a batch left running must not resolve paths through ids that mean something else now.
    /// </summary>
    void ProcessPending(CancellationToken ct)
    {
        if (_events.IsEmpty) return;
        bool any = false;
        lock (Sync)
        {
            if (RootId < 0 || !Index.IsAlive(RootId)) { _events.Clear(); return; }
            long generation = Index.Generation; // stable while Sync is held: Compact needs every root's Sync
            _batch = ct;
            _batchGeneration = generation;
            try
            {
                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (!ct.IsCancellationRequested && Index.Generation == generation && _events.TryDequeue(out var e))
                {
                    try
                    {
                        any |= e.Kind switch
                        {
                            EventKind.Created => AddOrUpdate(e.Path),
                            EventKind.Deleted => RemovePath(e.Path),
                            EventKind.Changed => changed.Add(e.Path) && AddOrUpdate(e.Path),
                            EventKind.Renamed => RenamePath(e.OldPath!, e.Path),
                            _ => false,
                        };
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* best effort per event */ }
                }
            }
            finally { _batch = CancellationToken.None; }
        }
        if (any) Host.MarkChanged();
    }

    /// <summary>The running batch's token and index generation, for what it starts (under <see cref="RootIndexer.Sync"/>).</summary>
    CancellationToken _batch;
    long _batchGeneration;

    int Resolve(string path)
    {
        var trimmed = path.AsSpan().TrimEnd('\\');
        if (trimmed.Equals(RootName, StringComparison.OrdinalIgnoreCase)) return RootId;
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return -1;
        int cur = RootId;
        foreach (var part in path[Prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_children.TryGetValue(new ChildKey(cur, part), out int id) || !Index.IsAlive(id)) return -1;
            cur = id;
        }
        return cur;
    }

    static (string Parent, string Name) Split(string path)
    {
        path = path.TrimEnd('\\');
        int i = path.LastIndexOf('\\');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

    bool AddOrUpdate(string path)
    {
        var (parentPath, name) = Split(path);
        if (name.Length == 0) return false;
        int parentId = Resolve(parentPath);
        if (parentId < 0) return false; // parent unknown or excluded
        if (!Native.TryGetMetadata(path, out var attrs, out long size, out long mod, out long created, out long accessed)) return false;
        // That read can stall (a share reconnecting, a disk spinning up) past the stop: then the ids looked up above may
        // belong to another index by now.
        _batch.ThrowIfCancellationRequested();
        if (Index.Generation != _batchGeneration) throw new OperationCanceledException();
        bool isDir = (attrs & FileAttributes.Directory) != 0;
        var key = new ChildKey(parentId, name);
        int existing = _children.TryGetValue(key, out int e) && Index.IsAlive(e) ? e : -1;
        if (Filter.Excludes(name, attrs, isDir ? path : null))
        {
            if (existing < 0) return false;
            Index.Remove(existing);
            _children.Remove(key);
            return true;
        }
        if (existing >= 0)
        {
            if (Index.IsDirectory(existing) != isDir)
            {
                Index.Remove(existing);
                _children.Remove(key);
            }
            else
            {
                Index.Update(existing, attrs, size, mod, created, accessed);
                Index.SetRecentChange(existing, Now);
                return true;
            }
        }
        int added = -1;
        if (isDir && (attrs & FileAttributes.ReparsePoint) == 0)
        {
            // A folder that appears (created or moved in) may already have content.
            ScannedDirectory tree;
            try { tree = FolderScanner.Scan(path, Filter, _batch); }
            catch (RootUnavailableException) { tree = new ScannedDirectory(path); }
            var map = _children;
            Index.BulkAdd(b => added = FolderScanner.AddTree(b, tree, name, parentId, attrs, mod, created, (p, n, id) => map[new ChildKey(p, n)] = id, accessed));
        }
        else
        {
            added = Index.Add(name, parentId, attrs, isDir ? -1 : size, mod, created, accessed);
            _children[key] = added;
        }
        if (added >= 0) Index.SetRecentChange(added, Now);
        return true;
    }

    /// <summary>When a change was seen (a watcher event has no time of its own).</summary>
    static long Now => DateTime.UtcNow.ToFileTimeUtc();

    bool RemovePath(string path)
    {
        int id = Resolve(path);
        if (id < 0 || id == RootId) return false;
        _children.Remove(new ChildKey(Index.Parent(id), Index.Name(id)));
        Index.Remove(id);
        return true;
    }

    bool RenamePath(string oldPath, string newPath)
    {
        int id = Resolve(oldPath);
        if (id < 0 || id == RootId) return AddOrUpdate(newPath);
        var (newParentPath, newName) = Split(newPath);
        int newParent = Resolve(newParentPath);
        var oldKey = new ChildKey(Index.Parent(id), Index.Name(id));
        if (newParent < 0 || Filter.Excludes(newName, Index.Attributes(id), Index.IsDirectory(id) ? newPath : null))
        {
            _children.Remove(oldKey);
            Index.Remove(id);
            return true;
        }
        var newKey = new ChildKey(newParent, newName);
        if (_children.TryGetValue(newKey, out int clash) && clash != id && Index.IsAlive(clash)) Index.Remove(clash);
        _children.Remove(oldKey);
        Index.Rename(id, newName, newParent);
        _children[newKey] = id;
        Index.SetRecentChange(id, Now);
        return true;
    }
}
