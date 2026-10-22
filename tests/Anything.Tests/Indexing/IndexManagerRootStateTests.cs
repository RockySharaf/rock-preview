using System.Reflection;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// <see cref="IndexManager.StateOfRoot"/>: whether the index holds a root, with folders from Settings › Folders that are
/// really temp folders.
/// </summary>
public sealed class IndexManagerRootStateTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anything-rootstate-").FullName;
    readonly string _dbDir = Directory.CreateTempSubdirectory("anything-rootstate-db-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    string Db => Path.Combine(_dbDir, "Anything.db");

    string Dir(string relative) => Path.Combine(_root, relative);

    IndexSettings FoldersOnly(params string[] folders) =>
        new() { IndexNtfsVolumes = false, Folders = [.. folders], DatabasePath = Db };

    [Fact]
    public void No_root_before_start_or_after_dispose()
    {
        string docs = Dir("docs");
        Write(Path.Combine(docs, "a.txt"));
        var mgr = Manager(() => []);
        try
        {
            Assert.Null(mgr.StateOfRoot(docs));

            mgr.Start(FoldersOnly(docs));
            WaitUntilSettled(mgr);
            Assert.NotNull(mgr.StateOfRoot(docs));
        }
        finally { mgr.Dispose(); }

        Assert.Null(mgr.StateOfRoot(docs));
    }

    [Fact]
    public void Scanned_folder_is_ready_with_entries_and_anything_else_is_not_a_root()
    {
        string docs = Dir("docs"), other = Dir("other");
        Write(Path.Combine(docs, "a.txt"));
        Write(Path.Combine(docs, "sub", "b.txt"));
        Write(Path.Combine(other, "c.txt"));
        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(docs));
        WaitUntilSettled(mgr);

        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(docs));
        // The app asks through the interface (the demo index has no roots at all).
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), ((IIndexManager)mgr).StateOfRoot(docs));

        // A folder Settings doesn't list, and a folder inside the root, are not roots.
        Assert.Null(mgr.StateOfRoot(other));
        Assert.Null(mgr.StateOfRoot(Path.Combine(docs, "sub")));
        Assert.Null(mgr.StateOfRoot(""));
    }

    [Fact]
    public void Folder_taken_out_of_settings_is_no_longer_a_root()
    {
        string keep = Dir("keep"), drop = Dir("drop");
        Write(Path.Combine(keep, "a.txt"));
        Write(Path.Combine(drop, "b.txt"));
        var settings = FoldersOnly(keep, drop);
        using var mgr = Manager(() => []);
        mgr.Start(settings);
        WaitUntilSettled(mgr);
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(drop));

        settings.Folders = [keep];
        mgr.Apply(settings);
        Assert.True(WaitFor(() => mgr.StateOfRoot(drop) == null), "the folder taken out is still a root");
        WaitUntilSettled(mgr);
        Assert.Null(mgr.StateOfRoot(drop));
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(keep));
    }

    [Fact]
    public void Drive_nobody_can_reach_is_offline_with_no_entries()
    {
        var letter = UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(letter + @":\"));
        WaitUntilSettled(mgr);

        var offline = new RootState(RootStatus.Offline, HasEntries: false, Complete: false);
        Assert.Equal(offline, mgr.StateOfRoot(letter + @":\"));
        Assert.Equal(offline, mgr.StateOfRoot(letter + ":"));
        Assert.Equal(offline, mgr.StateOfRoot(char.ToLowerInvariant(letter.Value) + ":/"));
    }

    [Fact]
    public void Folder_loaded_from_the_database_keeps_its_entries_while_offline()
    {
        var letter = UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        string root = letter + @":\data";
        var seed = new FileIndex();
        int id = seed.Add(root, FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        seed.Add("a.txt", id, FileAttributes.Normal, 1, 0, 0);
        IndexDatabase.Save(Db, seed, [new DbRoot { Path = root, Kind = RootKind.Folder, RootId = id, NextUsn = DateTime.UtcNow.AddDays(-1).ToFileTimeUtc() }]);

        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(root));
        WaitUntilSettled(mgr);
        Assert.Equal(new RootState(RootStatus.Offline, HasEntries: true, Complete: true), mgr.StateOfRoot(root));
    }

    [Fact]
    public void Root_comes_back_after_a_rebuild()
    {
        string docs = Dir("docs");
        Write(Path.Combine(docs, "a.txt"));
        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(docs));
        WaitUntilSettled(mgr);

        mgr.Rebuild();
        WaitUntilSettled(mgr);
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(docs));
    }

    [Fact]
    public void Name_matches_in_any_case_with_or_without_the_trailing_backslash()
    {
        string docs = Dir("Docs");
        Write(Path.Combine(docs, "a.txt"));
        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(docs));
        WaitUntilSettled(mgr);

        var ready = new RootState(RootStatus.Ready, HasEntries: true, Complete: true);
        Assert.Equal(ready, mgr.StateOfRoot(docs));
        Assert.Equal(ready, mgr.StateOfRoot(docs + "\\"));
        Assert.Equal(ready, mgr.StateOfRoot(docs.ToUpperInvariant()));
        Assert.Equal(ready, mgr.StateOfRoot(docs.ToLowerInvariant() + "\\"));
        Assert.Equal(ready, mgr.StateOfRoot(docs.Replace('\\', '/')));
    }

    [Fact]
    public void First_scan_of_a_folder_has_entries_that_are_not_complete_until_it_finishes()
    {
        string docs = Dir("docs");
        for (int i = 0; i < 20; i++) Write(Path.Combine(docs, $"sub{i}", "a.txt"));
        using var mgr = Manager(() => []);
        // Seen while the scan adds its entries: the index raises Changed on the scanning thread as each folder is added.
        var gate = new object();
        RootState? seen = null;
        mgr.Index.Changed += () =>
        {
            if (mgr.StateOfRoot(docs) is not { HasEntries: true } state) return;
            lock (gate) seen ??= state;
        };
        mgr.Start(FoldersOnly(docs));
        WaitUntilSettled(mgr);

        lock (gate) Assert.Equal(new RootState(RootStatus.Scanning, HasEntries: true, Complete: false), seen);
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(docs));
    }

    [Fact]
    public void First_scan_cut_short_and_saved_is_not_complete()
    {
        var letter = UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        string root = letter + @":\data";
        var seed = new FileIndex();
        int id = seed.Add(root, FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        seed.Add("a.txt", id, FileAttributes.Normal, 1, 0, 0);
        // Saved on exit partway through its first scan: no scan time.
        IndexDatabase.Save(Db, seed, [new DbRoot { Path = root, Kind = RootKind.Folder, RootId = id, NextUsn = 0 }]);

        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(root));
        WaitUntilSettled(mgr);
        Assert.Equal(new RootState(RootStatus.Offline, HasEntries: true, Complete: false), mgr.StateOfRoot(root));
    }

    [Fact]
    public void Root_let_go_of_for_an_eject_counts_as_none()
    {
        string docs = Dir("docs");
        Write(Path.Combine(docs, "a.txt"));
        using var mgr = Manager(() => []);
        mgr.Start(FoldersOnly(docs));
        WaitUntilSettled(mgr);
        // The manager has no hook for its roots: its list, as other tests reach private fields.
        var roots = typeof(IndexManager).GetField("_roots", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr) as List<RootIndexer>;
        Assert.NotNull(roots);
        RootIndexer root;
        lock (roots) root = Assert.Single(roots);

        // Just let go of (so the drive tick leaves it alone for a minute).
        root.ReleasedAtTicks = Environment.TickCount64;
        root.Released = true;
        Assert.Null(mgr.StateOfRoot(docs));
        root.Released = false;
        Assert.Equal(new RootState(RootStatus.Ready, HasEntries: true, Complete: true), mgr.StateOfRoot(docs));
    }
}
