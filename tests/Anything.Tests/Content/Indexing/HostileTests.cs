using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>Sources, stores and managers that misbehave: the follower records nothing wrong and keeps going.</summary>
public class HostileTests
{
    const string A = @"C:\Users\Me\a.txt";

    [Fact]
    public void A_source_that_throws_is_treated_as_temporary_trouble_never_as_a_problem_record()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.File(@"C:\Users\Me\b.txt", "more words");
        h.Files.OnRead = r => r.FullPath == A ? throw new InvalidDataException("a reader bug") : null;
        h.Start();
        h.Settle();
        Assert.Null(h.Row(A));
        Assert.Equal("more words", h.Doc(@"C:\Users\Me\b.txt")!.Body);
        h.Files.OnRead = null;
        h.Pass(75_000, step: 10_000);
        Assert.Equal("words", h.Doc(A)!.Body);
    }

    [Fact]
    public void A_commit_that_fails_is_reported_and_its_words_are_saved_at_the_next_try_without_reading_again()
    {
        var store = new FakeStore { CommitFailures = 1 };
        using var h = new Harness(store: store);
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Start();
        h.Run();
        h.Clock.Advance(3000);
        h.Run();
        Assert.Empty(store.Live());
        Assert.Contains("save", h.Content.Status.Reason ?? "");
        Assert.Equal(ContentPhase.Paused, h.Content.Status.Phase);
        h.Settle();
        Assert.Equal("words", h.Doc(A)!.Body);
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Content.Status.Reason);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void A_store_that_cant_be_opened_is_tried_again_and_commands_wait_for_it()
    {
        int opens = 0;
        var store = new FakeStore();
        var index = new FileIndex();
        var names = new FakeNames(index);
        var files = new FakeFiles();
        var clock = new FakeClock();
        int root = index.AddRoot("C:");
        int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
        int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
        files.Put(A, 15, 100, "words");
        index.Add("a.txt", me, FileAttributes.Archive, 15, 100, 0);
        var content = new ContentIndex(() => ++opens < 3 ? throw new IOException("locked") : store.Reopen(), files, new FakeGate(), NoPowerPolicy.Instance,
            new ContentIndexOptions { ManualLoop = true, Clock = () => clock.Now, VolumeResolver = Harness.Resolve, RecentItems = () => [] });
        content.Start(names, Harness.DefaultSettings());
        content.Pause(null);
        content.Follower!.RunUntilIdle();
        Assert.Equal(ContentPhase.Stopped, content.Status.Phase);
        Assert.Contains("opened", content.Status.Reason);
        content.Apply(Harness.DefaultSettings()); // waits for the store
        content.Resume();
        for (int i = 0; i < 20; i++) { content.Follower.RunUntilIdle(); clock.Advance(1000); }
        Assert.Equal(3, opens);
        Assert.Single(store.Live());
        content.Stop(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void More_than_a_hundred_thousand_touched_ids_below_one_root_take_a_reconcile_of_its_volume_instead()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Start();
        h.Settle();
        int full = h.Follower.FullReconciles, volumes = h.Follower.VolumeReconciles;
        int other = h.Folder(@"C:\Other");
        h.Index.BulkAdd(add =>
        {
            for (int i = 0; i < 100_001; i++) add.Add($"x{i}.dll", other, FileAttributes.Archive, 1, 1, 0);
        });
        h.Settle();
        Assert.Equal(full, h.Follower.FullReconciles);
        Assert.Equal(volumes + 1, h.Follower.VolumeReconciles);
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal(h.Id(A), h.BoundId(A));
    }

    [Fact]
    public void A_kind_switched_off_takes_its_documents_out()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(@"C:\Users\Me\a.pdf", "pdf words");
        h.File(@"C:\Users\Me\b.txt", "text words");
        h.Start();
        h.Settle();
        var noPdf = h.Settings.Clone();
        noPdf.Kinds &= ~ContentKinds.Pdf;
        h.Content.Apply(noPdf);
        h.Settle();
        Assert.Null(h.Row(@"C:\Users\Me\a.pdf"));
        Assert.NotNull(h.Row(@"C:\Users\Me\b.txt"));
    }

    [Fact]
    public void A_file_renamed_while_it_is_read_is_not_stored_under_its_old_name()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        bool renamed = false;
        h.Files.OnRead = r =>
        {
            if (renamed) return null;
            renamed = true;
            h.Rename(A, @"C:\Users\Me\renamed.txt"); // between the dispatch and the result
            return new ContentRead(new ExtractedDoc { Kind = r.Kind, Status = DocStatus.Ok, Body = "words" }, r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        };
        h.Start();
        h.Settle();
        Assert.Null(h.Row(A));
        Assert.Equal("words", h.Doc(@"C:\Users\Me\renamed.txt")!.Body);
        Assert.Equal(h.Id(@"C:\Users\Me\renamed.txt"), h.BoundId(@"C:\Users\Me\renamed.txt"));
    }

    [Fact]
    public void A_compact_and_a_clear_in_quick_succession_lose_nothing()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        for (int i = 0; i < 20; i++) h.File($@"C:\Users\Me\f{i}.txt", "w" + i);
        h.Start();
        h.Settle();
        h.File(@"C:\Other\x.txt");
        h.Index.Remove(h.Id(@"C:\Other\x.txt"));
        h.Compact();
        h.Index.Clear();
        h.ForgetIds();
        h.Folder(@"C:\Users\Me");
        for (int i = 0; i < 20; i++) h.Readd($@"C:\Users\Me\f{i}.txt");
        h.Compact();
        h.Settle();
        Assert.Equal(20, h.Files.Reads);
        Assert.Equal(20, h.Store.Live().Count);
        for (int i = 0; i < 20; i++) Assert.Equal(h.Id($@"C:\Users\Me\f{i}.txt"), h.BoundId($@"C:\Users\Me\f{i}.txt"));
    }
}

