using Anything.Core.Content;

namespace Anything.Tests.Content;

public class DocIdMapTests
{
    static DocIdMap Build(long generation, params (uint Segment, int Docs, (int Doc, int Id)[] Ids)[] segments)
    {
        var b = DocIdMap.Empty(0).ToBuilder();
        foreach (var (segment, docs, ids) in segments)
        {
            b.AddSegment(segment, docs);
            foreach (var (doc, id) in ids) b.Set(segment, doc, id);
        }
        return b.Build(generation);
    }

    static List<(uint, int, int)> All(DocIdMap map)
    {
        var list = new List<(uint, int, int)>();
        map.ForEachMapped((s, d, id) => list.Add((s, d, id)));
        return list;
    }

    [Fact]
    public void Empty_maps_nothing()
    {
        var map = DocIdMap.Empty(7);
        Assert.Equal(7, map.Generation);
        Assert.Equal(-1, map.FileId(1, 0));
        Assert.False(map.HasSegment(1));
        Assert.Null(map.Segment(1));
        Assert.Empty(All(map));
    }

    [Fact]
    public void Ids_by_segment_and_document()
    {
        var map = Build(3, (9, 10_000, [(0, 100), (4095, 101), (4096, 102), (9_999, 103)]), (2, 5, [(1, 7)]));
        Assert.Equal(3, map.Generation);
        Assert.Equal(100, map.FileId(9, 0));
        Assert.Equal(103, map.FileId(9, 9_999));
        Assert.Equal(-1, map.FileId(9, 1));
        Assert.Equal(-1, map.FileId(9, 10_000));
        Assert.Equal(-1, map.FileId(9, -1));
        Assert.True(map.HasSegment(2));
        Assert.Equal(10_000, map.Segment(9)!.DocCount);
        Assert.Equal(102, map.Segment(9)!.FileId(4096));
        Assert.Equal([(2u, 1, 7), (9u, 0, 100), (9u, 4095, 101), (9u, 4096, 102), (9u, 9_999, 103)], All(map));
        Assert.Equal([2u, 9u], map.SegmentIdList);
    }

    [Fact]
    public void Builders_copy_on_write_per_chunk()
    {
        var first = Build(1, (1, 3 * DocIdMap.ChunkSize, [(0, 10), (DocIdMap.ChunkSize, 11), (2 * DocIdMap.ChunkSize, 12)]));
        var builder = first.ToBuilder();
        builder.Set(1, 5, 99);
        var second = builder.Build(2);

        Assert.Equal(-1, first.FileId(1, 5));
        Assert.Equal(99, second.FileId(1, 5));
        var a = first.Segment(1)!.Chunks;
        var b = second.Segment(1)!.Chunks;
        Assert.NotSame(a[0], b[0]);
        Assert.Same(a[1], b[1]);
        Assert.Same(a[2], b[2]);

        // The builder can go on: what it changes now never reaches the map it built.
        builder.Set(1, 6, 98);
        builder.Set(1, DocIdMap.ChunkSize + 1, 97);
        Assert.Equal(-1, second.FileId(1, 6));
        Assert.Equal(-1, second.FileId(1, DocIdMap.ChunkSize + 1));
        var third = builder.Build(3);
        Assert.Equal(98, third.FileId(1, 6));
        Assert.Equal(97, third.FileId(1, DocIdMap.ChunkSize + 1));
        Assert.Same(b[2], third.Segment(1)!.Chunks[2]);
    }

    [Fact]
    public void Unmapping_and_bad_arguments()
    {
        var b = DocIdMap.Empty(0).ToBuilder();
        b.AddSegment(1, 10);
        b.Set(1, 3, 30);
        b.Set(1, 3, -1);
        b.Set(1, 4, -5);
        Assert.Equal(-1, b.FileId(1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Set(1, 10, 1));
        Assert.Throws<KeyNotFoundException>(() => b.Set(2, 0, 1));
        b.RemoveSegment(1);
        Assert.False(b.HasSegment(1));
        Assert.False(b.Build(0).HasSegment(1));
    }

    [Fact]
    public void Merge_moves_ids_through_the_remaps()
    {
        var map = Build(5, (1, 4, [(0, 100), (1, 101), (3, 103)]), (2, 3, [(0, 200), (2, 202)]), (3, 2, [(0, 300)]));
        var b = map.ToBuilder();
        // Old docs → new: segment 1's doc 1 was deleted (-1); segment 2's doc 2 lands at 0.
        b.Merge([(1, [1, -1, 2, 3]), (2, [4, 5, 0])], newSegment: 7, newDocCount: 6);
        var merged = b.Build(5);
        Assert.False(merged.HasSegment(1));
        Assert.False(merged.HasSegment(2));
        Assert.True(merged.HasSegment(3));
        Assert.Equal([(3u, 0, 300), (7u, 0, 202), (7u, 1, 100), (7u, 3, 103), (7u, 4, 200)], All(merged));
        Assert.Equal(101, map.FileId(1, 1)); // the published map is untouched
    }

    [Fact]
    public void RemapIds_follows_a_compact()
    {
        var map = Build(1, (1, 5, [(0, 0), (1, 2), (2, 4), (3, 9)]), (2, DocIdMap.ChunkSize + 1, [(DocIdMap.ChunkSize, 1)]));
        var b = map.ToBuilder();
        b.RemapIds([0, -1, 1, -1, 2]); // ids 1 and 3 removed; 9 is past the end
        var after = b.Build(2);
        Assert.Equal([(1u, 0, 0), (1u, 1, 1), (1u, 2, 2)], All(after));
        Assert.Equal(2, after.Generation);
        Assert.Null(after.Segment(2)!.Chunks[1]); // nothing left in that chunk
        Assert.Equal(4, map.FileId(1, 2));
    }
}
