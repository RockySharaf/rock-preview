using System.Collections.Concurrent;
using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

[Collection("Store memory")]
public class ViewLifetimeTests
{
    static SynthDoc Doc(ulong key, string body) => new(Synth.Row(key, ContentKind.Text), $"Docs\\{key}.txt", new StoredDoc(body, [], null));

    [Fact]
    public void Merged_segments_stay_readable_until_the_last_view_goes_then_their_files_are_deleted()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        Synth.Commit(store, [Doc(1, "alpha one"), Doc(2, "beta two")]).View.Dispose();
        Synth.Commit(store, [Doc(3, "gamma three")]).View.Dispose();

        var old = store.Acquire();
        var oldIds = old.Segments.Select(s => s.Id).ToList();
        var merged = store.Merge(new MergePlan(oldIds, 3, false), CancellationToken.None);
        merged.View.Dispose();

        // The old view still reads both merged-away segments: rows, text, terms, paths.
        Assert.Equal(2, old.Segments.Count);
        Assert.Equal("alpha one", old.Segments[0].ReadStored(0)!.Body);
        Assert.True(old.Segments[1].TryGetTerm("gamma"u8, out _));
        Assert.Equal("Docs\\2.txt", old.Segments[0].ReadPath(1));
        foreach (uint id in oldIds) Assert.True(File.Exists(temp.File(SegmentFormat.FileName(id)))); // manifest.prev still lists them

        // One more commit: neither manifest lists them now, but the old view holds them, so the files stay.
        Synth.Commit(store, [Doc(4, "delta four")]).View.Dispose();
        foreach (uint id in oldIds) Assert.True(File.Exists(temp.File(SegmentFormat.FileName(id))) || IsPosixDeleted(temp, id));
        Assert.Equal("gamma three", old.Segments[1].ReadStored(0)!.Body);

