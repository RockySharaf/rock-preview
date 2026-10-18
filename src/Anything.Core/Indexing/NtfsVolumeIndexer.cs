using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>
/// Indexes an NTFS volume from its raw MFT (falling back to FSCTL_ENUM_USN_DATA) and keeps it current by
/// tailing the USN change journal. Keeps a record-number → index-id map for applying journal records.
/// </summary>
internal sealed unsafe class NtfsVolumeIndexer : RootIndexer
{
    const int ErrorInvalidParameter = 87;

    SafeFileHandle? _volume;
    RecordMap _map = new();
    ulong _journalId;
    long _nextUsn;
    bool _hasJournal;
    readonly StrongBox<long> _progress = new();
    readonly HashSet<long> _dirty = [];
    readonly Dictionary<long, (long Parent, string Name)> _pendingOld = [];
    readonly List<int> _ids = [];
    readonly object _gate = new();
    Thread? _thread;
    CancellationTokenSource? _cts;
    volatile uint _nativeThreadId;
    Timer? _flushTimer;
    int _flushing;
    long _lastScanTicks;

    public NtfsVolumeIndexer(IIndexerHost host, string drive) : base(host, drive) { }

    public override RootKind Kind => RootKind.Ntfs;
    public long ProgressCount => Interlocked.Read(ref _progress.Value);
    /// <summary>"MFT" or "USN" depending on how the last scan enumerated the volume.</summary>
    public string ScanMethod { get; private set; } = "";

    /// <summary>Opens \\.\X: for reading (requires administrator).</summary>
    public bool TryOpen()
    {
        var h = Native.CreateFile(@"\\.\" + RootName, Native.GENERIC_READ, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) { h.Dispose(); return false; }
        _volume = h;
        return true;
    }

    public override (ulong JournalId, long NextUsn) JournalPosition => (_journalId, _nextUsn);

    public override long[]? BuildIdToFrn(int indexCount) => _map.BuildInverse(indexCount);

    public override void Remap(int[] remap)
    {
        base.Remap(remap);
        _map.Remap(remap);
    }

    public override bool AttachLoaded(LoadedRoot loaded)
    {
        lock (Sync)
        {
            RootId = loaded.RootId;
            if (loaded.Kind != RootKind.Ntfs || loaded.Frns == null) return false;
            var map = new RecordMap(loaded.Ids.Length);
            for (int i = 0; i < loaded.Ids.Length; i++)
                if (loaded.Frns[i] >= 0) map.Add(loaded.Frns[i], loaded.Ids[i]);
            _map = map;
            _journalId = loaded.JournalId;
            _nextUsn = loaded.NextUsn;
        }
        if (_volume == null || !UsnJournal.TryQuery(_volume, out var info, out _)) return false;
        // Current when the journal is the same one and still holds every record since the saved position.
        bool current = info.JournalId == loaded.JournalId && loaded.NextUsn >= info.FirstUsn && loaded.NextUsn <= info.NextUsn;
        _hasJournal = current;
        return current;
    }

    public override void Scan(CancellationToken ct)
    {
        StopMonitoring();
        if (_volume == null) throw new InvalidOperationException("volume not open");
        // Capture the journal position *before* reading the MFT so changes made during the scan are replayed.
        _hasJournal = UsnJournal.TryQueryOrCreate(_volume, out var info);
        long startUsn = _hasJournal ? info.NextUsn : 0;
        Interlocked.Exchange(ref _progress.Value, 0);
        Host.ReportProgress(this, 0);

        VolumeRecords records;
        try
        {
            records = MftReader.Read(_volume, ct, n => { Interlocked.Exchange(ref _progress.Value, n); });
            ScanMethod = "MFT";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            Interlocked.Exchange(ref _progress.Value, 0);
            records = UsnJournal.Enumerate(_volume, startUsn, ct, n => { Interlocked.Exchange(ref _progress.Value, n); });
            ScanMethod = "USN";
        }
        ct.ThrowIfCancellationRequested();

        lock (Sync)
        {
            var (rootId, map, count) = records.BuildInto(Index, RootName, Filter, n => Interlocked.Exchange(ref _progress.Value, n));
            int old = RootId;
            RootId = rootId;
            _map = map;
            _journalId = info.JournalId;
            _nextUsn = startUsn;
            _dirty.Clear();
            _pendingOld.Clear();
            if (old >= 0 && old != rootId) Index.Remove(old);
            Interlocked.Exchange(ref _progress.Value, count);
            _lastScanTicks = Environment.TickCount64;
        }
        Host.ReportProgress(this, ProgressCount);
    }

    public override void StartMonitoring()
    {
        lock (_gate)
        {
            if (!_hasJournal || _volume == null || _thread != null || RootId < 0) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => MonitorLoop(token)) { IsBackground = true, Name = "Rock Preview USN monitor " + RootName };
            _thread.Start();
            _flushTimer = new Timer(_ => FlushDirty(), null, 250, 250);
        }
    }

