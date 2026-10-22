using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>The Compacted handler's translation: the same map as DocIdMap's own RemapIds, whichever way it takes.</summary>
public class CompactRemapTests
{
    [Theory]
    [InlineData(0)] // every id moves (a slot removed at the start): every chunk translated
    [InlineData(9_000)] // few move (slots removed near the end): only those set
    [InlineData(20_000)] // none move (slots removed past every mapped id)
    public void Translate_gives_what_RemapIds_gives(int firstRemoved)
    {
        var random = new Random(firstRemoved + 1);
        const int Ids = 20_000;
        var b = DocIdMap.Empty(5).ToBuilder();
        b.AddSegment(1, 9000);
        b.AddSegment(2, 300);
        for (int d = 0; d < 9000; d++) if (random.Next(4) != 0) b.Set(1, d, random.Next(Math.Min(Ids, 9_500)));
        for (int d = 0; d < 300; d++) b.Set(2, d, random.Next(Ids));
        var map = b.Build(5);
        var before = Enumerable.Range(0, 9000).Select(d => map.FileId(1, d)).ToArray();
        // Compact keeps the order: ids before the first removed slot keep their value, the rest move down, some die.
        var remap = new int[Ids];
        int next = 0;
        for (int id = 0; id < Ids; id++) remap[id] = id >= firstRemoved && random.Next(10) == 0 ? -1 : next++;
        var expected = map.ToBuilder();
        expected.RemapIds(remap);
        var want = expected.Build(6);
        var got = CompactRemap.Translate(map, remap, 6);
        Assert.Equal(6, got.Generation);
        foreach (uint seg in (uint[])[1, 2])
            for (int d = 0; d < (seg == 1 ? 9000 : 300); d++) Assert.Equal(want.FileId(seg, d), got.FileId(seg, d));
        Assert.Equal(before, Enumerable.Range(0, 9000).Select(d => map.FileId(1, d)).ToArray()); // the published map is untouched
    }
}

/// <summary>What the app and searches see of the content index: views, maps, sharing, status, the kept index.</summary>
public class FacadeTests
{
    static Harness Ready(Action<Harness>? files = null, ContentSettings? settings = null, ContentIndexOptions? options = null)
    {
        var h = new Harness(settings, options: options);
        h.Folder(@"C:\Users\Me");
        files?.Invoke(h);
        h.Start();
        h.Settle();
        return h;
    }

    [Fact]
    public void A_folder_listing_shares_the_index_with_its_own_ids_mapped_on_demand()
    {
        using var h = Ready(h =>
        {
            h.File(@"C:\Users\Me\Jobs\a.pdf", "alpha");
            h.File(@"C:\Users\Me\Jobs\b.pdf", "beta");
        });
        // A listing tab of C:\Users\Me\Jobs: its own FileIndex with its own ids.
        var listing = new FileIndex();
        int root = listing.AddRoot(@"C:\Users\Me\Jobs");
        int b = listing.Add("b.pdf", root, FileAttributes.Archive, 10, 1, 0);
        int a = listing.Add("a.pdf", root, FileAttributes.Archive, 10, 1, 0);
        ContentServices.ShareWith(h.Index, listing);
        Assert.NotNull(listing.Content);
        using (var view = listing.Content!.AcquireView())
        {
            Assert.NotNull(view);
            listing.Lock.EnterReadLock();
            DocIdMap? map;
            try { map = listing.Content.MapFor(listing, view!); }
            finally { listing.Lock.ExitReadLock(); }
            Assert.NotNull(map);
            var docA = view!.Store.Find(Harness.Key(@"C:\Users\Me\Jobs\a.pdf"))!.Value;
            var docB = view.Store.Find(Harness.Key(@"C:\Users\Me\Jobs\b.pdf"))!.Value;
            Assert.Equal(a, map!.FileId(view.Store.Segments[docA.Segment].Id, docA.Doc));
            Assert.Equal(b, map.FileId(view.Store.Segments[docB.Segment].Id, docB.Doc));
            Assert.Equal(listing.Generation, map.Generation);
            // The same map comes back until the listing changes.
            listing.Lock.EnterReadLock();
            try { Assert.Same(map, listing.Content.MapFor(listing, view)); }
            finally { listing.Lock.ExitReadLock(); }
        }
        // Turning content off in the main window reaches the listing too.
        var off = h.Settings.Clone();
        off.Mode = ContentMode.NamesOnly;
        h.Content.Apply(off);
        Assert.Null(listing.Content!.AcquireView());
        ContentServices.Unshare(listing);
        Assert.Null(listing.Content);
    }