        var oldSegments = old.Segments.Cast<SegmentReader>().ToList();
        old.Dispose();
        foreach (uint id in oldIds) Assert.False(File.Exists(temp.File(SegmentFormat.FileName(id))), $"segment {id} is still there");
        Assert.True(oldSegments[0].IsReleased);
        Assert.Throws<ObjectDisposedException>(() => oldSegments[0].Row(0));
        Assert.Throws<ObjectDisposedException>(() => old.Segments); // a released lease answers nothing
    }

    static bool IsPosixDeleted(TempFolder temp, uint id) => !File.Exists(temp.File(SegmentFormat.FileName(id)));

    [Fact]
    public void A_view_taken_before_a_commit_keeps_its_own_documents_and_deletions()
    {
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [Doc(1, "one"), Doc(2, "two")]).View.Dispose();
        using var before = store.Acquire();
        Synth.Commit(store, [Doc(3, "three")], [new DocRef(0, 0)]).View.Dispose();
        using var after = store.Acquire();
        Assert.False(before.IsDeleted(new DocRef(0, 0)));
        Assert.True(after.IsDeleted(new DocRef(0, 0)));
        Assert.Single(before.Segments);
        Assert.Equal(2, after.Segments.Count);
        Assert.NotEqual(before.CommitGeneration, after.CommitGeneration);
        Assert.Equal([1UL, 2UL], before.RowsByKey().Select(r => r.Row.Key));
        Assert.Equal([2UL, 3UL], after.RowsByKey().Select(r => r.Row.Key));
    }

    [Fact]
    public void Each_holder_has_its_own_lease_that_counts_up_and_down_and_refuses_after_the_last()
    {
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [Doc(1, "one")]).View.Dispose();
        var view = store.Acquire();
        Assert.True(view.TryAddRef()); // one more on this lease
        var other = view.AddRef();     // a lease of its own
        Assert.NotSame(view, other);
        view.Dispose();
        view.Dispose();
        Assert.False(view.TryAddRef());
        Assert.Throws<ObjectDisposedException>(() => view.AddRef());
        Assert.Throws<ObjectDisposedException>(() => view.Segments);
        view.Dispose(); // once too often: harmless
        Synth.Commit(store, [Doc(2, "two")]).View.Dispose(); // the store lets go of that view; the other lease keeps it
        Assert.Equal("one", other.Segments[0].ReadStored(0)!.Body);
        other.Dispose();
        // The segment is still in the current view, so it stays readable there.
        using var now = store.Acquire();
        Assert.Equal("one", now.Segments[0].ReadStored(0)!.Body);
    }

    [Fact]
    public async Task A_lease_disposed_too_often_releases_nothing_of_the_store_or_other_holders()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        var first = Synth.Commit(store, [Doc(1, "alpha one"), Doc(2, "beta two")]);
        first.View.Dispose();
        first.View.Dispose(); // a commit's view too
        var innocent = store.Acquire();
        var careless = store.Acquire();
        for (int i = 0; i < 5; i++) careless.Dispose();
        var segment = (SegmentReader)innocent.Segments[0];
        Assert.False(segment.IsReleased);
        Assert.Equal("alpha one", segment.ReadStored(0)!.Body);

        // The store still hands out views, begins batches, commits and closes: nothing spins or waits.
        var work = Task.Run(() =>
        {
            using (var v = store.Acquire()) Assert.Equal(2, Synth.Live(v).Count);
            using (var batch = store.BeginBatch()) batch.Commit().View.Dispose();
            Synth.Commit(store, [Doc(3, "gamma three")]).View.Dispose();
            using (var v = store.Acquire()) Assert.Equal(3, Synth.Live(v).Count);
        });
        await work.WaitAsync(TimeSpan.FromSeconds(20)); // throws TimeoutException when the store hangs
        Assert.Equal("beta two", innocent.Segments[0].ReadStored(1)!.Body);
        innocent.Dispose();
        await Task.Run(store.Dispose).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Two_stores_never_share_a_segment_number_or_a_generation()
    {
        using var a = ContentStore.Open(null);
        using var b = ContentStore.Open(null);
        var ca = Synth.Commit(a, [Doc(1, "secret words")]);
        var cb = Synth.Commit(b, [Doc(1, "public words")]);
        uint ida = ca.NewSegment!.Value, idb = cb.NewSegment!.Value;
        long ga = ca.View.CommitGeneration, gb = cb.View.CommitGeneration;
        ca.View.Dispose();
        cb.View.Dispose();
        Assert.NotEqual(ida, idb);
        Assert.NotEqual(ga, gb);

        // A store made after both, in memory or in a new folder, counts on past them.
        using var temp = new TempFolder();
        foreach (var c in new[] { ContentStore.Open(null), ContentStore.Open(temp.Path) })
            using (c)
            {
                using (var empty = c.Acquire()) Assert.True(empty.CommitGeneration > Math.Max(ga, gb));
                var change = Synth.Commit(c, [Doc(1, "other words")]);
                Assert.True(change.NewSegment > Math.Max(ida, idb));
                change.View.Dispose();
            }
    }

    [Fact]
    public void A_brand_new_folder_counts_from_the_clock()
    {
        long seconds = (long)(DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        var change = Synth.Commit(store, [Doc(1, "x")]);
        Assert.True(change.NewSegment >= seconds); // a folder deleted and made again doesn't take earlier file names
        Assert.True(change.View.CommitGeneration >= seconds);
        change.View.Dispose();
    }

    [Fact]
    public void A_store_opened_again_after_delete_all_counts_on_from_the_empty_manifest()
    {
        using var temp = new TempFolder();
        uint highest;
        long generation;
        using (var store = ContentStore.Open(temp.Path))
        {
            store.VolumeIndex("unc:\\\\NAS\\JOBS", "\\\\nas\\jobs");
            for (int i = 0; i < 3; i++) Synth.Commit(store, [Doc((ulong)i + 1, "words")]).View.Dispose();
            using (var v = store.Acquire()) highest = v.Segments.Max(s => s.Id);
            Assert.True(store.DeleteAll());
            generation = store.Generation;
        }
        // DeleteAll leaves an empty manifest that keeps the counters: no segments, volumes or statistics.
        string path = temp.File("manifest");
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(path), out var m));
        Assert.Empty(m!.Segments);
        Assert.Empty(m.Volumes);
        Assert.Empty(m.Stats);
        Assert.True(m.NextSegmentId > highest);
        Assert.Equal(generation, m.Generation);
        Assert.Equal(["manifest"], temp.Names());

        // The persisted counters win when they are ahead of this process (another session counted further).
        uint next = m.NextSegmentId + 1_000_000;
        long later = m.Generation + 1_000_000;
        File.WriteAllBytes(path, new Manifest { Generation = later, NextSegmentId = next }.Serialize());
        using (var store = ContentStore.Open(temp.Path))
        {
            using (var v = store.Acquire())
            {
                Assert.Empty(v.Segments);
                Assert.True(v.CommitGeneration >= later);
            }
            var change = Synth.Commit(store, [Doc(9, "after")]);
            Assert.True(change.NewSegment >= next);
            Assert.True(change.View.CommitGeneration > later);
            change.View.Dispose();
        }
    }

    [Fact]
    public void Disposing_the_store_releases_only_its_own_reference()
    {
        using var temp = new TempFolder();
        var store = ContentStore.Open(temp.Path);
        Synth.Commit(store, [Doc(1, "kept alive by a search")]).View.Dispose();
        var lease = store.Acquire();
        uint id = lease.Segments[0].Id;
        store.Dispose();
        Assert.Equal("kept alive by a search", lease.Segments[0].ReadStored(0)!.Body);
        using (var empty = store.Acquire()) Assert.Empty(empty.Segments);
        Assert.False(File.Exists(temp.File("writer.lock")));
        lease.Dispose();
        Assert.True(File.Exists(temp.File(SegmentFormat.FileName(id)))); // committed: never deleted by a release
    }

    [Fact]
    public void Delete_all_stops_publishing_waits_for_leases_and_deletes_every_file()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        Synth.Commit(store, Synth.Docs(100, seed: 1)).View.Dispose();
        Synth.Commit(store, Synth.Docs(100, seed: 2)).View.Dispose();
        var lease = store.Acquire();
        uint lastId = lease.Segments.Max(s => s.Id);

        var saved = ContentStore.LeaseWait;
        ContentStore.LeaseWait = TimeSpan.FromMilliseconds(100);
        bool clean;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            clean = store.DeleteAll();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            ContentStore.LeaseWait = saved;
        }
        using (var empty = store.Acquire()) Assert.Empty(empty.Segments);
        // The lease still reads (the mapping stays until it is released), whatever the file system did with the names.
        Assert.NotNull(lease.Segments[0].ReadStored(0));
        lease.Dispose();
        Assert.True(clean || store.FinishDeleting());
        Assert.DoesNotContain(temp.Names(), n => n.StartsWith("seg-") || n == "manifest.prev" || n.EndsWith(".tmp"));
        Assert.Equal(Manifest.ParseResult.Ok, Manifest.Parse(File.ReadAllBytes(temp.File("manifest")), out var kept)); // keeps the counters
        Assert.Empty(kept!.Segments);

        // The store goes on, empty, and never reuses a segment number.
        var change = Synth.Commit(store, [Doc(7, "after a rebuild")]);
        Assert.True(change.NewSegment > lastId);
        change.View.Dispose();
        using var reopened = ContentStore.Open(temp.Path, readOnly: true);
        using var view = reopened.Acquire();
        Assert.Single(Synth.Live(view));
    }

    [Fact]
    public void A_lease_that_outlives_its_store_never_deletes_a_later_stores_file()
    {
        using var temp = new TempFolder();
        var first = ContentStore.Open(temp.Path);
        Synth.Commit(first, [Doc(1, "from the first store")]).View.Dispose();
        var lease = first.Acquire();
        uint oldId = lease.Segments[0].Id;
        string oldPath = temp.File(SegmentFormat.FileName(oldId));
        var saved = ContentStore.LeaseWait;
        ContentStore.LeaseWait = TimeSpan.FromMilliseconds(50);
        using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read)) // a scanner holds it: DeleteAll can't delete it
        {
            try { Assert.False(first.DeleteAll()); }
            finally { ContentStore.LeaseWait = saved; }
        }
        first.Dispose();
        File.Delete(oldPath); // gone later, by someone else (or by the store's last try on Dispose)

        // Turned back on: a new store in the same folder counts on, so it never takes the old file's name.
        using var second = ContentStore.Open(temp.Path);
        var change = Synth.Commit(second, [Doc(2, "from the second store")]);
        change.View.Dispose();
        Assert.True(change.NewSegment > oldId);
        // A file that turns up under the old name anyway (the folder made again by hand, a restored backup) isn't the
        // lease's to delete: the release checks it is still the file the segment was read from.
        File.Copy(temp.File(SegmentFormat.FileName(change.NewSegment!.Value)), oldPath);
        lease.Dispose();
        Assert.True(File.Exists(oldPath));
        using var reopened = ContentStore.Open(temp.Path, readOnly: true);
        using var view = reopened.Acquire();
        Assert.Equal([2UL], view.RowsByKey().Select(r => r.Row.Key));
    }

    [Fact]
    public void Finishing_a_delete_retries_only_what_was_left_and_keeps_later_commits()
    {
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        Synth.Commit(store, [Doc(1, "old words")]).View.Dispose();
        uint oldId;
        using (var v = store.Acquire()) oldId = v.Segments[0].Id;
        string oldPath = temp.File(SegmentFormat.FileName(oldId));
        var saved = ContentStore.LeaseWait;
        ContentStore.LeaseWait = TimeSpan.FromMilliseconds(50);
        var scanner = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read); // can't be deleted while held
        try
        {
            Assert.False(store.DeleteAll());
            Assert.True(store.DeletesPending);
            // "Rebuild text index": reading starts again and commits before the follower's timer retries.
            Synth.Commit(store, [Doc(2, "read again")]).View.Dispose();
            Assert.False(store.FinishDeleting()); // still held
            using (var v = store.Acquire()) Assert.Equal([2UL], v.RowsByKey().Select(r => r.Row.Key));
        }
        finally
        {
            scanner.Dispose();
            ContentStore.LeaseWait = saved;
        }
        Assert.True(store.FinishDeleting());
        Assert.False(store.DeletesPending);
        Assert.False(File.Exists(oldPath));
        using (var v = store.Acquire()) Assert.Equal([2UL], v.RowsByKey().Select(r => r.Row.Key)); // the new commit survived
        using var reopened = ContentStore.Open(temp.Path, readOnly: true);
        using var rv = reopened.Acquire();
        Assert.Equal([2UL], rv.RowsByKey().Select(r => r.Row.Key));
    }

    [Fact]
    public void Leases_never_read_a_released_segment_while_the_writer_commits_and_merges()
    {
        long before = Interlocked.Read(ref SegmentReader.AccessesAfterRelease);
        using var store = ContentStore.Open(null);
        Synth.Commit(store, [Doc(1, "seed document")]).View.Dispose();
        Run(store, publishes: 10_000, readers: 8);
        Assert.Equal(before, Interlocked.Read(ref SegmentReader.AccessesAfterRelease));
    }

    [Fact]
    public void Mapped_leases_survive_commits_merges_and_file_deletes()
    {
        long before = Interlocked.Read(ref SegmentReader.AccessesAfterRelease);
        using var temp = new TempFolder();
        using var store = ContentStore.Open(temp.Path);
        Synth.Commit(store, [Doc(1, "seed document")]).View.Dispose();
        Run(store, publishes: 150, readers: 8); // each commit flushes two files to disk (about 40 ms here)
        Assert.Equal(before, Interlocked.Read(ref SegmentReader.AccessesAfterRelease));
        // Everything not in either manifest is gone once the leases are.
        var listed = new HashSet<string>();
        using (var view = store.Acquire()) foreach (var s in view.Segments) listed.Add(SegmentFormat.FileName(s.Id));
        Assert.True(temp.Names().Count(n => n.StartsWith("seg-")) <= listed.Count + 16);
    }

    /// <summary>
    /// Eight threads acquire views and read every kind of data while the writer publishes (adds, deletes, and every merge the
    /// policy plans); every read must succeed on a live segment.
    /// </summary>
    static void Run(ContentStore store, int publishes, int readers)
    {
        var errors = new ConcurrentQueue<Exception>();
        int leases = 0, leasesWhilePublishing = 0, publishing = 0;
        using var started = new CountdownEvent(readers);
        using var stop = new CancellationTokenSource();
        var threads = Enumerable.Range(0, readers).Select(r => new Thread(() =>
        {
            var rnd = new Random(r);
            bool first = true;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var view = store.Acquire();
                    Interlocked.Increment(ref leases);
                    if (Volatile.Read(ref publishing) == 1) Interlocked.Increment(ref leasesWhilePublishing);
                    foreach (var seg in view.Segments)
                    {
                        int d = rnd.Next(seg.DocCount);
                        var row = seg.Row(d);
                        if (seg.Find(row.Key) != d) throw new InvalidOperationException("Find disagrees with Row");
                        _ = seg.ReadStored(d) ?? throw new InvalidOperationException("text didn't decode");
                        _ = seg.ReadPath(d) ?? throw new InvalidOperationException("path didn't decode");
                        if (seg.TryGetTerm("document"u8, out var p)) Synth.DocsOf(seg, p);
                        seg.Terms("a"u8, "c"u8, (ReadOnlySpan<byte> _, in TermPostings _) => true, CancellationToken.None);
                        _ = seg.ReadStoredRaw(d);
                    }
                    _ = view.RowsByKey().Take(20).Count();
                    if (view.Segments.Any(s => ((SegmentReader)s).IsReleased)) throw new InvalidOperationException("a held segment was released");
                }
                catch (Exception e)
                {
                    errors.Enqueue(e);
                }
                if (first)
                {
                    first = false;
                    started.Signal();
                }
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)), "the readers never started");

        Volatile.Write(ref publishing, 1);
        var rnd = new Random(99);
        ulong next = 100;
        var liveKeys = new List<ulong> { 1 };
        for (int i = 0; i < publishes; i++)
        {
            var plan = store.PlanMerge(allowLarge: true);
            if (plan != null && i % 3 == 0)
            {
                store.Merge(plan, CancellationToken.None).View.Dispose();
                continue;
            }
            using var view = store.Acquire();
            using var batch = store.BeginBatch();
            int adds = rnd.Next(1, 4);
            for (int a = 0; a < adds; a++)
            {
                ulong key = next++;
                batch.Add(Synth.Row(key, ContentKind.Text), $"Docs\\{key}.txt", new StoredDoc($"document {key} words alpha beta", [], null));
                liveKeys.Add(key);
            }
            if (liveKeys.Count > 20 && rnd.Next(2) == 0)
            {
                int k = rnd.Next(liveKeys.Count);
                if (view.Find(liveKeys[k]) is { } doc)
                {
                    batch.Delete(doc);
                    liveKeys.RemoveAt(k);
                }
            }
            batch.Commit().View.Dispose();
        }
        Volatile.Write(ref publishing, 0);
        stop.Cancel();
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(20))));
        Assert.True(errors.IsEmpty, errors.FirstOrDefault()?.ToString());
        Assert.True(leasesWhilePublishing > 0, "no lease was taken while the writer published");
        using var final = store.Acquire();
        Assert.Equal(liveKeys.Order(), final.RowsByKey().Select(r => r.Row.Key));
    }
}
