using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>
/// da: / dateaccessed:, rc: / recentchange:, and size: on folders, with the Date Accessed and Date Recently Changed
/// sorts: indexed, and (date accessed) read from the disk when it isn't, as Everything does.
/// </summary>
public sealed class FileInformationSearchTests : SearchTestBase
{
    string[] Names(string query, SortKey sort = SortKey.Name, bool descending = false)
        => Find(query, null, sort, descending).Select(p => p[(p.LastIndexOf('\\') + 1)..]).ToArray();

    [Theory]
    [InlineData("da:today")]
    [InlineData("dateaccessed:today")]
    [InlineData("rc:today")]
    [InlineData("recentchange:today")]
    [InlineData("dr:today")]
    [InlineData("daterun:today")]
    [InlineData("runcount:>1")]
    public void Functions_are_recognized(string query)
    {
        var node = QueryParser.Parse(query, new SearchOptions());
        Assert.IsType<FunctionNode>(node);
    }

    [Fact]
    public void Date_accessed_searches_and_sorts_when_indexed()
    {
        var index = Tree.Index;
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: false);
        // Every entry known (an unknown one would be read from the disk, where these paths don't exist or mean something else).
        for (int id = 0; id < index.Count; id++) index.Update(id, accessedFileTimeUtc: TreeBuilder.Ft(2001, 1, 1));
        index.Update(Tree.Id(@"C:\Music\song.mp3"), accessedFileTimeUtc: TreeBuilder.Ft(2024, 5, 15, 9));
        index.Update(Tree.Id(@"C:\Docs\report.docx"), accessedFileTimeUtc: TreeBuilder.Ft(2024, 5, 14, 9));
        index.Update(Tree.Id(@"C:\Docs\notes.txt"), accessedFileTimeUtc: TreeBuilder.Ft(2023, 1, 1));

        Assert.Equal(["song.mp3"], Names("da:today"));
        Assert.Equal(["report.docx"], Names("dateaccessed:yesterday"));
        Assert.Equal(["notes.txt", "report.docx", "song.mp3"], Names("da:>=2020", SortKey.DateAccessed));
        Assert.Equal(["song.mp3", "report.docx", "notes.txt"], Names("da:>=2020", SortKey.DateAccessed, descending: true));
        Assert.Equal(["notes.txt"], Names("da:2023"));
        Assert.Equal(Find("").Length - 3, Find("da:2001").Length);
    }

    [Fact]
    public void Recently_changed_finds_only_what_changed()
    {
        var index = Tree.Index;
        Assert.Empty(Find("rc:today")); // not recorded: nothing changed as far as the index knows
        index.Configure(dateAccessed: false, folderSizes: false, recentChanges: true);
        index.SetRecentChange(Tree.Id(@"C:\Music\track.wav"), TreeBuilder.Ft(2024, 5, 15, 11));
        index.SetRecentChange(Tree.Id(@"C:\Docs"), TreeBuilder.Ft(2024, 5, 15, 8));
        index.SetRecentChange(Tree.Id(@"D:\clip.mkv"), TreeBuilder.Ft(2024, 5, 1));

        Assert.Equal(["Docs", "track.wav"], Names("rc:today"));
        Assert.Equal(["clip.mkv", "Docs", "track.wav"], Names("rc:thismonth", SortKey.DateRecentlyChanged));
        Assert.Equal(["track.wav"], Names("file: rc:today"));
        Assert.Equal(Find("").Length - 3, Find("!rc:thismonth").Length); // never changed: unknown, so not "this month"
    }

    [Fact]
    public void Size_finds_folders_once_folder_sizes_are_on()
    {
        Assert.DoesNotContain("Archive", Names("size:>100mb")); // folders have no size yet
        Tree.Index.Configure(dateAccessed: false, folderSizes: true, recentChanges: false);
        Assert.Equal(["Archive", "C:", "Docs", "old.zip"], Names("size:>100mb"));
        var bySize = Names("folder: size:>0", SortKey.Size, descending: true);
        Assert.Equal("C:", bySize[0]);
        Assert.Equal("Docs", bySize[1]);
    }

    [Fact]
    public void Date_accessed_is_read_from_the_disk_when_not_indexed()
    {
        string dir = Directory.CreateTempSubdirectory("anything-da-").FullName;
        try
        {
            var old = Path.Combine(dir, "old.txt");
            var recent = Path.Combine(dir, "recent.txt");
            File.WriteAllText(old, "x");
            File.WriteAllText(recent, "y");
            File.SetLastAccessTimeUtc(old, new DateTime(2019, 6, 1, 12, 0, 0, DateTimeKind.Utc));
            File.SetLastAccessTimeUtc(recent, new DateTime(2023, 6, 1, 12, 0, 0, DateTimeKind.Utc));

            var index = new FileIndex();
            int root = index.AddRoot(dir);
            index.Add("old.txt", root, FileAttributes.Archive, 1, 0, 0);
            index.Add("recent.txt", root, FileAttributes.Archive, 1, 0, 0);
            index.Add("missing.txt", root, FileAttributes.Archive, 1, 0, 0);
            Assert.False(index.HasDateAccessed);

            var engine = new SearchEngine { SortCacheThreshold = 0 }; // even where orders are presorted: never for this
            string[] Run(string q, SortKey sort = SortKey.Name, bool desc = false)
                => engine.Search(index, q, new SearchOptions(), sort, desc, CancellationToken.None).Ids.Select(index.Name).ToArray();

            Assert.Equal(["old.txt"], Run("da:2019"));
            Assert.Equal(["recent.txt"], Run("da:2023"));
            Assert.Equal(["missing.txt", "old.txt", "recent.txt"], Run("file:", SortKey.DateAccessed)); // unknown (gone) first
            Assert.Equal(["recent.txt", "old.txt", "missing.txt"], Run("file:", SortKey.DateAccessed, desc: true));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
