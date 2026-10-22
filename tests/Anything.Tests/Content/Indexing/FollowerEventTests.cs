using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>The follower's answers to each kind of change in the name index (§2 "Specific events").</summary>
public class FollowerEventTests
{
    static Harness Ready(Action<Harness>? files = null, ContentSettings? settings = null, FakeStore? store = null, ContentIndexOptions? options = null)
    {
        var h = new Harness(settings, store, options);
        h.Folder("C:");
        h.Folder(@"C:\Users\Me");
        files?.Invoke(h);
        h.Start();
        h.Settle();
        return h;
    }

    [Fact]
    public void Files_in_scope_are_read_once_committed_and_bound()
    {
        using var h = Ready(h =>
        {
            h.File(@"C:\Users\Me\a.txt", "alpha beta");
            h.File(@"C:\Users\Me\Docs\b.pdf", "gamma");
            h.File(@"C:\Other\c.txt", "out of scope");
            h.File(@"C:\Users\Me\AppData\Roaming\x.txt", "denied folder");
            h.File(@"C:\Users\Me\.env", "SECRET=1");
            h.File(@"C:\Users\Me\song.mp3", "not a kind");
        });

        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("alpha beta", h.Doc(@"C:\Users\Me\a.txt")!.Body);
        Assert.Equal(h.Id(@"C:\Users\Me\a.txt"), h.BoundId(@"C:\Users\Me\a.txt"));
        Assert.Equal(h.Id(@"C:\Users\Me\Docs\b.pdf"), h.BoundId(@"C:\Users\Me\Docs\b.pdf"));
        Assert.Null(h.Row(@"C:\Other\c.txt"));
        Assert.Null(h.Row(@"C:\Users\Me\AppData\Roaming\x.txt"));
        Assert.Null(h.Row(@"C:\Users\Me\.env"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        Assert.True(h.Content.Status.Linked);
    }

    [Fact]
    public void The_count_in_scope_follows_files_that_go_between_reconciles()
    {
        using var h = Ready(h =>
        {
            h.File(@"C:\Users\Me\a.txt", "alpha");
            h.File(@"C:\Users\Me\b.txt", "beta");
            h.File(@"C:\Users\Me\c.txt", "gamma");
        });
        Assert.Equal(3, h.Content.Status.Total);
        int reconciles = h.Follower.FullReconciles;

        // A stored document's file deleted: one fewer once its delete is applied.
        h.Delete(@"C:\Users\Me\a.txt");
        h.Settle();
        Assert.Equal(2, h.Content.Status.Total);
        // A file renamed out of scope (no longer a kind that is read): the same.
        h.Rename(@"C:\Users\Me\b.txt", @"C:\Users\Me\b.tmp");
        h.Settle();
        Assert.Equal(1, h.Content.Status.Total);
        // A new file that goes again before it is read, and one renamed out of scope while it settles: never left counted.
        h.File(@"C:\Users\Me\d.txt", "delta");
        h.Pass(1000);
        Assert.Equal(2, h.Content.Status.Total);
        h.Delete(@"C:\Users\Me\d.txt");
        h.File(@"C:\Users\Me\e.txt", "epsilon");
        h.Pass(1000);
        h.Rename(@"C:\Users\Me\e.txt", @"C:\Users\Me\e.part");
        h.Settle();
        Assert.Equal(1, h.Content.Status.Total);
        Assert.Equal(1, h.Content.Status.Done);
        Assert.Equal(reconciles, h.Follower.FullReconciles); // all of it followed, no reconcile counting again
        Assert.Single(h.Store.Live());
    }

    [Fact]
    public void Files_still_settling_leave_the_index_up_to_date_until_they_are_due()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        // A file every 4 s (the demo's trickle, a busy Downloads folder): each settles 5 s before it is read.
        for (int i = 0; i < 4; i++)
        {
            h.File($@"C:\Users\Me\new{i}.txt", "fresh words");
            h.Pass(2000);
            var s = h.Content.Status;
            Assert.Equal(ContentPhase.UpToDate, s.Phase);
            Assert.Equal(s.Total, s.Done);
            h.Pass(2000);
        }
        h.Settle();
        Assert.Equal(5, h.Files.Reads);
        Assert.Equal(5, h.Content.Status.Total);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void A_pass_is_up_to_date_only_once_its_last_batch_is_committed()
    {
        // Measured: the status bar said up to date about 2 s before the last batch was committed, while its documents
        // weren't searchable yet. Read (once settled, 5 s), then committed 2 s after the queue empties.
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
        int commits = h.Follower.Commits;
        h.File(@"C:\Users\Me\b.txt", "beta words");
        h.File(@"C:\Users\Me\c.txt", "gamma words");
        int uncommitted = 0;
        for (int t = 0; t < 12_000 && h.Follower.Commits == commits; t += 100)
        {
            h.Clock.Advance(100);
            h.Run();
            if (h.Files.Reads < 3 || h.Follower.Commits != commits) continue;
            uncommitted++;
            Assert.Null(h.Row(@"C:\Users\Me\b.txt")); // not in the index yet
            Assert.Equal(ContentPhase.Reading, h.Content.Status.Phase);
            Assert.Equal(h.Content.Status.Total, h.Content.Status.Done); // everything is read, though
        }
        Assert.InRange(uncommitted, 15, 25); // about 2 s
        Assert.Equal(commits + 1, h.Follower.Commits);
        Assert.NotNull(h.Row(@"C:\Users\Me\b.txt"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void A_changed_file_is_read_again_after_it_settles()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "one"));
        h.Change(@"C:\Users\Me\a.txt", "two words");
        h.Pass(2000);
        Assert.Equal(1, h.Files.Reads); // still settling (5 s)
        h.Settle();
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("two words", h.Doc(@"C:\Users\Me\a.txt")!.Body);
        Assert.Single(h.Store.Live());
    }

    [Fact]
    public void A_file_that_keeps_changing_is_read_at_most_once_every_ten_minutes()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\log.txt", "v0"));
        for (int i = 1; i <= 5; i++)
        {
            h.Change(@"C:\Users\Me\log.txt", "v" + i);
            h.Settle();
        }
        Assert.Equal(2, h.Files.Reads); // the first read, then one change; the rest wait
        h.Pass(600_000, step: 30_000);
        Assert.Equal(3, h.Files.Reads);
        Assert.Equal("v5", h.Doc(@"C:\Users\Me\log.txt")!.Body);
    }

    [Fact]
    public void A_deleted_file_goes_after_its_grace()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        h.Delete(@"C:\Users\Me\a.txt");
        h.Pass(5000);
        Assert.NotNull(h.Row(@"C:\Users\Me\a.txt")); // 10 s grace
        h.Settle();
        Assert.Null(h.Row(@"C:\Users\Me\a.txt"));
    }

