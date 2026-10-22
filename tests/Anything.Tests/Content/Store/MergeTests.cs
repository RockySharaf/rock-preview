using System.Buffers.Binary;
using System.Diagnostics;
using Anything.Core.Content;
using Anything.Core.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Store;

public class MergeTests(ITestOutputHelper output)
{
    /// <summary>Three commits (one replacing a key, deletions in between) in a fresh store.</summary>
    static (ContentStore Store, List<SynthDoc> Live) ThreeSegments(string? folder, int perCommit = 700)
    {
        var store = ContentStore.Open(folder);
        var docs = Synth.Docs(perCommit * 3, seed: 21);
        var parts = new[] { docs.Where((_, i) => i % 3 == 0).ToList(), docs.Where((_, i) => i % 3 == 1).ToList(), docs.Where((_, i) => i % 3 == 2).ToList() };
        Synth.Commit(store, parts[0]).View.Dispose();
        Synth.Commit(store, parts[1]).View.Dispose();
        // The third commit replaces one key of the first and deletes some of the second.
        var replaced = parts[0][10] with { Doc = new StoredDoc("replaced text here", [], null) };
        using (var v = store.Acquire())
        {
            var deletes = parts[1].Where((_, i) => i % 7 == 0).Select(d => v.Find(d.Row.Key)!.Value).ToList();
            Synth.Commit(store, [.. parts[2], replaced], deletes).View.Dispose();
        }
        var live = docs.Except(parts[1].Where((_, i) => i % 7 == 0)).Where(d => d.Row.Key != replaced.Row.Key).Append(replaced)
            .OrderBy(d => d.Row.Key).ToList();
        return (store, live);
    }

    [Fact]
    public void A_merge_is_the_union_of_live_documents_in_key_order_with_text_bytes_unchanged()
    {
        using var temp = new TempFolder();
        var (store, live) = ThreeSegments(temp.Path);
        using (store)
        {
            using var before = store.Acquire();
            var rawBefore = live.ToDictionary(d => d.Row.Key, d =>
            {
                var r = before.Find(d.Row.Key)!.Value;
                return before.Segments[r.Segment].ReadStoredRaw(r.Doc).ToArray();
            });
            var plan = new MergePlan([.. before.Segments.Select(s => s.Id)], live.Count, false);
            int paced = 0;
            var change = store.Merge(plan, CancellationToken.None, _ => paced++);
            using var after = change.View;
            Assert.True(paced > 0);

            var seg = Assert.Single(after.Segments);
            Assert.Equal(change.NewSegment, seg.Id);
            Assert.Equal(live.Count, seg.DocCount);
            Assert.Equal(live.Select(d => d.Row.Key), Enumerable.Range(0, seg.DocCount).Select(d => seg.Row(d).Key));
            for (int d = 0; d < live.Count; d++)
            {
                Synth.SameDoc(live[d].Doc, seg.ReadStored(d));
                Assert.Equal(live[d].Path, seg.ReadPath(d));
                Assert.Equal(rawBefore[live[d].Row.Key], seg.ReadStoredRaw(d).ToArray());
                Assert.Equal(live[d].Row.Status, seg.Row(d).Status);
            }
            Assert.Equal(Synth.Postings(live), Synth.ReadAll(seg));

            // Remaps: every old document maps to its new place, deleted ones to -1.
            Assert.Equal(3, change.Remaps.Count);
            foreach (var (old, oldToNew) in change.Remaps)
            {
                int s = before.Segments.ToList().FindIndex(x => x.Id == old);
                for (int d = 0; d < before.Segments[s].DocCount; d++)
                {
                    bool deleted = before.IsDeleted(new DocRef(s, d));
                    Assert.Equal(deleted ? -1 : seg.Find(before.Segments[s].Row(d).Key), oldToNew[d]);
                }
            }

            // An id map follows the merge with no reconcile.
            var builder = DocIdMap.Empty(1).ToBuilder();
            int next = 0;
            var ids = new Dictionary<ulong, int>();
            foreach (var (r, row) in before.RowsByKey())
            {
                var id = before.Segments[r.Segment].Id;
                if (!builder.HasSegment(id)) builder.AddSegment(id, before.Segments[r.Segment].DocCount);
                builder.Set(id, r.Doc, ids[row.Key] = next++);
            }
            builder.Merge(change.Remaps, seg.Id, seg.DocCount);
            var map = builder.Build(1);
            for (int d = 0; d < seg.DocCount; d++) Assert.Equal(ids[seg.Row(d).Key], map.FileId(seg.Id, d));

            Assert.Equal(before.Stats.OrderBy(k => k.Key).Select(k => (k.Key, k.Value.Docs, k.Value.WithText, k.Value.RawTextBytes, k.Value.StoredBytes, k.Value.Problems)),
                after.Stats.OrderBy(k => k.Key).Select(k => (k.Key, k.Value.Docs, k.Value.WithText, k.Value.RawTextBytes, k.Value.StoredBytes, k.Value.Problems)));
        }
        using var reopened = ContentStore.Open(temp.Path);
        using var view = reopened.Acquire();
        Assert.Equal(live.Count, Synth.Live(view).Count);
    }

