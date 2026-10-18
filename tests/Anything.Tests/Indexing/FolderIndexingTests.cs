using System.Diagnostics;
using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

public sealed class FolderIndexingTests : IDisposable
{
    readonly string _root;
    readonly string _dbDir;

    public FolderIndexingTests()
    {
        _root = Directory.CreateTempSubdirectory("anything-scan-").FullName;
        _dbDir = Directory.CreateTempSubdirectory("anything-scan-db-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "skip"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "b.tmp"), "excluded by pattern");
        File.WriteAllText(Path.Combine(_root, "sub", "c.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "sub", "deep", "d.txt"), "dd");
        File.WriteAllText(Path.Combine(_root, "skip", "e.txt"), "excluded by folder path");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "f.txt"), "excluded by folder name");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    IndexSettings Settings() => new()
    {
        IndexNtfsVolumes = false,
        Folders = [_root],
        ExcludePatterns = ["*.tmp"],
        ExcludeFolders = [Path.Combine(_root, "skip"), "node_*"],
        DatabasePath = Path.Combine(_dbDir, "Anything.db"),
    };

    string[] Expected(params string[] extra) =>
        new[] { "", "a.txt", "sub", @"sub\c.txt", @"sub\deep", @"sub\deep\d.txt" }.Concat(extra)
            .Select(r => r.Length == 0 ? _root + "\\" : Path.Combine(_root, r))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    static string[] Paths(FileIndex index)
    {
        index.Lock.EnterReadLock();
        try
        {
            return Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { index.Lock.ExitReadLock(); }
    }

    static bool WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Scanner_builds_tree_with_exclusions()
    {
        var filter = new ExclusionFilter(Settings());
        var tree = FolderScanner.Scan(_root, filter, CancellationToken.None);
        var index = new FileIndex();
        int rootId = -1;
        index.BulkAdd(b => rootId = FolderScanner.AddTree(b, tree, _root, FileIndex.NoParent, FileAttributes.Directory, 0, 0, null));

        Assert.Equal(Expected(), Paths(index));
        int a = Enumerable.Range(0, index.Count).Single(i => index.Name(i) == "a.txt");
        Assert.Equal(5, index.Size(a));
        Assert.True(index.Modified(a) > 0);
        Assert.Equal(-1, index.Size(Enumerable.Range(0, index.Count).Single(i => index.Name(i) == "sub")));
    }

    [Fact]
    public void Exclusion_filter_rules()
    {
        var f = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = [@"C:\Windows\", @"*\bin\obj", ".git"],
            ExcludePatterns = ["*.tmp", "thumbs.db"],
            ExcludeHidden = true,
        });
        Assert.True(f.ExcludesFolder(@"C:\Windows", "Windows"));
        Assert.True(f.ExcludesFolder(@"c:\windows\system32", "system32"));
        Assert.False(f.ExcludesFolder(@"C:\WindowsApps", "WindowsApps"));
        Assert.True(f.ExcludesFolder(@"D:\src\bin\obj", "obj"));
        Assert.True(f.ExcludesFolder(@"D:\src\.git", ".git"));
        Assert.True(f.Excludes("Thumbs.DB", FileAttributes.Normal, null));
        Assert.True(f.Excludes("x.TMP", FileAttributes.Normal, null));
        Assert.True(f.Excludes("visible.txt", FileAttributes.Hidden, null));
        Assert.False(f.Excludes("visible.txt", FileAttributes.Archive, null));
        Assert.True(ExclusionFilter.None.IsEmpty);
    }

    [Fact]
    public void Manager_scans_folder_watches_changes_and_persists()
    {
        using (var mgr = new IndexManager())
        {
            mgr.Start(Settings());
            Assert.True(WaitFor(() => mgr.State == IndexState.Ready), $"state {mgr.State}: {mgr.StatusText} {mgr.LastError}");
            Assert.Equal(Expected(), Paths(mgr.Index));

            // Created file shows up through the FileSystemWatcher.
            string created = Path.Combine(_root, "sub", "new file.txt");
            File.WriteAllText(created, "12345678");
            Assert.True(WaitFor(() => Paths(mgr.Index).Contains(created, StringComparer.OrdinalIgnoreCase)), "created file not indexed");
            Assert.True(WaitFor(() => SizeOf(mgr.Index, "new file.txt") == 8), "size not refreshed");

            // Excluded pattern stays out even for live changes.
            File.WriteAllText(Path.Combine(_root, "live.tmp"), "x");

            // New folder with content (moved in) is scanned.
            string staging = Path.Combine(_dbDir, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "inner"));
            File.WriteAllText(Path.Combine(staging, "inner", "z.txt"), "z");
            Directory.Move(staging, Path.Combine(_root, "movedin"));
            Assert.True(WaitFor(() => Paths(mgr.Index).Contains(Path.Combine(_root, "movedin", "inner", "z.txt"), StringComparer.OrdinalIgnoreCase)), "moved-in folder not indexed");

            // Rename and delete.
            string renamed = Path.Combine(_root, "renamed.txt");
            File.Move(created, renamed);
            Assert.True(WaitFor(() => Paths(mgr.Index).Contains(renamed, StringComparer.OrdinalIgnoreCase) && !Paths(mgr.Index).Contains(created, StringComparer.OrdinalIgnoreCase)), "rename not applied");
            File.Delete(Path.Combine(_root, "a.txt"));
            Assert.True(WaitFor(() => !Paths(mgr.Index).Contains(Path.Combine(_root, "a.txt"), StringComparer.OrdinalIgnoreCase)), "delete not applied");
            Assert.DoesNotContain(Path.Combine(_root, "live.tmp"), Paths(mgr.Index), StringComparer.OrdinalIgnoreCase);
        }

        // Disposing saved the database; a new manager loads it instantly, then rescans and swaps without duplicates.
        Assert.True(File.Exists(Settings().DatabasePath));
        var expectedNow = Expected("renamed.txt", "movedin", @"movedin\inner", @"movedin\inner\z.txt")
            .Where(p => !p.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase)).ToArray();

        var loadedOnly = new FileIndex();
        Assert.NotNull(IndexDatabase.Load(Settings().DatabasePath, loadedOnly));
        Assert.Equal(expectedNow, Paths(loadedOnly));

        using (var mgr = new IndexManager())
        {
            mgr.Start(Settings());
            Assert.True(WaitFor(() => mgr.State == IndexState.Ready), $"state {mgr.State}: {mgr.StatusText} {mgr.LastError}");
            Assert.Equal(expectedNow, Paths(mgr.Index));
            Assert.Equal(expectedNow.Length, mgr.Index.LiveCount);

            mgr.Rebuild();
            Assert.True(WaitFor(() => mgr.State == IndexState.Ready && mgr.Index.LiveCount == expectedNow.Length), $"rebuild: {mgr.State} {mgr.Index.LiveCount}");
            Assert.Equal(expectedNow, Paths(mgr.Index));
        }
    }

    static long SizeOf(FileIndex index, string name)
    {
        for (int i = 0; i < index.Count; i++)
            if (index.IsAlive(i) && index.Name(i) == name) return index.Size(i);
        return long.MinValue;
    }
}
