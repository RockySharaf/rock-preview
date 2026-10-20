using System.Security.AccessControl;
using System.Security.Principal;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// Settings applied to a running index (<see cref="IndexManager.Apply"/>) and what scanned roots keep across restarts,
/// with drives that are really temp folders.
/// </summary>
public sealed class IndexManagerApplyTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anything-apply-").FullName;
    readonly string _dbDir = Directory.CreateTempSubdirectory("anything-apply-db-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    string Db => Path.Combine(_dbDir, "Anything.db");

    string Dir(string relative) => Path.Combine(_root, relative);

    static int IdOf(FileIndex index, string path)
    {
        index.Lock.EnterReadLock();
        try
        {
            for (int i = 0; i < index.Count; i++)
                if (index.IsAlive(i) && string.Equals(index.GetFullPath(i), path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        finally { index.Lock.ExitReadLock(); }
    }

    static IndexedRootInfo RootOf(IndexManager mgr, string path) =>
        Assert.Single(mgr.GetRoots(), r => string.Equals(r.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Denies listing <paramref name="folder"/> to the current user until disposed (an "access denied" share).</summary>
    static IDisposable DenyListing(string folder)
    {
        var dir = new DirectoryInfo(folder);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(rule);
        dir.SetAccessControl(acl);
        return new Undo(() =>
        {
            var a = dir.GetAccessControl();
            a.RemoveAccessRule(rule);
            dir.SetAccessControl(a);
        });
    }

    sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void Turning_a_drive_off_drops_only_that_drive_without_a_rebuild()
    {
        string a = Dir("a"), b = Dir("b");
        Write(Path.Combine(a, "one.txt"));
        Write(Path.Combine(b, "two.txt"));
        var settings = new IndexSettings { DatabasePath = Db };
        using var mgr = Manager(() => [FakeDrive(a), FakeDrive(b)]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);
        int kept = IdOf(mgr.Index, Path.Combine(a, "one.txt"));
        Assert.True(kept >= 0);
        long generation = mgr.Index.Generation;

        settings.SkipDrives = [b + "\\"];
        mgr.Apply(settings);
        Assert.True(WaitFor(() => PathsUnder(mgr.Index, b).Length == 0), "the drive turned off is still indexed");
        WaitUntilSettled(mgr);

        // The other drive was neither cleared nor rescanned: same entry, same index generation.
        Assert.Equal(Expected(a, "one.txt"), Paths(mgr.Index));
        Assert.Equal(kept, IdOf(mgr.Index, Path.Combine(a, "one.txt")));
        Assert.Equal(generation, mgr.Index.Generation);

        settings.SkipDrives = [];
        mgr.Apply(settings);
        Assert.True(WaitFor(() => PathsUnder(mgr.Index, b).Length == 2), "the drive turned back on is not indexed");
    }

    [Fact]
    public void Settings_edited_in_place_reach_the_index_only_through_Apply()
    {
        string a = Dir("a"), b = Dir("b");
        Write(Path.Combine(a, "one.txt"));
        Write(Path.Combine(b, "two.txt"));
        var settings = new IndexSettings { DatabasePath = Db };
        using var mgr = Manager(() => [FakeDrive(a), FakeDrive(b)]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);

        // Settings edits the object the app passed in; a drive check meanwhile must not act on a half-made change.
        settings.SkipDrives = [b + "\\"];
        mgr.CheckDrivesNow();
        mgr.WaitForBackgroundWork(5000);
        Assert.Equal(2, PathsUnder(mgr.Index, b).Length);
    }

    [Fact]
    public void Changed_exclusions_rescan_in_place()
    {
        string a = Dir("a");
        Write(Path.Combine(a, "keep.txt"));
        Write(Path.Combine(a, "skip", "gone.txt"));
        var settings = new IndexSettings { DatabasePath = Db };
        using var mgr = Manager(() => [FakeDrive(a)]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);
        Assert.Equal(Expected(a, "keep.txt", "skip", @"skip\gone.txt"), Paths(mgr.Index));

        settings.ExcludeFolders = [Path.Combine(a, "skip")];
        mgr.Apply(settings);
        Assert.True(WaitFor(() => Paths(mgr.Index).SequenceEqual(Expected(a, "keep.txt"), StringComparer.OrdinalIgnoreCase)),
            string.Join(" | ", Paths(mgr.Index)));
        WaitUntilSettled(mgr);
    }

    [Fact]
    public void Network_share_that_refuses_access_keeps_its_entries()
    {
        string share = Dir("share");
        Write(Path.Combine(share, "a.txt"));
        Write(Path.Combine(share, "sub", "b.txt"));
        var settings = new IndexSettings { DatabasePath = Db, NetworkRescanMinutes = 60 };
        Func<IReadOnlyList<DriveEntry>> drives = () => [FakeDrive(share, DriveKind.Network)];
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
        }

        // Like a NAS whose password changed: the share answers "access denied", which says nothing about its files.
        using (DenyListing(share))
        using (var mgr = Manager(drives))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(share, "a.txt", "sub", @"sub\b.txt"), Paths(mgr.Index));
            Assert.Equal(RootStatus.Offline, RootOf(mgr, share).Status);
        }
    }

    [Fact]
    public void Changed_exclusions_keep_what_an_offline_network_drive_had()
    {
        string share = Dir("share"), local = Dir("local");
        Write(Path.Combine(share, "a.txt"));
        Write(Path.Combine(local, "keep.txt"));
        Write(Path.Combine(local, "skip", "gone.txt"));
        var settings = new IndexSettings { DatabasePath = Db };
        using var mgr = Manager(() => [FakeDrive(share, DriveKind.Network), FakeDrive(local)]);
        mgr.Start(settings);
        WaitUntilSettled(mgr);

        using (DenyListing(share))
        {
            settings.ExcludeFolders = [Path.Combine(local, "skip")];
            mgr.Apply(settings);
            Assert.True(WaitFor(() => !PathsUnder(mgr.Index, local).Contains(Path.Combine(local, "skip"), StringComparer.OrdinalIgnoreCase)));
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(share, "a.txt"), PathsUnder(mgr.Index, share));
            Assert.Equal(RootStatus.Offline, RootOf(mgr, share).Status);
        }
    }

    [Fact]
    public void Drive_letter_mapped_to_another_share_is_scanned_not_resumed()
    {
        string folder = Dir("z");
        Write(Path.Combine(folder, "photo.jpg"));
        var settings = new IndexSettings { DatabasePath = Db, NetworkRescanMinutes = 60 };
        DriveEntry Mapped(string share) => FakeDrive(folder, DriveKind.Network) with { NetworkPath = share };
        using (var mgr = Manager(() => [Mapped(@"\\nas\photos")]))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
        }
        Write(Path.Combine(folder, "song.mp3"));

        // Same share, scanned minutes ago: resumed from the database (the scheduled rescan picks up changes later).
        using (var mgr = Manager(() => [Mapped(@"\\nas\photos")]))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(folder, "photo.jpg"), Paths(mgr.Index));
        }

        // Another share behind the same name: what the database has belongs to the old one, so it is scanned.
        using (var mgr = Manager(() => [Mapped(@"\\nas\music")]))
        {
            mgr.Start(settings);
            WaitUntilSettled(mgr);
            Assert.Equal(Expected(folder, "photo.jpg", "song.mp3"), Paths(mgr.Index));
        }
    }
}
