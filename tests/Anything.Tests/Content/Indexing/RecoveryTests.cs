using System.Diagnostics;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>Commits that fail, Rebuild (and its delete or reopen failing), a follower that gives up, and a Clear seen through the real root gate.</summary>
public class RecoveryTests
{
    const string Me = @"C:\Users\Me";

    [Fact]
    public void A_store_that_keeps_failing_to_commit_backs_off_and_never_reads_the_same_files_again()
    {
        var store = new FakeStore { CommitFailures = 1_000_000 };
        using var h = new Harness(store: store);
        h.Folder(Me);
        for (int i = 0; i < 50; i++) h.File($@"{Me}\f{i}.txt", "words " + i);
        h.Start();
        h.Pass(10 * 60_000, step: 1000);
        Assert.Equal(50, h.Files.Reads);
        Assert.Empty(store.Live());
        // Tried after 10 s, 1 min and 5 min: a handful of tries in ten minutes, not one per pass.
        Assert.InRange(store.CommitAttempts, 2, 6);
        Assert.Equal(ContentPhase.Paused, h.Content.Status.Phase);
        Assert.Contains("save", h.Content.Status.Reason);
        // The store works again: what was read is saved at the next try, nothing is read again.
        store.CommitFailures = 0;
        h.Pass(16 * 60_000, step: 30_000);
        Assert.Equal(50, store.Live().Count);
        Assert.Equal(50, h.Files.Reads);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        Assert.Null(h.Content.Status.Reason);
    }