    [Fact]
    public void A_small_listing_is_bound_by_lookups_and_a_change_in_it_patches_its_map()
    {
        // A store much larger than the listing: 2,000 stored documents (one of them the listing's), 20 files listed.
        var store = new FakeStore();
        ushort volume = store.VolumeIndex("path:C:", @"C:\");
        using (var b = store.BeginBatch())
        {
            for (int i = 0; i < 2000; i++)
                b.Add(new DocRow(Harness.Key($@"C:\Archive\x{i}.pdf"), 1, 1, volume, 0, ContentKind.Pdf, DocStatus.Ok, 0, 0), $@"Archive\x{i}.pdf", new StoredDoc("x", [], null));
            b.Add(new DocRow(Harness.Key(@"C:\Users\Me\Jobs\f3.pdf"), 1, 1, volume, 0, ContentKind.Pdf, DocStatus.Ok, 0, 0), @"Users\Me\Jobs\f3.pdf", new StoredDoc("f3", [], null));
            b.Add(new DocRow(Harness.Key(@"C:\Users\Me\Jobs\new.pdf"), 1, 1, volume, 0, ContentKind.Pdf, DocStatus.Ok, 0, 0), @"Users\Me\Jobs\new.pdf", new StoredDoc("new", [], null));
            b.Commit().View.Dispose();
        }
        var listing = new FileIndex();
        int root = listing.AddRoot(@"C:\Users\Me\Jobs");
        for (int i = 0; i < 20; i++) listing.Add($"f{i}.pdf", root, FileAttributes.Archive, 1, 1, 0);
        var maps = new OnDemandMap();
        using var view = new ContentView(store.Acquire(), DocIdMap.Empty(-1), ContentMode.Documents);
        DocIdMap Map()
        {
            listing.Lock.EnterReadLock();
            try { return maps.For(listing, view, _ => ("path:C:", @"Users\Me\Jobs")); }
            finally { listing.Lock.ExitReadLock(); }
        }
        int IdOf(string path)
        {
            var doc = view.Store.Find(Harness.Key(path))!.Value;
            return Map().FileId(view.Store.Segments[doc.Segment].Id, doc.Doc);
        }
        Assert.Equal(4, IdOf(@"C:\Users\Me\Jobs\f3.pdf")); // the root is id 0
        Assert.Equal((1, 0), (maps.Builds, maps.Patches));
        // A download lands in the folder: only that id is mapped (patched), nothing is built again.
        int added = listing.Add("new.pdf", root, FileAttributes.Archive, 1, 1, 0);
        Assert.Equal(added, IdOf(@"C:\Users\Me\Jobs\new.pdf"));
        Assert.Equal((1, 1), (maps.Builds, maps.Patches));
        // f3 deleted: unbound.
        listing.Remove(4);
        Assert.Equal(-1, IdOf(@"C:\Users\Me\Jobs\f3.pdf"));
        Assert.Equal((1, 2), (maps.Builds, maps.Patches));
        // The listing compacts: built again for the new ids.
        listing.Compact();
        Assert.Equal(added - 1, IdOf(@"C:\Users\Me\Jobs\new.pdf"));
        Assert.Equal(2, maps.Builds);
    }

    [Fact]
    public void A_stale_map_after_a_clear_answers_null_until_the_reconcile()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        using var view = h.Content.AcquireView()!;
        h.Index.Lock.EnterReadLock();
        try { Assert.NotNull(h.Content.MapFor(h.Index, view)); }
        finally { h.Index.Lock.ExitReadLock(); }
        h.Index.Clear();
        h.Index.Lock.EnterReadLock();
        try { Assert.Null(h.Content.MapFor(h.Index, view)); }
        finally { h.Index.Lock.ExitReadLock(); }
        Assert.True(h.Content.CatchingUp);
    }

    [Fact]
    public void A_view_taken_before_a_commit_stays_readable_until_released()
    {
        using var h = Ready(h => h.File(@"C:\Users\Me\a.txt", "alpha"));
        var view = h.Content.AcquireView()!;
        h.File(@"C:\Users\Me\b.txt", "beta");
        h.Settle();
        Assert.NotNull(view.Store.Find(Harness.Key(@"C:\Users\Me\a.txt")));
        Assert.Null(view.Store.Find(Harness.Key(@"C:\Users\Me\b.txt"))); // its own commit
        view.Dispose();
        Assert.True(view.IsReleased);
    }

