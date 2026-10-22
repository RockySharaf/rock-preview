using Anything.Core.Content.Store;

namespace Anything.Tests.Content;

public class DocSetTests
{
    static readonly int[] Counts = [100, 10_000, 64, 5_000, 0];

    static (DocSet Set, HashSet<(int, int)> Truth) RandomSet(Random rnd, double[] densities)
    {
        var builder = new DocSetBuilder(Counts);
        var truth = new HashSet<(int, int)>();
        for (int s = 0; s < Counts.Length; s++)
        {
            for (int d = 0; d < Counts[s]; d++)
            {
                if (rnd.NextDouble() >= densities[s]) continue;
                builder.Add(s, d);
                truth.Add((s, d));
                if (rnd.Next(10) == 0) builder.Add(s, d); // repeats are fine
            }
        }
        return (builder.Build(), truth);
    }

    static HashSet<(int, int)> Items(DocSet set)
    {
        var list = new List<(int, int)>();
        set.ForEach((s, d) => list.Add((s, d)), CancellationToken.None);
        Assert.Equal(list.Order().ToList(), list); // in order
        Assert.Equal(set.Count, list.Count);
        return [.. list];
    }

    static void Same(IEnumerable<(int, int)> expected, DocSet actual)
    {
        var e = expected.ToHashSet();
        var a = Items(actual);
        Assert.True(e.SetEquals(a), $"expected {e.Count} documents, got {a.Count}; missing {e.Except(a).FirstOrDefault()}, extra {a.Except(e).FirstOrDefault()}");
    }

    public static TheoryData<double[], double[]> Densities => new()
    {
        { [0.01, 0.001, 0.02, 0.002, 0], [0.02, 0.002, 0.01, 0.001, 0] },   // sparse with sparse
        { [0.5, 0.6, 0.9, 0.4, 0], [0.01, 0.001, 0.02, 0.002, 0] },         // dense with sparse
        { [0.5, 0.6, 0.9, 0.4, 0], [0.4, 0.7, 0.5, 0.3, 0] },               // dense with dense
        { [0, 0.3, 0, 1, 0], [1, 0, 0.5, 0, 0] },                           // empty segments, full segments
    };

    [Theory]
    [MemberData(nameof(Densities))]
    public void And_Or_Except_match_set_arithmetic(double[] a, double[] b)
    {
        var rnd = new Random(a.Sum().GetHashCode() ^ b.Length);
        var (x, tx) = RandomSet(rnd, a);
        var (y, ty) = RandomSet(rnd, b);
        Same(tx, x);
        Same(ty, y);
        Same(tx.Intersect(ty), x.And(y));
        Same(tx.Union(ty), x.Or(y));
        Same(tx.Except(ty), x.Except(y));
        Same(ty.Except(tx), y.Except(x));
        foreach (var (s, d) in tx) Assert.True(x.Contains(new DocRef(s, d)));
        Assert.False(x.Contains(new DocRef(1, 10_000)));
        Assert.False(x.Contains(new DocRef(9, 0)));
    }

    [Fact]
    public void Sets_of_different_widths_combine()
    {
        var b = new DocSetBuilder([10, 10]);
        b.Add(0, 1);
        b.Add(1, 2);
        var set = b.Build();
        Assert.Equal(0, set.And(DocSet.Empty).Count);
        Assert.Equal(2, set.Or(DocSet.Empty).Count);
        Assert.Equal(2, DocSet.Empty.Or(set).Count);
        Assert.Equal(2, set.Except(DocSet.Empty).Count);
        Assert.Equal(0, DocSet.Empty.Except(set).Count);
        Assert.Equal(2, set.SegmentCount);
    }

    [Fact]
    public void The_builder_sorts_dedupes_and_drops_out_of_range_documents()
    {
        var b = new DocSetBuilder([1000, 50]);
        b.Add(0, 9);
        b.Add(0, 3);
        b.Add(0, 9);
        b.AddSorted(0, [1, 3, 5]);
        b.Add(0, 1000);
        b.Add(0, -1);
        b.Add(2, 0);
        b.Add(-1, 0);
        b.AddSorted(1, [0, 49, 50]);
        var set = b.Build();
        Assert.Equal([(0, 1), (0, 3), (0, 5), (0, 9), (1, 0), (1, 49)], Items(set).Order());
        Assert.Equal(4, set.CountIn(0));
        Assert.Equal(2, set.CountIn(1));
        Assert.Equal(0, set.CountIn(7));
    }

    [Fact]
    public void Slices_join_back_into_the_same_set()
    {
        var rnd = new Random(3);
        var (set, truth) = RandomSet(rnd, [0.3, 0.01, 0.5, 0.2, 0]);
        var parts = Enumerable.Range(0, set.SegmentCount).Select(set.Slice).ToArray();
        Assert.All(parts, p => Assert.True(p.SegmentCount <= 1));
        var joined = DocSet.Join(parts);
        Same(truth, joined);
        Assert.Equal(set.CountIn(3), parts[3].Count);
        Assert.Equal(0, DocSet.Join([null, null]).Count);
    }

    [Fact]
    public void ForEach_polls_cancellation()
    {
        var b = new DocSetBuilder([200_000]);
        for (int d = 0; d < 200_000; d++) b.Add(0, d);
        var set = b.Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int seen = 0;
        Assert.Throws<OperationCanceledException>(() => set.ForEach((_, _) => seen++, cts.Token));
        Assert.True(seen < 200_000);
    }
}