    [Fact]
    public void Safe_save_keeps_the_document_and_reads_the_new_content()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        // Write a temp, delete the original, rename the temp over it: a new id with the old path.
        h.Delete(@"C:\Users\Me\a.txt");
        h.File(@"C:\Users\Me\a.txt", "alpha edited");
        h.Settle();
        Assert.Equal("alpha edited", h.Doc(@"C:\Users\Me\a.txt")!.Body);
        Assert.Equal(h.Id(@"C:\Users\Me\a.txt"), h.BoundId(@"C:\Users\Me\a.txt"));
        Assert.Single(h.Store.Live());
    }

    [Fact]
    public void A_renamed_file_is_rekeyed_from_stored_text_without_a_read()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        h.Rename(@"C:\Users\Me\a.txt", @"C:\Users\Me\b.txt");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row(@"C:\Users\Me\a.txt"));
        Assert.Equal("alpha", h.Doc(@"C:\Users\Me\b.txt")!.Body);
        Assert.Equal(h.Id(@"C:\Users\Me\b.txt"), h.BoundId(@"C:\Users\Me\b.txt"));
    }

    [Fact]
    public void A_renamed_folder_rekeys_everything_below_it_with_no_file_read()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 20; i++) h.File($@"C:\Users\Me\Jobs\job{i}.txt", "job " + i);
            h.File(@"C:\Users\Me\keep.txt", "keep");
        });
        Assert.Equal(21, h.Files.Reads);
        h.Rename(@"C:\Users\Me\Jobs", @"C:\Users\Me\Done");
        h.Settle();
        Assert.Equal(21, h.Files.Reads);
        for (int i = 0; i < 20; i++)
        {
            Assert.Null(h.Row($@"C:\Users\Me\Jobs\job{i}.txt"));
            Assert.Equal("job " + i, h.Doc($@"C:\Users\Me\Done\job{i}.txt")!.Body);
            Assert.Equal(h.Id($@"C:\Users\Me\Done\job{i}.txt"), h.BoundId($@"C:\Users\Me\Done\job{i}.txt"));
        }
        Assert.Equal(21, h.Store.Live().Count);
    }

    [Fact]
    public void A_renamed_folder_whose_commit_rewrites_its_segment_stays_bound()
    {
        // The store rewrites a segment its copies mostly move in the same commit (A4), so the change carries a remap: the
        // copies' new documents must be bound after it (LocalOfAdded), the documents that stayed carried over by it.
        using var h = Ready(h =>
        {
            h.Store.RewriteCopiedSegments = true;
            for (int i = 0; i < 20; i++) h.File($@"C:\Users\Me\Jobs\job{i}.txt", "job " + i);
            for (int i = 0; i < 10; i++) h.File($@"C:\Users\Me\keep{i}.txt", "keep " + i);
        });
        Assert.Equal(30, h.Files.Reads);
        h.Rename(@"C:\Users\Me\Jobs", @"C:\Users\Me\Done");
        h.Settle();
        Assert.True(h.Store.Rewrites > 0);
        Assert.Equal(30, h.Files.Reads);
        for (int i = 0; i < 20; i++)
        {
            string done = $@"C:\Users\Me\Done\job{i}.txt";
            Assert.Equal("job " + i, h.Doc(done)!.Body);
            Assert.Equal(h.Id(done), h.BoundId(done));
        }
        for (int i = 0; i < 10; i++) Assert.Equal(h.Id($@"C:\Users\Me\keep{i}.txt"), h.BoundId($@"C:\Users\Me\keep{i}.txt"));
        Assert.Equal(30, h.Store.Live().Count);
    }

    [Fact]
    public void A_big_rename_rewrites_its_segment_once_instead_of_copying_each_document()
    {
        // Above the threshold (lowered here), a pass moving 30% or more of a segment's live documents goes through
        // IContentStore.Rekey: the segment rewritten with the new keys, the moved files bound, nothing read.
        using var h = Ready(h =>
        {
            for (int i = 0; i < 20; i++) h.File($@"C:\Users\Me\Jobs\job{i}.txt", "job " + i);
            for (int i = 0; i < 10; i++) h.File($@"C:\Users\Me\keep{i}.txt", "keep " + i);
        }, options: new ContentIndexOptions { RekeyInPlaceAbove = 0 });
        int commits = h.Store.CommitCount;
        h.Rename(@"C:\Users\Me\Jobs", @"C:\Users\Me\Done");
        h.Settle();
        Assert.True(h.Store.RekeyCount > 0);
        Assert.Equal(30, h.Files.Reads);
        for (int i = 0; i < 20; i++)
        {
            string done = $@"C:\Users\Me\Done\job{i}.txt";
            Assert.Null(h.Row($@"C:\Users\Me\Jobs\job{i}.txt"));
            Assert.Equal("job " + i, h.Doc(done)!.Body);
            Assert.Equal(h.Id(done), h.BoundId(done));
        }
        for (int i = 0; i < 10; i++) Assert.Equal(h.Id($@"C:\Users\Me\keep{i}.txt"), h.BoundId($@"C:\Users\Me\keep{i}.txt"));
        Assert.Equal(30, h.Store.Live().Count);
    }

    [Fact]
    public void A_big_rename_the_store_declines_goes_through_the_batch_as_copies()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 20; i++) h.File($@"C:\Users\Me\Jobs\job{i}.txt", "job " + i);
        }, options: new ContentIndexOptions { RekeyInPlaceAbove = 0 });
        h.Store.RekeyDeclines = true;
        h.Rename(@"C:\Users\Me\Jobs", @"C:\Users\Me\Done");
        h.Settle();
        Assert.Equal(0, h.Store.RekeyCount);
        Assert.Equal(20, h.Files.Reads);
        for (int i = 0; i < 20; i++)
        {
            string done = $@"C:\Users\Me\Done\job{i}.txt";
            Assert.Equal("job " + i, h.Doc(done)!.Body);
            Assert.Equal(h.Id(done), h.BoundId(done));
        }
        Assert.Equal(20, h.Store.Live().Count);
    }

    [Fact]
    public void A_folder_moved_out_of_scope_takes_its_documents_out()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\Jobs\a.txt", "alpha"));
        h.Rename(@"C:\Users\Me\Jobs", @"C:\Other\Jobs");
        h.Settle();
        Assert.Empty(h.Store.Live());
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public void A_whole_root_rescan_with_new_ids_reads_nothing_again()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 30; i++) h.File($@"C:\Users\Me\f{i}.txt", "file " + i);
        });
        Assert.Equal(30, h.Files.Reads);
        // A rescan adds a new copy of the root and removes the old one: every file gets a new id.
        int oldRoot = h.Id("C:");
        h.ForgetIds();
        h.Index.Remove(oldRoot);
        h.Folder("C:");
        h.Folder(@"C:\Users\Me");
        for (int i = 0; i < 30; i++) h.Readd($@"C:\Users\Me\f{i}.txt");
        h.Settle();
        Assert.True(h.Files.Reads <= 30 + 0, $"read again: {h.Files.Reads - 30}");
        for (int i = 0; i < 30; i++) Assert.Equal(h.Id($@"C:\Users\Me\f{i}.txt"), h.BoundId($@"C:\Users\Me\f{i}.txt"));
        Assert.Equal(30, h.Store.Live().Count);
    }

    [Fact]
    public void Clear_never_deletes_and_rebinds_by_key_once_the_root_is_back()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 10; i++) h.File($@"C:\Users\Me\f{i}.txt", "file " + i);
        });
        h.Index.Clear();
        h.ForgetIds();
        h.Gate.Set("C:", RootStatus.Scanning);
        h.Settle();
        Assert.Equal(10, h.Store.Live().Count);
        Assert.True(h.Content.CatchingUpForTests() || h.BoundId(@"C:\Users\Me\f0.txt") == -1);
        // The rescan brings the same files back under new ids; same stamps.
        h.Folder("C:");
        h.Folder(@"C:\Users\Me");
        for (int i = 0; i < 10; i++) h.Readd($@"C:\Users\Me\f{i}.txt");
        h.Gate.Set("C:", RootStatus.Ready);
        h.Settle();
        Assert.Equal(10, h.Files.Reads);
        Assert.Equal(10, h.Store.Live().Count);
        Assert.Equal(h.Id(@"C:\Users\Me\f3.txt"), h.BoundId(@"C:\Users\Me\f3.txt"));
    }
}

