using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Anything.Tests.Content.Search;
using Anything.Tests.Search;

namespace Anything.Tests.Content.Integration;

/// <summary>
/// I1: where search (U4) meets the real store (U1) beyond U4's own cases: postings long enough for skip lists, the
/// per-segment cache across real commits and merges, a lookup's lease across a merge on disk, the real view published
/// as is, a reopened folder, and a re-add by key across commits.
/// </summary>
[Collection(ContentSearchSerial.Name)]
public sealed class SearchStoreSeamTests : IDisposable
{
    readonly StoreFixtures _fixtures = new();

    public void Dispose() => _fixtures.Dispose();

    static string[] Sorted(ContentFixture f, SearchResults r) => [.. r.Ids.Select(f.Index.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void The_fixture_reads_through_the_stores_own_segments()
    {
        var f = _fixtures.Make(onDisk: true);
        f.Doc(@"C:\Docs\a.txt", "alpha");
        f.NextSegment();
        f.Doc(@"C:\Docs\b.txt", "beta");
        var view = f.Publish();
        Assert.Equal(2, view.FakeSegments.Count);
        Assert.All(view.FakeSegments, s => Assert.IsType<SegmentReader>(s.Inner));
        Assert.NotNull(view.Inner);
        Assert.Equal([@"C:\Docs\a.txt"], f.Find("content:alpha"));
    }

    [Fact]
    public void Long_postings_with_skip_lists_answer_words_ands_prefixes_and_phrases()
    {
        // Thousands of documents per word: postings past 256 documents carry skip entries, and ANDs and phrases jump through them.
        var f = _fixtures.Make(onDisk: true);
        var bodies = new Dictionary<string, string>();
        for (int i = 0; i < 4500; i++)
        {
            if (i == 3000) f.NextSegment();
            string body = (i % 7 == 0 ? "the annual report is due" : i % 5 == 0 ? "report filed, annual review later" : "the annual budget")
                + (i % 11 == 0 ? " INV-2024-" + i.ToString("D4") : "") + (i % 13 == 0 ? " reporting" : "");
            string path = $@"C:\Big\d{i:D4}.txt";
            f.Doc(path, body);
            bodies[path] = body;
        }
        f.Publish();
        string[] Expect(string value, bool prefix = true, bool whole = false)
        {
            var word = ContentTokenizer.Analyze(value, lastIsPrefix: prefix);
            return [.. bodies.Where(b => ContentTokenizer.FindSequence(b.Value, word, false, false, whole).Start >= 0).Select(b => b.Key)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        Assert.Equal(4500, f.Run("content:annual").Ids.Length);
        Assert.Equal(Expect("report"), Sorted(f, f.Run("content:report")));
        Assert.Equal(Expect("report", prefix: false, whole: true), Sorted(f, f.Run("ww:content:report")));
        Assert.Equal(Expect("annual report"), Sorted(f, f.Run("content:\"annual report\"")));
        Assert.Equal(Expect("report is due"), Sorted(f, f.Run("content:\"report is due\"")));
        Assert.Equal(Expect("INV-2024-01"), Sorted(f, f.Run("content:INV-2024-01")));
        Assert.Equal(Expect("report").Intersect(Expect("budget")).Order(StringComparer.OrdinalIgnoreCase),
            Sorted(f, f.Run("content:report content:budget")));
        Assert.Equal(Expect("annual").Except(Expect("report")).Order(StringComparer.OrdinalIgnoreCase),
            Sorted(f, f.Run("content:annual !content:report")));
        Assert.Equal(Expect("report").Intersect(Expect("reporting")).Order(StringComparer.OrdinalIgnoreCase),
            Sorted(f, f.Run("content:report content:reporting")));
    }

    [Fact]
    public void The_cache_keeps_one_set_per_segment_across_real_commits_and_a_merge()
    {
        var f = _fixtures.Make(onDisk: true);
        f.Doc(@"C:\Docs\minutes.txt", "the budget for next year");
        f.NextSegment();
        f.Doc(@"C:\Docs\plan.txt", "budget plan draft");
        f.Doc(@"C:\Docs\other.txt", "nothing");
        var first = f.Publish();
        Assert.Equal([@"C:\Docs\minutes.txt", @"C:\Docs\plan.txt"], f.Set("content:budget"));
        int[] scans = [.. first.FakeSegments.Select(s => s.TermScans)];
        string key = ContentPlan.Build(QueryParser.Parse("content:budget", new SearchOptions()), new SearchOptions(), ContentMode.Documents,
            ContentFixture.Now).Terms.Values.Single().Key;
        Assert.Equal(2, f.Source.Cache.SetCount(key));

        // A third commit: only the new segment is looked up.
        f.NextSegment();
        f.Doc(@"C:\Docs\later.txt", "budget again");
        var second = f.Publish();
        Assert.Equal([@"C:\Docs\later.txt", @"C:\Docs\minutes.txt", @"C:\Docs\plan.txt"], f.Set("content:budget"));
        Assert.Equal(scans, second.FakeSegments.Take(2).Select(s => s.TermScans));
        Assert.Equal(1, second.FakeSegments[2].TermScans);

        // The store merges all three: one new segment, looked up once; the merged-away segments' sets are dropped.
        var change = f.Merge();
        Assert.Equal(3, change.DroppedSegments.Count);
        var merged = f.View!;
        Assert.Single(merged.FakeSegments);
        Assert.Equal([@"C:\Docs\later.txt", @"C:\Docs\minutes.txt", @"C:\Docs\plan.txt"], f.Set("content:budget"));
        Assert.Equal([@"C:\Docs\later.txt", @"C:\Docs\minutes.txt", @"C:\Docs\plan.txt"], f.Set("content:budget"));
        Assert.Equal(1, merged.FakeSegments[0].TermScans);
        Assert.Equal(1, f.Source.Cache.SetCount(key));
        Assert.Equal([@"C:\Docs\plan.txt"], f.Set("content:\"budget plan\""));
    }

    [Fact]
    public void A_lookup_running_across_a_merge_keeps_its_segment_files_until_it_ends()
    {
        var cache = new DocSetCache();
        var f = _fixtures.Make(onDisk: true, cache);
        f.Doc(@"C:\Docs\budget.txt", "nothing to see");
        f.Doc(@"C:\Docs\minutes.txt", "the budget for next year");
        f.NextSegment();
        f.Doc(@"C:\Docs\plan.txt", "budget plan draft");
        var before = f.Publish();
        string folder = f.FolderPath!;
        uint[] oldIds = [.. before.FakeSegments.Select(s => s.Id)];
        using var gate = new ManualResetEventSlim(false);
        before.FakeSegments[0].OnTerms = () => gate.Wait(TimeSpan.FromSeconds(20));
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(30);
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);

        // The store merges and the index publishes the merged view; then one more commit, so no manifest lists the old segments.
        f.Merge();
        f.NextSegment();
        f.Doc(@"C:\Docs\later.txt", "unrelated");
        f.Publish();
        Assert.False(before.Released, "the view was released under a running lookup");
        Assert.All(before.FakeSegments, s => Assert.False(((SegmentReader)s.Inner!).IsReleased));
        foreach (uint id in oldIds) Assert.True(File.Exists(Path.Combine(folder, SegmentFormat.FileName(id))), $"segment {id} went under a lease");

        // The lookup finishes on the old segments (still mapped), then lets them go.
        gate.Set();
        Assert.True(f.Source.WaitCompleted(TimeSpan.FromSeconds(20)), "the lookup never finished");
        Assert.True(SpinWait.SpinUntil(() => before.Released, TimeSpan.FromSeconds(20)), "the lookup never released its lease");
        Assert.All(before.FakeSegments, s => Assert.True(((SegmentReader)s.Inner!).IsReleased));
        foreach (uint id in oldIds) Assert.False(File.Exists(Path.Combine(folder, SegmentFormat.FileName(id))), $"segment {id} is still there");

        // The merged view answers in full.
        var r = f.Run("budget", ContentFixture.Plain);
        Assert.False(r.ContentPending);
        Assert.Equal([@"C:\Docs\budget.txt", @"C:\Docs\minutes.txt", @"C:\Docs\plan.txt"], r.Ids.Select(f.Index.GetFullPath));
        Assert.Equal(1, r.NameMatches);
    }

    [Fact]
    public void The_stores_own_view_published_as_is_answers_and_a_reopened_folder_answers_the_same()
    {
        using var temp = new Store.TempFolder();
        var tree = new TreeBuilder();
        var docs = new (string Path, string Body, (DocField, string)[] Fields)[]
        {
            (@"C:\Docs\letter.docx", "Dear client,\nplease find the invoice INV-2024-001 attached.", [(DocField.Title, "Invoice letter")]),
            (@"C:\Docs\notes.txt", "Meeting notes\nThe annual report is due.", []),
            (@"C:\Docs\cafe.txt", "café au lait", []),
            (@"C:\Docs\tokyo.txt", "東京都に行きました。", []),
        };
        var ids = docs.Select(d => tree.File(d.Path, 10, TreeBuilder.Ft(2024, 1, 1))).ToArray();
        string[] queries = ["content:invoice", "content:\"annual report\"", "content:INV-2024", "title:invoice", "content:cafe", "case:content:café",
            "content:東京都", "content:"];

        string[][] Answers(ContentStore store)
        {
            var source = new FakeContentSource();
            var view = store.Acquire();
            var map = DocIdMap.Empty(tree.Index.Generation).ToBuilder();
            foreach (var seg in view.Segments) map.AddSegment(seg.Id, seg.DocCount);
            for (int i = 0; i < docs.Length; i++)
                if (view.Find(ContentFixture.KeyOf(docs[i].Path)) is { } d) map.Set(view.Segments[d.Segment].Id, d.Doc, ids[i]);
            source.Publish(new ContentView(view, map.Build(tree.Index.Generation), ContentMode.Documents));
            tree.Index.Content = source;
            var engine = new SearchEngine { Clock = () => ContentFixture.Now, ContentBudget = TimeSpan.FromSeconds(30) };
            try
            {
                return [.. queries.Select(q =>
                {
                    var r = engine.Search(tree.Index, q, new SearchOptions(), SortKey.Name, false, default);
                    Assert.Null(r.Error);
                    return (string[])[.. r.Ids.Select(tree.Index.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)];
                })];
            }
            finally
            {
                tree.Index.Content = null;
                source.Publish(null);
            }
        }

        string[][] first;
        using (var store = ContentStore.Open(temp.Path))
        {
            ushort volume = store.VolumeIndex("vol:test", "C:\\");
            using (var batch = store.BeginBatch())
            {
                foreach (var (path, body, fields) in docs.Take(2))
                    batch.Add(new DocRow(ContentFixture.KeyOf(path), 0, 10, volume, 0, ContentTypes.KindOf(path), DocStatus.Ok, 1, 0), path[3..],
                        new StoredDoc(body, fields, null));
                batch.Commit().View.Dispose();
            }
            using (var batch = store.BeginBatch())
            {
                foreach (var (path, body, fields) in docs.Skip(2))
                    batch.Add(new DocRow(ContentFixture.KeyOf(path), 0, 10, volume, 0, ContentKind.Text, DocStatus.Ok, 1, 0), path[3..],
                        new StoredDoc(body, fields, null));
                batch.Commit().View.Dispose();
            }
            first = Answers(store);
        }
        Assert.Equal([@"C:\Docs\letter.docx"], first[0]);
        Assert.Equal([@"C:\Docs\notes.txt"], first[1]);
        Assert.Equal([@"C:\Docs\letter.docx"], first[2]);
        Assert.Equal([@"C:\Docs\letter.docx"], first[3]);
        Assert.Equal([@"C:\Docs\cafe.txt"], first[4]);
        Assert.Equal([@"C:\Docs\cafe.txt"], first[5]);
        Assert.Equal([@"C:\Docs\tokyo.txt"], first[6]);
        Assert.Equal(4, first[7].Length);

        using (var reopened = ContentStore.Open(temp.Path)) Assert.Equal(first, Answers(reopened));
        using (var readOnly = ContentStore.Open(temp.Path, readOnly: true)) Assert.Equal(first, Answers(readOnly));
    }

    [Fact]
    public void A_document_added_again_under_its_key_replaces_the_old_one_across_commits()
    {
        var f = _fixtures.Make(onDisk: false);
        f.Doc(@"C:\Docs\letter.docx", "the invoice is attached");
        f.Doc(@"C:\Docs\other.txt", "an invoice too");
        f.Publish();
        Assert.Equal([@"C:\Docs\letter.docx", @"C:\Docs\other.txt"], f.Set("content:invoice"));
        f.NextSegment();
        f.Doc(@"C:\Docs\letter.docx", "the receipt is attached");
        f.Publish();
        Assert.Equal([@"C:\Docs\other.txt"], f.Set("content:invoice"));
        Assert.Equal([@"C:\Docs\letter.docx"], f.Set("content:receipt"));
        Assert.Equal([@"C:\Docs\letter.docx", @"C:\Docs\other.txt"], f.Set("content:attached|content:too"));
        f.Merge();
        Assert.Equal([@"C:\Docs\other.txt"], f.Set("content:invoice"));
        Assert.Equal([@"C:\Docs\letter.docx"], f.Set("content:receipt"));
    }
}
