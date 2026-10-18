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

    public FolderIndexer(IIndexerHost host, string rootPath) : base(host, rootPath) { }

    public override RootKind Kind => RootKind.Folder;

    public long ProgressCount => Interlocked.Read(ref _progress.Value);

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
        }
        return false; // no journal for folders: always rescan (entries stay searchable meanwhile)
    }

    public override void Scan(CancellationToken ct)
    {
        _scanning = true;
        EnsureWatcher(); // buffer changes that happen while we scan
        try
        {
            Interlocked.Exchange(ref _progress.Value, 0);
            Host.ReportProgress(this, 0);
            if (RootId < 0)
            {
                ScanProgressively(ct);
                Host.ReportProgress(this, ProgressCount);
                return;
            }
            var tree = FolderScanner.Scan(RootName, Filter, ct, _progress);
            ct.ThrowIfCancellationRequested();
            Native.TryGetMetadata(RootPath, out _, out _, out long mod, out long created);
            lock (Sync)
            {
                var map = new Dictionary<ChildKey, int>((int)Math.Min(ProgressCount + 16, 50_000_000), ChildKeyComparer.Instance);
                int newRoot = -1;
                Index.BulkAdd(b => newRoot = FolderScanner.AddTree(b, tree, RootName, FileIndex.NoParent, FileAttributes.Directory, mod, created,
                    (p, n, id) => map[new ChildKey(p, n)] = id));
                int old = RootId;
                RootId = newRoot;
                _children = map;
                if (old >= 0 && old != newRoot) Index.Remove(old);
            }
            Host.ReportProgress(this, ProgressCount);
        }
        finally
        {
            _scanning = false;
            Wake();
        }
    }

    /// <summary>
    /// First scan of a root with nothing loaded: add each folder's entries as soon as it is read so results appear
    /// while the scan runs (there is no previous copy to keep searchable, so no swap is needed).
    /// </summary>
    void ScanProgressively(CancellationToken ct)
    {
        Native.TryGetMetadata(RootPath, out _, out _, out long rootMod, out long rootCreated);
        var map = new Dictionary<ChildKey, int>(ChildKeyComparer.Instance);
        int rootId;
        lock (Sync)
        {
            rootId = Index.Add(RootName, FileIndex.NoParent, FileAttributes.Directory, -1, rootMod, rootCreated);
            RootId = rootId;
            _children = map;
        }
        FolderScanner.Scan(RootName, Filter, ct, _progress, dir =>
        {
            if (dir.Entries.Count == 0) return;
            lock (Sync)
            {
                Index.BulkAdd(b =>
                {
                    foreach (var e in dir.Entries)
                    {
                        int id = b.Add(e.Name, dir.IndexId, e.Attributes, e.Size, e.Modified, e.Created);
                        map[new ChildKey(dir.IndexId, e.Name)] = id;
                        if (e.Directory != null) e.Directory.IndexId = id;
                    }
                });
            }
        }, rootId);
        ct.ThrowIfCancellationRequested();
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

    public override void StartMonitoring() => EnsureWatcher();

    void EnsureWatcher()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_thread == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _thread = new Thread(() => ProcessLoop(token)) { IsBackground = true, Name = "Rock Preview folder monitor " + RootPath };
                _thread.Start();
            }
            if (_watcher != null) return;
            try
            {
                var w = new FileSystemWatcher(RootPath)
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
                _watcher = w;
            }
            catch
            {
                // Not watchable (e.g. some network shares): index stays static until the next rescan.
            }
        }
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
        // Buffer overflow (or a lost watch): events were dropped, so the only safe recovery is a rescan.
        _events.Clear();
        Host.RequestRescan(this, ex is InternalBufferOverflowException ? "change buffer overflow" : ex.Message);
    }

    public override void StopMonitoring()
    {
        Thread? t;
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _cts?.Cancel();
            t = _thread;
            _thread = null;
        }
        if (t != null && t != Thread.CurrentThread) t.Join(5000);
        _cts?.Dispose();
        _cts = null;
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
            ProcessPending();
        }
    }

    /// <summary>Applies all queued watcher events (also called by tests through the manager).</summary>
    internal void ProcessPending()
    {
        if (_events.IsEmpty) return;
        bool any = false;
        lock (Sync)
        {
            if (RootId < 0 || !Index.IsAlive(RootId)) { _events.Clear(); return; }
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_events.TryDequeue(out var e))
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* best effort per event */ }
            }
        }
        if (any) Host.MarkChanged();
    }

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
        if (!Native.TryGetMetadata(path, out var attrs, out long size, out long mod, out long created)) return false;
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
                Index.Update(existing, attrs, size, mod, created);
                return true;
            }
        }
        if (isDir && (attrs & FileAttributes.ReparsePoint) == 0)
        {
            // A folder that appears (created or moved in) may already have content.
            var tree = FolderScanner.Scan(path, Filter, CancellationToken.None);
            var map = _children;
            Index.BulkAdd(b => FolderScanner.AddTree(b, tree, name, parentId, attrs, mod, created, (p, n, id) => map[new ChildKey(p, n)] = id));
        }
        else
        {
            int id = Index.Add(name, parentId, attrs, isDir ? -1 : size, mod, created);
            _children[key] = id;
        }
        return true;
    }

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
        return true;
    }
}
