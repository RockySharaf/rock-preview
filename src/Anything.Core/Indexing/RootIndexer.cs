namespace Anything.Core.Indexing;

public enum RootKind : byte { Ntfs = 0, Folder = 1 }

/// <summary>Services the manager provides to its root indexers.</summary>
internal interface IIndexerHost
{
    FileIndex Index { get; }
    ExclusionFilter Filter { get; }
    /// <summary>Requests a coalesced <see cref="FileIndex.RaiseChanged"/> (at most every ~250 ms).</summary>
    void MarkChanged();
    /// <summary>Asks for a background rescan of a root (journal wrap, watcher overflow...).</summary>
    void RequestRescan(RootIndexer root, string reason);
    void ReportProgress(RootIndexer root, long objects);
}

/// <summary>One indexed root (an NTFS volume or a scanned folder). Owns its live-update machinery.</summary>
internal abstract class RootIndexer : IDisposable
{
    readonly bool _explicit;
    ExclusionFilter? _filterSource, _filter;

    /// <param name="explicitRoot">
    /// A folder from Settings › Folders: searched even inside a left-out folder, so the manager's rules that leave out
    /// the folder itself or a folder above it don't apply to it (<see cref="ExclusionFilter.WithoutAncestorsOf"/>).
    /// </param>
    protected RootIndexer(IIndexerHost host, string rootPath, bool explicitRoot = false)
    {
        Host = host;
        _explicit = explicitRoot;
        RootName = NormalizeRootName(rootPath);
        RootPath = RootName.Length == 2 && RootName[1] == ':' ? RootName + "\\" : RootName;
    }

    protected IIndexerHost Host { get; }
    protected FileIndex Index => Host.Index;

    /// <summary>
    /// The exclusion rules for this root, following the manager's: changed exclusions apply to the next scan (and to live
    /// changes) without making the root again.
    /// </summary>
    public ExclusionFilter Filter
    {
        get
        {
            var source = Host.Filter;
            if (!_explicit) return source;
            var cached = _filter;
            if (cached != null && ReferenceEquals(_filterSource, source)) return cached;
            cached = source.WithoutAncestorsOf(RootName);
            _filterSource = source; // benign race: both threads compute the same immutable filter
            _filter = cached;
            return cached;
        }
    }

    /// <summary>
    /// <see cref="ExclusionFilter.Stamp"/> of the rules this root's entries were built with (kept in the database), 0 =
    /// unknown. When it differs from <see cref="Filter"/>'s, the entries need a rescan to match the current exclusions.
    /// </summary>
    public ulong RulesStamp { get; set; }

    /// <summary>The kind of drive this root is on (set by the manager from the plan).</summary>
    public DriveKind DriveKind { get; init; } = DriveKind.Unknown;
    /// <summary>On the network: scanned off the main indexing thread, rescanned on a schedule, kept while offline.</summary>
    public bool IsNetwork { get; init; }
    /// <summary>The plan entry this root was made from (null in tests that build roots directly).</summary>
    public PlannedRoot? Plan { get; init; }
    /// <summary>Cancelled when the root is dropped (its drive went away or Settings turned it off) or released for an eject.</summary>
    public CancellationTokenSource Lifetime { get; private set; } = new();
    /// <summary>Set when its drive is being ejected: handles are closed, so the manager replaces it if the eject fails.</summary>
    public volatile bool Released;
    /// <summary>Windows cancelled the eject (another program kept the drive busy): the root can be read again.</summary>
    public volatile bool EjectFailed;
    public long ReleasedAtTicks;

    /// <summary>1 while a scan of this root runs: two scans of one root at once would corrupt its maps.</summary>
    public int ScanBusy;
    /// <summary>A rescan was asked for while one was running (a watcher overflow mid-scan): scan once more after it.</summary>
    public volatile bool RescanPending;
    /// <summary>Set while no scan runs, so an eject can wait for a scan to close its handles.</summary>
    public ManualResetEventSlim ScanIdle { get; } = new(true);
    /// <summary>When a failed scan is tried again (Environment.TickCount64), 0 = not scheduled.</summary>
    public long RetryAtTicks;

    volatile RootStatus _status = RootStatus.Waiting;
    public RootStatus Status { get => _status; protected set => _status = value; }
    /// <summary>Why it is offline, or the last scan error; null when fine.</summary>
    public volatile string? Detail;
    /// <summary>When the last complete scan finished (FILETIME UTC), 0 if never.</summary>
    public long LastScanUtc { get; protected set; }
    /// <summary>For Settings: "MFT", "USN" or "scan".</summary>
    public virtual string Method => "scan";
    /// <summary>Progress of the scan in progress (entries found so far).</summary>
    public virtual long ProgressCount => 0;

    /// <summary>Stops change monitoring and closes every handle on the drive (it is being ejected).</summary>
    public virtual void Release()
    {
        Released = true;
        StopMonitoring();
    }

    /// <summary>
    /// After an eject or volume lock that didn't happen: makes a released root usable again with what it still has
    /// (entries, maps, journal position), so it resumes instead of starting over. NTFS roots need a new volume
    /// <paramref name="source"/>. False when it can't be revived (the caller drops it and starts a new one).
    /// </summary>
    public virtual bool Revive(IVolumeSource? source)
    {
        if (!Released) return true;
        Lifetime = new CancellationTokenSource();
        EjectFailed = false;
        ReleasedAtTicks = 0;
        Released = false;
        return true;
    }

    /// <summary>Display/root path, e.g. "C:\" or "D:\Data".</summary>
    public string RootPath { get; }
    /// <summary>Name of the root entry in the index, e.g. "C:" or "D:\Data".</summary>
    public string RootName { get; }
    public abstract RootKind Kind { get; }

    /// <summary>Guards this root's maps. Lock order: Sync before the index lock.</summary>
    public object Sync { get; } = new();

    /// <summary>Index id of the root entry, -1 when the root has no entries yet.</summary>
    public int RootId { get; protected set; } = -1;

    /// <summary>Full scan into the index; atomically replaces any previous entries of this root when done.</summary>
    public abstract void Scan(CancellationToken ct);

    /// <summary>Adopts entries loaded from the database. Returns true when they are current (no rescan needed).</summary>
    public abstract bool AttachLoaded(LoadedRoot loaded);

    public abstract void StartMonitoring();
    public abstract void StopMonitoring();

    /// <summary>Persistence info; called with <see cref="Sync"/> held.</summary>
    public virtual (ulong JournalId, long NextUsn) JournalPosition => (0, 0);

    /// <summary>Index id → FRN map for the database (NTFS only); called with <see cref="Sync"/> held.</summary>
    public virtual long[]? BuildIdToFrn(int indexCount) => null;

    /// <summary>
    /// Translates every stored index id after <see cref="FileIndex.Compact"/> (old id → new id, -1 = removed).
    /// Called with <see cref="Sync"/> held and while no scan of this root is running.
    /// </summary>
    public virtual void Remap(int[] remap) => RootId = RemapId(remap, RootId);

    protected static int RemapId(int[] remap, int id) => (uint)id < (uint)remap.Length ? remap[id] : -1;

    public virtual void Dispose()
    {
        try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
        StopMonitoring();
    }

    public static string NormalizeRootName(string path)
    {
        path = path.Trim().Replace('/', '\\');
        if (path.Length >= 2 && path[1] == ':' && path.Length <= 3) return char.ToUpperInvariant(path[0]) + ":";
        try { path = Path.GetFullPath(path); } catch { }
        return path.TrimEnd('\\');
    }

    public override string ToString() => $"{Kind} {RootPath}";
}
