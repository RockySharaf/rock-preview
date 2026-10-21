using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// The optional information Everything can keep per entry: folder sizes (kept current through every kind of change),
/// date accessed, and recent changes.
/// </summary>
public sealed class FileIndexInformationTests
{
    static FileIndex WithFolderSizes()
    {
        var index = new FileIndex();
        index.Configure(dateAccessed: false, folderSizes: true, recentChanges: false);
        return index;
    }

    [Fact]
    public void Folder_sizes_add_up_the_files_below_and_follow_adds_updates_and_removals()
    {
        var index = WithFolderSizes();
        int c = index.AddRoot("C:");
        int docs = index.Add("Docs", c, FileAttributes.Directory, -1, 0, 0);
        int old = index.Add("Old", docs, FileAttributes.Directory, -1, 0, 0);
        int a = index.Add("a.txt", docs, FileAttributes.Archive, 100, 0, 0);
        index.Add("b.zip", old, FileAttributes.Archive, 1000, 0, 0);
        index.Add("unknown.bin", old, FileAttributes.Archive, -1, 0, 0); // a size not known counts as 0
        int empty = index.Add("Empty", c, FileAttributes.Directory, -1, 0, 0);

        Assert.Equal(1000, index.Size(old));
        Assert.Equal(1100, index.Size(docs));
        Assert.Equal(1100, index.Size(c));
        Assert.Equal(0, index.Size(empty));

        index.Update(a, size: 150);
        Assert.Equal(1150, index.Size(docs));
        Assert.Equal(1150, index.Size(c));

        index.Remove(old); // the whole subtree: its 1000 bytes leave every folder above
        Assert.Equal(150, index.Size(docs));
        Assert.Equal(150, index.Size(c));

        index.Remove(a);
        Assert.Equal(0, index.Size(docs));
        Assert.Equal(0, index.Size(c));
    }

    [Fact]
    public void Folder_sizes_follow_moves_and_ignore_sizes_given_to_folders()
    {
        var index = WithFolderSizes();
        int c = index.AddRoot("C:");
        int from = index.Add("From", c, FileAttributes.Directory, 999, 0, 0); // a folder's size is worked out, not taken
        int to = index.Add("To", c, FileAttributes.Directory, -1, 0, 0);
        int sub = index.Add("Sub", from, FileAttributes.Directory, -1, 0, 0);
        index.Add("x.bin", sub, FileAttributes.Archive, 40, 0, 0);
        int f = index.Add("f.bin", from, FileAttributes.Archive, 2, 0, 0);
        Assert.Equal(42, index.Size(from));

        index.Rename(sub, "Sub", to); // a folder moves with its whole total
        Assert.Equal(2, index.Size(from));
        Assert.Equal(40, index.Size(to));
        Assert.Equal(42, index.Size(c));

        index.Rename(f, "renamed.bin", from); // renamed in place: nothing moves
        Assert.Equal(2, index.Size(from));

        index.Update(to, size: 5); // folders take no size from outside
        Assert.Equal(40, index.Size(to));
    }

    [Fact]
    public void Folder_sizes_survive_forward_parents_and_compaction_and_turn_off_and_on()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int f1 = 0, folder = 0;
        index.BulkAdd(b =>
        {
            // A file read before its folder (as a database with a moved folder loads): parented afterwards.
            f1 = b.Add("early.txt", FileIndex.NoParent, FileAttributes.Archive, 7, 0, 0);
            folder = b.Add("Late", root, FileAttributes.Directory, -1, 0, 0);
            b.SetParent(f1, folder);
            b.Add("more.txt", folder, FileAttributes.Archive, 3, 0, 0);
        });
        Assert.Equal(-1, index.Size(folder)); // off: folders have no size

        index.Configure(dateAccessed: false, folderSizes: true, recentChanges: false);
        Assert.Equal(10, index.Size(folder));
        Assert.Equal(10, index.Size(root));

        int gone = index.Add("gone.txt", folder, FileAttributes.Archive, 100, 0, 0);
        index.Remove(gone);
        var remap = index.Compact();
        Assert.Equal(10, index.Size(remap[folder]));

        // Same after the folder that was read late moves under a new one.
        index.BulkAdd(b =>
        {
            int fresh = b.Add("Fresh", remap[root], FileAttributes.Directory, -1, 0, 0);
            b.SetParent(remap[folder], fresh);
        });
        Assert.Equal(10, index.Size(remap[root]));

