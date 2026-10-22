using Anything.Core;

namespace Anything.Tests.Content;

public class FileIndexCompactedTests
{
    static (FileIndex Index, int Root, int Dead, int Live) Build()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int dead = index.Add("dead.txt", root, FileAttributes.Archive, 1, 0, 0);
        int live = index.Add("live.txt", root, FileAttributes.Archive, 2, 0, 0);
        index.Remove(dead);
        return (index, root, dead, live);
    }

    [Fact]
    public void Raised_inside_the_write_lock_with_the_remap_and_the_new_generation()
    {
        var (index, root, dead, live) = Build();
        int[]? seen = null;
        long seenGeneration = -1;
        bool locked = false;
        index.Compacted += (remap, generation) =>
        {
            seen = remap;
            seenGeneration = generation;
            locked = index.Lock.IsWriteLockHeld;
        };
        long before = index.Generation;

        var result = index.Compact();

        Assert.Same(result, seen);
        Assert.True(locked);
        Assert.Equal(before + 1, seenGeneration);
        Assert.Equal(index.Generation, seenGeneration);
        Assert.Equal(-1, result[dead]);
        Assert.Equal("live.txt", index.Name(result[live]));
        Assert.Equal(0, result[root]);
    }

    [Fact]
    public void Not_raised_by_Clear()
    {
        var (index, _, _, _) = Build();
        int raised = 0;
        index.Compacted += (_, _) => raised++;
        index.Clear();
        Assert.Equal(0, raised);
        index.Compact();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_throwing_subscriber_leaves_Compact_returning_the_remap()
    {
        var (index, _, dead, live) = Build();
        int after = 0, changed = 0;
        index.Compacted += (_, _) => throw new InvalidOperationException("content bug");
        index.Compacted += (_, _) => after++;
        index.Changed += () => changed++;

        var remap = index.Compact();

        Assert.Equal(1, after);
        Assert.Equal(1, changed);
        Assert.Equal(-1, remap[dead]);
        Assert.Equal(1, remap[live]);
        Assert.False(index.Lock.IsWriteLockHeld);
        // The index works on: later mutations and compactions are fine.
        index.Add("new.txt", 0, FileAttributes.Archive, 3, 0, 0);
        Assert.Equal(3, index.LiveCount);
    }

    [Fact]
    public void The_content_source_starts_empty()
    {
        var index = new FileIndex();
        Assert.Null(index.Content);
    }
}