    public override void StopMonitoring()
    {
        Thread? t;
        lock (_gate)
        {
            _cts?.Cancel();
            t = _thread;
            _thread = null;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        if (t != null && t != Thread.CurrentThread)
        {
            // The journal read may block; cancel it.
            for (int i = 0; i < 50 && !t.Join(100); i++)
            {
                uint tid = _nativeThreadId;
                if (tid == 0) continue;
                nint th = Native.OpenThread(Native.THREAD_TERMINATE, 0, tid);
                if (th != 0) { Native.CancelSynchronousIo(th); Native.CloseHandle(th); }
            }
        }
        _cts?.Dispose();
        _cts = null;
        FlushDirty();
    }

    public override void Dispose()
    {
        StopMonitoring();
        _volume?.Dispose();
        _volume = null;
    }

    void MonitorLoop(CancellationToken ct)
    {
        _nativeThreadId = Native.GetCurrentThreadId();
        const int Size = 1 << 16;
        byte* buf = (byte*)NativeMemory.Alloc(Size);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long start; ulong jid;
                lock (Sync) { start = _nextUsn; jid = _journalId; }
                var volume = _volume;
                if (volume == null) return;
                int ret = UsnJournal.Read(volume, jid, start, buf, Size);
                if (ct.IsCancellationRequested) return;
                if (ret < 0)
                {
                    int err = -ret;
                    if (err is Native.ERROR_JOURNAL_ENTRY_DELETED or Native.ERROR_JOURNAL_NOT_ACTIVE
                        or Native.ERROR_JOURNAL_DELETE_IN_PROGRESS or ErrorInvalidParameter)
                    {
                        _hasJournal = false;
                        // A journal that fails right after a fresh scan would loop rescans forever: stop monitoring instead.
                        if (_lastScanTicks != 0 && Environment.TickCount64 - _lastScanTicks < 60_000) return;
                        Host.RequestRescan(this, "USN journal out of date");
                        return;
                    }
                    if (err == Native.ERROR_OPERATION_ABORTED) continue;
                    ct.WaitHandle.WaitOne(5000); // volume temporarily unavailable: retry
                    continue;
                }
                if (ret < 8) { ct.WaitHandle.WaitOne(250); continue; }
                long next = *(long*)buf;
                bool changed = false;
                lock (Sync)
                {
                    var span = new ReadOnlySpan<byte>(buf + 8, ret - 8);
                    while (span.Length > 0)
                    {
                        if (!UsnRecordParser.TryParse(span, out var r, out int len) || len <= 0) break;
                        if (r.Name != null)
                        {
                            try { changed |= Apply(r); }
                            catch (Exception ex) when (ex is not OutOfMemoryException) { }
                        }
                        span = span[len..];
                    }
                    _nextUsn = next;
                }
                if (changed) Host.MarkChanged();
                if (ret == 8 || next == start) ct.WaitHandle.WaitOne(250);
            }
        }
        finally
        {
            _nativeThreadId = 0;
            NativeMemory.Free(buf);
        }
    }

    /// <summary>Applies one journal record (called with <see cref="RootIndexer.Sync"/> held). Returns true if the index changed.</summary>
    bool Apply(in UsnRecord r)
    {
        long rec = r.Record;
        if (rec < VolumeRecords.FirstUserRecord) return false; // metafiles and the root itself
        var reason = r.Reason;

        if ((reason & UsnReason.FileDelete) != 0)
        {
            _map.GetAll(rec, _ids);
            _dirty.Remove(rec);
            _pendingOld.Remove(rec);
            if (_ids.Count == 0) return false;
            foreach (int id in _ids) if (id != RootId) Index.Remove(id);
            _map.RemoveAll(rec);
            return true;
        }
        if ((reason & UsnReason.RenameNewName) != 0) return ApplyRename(r);
        if ((reason & UsnReason.RenameOldName) != 0)
        {
            _pendingOld[rec] = (r.ParentRecord, r.Name);
            return false;
        }
        if ((reason & UsnReason.HardLinkChange) != 0) return ApplyHardLink(r);

        int existing = FindLink(rec, r.ParentRecord, r.Name);
        if (existing < 0) return TryAdd(r, enumerateChildren: (reason & UsnReason.FileCreate) == 0);
        if ((reason & UsnReason.MetadataChanged) != 0) _dirty.Add(rec);
        return false;
    }

    /// <summary>The live id of <paramref name="rec"/> whose parent/name match, else its first live id, else -1.</summary>
    int FindLink(long rec, long parentRec, string name, bool exact = false)
    {
        _map.GetAll(rec, _ids);
        int parentId = _map.Get(parentRec);
        int fallback = -1;
        foreach (int id in _ids)
        {
            if (!Index.IsAlive(id)) continue;
            if (Index.Parent(id) == parentId && string.Equals(Index.Name(id), name, StringComparison.OrdinalIgnoreCase)) return id;
            if (fallback < 0) fallback = id;
        }
        return exact ? -1 : fallback;
    }

    int LiveParent(long parentRec)
    {
        int parentId = _map.Get(parentRec);
        return parentId >= 0 && Index.IsAlive(parentId) && Index.IsDirectory(parentId) ? parentId : -1;
    }

    bool TryAdd(in UsnRecord r, bool enumerateChildren)
    {
        int parentId = LiveParent(r.ParentRecord);
        if (parentId < 0) return false; // parent unknown or excluded
        var attrs = r.Attributes;
        bool isDir = (attrs & FileAttributes.Directory) != 0;
        string path = ExclusionFilter.Join(Index.GetFullPath(parentId), r.Name);
        if (Filter.Excludes(r.Name, attrs, isDir ? path : null)) return false;
        bool created = (r.Reason & UsnReason.FileCreate) != 0;
        int id = Index.Add(r.Name, parentId, attrs, isDir ? -1 : 0, r.TimeStamp, created ? r.TimeStamp : 0);
        _map.Add(r.Record, id);
        _dirty.Add(r.Record); // fetch real size/dates on the next flush
        if (isDir && enumerateChildren && (attrs & FileAttributes.ReparsePoint) == 0) AddChildren(path, id);
        return true;
    }

    bool ApplyRename(in UsnRecord r)
    {
        long rec = r.Record;
        int id = _pendingOld.Remove(rec, out var old) ? FindLink(rec, old.Parent, old.Name) : FindLink(rec, r.ParentRecord, r.Name);
        if (id < 0 || id == RootId) return TryAdd(r, enumerateChildren: true); // moved in from an unindexed/excluded place
        int newParent = LiveParent(r.ParentRecord);
        bool isDir = Index.IsDirectory(id);
        if (newParent < 0 || (isDir && IsSelfOrAncestor(id, newParent))
            || Filter.Excludes(r.Name, Index.Attributes(id), isDir ? ExclusionFilter.Join(Index.GetFullPath(newParent), r.Name) : null))
        {
            Index.Remove(id);
            _map.RemoveId(rec, id);
            return true;
        }
        Index.Rename(id, r.Name, newParent);
        return true;
    }

    bool IsSelfOrAncestor(int ancestor, int id)
    {
        for (int cur = id, guard = 0; cur != FileIndex.NoParent && guard < 4096; cur = Index.Parent(cur), guard++)
            if (cur == ancestor) return true;
        return false;
    }

    bool ApplyHardLink(in UsnRecord r)
    {
        int parentId = LiveParent(r.ParentRecord);
        if (parentId < 0) return false;
        int existing = FindLink(r.Record, r.ParentRecord, r.Name, exact: true);
        string path = ExclusionFilter.Join(Index.GetFullPath(parentId), r.Name);
        bool onDisk = Native.TryGetMetadata(path, out var attrs, out long size, out long mod, out long created);
        if (onDisk && existing < 0)
        {
            if (Filter.Excludes(r.Name, attrs, null)) return false;
            int id = Index.Add(r.Name, parentId, attrs, size, mod, created);
            _map.Add(r.Record, id);
            return true;
        }
        if (!onDisk && existing >= 0)
        {
            Index.Remove(existing);
            _map.RemoveId(r.Record, existing);
            return true;
        }
        return false;
    }

    /// <summary>Adds the contents of a folder that moved into the indexed area, with file ids for future journal records.</summary>
    void AddChildren(string dirPath, int dirId)
    {
        const int Size = 64 * 1024;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(Size, 8);
        try
        {
            var stack = new Stack<(string Path, int Id)>();
            stack.Push((dirPath, dirId));
            while (stack.Count > 0)
            {
                var (path, id) = stack.Pop();
                using var h = Native.CreateFile(Native.LongPath(path), Native.FILE_LIST_DIRECTORY, Native.FILE_SHARE_ALL, 0,
                    Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
                if (h.IsInvalid) continue;
                while (Native.GetFileInformationByHandleEx(h, Native.FileIdBothDirectoryInfo, buf, Size))
                {
                    int off = 0;
                    while (true)
                    {
                        byte* e = buf + off;
                        int nextOfs = *(int*)e;
                        var attrs = (FileAttributes)(*(uint*)(e + 56));
                        int nameBytes = *(int*)(e + 60);
                        string name = new((char*)(e + 104), 0, nameBytes / 2);
                        if (name is not "." and not "..")
                        {
                            long frn = *(long*)(e + 96) & 0x0000FFFFFFFFFFFF;
                            bool isDir = (attrs & FileAttributes.Directory) != 0;
                            string childPath = ExclusionFilter.Join(path, name);
                            if (!Filter.Excludes(name, attrs, isDir ? childPath : null))
                            {
                                int cid = Index.Add(name, id, attrs, isDir ? -1 : *(long*)(e + 40), *(long*)(e + 24), *(long*)(e + 8));
                                _map.Add(frn, cid);
                                if (isDir && (attrs & FileAttributes.ReparsePoint) == 0) stack.Push((childPath, cid));
                            }
                        }
                        if (nextOfs == 0) break;
                        off += nextOfs;
                    }
                }
            }
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    /// <summary>Re-reads size/dates/attributes of records touched by the journal (coalesced, timer driven).</summary>
    void FlushDirty()
    {
        if (Interlocked.Exchange(ref _flushing, 1) != 0) return;
        bool changed = false;
        try
        {
            lock (Sync)
            {
                if (_dirty.Count == 0) return;
                int budget = 20000;
                var done = new List<long>();
                foreach (long rec in _dirty)
                {
                    if (--budget < 0) break;
                    done.Add(rec);
                    _map.GetAll(rec, _ids);
                    foreach (int id in _ids)
                    {
                        if (id == RootId || !Index.IsAlive(id)) continue;
                        if (!Native.TryGetMetadata(Index.GetFullPath(id), out var attrs, out long size, out long mod, out long created)) continue;
                        if (Filter.ExcludesAttributes(attrs))
                        {
                            Index.Remove(id);
                            _map.RemoveId(rec, id);
                        }
                        else Index.Update(id, attrs, size, mod, created);
                        changed = true;
                    }
                }
                foreach (long rec in done) _dirty.Remove(rec);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { Volatile.Write(ref _flushing, 0); }
        if (changed) Host.MarkChanged();
    }
}
