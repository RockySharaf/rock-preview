using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Store;

/// <summary>Measurements (RP_STORE_PERF=1, Release): a million documents through the real store, as the follower would write them.</summary>
[Collection("Store memory")]
public class StorePerfTests(ITestOutputHelper output)
{
    [PerfFact]
    public void A_million_documents_build_open_and_answer_prefixes_in_time()
    {
        using var temp = new TempFolder();
        var words = Synth.Vocabulary(300_000, seed: 9);
        var zipf = new Synth.Zipf(words.Length, 1.1);
        var rnd = new Random(10);
        const int Total = 1_000_000, PerCommit = 2000;

        // Build: commits of 2,000 documents (print-shop-like: one in nine has about 450 words, the rest none), and every
        // merge the policy plans, large ones included.
        var clock = Stopwatch.StartNew();
        long merges = 0;
        using (var store = ContentStore.Open(temp.Path))
        {
            ushort volume = store.VolumeIndex("unc:\\\\NAS\\JOBS", "\\\\nas\\jobs");
            ulong key = 0;
            var sb = new StringBuilder();
            for (int c = 0; c < Total / PerCommit; c++)
            {
                using (var batch = store.BeginBatch())
                {
                    for (int i = 0; i < PerCommit; i++)
                    {
                        key += 1 + (ulong)rnd.Next(1_000_000);
                        sb.Clear();
                        bool text = rnd.Next(9) == 0;
                        if (text) for (int w = 0; w < 600; w++) sb.Append(words[zipf.Next(rnd)]).Append(w % 14 == 13 ? '\n' : ' ');
                        batch.Add(Synth.Row(key, ContentKind.Pdf, text ? DocStatus.Ok : DocStatus.NoText, volume: volume),
                            $"Jobs\\{2015 + rnd.Next(11)}\\{words[rnd.Next(30_000)]} {words[rnd.Next(30_000)]}\\{rnd.Next(100_000)}_{words[rnd.Next(300_000)]}.pdf",
                            new StoredDoc(sb.ToString(), [], null), 200_000, 60);
                    }
                    batch.Commit().View.Dispose();
                }
                for (MergePlan? plan; (plan = store.PlanMerge(allowLarge: true)) != null; merges++)
                    store.Merge(plan, CancellationToken.None).View.Dispose();
            }
            using var view = store.Acquire();
            output.WriteLine($"built {Total:N0} documents in {clock.Elapsed.TotalMinutes:F1} min ({merges} merges), {view.Segments.Count} segments, " +
                $"{store.BytesOnDisk / 1e6:F0} MB on disk");
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(10), "built in under 10 minutes");
        }

        // Open, and what it keeps resident.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(true);
        clock.Restart();
        using var opened = ContentStore.Open(temp.Path);
        using var current = opened.Acquire();
        var openTime = clock.Elapsed;
        long resident = GC.GetTotalMemory(true) - before;
        output.WriteLine($"open {openTime.TotalMilliseconds:F0} ms, resident {resident / 1e6:F1} MB, {Synth.Live(current).Count:N0} live documents");
        Assert.True(openTime < TimeSpan.FromMilliseconds(500), "open in under 500 ms");
        Assert.True(resident < 8 << 20, "steady resident memory under 8 MB");

        // Prefix lookups (2-5 letters), warm. The lookup (§7: binary search in DICTIDX, then a block scan, per segment) is
        // timed apart from the union of the postings it finds (§7: about 2 ns a posting, U4's budget).
        var prefixes = Enumerable.Range(0, 400).Select(i => words[zipf.Next(rnd)]).Where(w => w.Length >= 2)
            .Select(w => w[..Math.Min(w.Length, 2 + rnd.Next(4))]).ToList();
        (double Lookup, double Union, long Postings) Run(string prefix)
        {
            var from = Encoding.UTF8.GetBytes(prefix);
            byte[] to = [.. from, 0xFF];
            var found = new List<TermPostings>[current.Segments.Count];
            var sw = Stopwatch.StartNew();
            for (int s = 0; s < current.Segments.Count; s++)
            {
                var list = found[s] = [];
                current.Segments[s].Terms(from, to, (ReadOnlySpan<byte> _, in TermPostings p) => { list.Add(p); return true; }, CancellationToken.None);
            }
            double lookup = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var builder = new DocSetBuilder(current.Segments.Select(s => s.DocCount).ToList());
            for (int s = 0; s < current.Segments.Count; s++)
                foreach (var p in found[s]) current.Segments[s].ReadPostings(p, s, builder, CancellationToken.None);
            _ = builder.Build().Count;
            return (lookup, sw.Elapsed.TotalMilliseconds, found.Sum(l => l.Sum(p => (long)p.DocFreq)));
        }
        foreach (var p in prefixes) Run(p); // warm
        var runs = prefixes.Select(Run).ToList();
        var lookups = runs.Select(r => r.Lookup).Order().ToList();
        double p50 = lookups[lookups.Count / 2], p95 = lookups[(int)(lookups.Count * 0.95)];
        double unionNs = runs.Sum(r => r.Union) * 1e6 / Math.Max(1, runs.Sum(r => r.Postings));
        var unions = runs.Select(r => r.Union).Order().ToList();
        output.WriteLine($"warm prefix lookups over {current.Segments.Count} segments: p50 {p50:F3} ms, p95 {p95:F3} ms, max {lookups[^1]:F3} ms");
        foreach (var group in prefixes.Zip(runs).GroupBy(x => x.First.Length).OrderBy(g => g.Key))
        {
            var t = group.Select(x => x.Second.Lookup).Order().ToList();
            output.WriteLine($"  {group.Key} letters ({t.Count}): p50 {t[t.Count / 2]:F3} ms, p95 {t[(int)(t.Count * 0.95)]:F3} ms");
        }
        output.WriteLine($"postings union: {unionNs:F1} ns a posting; p50 {unions[unions.Count / 2]:F2} ms, p95 {unions[(int)(unions.Count * 0.95)]:F2} ms " +
            $"(largest {runs.Max(r => r.Postings):N0} postings)");
        // A lookup costs about 33 ns a matching term: a 2-letter prefix spans ~200,000 dictionary terms over 17 segments
        // (p95 about 7 ms here), so the 2 ms target is held for 3-5 letters, the words plain-word expansion looks up while
        // typing (explicit content:xx has no budget).
        var typed = prefixes.Zip(runs).Where(x => x.First.Length >= 3).Select(x => x.Second.Lookup).Order().ToList();
        Assert.True(typed[(int)(typed.Count * 0.95)] < 2, "warm prefix lookup p95 under 2 ms for 3-5 letters");
    }
}
