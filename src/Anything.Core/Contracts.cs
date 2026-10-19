namespace Anything.Core;

/// <summary>Result columns / sort orders (mirrors Everything's sort menu).</summary>
public enum SortKey { Name, Path, Size, Extension, Type, DateModified, DateCreated, Attributes }

/// <summary>Global match toggles from the Search menu. Query modifiers (case:, regex:, ...) override them per term.</summary>
public sealed record SearchOptions
{
    public bool MatchCase { get; init; }
    public bool MatchWholeWord { get; init; }
    public bool MatchPath { get; init; }
    public bool MatchDiacritics { get; init; }
    public bool Regex { get; init; }
    /// <summary>Active filter's search text (e.g. "ext:mp3;wav"), ANDed with the query; null/empty = Everything.</summary>
    public string? Filter { get; init; }
}

public sealed class SearchResults
{
    public required int[] Ids { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Non-null when the query is invalid (e.g. bad regex); Ids is then empty.</summary>
    public string? Error { get; init; }
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

public sealed class IndexSettings
{
    /// <summary>Index fixed NTFS volumes via the MFT + USN journal (requires administrator). Falls back to folder scanning when not elevated.</summary>
    public bool IndexNtfsVolumes { get; set; } = true;
    /// <summary>Drive roots to include, e.g. "C:\". Empty = all fixed drives.</summary>
    public List<string> Volumes { get; set; } = [];
    /// <summary>Extra folder roots indexed by directory scanning + change notifications (network shares, non-NTFS).</summary>
    public List<string> Folders { get; set; } = [];
    public List<string> ExcludeFolders { get; set; } = [];
    /// <summary>Wildcard name patterns to exclude, e.g. "*.tmp".</summary>
    public List<string> ExcludePatterns { get; set; } = [];
    public bool ExcludeHidden { get; set; }
    public bool ExcludeSystem { get; set; }
    /// <summary>Database file; loaded at startup for instant results, saved on exit.</summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rock Preview", "RockPreview.db");
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
    void Save();
}
