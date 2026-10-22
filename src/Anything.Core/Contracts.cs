namespace Anything.Core;

/// <summary>Result columns / sort orders (mirrors Everything's sort menu; new ones go last, settings keep the names).</summary>
public enum SortKey { Name, Path, Size, Extension, Type, DateModified, DateCreated, Attributes, RunCount, DateRecentlyChanged, DateAccessed, DateRun }

/// <summary>Global match toggles from the Search menu. Query modifiers (case:, regex:, ...) override them per term.</summary>
public sealed record SearchOptions
{
    /// <summary>Plain search words also find words inside files (Search › Match inside files), while the text index is on.</summary>
    public bool MatchContent { get; init; }
    public bool MatchCase { get; init; }
    public bool MatchWholeWord { get; init; }
    public bool MatchPath { get; init; }
    public bool MatchDiacritics { get; init; }
    public bool Regex { get; init; }
    /// <summary>
    /// Active filter's search text (e.g. "ext:mp3;wav", or a folder scope like <c>file: "C:\x\"</c>), ANDed with the query;
    /// null/empty = Everything. The app builds it, so the toggles above don't apply to it: it is read ignoring case, with
    /// whole words off, no regex, wildcards on and Match path off (a term with a backslash still matches the full path),
    /// but with diacritics matched (a scope of <c>C:\cafe</c> does not take <c>C:\café</c>); see
    /// <see cref="Search.QueryParser.FilterOptions"/>. Modifiers on its text terms (case:, ww:, ...) still apply.
    /// </summary>
    public string? Filter { get; init; }
}

public sealed class SearchResults
{
    public required int[] Ids { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Non-null when the query is invalid (e.g. bad regex); Ids is then empty.</summary>
    public string? Error { get; init; }
    /// <summary>How many of the first Ids matched by name (then come those matched only inside files); -1 when no plain word looked inside files.</summary>
    public int NameMatches { get; init; } = -1;
    /// <summary>Matches inside files may be missing (a lookup still runs, or the text index is catching up): search again on ContentChanged.</summary>
    public bool ContentPending { get; init; }
    public static SearchResults Empty { get; } = new() { Ids = [] };
}

public interface ISearchEngine
{
    /// <summary>Matches and sorts live entries. Must hold index.Lock (read) while scanning. Honors cancellation.</summary>
    SearchResults Search(FileIndex index, string query, SearchOptions options, SortKey sort, bool descending, CancellationToken cancellationToken);

