using Anything.Core;

namespace Anything;

/// <param name="Sortable">Clicking its header sorts by <paramref name="Sort"/>; false for the Matching text column.</param>
/// <param name="MenuTitle">How the header menu names it, when that says more than the header (null: the title).</param>
internal sealed record ColumnDef(string Key, string Title, SortKey Sort, int DefaultWidth, bool DefaultVisible, int DefaultPosition, HorizontalAlignment Align,
    bool Sortable = true, string? MenuTitle = null);

internal static class Columns
{
    // Index in this array == ListView column index (Name must be 0). The last four are Everything's too, hidden at first
    // as there; their keys match Everything's ini names (date_accessed_column...).
    public static readonly ColumnDef[] All =
    [
        new("name", "Name", SortKey.Name, 300, true, 0, HorizontalAlignment.Left),
        new("path", "Path", SortKey.Path, 300, true, 1, HorizontalAlignment.Left),
        new("size", "Size", SortKey.Size, 80, true, 2, HorizontalAlignment.Right),
        new("extension", "Extension", SortKey.Extension, 80, false, 4, HorizontalAlignment.Left),
        new("type", "Type", SortKey.Type, 140, false, 5, HorizontalAlignment.Left),
        new("date_modified", "Date Modified", SortKey.DateModified, 140, true, 3, HorizontalAlignment.Left),
        new("date_created", "Date Created", SortKey.DateCreated, 140, false, 6, HorizontalAlignment.Left),
        new("attributes", "Attributes", SortKey.Attributes, 80, false, 7, HorizontalAlignment.Left),
        new("date_accessed", "Date Accessed", SortKey.DateAccessed, 140, false, 8, HorizontalAlignment.Left),
        new("date_recently_changed", "Date Recently Changed", SortKey.DateRecentlyChanged, 140, false, 9, HorizontalAlignment.Left),
        new("run_count", "Run Count", SortKey.RunCount, 80, false, 10, HorizontalAlignment.Right),
        new("date_run", "Date Run", SortKey.DateRun, 140, false, 11, HorizontalAlignment.Left),
        // The line each result matched inside the file. Shown while search inside files is on and the window's Search ›
        // Match inside files is (the header menu's tick means "allowed"); position 0 puts it right of Name. Not sortable:
        // results matched by name come first, then those matched only inside files.
        new("content", "Matching text", SortKey.Name, 360, true, 0, HorizontalAlignment.Left, Sortable: false,
            MenuTitle: "Matching text (when words match inside files)"),
    ];

    public const int Name = 0, Path = 1, Size = 2, Extension = 3, Type = 4, DateModified = 5, DateCreated = 6, Attributes = 7,
        DateAccessed = 8, DateRecentlyChanged = 9, RunCount = 10, DateRun = 11, Content = 12;

    public static string SortTitle(SortKey k) => k switch
    {
        SortKey.DateModified => "Date Modified",
        SortKey.DateCreated => "Date Created",
        SortKey.DateAccessed => "Date Accessed",
        SortKey.DateRecentlyChanged => "Date Recently Changed",
        SortKey.RunCount => "Run Count",
        SortKey.DateRun => "Date Run",
        _ => k.ToString(),
    };

    /// <summary>Everything's sort shortcuts: Ctrl+1 to Ctrl+8 for Name to Attributes, Ctrl+9 for Date Recently Changed.</summary>
    public static Keys SortShortcut(SortKey k) => k switch
    {
        <= SortKey.Attributes => Keys.Control | (Keys.D1 + (int)k),
        SortKey.DateRecentlyChanged => Keys.Control | Keys.D9,
        _ => Keys.None,
    };
}

/// <summary>A kind of file (Search options › Kind): its name, the search it adds, what it shows ("videos") and its icon.</summary>
internal sealed record FilterDef(string Name, string Search, string Shown = "", char Glyph = ExplorerTheme.GFilter);

internal static class Filters
{
    // Icons from Segoe Fluent Icons: Audio, ZipFolder, Document, AppIconDefault, Folder, Picture, Video.
    public static readonly FilterDef[] All =
    [
        new("Everything", ""),
        new("Audio", "audio:", "audio files", ''),
        new("Compressed", "zip:", "compressed files", ''),
        new("Document", "doc:", "documents", ''),
        new("Executable", "exe:", "programs", ''),
        new("Folder", "folder:", "folders", ExplorerTheme.GFolder),
        new("Picture", "pic:", "pictures", ''),
        new("Video", "video:", "videos", ''),
    ];

    public static int IndexOf(string name)
    {
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
