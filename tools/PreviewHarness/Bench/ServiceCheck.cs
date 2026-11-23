using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;

namespace PreviewHarness.Bench;

/// <summary>
/// Checks the thumbnail service's promises about THE OPTION with counts, not times (files made by the corpus builder
/// only, copied fresh under .probe\cold): with no store nothing is written to disk and idle work is refused; the store
/// can be turned on, moved, cleared and turned off while requests are in flight, and every request still gets exactly
/// the answers it asked for (no request lost, no picture handed to another file's key); a store folder is removed only
/// after it is closed. Writes .probe\bench\servicecheck-*.txt and returns 1 when a check fails.
/// </summary>
internal static class ServiceCheck
{
    private sealed class Tally
    {
        public readonly ConcurrentDictionary<ThumbKey, int> Terminal = new();
        public int Low, Final, None, Foreign, WrongKey;
    }

    public static int Run(Corpus corpus, string coldDir, string benchDir, Action<string> log)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string root = Path.Combine(coldDir, "svc-" + stamp);
        string set = Path.Combine(root, "files");
        Directory.CreateDirectory(set);
        var lines = new List<string>();
        int failures = 0;
        void Check(bool ok, string what)
        {
            lines.Add((ok ? "ok    " : "FAIL  ") + what);
            log((ok ? "  ok    " : "  FAIL  ") + what);
            if (!ok) failures++;
        }
        try
        {
            // Made files only: small pictures, screenshots, documents and text, so every route of the ladder is used.
            var rnd = new Random(7);
            var sources = corpus.Files.Where(f => f.Origin == "made" && f.Bytes < 12_000_000 &&
                    (f.Id.StartsWith("made/mix-2mp", StringComparison.Ordinal) || f.Id.StartsWith("made/mix-screen", StringComparison.Ordinal) ||
                     f.Ext is ".pdf" or ".docx" or ".txt" or ".cs" or ".ttf" or ".png" or ".gif" or ".psd" or ".tif"))
                .OrderBy(_ => rnd.Next()).ToList();
            // 240 files, the sources repeated as needed: each copy is a file of its own (another path, another key).
            var files = new List<string>();
            for (int i = 0; i < 240 && sources.Count > 0; i++)
            {
                var src = sources[i % sources.Count];
                string dst = Path.Combine(set, $"s{i:0000}{src.Ext}");
                SourcesBench.CopyFile(corpus.Full(src), dst);
                files.Add(dst);
            }
            var stamps = files.ToDictionary(f => f, f =>
            {
                var fi = new FileInfo(f);
                return new ThumbStamp(fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc());
            });
            log($"servicecheck: {files.Count} made files in {sources.Select(s => s.Ext).Distinct().Count()} kinds");
            var lists = Enumerable.Range(0, 4).Select(k => files.Skip(k * 60).Take(60).ToList()).ToList();
            string storeA = Path.Combine(root, "store-a"), storeB = Path.Combine(root, "store-b");
            string[] Listing() => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            double worstCallMs = 0;
            void Timed(Action a)
            {
                var sw = Stopwatch.StartNew();
                a();
                worstCallMs = Math.Max(worstCallMs, sw.Elapsed.TotalMilliseconds);
            }

            var service = new ThumbnailService(new ThumbServiceOptions { Power = NoPowerPolicy.Instance, Name = "check thumbnails" });
            var tally = new Tally();
            var asked = new ConcurrentDictionary<ThumbKey, string>();
            var client = service.CreateClient(a =>
            {
                try
                {
                    if (!asked.TryGetValue(a.Key, out var path)) { Interlocked.Increment(ref tally.Foreign); return; }
                    if (!string.Equals(path, a.Path, StringComparison.Ordinal) || a.Key != ThumbKey.Of(a.Path, stamps[a.Path], a.DisplaySize))
                        Interlocked.Increment(ref tally.WrongKey);
                    if (a.Quality == Anything.Core.Thumbs.ThumbQuality.Low) { Interlocked.Increment(ref tally.Low); return; }
                    if (a.Quality == Anything.Core.Thumbs.ThumbQuality.Final) Interlocked.Increment(ref tally.Final);
                    else Interlocked.Increment(ref tally.None);
                    tally.Terminal.AddOrUpdate(a.Key, 1, (_, n) => n + 1);
                }
                finally
                {
                    a.Bitmap?.Dispose();
                }
            });
            List<ThumbRequest> Requests(List<string> list) =>
                list.Select((p, i) => new ThumbRequest(p, stamps[p], 160, ThumbPriority.Visible, i)).ToList();
            bool WaitAll(List<ThumbRequest> reqs, int ms)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    if (reqs.All(r => tally.Terminal.ContainsKey(r.Key)) && client.IsIdle) return true;
                    Thread.Sleep(20);
                }
                return false;
            }
            void Answered(List<ThumbRequest> reqs, string what)
            {
                bool all = WaitAll(reqs, 120000);
                int missing = reqs.Count(r => !tally.Terminal.ContainsKey(r.Key));
                int twice = reqs.Count(r => tally.Terminal.TryGetValue(r.Key, out int n) && n > 1);
                Check(all && missing == 0, $"{what}: every one of {reqs.Count} requests answered ({missing} missing)");
                Check(twice == 0, $"{what}: no request answered twice ({twice})");
            }

            // 1. Off: the normal way.
            var before = Listing();
            var r1 = Requests(lists[0]);
            foreach (var r in r1) asked[r.Key] = r.Path;
            Timed(() => client.SubmitViewport(1, r1));
            Answered(r1, "option off");
            Check(!service.KeepsStore && service.CurrentStore == null, "option off: the service reports no store");
            Check(client.SubmitIdle(1, Requests(lists[1])) == 0, "option off: idle work is refused");
            Thread.Sleep(1500);
            var after = Listing();
            Check(before.SequenceEqual(after), $"option off: nothing written to disk under the test folder ({after.Length - before.Length} new entries)");
            Check(!Directory.Exists(storeA) && !Directory.Exists(storeB), "option off: no store folder made");
            var s1 = service.Stats;
            Check(s1.FinalsStored == 0 && s1.NonesStored == 0, $"option off: nothing stored ({s1.FinalsStored} finals, {s1.NonesStored} nones)");

            // 2. Turned on while a list is in flight.
            var r2 = Requests(lists[1]);
            foreach (var r in r2) asked[r.Key] = r.Path;
            Timed(() => client.SubmitViewport(2, r2));
            Thread.Sleep(40);
            Timed(() => service.UseStore(() => ThumbStore.Open(storeA)));
            Answered(r2, "turned on mid-list");
            Check(service.KeepsStore, "turned on: the service keeps a store");
            Check(client.SubmitIdle(2, Requests(lists[0])) > 0, "turned on: idle work is taken");
            WaitAll(Requests(lists[0]), 120000);
            var a2 = service.CurrentStore;
            a2?.WaitUntilWritten(TimeSpan.FromSeconds(5));
            Check(a2 != null && a2.IsEnabled && a2.Count > 0, $"turned on: the store holds pictures ({a2?.Count ?? 0} entries)");

            // 3. Moved while a list is in flight: the old folder is removed only after its store closed.
            var r3 = Requests(lists[2]);
            foreach (var r in r3) asked[r.Key] = r.Path;
            Timed(() => client.SubmitViewport(3, r3));
            Thread.Sleep(40);
            System.Threading.Tasks.Task closedA = null!;
            Timed(() => closedA = service.UseStore(() => ThumbStore.Open(storeB)));
            Check(closedA.Wait(10000), "moved: the old store closed");
            var removedA = ThumbStore.RemoveFolder(storeA);
            Check(removedA == StoreRemoval.Removed, $"moved: the old folder removed ({removedA})");
            Answered(r3, "moved mid-list");

            // 4. Cleared while a list is in flight.
            var r4 = Requests(lists[3]);
            foreach (var r in r4) asked[r.Key] = r.Path;
            Timed(() => client.SubmitViewport(4, r4));
            Thread.Sleep(40);
            Timed(service.ClearStore);
            Answered(r4, "cleared mid-list");

            // 5. Turned off while a list is in flight (the first list again, at a size of the other size class, so it is new work).
            var r5 = lists[0].Select((p, i) => new ThumbRequest(p, stamps[p], 320, ThumbPriority.Visible, i)).ToList();
            foreach (var r in r5) asked[r.Key] = r.Path;
            Timed(() => client.SubmitViewport(5, r5));
            Thread.Sleep(40);
            System.Threading.Tasks.Task closedB = null!;
            Timed(() => closedB = service.UseStore(null));
            Check(closedB.Wait(10000), "turned off: the store closed");
            var removedB = ThumbStore.RemoveFolder(storeB);
            Check(removedB == StoreRemoval.Removed, $"turned off: the store folder removed ({removedB})");
            Answered(r5, "turned off mid-list");
            Check(client.SubmitIdle(5, Requests(lists[1])) == 0, "turned off: idle work is refused again");
            var beforeOff = Listing();
            var r6 = lists[1].Select((p, i) => new ThumbRequest(p, stamps[p], 320, ThumbPriority.Visible, i)).ToList();
            foreach (var r in r6) asked[r.Key] = r.Path;
            client.SubmitViewport(6, r6);
            Answered(r6, "after turning off");
            Thread.Sleep(1500);
            Check(beforeOff.SequenceEqual(Listing()), "after turning off: nothing written to disk");

            Check(tally.Foreign == 0, $"no answer for a key nobody asked for ({tally.Foreign})");
            Check(tally.WrongKey == 0, $"every answer's key belongs to its own file ({tally.WrongKey} mismatches)");
            var st = service.Stats;
            lines.Add($"answers: {tally.Final} final, {tally.None} none, {tally.Low} stand-ins; stored {st.FinalsStored} finals and {st.NonesStored} nones; dropped {st.Dropped}");
            lines.Add($"worst time of a call the grid or Settings makes (SubmitViewport, UseStore, ClearStore): {worstCallMs:0.00} ms (reported, not checked)");
            log("  " + lines[^2]);
            log("  " + lines[^1]);
            client.Dispose();
            service.Dispose();
        }
        catch (Exception e)
        {
            Check(false, "threw " + e);
        }
        finally
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch { Thread.Sleep(500); }
            }
        }
        lines.Add(failures == 0 ? "all checks passed" : $"{failures} checks failed");
        File.WriteAllLines(Path.Combine(benchDir, $"servicecheck-{stamp}.txt"), lines);
        log("servicecheck: " + lines[^1]);
        return failures == 0 ? 0 : 1;
    }
}