    [Fact]
    public void A_cancelled_merge_changes_nothing_and_leaves_no_temp_files()
    {
        using var temp = new TempFolder();
        var (store, live) = ThreeSegments(temp.Path, perCommit: 1500);
        using (store)
        {
            var names = temp.Names();
            long generation = store.Generation;
            using var cts = new CancellationTokenSource();
            using var view = store.Acquire();
            var plan = new MergePlan([.. view.Segments.Select(s => s.Id)], live.Count, false);
            int calls = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => store.Merge(plan, cts.Token, ct =>
            {
                if (++calls == 2) cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }));
            Assert.Equal(generation, store.Generation);
            Assert.Equal(names.Where(n => n != "tmp"), temp.Names().Where(n => n != "tmp"));
            Assert.False(Directory.Exists(temp.File("tmp")) && Directory.EnumerateFiles(temp.File("tmp")).Any());
            Assert.DoesNotContain(temp.Names(), n => n.EndsWith(".tmp"));
            using var now = store.Acquire();
            Assert.Equal(live.Count, Synth.Live(now).Count);

            // A merge after it works, and spends a new segment id (never reusing one).
            var change = store.Merge(plan, CancellationToken.None);
            Assert.True(change.NewSegment > view.Segments.Max(s => s.Id));
            change.View.Dispose();
        }
    }

    [Fact]
    public void A_damaged_input_is_dropped_and_the_others_merge()
    {
        using var temp = new TempFolder();
        List<SynthDoc> live;
        uint victim;
        {
            var (store, l) = ThreeSegments(temp.Path, perCommit: 300);
            live = l;
            using (var v = store.Acquire()) victim = v.Segments[1].Id;
            store.Dispose();
        }
        // Flip a byte inside TEXT (checked by the scrub and before a merge, not at open).
        var path = temp.File(SegmentFormat.FileName(victim));
        var bytes = File.ReadAllBytes(path);
        bytes[SegmentFormat.HeaderSize + 100] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        using var reopened = ContentStore.Open(temp.Path);
        using var view = reopened.Acquire();
        Assert.Equal(3, view.Segments.Count);
        var change = reopened.Merge(new MergePlan([.. view.Segments.Select(s => s.Id)], 0, false), CancellationToken.None);
        using var after = change.View;
        Assert.Contains(victim, change.DroppedSegments);
        Assert.Equal(2, change.Remaps.Count);
        Assert.DoesNotContain(change.Remaps, r => r.OldSegment == victim);
        var keysOfVictim = Enumerable.Range(0, view.Segments[1].DocCount).Where(d => !view.IsDeleted(new DocRef(1, d)))
            .Select(d => view.Segments[1].Row(d).Key).ToHashSet();
        Assert.Equal(live.Count - keysOfVictim.Count, Synth.Live(after).Count);
    }

    [Fact]
    public void The_scrub_finds_a_flipped_bit_and_the_next_commit_drops_the_segment()
    {
        using var temp = new TempFolder();
        uint victim;
        using (var store = ContentStore.Open(temp.Path))
        {
            Synth.Commit(store, Synth.Docs(200, seed: 2)).View.Dispose();
            Synth.Commit(store, Synth.Docs(200, seed: 3)).View.Dispose();
            using var v = store.Acquire();
            victim = v.Segments[0].Id;
        }
        var path = temp.File(SegmentFormat.FileName(victim));
        var bytes = File.ReadAllBytes(path);
        bytes[SegmentFormat.HeaderSize + 10] ^= 1; // inside TEXT
        File.WriteAllBytes(path, bytes);

        using var reopened = ContentStore.Open(temp.Path);
        while (!reopened.Scrub(CancellationToken.None)) { }
        Assert.True(reopened.HasDamage);
        var change = Synth.Commit(reopened, []);
        using var after = change.View;
        Assert.Equal([victim], change.DroppedSegments);
        Assert.Single(after.Segments);
        Assert.False(reopened.HasDamage);
    }

    [Fact]
    public void A_segment_of_an_older_tokenizer_is_planned_for_a_rewrite()
    {
        using var temp = new TempFolder();
        uint id;
        var docs = Synth.Docs(100, seed: 5);
        using (var store = ContentStore.Open(temp.Path))
        {
            Synth.Commit(store, docs).View.Dispose();
            using var v = store.Acquire();
            id = v.Segments[0].Id;
        }
        // Pretend tokenizer version 0 wrote it: patch the header and its CRCs.
        var path = temp.File(SegmentFormat.FileName(id));
        var b = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), 0);
        int tocLength = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(b.Length - 16));
        int toc = b.Length - 16 - tocLength;
        for (int e = toc; e < toc + tocLength; e += SegmentFormat.TocEntrySize)
            if (b[e] == 0) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e + 17), Crc32C.Compute(b.AsSpan(0, SegmentFormat.HeaderSize)));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(b.Length - 12), Crc32C.Compute(b.AsSpan(toc, tocLength)));
        File.WriteAllBytes(path, b);

        using var reopened = ContentStore.Open(temp.Path);
        using (var v = reopened.Acquire()) Assert.Equal(0, v.Segments[0].TokenizerVersion);
        var plan = reopened.PlanMerge(allowLarge: false);
        Assert.NotNull(plan);
        Assert.Equal([id], plan!.Segments);
        using var after = reopened.Merge(plan, CancellationToken.None).View;
        Assert.Equal(ContentTokenizer.Version, after.Segments[0].TokenizerVersion);
        Assert.Equal(Synth.Postings(docs), Synth.ReadAll(after.Segments[0]));
        Assert.Null(reopened.PlanMerge(true));
    }

    [Fact]
    public void Plans_for_wholly_deleted_unknown_or_single_segments()
    {
        Assert.Equal([2u], MergePolicy.Plan([(1, 100, 0, 1000), (2, 50, 50, 500)], false)!.Segments);
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [new SynthDoc(Synth.Row(1), "a", new StoredDoc("x y", [], null))]).View.Dispose();
        using var v = store.Acquire();
        uint unknown = v.Segments[0].Id + 1_000_000; // numbers count on across the process: pick one this store can't have
        var change = store.Merge(new MergePlan([v.Segments[0].Id, unknown], 0, false), CancellationToken.None);
        using var after = change.View;
        Assert.NotNull(change.NewSegment); // one live document: rewritten
        Assert.Equal([v.Segments[0].Id], change.DroppedSegments); // gone from the view, its documents remapped
        Assert.Equal([v.Segments[0].Id], change.Remaps.Select(r => r.OldSegment));
        var none = store.Merge(new MergePlan([unknown + 1], 0, false), CancellationToken.None); // unknown ids: nothing to do
        Assert.Null(none.NewSegment);
        none.View.Dispose();
    }

    [Fact]
    public void The_policy_merges_the_eight_smallest_of_a_tier_and_leaves_full_segments_alone()
    {
        Assert.Equal(0, MergePolicy.Tier(0));
        Assert.Equal(0, MergePolicy.Tier(32_767));
        Assert.Equal(1, MergePolicy.Tier(32_768));
        Assert.Equal(1, MergePolicy.Tier(262_143));
        Assert.Equal(2, MergePolicy.Tier(262_144));
        Assert.Equal(3, MergePolicy.Tier(2_097_152));

        var seven = Enumerable.Range(1, 7).Select(i => ((uint)i, 1000, 0, 100_000L)).ToList();
        Assert.Null(MergePolicy.Plan(seven, true));
        var nine = Enumerable.Range(1, 9).Select(i => ((uint)i, 1000 + i, 0, 100_000L)).ToList();
        var plan = MergePolicy.Plan(nine, false)!;
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (uint)i), plan.Segments);
        Assert.False(plan.Large);

        // Full segments never count toward a tier.
        var full = Enumerable.Range(1, 8).Select(i => ((uint)i, 1_200_000, 0, 2L << 30)).ToList();
        Assert.Null(MergePolicy.Plan(full, true));
        // Large merges wait for allowLarge.
        var big = Enumerable.Range(1, 8).Select(i => ((uint)i, 40_000, 0, 50L << 20)).ToList();
        Assert.Null(MergePolicy.Plan(big, false));
        Assert.True(MergePolicy.Plan(big, true)!.Large);
        // 30% deleted: rewritten alone, even a full one when allowed.
        var deleted = new List<(uint, int, int, long)> { (1, 10_000, 2_000, 1L << 20), (2, 10_000, 3_500, 1L << 20), (3, 1_500_000, 600_000, 3L << 30) };
        Assert.Equal([2u], MergePolicy.Plan(deleted, false)!.Segments);
        Assert.Equal([3u], MergePolicy.Plan(deleted, true)!.Segments);
    }

    [Fact]
    public void The_tiered_policy_converges_at_a_million_documents()
    {
        // First pass (commits of 200-2,000 documents) and steady state (commits of 1-5): bounded segments, each document
        // merged a few times, never a rewrite of a large segment per handful of commits.
        var (segments, amplification) = Simulate(200, 2000, 1_000_000);
        Assert.True(segments <= 7 * 7 + 1, $"{segments} non-full segments");
        Assert.True(amplification < 5, $"each document merged {amplification:F2} times");
        (segments, amplification) = Simulate(1, 5, 200_000);
        Assert.True(segments <= 7 * 7 + 1, $"{segments} non-full segments");
        Assert.True(amplification < 8, $"each document merged {amplification:F2} times");
    }

    (int MaxNonFull, double Amplification) Simulate(int minCommit, int maxCommit, int total)
    {
        // Simulated: commits of minCommit-maxCommit documents, a deletion now and then, every planned merge applied.
        var rnd = new Random(8);
        var segments = new List<(uint Id, int Docs, int Deleted, long Bytes)>();
        uint next = 1;
        long added = 0, merged = 0;
        int maxNonFull = 0, merges = 0;
        while (added < total)
        {
            int n = rnd.Next(minCommit, maxCommit + 1);
            segments.Add((next++, n, 0, n * 1000L));
            added += n;
            if (rnd.Next(10) == 0 && segments.Count > 0)
            {
                int i = rnd.Next(segments.Count);
                var s = segments[i];
                segments[i] = s with { Deleted = Math.Min(s.Docs, s.Deleted + rnd.Next(1, Math.Max(2, s.Docs / 5))) };
            }
            for (int guard = 0; ; guard++)
            {
                Assert.True(guard < 100, "the policy keeps planning");
                var plan = MergePolicy.Plan(segments, allowLarge: true);
                if (plan == null) break;
                var inputs = segments.Where(s => plan.Segments.Contains(s.Id)).ToList();
                int live = inputs.Sum(s => s.Docs - s.Deleted);
                segments.RemoveAll(s => plan.Segments.Contains(s.Id));
                if (live > 0) segments.Add((next++, live, 0, live * 1000L));
                merged += live;
                merges++;
            }
            maxNonFull = Math.Max(maxNonFull, segments.Count(s => !MergePolicy.IsFull(s.Docs, s.Bytes)));
        }
        output.WriteLine($"commits of {minCommit}-{maxCommit}: segments {segments.Count}, most non-full {maxNonFull}, merges {merges}, " +
            $"each document merged {merged / (double)added:F2} times");
        return (maxNonFull, merged / (double)added);
    }

    [Fact]
    public void Eight_small_commits_are_planned_into_one_merge()
    {
        using var store = ContentStore.Open(null);
        for (int i = 0; i < 8; i++)
            Synth.Commit(store, [new SynthDoc(Synth.Row((ulong)i + 1), $"{i}.txt", new StoredDoc($"doc {i}", [], null))]).View.Dispose();
        var plan = store.PlanMerge(allowLarge: false);
        Assert.NotNull(plan);
        Assert.Equal(8, plan!.Segments.Count);
        using var after = store.Merge(plan, CancellationToken.None).View;
        Assert.Single(after.Segments);
        Assert.Null(store.PlanMerge(true));
    }
}