        index.Configure(dateAccessed: false, folderSizes: false, recentChanges: false);
        Assert.Equal(-1, index.Size(remap[folder]));
        Assert.Equal(-1, index.Size(remap[root]));
        Assert.Equal(7, index.Size(remap[f1]));
    }

    [Fact]
    public void Date_accessed_is_kept_only_while_indexed()
    {
        var index = new FileIndex();
        int c = index.AddRoot("C:");
        int a = index.Add("a.txt", c, FileAttributes.Archive, 1, 10, 20, accessedFileTimeUtc: 30);
        Assert.False(index.HasDateAccessed);
        Assert.Equal(0, index.Accessed(a)); // not indexed: nothing kept

        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: false);
        Assert.True(index.HasDateAccessed);
        Assert.Equal(0, index.Accessed(a)); // unknown until read again
        index.Update(a, accessedFileTimeUtc: 33);
        int b = index.Add("b.txt", c, FileAttributes.Archive, 1, 10, 20, accessedFileTimeUtc: 44);
        Assert.Equal(33, index.Accessed(a));
        Assert.Equal(44, index.Accessed(b));

        // Grows with the index, and keeps up with compaction.
        int last = -1;
        for (int i = 0; i < 3000; i++) last = index.Add($"f{i}", c, FileAttributes.Archive, 1, 1, 1, accessedFileTimeUtc: 1000 + i);
        index.Remove(a);
        var remap = index.Compact();
        Assert.Equal(44, index.Accessed(remap[b]));
        Assert.Equal(3999, index.Accessed(remap[last]));

        index.Configure(dateAccessed: false, folderSizes: false, recentChanges: false);
        Assert.Equal(0, index.Accessed(remap[b]));
    }

    [Fact]
    public void Recent_changes_keep_the_latest_time_and_follow_removal_and_compaction()
    {
        var index = new FileIndex();
        int c = index.AddRoot("C:");
        int dir = index.Add("Dir", c, FileAttributes.Directory, -1, 0, 0);
        int a = index.Add("a.txt", dir, FileAttributes.Archive, 1, 0, 0);
        int b = index.Add("b.txt", c, FileAttributes.Archive, 1, 0, 0);

        index.SetRecentChange(a, 500); // not recorded: off
        Assert.Equal(0, index.RecentChange(a));

        index.Configure(dateAccessed: false, folderSizes: false, recentChanges: true);
        index.SetRecentChange(a, 500);
        index.SetRecentChange(a, 400); // older: the latest stays
        index.SetRecentChanges([(b, 700), (dir, 600)]);
        Assert.Equal(500, index.RecentChange(a));
        Assert.Equal(700, index.RecentChange(b));

        index.Remove(dir); // its subtree with it
        Assert.Equal(0, index.RecentChange(a));
        Assert.Equal(0, index.RecentChange(dir));
        var remap = index.Compact();
        Assert.Equal(700, index.RecentChange(remap[b]));

        index.Clear();
        Assert.Equal(0, index.RecentChange(0));
    }

    [Fact]
    public void Path_version_moves_only_when_a_folder_is_renamed_or_moved()
    {
        var index = WithFolderSizes();
        int c = index.AddRoot("C:");
        int dir = index.Add("Dir", c, FileAttributes.Directory, -1, 0, 0);
        int other = index.Add("Other", c, FileAttributes.Directory, -1, 0, 0);
        int file = index.Add("f.txt", dir, FileAttributes.Archive, 5, 0, 0);
        long v = index.PathVersion;

        index.Update(file, size: 50);            // touches Dir and C: (their sizes): no path changed
        index.Rename(file, "g.txt", other);      // a file moving changes only its own path
        index.Add("new.txt", dir, FileAttributes.Archive, 1, 0, 0);
        Assert.Equal(v, index.PathVersion);

        index.Rename(dir, "Renamed", c);
        Assert.NotEqual(v, index.PathVersion);
        v = index.PathVersion;
        index.BulkAdd(b => b.SetParent(other, dir));
        Assert.NotEqual(v, index.PathVersion);
    }

    [Fact]
    public void Turning_information_on_restarts_presorted_orders()
    {
        var index = new FileIndex();
        int c = index.AddRoot("C:");
        index.Add("a", c, FileAttributes.Archive, 1, 0, 0);
        long seq = index.ChangeSequence;
        index.Configure(dateAccessed: true, folderSizes: true, recentChanges: true);
        Assert.False(index.TryGetChangesSince(seq, [])); // every value may have changed: orders rebuild
    }
}
