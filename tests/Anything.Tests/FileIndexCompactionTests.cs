using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests;

public class FileIndexCompactionTests
{
    static (FileIndex Index, int Root, int Dir, int File1, int File2) Build()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int dir = index.Add("dir", root, FileAttributes.Directory, -1, 1, 1);
        int f1 = index.Add("a.txt", dir, FileAttributes.Archive, 10, 2, 2);
        int f2 = index.Add("b.txt", root, FileAttributes.Archive, 20, 3, 3);
        return (index, root, dir, f1, f2);
    }

    [Fact]
    public void Duplicate_names_share_one_string_instance()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int a = index.Add(new string("index.js".AsSpan()), root, FileAttributes.Archive, 1, 0, 0);
        int b = index.Add(new string("index.js".AsSpan()), root, FileAttributes.Archive, 1, 0, 0);
        Assert.Same(index.Name(a), index.Name(b));
    }

    [Fact]
    public void Removed_entries_report_empty_names_and_compact_away()
    {
        var (index, root, dir, f1, f2) = Build();
        index.Remove(dir); // removes dir and a.txt
        Assert.Equal("", index.Name(f1));
        Assert.Equal(2, index.DeadCount);
        long generation = index.Generation;

        var remap = index.Compact();

        Assert.True(index.Generation > generation);
        Assert.Equal(0, index.DeadCount);
        Assert.Equal(2, index.Count);
        Assert.Equal(-1, remap[dir]);
        Assert.Equal(-1, remap[f1]);
        int newRoot = remap[root], newF2 = remap[f2];
        Assert.Equal("C:", index.Name(newRoot));
        Assert.Equal("b.txt", index.Name(newF2));
        Assert.Equal(newRoot, index.Parent(newF2));
        Assert.Equal(@"C:\b.txt", index.GetFullPath(newF2));
        Assert.Equal(20, index.Size(newF2));
        Assert.Equal(3, index.Modified(newF2));
    }

    [Fact]
    public void Compaction_keeps_deep_paths_and_allows_further_adds()
    {
        var (index, root, dir, f1, f2) = Build();
        index.Remove(f2);
        var remap = index.Compact();
        int newFile = remap[f1];
        Assert.Equal(@"C:\dir\a.txt", index.GetFullPath(newFile));
        int added = index.Add("c.txt", remap[dir], FileAttributes.Archive, 5, 0, 0);
        Assert.Equal(@"C:\dir\c.txt", index.GetFullPath(added));
    }

    [Fact]
    public void Record_map_follows_compaction_and_drops_removed_ids()
    {
        var map = new RecordMap();
        map.Add(5, 0);   // root
        map.Add(40, 2);  // file id 2
        map.Add(40, 3);  // hard link: id 3
        map.Add(41, 4);
        var remap = new[] { 0, -1, 1, -1, 2 }; // ids 1 and 3 removed
        map.Remap(remap);
        Assert.Equal(0, map.Get(5));
        Assert.Equal(1, map.Get(40));
        Assert.False(map.HasExtra(40));
        Assert.Equal(2, map.Get(41));

        var all = new List<int>();
        map.Add(42, 9);
        map.Add(42, 7);
        map.Remap(Enumerable.Range(0, 10).Select(i => i == 9 ? -1 : i).ToArray()); // primary removed, extra promoted
        map.GetAll(42, all);
        Assert.Equal([7], all);
    }
}
