using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// The index manager following the plan, with drives that are really temp folders (<see cref="FakeDrive"/>), so
/// no real drive is read and no device notifications are involved.
/// </summary>
public sealed class DriveIndexingManagerTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anything-drives-").FullName;
    readonly string _dbDir = Directory.CreateTempSubdirectory("anything-drives-db-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    string Db => Path.Combine(_dbDir, "Anything.db");

    string Dir(string relative) => Path.Combine(_root, relative);

    static bool Has(IndexManager mgr, string path) => Paths(mgr.Index).Contains(path, StringComparer.OrdinalIgnoreCase);

    static IndexedRootInfo RootOf(IndexManager mgr, string path) =>
        Assert.Single(mgr.GetRoots(), r => string.Equals(r.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Folder_listed_inside_an_excluded_folder_is_indexed()
    {
        string outer = Dir("outer"), inner = Path.Combine(outer, "inner");
        Write(Path.Combine(outer, "a.txt"));
        Write(Path.Combine(inner, "b.txt"));
        Write(Path.Combine(inner, "skip", "c.txt"));
        Write(Path.Combine(inner, "node_modules", "d.txt"));
        Write(Path.Combine(inner, "deep", "e.txt"));
        var settings = new IndexSettings
        {
            IndexNtfsVolumes = false,
            Folders = [inner],
            ExcludeFolders = [outer, Path.Combine(inner, "skip"), "node_modules"],
            ExcludePatterns = ["*.tmp"],
            DatabasePath = Db,
        };
        Write(Path.Combine(inner, "old.tmp"));

        using var mgr = Manager(() => []);
        mgr.Start(settings);
        WaitUntilSettled(mgr);
        // The excluded ancestor does not hide it; the rules about what is inside it still apply.
        Assert.Equal(Expected(inner, "b.txt", "deep", @"deep\e.txt"), Paths(mgr.Index));
        Assert.Equal(RootStatus.Ready, RootOf(mgr, inner).Status);

        // Live changes follow the same rules.
        Write(Path.Combine(inner, "skip", "ignored.txt"));
        Write(Path.Combine(inner, "late.tmp"));
        string created = Path.Combine(inner, "deep", "new.txt");
        Write(created);
        Assert.True(WaitFor(() => { mgr.FlushPendingChanges(); return Has(mgr, created); }), "created file not indexed");
        Assert.False(Has(mgr, Path.Combine(inner, "skip", "ignored.txt")));
        Assert.False(Has(mgr, Path.Combine(inner, "late.tmp")));
        Assert.False(Has(mgr, Path.Combine(outer, "a.txt")));
    }

    [Fact]
    public void Left_out_drive_still_indexes_the_folders_listed_inside_it()
    {
        // The user case: C:\ on the exclude list, Desktop and Downloads under Folders.
        string vol = Dir("vol"), me = Path.Combine(vol, "Users", "me");
        string desktop = Path.Combine(me, "Desktop"), downloads = Path.Combine(me, "Downloads");
        Write(Path.Combine(vol, "Windows", "w.txt"));
        Write(Path.Combine(me, "Documents", "z.txt"));
        Write(Path.Combine(desktop, "x.txt"));
        Write(Path.Combine(desktop, "node_modules", "n.txt"));
        Write(Path.Combine(downloads, "y.txt"));
        var drive = FakeDrive(vol);
        var settings = new IndexSettings
        {
            ExcludeFolders = [vol + "\\", "node_modules"],
            Folders = [desktop, downloads],
            DatabasePath = Db,
        };
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(settings, drive));

        using var mgr = Manager(() => [drive]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);

        var expected = Expected(desktop, "x.txt").Concat(Expected(downloads, "y.txt")).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, Paths(mgr.Index));

        var roots = mgr.GetRoots();
        Assert.Equal(2, roots.Count);
        foreach (var path in new[] { desktop, downloads })
        {
            var r = RootOf(mgr, path);
            Assert.Equal(RootStatus.Ready, r.Status);
            Assert.Equal(2, r.Items);
            Assert.Equal("scan", r.Method);
            Assert.False(r.Network);
        }
        Assert.DoesNotContain(roots, r => string.Equals(r.Path.TrimEnd('\\'), vol, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Drive_that_appears_is_indexed_and_dropped_when_it_goes_away()
    {
        string usb = Dir("usb"), net = Dir("net");
        Write(Path.Combine(usb, "photo.jpg"));
        Write(Path.Combine(usb, "dcim", "img1.jpg"));
        Write(Path.Combine(net, "share.doc"));
        var gate = new object();
        var drives = new List<DriveEntry>();
        IReadOnlyList<DriveEntry> Current() { lock (gate) return [.. drives]; }
        var settings = new IndexSettings { DatabasePath = Db };

        using var mgr = Manager(Current);
        mgr.Start(settings);
        WaitUntilSettled(mgr);
        Assert.Empty(Paths(mgr.Index));
        Assert.Empty(mgr.GetRoots());

        bool Settle(Func<bool> done) => WaitFor(() =>
        {
            mgr.CheckDrivesNow();
            mgr.WaitForBackgroundWork(5000);
            return done();
        }, 20_000);

        // Plugged in / mapped.
        lock (gate)
        {
            drives.Add(FakeDrive(usb, DriveKind.Removable));
            drives.Add(FakeDrive(net, DriveKind.Network));
        }
        var usbPaths = Expected(usb, "photo.jpg", "dcim", @"dcim\img1.jpg");
        var netPaths = Expected(net, "share.doc");
        var both = usbPaths.Concat(netPaths).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(Settle(() => Paths(mgr.Index).Length == both.Length && mgr.GetRoots().All(r => r.Status == RootStatus.Ready)),
            $"new drives not indexed: {string.Join(", ", Paths(mgr.Index))} {mgr.LastError}");
        Assert.Equal(both, Paths(mgr.Index));

        var usbRoot = RootOf(mgr, usb);
        Assert.Equal(DriveKind.Removable, usbRoot.Kind);
        Assert.False(usbRoot.Network);
        Assert.Equal(usbPaths.Length, usbRoot.Items);
        var netRoot = RootOf(mgr, net);
        Assert.Equal(DriveKind.Network, netRoot.Kind);
        Assert.True(netRoot.Network);
        Assert.Equal(netPaths.Length, netRoot.Items);

        // Unplugged: its entries go.
        lock (gate) drives.RemoveAll(d => d.Root.StartsWith(usb, StringComparison.OrdinalIgnoreCase));
        Assert.True(Settle(() => PathsUnder(mgr.Index, usb).Length == 0 && mgr.GetRoots().Count == 1), "unplugged drive not dropped");
        Assert.Equal(netPaths, Paths(mgr.Index));
        Assert.Equal(netPaths.Length, mgr.Index.LiveCount);

        // Unmapped.
        lock (gate) drives.Clear();
        Assert.True(Settle(() => mgr.Index.LiveCount == 0 && mgr.GetRoots().Count == 0), "unmapped drive not dropped");

        // And back again.
        lock (gate) drives.Add(FakeDrive(usb, DriveKind.Removable));
        Assert.True(Settle(() => Paths(mgr.Index).Length == usbPaths.Length), "drive plugged back in not indexed");
        Assert.Equal(usbPaths, Paths(mgr.Index));
    }

    [Fact]
    public void Get_roots_counts_match_the_index()
    {
        string drive = Dir("drive"), extra = Dir("extra");
        Write(Path.Combine(drive, "a.txt"));
        Write(Path.Combine(drive, "one", "b.txt"));
        Write(Path.Combine(drive, "one", "two", "c.txt"));
        Write(Path.Combine(extra, "d.txt"));
        Write(Path.Combine(extra, "e.txt"));
        var settings = new IndexSettings { Folders = [extra], DatabasePath = Db };

        using var mgr = Manager(() => [FakeDrive(drive)]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);

        var roots = mgr.GetRoots();
        Assert.Equal(2, roots.Count);
        foreach (var r in roots)
        {
            Assert.Equal(PathsUnder(mgr.Index, r.Path).Length, r.Items);
            Assert.Equal(RootStatus.Ready, r.Status);
            Assert.Null(r.Detail);
        }
        Assert.Equal(6, RootOf(mgr, drive).Items);
        Assert.Equal(3, RootOf(mgr, extra).Items);
        Assert.Equal(mgr.Index.LiveCount, roots.Sum(r => r.Items));

        // Counts follow live changes.
        Write(Path.Combine(drive, "one", "new.txt"));
        Assert.True(WaitFor(() => { mgr.FlushPendingChanges(); return RootOf(mgr, drive).Items == 7; }), "count not updated");
        File.Delete(Path.Combine(extra, "d.txt"));
        Assert.True(WaitFor(() => { mgr.FlushPendingChanges(); return RootOf(mgr, extra).Items == 2; }), "count not updated after delete");
        Assert.Equal(mgr.Index.LiveCount, mgr.GetRoots().Sum(r => r.Items));
    }

    [Fact]
    public void Deleted_folder_ends_up_empty_not_kept()
    {
        string gone = Dir("gone");
        Write(Path.Combine(gone, "a.txt"));
        Write(Path.Combine(gone, "sub", "b.txt"));
        var settings = new IndexSettings { IndexNtfsVolumes = false, Folders = [gone], DatabasePath = Db };

        using (var mgr = Manager(() => []))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(gone, "a.txt", "sub", @"sub\b.txt"), Paths(mgr.Index));
        }

        // Deleted while the app was closed: its drive is there, so this is not "offline".
        Directory.Delete(gone, true);
        using (var mgr = Manager(() => []))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(gone), Paths(mgr.Index));
            var root = RootOf(mgr, gone);
            Assert.Equal(RootStatus.Ready, root.Status);
            Assert.Equal(1, root.Items);
        }
    }

    [Fact]
    public void Deleted_network_folder_is_not_resumed_with_its_old_entries()
    {
        string share = Dir("share");
        Write(Path.Combine(share, "a.txt"));
        Write(Path.Combine(share, "sub", "b.txt"));
        var settings = new IndexSettings { NetworkRescanMinutes = 60, DatabasePath = Db };
        Func<IReadOnlyList<DriveEntry>> drives = () => [FakeDrive(share, DriveKind.Network)];

        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(share, "a.txt", "sub", @"sub\b.txt"), Paths(mgr.Index));
        }

        // Like \\nas\share\projects deleted on the server while the app was closed: the share answers, the folder is gone.
        Directory.Delete(share, true);
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(share), Paths(mgr.Index));
        }
    }

    [Fact]
    public void Unreachable_folder_keeps_its_entries_offline()
    {
        var letter = UnusedDriveLetter();
        Assert.True(letter.HasValue, "no free drive letter to test with");
        string root = letter + @":\data";
        long scanned = DateTime.UtcNow.AddDays(-1).ToFileTimeUtc();

        // A database from when the drive was there.
        var seed = new FileIndex();
        int id = seed.Add(root, FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        int sub = seed.Add("sub", id, FileAttributes.Directory, -1, 0, 0);
        seed.Add("a.txt", id, FileAttributes.Normal, 1, 0, 0);
        seed.Add("b.txt", sub, FileAttributes.Normal, 1, 0, 0);
        IndexDatabase.Save(Db, seed, [new DbRoot { Path = root, Kind = RootKind.Folder, RootId = id, NextUsn = scanned }]);

        var settings = new IndexSettings { IndexNtfsVolumes = false, Folders = [root], DatabasePath = Db };
        var expected = Expected(root, "a.txt", "sub", @"sub\b.txt");
        using (var mgr = Manager(() => []))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(expected, Paths(mgr.Index));
            var info = RootOf(mgr, root);
            Assert.Equal(RootStatus.Offline, info.Status);
            Assert.Equal(4, info.Items);
            Assert.False(string.IsNullOrEmpty(info.Detail));
        }

        // Still in the database, with the time of the last real scan.
        var again = new FileIndex();
        var loaded = Assert.Single(IndexDatabase.Load(Db, again)!);
        Assert.Equal(expected, Paths(again));
        Assert.Equal(scanned, loaded.NextUsn);
    }

    [Fact]
    public void Last_scan_time_is_saved_and_a_recent_network_root_resumes_without_a_rescan()
    {
        string share = Dir("share"), local = Dir("local");
        Write(Path.Combine(share, "a.txt"));
        Write(Path.Combine(share, "sub", "b.txt"));
        Write(Path.Combine(local, "c.txt"));
        var settings = new IndexSettings { NetworkRescanMinutes = 60, DatabasePath = Db };
        Func<IReadOnlyList<DriveEntry>> drives = () => [FakeDrive(share, DriveKind.Network), FakeDrive(local, DriveKind.Fixed)];
        var sharePaths = Expected(share, "a.txt", "sub", @"sub\b.txt");

        long before = DateTime.UtcNow.ToFileTimeUtc();
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(sharePaths, PathsUnder(mgr.Index, share));
            Assert.True(RootOf(mgr, share).Network);
            Assert.Equal(RootStatus.Ready, RootOf(mgr, share).Status);
        }
        long after = DateTime.UtcNow.ToFileTimeUtc();

        // Folder roots keep the time of their last full scan in the database's journal position.
        var loaded = IndexDatabase.Load(Db, new FileIndex())!;
        var shareRow = Assert.Single(loaded, l => string.Equals(l.Path, share, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RootKind.Folder, shareRow.Kind);
        Assert.InRange(shareRow.NextUsn, before, after);
        Assert.InRange(Assert.Single(loaded, l => string.Equals(l.Path, local, StringComparison.OrdinalIgnoreCase)).NextUsn, before, after);

        // Files added while the app is closed: a local root is rescanned at startup, a network root scanned
        // within the rescan interval is not (its entries are reused, then changes are watched).
        string missedOnShare = Path.Combine(share, "added-while-closed.txt");
        string seenLocally = Path.Combine(local, "added-while-closed.txt");
        Write(missedOnShare);
        Write(seenLocally);
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(sharePaths, PathsUnder(mgr.Index, share));
            Assert.True(Has(mgr, seenLocally), "local root not rescanned");
            Assert.Equal(RootStatus.Ready, RootOf(mgr, share).Status);

            string live = Path.Combine(share, "live.txt");
            Write(live);
            Assert.True(WaitFor(() => { mgr.FlushPendingChanges(); return Has(mgr, live); }), "resumed network root is not watched");
        }

        // Not rescanned, so the saved scan time is still the first one.
        var reloaded = IndexDatabase.Load(Db, new FileIndex())!;
        Assert.Equal(shareRow.NextUsn, Assert.Single(reloaded, l => string.Equals(l.Path, share, StringComparison.OrdinalIgnoreCase)).NextUsn);
    }
}
