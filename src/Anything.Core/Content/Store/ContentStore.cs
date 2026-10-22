using System.Collections.Concurrent;

namespace Anything.Core.Content.Store;

/// <summary>
/// The text index files: segments, manifest, commits and merges. Never overwrites a manifest it couldn't read, and never
/// writes before it has read. A read-only open (the CLI) never deletes, sweeps or rewrites anything; only the writer cleans
/// up, after taking Content\writer.lock (FileShare.None, deleted on close; a second writer opens read-only).
/// <para>
/// Segment numbers are never reused and no two views share a commit generation: every store of the process, on disk or in
/// memory, counts on from a process-wide high-water mark, so a store opened again after DeleteAll, or after its folder was
/// deleted, never brings back a number a long-lived cache (DocSetCache, by segment id) has seen. In the folder the next
/// number is max(persisted, highest on disk, process) + 1; DeleteAll leaves an empty manifest that keeps the counters, and
/// a brand-new folder starts counting at the seconds since 2026-01-01, so even a recreated folder doesn't take the name of
/// a file another process may still hold. A view's generation is therefore at least its manifest's, not always equal.
/// </para>
/// <para>
/// Folder layout: manifest (the commit point), manifest.prev (the commit before, kept loadable), seg-XXXXXXXX.rpc
/// (immutable segments), writer.lock, tmp\ (spill files, deleted on close). Opening loads manifest, else manifest.prev,
/// else rebuilds the segment list from every segment that reads back (the newest segment wins per key; lost deletions are
/// redone by reconcile). Segments that fail their checks are left out of views and dropped at the next commit.
/// </para>
/// <para>
/// A file that can't be read now (in use without read sharing, access denied, a share that doesn't answer) is never taken
/// for damage. After a few retries the store opens read-only for the session (<see cref="IsUnavailable"/>), deletes and
/// writes nothing, and the caller opens it again later. Only a manifest that reads but fails its checks falls back to
/// manifest.prev, and only a segment that is gone or reads but fails its checks is dropped.
/// </para>
/// <para>
/// A commit sorts the batch by key, writes seg-N.rpc.tmp in one pass, flushes and renames it, then writes manifest.tmp,
/// flushes and replaces manifest (the old one becomes manifest.prev), deletes segment files neither manifest references
/// (or once their last view is released), and publishes the new view. Views are ref-counted: Acquire, AddRef and
/// StoreChange.View each hand the caller its own lease, which gives its reference back once however often it is disposed.
/// One writer thread: one batch at a time, and merges and re-keys only between batches.
/// </para>
/// </summary>
internal sealed class ContentStore : IContentStore
{
    const string ManifestName = "manifest", PrevName = "manifest.prev", TmpName = "manifest.tmp", LockName = "writer.lock", SpillName = "tmp";
    /// <summary>How long DeleteAll waits for searches to release their views before deleting anyway.</summary>
    internal static TimeSpan LeaseWait { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// The most live documents a plain commit rewrites a segment for when it copies 30% or more of them away (well under a
    /// second, with no cancellation; the follower's 2,000-document commits never reach past it). Larger re-keys go through
    /// <see cref="Rekey"/>, which runs under ct, pace and the merge gates.
    /// </summary>
    internal const int RewriteInCommitDocs = 8192;
    /// <summary>Live documents above which Rekey waits for allowLarge, as a large merge does (tests lower it).</summary>
    internal static int LargeRekeyDocs { get; set; } = MergePolicy.LargeDocs;

    /// <summary>The highest segment number and commit generation any store of this process has used (Interlocked).</summary>
    static long s_segmentId, s_generation;

    readonly string? _folder;
    readonly object _gate = new(), _volumeGate = new();
    readonly ConcurrentDictionary<uint, SegmentReader> _mapped = new();
    readonly ConcurrentDictionary<uint, byte> _retired = new();
    readonly HashSet<uint> _failed = [];
    /// <summary>Segment files DeleteAll couldn't delete yet (FinishDeleting retries only these).</summary>
    readonly HashSet<uint> _deleting = [];
    HashSet<uint> _currentIds = [], _prevIds = [];
    FileStream? _lock;
    bool _readOnly, _newer, _disposed, _manifestOnDisk, _dirty, _unavailable;
    /// <summary>The manifest and manifest.prev from before DeleteAll are still to go.</summary>
    bool _deleteManifest, _deletePrev;
    StoreView _current = StoreView.Empty(0);
    List<VolumeEntry> _volumes = [];
    ContentMode _mode;
    /// <summary>The next segment number this folder may take (a long, so max + 1 can't wrap round to 0).</summary>
    long _nextId = 1;
    long _generation;
    Batch? _batch;

    ContentStore(string? folder, bool readOnly)
    {
        _folder = folder == null ? null : Path.GetFullPath(folder);
        _readOnly = readOnly;
    }

    /// <summary>Opens or creates the folder; never throws for bad data (damaged parts are dropped); null folder = in memory.</summary>
    public static ContentStore Open(string? folder, bool readOnly = false)
    {
        var store = new ContentStore(folder, readOnly);
        store.Load();
        return store;
    }

    /// <summary>The manifest was written by a newer version (the store stays read-only and off).</summary>
    public bool IsNewerFormat => _newer;

    public bool IsReadOnly => _readOnly || _newer || _disposed;

    /// <summary>
    /// The writer couldn't read or lock everything for now (a file in use without read sharing, access denied, a share that
    /// didn't answer, another writer): it opened read-only for this session, deleted and wrote nothing, and what it shows
    /// may be a commit old or leave a segment out. Open the folder again later. Also set for a reader that met such a file.
    /// </summary>
    public bool IsUnavailable => _unavailable;

    /// <summary>The folder, or null when in memory.</summary>
    internal string? Folder => _folder;

    /// <summary>The commit generation of the current view.</summary>
    internal long Generation => Volatile.Read(ref _current).CommitGeneration;

    /// <summary>The id the next segment gets (at least; another store of the process may take numbers first).</summary>
    internal uint NextSegmentId => (uint)Math.Min(uint.MaxValue, Math.Max(_nextId, Volatile.Read(ref s_segmentId) + 1));

    /// <summary>The next number as the manifest keeps it.</summary>
    uint PersistedNextId => (uint)Math.Min(uint.MaxValue, _nextId);

    /// <summary>
    /// Some segment failed its checks or is gone (it is dropped at the next commit, which may be an otherwise empty one).
    /// Never true for a read-only store, nor for a segment that was only unavailable.
    /// </summary>
    public bool HasDamage
    {
        get
        {
            lock (_gate) return !IsReadOnly && (_failed.Count > 0 || _current.Readers.Any(r => r.IsDamaged));
        }
    }

    public long BytesOnDisk
    {
        get
        {
            var view = Retain();
            try
            {
                long n = 0;
                foreach (var r in view.Readers) n += r.Bytes;
                if (_folder != null && _manifestOnDisk) n += TryLength(Path.Combine(_folder, ManifestName)) + TryLength(Path.Combine(_folder, PrevName));
                return n;
            }
            finally
            {
                view.Release();
            }
        }
    }

    static long TryLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The current view as the caller's own lease (the caller disposes it).</summary>
    public IStoreView Acquire() => new StoreLease(Retain());

    /// <summary>
    /// The current view with one more reference for the store's own use (Release it). The store gives up its reference
    /// on a view only after it swapped in the next one, so a refusal means "read again"; refusing twice on the same view
    /// would mean it was let go without a successor, which throws instead of spinning.
    /// </summary>
    StoreView Retain()
    {
        StoreView? refused = null;
        for (var spin = new SpinWait(); ; spin.SpinOnce())
        {
            var view = Volatile.Read(ref _current);
            if (view.TryRetain()) return view;
            if (ReferenceEquals(view, refused)) throw new ObjectDisposedException(nameof(ContentStore), "The store's view was released.");
            refused = view;
        }
    }

    static StoreLease LeaseOf(StoreView view) => view.Lease() ?? throw new ObjectDisposedException(nameof(ContentStore), "The store's view was released.");

    /// <summary>A number above every one of its kind this process has handed out, and at least atLeast; it becomes the mark.</summary>
    static long Raise(ref long highWater, long atLeast)
    {
        for (long last = Volatile.Read(ref highWater); ; last = Volatile.Read(ref highWater))
        {
            long next = Math.Max(atLeast, last + 1);
            if (Interlocked.CompareExchange(ref highWater, next, last) == last) return next;
        }
    }

    /// <summary>Notes numbers a folder already used, so no store of this process hands them out again.</summary>
    static void Note(ref long highWater, long used)
    {
        for (long last = Volatile.Read(ref highWater); used > last; last = Volatile.Read(ref highWater))
            if (Interlocked.CompareExchange(ref highWater, used, last) == last) return;
    }

    /// <summary>The generation of the next view: above this store's and every other view of the process.</summary>
    long NewGeneration() => Raise(ref s_generation, _generation + 1);

    /// <summary>The next segment number: never one this folder or this process used before (spent even if the build fails).</summary>
    uint TakeSegmentId()
    {
        if (_nextId >= uint.MaxValue) throw new InvalidOperationException("The text index ran out of segment numbers: rebuild it.");
        long id = Raise(ref s_segmentId, _nextId);
        if (id >= uint.MaxValue) throw new InvalidOperationException("The text index ran out of segment numbers: rebuild it.");
        _nextId = id + 1;
        return (uint)id;
    }

    /// <summary>Where a brand-new folder starts counting: the seconds since 2026-01-01.</summary>
    static long FreshStart() => (long)Math.Max(1, (DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);

    /// <summary>The mode recorded in the manifest (what the index was built for); NamesOnly when none is recorded.</summary>
    public ContentMode RecordedMode => _mode;

    /// <summary>Records the mode the index is built for; persisted with the next commit's manifest.</summary>
    public void RecordMode(ContentMode mode)
    {
        lock (_gate)
        {
            ThrowIfNotWritable();
            if (_mode == mode) return;
            _mode = mode;
            _dirty = true;
        }
    }

    /// <summary>The volume table as it stands now (with entries not committed yet).</summary>
    internal IReadOnlyList<VolumeEntry> VolumesNow()
    {
        lock (_volumeGate) return [.. _volumes];
    }

    /// <summary>The index of a volume key, added when new (persisted at the next commit); a changed display root is updated.</summary>
    public ushort VolumeIndex(string volumeKey, string displayRoot)
    {
        ArgumentNullException.ThrowIfNull(volumeKey);
        displayRoot ??= "";
        lock (_volumeGate)
        {
            for (int i = 0; i < _volumes.Count; i++)
            {
                var v = _volumes[i];
                if (!string.Equals(v.Key, volumeKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (v.DisplayRoot != displayRoot && !IsReadOnly)
                {
                    _volumes[i] = v with { DisplayRoot = displayRoot };
                    _dirty = true;
                }
                return v.Index;
            }
            if (IsReadOnly) throw new InvalidOperationException("The text index is read-only: no volume can be added.");
            if (_volumes.Count >= ushort.MaxValue) throw new InvalidOperationException("The volume table is full.");
            ushort index = (ushort)(_volumes.Count == 0 ? 0 : _volumes.Max(v => v.Index) + 1);
            _volumes.Add(new VolumeEntry(index, volumeKey, displayRoot, DateTime.UtcNow));
            _dirty = true;
            return index;
        }
    }

    /// <summary>Records that a volume was seen (90-day purge of unseen volumes); persisted with the next commit.</summary>
    public void SeenVolume(ushort volume, DateTime utc)
    {
        lock (_volumeGate)
        {
            for (int i = 0; i < _volumes.Count; i++)
            {
                if (_volumes[i].Index != volume) continue;
                var seen = utc.ToUniversalTime();
                if (seen <= _volumes[i].LastSeenUtc) return;
                if (seen - _volumes[i].LastSeenUtc >= TimeSpan.FromDays(1)) _dirty = true;
                _volumes[i] = _volumes[i] with { LastSeenUtc = seen };
                return;
            }
        }
    }

    public IStoreBatch BeginBatch()
    {
        lock (_gate)
        {
            ThrowIfNotWritable();
            if (_batch != null) throw new InvalidOperationException("A batch is already open: commit or dispose it first.");
            // Under the gate the current view can't be swapped, and the store holds its own reference on it.
            if (!_current.TryRetain()) throw new ObjectDisposedException(nameof(ContentStore), "The store's view was released.");
            return _batch = new Batch(this, _current);
        }
    }

    void ThrowIfNotWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly) throw new InvalidOperationException(_newer ? "The text index was made by a newer version." : "The text index is open read-only.");
    }

    // ── Opening ──────────────────────────────────────────────────────────────────────────────────────────────────────

    void Load()
    {
        bool loaded = false;
        if (_folder != null)
        {
            try
            {
                loaded = LoadFolder();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                ReleaseLoaded();
                if (StoreFiles.IsUnavailable(e)) _unavailable = true;
                BecomeReadOnly();
                loaded = false;
            }
        }
        if (!loaded)
        {
            _generation = Raise(ref s_generation, _generation);
            Interlocked.Exchange(ref _current, StoreView.Empty(_generation)).Release();
        }
    }

    /// <summary>Takes the writer lock and loads the folder; false when nothing was published (no folder, or a newer build's).</summary>
    bool LoadFolder()
    {
        string folder = _folder!;
        if (!_readOnly)
        {
            Directory.CreateDirectory(folder);
            try
            {
                _lock = new FileStream(Path.Combine(folder, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _readOnly = true; // another writer has it: search only, and try again later
                _unavailable = true;
            }
        }
        if (!Directory.Exists(folder)) return false;
        bool? loaded = LoadOnce(retryMissing: _readOnly);
        loaded ??= LoadOnce(retryMissing: false); // a reader raced a commit: read the manifest again
        return loaded == true;
    }

    /// <summary>Downgrades to a reader for this session: nothing is deleted or written, and the lock goes.</summary>
    void BecomeReadOnly()
    {
        _readOnly = true;
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>One attempt: true when a view was published, false when not (a newer build's folder), null when a listed segment was missing and retryMissing asks to read the manifest again.</summary>
    bool? LoadOnce(bool retryMissing)
    {
        string folder = _folder!;
        var state = ReadManifest(Path.Combine(folder, ManifestName), out var manifest);
        var prevState = ReadManifest(Path.Combine(folder, PrevName), out var prev);
        if (state == Manifest.ParseResult.Newer || (state != Manifest.ParseResult.Ok && prevState == Manifest.ParseResult.Newer))
        {
            _newer = true; // never write over what a later build made
            return false;
        }
        // A manifest that couldn't be read now may be newer than manifest.prev: this session neither sweeps nor writes.
        bool unavailable = state == Manifest.ParseResult.Unavailable || prevState == Manifest.ParseResult.Unavailable;
        if (unavailable)
        {
            _unavailable = true;
            BecomeReadOnly();
        }
        _manifestOnDisk = state == Manifest.ParseResult.Ok;
        if (state != Manifest.ParseResult.Ok) manifest = prevState == Manifest.ParseResult.Ok ? prev : null;
        _prevIds = prevState == Manifest.ParseResult.Ok ? [.. prev!.Segments.Select(s => s.Id)] : [];

        var onDisk = SegmentFilesOnDisk(folder);
        // Numbers this store never reaches (above 2^31) come only from stray files: they don't count, so they can't use the
        // numbers up; a writer sweeps them.
        uint maxOnDisk = onDisk.Where(id => id <= uint.MaxValue / 2).DefaultIfEmpty(0u).Max();
        if (!_readOnly && state == Manifest.ParseResult.Missing && prevState == Manifest.ParseResult.Missing && onDisk.Count == 0)
        {
            // A brand-new folder (or one deleted and made again): count on from the clock, past any earlier folder's numbers.
            long start = FreshStart();
            _nextId = Math.Max(_nextId, Math.Min(uint.MaxValue / 2, start));
            _generation = Math.Max(_generation, start);
        }

        var readers = new List<SegmentReader>();
        try
        {
            // Without a readable manifest the segments on disk are recovered, but not when one couldn't be read just now:
            // it may hold deletions the segments don't, so this session shows nothing rather than deleted documents.
            return LoadSegments(folder, manifest, recover: !unavailable, onDisk, maxOnDisk, readers, retryMissing) ? true : null;
        }
        catch
        {
            foreach (var r in readers) if (!r.IsReleased) r.Release();
            throw;
        }
    }

    bool LoadSegments(string folder, Manifest? manifest, bool recover, List<uint> onDisk, uint maxOnDisk, List<SegmentReader> readers, bool retryMissing)
    {
        var entries = new List<Manifest.SegmentEntry>();
        if (manifest != null)
        {
            foreach (var entry in manifest.Segments.OrderBy(s => s.Id))
            {
                if (entry.Damaged)
                {
                    _failed.Add(entry.Id);
                    continue;
                }
                var reader = SegmentReader.Open(Path.Combine(folder, SegmentFormat.FileName(entry.Id)), entry.Id, out var failure);
                if (reader == null && failure == SegmentReader.OpenFailure.Missing && retryMissing)
                {
                    foreach (var r in readers) r.Release();
                    readers.Clear();
                    _failed.Clear();
                    return false;
                }
                if (reader == null && failure == SegmentReader.OpenFailure.Unavailable)
                {
                    // In use or denied for now: not damage. Leave it out of this session's view and write nothing, so its
                    // manifest entry and file stay as they are for the next open.
                    _unavailable = true;
                    BecomeReadOnly();
                    continue;
                }
                if (reader == null || reader.DocCount != entry.DocCount)
                {
                    reader?.Release();
                    _failed.Add(entry.Id);
                    continue;
                }
                Track(reader);
                readers.Add(reader);
                entries.Add(entry);
            }
            _currentIds = [.. manifest.Segments.Select(s => s.Id)];
            _generation = Math.Max(_generation, manifest.Generation);
            _mode = manifest.Mode;
            _volumes = [.. manifest.Volumes];
            _nextId = Math.Max(_nextId, Math.Max(manifest.NextSegmentId, maxOnDisk + 1L));
        }
        else
        {
            if (recover)
            {
                // No readable manifest: every segment that reads back, the newest winning per key.
                foreach (uint id in onDisk.Order())
                {
                    var reader = SegmentReader.Open(Path.Combine(folder, SegmentFormat.FileName(id)), id, out var failure);
                    if (reader == null)
                    {
                        if (failure == SegmentReader.OpenFailure.Unavailable)
                        {
                            _unavailable = true; // a writer would sweep it as unreferenced: search only this session
                            BecomeReadOnly();
                        }
                        continue;
                    }
                    Track(reader);
                    readers.Add(reader);
                }
            }
            entries = RecoverEntries(readers);
            _currentIds = [.. readers.Select(r => r.Id)];
            _volumes = RecoverVolumes(readers);
            _nextId = Math.Max(_nextId, maxOnDisk + 1L);
            _dirty = readers.Count > 0; // write the recovered manifest at the next commit
        }

        if (!_readOnly) Sweep(folder, onDisk);
        Note(ref s_segmentId, Math.Min(_nextId - 1, uint.MaxValue / 2)); // a damaged manifest's number stops this folder only
        _generation = Raise(ref s_generation, _generation);
        var view = new StoreView(_generation, readers, entries, _volumes);
        foreach (var r in readers) r.Release(); // the view holds them now
        readers.Clear();
        Interlocked.Exchange(ref _current, view).Release();
        return true;
    }

    /// <summary>Reads a manifest: Missing when there is none, Unavailable when it couldn't be read now (in use, denied: after retries).</summary>
    static Manifest.ParseResult ReadManifest(string path, out Manifest? manifest)
    {
        manifest = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                if (fs.Length > 256L << 20) return Manifest.ParseResult.Bad;
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                return Manifest.Parse(bytes, out manifest);
            }
            catch (Exception e) when (StoreFiles.IsMissing(e))
            {
                return Manifest.ParseResult.Missing;
            }
            catch (Exception e) when (StoreFiles.IsUnavailable(e))
            {
                if (attempt >= StoreFiles.Retries) return Manifest.ParseResult.Unavailable;
                Thread.Sleep(StoreFiles.RetryWait);
            }
        }
    }

    static List<uint> SegmentFilesOnDisk(string folder)
    {
        var ids = new List<uint>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "seg-*"))
                if (SegmentFormat.TryParseFileName(Path.GetFileName(path), out uint id)) ids.Add(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ids;
    }

    /// <summary>Entries for segments found without a manifest: older copies of a key are deleted (the newest segment wins).</summary>
    static List<Manifest.SegmentEntry> RecoverEntries(List<SegmentReader> readers)
    {
        var deleted = new ulong[]?[readers.Count];
        var counts = new int[readers.Count];
        var heap = new PriorityQueue<(int Segment, int Doc), (ulong Key, int Order)>();
        for (int s = 0; s < readers.Count; s++) if (readers[s].DocCount > 0) heap.Enqueue((s, 0), (readers[s].KeyAt(0), -s));
        bool any = false;
        ulong last = 0;
        while (heap.TryDequeue(out var item, out var priority))
        {
            if (item.Doc + 1 < readers[item.Segment].DocCount)
                heap.Enqueue((item.Segment, item.Doc + 1), (readers[item.Segment].KeyAt(item.Doc + 1), -item.Segment));
            if (any && priority.Key == last)
            {
                var bits = deleted[item.Segment] ??= new ulong[(readers[item.Segment].DocCount + 63) >> 6];
                bits[item.Doc >> 6] |= 1UL << item.Doc;
                counts[item.Segment]++;
                continue;
            }
            any = true;
            last = priority.Key;
        }
        var entries = new List<Manifest.SegmentEntry>(readers.Count);
        for (int s = 0; s < readers.Count; s++)
        {
            var stats = new Dictionary<ContentKind, KindStats>();
            if (deleted[s] is { } bits)
                for (int d = 0; d < readers[s].DocCount; d++)
                    if ((bits[d >> 6] & (1UL << d)) != 0) AddDeleted(stats, readers[s], d);
            entries.Add(Entry(readers[s], deleted[s], counts[s], stats));
        }
        return entries;
    }

    static List<VolumeEntry> RecoverVolumes(List<SegmentReader> readers)
    {
        var byIndex = new SortedDictionary<ushort, VolumeEntry>();
        foreach (var r in readers) // ascending id: later segments overwrite
            foreach (var v in r.Volumes) byIndex[v.Index] = v with { LastSeenUtc = DateTime.UtcNow };
        return [.. byIndex.Values];
    }

    static Manifest.SegmentEntry Entry(SegmentReader r, ulong[]? deleted, int deletedCount, IReadOnlyDictionary<ContentKind, KindStats> deletedStats)
        => new()
        {
            Id = r.Id, DocCount = r.DocCount, Bytes = r.Bytes, Tier = (byte)MergePolicy.Tier(r.DocCount - deletedCount),
            Deleted = deletedCount > 0 ? deleted : null, DeletedCount = deletedCount, DeletedStats = deletedStats,
        };

    /// <summary>Adds what a deleted document took from its segment's statistics (exact from the row, averages for the rest).</summary>
    static void AddDeleted(Dictionary<ContentKind, KindStats> into, SegmentReader r, int doc)
    {
        var row = r.Row(doc);
        var (stored, raw) = r.TextLengths(doc);
        var s = r.StatsOf(row.Kind);
        long n = Math.Max(1, s.Docs);
        var one = new KindStats(1, (row.Flags & DocFlags.HasText) != 0 ? 1 : 0, raw, stored, s.Pairs / n, s.BytesRead / n, s.MsTotal / n,
            SegmentFormat.IsProblem(row.Status) ? 1 : 0);
        into[row.Kind] = KindStatsMath.Add(into.GetValueOrDefault(row.Kind), one);
    }

    void Track(SegmentReader reader)
    {
        if (_folder == null) return;
        _mapped[reader.Id] = reader;
        reader.Released = OnReleased;
    }

    void OnReleased(SegmentReader reader)
    {
        _mapped.TryRemove(new KeyValuePair<uint, SegmentReader>(reader.Id, reader));
        if (!_retired.ContainsKey(reader.Id) || _folder == null) return;
        // Numbers aren't reused, but a lease can outlive its store and the folder may have been made again since: delete
        // only the file this segment was read from.
        if (reader.Path is { } path && File.Exists(path) && !SegmentReader.IsSameFile(path, reader.Fingerprint)) _retired.TryRemove(reader.Id, out _);
        else TryDeleteRetired(reader.Id);
    }

    /// <summary>Marks a segment file for deletion: now when nothing maps it, else when its last view goes.</summary>
    void Retire(uint id)
    {
        if (_folder == null) return;
        _retired[id] = 0;
        if (!_mapped.ContainsKey(id)) TryDeleteRetired(id);
    }

    void TryDeleteRetired(uint id)
    {
        if (_folder == null) return;
        if (TryDelete(Path.Combine(_folder, SegmentFormat.FileName(id)))) _retired.TryRemove(id, out _);
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The writer's clean-up at open: temp files, spill files, and segments neither manifest references.</summary>
    void Sweep(string folder, List<uint> onDisk)
    {
        DeleteTempFiles(folder);
        foreach (uint id in onDisk)
            if (!_currentIds.Contains(id) && !_prevIds.Contains(id)) Retire(id);
    }

    /// <summary>
    /// Deletes the store's own temp files (manifest.tmp, seg-N.rpc.tmp) and spill files (tmp\w*.tmp, then tmp\ if that
    /// empties it), and nothing else: the folder may be one the user keeps files in, so other names are never touched.
    /// True when all of them are gone.
    /// </summary>
    static bool DeleteTempFiles(string folder)
    {
        bool clean = true;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.tmp"))
                if (IsOwnTempName(Path.GetFileName(path))) clean &= TryDelete(path);
            string spill = Path.Combine(folder, SpillName);
            if (Directory.Exists(spill))
            {
                foreach (var path in Directory.EnumerateFiles(spill, "w*.tmp")) clean &= TryDelete(path);
                if (!Directory.EnumerateFileSystemEntries(spill).Any()) Directory.Delete(spill);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { clean = false; }
        return clean;
    }

    /// <summary>manifest.tmp or a segment's seg-N.rpc.tmp.</summary>
    static bool IsOwnTempName(string name)
        => name.Equals(TmpName, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && SegmentFormat.TryParseFileName(name.AsSpan(0, name.Length - 4), out _);

    /// <summary>The crash marker the follower keeps beside the store (Content\reading).</summary>
    internal const string MarkerName = "reading";

    /// <summary>
    /// Whether a folder holds a text index: a manifest, manifest.prev or a segment file, or nothing but what an index
    /// leaves before its first commit (its crash marker, writer lock and temp files). A folder that merely happens to be
    /// called Content (beside a moved name database) doesn't, and is never opened to be deleted.
    /// </summary>
    internal static bool HoldsIndex(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            if (File.Exists(Path.Combine(folder, ManifestName)) || File.Exists(Path.Combine(folder, PrevName))) return true;
            foreach (var path in Directory.EnumerateFiles(folder, "seg-*"))
                if (SegmentFormat.TryParseFileName(Path.GetFileName(path), out _)) return true;
            return OnlyOwnLeftovers(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The folder holds something, and only a crash marker (by its header), the writer lock, temp and spill files.</summary>
    static bool OnlyOwnLeftovers(string folder)
    {
        bool any = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            any = true;
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (!name.Equals(SpillName, StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var spill in Directory.EnumerateFileSystemEntries(entry))
                    if (Directory.Exists(spill) || !Path.GetFileName(spill).StartsWith('w') || !spill.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (name.Equals(LockName, StringComparison.OrdinalIgnoreCase) || IsOwnTempName(name)) continue;
            if (!name.Equals(MarkerName, StringComparison.OrdinalIgnoreCase) || !IsMarkerFile(entry)) return false;
        }
        return any;
    }

    /// <summary>A crash marker: empty (made, never written) or starting with its header's magic ("RPCMRK01", CrashMarker's).</summary>
    static bool IsMarkerFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0) return true;
        Span<byte> head = stackalloc byte[8];
        return fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual("RPCMRK01"u8);
    }

    /// <summary>
    /// Deletes a text index's own files from a folder no store holds open (the manifests, segments and their temp files,
    /// the writer lock, the crash marker, spill files), then the folder itself only when nothing else is left in it: files
    /// the user keeps there stay where they are. True when the index's files are all gone.
    /// </summary>
    internal static bool DeleteIndexFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return true;
            bool clean = DeleteTempFiles(folder);
            foreach (var name in (string[])[ManifestName, PrevName, LockName, MarkerName])
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) clean &= TryDelete(path);
            }
            foreach (var path in Directory.EnumerateFiles(folder, "seg-*"))
                if (SegmentFormat.TryParseFileName(Path.GetFileName(path), out _)) clean &= TryDelete(path);
            if (clean && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            return clean;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    void ReleaseLoaded()
    {
        Interlocked.Exchange(ref _current, StoreView.Empty(0)).Release();
        _failed.Clear();
    }

    // ── Commits ──────────────────────────────────────────────────────────────────────────────────────────────────────

    void BatchClosed(Batch batch)
    {
        lock (_gate) if (_batch == batch) _batch = null;
    }

    StoreChange CommitBatch(Batch batch)
    {
        lock (_gate)
        {
            ThrowIfNotWritable();
            if (_batch != batch || !ReferenceEquals(batch.View, _current))
                throw new InvalidOperationException("The store changed since the batch began; begin a new batch.");
            return Apply(_current, batch.Entries, batch.Entries.Count, batch.Deletes, rewriteAlso: -1, CancellationToken.None, null);
        }
    }

    /// <summary>
    /// Commits new documents, copies and deletions against the current view. A segment 30% or more of whose live documents
    /// the copies take away is rewritten once instead (its survivors and the copies go into one new segment): in a plain
    /// commit only up to <see cref="RewriteInCommitDocs"/> live documents, and always the segment at ordinal rewriteAlso
    /// (<see cref="Rekey"/>). Each segment rewritten is checked against its CRCs first; one that fails isn't (it is dropped).
    /// </summary>
    StoreChange Apply(StoreView view, IReadOnlyList<Batch.Entry> all, int addedCount, IEnumerable<DocRef> explicitDeletes, int rewriteAlso,
        CancellationToken ct, Action<CancellationToken>? pace)
    {
        // The documents by key; a key added twice keeps its last addition.
        var order = new int[all.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => all[a].Row.Key != all[b].Row.Key ? all[a].Row.Key.CompareTo(all[b].Row.Key) : a.CompareTo(b));
        var kept = new List<Batch.Entry>(order.Length);
        for (int i = 0; i < order.Length; i++)
            if (i + 1 == order.Length || all[order[i + 1]].Row.Key != all[order[i]].Row.Key) kept.Add(all[order[i]]);
        var localOfAdded = new int[addedCount];
        Array.Fill(localOfAdded, -1);

        // Deletions: the explicit ones, and every live document whose key is added again.
        var deletes = new Dictionary<int, List<int>>();
        void Delete(DocRef d)
        {
            if (view.IsDeleted(d)) return;
            if (!deletes.TryGetValue(d.Segment, out var list)) deletes[d.Segment] = list = [];
            list.Add(d.Doc);
        }
        foreach (var d in explicitDeletes) Delete(d);
        foreach (var e in kept) if (view.Find(e.Row.Key) is { } old) Delete(old);
        foreach (var list in deletes.Values) { list.Sort(); RemoveRepeats(list); }

        // A re-key that copies 30% or more of a segment's live documents away rewrites that segment once, here.
        var rewrite = new HashSet<int>();
        var copies = new Dictionary<int, int>();
        foreach (var e in kept)
            if (e.Doc == null && deletes.TryGetValue(e.Source.Segment, out var gone) && gone.BinarySearch(e.Source.Doc) >= 0)
                copies[e.Source.Segment] = copies.GetValueOrDefault(e.Source.Segment) + 1;
        foreach (var (s, n) in copies)
        {
            int live = view.Entries[s].DocCount - view.Entries[s].DeletedCount;
            if (live > 0 && n >= MergePolicy.DeletedShare * live && live <= RewriteInCommitDocs) rewrite.Add(s);
        }
        if (rewriteAlso >= 0) rewrite.Add(rewriteAlso);
        rewrite.RemoveWhere(s => view.Readers[s].IsDamaged || !view.Readers[s].Verify(ct));

        bool fullyDeleted = false;
        for (int s = 0; s < view.Readers.Count; s++)
            if (view.Entries[s].DocCount - view.Entries[s].DeletedCount - (deletes.TryGetValue(s, out var l) ? l.Count : 0) <= 0) fullyDeleted = true;
        bool damaged = _failed.Count > 0 || view.Readers.Any(r => r.IsDamaged);
        if (kept.Count == 0 && deletes.Count == 0 && rewrite.Count == 0 && !_dirty && !damaged && !fullyDeleted)
            return new StoreChange(LeaseOf(view), null, localOfAdded, [], []);

        // New deletion bitmaps (copy-on-write).
        var newEntries = new Dictionary<int, Manifest.SegmentEntry>();
        foreach (var (s, docs) in deletes)
        {
            var entry = view.Entries[s];
            var reader = view.Readers[s];
            var bits = entry.Deleted is { } had ? (ulong[])had.Clone() : new ulong[(entry.DocCount + 63) >> 6];
            var stats = new Dictionary<ContentKind, KindStats>(entry.DeletedStats);
            foreach (int d in docs)
            {
                bits[d >> 6] |= 1UL << d;
                AddDeleted(stats, reader, d);
            }
            newEntries[s] = Entry(reader, bits, entry.DeletedCount + docs.Count, stats);
        }
        Manifest.SegmentEntry EntryOf(int s) => newEntries.TryGetValue(s, out var e) ? e : view.Entries[s];

        var sources = new List<IBuildSource>();
        if (kept.Count > 0) sources.Add(new BatchSource(kept, view, localOfAdded));
        var rewritten = new List<(int Ordinal, SegmentSource Source)>();
        foreach (int s in rewrite.Order())
        {
            var src = new SegmentSource(view.Readers[s], EntryOf(s));
            rewritten.Add((s, src));
            sources.Add(src);
        }
        return Publish(view, sources, s => rewrite.Contains(s) ? null : EntryOf(s), rewritten, extraDropped: [], ct, pace);
    }

    static void RemoveRepeats(List<int> sorted)
    {
        int k = 0;
        for (int i = 0; i < sorted.Count; i++)
            if (k == 0 || sorted[k - 1] != sorted[i]) sorted[k++] = sorted[i];
        sorted.RemoveRange(k, sorted.Count - k);
    }

    /// <summary>
    /// Builds a segment from the sources (when they yield anything), writes the new manifest and publishes the view.
    /// entryFor gives each current segment's entry in the new manifest (null = it is replaced: rewritten or merged).
    /// </summary>
    StoreChange Publish(StoreView view, List<IBuildSource> sources, Func<int, Manifest.SegmentEntry?> entryFor,
        List<(int Ordinal, SegmentSource Source)> replaced, List<uint> extraDropped, CancellationToken ct, Action<CancellationToken>? pace)
    {
        SegmentReader? built = null;
        uint? newId = null;
        try
        {
            if (sources.Count > 0)
            {
                uint id = TakeSegmentId();
                built = Build(sources, id, ct, pace);
                if (built != null) newId = id;
            }

            var readers = new List<SegmentReader>();
            var entries = new List<Manifest.SegmentEntry>();
            var dropped = new List<uint>(extraDropped);
            for (int s = 0; s < view.Readers.Count; s++)
            {
                var entry = entryFor(s);
                if (entry == null) continue;
                var reader = view.Readers[s];
                if (reader.IsDamaged || entry.DeletedCount >= entry.DocCount)
                {
                    dropped.Add(reader.Id);
                    continue;
                }
                readers.Add(reader);
                entries.Add(entry);
            }
            dropped.AddRange(_failed);
            if (built != null)
            {
                readers.Add(built);
                entries.Add(Entry(built, null, 0, new Dictionary<ContentKind, KindStats>()));
            }

            long generation = NewGeneration();
            var volumes = VolumesNow();
            var next = new StoreView(generation, readers, entries, volumes);
            var manifest = new Manifest
            {
                Generation = generation, CreatedUtc = DateTime.UtcNow.ToFileTimeUtc(), NextSegmentId = PersistedNextId, Mode = _mode,
                Volumes = volumes, Segments = entries, Stats = next.Stats,
            };
            try
            {
                WriteManifest(manifest);
            }
            catch
            {
                next.Release();
                throw;
            }

            _generation = generation;
            _dirty = false;
            _failed.Clear();
            built?.Release(); // the view holds it now
            built = null;
            Interlocked.Exchange(ref _current, next).Release();
            RetireUnreferenced();

            // A replaced (merged or rewritten) segment is gone from the view either way: listed as dropped, and when a new
            // segment took its documents, its remap says where they went (StoreChange's contract; the follower relies on both).
            var remaps = new List<(uint OldSegment, int[] OldToNew)>();
            foreach (var (ordinal, source) in replaced)
            {
                if (newId != null) remaps.Add((view.Readers[ordinal].Id, source.OldToNew));
                dropped.Add(view.Readers[ordinal].Id);
            }
            int[] localOfAdded = sources.OfType<BatchSource>().FirstOrDefault()?.LocalOfAdded ?? [];
            return new StoreChange(LeaseOf(next), newId, localOfAdded, remaps, dropped.Distinct().ToList());
        }
        catch
        {
            if (built != null)
            {
                built.Release();
                Retire(built.Id);
            }
            throw;
        }
    }

    /// <summary>Writes one segment from key-sorted sources (lower index wins a key); null when nothing was written.</summary>
    SegmentReader? Build(List<IBuildSource> sources, uint id, CancellationToken ct, Action<CancellationToken>? pace)
    {
        string? spill = _folder == null ? null : Path.Combine(_folder, SpillName);
        using var writer = new SegmentWriter(ContentTokenizer.Version, spill) { Volumes = VolumesNow() };
        var heap = new PriorityQueue<int, (ulong Key, int Priority)>();
        for (int i = 0; i < sources.Count; i++) if (sources[i].MoveNext()) heap.Enqueue(i, (sources[i].Key, i));
        bool any = false;
        ulong last = 0;
        int n = 0;
        while (heap.TryDequeue(out int i, out var priority))
        {
            var source = sources[i];
            if (any && priority.Key == last) source.Skip();
            else
            {
                source.Emit(writer);
                any = true;
                last = priority.Key;
            }
            if (source.MoveNext()) heap.Enqueue(i, (source.Key, i));
            if ((++n & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
                pace?.Invoke(ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (writer.DocCount == 0) return null;

        if (_folder == null)
        {
            var memory = new MemoryStream();
            writer.Write(memory, id, ct, pace);
            return SegmentReader.Open(memory.ToArray(), id) ?? throw new IOException("A new segment didn't read back.");
        }
        string final = Path.Combine(_folder, SegmentFormat.FileName(id));
        string tmp = final + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                writer.Write(fs, id, ct, pace);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, final, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        var opened = SegmentReader.Open(final, id, out var failure);
        if (opened == null)
        {
            Retire(id); // unreferenced: deleted now, or once whatever holds it lets go
            throw new IOException(failure == SegmentReader.OpenFailure.Unavailable ? "A new segment couldn't be opened (in use)." : "A new segment didn't read back.");
        }
        Track(opened);
        return opened;
    }

    void WriteManifest(Manifest manifest)
    {
        if (_folder == null) return;
        var bytes = manifest.Serialize();
        string path = Path.Combine(_folder, ManifestName), prev = Path.Combine(_folder, PrevName), tmp = Path.Combine(_folder, TmpName);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (_manifestOnDisk && File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, prev, ignoreMetadataErrors: true);
                }
                catch (Exception e) when (e is IOException or PlatformNotSupportedException)
                {
                    File.Move(path, prev, overwrite: true);
                    File.Move(tmp, path);
                }
                _prevIds = _currentIds;
                // manifest.prev is now the manifest just replaced: if that was still the one from before a DeleteAll (it
                // couldn't be written over then), it has to go; else the old manifest.prev was written over.
                _deletePrev = _deleteManifest;
            }
            else File.Move(tmp, path, overwrite: true); // the manifest there was unreadable: leave manifest.prev as it is
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        _manifestOnDisk = true;
        _deleteManifest = false;
        _currentIds = [.. manifest.Segments.Select(s => s.Id)];
    }

    /// <summary>Deletes (or retires) segment files neither manifest references any more, and retries earlier ones.</summary>
    void RetireUnreferenced()
    {
        if (_folder == null) return;
        foreach (uint id in SegmentFilesOnDisk(_folder))
            if (!_currentIds.Contains(id) && !_prevIds.Contains(id)) Retire(id);
        foreach (uint id in _retired.Keys)
            if (!_mapped.ContainsKey(id)) TryDeleteRetired(id);
    }

    // ── Merges and re-keys ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The next merge by the tiered policy; null when none is due (allowLarge = idle for 2 minutes and on mains power).</summary>
    public MergePlan? PlanMerge(bool allowLarge)
    {
        if (IsReadOnly) return null;
        var view = Retain();
        try
        {
            MergePlan? plan = null;
            for (int s = 0; s < view.Readers.Count && plan == null; s++)
            {
                var r = view.Readers[s];
                if (r.TokenizerVersion == ContentTokenizer.Version) continue;
                int live = view.Entries[s].DocCount - view.Entries[s].DeletedCount;
                if (live <= MergePolicy.LargeDocs || allowLarge) plan = new MergePlan([r.Id], live, live > MergePolicy.LargeDocs);
            }
            plan ??= MergePolicy.Plan([.. view.Entries.Select(e => (e.Id, e.DocCount, e.DeletedCount, e.Bytes))], allowLarge);
            if (plan == null) return null;
            return HasRoomFor(view, plan.Segments) ? plan : null;
        }
        finally
        {
            view.Release();
        }
    }

    /// <summary>Whether the disk has twice the live part of these segments free (always true in memory or when unknown).</summary>
    bool HasRoomFor(StoreView view, IEnumerable<uint> segments)
    {
        if (_folder == null) return true;
        long output = 0;
        foreach (uint id in segments)
        {
            int s = view.OrdinalOf(id);
            if (s < 0 || view.Entries[s].DocCount == 0) continue;
            output += view.Entries[s].Bytes * (view.Entries[s].DocCount - view.Entries[s].DeletedCount) / view.Entries[s].DocCount;
        }
        return FreeBytes(_folder) is not long free || free >= 2 * output;
    }

    static long? FreeBytes(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(folder);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the segments from stored text into one and commits: their live documents in key order, compressed text
    /// copied unchanged, terms from tokenizing the text again. Every input's sections are checked first; one that fails is
    /// dropped (its documents are read again by reconcile). pace runs between chunks (the follower's duty cycle). A
    /// cancelled merge leaves the store as it was and removes its temp files.
    /// </summary>
    public StoreChange Merge(MergePlan plan, CancellationToken ct, Action<CancellationToken>? pace = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            ThrowIfNotWritable();
            if (_batch != null) throw new InvalidOperationException("Commit or dispose the open batch before merging.");
            var view = _current;
            var inputs = plan.Segments.Distinct().Select(view.OrdinalOf).Where(s => s >= 0).Order().ToList();
            if (inputs.Count == 0) return new StoreChange(LeaseOf(view), null, [], [], []);

            var damaged = new List<uint>();
            var replaced = new List<(int Ordinal, SegmentSource Source)>();
            var sources = new List<IBuildSource>();
            var gone = new HashSet<int>(inputs);
            foreach (int s in inputs.OrderByDescending(s => s)) // newer segments win a key
            {
                var reader = view.Readers[s];
                if (!reader.Verify(ct))
                {
                    damaged.Add(reader.Id);
                    continue;
                }
                var source = new SegmentSource(reader, view.Entries[s]);
                replaced.Add((s, source));
                sources.Add(source);
            }
            return Publish(view, sources, s => gone.Contains(s) ? null : view.Entries[s], replaced, damaged, ct, pace);
        }
    }

    /// <summary>
    /// A big rename or move (A4): rewrites one segment once, the documents in moves under their new rows and paths and its
    /// other live documents as they are, all into one new segment, instead of copying the moved ones into a small segment
    /// and rewriting the source again later. Meant for moves of 30% or more of the segment's live documents (fewer go
    /// through AddCopy). Runs between batches under ct and pace, like a merge, and returns null when it can't run now: the
    /// segment is gone or damaged, it has more live documents than a large merge and allowLarge is false (idle for 2
    /// minutes and on mains power), or the disk lacks twice its size free. Stored text is copied, never compressed again; a
    /// live document elsewhere with a moved document's new key is deleted, as in a batch. LocalOfAdded follows the order
    /// of moves; a move whose document isn't live any more is left out (-1). Remaps maps the segment's documents that
    /// stayed (moved ones are -1 there).
    /// </summary>
    public StoreChange? Rekey(uint segment, IReadOnlyList<(int Doc, DocRow Row, string Path)> moves, bool allowLarge, CancellationToken ct,
        Action<CancellationToken>? pace = null)
    {
        ArgumentNullException.ThrowIfNull(moves);
        lock (_gate)
        {
            ThrowIfNotWritable();
            if (_batch != null) throw new InvalidOperationException("Commit or dispose the open batch before a re-key.");
            var view = _current;
            int s = view.OrdinalOf(segment);
            if (s < 0 || view.Readers[s].IsDamaged) return null;
            int live = view.Entries[s].DocCount - view.Entries[s].DeletedCount;
            if (live > LargeRekeyDocs && !allowLarge) return null;
            if (!HasRoomFor(view, [segment])) return null;

            var entries = new List<Batch.Entry>(moves.Count);
            var sourcesMoved = new List<DocRef>(moves.Count);
            for (int i = 0; i < moves.Count; i++)
            {
                var (doc, row, path) = moves[i];
                ArgumentNullException.ThrowIfNull(path);
                var source = new DocRef(s, doc);
                if (view.IsDeleted(source)) continue; // gone since the caller looked
                entries.Add(new Batch.Entry(i, row, path, null, source, 0, 0));
                sourcesMoved.Add(source);
            }
            if (entries.Count == 0)
            {
                var none = new int[moves.Count];
                Array.Fill(none, -1);
                return new StoreChange(LeaseOf(view), null, none, [], []);
            }
            if (!view.Readers[s].Verify(ct)) return null; // damaged: the next commit drops it, and reconcile reads its files again
            return Apply(view, entries, moves.Count, sourcesMoved, rewriteAlso: s, ct, pace);
        }
    }

    /// <summary>
    /// The idle scrub: checks the current segments' sections against their CRCs, one segment per call; true when every
    /// segment is checked. A segment that fails is dropped at the next commit (an empty commit drops it too).
    /// </summary>
    public bool Scrub(CancellationToken ct)
    {
        var view = Retain();
        try
        {
            foreach (var r in view.Readers)
            {
                if (r.IsVerified || r.IsDamaged) continue; // a damaged one waits for the commit that drops it
                if (!r.Verify(ct)) lock (_gate) _dirty = true;
                return false;
            }
            return true;
        }
        finally
        {
            view.Release();
        }
    }

    // ── Deleting ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops publishing, waits a bounded time for leases to go, then deletes every segment, manifest.prev and the temp
    /// files, and writes over the manifest with an empty one that keeps the counters (the next segment number and the
    /// generation), so a later store in this folder never reuses them. False when some files are still in use: call
    /// <see cref="FinishDeleting"/> later (the follower's timer; Settings says "finishing deleting…"), which retries only
    /// those. Calling DeleteAll again instead deletes whatever was committed since as well. The store stays usable and
    /// empty.
    /// </summary>
    public bool DeleteAll()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readOnly) throw new InvalidOperationException("The text index is open read-only.");
            _batch?.Invalidate();
            _batch = null;
            var ids = new HashSet<uint>(_currentIds);
            ids.UnionWith(_prevIds);
            ids.UnionWith(_mapped.Keys);
            ids.UnionWith(_current.Readers.Select(r => r.Id));
            if (_folder != null) ids.UnionWith(SegmentFilesOnDisk(_folder));
            long generation = NewGeneration();
            Interlocked.Exchange(ref _current, StoreView.Empty(generation, VolumesNow())).Release();
            _generation = generation;
            _newer = false; // whatever a newer build left is being deleted: this store may write again
            if (_nextId > uint.MaxValue / 2) _nextId = FreshStart(); // only a bad manifest takes numbers this high: start over
            _failed.Clear();
            _currentIds = [];
            _prevIds = [];
            _dirty = false;
            if (_folder == null) return true;
            foreach (uint id in ids)
            {
                _retired[id] = 0; // a release or a commit deletes it too, if it is still there then
                _deleting.Add(id);
            }
            _deleteManifest = _deletePrev = true;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!_mapped.IsEmpty && clock.Elapsed < LeaseWait) Thread.Sleep(10);
            return DeleteLeftovers();
        }
    }

    /// <summary>
    /// Retries what DeleteAll couldn't delete (files that were still in use), and only that: segments and manifests
    /// committed since are never touched. True when nothing is left to delete. Commits and released views retry the
    /// segment files too; a disposed store retries nothing (another writer may own the folder by then).
    /// </summary>
    public bool FinishDeleting()
    {
        lock (_gate)
        {
            if (_folder == null || _disposed || _readOnly) return !DeletesPending;
            return DeleteLeftovers();
        }
    }

    /// <summary>DeleteAll left files to delete.</summary>
    internal bool DeletesPending => _deleting.Count > 0 || _deleteManifest || _deletePrev;

    /// <summary>One pass over what DeleteAll still has to delete; true when it is all gone. Called under the gate.</summary>
    bool DeleteLeftovers()
    {
        string folder = _folder!;
        bool clean = true;
        if (_deletePrev)
        {
            if (TryDelete(Path.Combine(folder, PrevName))) _deletePrev = false;
            else clean = false;
        }
        if (_deleteManifest)
        {
            if (TryWriteEmptyManifest()) _deleteManifest = false;
            else if (TryDelete(Path.Combine(folder, ManifestName)))
            {
                _deleteManifest = false;
                _manifestOnDisk = false;
            }
            else clean = false;
        }
        foreach (uint id in _deleting.ToList())
        {
            // Numbers aren't reused, so nothing committed since has these names; still, never delete a listed segment.
            if (_currentIds.Contains(id) || _prevIds.Contains(id) || TryDelete(Path.Combine(folder, SegmentFormat.FileName(id))))
            {
                _deleting.Remove(id);
                _retired.TryRemove(id, out _);
            }
            else clean = false;
        }
        clean &= DeleteTempFiles(folder);
        return clean && !DeletesPending;
    }

    /// <summary>Writes over the manifest with an empty one (no segments, volumes or statistics) that keeps the counters.</summary>
    bool TryWriteEmptyManifest()
    {
        string path = Path.Combine(_folder!, ManifestName), tmp = Path.Combine(_folder!, TmpName);
        var empty = new Manifest
        {
            Generation = _generation, CreatedUtc = DateTime.UtcNow.ToFileTimeUtc(), NextSegmentId = PersistedNextId, Mode = _mode,
        };
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 12))
            {
                fs.Write(empty.Serialize());
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            _manifestOnDisk = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return false;
        }
    }

    /// <summary>Releases only the store's own reference: segments unmap when the last view goes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _batch?.Invalidate();
            _batch = null;
            if (_folder != null && !_readOnly && DeletesPending) DeleteLeftovers(); // last chance while the lock is ours
            _disposed = true;
            Interlocked.Exchange(ref _current, StoreView.Empty(NewGeneration())).Release();
            foreach (uint id in _retired.Keys)
                if (!_mapped.ContainsKey(id)) TryDeleteRetired(id);
            _lock?.Dispose();
            _lock = null;
        }
    }

