using System.Diagnostics;
using System.Text;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Xunit.Abstractions;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// Review round 2: a Rebuild or "Read problem files again" while the index is in use, a crash marker another copy held,
/// the user's pause across followers, on-demand reads while elevated, renamed folders outside the scope.
/// </summary>
public class FollowerRound2Tests
{
    const string Me = @"C:\Users\Me", A = @"C:\Users\Me\a.txt";

    [Fact]
    public void A_rebuild_while_another_program_holds_the_index_deletes_once_it_can_and_reads_everything_again()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File(A, "alpha");
        h.Start();
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        // Another program has the writer lock: the store held now is read-only, and so is the next one opened.
        h.Store.IsReadOnly = h.Store.IsUnavailable = true;
        h.Store.UnavailableOpens = 1;
        h.Content.Rebuild();
        h.Run();
        Assert.Equal(ContentPhase.Preparing, h.Content.Status.Phase);
        Assert.Contains("finishing deleting", h.Content.Status.Reason);
        Assert.Contains("in use", h.Content.Status.Reason);
        h.Pass(4000);
        Assert.Equal(0, h.Store.DeleteAllCount); // nothing could be deleted yet, and it isn't said to be done
        Assert.Contains("finishing deleting", h.Content.Status.Reason);
        // Free again: deleted, opened again, everything read again.
        h.Pass(60_000, step: 2000);
        Assert.Equal(1, h.Store.DeleteAllCount);
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("alpha", h.Doc(A)!.Body);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        Assert.Null(h.Content.Status.Reason);
    }

    [Fact]
    public void Read_problem_files_again_while_the_index_is_in_use_is_done_once_it_can_be_written()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File(A, "alpha");
        h.Files.OnRead = r => new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.Damaged), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal(DocStatus.Damaged, h.Row(A)!.Value.Status);
        h.Files.OnRead = null;
        h.Store.IsReadOnly = h.Store.IsUnavailable = true; // in use: opened again on a timer
        h.Content.RetryProblems();
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal(DocStatus.Damaged, h.Row(A)!.Value.Status);
        h.Pass(120_000, step: 5000);
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal(DocStatus.Ok, h.Row(A)!.Value.Status);
        Assert.Equal("alpha", h.Doc(A)!.Body);
    }

    [Fact]
    public async Task A_crash_marker_another_copy_held_is_opened_again_and_reading_starts()
    {
        var dir = Directory.CreateTempSubdirectory("rp-marker-");
        string text = Path.Combine(dir.FullName, "window.txt");
        File.WriteAllText(text, "line one\nline two\n");
        try
        {
            var holder = File.Open(Path.Combine(dir.FullName, ContentStore.MarkerName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            using var h = new Harness(options: new ContentIndexOptions { Folder = dir.FullName });
            h.Folder(Me);
            h.File(A, "alpha");
            h.Start();
            h.Settle();
            Assert.Equal(0, h.Files.Reads);
            Assert.Contains("Another copy", h.Content.Status.Reason);
            // Without crash protection nothing is read on demand either.
            Assert.Null(await h.Content.LoadTextWindowAsync(text, 1, 4096, CancellationToken.None));
            holder.Dispose();
            h.Pass(40_000, step: 5000);
            h.Settle();
            Assert.Equal(1, h.Files.Reads);
            Assert.True(h.Follower.Marker!.HasFile);
            Assert.Null(h.Content.Status.Reason);
            Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
            Assert.NotNull(await h.Content.LoadTextWindowAsync(text, 1, 4096, CancellationToken.None));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public void A_pause_until_resumed_holds_for_a_follower_started_later()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File(A, "alpha");
        h.Start();
        h.Settle();
        h.Content.Pause(null);
        var off = h.Settings.Clone();
        off.Mode = ContentMode.NamesOnly;
        h.Content.Apply(off);
        Assert.Null(h.Content.Follower);
        h.Content.Apply(h.Settings); // a new follower
        h.File(@"C:\Users\Me\b.txt", "beta");
        h.Pass(600_000, step: 60_000);
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal("paused by you", h.Content.Status.Reason);
        h.Content.Resume();
        h.Settle();
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void A_pause_asked_while_no_follower_runs_and_a_timed_one_keep_what_is_left()
    {
        var names = Harness.DefaultSettings();
        names.Mode = ContentMode.NamesOnly;
        using var h = new Harness(names);
        h.Folder(Me);
        h.File(A, "alpha");
        h.Start();
        h.Content.Pause(TimeSpan.FromHours(1));
        h.Clock.Advance(TimeSpan.FromMinutes(40));
        var on = names.Clone();
        on.Mode = ContentMode.Documents;
        h.Content.Apply(on);
        h.Pass(15 * 60_000, step: 60_000);
        Assert.Equal(0, h.Files.Reads); // 20 minutes were left
        h.Pass(10 * 60_000, step: 60_000);
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public async Task Running_as_administrator_reads_nothing_on_demand()
    {
        var dir = Directory.CreateTempSubdirectory("rp-elevated-");
        try
        {
            string text = Path.Combine(dir.FullName, "notes.txt"), pdf = Path.Combine(dir.FullName, "invoice.pdf");
            File.WriteAllText(text, "first line\nsecond line\n");
            File.WriteAllBytes(pdf, InvoicePage());
            using var h = new Harness();
            h.Folder(Me);
            h.Start();
            h.Run();
            var highlighter = ContentHighlighter.Create("content:invoice", new SearchOptions())!;
            Assert.Single(await h.Content.LocateOnPdfPageAsync(pdf, 0, highlighter, CancellationToken.None));
            Assert.NotNull(await h.Content.LoadTextWindowAsync(text, 1, 4096, CancellationToken.None));
            h.Names.IsElevated = true;
            Assert.Empty(await h.Content.LocateOnPdfPageAsync(pdf, 0, highlighter, CancellationToken.None));
            Assert.Null(await h.Content.LoadTextWindowAsync(text, 1, 4096, CancellationToken.None));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_offer_is_priced_without_opening_a_file()
    {
        var names = Harness.DefaultSettings();
        names.Mode = ContentMode.NamesOnly;
        names.ModeExplicit = false; // nothing chosen yet: the offer bar's case
        using var h = new Harness(names);
        h.Folder(Me);
        for (int i = 0; i < 5; i++) h.File($@"{Me}\f{i}.txt", "words " + i);
        h.Start();
        Assert.Null(h.Content.Follower);
        var documents = names.Clone();
        documents.Mode = ContentMode.Documents;
        documents.ModeExplicit = true;
        var offer = await h.Content.EstimateWithoutReadingAsync(documents, CancellationToken.None);
        Assert.Equal(5, offer.Documents);
        Assert.True(offer.IndexBytes > 0);
        Assert.Equal(0, h.Files.Reads);
        // Settings and Setup sample real files, once the user looks at the choice.
        await h.Content.EstimateAsync(documents, CancellationToken.None);
        Assert.True(h.Files.Reads > 0);
    }

    /// <summary>One page with "Find invoice here" in Helvetica.</summary>
    static byte[] InvoicePage()
    {
        var w = new W();
        int catalog = w.Reserve(), tree = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int stream = w.AddStream("<< >>", Encoding.ASCII.GetBytes("BT /F1 10 Tf 100 600 Td (Find invoice here) Tj ET"));
        int page = w.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {stream} 0 R >>");
        w.Set(tree, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        w.Root = catalog;
        return w.Write();
    }

    [Fact]
    public void A_folder_renamed_outside_the_scope_costs_no_pass_over_the_names()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File(A, "alpha");
        for (int i = 0; i < 50; i++) h.File($@"D:\Data\Old\f{i}.txt", "out of scope");
        h.File(@"C:\Users\Me\Sub\b.txt", "beta");
        h.Start();
        h.Settle();
        int passes = h.Follower.RenamedFolderPasses;
        h.Rename(@"D:\Data\Old", @"D:\Data\New");
        h.Settle();
        Assert.Equal(passes, h.Follower.RenamedFolderPasses);
        // In the scope, the documents below a renamed folder still follow it.
        h.Rename(@"C:\Users\Me\Sub", @"C:\Users\Me\Moved");
        h.Settle();
        Assert.Equal(passes + 1, h.Follower.RenamedFolderPasses);
        Assert.Equal("beta", h.Doc(@"C:\Users\Me\Moved\b.txt")!.Body);
        Assert.Null(h.Row(@"C:\Users\Me\Sub\b.txt"));
        // Moved out of the scope with a document below it: the document leaves the index.
        h.Rename(@"C:\Users\Me\Moved", @"D:\Data\Moved");
        h.Pass(20_000);
        Assert.Equal(passes + 2, h.Follower.RenamedFolderPasses);
        Assert.Null(h.Row(@"C:\Users\Me\Moved\b.txt"));
        Assert.Equal(2, h.Files.Reads);
    }
}

/// <summary>The 50% duty cycle while the user is active really rests: a file handed over during the rest can't end it.</summary>
[Collection(TimingCollection.Name)]
public class DutyCycleTests
{
    sealed class SleepingSource(int ms) : IContentSource
    {
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            // Thread.Sleep rounds short waits up to the ~15 ms timer tick: spin for short reads so they really take ms.
            if (ms >= 20) Thread.Sleep(ms);
            else { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) Thread.SpinWait(50); }
            return new ContentRead(new ExtractedDoc { Kind = request.Kind, Status = DocStatus.Ok, Body = "w" }, 10, 10, false, false);
        }
        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes) { size = 10; modifiedUtc = 10; attributes = 0; return true; }
    }

    static ReadJob Job(int i) => new()
    {
        FileId = i, Generation = 0, Key = ContentKeys.Of("path:", "f" + i), Volume = 0, RelativePath = "f" + i, RootName = "C:", RootPath = "C:",
        Share = "path:C:", Network = false,
        Request = new ExtractRequest(@"C:\f" + i + ".txt", ContentKind.Text, 10, 10, false, false, long.MaxValue, ExtractLimits.Local),
    };

    /// <summary>Reads n files as the follower does: hands the next one whenever the lane can take it, woken by the scheduler.</summary>
    static long Run(bool active, int n, int ms)
    {
        using var wake = new AutoResetEvent(false);
        using var s = new ReadScheduler(new SleepingSource(ms), CrashMarker.InMemory(), new ReadSchedulerOptions
        {
            UserActive = () => active,
            Wake = () => { try { wake.Set(); } catch (ObjectDisposedException) { } },
        });
        var sw = Stopwatch.StartNew();
        int handed = 0, done = 0;
        while (done < n && sw.ElapsedMilliseconds < 30_000)
        {
            if (handed < n && s.CanStart(network: false) && s.Start(Job(handed))) handed++;
            while (s.Results.TryDequeue(out _)) done++;
            wake.WaitOne(2);
        }
        Assert.Equal(n, done);
        return sw.ElapsedMilliseconds;
    }

    [Fact]
    public void While_the_user_is_active_each_file_is_followed_by_a_rest_as_long_as_it_took()
    {
        const int N = 20, Ms = 40;
        long idle = Run(active: false, N, Ms), active = Run(active: true, N, Ms);
        Assert.True(active >= 1.8 * N * Ms, $"active {active} ms for {N} reads of {Ms} ms (idle {idle} ms)");
        Assert.True(active >= 1.6 * idle, $"active {active} ms, idle {idle} ms");
    }

    [Fact]
    public void Small_files_rest_in_chunks_so_the_timer_tick_does_not_slow_them_tenfold()
    {
        // 1 ms reads: a rest after each one would round up to the ~15 ms timer tick (about 3 s for 200 files).
        const int N = 200, Ms = 1;
        long idle = Run(active: false, N, Ms), active = Run(active: true, N, Ms);
        Assert.True(active < 2_000, $"active {active} ms for {N} reads of {Ms} ms (idle {idle} ms)");
        Assert.True(active > idle, $"active {active} ms, idle {idle} ms: the user's activity must still slow reading");
    }
}

/// <summary>The full reconcile's memory with many files still to read: moved or deleted documents cost no more.</summary>
[Collection(TimingCollection.Name)]
public class ReconcileMemoryTests(ITestOutputHelper output)
{
    const int Unread = 200_000;
    const long Stamp = 133_500_000_000_000_000;

    static DocRow Row(string rel, long size, long modified)
        => new(Harness.Key(@"C:\" + rel), modified, size, 0, 0, ContentKind.Text, DocStatus.Ok, 0, DocFlags.HasText);

    (long Allocated, int Rekeyed, int Queued) Measure(string orphans)
    {
        var store = new FakeStore();
        store.VolumeIndex("path:C:", @"C:\");
        using var h = new Harness(store: store);
        var rows = new List<(DocRow Row, string Rel)>();
        h.Index.BulkAdd(add =>
        {
            int root = add.Add("C:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            int users = add.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
            int me = add.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
            int stored = add.Add("Stored", me, FileAttributes.Directory, -1, 0, 0);
            for (int i = 0; i < 20; i++)
            {
                add.Add($"s{i}.txt", stored, FileAttributes.Archive, 100 + i, Stamp + i, 0);
                rows.Add((Row($@"Users\Me\Stored\s{i}.txt", 100 + i, Stamp + i), $@"Users\Me\Stored\s{i}.txt"));
            }
            int moved = add.Add("New", me, FileAttributes.Directory, -1, 0, 0);
            if (orphans == "moved")
                for (int i = 0; i < 2; i++) add.Add($"m{i}.txt", moved, FileAttributes.Archive, 500 + i, Stamp + 500 + i, 0);
            for (int f = 0; f < Unread / 1000; f++)
            {
                int folder = add.Add("D" + f, me, FileAttributes.Directory, -1, 0, 0);
                for (int i = 0; i < 1000; i++) add.Add($"u{i}.txt", folder, FileAttributes.Archive, 1000 + i, Stamp + 1000 + f * 1000 + i, 0);
            }
        });
        // Read before, and moved (or deleted) while the app was off.
        if (orphans != "none")
            for (int i = 0; i < 2; i++) rows.Add((Row($@"Users\Me\Old\m{i}.txt", 500 + i, Stamp + 500 + i), $@"Users\Me\Old\m{i}.txt"));
        using (var batch = store.BeginBatch())
        {
            foreach (var (row, rel) in rows) batch.Add(row, rel, new StoredDoc("w", [], null));
            batch.Commit();
        }
        h.Start();
        h.Content.Pause(null); // nothing is read: the reconcile alone is measured
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        h.Follower.RunOnce();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, h.Follower.FullReconciles);
        int queued = h.Follower.Waiting - h.Follower.PendingRekeys;
        // The re-keys' copies are committed with the batch (every 5 minutes while files wait).
        h.Pass(6 * 60_000, step: 60_000);
        int rekeyed = Enumerable.Range(0, 2).Count(i => h.Row($@"C:\Users\Me\New\m{i}.txt") is { } row && row.Size == 500 + i);
        return (allocated, rekeyed, queued);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_first_reconcile_of_many_unread_files_costs_no_more_with_moved_or_deleted_documents()
    {
        var none = Measure("none");
        var deleted = Measure("deleted");
        var moved = Measure("moved");
        output.WriteLine($"{Unread:N0} unread: no orphans {none.Allocated / 1e6:F1} MB, two deleted {deleted.Allocated / 1e6:F1} MB, " +
            $"two moved {moved.Allocated / 1e6:F1} MB");
        Assert.Equal(2, moved.Rekeyed); // found and re-keyed, not read again
        Assert.Equal(0, deleted.Rekeyed);
        Assert.True(none.Queued >= Unread, $"{none.Queued} queued");
        Assert.Equal(none.Queued, moved.Queued);
        // About 12 bytes a candidate and 12 a queued file (each grown as it fills), whatever the orphans.
        Assert.True(none.Allocated < Unread * 100L, $"{none.Allocated / 1e6:F1} MB");
        Assert.True(deleted.Allocated - none.Allocated < 2_000_000, $"deleted: +{(deleted.Allocated - none.Allocated) / 1e6:F1} MB");
        Assert.True(moved.Allocated - none.Allocated < 2_000_000, $"moved: +{(moved.Allocated - none.Allocated) / 1e6:F1} MB");
    }
}

/// <summary>Names only, chosen: only the text index's own files are deleted, never a folder of the user's that is called Content.</summary>
public class NamesOnlyDeleteTests
{
    static async Task NamesOnlyStart(string folder)
    {
        var index = (ContentIndex)ContentServices.Create(folder);
        try
        {
            index.Start(new FakeNames(new FileIndex()), new ContentSettings { Mode = ContentMode.NamesOnly, ModeExplicit = true });
            await index.Settled;
        }
        finally { index.Stop(TimeSpan.FromSeconds(1)); }
    }

    static void WriteIndex(string folder)
    {
        using var store = ContentStore.Open(folder);
        using var batch = store.BeginBatch();
        batch.Add(new DocRow(ContentKeys.Of("path:C:", "a.txt"), 1, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 0, DocFlags.HasText), "a.txt",
            new StoredDoc("words", [], null));
        batch.Commit();
    }

    [Fact]
    public async Task A_folder_of_the_users_own_beside_a_moved_database_is_left_alone()
    {
        var dir = Directory.CreateTempSubdirectory("rp-names-");
        try
        {
            string content = Path.Combine(dir.FullName, "Content");
            Directory.CreateDirectory(Path.Combine(content, "Clients"));
            File.WriteAllText(Path.Combine(content, "notes.txt"), "mine");
            File.WriteAllText(Path.Combine(content, "draft.tmp"), "mine too");
            File.WriteAllText(Path.Combine(content, "Clients", "quote.docx"), "a quote");
            Assert.False(ContentServices.HoldsIndex(content));
            await NamesOnlyStart(content);
            Assert.True(File.Exists(Path.Combine(content, "notes.txt")));
            Assert.True(File.Exists(Path.Combine(content, "draft.tmp")));
            Assert.True(File.Exists(Path.Combine(content, "Clients", "quote.docx")));
            Assert.False(File.Exists(Path.Combine(content, "writer.lock")));
            Assert.False(File.Exists(Path.Combine(content, "manifest")));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public async Task An_index_kept_beside_the_users_files_goes_and_the_files_stay()
    {
        var dir = Directory.CreateTempSubdirectory("rp-names-");
        try
        {
            string content = Path.Combine(dir.FullName, "Content");
            Directory.CreateDirectory(Path.Combine(content, "tmp"));
            File.WriteAllText(Path.Combine(content, "notes.txt"), "mine");
            File.WriteAllText(Path.Combine(content, "tmp", "keep.txt"), "mine too");
            WriteIndex(content);
            File.WriteAllText(Path.Combine(content, ContentStore.MarkerName), "marker");
            Assert.True(ContentServices.HoldsIndex(content));
            await NamesOnlyStart(content);
            var left = Directory.EnumerateFileSystemEntries(content, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(content, p)).Order();
            Assert.Equal(["notes.txt", "tmp", @"tmp\keep.txt"], left);
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public async Task An_index_alone_in_its_folder_goes_with_the_folder()
    {
        var dir = Directory.CreateTempSubdirectory("rp-names-");
        try
        {
            string content = Path.Combine(dir.FullName, "Content");
            WriteIndex(content);
            await NamesOnlyStart(content);
            Assert.False(Directory.Exists(content));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public async Task What_an_index_leaves_before_its_first_commit_is_its_own_too()
    {
        var dir = Directory.CreateTempSubdirectory("rp-names-");
        try
        {
            string content = Path.Combine(dir.FullName, "Content"), mine = Path.Combine(dir.FullName, "Mine");
            Directory.CreateDirectory(content);
            CrashMarker.Open(Path.Combine(content, ContentStore.MarkerName), out _)!.Dispose();
            Assert.True(ContentServices.HoldsIndex(content));
            await NamesOnlyStart(content);
            Assert.False(Directory.Exists(content));
            // A file of the user's that happens to be called "reading" isn't a crash marker.
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, ContentStore.MarkerName), "a reading list");
            Assert.False(ContentServices.HoldsIndex(mine));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public void A_writer_sweeps_only_its_own_temp_files()
    {
        var dir = Directory.CreateTempSubdirectory("rp-sweep-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "notes.tmp"), "the user's");
            File.WriteAllText(Path.Combine(dir.FullName, "manifest.tmp"), "half a manifest");
            using (var store = ContentStore.Open(dir.FullName)) Assert.False(store.IsReadOnly);
            Assert.True(File.Exists(Path.Combine(dir.FullName, "notes.tmp")));
            Assert.False(File.Exists(Path.Combine(dir.FullName, "manifest.tmp")));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }
}
