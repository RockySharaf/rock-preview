using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>
/// Indexes an NTFS volume from its raw MFT (falling back to FSCTL_ENUM_USN_DATA) and keeps it current by
/// tailing the USN change journal. Keeps a record-number → index-id map for applying journal records.
/// The volume is read directly (administrator) or through the Rock Preview service (<see cref="IVolumeSource"/>).
/// </summary>
internal sealed unsafe class NtfsVolumeIndexer : RootIndexer
{
    const int ErrorInvalidParameter = 87;

    IVolumeSource? _volume; // null once released or disposed; changed under _gate
    readonly bool _viaService;
    RecordMap _map = new();
    ulong _journalId;
    long _nextUsn;
    bool _hasJournal;
    readonly StrongBox<long> _progress = new();
    readonly HashSet<long> _dirty = [];
    readonly Dictionary<long, (long Parent, string Name)> _pendingOld = [];
    readonly List<int> _ids = [];
    // Recent changes seen in one batch of journal records, recorded together (Date Recently Changed).
    readonly List<(int Id, long Time)> _recent = [];
    // Recent changes taken from the whole journal once, at the first monitoring after startup (see LoadRecentChanges).
    bool _recentLoaded;

    /// <summary>
    /// Journal reasons that count as a change for Date Recently Changed: all but a delete and a rename's old name. As in
    /// Everything (compared on a real PC), a change of permissions or attributes counts too.
    /// </summary>
    const UsnReason RecentReasons = ~(UsnReason.FileDelete | UsnReason.RenameOldName);
    readonly object _gate = new();
    Thread? _thread;
    CancellationTokenSource? _cts;
    volatile uint _nativeThreadId;
    Timer? _flushTimer;
    int _flushing;
    long _lastScanTicks;

    public NtfsVolumeIndexer(IIndexerHost host, string drive, IVolumeSource source) : base(host, drive)
    {
        _volume = source;
        _viaService = source.Kind == "service";
    }

    public override RootKind Kind => RootKind.Ntfs;
    public override long ProgressCount => Interlocked.Read(ref _progress.Value);
    /// <summary>"MFT" or "USN" depending on how the last scan enumerated the volume.</summary>
    public string ScanMethod { get; private set; } = "";
    public override string Method => ScanMethod.Length > 0 ? ScanMethod : "MFT";
    /// <summary>True when the volume is read through the Rock Preview service.</summary>
    public bool ViaService => _viaService;

    public override (ulong JournalId, long NextUsn) JournalPosition => (_journalId, _nextUsn);

    public override long[]? BuildIdToFrn(int indexCount) => _map.BuildInverse(indexCount);