    [Fact]
    public void A_crashed_record_whose_commit_fails_is_kept_and_its_file_is_never_opened()
    {
        var dir = Directory.CreateTempSubdirectory("rp-recovery-");
        try
        {
            var store = new FakeStore();
            var settings = Harness.DefaultSettings();
            var options = new ContentIndexOptions { Folder = dir.FullName };
            using (var h = new Harness(settings, store, options))
            {
                h.Folder(Me);
                h.File($@"{Me}\good.txt", "fine");
                h.File($@"{Me}\bad.pdf", "would crash");
                h.Files.OnRead = r =>
                {
                    if (!r.FullPath.EndsWith("bad.pdf", StringComparison.Ordinal)) return null;
                    CrashMarkerTests.CloseWithoutClearing(h.Follower.Marker!); // the process dies inside this file
                    throw new OperationCanceledException();
                };
                h.Start();
                try { h.Settle(); } catch (OperationCanceledException) { }
            }
            store.Reopen().CommitFailures = 1; // the commit holding the Crashed record fails
            using (var h = new Harness(settings, store, options))
            {
                h.Folder(Me);
                h.File($@"{Me}\good.txt", "fine");
                h.File($@"{Me}\bad.pdf", "would crash");
                h.Start();
                h.Pass(30_000);
                Assert.DoesNotContain(h.Files.ReadPaths, p => p.EndsWith("bad.pdf", StringComparison.Ordinal));
                Assert.Equal(DocStatus.Crashed, h.Row($@"{Me}\bad.pdf")!.Value.Status);
            }
            // Recorded: a third start blames nothing more.
            using var marker = CrashMarker.Open(Path.Combine(dir.FullName, "reading"), out var left)!;
            Assert.Empty(left);
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public void Rebuild_empties_the_index_and_reads_everything_once_again()
    {
        using var h = new Harness();
        h.Folder(Me);
        for (int i = 0; i < 5; i++) h.File($@"{Me}\f{i}.txt", "words " + i);
        h.Start();
        h.Settle();
        Assert.Equal(5, h.Files.Reads);
        h.Content.Rebuild();
        h.Settle();
        Assert.Equal(1, h.Store.DeleteAllCount);
        Assert.Equal(10, h.Files.Reads);
        Assert.Equal(5, h.Store.Live().Count);
        Assert.Equal("words 3", h.Doc($@"{Me}\f3.txt")!.Body);
        Assert.Equal(h.Id($@"{Me}\f3.txt"), h.BoundId($@"{Me}\f3.txt"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void A_rebuild_whose_delete_fails_says_so_tries_again_on_a_timer_and_never_goes_back_to_the_old_index()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File($@"{Me}\a.txt", "alpha");
        h.Start();
        h.Settle();
        h.Store.DeleteAllFailures = 5;
        var sw = Stopwatch.StartNew();
        h.Content.Rebuild();
        h.Run();
        Assert.True(sw.ElapsedMilliseconds < 2000, "the delete must not block the follower");
        Assert.Equal(ContentPhase.Preparing, h.Content.Status.Phase);
        Assert.Equal("finishing deleting…", h.Content.Status.Reason);
        Assert.Null(h.Content.AcquireView()); // nothing of the old index is searched meanwhile
        h.Pass(20_000);
        Assert.Equal("finishing deleting…", h.Content.Status.Reason);
        h.Pass(120_000, step: 5000);
        // One delete, then retries of only what it left (never a second DeleteAll, which would also delete newer commits).
        Assert.Equal(1, h.Store.DeleteAllCount);
        Assert.Equal(5, h.Store.FinishDeletingCount);
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("alpha", h.Doc($@"{Me}\a.txt")!.Body);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        Assert.Null(h.Content.Status.Reason);
    }

    [Fact]
    public void A_rebuild_that_cant_open_the_store_again_says_so_and_tries_again()
    {
        using var h = new Harness();
        h.Folder(Me);
        h.File($@"{Me}\a.txt", "words");
        h.Start();
        h.Settle();
        int opens = h.StoreOpens;
        h.FailOpen = n => n > opens && n <= opens + 3; // a scanner holds the folder for three tries
        h.Content.Rebuild();
        h.Pass(3000);
        Assert.Equal(ContentPhase.Stopped, h.Content.Status.Phase);
        Assert.Contains("opened", h.Content.Status.Reason);
        h.Pass(120_000, step: 5000);
        Assert.True(h.StoreOpens >= opens + 4);
        h.File($@"{Me}\b.txt", "new words");
        h.Settle();
        Assert.Equal("words", h.Doc($@"{Me}\a.txt")!.Body);
        Assert.Equal("new words", h.Doc($@"{Me}\b.txt")!.Body);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void A_clear_and_a_partial_rescan_seen_through_the_real_root_gate_delete_and_read_nothing()
    {
        long gateNow = 0;
        using var h = new Harness(gate: names => new RootGate(names, () => gateNow));
        h.Names.Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "scan", RootStatus.Ready, 10, null)];
        var gate = (RootGate)h.RootGate!;
        gate.LoadNow();
        h.Folder("C:");
        h.Folder(Me);
        for (int i = 0; i < 50; i++) h.File($@"{Me}\f{i:00}.txt", "words " + i);
        h.Start();
        h.Settle();
        Assert.Equal(50, h.Store.Live().Count);
        int reads = h.Files.Reads;

        // Tools › Rebuild Index (names) 20 s after the gate counted: Clear, the names scanning, the root rescanned
        // progressively (30 of the 50 files listed so far); the gate's minute isn't up.
        gateNow = 20_000;
        h.Names.State = IndexState.Scanning;
        h.Names.Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "scan", RootStatus.Scanning, 0, null)];
        h.Names.Raise();
        h.Index.Clear();
        h.ForgetIds();
        h.Folder("C:");
        h.Folder(Me);
        for (int i = 0; i < 30; i++) h.Readd($@"{Me}\f{i:00}.txt");
        h.Settle();
        Assert.Equal(50, h.Store.Live().Count);
        Assert.False(gate.StateOf("C:")!.Value.Complete);
        // The rescan finishes.
        for (int i = 30; i < 50; i++) h.Readd($@"{Me}\f{i:00}.txt");
        h.Names.State = IndexState.Ready;
        h.Names.Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "scan", RootStatus.Ready, 52, null)];
        h.Names.Raise();
        gateNow = 90_000;
        gate.LoadNow();
        h.Settle();
        Assert.Equal(50, h.Store.Live().Count);
        Assert.Equal(reads, h.Files.Reads);
        Assert.Equal(h.Id($@"{Me}\f42.txt"), h.BoundId($@"{Me}\f42.txt"));
    }

    [Fact]
    public void A_file_gone_after_a_clear_is_deleted_once_the_names_and_the_gate_are_ready_again()
    {
        long gateNow = 0;
        using var h = new Harness(gate: names => new RootGate(names, () => gateNow));
        h.Names.Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "scan", RootStatus.Ready, 10, null)];
        var gate = (RootGate)h.RootGate!;
        gate.LoadNow();
        h.Folder("C:");
        h.Folder(Me);
        h.File($@"{Me}\keep.txt", "keep");
        h.File($@"{Me}\gone.txt", "gone");
        h.Start();
        h.Settle();
        gateNow = 20_000;
        h.Names.State = IndexState.Scanning;
        h.Names.Raise();
        h.Index.Clear();
        h.ForgetIds();
        h.Files.Remove($@"{Me}\gone.txt");
        h.Folder("C:");
        h.Folder(Me);
        h.Readd($@"{Me}\keep.txt");
        h.Settle();
        Assert.Equal(2, h.Store.Live().Count); // not complete yet: kept
        h.Names.State = IndexState.Ready;
        h.Names.Raise();
        gateNow = 90_000;
        gate.LoadNow();
        h.Settle();
        Assert.Single(h.Store.Live());
        Assert.NotNull(h.Row($@"{Me}\keep.txt"));
    }
}

