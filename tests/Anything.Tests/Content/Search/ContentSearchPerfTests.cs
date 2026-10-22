using System.Diagnostics;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Search;

/// <summary>A fake store of 300,000 documents: plain-word searches stay well under 2 s and mapping (Prepare) under 30 ms p95.</summary>
[Collection(ContentSearchSerial.Name)]
public class ContentSearchPerfTests(ITestOutputHelper output)
{
    const int Docs = 300_000, PerSegment = 100_000;

    static (FileIndex Index, FakeContentSource Source) Build()
    {
        var index = new FileIndex();
        var ids = new int[Docs];
        index.BulkAdd(add =>
        {
            int root = add.Add("C:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            int folder = -1;
            for (int i = 0; i < Docs; i++)
            {
                if (i % 1000 == 0) folder = add.Add("folder" + i / 1000, root, FileAttributes.Directory, -1, 0, 0);
                // One name in a hundred has the word, so the search has both groups.
                string name = i % 100 == 0 ? $"budget{i}.txt" : $"file{i}.txt";
                ids[i] = add.Add(name, folder, FileAttributes.Archive, 100 + i % 977, 133_500_000_000_000_000L + i, 0);
            }
        });
        var segments = new List<FakeSegment>();
        var map = DocIdMap.Empty(index.Generation).ToBuilder();
        for (int s = 0; s * PerSegment < Docs; s++)
        {
            var docs = new List<FakeDoc>(PerSegment);
            for (int d = 0; d < PerSegment; d++)
            {
                int i = s * PerSegment + d;
                string body = (i % 2 == 0 ? "the yearly budget report " : "the yearly summary ") + "word" + i % 5000;
                docs.Add(new FakeDoc($@"folder{i / 1000}\file{i}.txt",
                    new DocRow((ulong)i, 0, 100, 0, 0, ContentKind.Text, DocStatus.Ok, 1, DocFlags.HasText), new StoredDoc(body, [], null)));
            }
            uint id = (uint)(s + 1);
            segments.Add(new FakeSegment(id, docs));
            map.AddSegment(id, docs.Count);
            for (int d = 0; d < docs.Count; d++) map.Set(id, d, ids[s * PerSegment + d]);
        }
        var source = new FakeContentSource();
        source.Publish(new ContentView(new FakeStoreView(segments, 1), map.Build(index.Generation), ContentMode.Documents));
        index.Content = source;
        return (index, source);
    }

    [Fact]
    public void Plain_words_over_300K_documents_stay_fast()
    {
        var (index, _) = Build();
        var engine = new SearchEngine { ContentBudget = TimeSpan.FromSeconds(10) };
        var plain = new SearchOptions { MatchContent = true };
        var first = engine.Search(index, "budget", plain, SortKey.Name, false, default); // cold: resolves and caches the sets
        Assert.False(first.ContentPending);
        Assert.Equal(Docs / 2, first.Ids.Length); // content in every other file (the budget names are among them)
        Assert.Equal(Docs / 100, first.NameMatches);

        double worst = 0;
        foreach (var (query, sort) in new[] { ("budget", SortKey.Name), ("budget", SortKey.Size), ("yearly", SortKey.Path), ("budget word7", SortKey.Name) })
        {
            var sw = Stopwatch.StartNew();
            var r = engine.Search(index, query, plain, sort, false, default);
            sw.Stop();
            Assert.Null(r.Error);
            output.WriteLine($"{query,-14} {sort,-6} {r.Ids.Length,8:N0} results, {r.NameMatches,6:N0} by name  {sw.Elapsed.TotalMilliseconds,7:F1} ms");
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
        }
        var phrase = Stopwatch.StartNew();
        var exact = engine.Search(index, "content:\"budget report\"", new SearchOptions(), SortKey.Name, false, default);
        output.WriteLine($"phrase over {Docs / 2:N0} candidates: {exact.Ids.Length:N0} results, {phrase.Elapsed.TotalMilliseconds:F0} ms (cold)");
        Assert.Equal(Docs / 2, exact.Ids.Length);
        Assert.True(worst < 2000, $"a plain-word search took {worst:F0} ms");
    }

    [Fact]
    public void Mapping_documents_to_ids_under_the_lock_is_fast()
    {
        var (index, _) = Build();
        var options = new SearchOptions { MatchContent = true };
        var query = QueryParser.Parse("budget", options);
        using var prefetch = ContentSearch.Begin(index, query, options, DateTime.Now, default, null, TimeSpan.FromSeconds(10))!;
        var set = prefetch.For(query)!;
        Assert.Equal(Docs / 2, set.Count);
        var times = new List<double>();
        index.Lock.EnterReadLock();
        try
        {
            var ctx = new SearchContext(index, new FolderPathCache(index), DateTime.Now);
            ContentSearch.Attach(ctx, prefetch);
            for (int run = 0; run < 30; run++)
            {
                var m = new ContentSetMatcher(set);
                var sw = Stopwatch.StartNew();
                m.Prepare(ctx, default);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        finally { index.Lock.ExitReadLock(); }
        times.Sort();
        double p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        output.WriteLine($"Prepare of {Docs / 2:N0} documents: median {times[times.Count / 2]:F2} ms, p95 {p95:F2} ms");
        Assert.True(p95 < 30, $"Prepare p95 {p95:F1} ms");
    }
}