    public override bool CanReadDateAccessed => _volume?.ProvidesAccessTimes ?? false;

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
            HasDateAccessed = loaded.HasDateAccessed;
        }
        var volume = _volume;
        if (volume == null || !volume.TryQueryJournal(create: false, out var info)) return false;
        // Current when the journal is the same one and still holds every record since the saved position.
        bool current = info.JournalId == loaded.JournalId && loaded.NextUsn >= info.FirstUsn && loaded.NextUsn <= info.NextUsn;
        _hasJournal = current;
        if (current) Status = RootStatus.Ready;
        return current;
    }

    public override void Scan(CancellationToken ct)
    {
        StopMonitoring();
        var volume = _volume ?? throw new InvalidOperationException("volume not open");
        // The index this scan writes into: a scan the manager stopped waiting for must not write into the next one.
        long generation;
        lock (Sync) generation = Index.Generation;
        Status = RootStatus.Scanning;
        try
        {
            // Capture the journal position *before* reading the MFT so changes made during the scan are replayed.
            _hasJournal = volume.TryQueryJournal(create: true, out var info);
            long startUsn = _hasJournal ? info.NextUsn : 0;
            Interlocked.Exchange(ref _progress.Value, 0);
            Host.ReportProgress(this, 0);

            var records = volume.ReadRecords(startUsn, ct, n => { Interlocked.Exchange(ref _progress.Value, n); }, out string method);
            ScanMethod = method;
            ct.ThrowIfCancellationRequested();

            lock (Sync)
            {
                bool Abandoned() => ct.IsCancellationRequested || Index.Generation != generation;
                if (Abandoned()) throw new OperationCanceledException(ct);
                // Checked again under the index's write lock, which Clear takes too.
                var (rootId, map, count) = records.BuildInto(Index, RootName, Filter, n => Interlocked.Exchange(ref _progress.Value, n), Abandoned);
                if (rootId < 0) throw new OperationCanceledException(ct);
                int old = RootId;
                var oldMap = _map;
                RootId = rootId;
                _map = map;
                HasDateAccessed = Index.HasDateAccessed && records.HasAccessTimes;
                if (old >= 0 && old != rootId) CarryRecentChanges(oldMap, map);
                _journalId = info.JournalId;
                _nextUsn = startUsn;
                _dirty.Clear();
                _pendingOld.Clear();
                if (old >= 0 && old != rootId) Index.Remove(old);
                Interlocked.Exchange(ref _progress.Value, count);
                _lastScanTicks = Environment.TickCount64;
                LastScanUtc = DateTime.UtcNow.ToFileTimeUtc();
                Status = RootStatus.Ready;
                Detail = null;
            }
            Host.ReportProgress(this, ProgressCount);
        }
        catch (Exception ex)
        {
            // Back to what the index still holds (the previous scan, or nothing yet); the manager retries.
            Status = RootId >= 0 ? RootStatus.Ready : RootStatus.Waiting;
            if (ex is not OperationCanceledException) Detail = ex.Message;
            throw;
        }
    }

    /// <summary>
    /// A rescan made new entries; what changed before still did: their recent changes go to the new entries of the same
    /// file record (and name, for a file with several hard links).
    /// </summary>
    void CarryRecentChanges(RecordMap from, RecordMap to)
    {
        var recent = Index.RecentChanges();
        if (recent.Length == 0) return;
        var records = from.BuildInverse(Index.Count);
        var moved = new List<(int, long)>();
        var ids = new List<int>();
        foreach (var (id, time) in recent)
        {
            long rec = (uint)id < (uint)records.Length ? records[id] : -1;
            if (rec < 0) continue; // another root's
            to.GetAll(rec, ids);
            int match = ids.Count == 1 ? ids[0] : ids.FirstOrDefault(n => string.Equals(Index.Name(n), Index.Name(id), StringComparison.OrdinalIgnoreCase), -1);
            if (match >= 0) moved.Add((match, time));
        }
        Index.SetRecentChanges(moved);
    }

    public override void StartMonitoring()
    {
        lock (_gate)
        {
            // Released for an eject: a monitor now would reopen the drive (or use its closed handle).
            if (Released || !_hasJournal || _volume == null || _thread != null || RootId < 0) return;
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
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            cts?.Cancel();
            t = _thread;
            _thread = null;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        if (t != null && t != Thread.CurrentThread)
        {
            // Journal reads return at once, so the loop normally ends within one round trip. Through the service, let
            // it finish: cancelling the pipe read would cut the connection, and the service could no longer be asked
            // to close the volume (it keeps it open until it notices, which can veto an eject).
            if (_viaService) t.Join(1500);
            // A read that hangs anyway (a dying drive): cancel it.
            for (int i = 0; i < 50 && !t.Join(100); i++)
            {
                uint tid = _nativeThreadId;
                if (tid == 0) continue;
                nint th = Native.OpenThread(Native.THREAD_TERMINATE, 0, tid);
                if (th != 0) { Native.CancelSynchronousIo(th); Native.CloseHandle(th); }
            }
        }
        // After the join: a loop still running sees a disposed token and exits (MonitorLoop catches that).
        cts?.Dispose();
        FlushDirty();
    }

    /// <summary>Stops the journal monitor and closes the volume (through the service too), so the drive can be ejected.</summary>
    public override void Release()
    {
        IVolumeSource? volume;
        lock (_gate)
        {
            // Under the gate, so a StartMonitoring racing with this either sees Released or is stopped just below.
            Released = true;
            volume = _volume;
            _volume = null;
        }
        StopMonitoring();
        volume?.Dispose();
    }

    /// <summary>
    /// The eject (or volume lock) didn't happen: carries on with <paramref name="source"/>, keeping the entries, the map
    /// and the journal position, so the next <see cref="StartMonitoring"/> replays the journal from where it stopped
    /// (a journal that moved on asks for a rescan, which replaces the entries when done). True: the root owns
    /// <paramref name="source"/> (and disposes it if it was never released). False when there is nothing to carry on
    /// with (no source, or released before its first scan finished): the caller keeps the source.
    /// </summary>
    public override bool Revive(IVolumeSource? source)
    {
        if (source == null) return false;
        IVolumeSource? old;
        lock (_gate)
        {
            if (!Released)
            {
                old = source; // still has its own
            }
            else
            {
                if (RootId < 0) return false;
                old = _volume;
                _volume = source;
                base.Revive(source);
            }
        }
        old?.Dispose();
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        IVolumeSource? volume;
        lock (_gate)
        {
            volume = _volume;
            _volume = null;
        }
        volume?.Dispose();
    }

    void MonitorLoop(CancellationToken ct)
    {
        _nativeThreadId = Native.GetCurrentThreadId();
        const int Size = 1 << 16;
        byte* buf = (byte*)NativeMemory.Alloc(Size);
        try
        {
            if (!_recentLoaded && Host.RecentChangesFromJournal && Index.RecordsRecentChanges)
            {
                _recentLoaded = true;
                LoadRecentChanges(buf, Size, ct);
            }
            while (!ct.IsCancellationRequested)
            {
                long start; ulong jid;
                lock (Sync) { start = _nextUsn; jid = _journalId; }
                var volume = _volume;
                if (volume == null) return;
                int ret = volume.ReadJournal(jid, start, buf, Size);
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
                    if (_recent.Count > 0)
                    {
                        Index.SetRecentChanges(_recent);
                        _recent.Clear();
                        changed = true;
                    }
                }
                if (changed) Host.MarkChanged();
                if (ret == 8 || next == start) ct.WaitHandle.WaitOne(250);
            }
        }
        // The volume was closed for an eject (or the token disposed) while a read was under way: nothing left to watch.
        catch (ObjectDisposedException) { }
        finally
        {
            _nativeThreadId = 0;
            NativeMemory.Free(buf);
        }
    }

    /// <summary>
    /// Reads the journal from its oldest record up to where monitoring starts, for when each file there last changed
    /// (Everything's "load recent changes from USN journal"): Date Recently Changed then reaches back as far as the
    /// journal does, not just to the start of the app. The records aren't applied again: the index is current.
    /// </summary>
    void LoadRecentChanges(byte* buf, int size, CancellationToken ct)
    {
        var volume = _volume;
        if (volume == null || !volume.TryQueryJournal(create: false, out var info)) return;
        long stop, pos;
        ulong jid;
        lock (Sync) { stop = _nextUsn; jid = _journalId; }
        if (info.JournalId != jid) return;
        pos = info.FirstUsn;
        // Per link (record, folder, name): a record names one hard link, and only that one changed as far as Everything says.
        var latest = new Dictionary<(long Rec, long Parent, string Name), long>();
        while (pos < stop && !ct.IsCancellationRequested)
        {
            int ret = volume.ReadJournal(jid, pos, buf, size);
            if (ret < 8) break;
            long next = *(long*)buf;
            var span = new ReadOnlySpan<byte>(buf + 8, ret - 8);
            while (span.Length > 0)
            {
                if (!UsnRecordParser.TryParse(span, out var r, out int len) || len <= 0) break;
                if (r.Usn >= stop) { next = stop; break; }
                if (r.Name != null && (r.Reason & RecentReasons) != 0 && (r.Reason & UsnReason.FileDelete) == 0)
                {
                    var key = (r.Record, r.ParentRecord, r.Name);
                    if (!latest.TryGetValue(key, out long had) || r.TimeStamp > had) latest[key] = r.TimeStamp;
                }
                span = span[len..];
            }
            if (next <= pos) break;
            pos = next;
        }
        if (ct.IsCancellationRequested || latest.Count == 0) return;
        lock (Sync)
        {
            var found = new List<(int, long)>(latest.Count);
            foreach (var ((rec, parent, name), time) in latest)
            {
                int id = FindLink(rec, parent, name, exact: true);
                if (id >= 0 && id != RootId) found.Add((id, time));
            }
            Index.SetRecentChanges(found);
        }
        Host.MarkChanged();
    }

    /// <summary>Applies one journal record (called with <see cref="RootIndexer.Sync"/> held). Returns true if the index changed.</summary>
    bool Apply(in UsnRecord r)
    {
        bool changed = ApplyChange(r);
        // Date Recently Changed: whatever the journal says changed, at the journal's time (catching up at startup too).
        // Only the link the record names: another hard link of the file isn't listed as changed (as in Everything).
        if (Index.RecordsRecentChanges && (r.Reason & RecentReasons) != 0 && (r.Reason & UsnReason.FileDelete) == 0)
        {
            int id = FindLink(r.Record, r.ParentRecord, r.Name, exact: true);
            if (id >= 0 && id != RootId) _recent.Add((id, r.TimeStamp));
        }
        return changed;
    }

    bool ApplyChange(in UsnRecord r)
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
        int id = Index.Add(r.Name, parentId, attrs, isDir ? -1 : 0, r.TimeStamp, created ? r.TimeStamp : 0, r.TimeStamp);
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
        bool onDisk = Native.TryGetMetadata(path, out var attrs, out long size, out long mod, out long created, out long accessed);
        if (onDisk && existing < 0)
        {
            if (Filter.Excludes(r.Name, attrs, null)) return false;
            int id = Index.Add(r.Name, parentId, attrs, size, mod, created, accessed);
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
                                // FILE_ID_BOTH_DIR_INFO: created at 8, accessed at 16, written at 24, size at 40.
                                int cid = Index.Add(name, id, attrs, isDir ? -1 : *(long*)(e + 40), *(long*)(e + 24), *(long*)(e + 8), *(long*)(e + 16));
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
        // A drive being ejected isn't touched again: each re-read opens a file on it.
        if (Released) return;
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
                        if (!Native.TryGetMetadata(Index.GetFullPath(id), out var attrs, out long size, out long mod, out long created, out long accessed)) continue;
                        if (Filter.ExcludesAttributes(attrs))
                        {
                            Index.Remove(id);
                            _map.RemoveId(rec, id);
                        }
                        else Index.Update(id, attrs, size, mod, created, accessed);
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
