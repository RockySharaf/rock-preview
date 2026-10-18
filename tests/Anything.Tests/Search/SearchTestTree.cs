using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>Builds synthetic FileIndex trees from "C:\dir\file" style paths.</summary>
internal sealed class TreeBuilder
{
    public FileIndex Index { get; } = new();
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

    public static long Ft(int y, int mo, int d, int h = 12, int mi = 0)
        => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local).ToFileTimeUtc();

    public int Folder(string path, long modified = 0, long created = 0, FileAttributes extra = 0)
    {
        if (_ids.TryGetValue(path, out int existing)) return existing;
        int cut = path.LastIndexOf('\\');
        int id = cut < 0
            ? Index.AddRoot(path)
            : Index.Add(path[(cut + 1)..], Folder(path[..cut]), FileAttributes.Directory | extra, -1, modified, created);
        _ids[path] = id;
        return id;
    }

    public int File(string path, long size = 0, long modified = 0, long created = 0, FileAttributes attrs = FileAttributes.Archive)
    {
        int cut = path.LastIndexOf('\\');
        int id = Index.Add(path[(cut + 1)..], Folder(path[..cut]), attrs, size, modified, created);
        _ids[path] = id;
        return id;
    }

    public int Id(string path) => _ids[path];
}

/// <summary>Shared fixture tree with a fixed clock (Wednesday 2024-05-15 12:00 local).</summary>
public abstract class SearchTestBase
{
    protected static readonly DateTime Now = new(2024, 5, 15, 12, 0, 0, DateTimeKind.Local);
    const long KB = 1024, MB = 1024 * 1024;

    internal readonly TreeBuilder Tree = new();
    protected readonly SearchEngine Engine = new() { };

    protected static readonly string[] DFiles =
        [@"D:\foo_bar.txt", @"D:\foobar.txt", @"D:\bar foo.txt", @"D:\it's.txt", @"D:\clip.mkv", @"D:\image.PNG"];

    protected static readonly string[] AllFolders =
        ["C:", @"C:\Music", @"C:\Docs", @"C:\Docs\Archive", @"C:\Empty", @"C:\Program Files", @"C:\Program Files\App", "D:", @"D:\R&D"];

    protected SearchTestBase()
    {
        Engine.Clock = () => Now;
        var t = Tree;
        t.Folder("C:");
        t.Folder(@"C:\Music");
        t.File(@"C:\Music\song.mp3", 5 * MB, TreeBuilder.Ft(2024, 5, 15, 10), TreeBuilder.Ft(2024, 1, 1));
        t.File(@"C:\Music\Song Two.MP3", 3 * KB, TreeBuilder.Ft(2024, 5, 14, 9), TreeBuilder.Ft(2024, 1, 1));
        t.File(@"C:\Music\track.wav", 50 * KB, TreeBuilder.Ft(2024, 5, 8, 14), TreeBuilder.Ft(2023, 1, 1));
        t.File(@"C:\Music\Café.flac", 20 * MB, TreeBuilder.Ft(2024, 4, 20), TreeBuilder.Ft(2023, 1, 1));
        t.Folder(@"C:\Docs");
        t.File(@"C:\Docs\report.docx", 200 * KB, TreeBuilder.Ft(2023, 6, 1));
        t.File(@"C:\Docs\notes.txt", 0, TreeBuilder.Ft(2024, 1, 10), 0, FileAttributes.Hidden | FileAttributes.Archive);
        t.Folder(@"C:\Docs\Archive");
        t.File(@"C:\Docs\Archive\old.zip", 200 * MB, TreeBuilder.Ft(2020, 3, 3), 0, FileAttributes.ReadOnly | FileAttributes.Archive);
        t.File(@"C:\Docs\Archive\notes.txt", 100);
        t.Folder(@"C:\Empty");
        t.Folder(@"C:\Program Files\App");
        t.File(@"C:\Program Files\App\app.exe", MB, TreeBuilder.Ft(2024, 5, 16, 8), 0, FileAttributes.System | FileAttributes.Archive);
        t.File(@"C:\Program Files\App\readme", 10, TreeBuilder.Ft(2022, 2, 2));
        t.Folder("D:");
        foreach (var f in DFiles) t.File(f, 1);
        t.Folder(@"D:\R&D");
    }

    protected SearchResults Run(string query, SearchOptions? options = null, SortKey sort = SortKey.Name, bool descending = false)
        => Engine.Search(Tree.Index, query, options ?? new SearchOptions(), sort, descending, CancellationToken.None);

    /// <summary>Full paths of the results, in result order.</summary>
    protected string[] Find(string query, SearchOptions? options = null, SortKey sort = SortKey.Name, bool descending = false)
    {
        var r = Run(query, options, sort, descending);
        Assert.Null(r.Error);
        return r.Ids.Select(PathOf).ToArray();
    }

    protected string PathOf(int id)
    {
        var p = Tree.Index.GetFullPath(id);
        return Tree.Index.Parent(id) == FileIndex.NoParent ? p.TrimEnd('\\') : p;
    }

    protected void AssertFinds(string query, params string[] expected) => AssertFinds(query, null, expected);

    protected void AssertFinds(string query, SearchOptions? options, params string[] expected)
    {
        var actual = Find(query, options).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var exp = expected.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(exp.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
            $"Query [{query}]\n expected: {string.Join(" | ", exp)}\n actual:   {string.Join(" | ", actual)}");
    }
}
