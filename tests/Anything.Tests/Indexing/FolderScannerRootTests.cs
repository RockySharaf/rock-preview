using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

public sealed class FolderScannerRootTests : IDisposable
{
    const int ErrorPathNotFound = 3;
    readonly string _root = Directory.CreateTempSubdirectory("anything-root-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Missing_root_throws_root_unavailable()
    {
        string missing = Path.Combine(_root, "not-there");
        var ex = Assert.Throws<RootUnavailableException>(() => FolderScanner.Scan(missing, ExclusionFilter.None, CancellationToken.None));
        Assert.Equal(missing, ex.Path);
        Assert.Equal(ErrorPathNotFound, ex.Error);
        Assert.False(ex.Offline); // the drive is there: the folder was deleted, nothing to keep
        Assert.NotEqual(0, FolderScanner.ProbeRoot(missing));
    }

    [Fact]
    public void Missing_root_with_failOnBrokenConnection_throws_too()
    {
        string missing = Path.Combine(_root, "not-there");
        Assert.Throws<RootUnavailableException>(() =>
            FolderScanner.Scan(missing, ExclusionFilter.None, CancellationToken.None, failOnBrokenConnection: true));
    }

    [Fact]
    public void Root_on_a_missing_drive_is_offline()
    {
        var letter = DriveIndexingTestSupport.UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        string path = letter + @":\data";
        var ex = Assert.Throws<RootUnavailableException>(() => FolderScanner.Scan(path, ExclusionFilter.None, CancellationToken.None));
        Assert.True(ex.Offline, $"error {ex.Error}");
        Assert.True(FolderScanner.IsOfflineError(ex.Error));
    }

    [Fact]
    public void Empty_root_does_not_throw()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(0, FolderScanner.ProbeRoot(empty));

        var tree = FolderScanner.Scan(empty, ExclusionFilter.None, CancellationToken.None, failOnBrokenConnection: true);
        Assert.Empty(tree.Entries);
        Assert.Equal(empty, tree.FullPath);

        // Progressive scans of an empty root are fine too.
        int calls = 0;
        FolderScanner.Scan(empty, ExclusionFilter.None, CancellationToken.None, onScanned: _ => calls++, rootIndexId: 0);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Root_with_only_excluded_entries_is_empty_not_unavailable()
    {
        string dir = Path.Combine(_root, "only-tmp");
        DriveIndexingTestSupport.Write(Path.Combine(dir, "a.tmp"));
        var filter = new ExclusionFilter(new IndexSettings { ExcludePatterns = ["*.tmp"] });
        Assert.Empty(FolderScanner.Scan(dir, filter, CancellationToken.None).Entries);
    }

    [Fact]
    public void Path_not_found_is_offline_only_when_the_volume_is_missing()
    {
        string missing = Path.Combine(_root, "not-there");
        Assert.False(FolderScanner.IsOffline(missing, ErrorPathNotFound));
        // The drive root itself not found: the drive is gone.
        Assert.True(FolderScanner.IsOffline(@"C:\", ErrorPathNotFound));

        var letter = DriveIndexingTestSupport.UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        Assert.True(FolderScanner.IsOffline(letter + @":\data\sub", ErrorPathNotFound));
    }

    [Theory]
    [InlineData(15, true)]   // ERROR_INVALID_DRIVE
    [InlineData(21, true)]   // ERROR_NOT_READY
    [InlineData(53, true)]   // ERROR_BAD_NETPATH
    [InlineData(64, true)]   // ERROR_NETNAME_DELETED
    [InlineData(67, true)]   // ERROR_BAD_NET_NAME
    [InlineData(1222, true)] // ERROR_NO_NETWORK
    [InlineData(2250, true)] // ERROR_NOT_CONNECTED
    [InlineData(2, false)]   // ERROR_FILE_NOT_FOUND
    [InlineData(5, false)]   // ERROR_ACCESS_DENIED
    [InlineData(0, false)]
    public void Offline_errors(int error, bool offline)
    {
        Assert.Equal(offline, FolderScanner.IsOfflineError(error));
        Assert.Equal(offline, FolderScanner.IsOffline(Path.Combine(_root, "x"), error));
    }

    [Fact]
    public void Root_unavailable_exception_reports_offline_from_the_error()
    {
        Assert.True(new RootUnavailableException(@"\\nas\share", 53).Offline);
        Assert.False(new RootUnavailableException(Path.Combine(_root, "gone"), ErrorPathNotFound).Offline);
        Assert.False(new RootUnavailableException(_root, 5).Offline);
        Assert.Contains(_root, new RootUnavailableException(_root, 5).Message);
    }
}

public sealed class CountByRootTests
{
    const FileAttributes Dir = FileAttributes.Directory;

    [Fact]
    public void Empty_index_has_no_roots()
    {
        Assert.Empty(new FileIndex().CountByRoot());
    }

    [Fact]
    public void Counts_live_entries_per_root_including_the_root()
    {
        var index = new FileIndex();
        int c = index.AddRoot("C:");
        int users = index.Add("Users", c, Dir, -1, 0, 0);
        int me = index.Add("me", users, Dir, -1, 0, 0);
        index.Add("a.txt", me, FileAttributes.Normal, 1, 0, 0);
        int b = index.Add("b.txt", me, FileAttributes.Normal, 1, 0, 0);
        int win = index.Add("Windows", c, Dir, -1, 0, 0);
        index.Add("w1.dll", win, FileAttributes.Normal, 1, 0, 0);
        index.Add("w2.dll", win, FileAttributes.Normal, 1, 0, 0);

        int d = index.AddRoot(@"D:\Data");
        index.Add("x.txt", d, FileAttributes.Normal, 1, 0, 0);

        int e = index.AddRoot("E:");

        var counts = index.CountByRoot();
        Assert.Equal(3, counts.Count);
        Assert.Equal(8, counts[c]);
        Assert.Equal(2, counts[d]);
        Assert.Equal(1, counts[e]);
        Assert.Equal(index.LiveCount, counts.Values.Sum());

        // A removed file and a removed folder (with everything in it) no longer count.
        index.Remove(b);
        index.Remove(win);
        counts = index.CountByRoot();
        Assert.Equal(4, counts[c]);
        Assert.Equal(2, counts[d]);
        Assert.Equal(index.LiveCount, counts.Values.Sum());

        // A removed root disappears.
        index.Remove(d);
        counts = index.CountByRoot();
        Assert.False(counts.ContainsKey(d));
        Assert.Equal(4, counts[c]);
        Assert.Equal(index.LiveCount, counts.Values.Sum());
    }

    [Fact]
    public void A_rescanned_root_counts_only_its_new_copy()
    {
        // A rescan adds a fresh copy of the root and then removes the old one.
        var index = new FileIndex();
        int old = index.AddRoot(@"D:\Data");
        for (int i = 0; i < 5; i++) index.Add($"f{i}", old, FileAttributes.Normal, 1, 0, 0);
        int fresh = index.AddRoot(@"D:\Data");
        for (int i = 0; i < 3; i++) index.Add($"g{i}", fresh, FileAttributes.Normal, 1, 0, 0);
        index.Remove(old);

        var counts = index.CountByRoot();
        Assert.Single(counts);
        Assert.Equal(4, counts[fresh]);
    }

    [Fact]
    public void Children_added_before_their_parent_are_counted()
    {
        // Database loads can add a child first and set its parent afterwards.
        var index = new FileIndex();
        int root = -1;
        index.BulkAdd(b =>
        {
            int child = b.Add("late.txt", FileIndex.NoParent, FileAttributes.Normal, 1, 0, 0);
            int folder = b.Add("folder", FileIndex.NoParent, Dir, -1, 0, 0);
            root = b.Add("R:", FileIndex.NoParent, Dir, -1, 0, 0);
            b.SetParent(folder, root);
            b.SetParent(child, folder);
        });
        var counts = index.CountByRoot();
        Assert.Single(counts);
        Assert.Equal(3, counts[root]);
    }

    [Fact]
    public void Matches_a_naive_count_on_a_larger_tree()
    {
        var index = new FileIndex();
        var rnd = new Random(1234);
        var roots = new List<int>();
        var folders = new List<int>();
        for (int r = 0; r < 4; r++)
        {
            int id = index.AddRoot($"{(char)('C' + r)}:");
            roots.Add(id);
            folders.Add(id);
        }
        for (int i = 0; i < 3000; i++)
        {
            int parent = folders[rnd.Next(folders.Count)];
            bool dir = rnd.Next(4) == 0;
            int id = index.Add($"n{i}", parent, dir ? Dir : FileAttributes.Normal, dir ? -1 : 1, 0, 0);
            if (dir) folders.Add(id);
        }
        for (int i = 0; i < 40; i++) index.Remove(rnd.Next(4, index.Count));

        var naive = new Dictionary<int, int>();
        for (int i = 0; i < index.Count; i++)
        {
            if (!index.IsAlive(i)) continue;
            int cur = i;
            while (index.Parent(cur) != FileIndex.NoParent) cur = index.Parent(cur);
            naive[cur] = naive.GetValueOrDefault(cur) + 1;
        }
        Assert.Equal(naive.OrderBy(k => k.Key), index.CountByRoot().OrderBy(k => k.Key));
    }
}