public class FollowerEventTests2
{
    static Harness Ready(Action<Harness>? files = null, ContentSettings? settings = null, FakeStore? store = null, ContentIndexOptions? options = null)
    {
        var h = new Harness(settings, store, options);
        h.Folder("C:");
        h.Folder(@"C:\Users\Me");
        files?.Invoke(h);
        h.Start();
        h.Settle();
        return h;
    }

    [Fact]
    public void Compact_translates_the_bindings_and_reads_nothing()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 10; i++) h.File($@"C:\Users\Me\f{i}.txt", "file " + i);
            h.File(@"C:\Other\x.txt");
        });
        h.Index.Remove(h.Id(@"C:\Other\x.txt"));
        h.Compact();
        // Searches see the translated map at once (the handler swapped it in), before the follower runs.
        Assert.Equal(h.Id(@"C:\Users\Me\f9.txt"), h.BoundId(@"C:\Users\Me\f9.txt"));
        h.Settle();
        Assert.Equal(10, h.Files.Reads);
        for (int i = 0; i < 10; i++) Assert.Equal(h.Id($@"C:\Users\Me\f{i}.txt"), h.BoundId($@"C:\Users\Me\f{i}.txt"));
    }

    [Fact]
    public void A_batch_caught_by_a_compact_is_translated()
    {
        using var h = Ready(options: new ContentIndexOptions(CommitAfter: TimeSpan.FromHours(1)));
        h.File(@"C:\Other\dead.txt");
        h.Index.Remove(h.Id(@"C:\Other\dead.txt"));
        h.File(@"C:\Users\Me\new.txt", "fresh words");
        h.Pass(6000);
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row(@"C:\Users\Me\new.txt")); // read, still in the batch
        int before = h.Id(@"C:\Users\Me\new.txt");
        h.Compact();
        Assert.NotEqual(before, h.Id(@"C:\Users\Me\new.txt"));
        h.Pass(3_600_000, step: 60_000);
        Assert.Equal("fresh words", h.Doc(@"C:\Users\Me\new.txt")!.Body);
        Assert.Equal(h.Id(@"C:\Users\Me\new.txt"), h.BoundId(@"C:\Users\Me\new.txt"));
    }

    [Fact]
    public void An_offline_share_is_neither_read_nor_deleted_and_catches_up_when_back()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"\\nas\jobs");
        using var h = Ready(h =>
        {
            h.Folder(@"\\nas\jobs");
            h.File(@"\\nas\jobs\a.pdf", "print job");
        }, settings);
        Assert.Equal("print job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
        h.Gate.Set(@"\\nas\jobs", RootStatus.Offline);
        h.Files.OfflineRoots.Add(@"\\nas\jobs");
        h.File(@"\\nas\jobs\b.pdf", "second job");
        h.Index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true); // a full reconcile while it's away
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.NotNull(h.Row(@"\\nas\jobs\a.pdf"));
        h.Gate.Set(@"\\nas\jobs", RootStatus.Ready);
        h.Files.OfflineRoots.Clear();
        h.Pass(400_000, step: 20_000);
        Assert.Equal("second job", h.Doc(@"\\nas\jobs\b.pdf")!.Body);
    }

    [Fact]
    public void A_read_that_finds_the_share_gone_backs_it_off_and_records_nothing()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"\\nas\jobs");
        using var h = Ready(h => h.Folder(@"\\nas\jobs"), settings);
        h.Files.OfflineRoots.Add(@"\\nas\jobs"); // the gate still says Ready: the read itself finds out
        h.File(@"\\nas\jobs\a.pdf", "job");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row(@"\\nas\jobs\a.pdf"));
        h.Pass(60_000, step: 10_000);
        Assert.Equal(1, h.Files.Reads); // backed off for 5 minutes
        h.Files.OfflineRoots.Clear();
        h.Pass(300_000, step: 20_000);
        Assert.Equal("job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
    }

    [Fact]
    public void The_mass_delete_guard_waits_ten_minutes_and_deletes_only_what_is_still_gone()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            for (int i = 0; i < 1500; i++) h.File($@"E:\f{i}.txt", "f" + i);
        }, settings);
        Assert.Equal(1500, h.Store.Live().Count);
        // The stick's root is rescanned and lists only 10 of its files (a mount that briefly lists almost nothing; an
        // empty listing never deletes at all, since a root without entries isn't trusted).
        h.Gate.Set("E:", RootStatus.Scanning);
        h.Run();
        h.Index.Remove(h.Id("E:"));
        foreach (var p in h.Under("E:").Append("E:").ToList()) h.Forget(p);
        h.Folder("E:");
        for (int i = 0; i < 10; i++) h.Readd($@"E:\f{i}.txt");
        h.Gate.Set("E:", RootStatus.Ready);
        h.Settle();
        Assert.Equal(1500, h.Store.Live().Count);
        Assert.Equal(1, h.Follower.GuardedVolumes);
        // Most come back; ten minutes later only the ones still gone are deleted.
        for (int i = 10; i < 750; i++) h.Readd($@"E:\f{i}.txt");
        h.Pass(700_000, step: 20_000);
        Assert.Equal(750, h.Store.Live().Count);
        Assert.Equal(0, h.Follower.GuardedVolumes);
        Assert.Equal(1500, h.Files.Reads);
    }

    [Fact]
    public void An_unknown_date_is_stated_before_anything_is_read()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        var (size, modified, _, _) = h.Files.Get(@"C:\Users\Me\a.txt");
        // A USN-fallback root: the entry comes back with size and date unknown.
        h.Index.Update(h.Id(@"C:\Users\Me\a.txt"), size: 0, modifiedFileTimeUtc: 0);
        int stats = h.Files.Stats;
        h.Settle();
        Assert.True(h.Files.Stats > stats);
        Assert.Equal(1, h.Files.Reads);
        // The disk says it changed: then it is read.
        h.Files.Put(@"C:\Users\Me\a.txt", size + 1, modified + 1, "alpha changed");
        h.Index.Update(h.Id(@"C:\Users\Me\a.txt"), size: 0, modifiedFileTimeUtc: 0);
        h.Settle();
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("alpha changed", h.Doc(@"C:\Users\Me\a.txt")!.Body);
    }

    [Fact]
    public void An_unplugged_stick_keeps_its_words_and_a_replug_reads_nothing()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            for (int i = 0; i < 5; i++) h.File($@"E:\f{i}.txt", "f" + i);
        }, settings);
        h.Index.Remove(h.Id("E:"));
        foreach (var p in h.Under("E:").Append("E:").ToList()) h.Forget(p);
        h.Gate.Set("E:", (RootGateState?)null);
        h.Index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true); // and a full reconcile meanwhile
        h.Pass(60_000, step: 5000);
        Assert.Equal(5, h.Store.Live().Count);
        h.Folder("E:");
        h.Gate.Set("E:", RootStatus.Ready);
        for (int i = 0; i < 5; i++) h.Readd($@"E:\f{i}.txt");
        h.Settle();
        Assert.Equal(5, h.Files.Reads);
        Assert.Equal(h.Id(@"E:\f2.txt"), h.BoundId(@"E:\f2.txt"));
    }

    [Fact]
    public void A_big_rescan_of_one_root_reconciles_only_its_volume_and_reads_nothing()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            for (int i = 0; i < 300; i++) h.File($@"E:\f{i}.txt", "f" + i);
            for (int i = 0; i < 5; i++) h.File($@"C:\Users\Me\c{i}.txt", "c" + i);
        }, settings);
        h.Follower.MaxTouched = 200; // the rescan below touches about 600 ids
        int reads = h.Files.Reads, full = h.Follower.FullReconciles, volumes = h.Follower.VolumeReconciles;
        h.Index.Remove(h.Id("E:"));
        foreach (var p in h.Under("E:").Append("E:").ToList()) h.Forget(p);
        h.Folder("E:");
        for (int i = 0; i < 300; i++) h.Readd($@"E:\f{i}.txt");
        h.Settle();
        Assert.Equal(full, h.Follower.FullReconciles);
        Assert.Equal(volumes + 1, h.Follower.VolumeReconciles);
        Assert.Equal(reads, h.Files.Reads);
        Assert.Equal(305, h.Store.Live().Count);
        Assert.Equal(h.Id(@"E:\f123.txt"), h.BoundId(@"E:\f123.txt"));
        Assert.Equal(h.Id(@"C:\Users\Me\c3.txt"), h.BoundId(@"C:\Users\Me\c3.txt"));
    }

    [Fact]
    public void A_big_step_that_isnt_one_roots_rescan_takes_a_full_reconcile()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            for (int i = 0; i < 300; i++) h.File($@"E:\f{i}.txt", "f" + i);
            h.File(@"C:\Users\Me\c.txt", "c");
        }, settings);
        h.Follower.MaxTouched = 200;
        int full = h.Follower.FullReconciles;
        h.Index.Remove(h.Id("E:"));
        foreach (var p in h.Under("E:").Append("E:").ToList()) h.Forget(p);
        h.Folder("E:");
        for (int i = 0; i < 300; i++) h.Readd($@"E:\f{i}.txt");
        h.Change(@"C:\Users\Me\c.txt", "c changed"); // the same step: not explained by the rescan
        h.Settle();
        Assert.Equal(full + 1, h.Follower.FullReconciles);
        Assert.Equal("c changed", h.Doc(@"C:\Users\Me\c.txt")!.Body);
        Assert.Equal(h.Id(@"E:\f7.txt"), h.BoundId(@"E:\f7.txt"));
    }

    [Fact]
    public void A_move_across_volumes_is_rekeyed_from_the_one_matching_orphan()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            h.File(@"C:\Users\Me\report.docx", "quarterly report");
        }, settings);
        var (size, modified, body, _) = h.Files.Get(@"C:\Users\Me\report.docx");
        h.File(@"E:\report.docx", body, size, modified); // copy, then delete the original
        h.Delete(@"C:\Users\Me\report.docx");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row(@"C:\Users\Me\report.docx"));
        Assert.Equal("quarterly report", h.Doc(@"E:\report.docx")!.Body);
        Assert.Equal(h.Id(@"E:\report.docx"), h.BoundId(@"E:\report.docx"));
    }

    [Fact]
    public void A_file_deleted_while_the_app_was_off_goes_once_its_scanned_root_is_complete()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        var store = new FakeStore();
        using (var first = Ready(h =>
        {
            h.Folder("E:");
            h.File(@"E:\keep.txt", "keep");
            h.File(@"E:\gone.txt", "gone");
        }, settings, store))
        {
            Assert.Equal(2, store.Live().Count);
        }
        // Next start: the database still lists both, the startup rescan of the stick runs, gone.txt isn't there any more.
        using var h = new Harness(settings, store);
        h.Gate.Set("E:", RootStatus.Scanning);
        h.Folder("E:");
        h.File(@"E:\keep.txt", "keep");
        h.Start();
        h.Settle();
        Assert.Equal(2, store.Live().Count); // the root isn't complete: the orphan is kept
        h.Gate.Set("E:", RootStatus.Ready);
        h.Settle();
        Assert.Single(store.Live());
        Assert.Equal("keep", h.Doc(@"E:\keep.txt")!.Body);
    }

    [Fact]
    public void A_volume_unseen_for_ninety_days_is_purged()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"E:\");
        using var h = Ready(h =>
        {
            h.Folder("E:");
            h.File(@"E:\old.txt", "old stick");
        }, settings);
        h.Index.Remove(h.Id("E:"));
        foreach (var p in h.Under("E:").Append("E:").ToList()) h.Forget(p);
        h.Gate.Set("E:", (RootGateState?)null);
        h.Settle();
        Assert.Single(h.Store.Live());
        ushort volume = h.Row(@"E:\old.txt")!.Value.Volume;
        h.Store.SetLastSeen(volume, h.Clock.Now.AddDays(-91));
        h.File(@"C:\Users\Me\a.txt", "a commit brings the store's volume table along");
        h.Settle();
        h.Index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true);
        h.Settle();
        Assert.Null(h.Row(@"E:\old.txt"));
        Assert.NotNull(h.Row(@"C:\Users\Me\a.txt"));
    }

    [Fact]
    public void A_folder_taken_out_of_the_scope_loses_its_documents_at_once_even_offline()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders.Add(@"\\nas\jobs");
        using var h = Ready(h =>
        {
            h.Folder(@"\\nas\jobs");
            h.File(@"\\nas\jobs\a.pdf", "print job");
            h.File(@"C:\Users\Me\b.txt", "mine");
        }, settings);
        h.Gate.Set(@"\\nas\jobs", RootStatus.Offline);
        h.Files.OfflineRoots.Add(@"\\nas\jobs");
        var changed = settings.Clone();
        changed.Folders.Remove(@"\\nas\jobs");
        h.Content.Apply(changed);
        h.Settle();
        Assert.Null(h.Row(@"\\nas\jobs\a.pdf"));
        Assert.NotNull(h.Row(@"C:\Users\Me\b.txt"));
    }

    [Fact]
    public void Pictures_enter_with_their_mode_and_leave_with_it()
    {
        using var h = Ready(h =>
        {
            h.File(@"C:\Users\Me\photo.jpg", "");
            h.File(@"C:\Users\Me\a.txt", "alpha");
        });
        Assert.Null(h.Row(@"C:\Users\Me\photo.jpg"));
        var pictures = h.Settings.Clone();
        pictures.Mode = ContentMode.DocumentsAndPictures;
        h.Content.Apply(pictures);
        h.Settle();
        Assert.Equal(DocStatus.NoText, h.Row(@"C:\Users\Me\photo.jpg")!.Value.Status);
        // Settings says "1 document and 1 picture": the pictures are counted apart once up to date.
        var s = h.Content.Status;
        Assert.Equal((ContentPhase.UpToDate, 2, true, 1), (s.Phase, s.Total, s.WithPictures, s.Pictures));
        h.Content.Apply(h.Settings.Clone());
        h.Settle();
        Assert.Null(h.Row(@"C:\Users\Me\photo.jpg"));
        Assert.NotNull(h.Row(@"C:\Users\Me\a.txt"));
        s = h.Content.Status;
        Assert.Equal((ContentPhase.UpToDate, 1, false, 0), (s.Phase, s.Total, s.WithPictures, s.Pictures));
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void Names_only_chosen_stops_and_deletes_the_index()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        Assert.Same(h.Content, h.Index.Content);
        var off = h.Settings.Clone();
        off.Mode = ContentMode.NamesOnly;
        h.Content.Apply(off);
        Assert.Null(h.Content.Follower);
        Assert.Null(h.Index.Content);
        Assert.Equal(ContentPhase.Off, h.Content.Status.Phase);
    }

    [Fact]
    public void A_manager_whose_GetRoots_is_empty_still_reads_and_binds_but_never_deletes_orphans()
    {
        var index = new FileIndex();
        var names = new FakeNames(index);
        var store = new FakeStore();
        var files = new FakeFiles();
        var clock = new FakeClock();
        using var gate = new RootGate(names, () => (long)(clock.Now - DateTime.UnixEpoch).TotalMilliseconds);
        var content = new ContentIndex(() => store.Reopen(), files, gate, NoPowerPolicy.Instance, new ContentIndexOptions
        {
            ManualLoop = true, Clock = () => clock.Now, VolumeResolver = Harness.Resolve,
        });
        int root = index.AddRoot("C:");
        int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
        int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
        files.Put(@"C:\Users\Me\a.txt", 15, 100, "alpha");
        int a = index.Add("a.txt", me, FileAttributes.Archive, 15, 100, 0);
        content.Start(names, Harness.DefaultSettings());
        for (int i = 0; i < 15; i++) { content.Follower!.RunUntilIdle(); clock.Advance(1000); }
        Assert.Equal(1, files.Reads);
        index.Remove(a);
        for (int i = 0; i < 15; i++) { content.Follower!.RunUntilIdle(); clock.Advance(1000); }
        // A real delete under a live root still goes (pending delete, grace); orphans of a reconcile would be kept.
        Assert.Empty(store.Live());
        content.Stop(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Renames that land together (chains, swaps, a shifted series), renames with edits, and scope bounces within the grace.</summary>
public class FollowerRenameTests
{
    const string Me = @"C:\Users\Me";

    static Harness Ready(Action<Harness> files, ContentIndexOptions? options = null)
    {
        var h = new Harness(options: options);
        h.Folder("C:");
        h.Folder(Me);
        files(h);
        h.Start();
        h.Settle();
        return h;
    }

    static void Later(Harness h)
    {
        h.Settle();
        h.Pass(120_000, step: 10_000);
    }

    [Fact]
    public void Rename_c_to_d_then_a_to_c_in_one_step_keeps_each_text_with_its_file()
    {
        using var h = Ready(h =>
        {
            h.File($@"{Me}\a.txt", "alpha");
            h.File($@"{Me}\c.txt", "charlie");
        });
        int reads = h.Files.Reads;
        h.Rename($@"{Me}\c.txt", $@"{Me}\d.txt");
        h.Rename($@"{Me}\a.txt", $@"{Me}\c.txt");
        Later(h);
        Assert.Equal("alpha", h.Doc($@"{Me}\c.txt")!.Body);
        Assert.Equal("charlie", h.Doc($@"{Me}\d.txt")!.Body);
        Assert.Null(h.Row($@"{Me}\a.txt"));
        Assert.Equal(2, h.Store.Live().Count);
        Assert.Equal(reads, h.Files.Reads);
        Assert.Equal(h.Id($@"{Me}\c.txt"), h.BoundId($@"{Me}\c.txt"));
        Assert.Equal(h.Id($@"{Me}\d.txt"), h.BoundId($@"{Me}\d.txt"));
    }

    [Fact]
    public void A_swap_through_a_temporary_name_swaps_the_texts()
    {
        using var h = Ready(h =>
        {
            h.File($@"{Me}\a.txt", "alpha");
            h.File($@"{Me}\b.txt", "bravo");
        });
        int reads = h.Files.Reads;
        h.Rename($@"{Me}\a.txt", $@"{Me}\t.txt");
        h.Rename($@"{Me}\b.txt", $@"{Me}\a.txt");
        h.Rename($@"{Me}\t.txt", $@"{Me}\b.txt");
        Later(h);
        Assert.Equal("bravo", h.Doc($@"{Me}\a.txt")!.Body);
        Assert.Equal("alpha", h.Doc($@"{Me}\b.txt")!.Body);
        Assert.Null(h.Row($@"{Me}\t.txt"));
        Assert.Equal(2, h.Store.Live().Count);
        Assert.Equal(reads, h.Files.Reads);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(700)] // more than one pass takes at once: the chain pulls the rest in
    public void Shifting_a_numbered_series_by_one_keeps_every_page_with_its_file(int pages)
    {
        using var h = Ready(h =>
        {
            for (int i = 1; i <= pages; i++) h.File($@"{Me}\p{i}.txt", "page" + i + "words");
        });
        int reads = h.Files.Reads;
        // A page inserted at the front: p(n) → p(n+1), last first, as a batch renamer does.
        for (int i = pages; i >= 1; i--) h.Rename($@"{Me}\p{i}.txt", $@"{Me}\p{i + 1}.txt");
        Later(h);
        Assert.Null(h.Row($@"{Me}\p1.txt"));
        for (int i = 2; i <= pages + 1; i++)
        {
            Assert.Equal("page" + (i - 1) + "words", h.Doc($@"{Me}\p{i}.txt")?.Body);
            Assert.Equal(h.Id($@"{Me}\p{i}.txt"), h.BoundId($@"{Me}\p{i}.txt"));
        }
        Assert.Equal(pages, h.Store.Live().Count);
        Assert.Equal(reads, h.Files.Reads);
    }

    [Fact]
    public void A_file_renamed_and_edited_in_one_step_is_read_again()
    {
        using var h = Ready(h => h.File($@"{Me}\a.txt", "alpha"));
        h.Rename($@"{Me}\a.txt", $@"{Me}\b.txt");
        h.Change($@"{Me}\b.txt", "beta edited");
        Later(h);
        Assert.Equal("beta edited", h.Doc($@"{Me}\b.txt")!.Body);
        Assert.Null(h.Row($@"{Me}\a.txt"));
        Assert.Single(h.Store.Live());
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void A_file_renamed_on_battery_and_edited_before_the_charger_is_back_is_read_again()
    {
        using var h = Ready(h => h.File($@"{Me}\a.txt", "alpha"));
        h.Power.State = PowerState.Unlimited with { OnBattery = true };
        h.Rename($@"{Me}\a.txt", $@"{Me}\b.txt");
        h.Settle();
        h.Change($@"{Me}\b.txt", "edited on battery");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        h.Power.State = PowerState.Unlimited;
        Later(h);
        Assert.Equal("edited on battery", h.Doc($@"{Me}\b.txt")!.Body);
        Assert.Null(h.Row($@"{Me}\a.txt"));
        Assert.Single(h.Store.Live());
    }

    [Fact]
    public void A_new_file_under_the_old_name_of_a_renamed_one_is_read_and_the_renamed_one_keeps_its_text()
    {
        using var h = Ready(h => h.File($@"{Me}\c.txt", "charlie"));
        h.Rename($@"{Me}\c.txt", $@"{Me}\d.txt");
        h.File($@"{Me}\c.txt", "a new c");
        Later(h);
        Assert.Equal("charlie", h.Doc($@"{Me}\d.txt")!.Body);
        Assert.Equal("a new c", h.Doc($@"{Me}\c.txt")!.Body);
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal(h.Id($@"{Me}\c.txt"), h.BoundId($@"{Me}\c.txt"));
        Assert.Equal(h.Id($@"{Me}\d.txt"), h.BoundId($@"{Me}\d.txt"));
    }

    [Fact]
    public void A_file_renamed_out_of_scope_and_back_within_the_grace_keeps_its_document()
    {
        using var h = Ready(h => h.File($@"{Me}\a.txt", "alpha"));
        h.Rename($@"{Me}\a.txt", $@"{Me}\a.txt.bak"); // .bak isn't read: out of scope
        h.Pass(3000);
        h.Rename($@"{Me}\a.txt.bak", $@"{Me}\a.txt");
        Later(h);
        Assert.Equal("alpha", h.Doc($@"{Me}\a.txt")!.Body);
        Assert.Equal(h.Id($@"{Me}\a.txt"), h.BoundId($@"{Me}\a.txt"));
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal(0, h.Follower.PendingDeletes);
    }

    [Fact]
    public void A_folder_moved_out_of_scope_and_back_within_the_grace_keeps_its_documents()
    {
        using var h = Ready(h =>
        {
            h.File($@"{Me}\Jobs\a.txt", "alpha");
            h.File($@"{Me}\Jobs\b.txt", "bravo");
        });
        h.Rename($@"{Me}\Jobs", @"C:\Other\Jobs");
        h.Pass(3000);
        h.Rename(@"C:\Other\Jobs", $@"{Me}\Jobs");
        Later(h);
        Assert.Equal("alpha", h.Doc($@"{Me}\Jobs\a.txt")!.Body);
        Assert.Equal("bravo", h.Doc($@"{Me}\Jobs\b.txt")!.Body);
        Assert.Equal(h.Id($@"{Me}\Jobs\b.txt"), h.BoundId($@"{Me}\Jobs\b.txt"));
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void Live_ids_are_handled_before_dead_ones_so_a_new_id_takes_over_words_still_waiting_for_the_old_one()
    {
        // The words are read but not committed yet (an hour's batch) when the entry is replaced by a new id with the same
        // path and stamp (a rescan's new copy, a re-add): handled dead-first, the waiting words would be dropped with
        // the dead id and the file read again.
        using var h = Ready(_ => { }, new ContentIndexOptions(CommitAfter: TimeSpan.FromHours(1)));
        h.File($@"{Me}\a.txt", "alpha");
        h.Pass(6000);
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row($@"{Me}\a.txt")); // read, still waiting in the batch
        var (size, modified, body, _) = h.Files.Get($@"{Me}\a.txt");
        int old = h.Id($@"{Me}\a.txt");
        h.Index.Remove(old);
        int fresh = h.File($@"{Me}\a.txt", body, size, modified);
        h.Clock.Advance(2000);
        h.Run(); // one step sees both ids
        Assert.Equal(0, h.Follower.PendingDeletes);
        h.Pass(3_600_000, step: 60_000);
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal("alpha", h.Doc($@"{Me}\a.txt")!.Body);
        Assert.Equal(fresh, h.BoundId($@"{Me}\a.txt"));
    }
}

internal static class ContentIndexTestExtensions
{
    public static bool CatchingUpForTests(this ContentIndex index) => index.CatchingUp;
}