/// <summary>
/// Store tests that run with no other test alongside: merges measured for memory (the heap is theirs) and the many-thread
/// stress tests (which would starve other tests' timing assertions).
/// </summary>
[CollectionDefinition("Store memory", DisableParallelization = true)]
public class StoreMemoryCollection;

[Collection("Store memory")]
public class MergeMemoryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Office-sized synthetic documents, made one at a time in ascending key order (segment number in the top byte): about
    /// 500 distinct words each from a 200,000-word Zipf vocabulary (the brief's Office average is 508).
    /// </summary>
    internal static IEnumerable<SynthDoc> OfficeDocs(int count, int segment, string[] words, Synth.Zipf zipf)
    {
        var rnd = new Random(1000 + segment);
        var sb = new System.Text.StringBuilder(8192);
        for (int i = 0; i < count; i++)
        {
            ulong key = ((ulong)segment << 56) | ((ulong)i << 8) | (byte)rnd.Next(256);
            sb.Clear();
            for (int w = 0; w < 640; w++) sb.Append(words[zipf.Next(rnd)]).Append(w % 15 == 14 ? '\n' : ' ');
            yield return new SynthDoc(Synth.Row(key, ContentKind.Office), $"Office\\Folder{rnd.Next(500)}\\doc{segment}_{i}.docx",
                new StoredDoc(sb.ToString(), [(DocField.Title, $"{words[zipf.Next(rnd)]} {words[zipf.Next(rnd)]}")], null), 200_000, 18);
        }
    }

    /// <summary>Writes inputs segments straight to files (in parallel) and a manifest listing them, then opens the store.</summary>
    internal static ContentStore StoreOf(string folder, int segments, int docsEach, out long docsTotal, out long pairsTotal)
    {
        var words = Synth.Vocabulary(200_000, seed: 77);
        var zipf = new Synth.Zipf(words.Length, 1.05);
        var entries = new Manifest.SegmentEntry[segments];
        long pairs = 0;
        Parallel.For(0, segments, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(segments, Environment.ProcessorCount) }, s =>
        {
            uint id = (uint)s + 1;
            // Each segment its own key range, so segments can be written independently.
            using var w = new SegmentWriter(ContentTokenizer.Version, Path.Combine(folder, "tmp"));
            foreach (var d in OfficeDocs(docsEach, s, words, zipf)) w.Add(d.Row, d.Path, d.Doc, default, d.BytesRead, d.Ms);
            string path = Path.Combine(folder, SegmentFormat.FileName(id));
            SegmentInfo info;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20)) info = w.Write(fs, id);
            Interlocked.Add(ref pairs, info.Stats.Values.Sum(k => k.Pairs));
            entries[s] = new Manifest.SegmentEntry { Id = id, DocCount = info.DocCount, Bytes = info.Bytes };
        });
        var manifest = new Manifest { Generation = 1, NextSegmentId = (uint)segments + 1, Segments = entries };
        File.WriteAllBytes(Path.Combine(folder, "manifest"), manifest.Serialize());
        docsTotal = (long)segments * docsEach;
        pairsTotal = pairs;
        return ContentStore.Open(folder);
    }

    /// <summary>The most managed memory live during the action: full collections every 50 ms, less the baseline.</summary>
    internal static (long PeakLive, long PeakSampled) Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peakLive = 0, peakSampled = 0;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            int tick = 0;
            while (!done.Wait(5))
            {
                peakSampled = Math.Max(peakSampled, GC.GetTotalMemory(false) - baseline);
                if (++tick % 10 == 0) peakLive = Math.Max(peakLive, GC.GetTotalMemory(true) - baseline);
            }
        }) { IsBackground = true };
        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            done.Set();
            sampler.Join();
        }
        return (peakLive, peakSampled);
    }

    [Fact]
    public void Merging_office_sized_documents_stays_in_bounded_memory()
    {
        using var temp = new TempFolder();
        using var store = StoreOf(temp.Path, segments: 8, docsEach: 1500, out long docs, out long pairs);
        RunMerge(store, docs, pairs);
    }

    [PerfFact]
    public void Merging_8_x_128K_office_sized_documents_peaks_under_96_MB()
    {
        using var temp = new TempFolder();
        var clock = Stopwatch.StartNew();
        using var store = StoreOf(temp.Path, segments: 8, docsEach: 128 * 1024, out long docs, out long pairs);
        output.WriteLine($"built 8 inputs in {clock.Elapsed.TotalSeconds:F0} s");
        RunMerge(store, docs, pairs);
    }

    void RunMerge(ContentStore store, long docs, long pairs)
    {
        using var view = store.Acquire();
        var plan = new MergePlan([.. view.Segments.Select(s => s.Id)], (int)docs, docs > MergePolicy.LargeDocs);
        StoreChange? change = null;
        var clock = Stopwatch.StartNew();
        var (live, sampled) = Measure(() => change = store.Merge(plan, CancellationToken.None));
        clock.Stop();
        using var after = change!.View;
        output.WriteLine($"{docs:N0} documents, {pairs:N0} (term, document) pairs merged in {clock.Elapsed.TotalSeconds:F1} s; " +
            $"peak live managed memory {live / 1e6:F1} MB (sampled with garbage {sampled / 1e6:F1} MB); output {after.Segments[0].DocCount:N0} docs");
        Assert.Equal(docs, after.Segments[0].DocCount);
        Assert.True(live <= 96L << 20, $"peak {live / 1e6:F1} MB");
    }
}
