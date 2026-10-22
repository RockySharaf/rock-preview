using System.Diagnostics;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// U5's measured targets with fakes: a full reconcile of 850,000 documents in a 3,000,000-entry index (under 3 s and 30 MB
/// transient), the Compacted handler (under 5 ms), no read-lock hold above 20 ms, and a resume after a stop mid-run with
/// no duplicates and nothing lost.
/// </summary>
[Collection(TimingCollection.Name)]
public class IndexingPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void A_full_reconcile_of_850_thousand_documents_in_a_3_million_entry_index()
    {
        const int Folders = 850, PerFolder = 1000, OtherFolders = 2000, PerOther = 1075;
        var index = new FileIndex();
        var rows = new List<DocRow>(Folders * PerFolder);
        long stamp = 133_500_000_000_000_000;
        int firstOther = -1, firstDocs = -1;
        index.BulkAdd(add =>
        {
            int root = add.Add("C:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            int users = add.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
            // Out of scope, before every document's file: removing it moves every mapped id (the Compacted worst case).
            firstDocs = add.Add("Public", users, FileAttributes.Directory, -1, 0, 0);
            for (int i = 0; i < 100; i++) add.Add($"p{i}.dat", firstDocs, FileAttributes.Archive, 10, stamp, 0);
            int me = add.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
            int windows = add.Add("Windows", root, FileAttributes.Directory, -1, 0, 0);
            string[] exts = ["pdf", "docx", "txt"];
            for (int f = 0; f < Folders; f++)
            {
                int folder = add.Add("D" + f, me, FileAttributes.Directory, -1, 0, 0);
                ulong state = ContentKeys.Of("path:C:", @"Users\Me\D" + f);
                for (int i = 0; i < PerFolder; i++)
                {
                    string name = $"f{i}.{exts[i % 3]}";
                    long modified = stamp + f * PerFolder + i;
                    add.Add(name, folder, FileAttributes.Archive, 1000 + i, modified, 0);
                    rows.Add(new DocRow(ContentKeys.Continue(state, name), modified, 1000 + i, 0, 0,
                        i % 3 == 0 ? ContentKind.Pdf : i % 3 == 1 ? ContentKind.Office : ContentKind.Text, DocStatus.Ok, 0, DocFlags.HasText));
                }
            }
            for (int f = 0; f < OtherFolders; f++)
            {
                int folder = add.Add("S" + f, windows, FileAttributes.Directory, -1, 0, 0);
                if (f == 0) firstOther = folder;
                for (int i = 0; i < PerOther; i++) add.Add($"w{i}.dll", folder, FileAttributes.Archive, 10, stamp, 0);
            }
        });
        output.WriteLine($"index: {index.Count:N0} entries");

        // The store already holds every document with the same stamps: the reconcile binds, reads nothing.
        var store = new FakeStore();
        store.VolumeIndex("path:C:", @"C:\");
        var stored = new StoredDoc("w", [], null);
        using (var batch = store.BeginBatch())
        {
            foreach (var row in rows) batch.Add(row, "p", stored);
            batch.Commit();
        }
        rows = null!;
        var files = new FakeFiles();
        // The real root gate, over a manager whose GetRoots counts every entry under the read lock as IndexManager's does
        // (CountByRoot): it must not count again because the follower reconciles.
        var getRootsMs = new List<double>();
        var names = new FakeNames(index)
        {
            Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "MFT", RootStatus.Ready, index.Count, null)],
            OnGetRoots = () =>
            {
                var sw = Stopwatch.StartNew();
                index.CountByRoot();
                lock (getRootsMs) getRootsMs.Add(sw.Elapsed.TotalMilliseconds);
            },
        };
        var clock = new FakeClock();
        using var gate = new RootGate(names);
        Assert.True(SpinWait.SpinUntil(() => gate.Loads == 1, 10_000));
        var content = new ContentIndex(() => store.Reopen(), files, gate, NoPowerPolicy.Instance, new ContentIndexOptions
        {
            ManualLoop = true, VolumeResolver = Harness.Resolve, RecentItems = () => [], Clock = () => clock.Now,
        });
        content.Start(names, Harness.DefaultSettings());
        var follower = content.Follower!;
        // Building 400 MB of test data leaves the GC with work: settle it first, so a GC pause isn't counted as a hold.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        follower.HoldLog = [];
        int gcs = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        follower.RunUntilIdle();
        var firstHolds = follower.HoldLog.ToList();
        output.WriteLine($"first reconcile: {Describe(firstHolds)}, GCs {GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - gcs}, " +
            $"longest at {follower.MaxReadLockSite}");
        Assert.Equal(0, files.Reads);
        Assert.Equal(1, follower.FullReconciles);

        // The measured ones: a log reset forces another full reconcile (three times in Release, where holds are timed).
        var runs = new List<List<double>>();
        long transient = 0, elapsed = 0;
        for (int run = 0; run < (Timed ? 3 : 1); run++)
        {
            follower.ResetMeasurements();
            follower.HoldLog = [];
            index.Configure(dateAccessed: run % 2 == 0, folderSizes: false, recentChanges: false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            int target = follower.FullReconciles + 1;
            while (follower.FullReconciles < target) follower.RunOnce();
            sw.Stop();
            transient = Math.Max(transient, GC.GetAllocatedBytesForCurrentThread() - allocated);
            elapsed = run == 0 ? sw.ElapsedMilliseconds : Math.Min(elapsed, sw.ElapsedMilliseconds);
            runs.Add([.. follower.HoldLog]);
            output.WriteLine($"reconcile {run + 1}: {sw.ElapsedMilliseconds} ms, {Describe(runs[^1])}, longest at {follower.MaxReadLockSite}");
        }
        Assert.Equal(0, files.Reads);
        Assert.True(elapsed < 3000 * SlowFactor, $"{elapsed} ms");
        Assert.True(transient < 30_000_000, $"{transient / 1e6:F1} MB");
        // The gate counted once (when it was made), not for any reconcile; that count's own hold is the manager's.
        Assert.Equal(1, names.GetRootsCalls);
        output.WriteLine($"GetRoots (CountByRoot, one read-lock hold over {index.Count:N0} entries): {getRootsMs[0]:F1} ms, once");

        // Every document is bound to its file.
        using (var view = content.AcquireView()!)
        {
            index.Lock.EnterReadLock();
            DocIdMap? map;
            try { map = content.MapFor(index, view); }
            finally { index.Lock.ExitReadLock(); }
            int bound = 0;
            map!.ForEachMapped((_, _, _) => bound++);
            Assert.Equal(Folders * PerFolder, bound);
        }

        // Compact (an out-of-scope folder removed after the documents' files, as a rescan's old copy is): the handler
        // translates the published map inside the write lock. Five times, each another folder.
        var compacted = new List<double>();
        for (int k = 0; k < 5; k++)
        {
            index.Remove(firstOther); // C:\Windows\S{k}: after a Compact the next folder takes its place
            index.Compact();
            compacted.Add(content.LastCompactedMs);
            follower.RunUntilIdle();
        }
        output.WriteLine($"Compacted handler: {string.Join(", ", compacted.Select(c => c.ToString("F2")))} ms");
        // The worst case for comparison (not a target): a folder removed before every document's file, so every id moves.
        index.Remove(firstDocs);
        index.Compact();
        output.WriteLine($"Compacted handler when every mapped id moves: {content.LastCompactedMs:F2} ms");

        // Settings' estimate counts the same index (from a thread in background mode, as calibration threads are): a
        // writer (the journal) waiting for the read lock meanwhile, timed.
        var waits = new List<double>();
        bool counting = true;
        var writer = new Thread(() =>
        {
            while (Volatile.Read(ref counting))
            {
                var sw = Stopwatch.StartNew();
                index.Lock.EnterWriteLock();
                double ms = sw.Elapsed.TotalMilliseconds;
                index.Lock.ExitWriteLock();
                waits.Add(ms);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        writer.Start();
        ContentCensus census = null!;
        var counter = new Thread(() =>
        {
            ContentNative.EnterBackgroundMode();
            try { census = ContentEstimator.CountIndex(index, Harness.DefaultSettings(), CancellationToken.None); }
            finally { ContentNative.LeaveBackgroundMode(); }
        });
        counter.Start();
        counter.Join();
        Volatile.Write(ref counting, false);
        writer.Join();
        var sortedWaits = waits.Order().ToList();
        output.WriteLine($"CountIndex: {census.Kinds.Values.Sum(k => k.Files):N0} files; a writer waited {sortedWaits.Count} times, " +
            $"median {sortedWaits[sortedWaits.Count / 2]:F2} ms, max {sortedWaits[^1]:F2} ms");
        Assert.Equal(Folders * PerFolder, census.Kinds.Values.Sum(k => k.Files));

        // The U5 targets (§13), timed in Release only (Debug code is several times slower, so it isn't timed at all): no
        // read-lock hold above 20 ms in any reconcile, the first of the session included, and the Compacted handler under
        // 5 ms at 850,000 documents.
        if (Timed)
        {
            Assert.True(firstHolds.Max() <= 20, $"the first reconcile held the read lock {firstHolds.Max():F2} ms");
            for (int run = 0; run < runs.Count; run++)
                Assert.True(runs[run].Max() <= 20, $"reconcile {run + 1} held the read lock {runs[run].Max():F2} ms");
            Assert.True(compacted.Max() < 5, $"Compacted handler {compacted.Max():F2} ms");
            Assert.True(sortedWaits[^1] <= 20, $"a writer waited {sortedWaits[^1]:F2} ms behind CountIndex");
        }
        content.Stop(TimeSpan.FromSeconds(1));
    }

    /// <summary>Holds and the handler are timed against their targets (Release, no debugger).</summary>
    static bool Timed => !IsDebugBuild && !Debugger.IsAttached;

    static string Describe(List<double> holds)
    {
        var sorted = holds.Order().ToList();
        // Where the long ones fall: the first slices of a pass (cold code) or anywhere (another process took the CPU).
        var late = holds.Select((h, i) => (h, i)).Where(p => p.h > 10).Select(p => p.i).Take(8);
        return $"{sorted.Count} holds, median {sorted[sorted.Count / 2]:F2} ms, p95 {sorted[(int)(sorted.Count * 0.95)]:F2} ms, " +
            $"p99 {sorted[(int)(sorted.Count * 0.99)]:F2} ms, max {sorted[^1]:F2} ms (over 10 ms at #{string.Join(", #", late)})";
    }

    /// <summary>Debug builds are slower than the release targets (durations only; holds are timed in Release).</summary>
    static double SlowFactor => Debugger.IsAttached ? 10 : IsDebugBuild ? 4 : 1;

    static bool IsDebugBuild => typeof(ContentIndex).Assembly.GetCustomAttributes(false).OfType<DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled);

    [Fact]
    public void A_stop_in_the_middle_of_the_first_pass_resumes_with_no_duplicates_and_nothing_lost()
    {
        var store = new FakeStore();
        var settings = Harness.DefaultSettings();
        const int N = 1200;
        int firstReads;
        using (var h = new Harness(settings, store, new ContentIndexOptions(CommitAfter: TimeSpan.FromMilliseconds(500))))
        {
            h.Folder(@"C:\Users\Me");
            for (int i = 0; i < N; i++) h.File($@"C:\Users\Me\f{i}.txt", "words " + i);
            h.Start();
            // Run a few iterations only: part of the pass committed, part read but not committed, the rest waiting.
            for (int i = 0; i < 3; i++) { h.Follower.RunOnce(); h.Clock.Advance(600); }
            firstReads = h.Files.Reads;
            Assert.InRange(firstReads, 1, N - 1);
        }
        int committed = store.Reopen().Live().Count;
        output.WriteLine($"first session: read {firstReads}, committed {committed}");
        using (var h = new Harness(settings, store))
        {
            h.Folder(@"C:\Users\Me");
            for (int i = 0; i < N; i++) h.File($@"C:\Users\Me\f{i}.txt", "words " + i);
            h.Start();
            h.Settle();
            var live = store.Live();
            Assert.Equal(N, live.Count);
            Assert.Equal(N, live.Select(d => d.Row.Key).Distinct().Count());
            Assert.Equal(N - committed, h.Files.Reads); // only what wasn't committed is read again
            for (int i = 0; i < N; i += 97) Assert.Equal("words " + i, h.Doc($@"C:\Users\Me\f{i}.txt")!.Body);
        }
    }
}