    /// <summary>Returns (start, length) ranges in <paramref name="text"/> that the query's plain terms match, for bold highlighting.</summary>
    IReadOnlyList<(int Start, int Length)> GetHighlights(string query, SearchOptions options, string text);
}

public enum IndexState { Idle, Loading, Scanning, Ready, Error }

/// <summary>What kind of drive a volume is (from GetDriveType).</summary>
public enum DriveKind : byte { Fixed, Removable, Network, Optical, Ram, Unknown }

/// <summary>Where one indexed root stands, for Settings › Indexing.</summary>
public enum RootStatus : byte { Waiting, Scanning, Ready, Offline }

/// <summary>One indexed root (a drive, a mounted volume or a folder) as it is now.</summary>
/// <param name="Path">"C:\", "Z:\", "C:\Mount\Data" or "\\server\share".</param>
/// <param name="Method">How it is read: "MFT", "USN" (file table through the change journal) or "scan" (folders listed one by one).</param>
/// <param name="Items">Files and folders it holds in the index, the root included.</param>
/// <param name="Detail">Why it is offline, or the last error; null when there is nothing to say.</param>
public sealed record IndexedRootInfo(string Path, DriveKind Kind, bool Network, string Method, RootStatus Status, int Items, string? Detail);

/// <summary>Where one root the index holds stands now, without counting its items (<see cref="IIndexManager.StateOfRoot"/>).</summary>
/// <param name="Status">Waiting, scanning, ready or offline (an offline root keeps what it had).</param>
/// <param name="HasEntries">
/// Its root entry is in the index: loaded from the database or added by a scan. A first scan of a folder adds entries as
/// it goes, so while it scans they can be partial (see <paramref name="Complete"/>). False while it has none (never
/// scanned, or never reached).
/// </param>
/// <param name="Complete">
/// Its entries are a whole copy: a scan of it finished, now or before the database was saved. False while it has none,
/// during a folder's first scan, and for a first scan cut short (saved on exit, or broken off when its drive went away),
/// whose entries are partial.
/// </param>
public readonly record struct RootState(RootStatus Status, bool HasEntries, bool Complete);

public sealed class IndexSettings
{
    /// <summary>
    /// Index whole drives (Settings › Indexing). NTFS drives are read from their file table (through the service or as
    /// administrator), every other drive by scanning its folders. Off: only <see cref="Folders"/> are indexed.
    /// </summary>
    public bool IndexNtfsVolumes { get; set; } = true;
    /// <summary>
    /// Legacy (1.4.0 and earlier, which listed fixed drives only): when non-empty, a fixed drive not on it is turned off;
    /// other kinds of drive ignore it. Settings now writes <see cref="SkipDrives"/> and only adds to this list.
    /// </summary>
    public List<string> Volumes { get; set; } = [];
    /// <summary>Drive roots turned off in Settings › Indexing, e.g. "D:\" or "Z:\". Every other drive of an enabled kind is indexed.</summary>
    public List<string> SkipDrives { get; set; } = [];
    /// <summary>Index USB drives, memory cards and CDs/DVDs while they are plugged in.</summary>
    public bool IndexRemovableDrives { get; set; } = true;
    /// <summary>Index mapped network drives (Z:). Their entries stay searchable while the network is away.</summary>
    public bool IndexNetworkDrives { get; set; } = true;
    /// <summary>
    /// Minutes between full rescans of network drives and network folders, whose change notifications can miss changes.
    /// 0 = never (only change notifications and Rebuild).
    /// </summary>
    public int NetworkRescanMinutes { get; set; } = 60;
    /// <summary>Extra folder roots indexed by directory scanning + change notifications (network shares, non-NTFS).</summary>
    public List<string> Folders { get; set; } = [];
    public List<string> ExcludeFolders { get; set; } = [];
    /// <summary>Wildcard name patterns to exclude, e.g. "*.tmp".</summary>
    public List<string> ExcludePatterns { get; set; } = [];
    public bool ExcludeHidden { get; set; }
    public bool ExcludeSystem { get; set; }
    /// <summary>
    /// Keep every file's and folder's date accessed in the index (8 bytes each), for instant da: searches and sorting.
    /// Off (Everything's default too): the date is read from the disk when shown or searched, which is slow.
    /// </summary>
    public bool IndexDateAccessed { get; set; }
    /// <summary>Folders show the total size of everything in them, kept current as files change (Everything: index_folder_size).</summary>
    public bool IndexFolderSizes { get; set; }
    /// <summary>Remember what changed while the app runs (Date Recently Changed, rc:), like Everything's index_recent_changes.</summary>
    public bool IndexRecentChanges { get; set; } = true;
    /// <summary>At startup, take recent changes from the NTFS change journal, so they reach back days, not just since the start.</summary>
    public bool RecentChangesFromJournal { get; set; }
    /// <summary>Database file; loaded at startup for instant results, saved on exit.</summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rock Preview", "RockPreview.db");

