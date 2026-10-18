using Anything.Core;

namespace Anything;

internal sealed record ColumnDef(string Key, string Title, SortKey Sort, int DefaultWidth, bool DefaultVisible, int DefaultPosition, HorizontalAlignment Align);

internal static class Columns
{
    // Index in this array == ListView column index (Name must be 0).
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
    ];

    public const int Name = 0, Path = 1, Size = 2, Extension = 3, Type = 4, DateModified = 5, DateCreated = 6, Attributes = 7;

    public static string SortTitle(SortKey k) => k switch
    {
        SortKey.DateModified => "Date Modified",
        SortKey.DateCreated => "Date Created",
        _ => k.ToString(),
    };
}

internal sealed record FilterDef(string Name, string Search);

internal static class Filters
{
    public static readonly FilterDef[] All =
    [
        new("Everything", ""),
        new("Audio", "audio:"),
        new("Compressed", "zip:"),
        new("Document", "doc:"),
        new("Executable", "exe:"),
        new("Folder", "folder:"),
        new("Picture", "pic:"),
        new("Video", "video:"),
    ];

    public static int IndexOf(string name)
    {
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
