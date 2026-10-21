using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>The presorted order used for large indexes must produce exactly the classic sorter's results.</summary>
public class SortOrderCacheTests
{
    static readonly SortKey[] Keys = Enum.GetValues<SortKey>();
    static readonly string[] Queries = ["", "a", "*.txt", "file:", "folder:", "ext:jpg;TXT", "size:>500", "Dir"];

    static FileIndex BuildIndex(int seed)
    {
        var rnd = new Random(seed);
        var index = new FileIndex();
        // Dates accessed indexed (else they are read from the disk and never presorted) and recent changes recorded.
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true);
        var folders = new List<int> { index.AddRoot("C:"), index.AddRoot("D:") };
        foreach (int root in folders) index.Update(root, accessedFileTimeUtc: 1); // known: never read from this PC's disk
        string[] exts = ["txt", "TXT", "jpg", "Jpg", "", "md", "tar.gz", "cs"];
        string[] stems = ["a", "A", "alpha", "Beta", "beta", "index", "Index", "readme", "Ärger", "zeta", "dir", "x10", "x9"];
        for (int i = 0; i < 2500; i++)
        {
            int parent = folders[rnd.Next(folders.Count)];
            bool folder = rnd.Next(5) == 0;
            string stem = stems[rnd.Next(stems.Length)] + (rnd.Next(3) == 0 ? rnd.Next(50).ToString() : "");
            if (folder)
                folders.Add(index.Add(stem + "Dir", parent, FileAttributes.Directory, -1, rnd.Next(1, 1000), rnd.Next(1, 1000), rnd.Next(1, 1000)));
            else
            {
                string ext = exts[rnd.Next(exts.Length)];
                var attrs = rnd.Next(4) == 0 ? FileAttributes.Hidden | FileAttributes.Archive : FileAttributes.Archive;
                int id = index.Add(ext.Length == 0 ? stem : stem + "." + ext, parent, attrs, rnd.Next(0, 1000), rnd.Next(1, 1000), rnd.Next(1, 1000), rnd.Next(1, 1000));
                if (rnd.Next(6) == 0) index.SetRecentChange(id, rnd.Next(1, 1000));
                if (rnd.Next(8) == 0) index.RunHistory.Set(index.GetFullPath(id), rnd.Next(1, 5), rnd.Next(0, 1000));
            }
        }
        return index;
    }

    static int[] Classic(FileIndex index, string q, SortKey key, bool desc)
        => new SearchEngine { SortCacheThreshold = int.MaxValue }.Search(index, q, new SearchOptions(), key, desc, CancellationToken.None).Ids;

    static void AssertSameAsClassic(FileIndex index, SearchEngine cached)
    {
        foreach (var key in Keys)
            foreach (var q in Queries)
            {
                var expected = Classic(index, q, key, false);
                var actual = cached.Search(index, q, new SearchOptions(), key, false, CancellationToken.None).Ids;
                Assert.True(expected.SequenceEqual(actual), $"ascending {key} '{q}' differs");

                // Descending walks the order backwards: same members, and the exact reverse of ascending (date accessed
                // is never presorted: sorted each time, ties keep their id order both ways).
                var desc = cached.Search(index, q, new SearchOptions(), key, true, CancellationToken.None).Ids;
                if (key != SortKey.DateAccessed) Assert.True(actual.Reverse().SequenceEqual(desc), $"descending {key} '{q}' is not the reverse");
                Assert.True(Classic(index, q, key, true).Order().SequenceEqual(desc.Order()), $"descending {key} '{q}' members differ");
            }
    }

    [Fact]
    public void Rebuilt_order_matches_classic_sort_for_every_key()
    {
        var index = BuildIndex(1);
        AssertSameAsClassic(index, new SearchEngine { SortCacheThreshold = 0 });
    }

    [Theory]
    [InlineData(SortKey.Name)]
    [InlineData(SortKey.Path)]
    [InlineData(SortKey.Size)]
    [InlineData(SortKey.Extension)]
    [InlineData(SortKey.DateModified)]
    [InlineData(SortKey.DateAccessed)]
    [InlineData(SortKey.DateRecentlyChanged)]
    [InlineData(SortKey.RunCount)]
    [InlineData(SortKey.DateRun)]
    public void Incremental_updates_keep_the_order_exact(SortKey key)
    {
        var index = BuildIndex(2);
        var engine = new SearchEngine { SortCacheThreshold = 0 };
        engine.Search(index, "", new SearchOptions(), key, false, CancellationToken.None); // build the cache
        var rnd = new Random(3);

        for (int round = 0; round < 35; round++)
        {
            int victim;
            do victim = rnd.Next(index.Count); while (!index.IsAlive(victim) || index.Parent(victim) == FileIndex.NoParent);
            switch (round % 7)
            {
                case 0: index.Add($"new{round}.TXT", index.Parent(victim), FileAttributes.Archive, rnd.Next(1000), rnd.Next(1000), 1, rnd.Next(1000)); break;
                case 1: index.Rename(victim, $"renamed{round}.jpg", index.Parent(victim)); break;
                case 2: index.Update(victim, size: rnd.Next(1000), modifiedFileTimeUtc: rnd.Next(1000), accessedFileTimeUtc: rnd.Next(1000)); break;
                case 3: index.Remove(victim); break; // may remove a whole folder
                case 4: index.Add($"Folder{round}Dir", index.Parent(victim), FileAttributes.Directory, -1, rnd.Next(1000), 1); break;
                case 5: index.SetRecentChange(victim, rnd.Next(1000, 2000)); break;
                case 6: index.RunHistory.Increment(index.GetFullPath(victim), rnd.Next(1000, 2000)); break;
            }
            foreach (var q in Queries)
            {
                var expected = Classic(index, q, key, false);
                var actual = engine.Search(index, q, new SearchOptions(), key, false, CancellationToken.None).Ids;
                Assert.True(expected.SequenceEqual(actual), $"round {round}: {key} '{q}' differs after an update");
            }
        }
    }

    [Theory]
    [InlineData(SortKey.Path)]
    [InlineData(SortKey.Size)]
    [InlineData(SortKey.Name)]
    public void Folder_sizes_keep_orders_exact_while_every_change_touches_the_folders_above(SortKey key)
    {
        var index = BuildIndex(5);
        index.Configure(dateAccessed: true, folderSizes: true, recentChanges: true);
        var engine = new SearchEngine { SortCacheThreshold = 0 };
        engine.Search(index, "", new SearchOptions(), key, false, CancellationToken.None);
        var rnd = new Random(6);
        for (int round = 0; round < 30; round++)
        {
            int victim;
            do victim = rnd.Next(index.Count); while (!index.IsAlive(victim) || index.Parent(victim) == FileIndex.NoParent);
            switch (round % 5)
            {
                case 0: index.Add($"grow{round}.bin", index.Parent(victim), FileAttributes.Archive, rnd.Next(1, 5000), 1, 1); break;
                case 1: if (!index.IsDirectory(victim)) index.Update(victim, size: rnd.Next(5000)); break;
                case 2: index.Remove(victim); break;
                case 3: index.Rename(victim, $"moved{round}", index.Parent(index.Parent(victim)) is var p && p != FileIndex.NoParent ? p : index.Parent(victim)); break;
                case 4: index.Rename(victim, $"renamed{round}.TXT", index.Parent(victim)); break;
            }
            foreach (var q in Queries)
            {
                var expected = Classic(index, q, key, false);
                var actual = engine.Search(index, q, new SearchOptions(), key, false, CancellationToken.None).Ids;
                Assert.True(expected.SequenceEqual(actual), $"round {round}: {key} '{q}' differs");
            }
        }
    }

    [Fact]
    public void Compaction_invalidates_and_rebuilds_the_order()
    {
        var index = BuildIndex(4);
        var engine = new SearchEngine { SortCacheThreshold = 0 };
        engine.Search(index, "", new SearchOptions(), SortKey.Name, false, CancellationToken.None);
        for (int i = 0; i < index.Count; i += 7) if (index.IsAlive(i) && index.Parent(i) != FileIndex.NoParent) index.Remove(i);
        index.Compact();
        AssertSameAsClassic(index, engine);
    }
}