    /// <summary>A copy, lists included: the index works from a snapshot, so Settings can edit the original freely.</summary>
    public IndexSettings Clone() => new()
    {
        IndexNtfsVolumes = IndexNtfsVolumes,
        Volumes = [.. Volumes],
        SkipDrives = [.. SkipDrives],
        IndexRemovableDrives = IndexRemovableDrives,
        IndexNetworkDrives = IndexNetworkDrives,
        NetworkRescanMinutes = NetworkRescanMinutes,
        Folders = [.. Folders],
        ExcludeFolders = [.. ExcludeFolders],
        ExcludePatterns = [.. ExcludePatterns],
        ExcludeHidden = ExcludeHidden,
        ExcludeSystem = ExcludeSystem,
        IndexDateAccessed = IndexDateAccessed,
        IndexFolderSizes = IndexFolderSizes,
        IndexRecentChanges = IndexRecentChanges,
        RecentChangesFromJournal = RecentChangesFromJournal,
        DatabasePath = DatabasePath,
    };
}

public interface IIndexManager : IDisposable
{
    FileIndex Index { get; }
    IndexState State { get; }
    /// <summary>Human readable progress for the status bar, e.g. "Scanning C:\ (1,234,567 objects)".</summary>
    string StatusText { get; }
    event Action? StateChanged;
    bool IsElevated { get; }
    /// <summary>NTFS volumes are read through the Rock Preview service (instant indexing without administrator).</summary>
    bool UsesService { get; }

    /// <summary>Loads the database (if present) then starts background (re)indexing and change monitoring. Returns immediately.</summary>
    void Start(IndexSettings settings);
    /// <summary>Discards the index and rescans everything with the current settings.</summary>
    void Rebuild();

    /// <summary>
    /// Changed settings from Settings: drives and folders switched on or off are added or dropped without touching the
    /// rest of the index; changed exclusions rescan what is indexed, keeping it searchable meanwhile (and keeping what
    /// offline network drives had). Returns quickly.
    /// </summary>
    void Apply(IndexSettings settings) => Start(settings);
    void Save();

    /// <summary>The indexed roots with their item counts. Walks the whole index, so call it off the UI thread.</summary>
    IReadOnlyList<IndexedRootInfo> GetRoots() => [];

    /// <summary>
    /// The indexed root named <paramref name="rootName"/> ("C:\", "D:\Data", "\\server\share"; any case, with or without
    /// the trailing backslash), or null when the index has no such root: Settings leaves it out, its drive is being
    /// ejected, or it isn't started or has stopped. Null is not an answer yet while <see cref="State"/> is Loading or a
    /// rebuild has just started over (the roots are listed again then): ask again on <see cref="StateChanged"/>. Counts
    /// nothing, so the UI thread can ask at any time.
    /// </summary>
    RootState? StateOfRoot(string rootName) => null;

    /// <summary>
    /// Raised on a worker thread when <see cref="PlannedDrives"/> changes: the index listed the drives and planned its
    /// roots with them, at start or after the drives changed (a drive plugged in or removed, a network drive mapped, a
    /// card put in a reader or a drive unlocked, which a look every 30 seconds finds). New roots are added and gone ones
    /// dropped by then. Only when they differ from the last plan's: not for no drives at all, nor for a Rebuild with the
    /// same ones. The first can come before a handler is added, so read <see cref="PlannedDrives"/> once after adding one.
    /// </summary>
    event Action? DrivesChanged { add { } remove { } }

    /// <summary>
    /// The drives the index last planned its roots with (a network drive as mapped, its server not asked), so what is
    /// worked out from the drives matches what the index does. Empty until it has planned.
    /// </summary>
    IReadOnlyList<Indexing.DriveEntry> PlannedDrives => [];

    /// <summary>
    /// Folders the index doesn't hold, read from the disk into a listing of their own (<see cref="Indexing.FolderListing"/>)
    /// whose run history follows this index's. The index manager's also lets go of a drive being ejected, as the index
    /// does. Throws <see cref="ArgumentException"/> when no folder is left to read.
    /// </summary>
    Indexing.FolderListing CreateListing(IReadOnlyList<string> folders, IndexSettings settings) => new(folders, settings, Index.RunHistory);
}