public class RootGateTests
{
    [Fact]
    public void Statuses_come_from_GetRoots_by_root_name_and_refresh_on_scanning_ready_edges_at_most_once_a_minute()
    {
        var index = new FileIndex();
        var names = new FakeNames(index)
        {
            State = IndexState.Scanning,
            Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "MFT", RootStatus.Scanning, 10, null)],
        };
        long now = 0;
        using var gate = new RootGate(names, () => now);
        Assert.True(SpinWait.SpinUntil(() => gate.Loads == 1, 5000));
        Assert.Equal(new RootGateState(RootStatus.Scanning, true, false), gate.StateOf("C:"));
        Assert.Null(gate.StateOf("D:"));
        // Every 250 ms tick while scanning raises StateChanged: no count for those.
        for (int i = 0; i < 20; i++) names.Raise();
        Thread.Sleep(50);
        Assert.Equal(1, gate.Loads);
        // Scanning → Ready: a refresh, but not before the minute is up.
        names.Roots = [new IndexedRootInfo(@"C:\", DriveKind.Fixed, false, "MFT", RootStatus.Ready, 10, null)];
        names.State = IndexState.Ready;
        now = 30_000;
        names.Raise();
        Thread.Sleep(50);
        Assert.Equal(1, gate.Loads);
        now = 61_000;
        gate.Refresh();
        Assert.True(SpinWait.SpinUntil(() => gate.Loads == 2, 5000));
        Assert.Equal(new RootGateState(RootStatus.Ready, true, true), gate.StateOf("C:"));
        Assert.Equal(new RootGateState(RootStatus.Ready, true, true), gate.StateOf(@"C:\"));
    }

    [Fact]
    public void The_demo_gate_says_every_root_is_ready_once_the_names_are()
    {
        var names = new FakeNames(new FileIndex()) { State = IndexState.Scanning };
        var gate = new AllRootsReadyGate(names);
        Assert.False(gate.StateOf("anything")!.Value.Complete);
        names.State = IndexState.Ready;
        Assert.Equal(new RootGateState(RootStatus.Ready, true, true), gate.StateOf("anything"));
    }
}