/// <summary>Threads: a follower that gives up detaches from the name index; a checkpoint never waits for a busy follower to clear the marker.</summary>
[Collection(TimingCollection.Name)]
public class RecoveryThreadTests
{
    static bool WaitFor(Func<bool> condition, int ms = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public async Task A_follower_that_stops_after_errors_comes_off_the_name_index()
    {
        var index = new FileIndex();
        var names = new FakeNames(index);
        int root = index.AddRoot("C:");
        int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
        int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 1000; i++) index.Add($"x{i}.bin", me, FileAttributes.Archive, 1, 1, 0);
        var store = new BrokenRowsStore(new FakeStore());
        // A short back-off between the failures (5 s times the failures in a row by default, waited out in full).
        var content = new ContentIndex(() => store, new FakeFiles(), new FakeGate(), NoPowerPolicy.Instance,
            new ContentIndexOptions { InlineReads = true, VolumeResolver = Harness.Resolve, RecentItems = () => [], FailureBackOffMs = 50 });
        content.Start(names, Harness.DefaultSettings());
        try
        {
            await content.Settled.WaitAsync(TimeSpan.FromSeconds(5));
            var f = content.Follower!;
            Assert.True(WaitFor(() => { f.Wake(); return content.Status.Phase == ContentPhase.Stopped; }), "the follower didn't give up");
            Assert.Contains("error", content.Status.Reason);
            Assert.True(WaitFor(() => index.Content == null), "still attached to the name index");
            for (int i = 0; i < 5; i++) index.Compact();
            Assert.Equal(0, f.QueuedCompacts);
        }
        finally { content.Stop(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public void A_checkpoint_clears_the_marker_at_once_while_the_follower_is_busy()
    {
        var dir = Directory.CreateTempSubdirectory("rp-checkpoint-");
        try
        {
            var index = new FileIndex();
            var names = new FakeNames(index);
            var files = new FakeFiles();
            int root = index.AddRoot("C:");
            int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
            int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
            files.Put(@"C:\Users\Me\big.pdf", 15, 100, "words");
            index.Add("big.pdf", me, FileAttributes.Archive, 15, 100, 0);
            using var reading = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            files.OnRead = _ => { reading.Set(); release.Wait(10_000); return null; };
            var store = new SlowStatusStore(new FakeStore(), reading);
            var content = new ContentIndex(() => store, files, new FakeGate(), NoPowerPolicy.Instance,
                new ContentIndexOptions(IgnorePower: true, IgnoreElevation: true) { Folder = dir.FullName, VolumeResolver = Harness.Resolve, RecentItems = () => [] });
            content.Start(names, Harness.DefaultSettings());
            try
            {
                Assert.True(reading.Wait(10_000));
                Assert.True(store.Entered.Wait(10_000)); // the follower is inside a step that runs long
                Assert.Equal(1, LiveSlots(dir));
                content.Checkpoint(TimeSpan.FromMilliseconds(300));
                Assert.Equal(0, LiveSlots(dir));
                release.Set();
                // Still running: the read finishes and is saved.
                Assert.True(WaitFor(() => store.Inner.Live().Count == 1), "nothing saved after the checkpoint");
            }
            finally
            {
                release.Set();
                content.Stop(TimeSpan.FromSeconds(2));
            }
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    static int LiveSlots(DirectoryInfo dir)
    {
        var bytes = new byte[4096];
        using (var fs = new FileStream(Path.Combine(dir.FullName, "reading"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            fs.ReadExactly(bytes, 0, (int)Math.Min(4096, fs.Length));
        int named = 0;
        for (int i = 0; i < CrashMarker.LiveSlots; i++) if (BitConverter.ToUInt64(bytes, 64 + i * 32) != 0) named++;
        return named;
    }
}

/// <summary>A store whose BytesOnDisk (read by the follower's status) takes 3 s once a read has started: a follower step that runs long.</summary>
internal sealed class SlowStatusStore(FakeStore inner, ManualResetEventSlim afterRead) : IContentStore
{
    int _once;
    public readonly ManualResetEventSlim Entered = new(false);
    public FakeStore Inner => inner;
    public bool IsReadOnly => inner.IsReadOnly;
    public long BytesOnDisk
    {
        get
        {
            if (afterRead.IsSet && Interlocked.Exchange(ref _once, 1) == 0) { Entered.Set(); Thread.Sleep(3000); }
            return inner.BytesOnDisk;
        }
    }
    public IStoreView Acquire() => inner.Acquire();
    public ContentMode RecordedMode => inner.RecordedMode;
    public void RecordMode(ContentMode mode) => inner.RecordMode(mode);
    public ushort VolumeIndex(string volumeKey, string displayRoot) => inner.VolumeIndex(volumeKey, displayRoot);
    public void SeenVolume(ushort volume, DateTime utc) => inner.SeenVolume(volume, utc);
    public IStoreBatch BeginBatch() => inner.BeginBatch();
    public MergePlan? PlanMerge(bool allowLarge) => null;
    public StoreChange Merge(MergePlan plan, CancellationToken ct, Action<CancellationToken>? pace = null) => throw new NotSupportedException();
    public bool DeleteAll() => inner.DeleteAll();
    public bool IsNewerFormat => inner.IsNewerFormat;
    public bool IsUnavailable => inner.IsUnavailable;
    public bool HasDamage => inner.HasDamage;
    public bool Scrub(CancellationToken ct) => inner.Scrub(ct);
    public StoreChange? Rekey(uint segment, IReadOnlyList<(int Doc, DocRow Row, string Path)> moves, bool allowLarge, CancellationToken ct,
        Action<CancellationToken>? pace = null) => inner.Rekey(segment, moves, allowLarge, ct, pace);
    public bool FinishDeleting() => inner.FinishDeleting();
    public void Dispose() => inner.Dispose();
}

/// <summary>A store whose views fail while their rows are read in key order (a damaged segment, a store bug).</summary>
internal sealed class BrokenRowsStore(FakeStore inner) : IContentStore
{
    public bool IsReadOnly => inner.IsReadOnly;
    public long BytesOnDisk => inner.BytesOnDisk;
    public IStoreView Acquire() => new BrokenRowsView(inner.Acquire());
    public ContentMode RecordedMode => inner.RecordedMode;
    public void RecordMode(ContentMode mode) => inner.RecordMode(mode);
    public ushort VolumeIndex(string volumeKey, string displayRoot) => inner.VolumeIndex(volumeKey, displayRoot);
    public void SeenVolume(ushort volume, DateTime utc) => inner.SeenVolume(volume, utc);
    public IStoreBatch BeginBatch() => inner.BeginBatch();
    public MergePlan? PlanMerge(bool allowLarge) => null;
    public StoreChange Merge(MergePlan plan, CancellationToken ct, Action<CancellationToken>? pace = null) => throw new NotSupportedException();
    public bool DeleteAll() => inner.DeleteAll();
    public bool IsNewerFormat => inner.IsNewerFormat;
    public bool IsUnavailable => inner.IsUnavailable;
    public bool HasDamage => inner.HasDamage;
    public bool Scrub(CancellationToken ct) => inner.Scrub(ct);
    public StoreChange? Rekey(uint segment, IReadOnlyList<(int Doc, DocRow Row, string Path)> moves, bool allowLarge, CancellationToken ct,
        Action<CancellationToken>? pace = null) => inner.Rekey(segment, moves, allowLarge, ct, pace);
    public bool FinishDeleting() => inner.FinishDeleting();
    public void Dispose() => inner.Dispose();
}

internal sealed class BrokenRowsView(IStoreView inner) : IStoreView
{
    public long CommitGeneration => inner.CommitGeneration;
    public IReadOnlyList<IContentSegment> Segments => inner.Segments;
    public IReadOnlyList<VolumeEntry> Volumes => inner.Volumes;
    public bool IsDeleted(DocRef doc) => inner.IsDeleted(doc);
    public DocRef? Find(ulong key) => inner.Find(key);
    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey() => throw new InvalidDataException("a damaged segment");
    public IReadOnlyDictionary<ContentKind, KindStats> Stats => inner.Stats;
    public IStoreView AddRef() { inner.AddRef(); return this; }
    public bool TryAddRef() => inner.TryAddRef();
    public void Dispose() => inner.Dispose();
}
