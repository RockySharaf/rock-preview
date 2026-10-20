using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// The database stays true to the index: never overwritten while it is still being read, and each root remembers the
/// exclusion rules its entries were built with, so a change the index didn't finish applying is finished later.
/// </summary>
public sealed class IndexManagerStampTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anything-stamp-").FullName;
    readonly string _dbDir = Directory.CreateTempSubdirectory("anything-stamp-db-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    string Db => Path.Combine(_dbDir, "Anything.db");

    string Dir(string relative) => Path.Combine(_root, relative);

    [Fact]
    public void Stopping_while_the_database_loads_never_overwrites_it()
    {
        // A database big enough that loading it takes a moment, for a root that is no longer planned.
        var seed = new FileIndex();
        int root = seed.Add(@"Q:\elsewhere", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 200_000; i++) seed.Add("file" + i + ".txt", root, FileAttributes.Normal, i, 0, 0);
        IndexDatabase.Save(Db, seed, [new DbRoot { Path = @"Q:\elsewhere", Kind = RootKind.Folder, RootId = root }]);
        long size = new FileInfo(Db).Length;

        string a = Dir("a");
        Write(Path.Combine(a, "one.txt"));
        var settings = new IndexSettings { DatabasePath = Db };
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var mgr = Manager(() => [FakeDrive(a)]);
            mgr.Start(settings);
            // Applying, then quitting, right away: both used to save the half-attached index over the database.
            mgr.Apply(settings);
        }

        var check = new FileIndex();
        var loaded = IndexDatabase.Load(Db, check);
        Assert.NotNull(loaded);
        // Either untouched (stopped while loading) or saved after a full attach: never emptied.
        Assert.True(check.LiveCount >= 2, $"database emptied: {check.LiveCount} entries (was {size} bytes)");
    }

    [Fact]
    public void Rules_stamps_round_trip_through_the_database()
    {
        var seed = new FileIndex();
        int a = seed.Add(@"X:\a", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        int b = seed.Add(@"X:\b", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        IndexDatabase.Save(Db, seed,
        [
            new DbRoot { Path = @"X:\a", Kind = RootKind.Folder, RootId = a, RulesStamp = 0x1234_5678_9ABC_DEF0 },
            new DbRoot { Path = @"X:\b", Kind = RootKind.Folder, RootId = b },
        ]);
        var loaded = IndexDatabase.Load(Db, new FileIndex())!;
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, loaded.Single(r => r.Path == @"X:\a").RulesStamp);
        Assert.Equal(0UL, loaded.Single(r => r.Path == @"X:\b").RulesStamp);
    }

    [Fact]
    public void Exclusion_stamp_follows_the_rules_and_not_their_order()
    {
        var one = new ExclusionFilter(new IndexSettings { ExcludeFolders = [@"C:\a", "node_modules"], ExcludePatterns = ["*.tmp"] });
        var same = new ExclusionFilter(new IndexSettings { ExcludeFolders = ["NODE_MODULES", @"c:\A\"], ExcludePatterns = ["*.TMP"] });
        var other = new ExclusionFilter(new IndexSettings { ExcludeFolders = [@"C:\a"], ExcludePatterns = ["*.tmp"] });
        var hidden = new ExclusionFilter(new IndexSettings { ExcludeFolders = [@"C:\a", "node_modules"], ExcludePatterns = ["*.tmp"], ExcludeHidden = true });
        Assert.Equal(one.Stamp, same.Stamp);
        Assert.NotEqual(one.Stamp, other.Stamp);
        Assert.NotEqual(one.Stamp, hidden.Stamp);
        Assert.NotEqual(0UL, ExclusionFilter.None.Stamp);
    }

    [Fact]
    public void Network_drive_built_with_other_exclusions_is_rescanned_not_resumed()
    {
        string share = Dir("share");
        Write(Path.Combine(share, "keep.txt"));
        Write(Path.Combine(share, "secret.tmp"));
        Func<IReadOnlyList<DriveEntry>> drives = () => [FakeDrive(share, DriveKind.Network)];
        using (var mgr = Manager(drives))
        {
            mgr.Start(new IndexSettings { DatabasePath = Db, NetworkRescanMinutes = 60 });
            WaitUntilSettled(mgr);
            Assert.Contains(Path.Combine(share, "secret.tmp"), Paths(mgr.Index), StringComparer.OrdinalIgnoreCase);
        }

        // *.tmp left out while the app was closed (or the rescan for it was cut short): scanned minutes ago, but with
        // other rules, so the saved entries must not simply be resumed.
        using (var mgr = Manager(drives))
        {
            mgr.Start(new IndexSettings { DatabasePath = Db, NetworkRescanMinutes = 60, ExcludePatterns = ["*.tmp"] });
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(share, "keep.txt"), Paths(mgr.Index));
        }
    }

    [Fact]
    public void Exclusion_change_applied_in_place_is_finished_after_a_restart()
    {
        string a = Dir("a");
        Write(Path.Combine(a, "keep.txt"));
        Write(Path.Combine(a, "secret.tmp"));
        string share = Dir("share");
        Write(Path.Combine(share, "x.tmp"));
        var settings = new IndexSettings { DatabasePath = Db, NetworkRescanMinutes = 60 };
        Func<IReadOnlyList<DriveEntry>> drives = () => [FakeDrive(a), FakeDrive(share, DriveKind.Network)];
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            settings.ExcludePatterns = ["*.tmp"];
            mgr.Apply(settings);
            Assert.True(WaitFor(() => !Paths(mgr.Index).Any(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))),
                string.Join(" | ", Paths(mgr.Index)));
            WaitUntilSettled(mgr);
        }
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.DoesNotContain(Paths(mgr.Index), p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Path.Combine(a, "keep.txt"), Paths(mgr.Index), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Folder_named_through_a_mapped_letter_is_planned_once_with_its_share_ancestor()
    {
        var z = new DriveEntry(@"Z:\", DriveKind.Network, "", "", true, @"\\fileserver\share", null);
        var settings = new IndexSettings { IndexNetworkDrives = false, Folders = [@"Z:\docs\x", @"\\fileserver\share\docs"] };
        var plan = IndexPlan.Plan(settings, [z], new ExclusionFilter(settings), _ => null);
        Assert.Single(plan);
        Assert.Equal(@"\\fileserver\share\docs", plan[0].Name, ignoreCase: true);
    }
}
