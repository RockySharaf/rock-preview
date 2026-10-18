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
    protected RootIndexer(IIndexerHost host, string rootPath)
    {
        Host = host;
        RootName = NormalizeRootName(rootPath);
        RootPath = RootName.Length == 2 && RootName[1] == ':' ? RootName + "\\" : RootName;
    }

    protected IIndexerHost Host { get; }
    protected FileIndex Index => Host.Index;
    protected ExclusionFilter Filter => Host.Filter;

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

    public virtual void Dispose() => StopMonitoring();

    public static string NormalizeRootName(string path)
    {
        path = path.Trim().Replace('/', '\\');
        if (path.Length >= 2 && path[1] == ':' && path.Length <= 3) return char.ToUpperInvariant(path[0]) + ":";
        try { path = Path.GetFullPath(path); } catch { }
        return path.TrimEnd('\\');
    }

    public override string ToString() => $"{Kind} {RootPath}";
}