    [Fact]
    public void Status_counts_and_reasons()
    {
        using var h = Ready(h =>
        {
            h.File(@"C:\Users\Me\a.txt", "alpha");
            h.File(@"C:\Users\Me\scan.pdf", "");
            h.File(@"C:\Users\Me\locked.pdf", "x");
            h.File(@"C:\Other\x.txt", "out");
        }, options: null);
        // locked.pdf as password-protected: read it again through a reconcile.
        h.Files.OnRead = r => r.FullPath.EndsWith("locked.pdf", StringComparison.Ordinal)
            ? new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.PasswordProtected), r.ExpectedSize, r.ExpectedModifiedUtc, false, false) : null;
        h.Change(@"C:\Users\Me\locked.pdf", "y");
        h.Pass(30_000);
        var s = h.Content.Status;
        Assert.Equal(ContentPhase.UpToDate, s.Phase);
        Assert.Equal(3, s.Total);
        Assert.Equal(3, s.Done);
        Assert.Equal(1, s.Problems);
        Assert.Equal(1, s.NoText);
        Assert.True(s.Linked);
        var counts = h.Content.CountsByStatus();
        Assert.Equal(1, counts[DocStatus.PasswordProtected]);
        Assert.Equal(1, counts[DocStatus.NoText]);
        Assert.False(counts.ContainsKey(DocStatus.Ok));
        Assert.Equal(DocStatus.PasswordProtected, h.Content.GetStatus(@"C:\Users\Me\locked.pdf"));
        // Read, no words (a scan, a picture): no reason to show in a row that matched by name.
        Assert.Null(h.Content.GetStatus(@"C:\Users\Me\scan.pdf"));
        Assert.Null(h.Content.GetStatus(@"C:\Users\Me\a.txt"));
        Assert.Null(h.Content.GetStatus(@"C:\Other\x.txt")); // out of scope
        Assert.Null(h.Content.GetStatus(@"Q:\nowhere.txt"));
    }

    [Fact]
    public void Reading_shows_progress_while_files_wait()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        for (int i = 0; i < 5; i++) h.File($@"C:\Users\Me\f{i}.txt", "w" + i);
        h.Power.State = PowerState.Unlimited with { OnBattery = true };
        h.Start();
        h.Settle();
        var s = h.Content.Status;
        Assert.Equal(ContentPhase.Paused, s.Phase);
        Assert.Equal("on battery", s.Reason);
        Assert.Equal(5, s.Total);
        Assert.Equal(0, s.Done);
    }

    [Fact]
    public void Before_the_first_reconcile_the_status_is_preparing_and_searches_hear_they_are_catching_up()
    {
        using var h = new Harness();
        h.Names.State = IndexState.Loading;
        h.Folder(@"C:\Users\Me");
        h.File(@"C:\Users\Me\a.txt", "alpha");
        h.Start();
        h.Settle();
        Assert.Equal(ContentPhase.Preparing, h.Content.Status.Phase);
        Assert.True(h.Content.CatchingUp);
        Assert.Equal(0, h.Files.Reads); // waits for the names to load
        h.Names.State = IndexState.Ready;
        h.Settle();
        Assert.False(h.Content.CatchingUp);
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public void An_unset_mode_keeps_an_index_left_on_disk_and_offers_its_mode_back()
    {
        var dir = Directory.CreateTempSubdirectory("rp-kept-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "manifest"), "");
            var store = new FakeStore();
            store.RecordMode(ContentMode.DocumentsAndPictures);
            using (var b = store.BeginBatch())
            {
                b.Add(new DocRow(1, 1, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 0, 0), "a.txt", new StoredDoc("a", [], null));
                b.Commit();
            }
            var unset = Harness.DefaultSettings();
            unset.Mode = ContentMode.NamesOnly;
            unset.ModeExplicit = false;
            using var h = new Harness(unset, store, new ContentIndexOptions { Folder = dir.FullName, OpenReadOnly = () => store.Reopen() });
            h.Start();
            Assert.Null(h.Content.Follower);
            var kept = h.Content.Kept;
            Assert.NotNull(kept);
            Assert.Equal(ContentMode.DocumentsAndPictures, kept!.Value.Mode);
            Assert.Equal(0, store.DeleteAllCount);
            Assert.True(Directory.Exists(dir.FullName));
            // Chosen explicitly: deleted.
            var names = unset.Clone();
            names.ModeExplicit = true;
            h.Content.Apply(names);
            Assert.True(store.DeleteAllCount > 0);
            Assert.False(Directory.Exists(dir.FullName));
            Assert.Null(h.Content.Kept);
        }
        finally { if (dir.Exists) dir.Delete(true); }
    }

    [Fact]
    public async Task Off_means_nothing_is_read_found_or_kept_and_on_demand_reads_say_so()
    {
        var off = Harness.DefaultSettings();
        off.Mode = ContentMode.NamesOnly;
        using var h = new Harness(off);
        h.Folder(@"C:\Users\Me");
        h.File(@"C:\Users\Me\a.txt", "alpha");
        h.Start();
        Assert.Null(h.Content.Follower);
        Assert.Equal(ContentStatus.Off, h.Content.Status);
        Assert.Null(h.Index.Content);
        Assert.Null(h.Content.GetStatus(@"C:\Users\Me\a.txt"));
        Assert.Empty(await h.Content.LocateOnPdfPageAsync(@"C:\Users\Me\a.pdf", 0, null!, CancellationToken.None));
        Assert.Null(await h.Content.LoadTextWindowAsync(@"C:\Users\Me\a.txt", 1, 1000, CancellationToken.None));
        Assert.Equal(0, h.Files.Reads);
        // Turned on later: it starts following.
        h.Content.Apply(Harness.DefaultSettings());
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public void Settings_are_copied_on_entry_so_editing_them_later_changes_nothing()
    {
        var settings = Harness.DefaultSettings();
        using var h = new Harness(settings);
        h.Folder(@"C:\Users\Me");
        h.File(@"C:\Users\Me\a.txt", "alpha");
        h.Start();
        settings.Folders.Clear();
        settings.Mode = ContentMode.NamesOnly;
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal(ContentMode.Documents, h.Follower.Settings.Mode);
    }

    [Fact]
    public async Task An_estimate_prices_the_live_index_with_the_index_statistics_where_it_has_them()
    {
        using var h = Ready(h =>
        {
            for (int i = 0; i < 210; i++) h.File($@"C:\Users\Me\t{i}.txt", "words " + i);
        }, options: new ContentIndexOptions(AllRootsInScope: true));
        var settings = h.Settings.Clone();
        var task = h.Content.EstimateAsync(settings, CancellationToken.None);
        settings.Folders.Add(@"Z:\"); // edited meanwhile: no effect
        var e = await task;
        Assert.Equal(210, e.Documents);
        // FakeView.Stats: 1,000 stored bytes and 100 pairs per document, 10 ms each.
        Assert.Equal(210 * (1000 + 1.37 * 100 + 85) + 10 * 60 * Math.Pow(210 * 100, 0.6), e.IndexBytes, 0);
        Assert.Equal(TimeSpan.FromMilliseconds(1.5 * 210 * 10), e.FirstPass);
    }

    [Fact]
    public void The_services_make_the_real_index_and_the_demo_one()
    {
        Assert.Equal(@"C:\Data\Content", ContentServices.FolderFor(@"C:\Data\RockPreview.db"));
        using var real = ContentServices.Create(Path.Combine(Path.GetTempPath(), "rp-never-started-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(ContentPhase.Off, real.Status.Phase);
        using var demo = ContentServices.CreateInMemory(new FakeFiles(), demo: true);
        Assert.Equal(ContentPhase.Off, demo.Status.Phase);
        Assert.NotNull(ContentServices.Files());
        Assert.False(ContentServices.AttachForSearch(new FileIndex(), Path.Combine(Path.GetTempPath(), "rp-none-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void The_demo_reads_every_root_while_the_names_scan_and_deletes_only_once_they_are_ready()
    {
        var index = new FileIndex();
        var names = new FakeNames(index) { State = IndexState.Scanning };
        int root = index.AddRoot("C:");
        int docs = index.Add("Docs", root, FileAttributes.Directory, -1, 0, 0);
        var files = new FakeFiles();
        files.Put(@"C:\Docs\a.txt", 10, 1, "demo words");
        index.Add("a.txt", docs, FileAttributes.Archive, 10, 1, 0);
        // A document left from before for a file the names don't hold (yet: they are still scanning).
        var store = new FakeStore();
        ushort volume = store.VolumeIndex("path:C:", @"C:\");
        using (var b = store.BeginBatch())
        {
            b.Add(new DocRow(Harness.Key(@"C:\Docs\gone.txt"), 1, 10, volume, 0, ContentKind.Text, DocStatus.Ok, 0, 0), @"Docs\gone.txt", new StoredDoc("old", [], null));
            b.Commit().View.Dispose();
        }
        var clock = new FakeClock();
        var content = new ContentIndex(() => store.Reopen(), files, null, NoPowerPolicy.Instance,
            new ContentIndexOptions(AllRootsInScope: true, IgnorePower: true, IgnoreElevation: true)
            {
                ManualLoop = true, VolumeResolver = Harness.Resolve, Clock = () => clock.Now, RecentItems = () => [],
            });
        content.Start(names, new ContentSettings { Mode = ContentMode.DocumentsAndPictures, ModeExplicit = true });
        void Pass()
        {
            for (int i = 0; i < 5; i++) { content.Follower!.RunUntilIdle(); clock.Advance(1000); }
        }
        Pass();
        Assert.Equal(1, files.Reads); // reads go on while scanning: only deletes wait for complete roots
        Assert.NotNull(store.Live().FirstOrDefault(d => d.Path == @"Docs\gone.txt").Path); // kept while the names scan
        names.State = IndexState.Ready;
        Pass();
        Assert.Null(store.Live().FirstOrDefault(d => d.Path == @"Docs\gone.txt").Path); // complete now: gone
        Assert.Equal("demo words", store.Live().Single().Doc.Body);
        content.Stop(TimeSpan.FromSeconds(1));
    }
}