    // ── Batches and build sources ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Documents added and deleted together; searchable only after Commit. Holds a reference on its view.</summary>
    sealed class Batch(ContentStore store, StoreView view) : IStoreBatch
    {
        internal sealed record Entry(int AddIndex, DocRow Row, string Path, StoredDoc? Doc, DocRef Source, long BytesRead, int Ms);

        internal readonly List<Entry> Entries = [];
        internal readonly HashSet<DocRef> Deletes = [];
        long _raw;
        bool _closed;

        internal StoreView View => view;
        public int Count => Entries.Count;
        public long RawBytes => _raw;

        public int Add(in DocRow row, string relativePath, StoredDoc doc, long bytesRead = 0, int elapsedMs = 0)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(relativePath);
            ArgumentNullException.ThrowIfNull(doc);
            int i = Entries.Count;
            Entries.Add(new Entry(i, row, relativePath, doc, default, bytesRead, elapsedMs));
            _raw += StoredText.BlobLength(doc);
            return i;
        }

        public int AddCopy(DocRef source, in DocRow row, string relativePath)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(relativePath);
            if (view.IsDeleted(source)) throw new ArgumentException("The source is not a live document of the batch's view.", nameof(source));
            int i = Entries.Count;
            Entries.Add(new Entry(i, row, relativePath, null, source, 0, 0));
            view.Readers[source.Segment].StoredSpan(source.Doc, out int raw);
            _raw += raw;
            return i;
        }

        public void Delete(DocRef doc)
        {
            ThrowIfClosed();
            if ((uint)doc.Segment >= (uint)view.Readers.Count || (uint)doc.Doc >= (uint)view.Readers[doc.Segment].DocCount)
                throw new ArgumentOutOfRangeException(nameof(doc), "Not a document of the batch's view.");
            Deletes.Add(doc);
        }

        public StoreChange Commit()
        {
            ThrowIfClosed();
            var change = store.CommitBatch(this);
            Close();
            return change;
        }

        void ThrowIfClosed()
        {
            if (_closed) throw new InvalidOperationException("The batch was committed or disposed.");
        }

        /// <summary>Closed by the store (DeleteAll, Dispose).</summary>
        internal void Invalidate()
        {
            if (_closed) return;
            _closed = true;
            view.Release();
        }

        void Close()
        {
            if (_closed) return;
            _closed = true;
            view.Release();
            store.BatchClosed(this);
        }

        public void Dispose() => Close();
    }

    /// <summary>Documents in ascending key order for one build; the lowest source index wins a key two sources have.</summary>
    interface IBuildSource
    {
        bool MoveNext();
        ulong Key { get; }
        /// <summary>Writes the current document; -1 when it can't be read (its segment is marked damaged).</summary>
        int Emit(SegmentWriter writer);
        /// <summary>The current document lost its key to a newer one.</summary>
        void Skip();
    }

    /// <summary>A batch's kept documents (sorted by key): new ones, and copies of live documents under a new key and path.</summary>
    sealed class BatchSource(List<Batch.Entry> sorted, StoreView view, int[] localOfAdded) : IBuildSource
    {
        int _i = -1;
        public int[] LocalOfAdded => localOfAdded;
        public bool MoveNext() => ++_i < sorted.Count;
        public ulong Key => sorted[_i].Row.Key;
        public void Skip() { }

        public int Emit(SegmentWriter writer)
        {
            var e = sorted[_i];
            int local;
            if (e.Doc != null) local = writer.Add(e.Row, e.Path, e.Doc, default, e.BytesRead, e.Ms);
            else
            {
                var reader = view.Readers[e.Source.Segment];
                local = CopyFrom(writer, reader, e.Source.Doc, e.Row, e.Path);
            }
            localOfAdded[e.AddIndex] = local;
            return local;
        }
    }

    /// <summary>A segment's live documents (its deletions applied), for merges and one-rewrite re-keys.</summary>
    sealed class SegmentSource(SegmentReader reader, Manifest.SegmentEntry entry) : IBuildSource
    {
        int _doc = -1;
        public int[] OldToNew { get; } = NewRemap(reader.DocCount);

        static int[] NewRemap(int n)
        {
            var a = new int[n];
            Array.Fill(a, -1);
            return a;
        }

        public bool MoveNext()
        {
            do _doc++; while (_doc < reader.DocCount && entry.IsDeleted(_doc));
            return _doc < reader.DocCount;
        }

        public ulong Key => reader.KeyAt(_doc);
        public void Skip() { }

        public int Emit(SegmentWriter writer)
        {
            int local = CopyFrom(writer, reader, _doc, reader.Row(_doc), null);
            OldToNew[_doc] = local;
            return local;
        }
    }

    /// <summary>
    /// Writes a stored document again: compressed text copied as it is, terms from its decoded text. -1 when the segment is
    /// known to be damaged, or its text or path doesn't decode (which marks it damaged): nothing is copied from it.
    /// </summary>
    static int CopyFrom(SegmentWriter writer, SegmentReader reader, int doc, DocRow row, string? path)
    {
        if (reader.IsDamaged) return -1;
        var stored = reader.StoredSpan(doc, out int raw);
        var text = StoredText.Decode(stored, raw);
        path ??= reader.ReadPath(doc);
        if (text == null || path == null)
        {
            reader.MarkDamaged();
            return -1;
        }
        if (reader.TryGetPicture(doc, out var picture)) text = text with { Picture = picture };
        var (bytesRead, ms) = reader.Averages(reader.Row(doc).Kind);
        return writer.AddStored(row, path, text, stored, raw, bytesRead, ms);
    }
}

/// <summary>
/// How the store tells a file it can't read now (in use without read sharing, access denied or delete pending, a share
/// that doesn't answer) from one that is gone, or one that reads but fails its checks.
/// </summary>
internal static class StoreFiles
{
    /// <summary>Retries of a file that can't be read now before it counts as unavailable.</summary>
    internal static int Retries { get; set; } = 3;
    /// <summary>The wait before each retry.</summary>
    internal static TimeSpan RetryWait { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The file or its folder isn't there.</summary>
    public static bool IsMissing(Exception e) => e is FileNotFoundException or DirectoryNotFoundException;

    /// <summary>An error of the file system rather than of the data: the file may read fine later.</summary>
    public static bool IsUnavailable(Exception e) => e is UnauthorizedAccessException || (e is IOException && !IsMissing(e));
}
