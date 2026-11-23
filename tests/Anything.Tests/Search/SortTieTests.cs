using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>Files with the same date, size or type come by name, A to Z, whichever way the column is sorted (as in File Explorer).</summary>
public class SortTieTests
{
    // Three files with every value the same (size, dates, extension) and one that differs in all of them.
    static (FileIndex Index, int[] Tied, int Other) Build()
    {
        var index = new FileIndex();
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true);
        int root = index.AddRoot("C:");
        index.Update(root, accessedFileTimeUtc: 1);
        // Added out of name order, so neither the id order nor its reverse is the name order.
        int b = index.Add("b.txt", root, FileAttributes.Archive, 10, 500, 400, 300);
        int c = index.Add("c.txt", root, FileAttributes.Archive, 10, 500, 400, 300);
        int a = index.Add("a.txt", root, FileAttributes.Archive, 10, 500, 400, 300);
        int other = index.Add("m.zip", root, FileAttributes.Archive | FileAttributes.Hidden, 99, 900, 800, 700);
        foreach (int id in new[] { a, b, c }) index.SetRecentChange(id, 600);
        index.SetRecentChange(other, 950);
        return (index, [a, b, c], other);
    }

    public static TheoryData<SortKey, bool> TiedKeys()
    {
        var data = new TheoryData<SortKey, bool>();
        foreach (var key in new[] { SortKey.Size, SortKey.Extension, SortKey.Type, SortKey.DateModified, SortKey.DateCreated,
                     SortKey.DateAccessed, SortKey.Attributes, SortKey.DateRecentlyChanged })
            foreach (int threshold in new[] { int.MaxValue, 0 }) // sorted directly, and from the presorted order
                data.Add(key, threshold == 0);
        return data;
    }

    [Theory]
    [MemberData(nameof(TiedKeys))]
    public void Ties_come_by_name_both_ways(SortKey key, bool presorted)
    {
        var (index, tied, other) = Build();
        var engine = new SearchEngine { SortCacheThreshold = presorted ? 0 : int.MaxValue };
        foreach (bool descending in new[] { false, true })
        {
            var ids = engine.Search(index, "file:", new SearchOptions(), key, descending, CancellationToken.None).Ids;
            Assert.Equal(4, ids.Length);
            int[] expected = descending ? [other, .. tied] : [.. tied, other];
            Assert.True(expected.SequenceEqual(ids), $"{key} {(descending ? "descending" : "ascending")}: " +
                string.Join(", ", ids.Select(index.Name)));
        }
    }

    [Fact]
    public void Name_descending_is_still_z_to_a()
    {
        var (index, tied, other) = Build();
        var ids = new SearchEngine().Search(index, "file:", new SearchOptions(), SortKey.Name, true, CancellationToken.None).Ids;
        Assert.Equal([other, tied[2], tied[1], tied[0]], ids);
    }

    [Fact]
    public void Descend_turns_runs_of_equal_keys_round_as_wholes()
    {
        int[] keys = [1, 1, 2, 3, 3, 3, 4];
        int[] order = [0, 1, 2, 3, 4, 5, 6]; // positions in ascending order
        ResultSorter.Descend<int>(order, (x, y) => keys[x] == keys[y]);
        Assert.Equal([6, 3, 4, 5, 2, 0, 1], order);

        int[] whole = [0, 1, 2];
        ResultSorter.Descend<int>(whole, null);
        Assert.Equal([2, 1, 0], whole);

        int[] empty = [];
        ResultSorter.Descend<int>(empty, (_, _) => true);
        Assert.Empty(empty);
    }
}
