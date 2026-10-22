using System.Diagnostics;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// The follower and the facade: windows hear when the index catches up without a commit, a follower that gave up can be
/// started again, an unplugged drive keeps its documents across restarts, files marked "don't index contents" or
/// encrypted lose their words at once, a folder taken out of the scope shows no text, an elevated app reads nothing to
/// estimate, estimates of a name index still scanning say "at least", and the census never samples stale ids.
/// Some wait on real threads and back-off times, so they run with the other timing tests, not beside the whole suite.
/// </summary>
[Collection(TimingCollection.Name)]
public class FollowerReviewTests
{
    const string Me = @"C:\Users\Me";

    static bool WaitFor(Func<bool> condition, int ms = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    static string[] Paths(int n) => [.. Enumerable.Range(0, n).Select(i => $@"{Me}\f{i}.txt")];

    /// <summary>A first session that reads and commits the files, then stops: the store it leaves is up to date.</summary>
    static (FakeStore Store, Dictionary<string, (long Size, long Modified, string Body)> Files) UpToDateStore(string[] paths, ContentSettings? settings = null,
        ContentIndexOptions? options = null, Action<Harness>? more = null)
    {
        var first = new Harness(settings, options: options);
        first.Folder(Me);
        foreach (var p in paths) first.File(p, "alpha words " + p.Length);
        more?.Invoke(first);
        first.Start();
        first.Settle();
        Assert.Equal(paths.Length, first.Store.Live().Count);
        var files = first.Files;
        var stamps = new Dictionary<string, (long, long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) { var f = files.Get(p); stamps[p] = (f.Size, f.Modified, f.Body); }
        first.Dispose();
        return (first.Store, stamps);
    }

    [Fact]
    public void A_second_session_on_an_index_already_up_to_date_tells_windows_once_it_links_without_reading()
    {
        var paths = Paths(3);
        var (store, stamps) = UpToDateStore(paths);
        using var h = new Harness(store: store);
        h.Folder(Me);
        foreach (var p in paths) h.File(p, stamps[p].Body, stamps[p].Size, stamps[p].Modified);
        int changed = 0;
        h.Content.ContentChanged += () => Interlocked.Increment(ref changed);
        h.Start();
        // A search now reports ContentPending (no view yet, catching up): the window shows names only meanwhile.
        Assert.True(h.Content.CatchingUp);
        h.Run();
        Assert.False(h.Content.CatchingUp);
        Assert.Equal(0, h.Files.Reads); // bound by key: nothing read, nothing committed
        Assert.True(WaitFor(() => Volatile.Read(ref changed) > 0, 5000), "linking raised no ContentChanged");

        // The name index is cleared (Rebuild, a new start) and fills with the same files: linked again, told again.
        int seen = Volatile.Read(ref changed);
        h.Index.Clear();
        h.ForgetIds();
        h.Folder(Me);
        foreach (var p in paths) h.File(p, stamps[p].Body, stamps[p].Size, stamps[p].Modified);
        h.Run();
        Assert.Equal(0, h.Files.Reads);
        Assert.Equal(h.Id(paths[0]), h.BoundId(paths[0]));
        Assert.True(WaitFor(() => Volatile.Read(ref changed) > seen, 5000), "linking again after a Clear raised no ContentChanged");
    }

    /// <summary>A follower thread over a small index of three files, with the store's opens counted.</summary>
    sealed class Threaded
    {
        public readonly FileIndex Index = new();
        public readonly FakeFiles Files = new();
        public readonly FakeStore Store = new();
        public readonly FakeNames Names;
        public readonly ContentIndex Content;
        public int Opens;

        public Threaded(ContentIndexOptions options)
        {
            int root = Index.AddRoot("C:");
            int users = Index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
            int me = Index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
            for (int i = 0; i < 3; i++)
            {
                Files.Put($@"{Me}\f{i}.txt", 15, 100 + i, "words " + i);
                Index.Add($"f{i}.txt", me, FileAttributes.Archive, 15, 100 + i, 0);
            }
            Names = new FakeNames(Index);
            Content = new ContentIndex(() => { Interlocked.Increment(ref Opens); return Store.Reopen(); }, Files, new FakeGate(), NoPowerPolicy.Instance,
                options with { VolumeResolver = Harness.Resolve, RecentItems = () => [] });
            Content.Start(Names, Harness.DefaultSettings());
        }

        /// <summary>Three documents stored and nothing left to do (false while no follower holds the store: one that gave up closed it).</summary>
        public bool UpToDate()
        {
            try { return Content.Status.Phase == ContentPhase.UpToDate && Store.Live().Count == 3; }
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>Makes every iteration fail while the index keeps changing (waking the thread every 5 ms); returns how long until it gave up.</summary>
        public long FaultUntilGivenUp()
        {
            Store.Fault = true;
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource();
            var poke = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested) { Index.RaiseChanged(); Thread.Sleep(5); }
            });
            bool gaveUp = WaitFor(() => Content.Follower == null, 30_000);
            long took = sw.ElapsedMilliseconds;
            cts.Cancel();
            poke.Wait();
            Assert.True(gaveUp, "the follower never gave up");
            return took;
        }
    }

    [Fact]
    public void A_follower_that_gave_up_waits_out_its_back_off_and_rebuild_or_apply_start_a_new_one()
    {
        var t = new Threaded(new ContentIndexOptions(CommitAfter: TimeSpan.FromMilliseconds(100)) { FailureBackOffMs = 100 });
        try
        {
            Assert.True(WaitFor(t.UpToDate, 20_000), "the first pass didn't finish");
            var first = t.Content.Follower;
            long took = t.FaultUntilGivenUp();
            // 100 + 200 + 300 + 400 ms between the five failures, however often the index woke the thread meanwhile.
            Assert.True(took >= 900, $"gave up after {took} ms");
            Assert.Equal(ContentPhase.Stopped, t.Content.Status.Phase);
            Assert.Contains("stopped after an error", t.Content.Status.Reason);

            // The fault is gone: Rebuild text index starts a new follower, which opens the store again and reads.
            t.Store.Fault = false;
            int opens = Volatile.Read(ref t.Opens);
            t.Content.Rebuild();
            Assert.True(WaitFor(() => t.Content.Follower is { } f && !ReferenceEquals(f, first), 20_000), "Rebuild started no follower");
            Assert.True(WaitFor(t.UpToDate, 20_000), "nothing was read after the Rebuild");
            Assert.True(Volatile.Read(ref t.Opens) > opens);
            Assert.True(t.Store.DeleteAllCount > 0);

            // Again, and this time a settings change brings it back.
            var second = t.Content.Follower;
            t.FaultUntilGivenUp();
            t.Store.Fault = false;
            t.Content.Apply(Harness.DefaultSettings());
            Assert.True(WaitFor(() => t.Content.Follower is { } f && !ReferenceEquals(f, second), 20_000), "Apply started no follower");
            Assert.True(WaitFor(t.UpToDate, 20_000), "nothing was read after the Apply");
            Assert.NotEqual(ContentPhase.Stopped, t.Content.Status.Phase);
        }
        finally
        {
            t.Store.Fault = false;
            t.Content.Stop(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void An_unplugged_drive_keeps_its_documents_across_a_restart_and_a_replug_reads_nothing()
    {
        // As VolumeKeys.For: the drive's own key while it is there, a fallback key while it isn't.
        bool present = true;
        (string, string, string) Resolve(string path)
        {
            string p = path.TrimEnd('\\');
            if (!p.StartsWith("E:", StringComparison.OrdinalIgnoreCase)) return Harness.Resolve(path);
            string rest = p.Length > 2 ? p[2..].Trim('\\') : "";
            return present ? ("vol:{E0E0E0E0-0000-0000-0000-000000000001}", rest, @"E:\") : ("path:E:", rest, @"E:\");
        }
        var settings = Harness.DefaultSettings();
        settings.Folders = [Me, @"E:\"];
        var options = new ContentIndexOptions { VolumeResolver = Resolve };
        string a = $@"{Me}\a.txt";
        var onE = Enumerable.Range(0, 5).Select(i => $@"E:\Docs\e{i}.txt").ToArray();

        var one = new Harness(settings, options: options);
        one.Folder(Me);
        one.File(a, "alpha");
        foreach (var p in onE) one.File(p, "echo " + p);
        one.Start();
        one.Settle();
        var store = one.Store;
        Assert.Equal(6, store.Live().Count);
        Assert.Equal(5, store.Live().Count(d => store.VolumeKey(d.Row.Volume).StartsWith("vol:", StringComparison.Ordinal)));
        var stamps = new[] { a }.Concat(onE).ToDictionary(p => p, p => one.Files.Get(p));
        one.Dispose();

        // The app starts while the drive is unplugged: its include folder resolves only to a fallback key.
        present = false;
        using (var two = new Harness(settings, store: store, options: options))
        {
            two.Folder(Me);
            two.File(a, stamps[a].Body, stamps[a].Size, stamps[a].Modified);
            two.Start();
            two.Settle();
            two.Pass(60_000, step: 10_000);
            Assert.Equal(6, store.Live().Count);
        }

        // Plugged in again: every document binds by key and stamp, nothing is read.
        present = true;
        using var three = new Harness(settings, store: store, options: options);
        three.Folder(Me);
        foreach (var (p, s) in stamps) three.File(p, s.Body, s.Size, s.Modified);
        three.Start();
        three.Settle();
        Assert.Equal(0, three.Files.Reads);
        Assert.Equal(6, store.Live().Count);
    }

    [Theory]
    [InlineData(0x2000, DocStatus.OptedOut)]   // Windows' "don't index contents"
    [InlineData(0x4000, DocStatus.EfsEncrypted)] // encrypted, while encrypted files aren't read
    public void A_file_marked_after_it_was_read_loses_its_words_at_once_while_reading_is_paused(int attribute, DocStatus expected)
    {
        using var h = new Harness();
        h.Folder(Me);
        string path = $@"{Me}\medical.txt";
        h.File(path, "diagnosis words");
        h.Start();
        h.Settle();
        Assert.Equal("diagnosis words", h.Doc(path)!.Body);
        // On battery (reading on battery is off): nothing is read, and nothing needs to be.
        h.Power.State = PowerState.Unlimited with { OnBattery = true };
        h.Index.Update(h.Id(path), attributes: FileAttributes.Archive | (FileAttributes)attribute);
        h.Index.RaiseChanged();
        h.Pass(3000);
        Assert.Equal(expected, h.Row(path)!.Value.Status);
        Assert.Equal("", h.Doc(path)!.Body);
        Assert.Equal(1, h.Files.Reads);
        // The mark comes off: read again (once reading may go on).
        h.Power.State = PowerState.Unlimited;
        h.Index.Update(h.Id(path), attributes: FileAttributes.Archive);
        h.Index.RaiseChanged();
        h.Settle();
        Assert.Equal("diagnosis words", h.Doc(path)!.Body);
    }

    [Fact]
    public void Turning_off_encrypted_files_drops_their_words_without_waiting_for_reading()
    {
        var settings = Harness.DefaultSettings();
        settings.ReadEncrypted = true;
        using var h = new Harness(settings);
        h.Folder(Me);
        string path = $@"{Me}\secret.pdf";
        h.File(path, "secret words", attributes: FileAttributes.Archive | FileAttributes.Encrypted);
        h.Start();
        h.Settle();
        Assert.True((h.Row(path)!.Value.Flags & DocFlags.EncryptedOptIn) != 0);
        h.Power.State = PowerState.Unlimited with { OnBattery = true };
        var off = settings.Clone();
        off.ReadEncrypted = false;
        h.Content.Apply(off);
        h.Pass(3000);
        Assert.Equal(DocStatus.EfsEncrypted, h.Row(path)!.Value.Status);
        Assert.Equal("", h.Doc(path)!.Body);
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public void A_folder_taken_out_of_the_scope_shows_no_text_and_its_words_leave_the_store_at_once()
    {
        var settings = Harness.DefaultSettings();
        settings.Folders = [Me, @"C:\HR"];
        using var h = new Harness(settings);
        h.Folder(Me);
        h.Folder(@"C:\HR");
        string salary = @"C:\HR\salary.txt", other = @"C:\HR\rota.txt";
        h.File(salary, "the salary of the manager");
        h.File(other, "the rota of the week");
        h.Start();
        h.Settle();
        var highlighter = ContentHighlighter.Create("content:salary", new SearchOptions())!;
        Assert.NotNull(h.Content.GetSnippet(salary, highlighter));

        var narrower = settings.Clone();
        narrower.Folders = [Me];
        // A read-only store keeps every document it has: the text is never shown again all the same.
        h.Store.IsReadOnly = true;
        h.Content.Apply(narrower);
        h.Run();
        Assert.Null(h.Content.GetSnippet(salary, highlighter));
        Assert.Null(h.Content.GetStatus(salary));
        Assert.NotNull(h.Row(salary));

        // A store that can be written: the deletes are committed in the same step, not at the next cadence.
        h.Store.IsReadOnly = false;
        h.Content.Apply(settings);
        h.Settle();
        Assert.NotNull(h.Content.GetSnippet(salary, highlighter));
        h.Content.Apply(narrower);
        h.Run();
        Assert.Null(h.Row(salary));
        Assert.Null(h.Row(other));
    }

    [Fact]
    public async Task A_census_counted_elsewhere_is_calibrated_on_its_own_files()
    {
        // Settings › Add folder lists a folder the name index doesn't hold, and a scope change counts what it takes out:
        // both are priced as EstimateAsync prices, a short sample of their own files, not the priors alone.
        using var h = new Harness();
        h.Folder(Me);
        h.Start();
        h.Run();
        string[] listed = [.. Enumerable.Range(0, 4).Select(i => $@"C:\Jobs\long{i}.txt")];
        foreach (var p in listed) h.Files.Put(p, 9_000, h.NextTime(), string.Join(' ', Enumerable.Range(0, 1_000).Select(w => $"term{w}x{p.Length}")));
        var census = new ContentCensus(
            new Dictionary<ContentKind, KindTally> { [ContentKind.Text] = new(400, 3_600_000, 0, 0) },
            new Dictionary<ContentKind, IReadOnlyList<string>> { [ContentKind.Text] = listed }, 0, Complete: true);
        var calibration = await h.Content.CalibrateAsync(census, CancellationToken.None);
        Assert.Equal(listed.Length, h.Files.Reads);
        Assert.Equal(listed.Length - 1, calibration.Measured[ContentKind.Text]); // the first warms the reader up
        var prior = ContentEstimator.Priors.Costs[ContentKind.Text];
        Assert.True(calibration.Costs[ContentKind.Text].WordsPerFile > prior.WordsPerFile + 100, $"{calibration.Costs[ContentKind.Text]}");
        var priced = ContentEstimator.Estimate(census, calibration, ContentMode.Documents, h.Settings);
        Assert.NotEqual(ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, h.Settings).IndexBytes, priced.IndexBytes);
        // As administrator nothing is opened: the priors.
        h.Names.IsElevated = true;
        var elevated = await h.Content.CalibrateAsync(census, CancellationToken.None);
        Assert.Equal(listed.Length, h.Files.Reads);
        Assert.Equal(0, elevated.Measured.GetValueOrDefault(ContentKind.Text));
    }

    [Fact]
    public async Task Running_as_administrator_estimates_without_opening_a_file()
    {
        using var h = new Harness();
        h.Names.IsElevated = true;
        h.Folder(Me);
        for (int i = 0; i < 5; i++) h.File($@"{Me}\r{i}.pdf", "report " + i);
        h.Start();
        h.Run();
        Assert.Equal(0, h.Files.Reads); // the follower reads nothing as administrator
        await h.Content.EstimateAsync(h.Settings, CancellationToken.None);
        Assert.Equal(0, h.Files.Reads);
        // Not elevated, the calibration samples them.
        h.Names.IsElevated = false;
        await h.Content.EstimateAsync(h.Settings, CancellationToken.None);
        Assert.True(h.Files.Reads > 0);
    }

    [Fact]
    public async Task An_estimate_of_a_name_index_still_scanning_says_at_least_and_is_counted_again_once_ready()
    {
        using var h = new Harness();
        h.Folder(Me);
        for (int i = 0; i < 5; i++) h.File($@"{Me}\r{i}.txt", "report " + i);
        h.Start();
        h.Names.State = IndexState.Scanning;
        var during = await h.Content.EstimateAsync(h.Settings, CancellationToken.None);
        Assert.True(during.AtLeast);
        h.Names.State = IndexState.Ready;
        var after = await h.Content.EstimateAsync(h.Settings, CancellationToken.None);
        Assert.False(after.AtLeast); // counted again, not the cut-short census of a moment ago
        Assert.Equal(5, after.Documents);
    }

    [Fact]
    public void The_calibration_never_uses_setups_marker_file_for_an_index_in_memory()
    {
        var own = ContentIndex.CalibrationMarker(null, inMemory: true);
        Assert.NotNull(own);
        Assert.False(own!.HasFile);
        Assert.Null(ContentIndex.CalibrationMarker(null, inMemory: false)); // on disk without one: the %TEMP% fallback
        var dir = Directory.CreateTempSubdirectory("rp-marker-");
        try
        {
            using var file = CrashMarker.Open(Path.Combine(dir.FullName, "reading"), out _);
            Assert.NotNull(file);
            Assert.Same(file, ContentIndex.CalibrationMarker(file, inMemory: false));
            // The demo's follower has a marker with no file: the calibration gets one of its own, with no file either.
            Assert.False(ContentIndex.CalibrationMarker(CrashMarker.InMemory(), inMemory: true)!.HasFile);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_census_never_samples_ids_of_a_generation_that_moved()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int docs = index.Add("RvDocs", root, FileAttributes.Directory, -1, 0, 0);
        int secret = index.Add("RvSecret", root, FileAttributes.Directory, -1, 0, 0);
        var early = new List<int>();
        for (int i = 0; i < 60; i++)
        {
            int pdf = index.Add($"r{i}.pdf", docs, FileAttributes.Archive, 1000 + i, 1, 0);
            index.Add($"s{i}.txt", secret, FileAttributes.Archive, 10, 1, 0);
            if (i < 3) early.Add(pdf);
        }
        var settings = new ContentSettings { Mode = ContentMode.Documents, ModeExplicit = true, UseMyFolders = false, Folders = [@"C:\RvDocs"] };
        var plain = ContentEstimator.CountIndex(index, settings, CancellationToken.None);
        Assert.True(plain.Complete);
        Assert.NotEmpty(plain.Samples[ContentKind.Pdf]);
        Assert.All(plain.Samples[ContentKind.Pdf], p => Assert.StartsWith(@"C:\RvDocs\", p));

        // A Compact between the walk and the hold that reads the samples' paths: their ids now name other entries.
        ContentEstimator.AfterWalk = () =>
        {
            foreach (int id in early) index.Remove(id);
            index.Compact();
        };
        try
        {
            var moved = ContentEstimator.CountIndex(index, settings, CancellationToken.None);
            Assert.False(moved.Complete);
            Assert.Empty(moved.Samples);
        }
        finally { ContentEstimator.AfterWalk = null; }
    }

    [Fact]
    public async Task Setups_count_leaves_out_what_the_name_index_leaves_out()
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "names-census-" + Guid.NewGuid().ToString("N")));
        try
        {
            void Put(string rel)
            {
                string p = Path.Combine(dir.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[100]);
            }
            for (int i = 0; i < 5; i++) Put($@"docs\d{i}.pdf");
            for (int i = 0; i < 10; i++) Put($@"archive\a{i}.pdf");
            Put(@"docs\draft1.pdf");
            var settings = new ContentSettings { UseMyFolders = false };
            async Task<int> Pdfs(IReadOnlyList<string> folders, IndexSettings? names)
                => (await ContentEstimator.CountFoldersAsync(folders, settings, TimeSpan.FromSeconds(20), null, CancellationToken.None, names))
                    .Kinds.GetValueOrDefault(ContentKind.Pdf).Files;

            Assert.Equal(16, await Pdfs([dir.FullName], null));
            // The name index's own exclusions: a folder left out and a pattern.
            var excluding = new IndexSettings { ExcludeFolders = [Path.Combine(dir.FullName, "archive")], ExcludePatterns = ["*draft*"] };
            Assert.Equal(5, await Pdfs([dir.FullName], excluding));
            // The whole drive left out, but the folder searched by name: only what is inside that folder counts.
            string drive = Path.GetPathRoot(dir.FullName)!;
            var onlyDocs = new IndexSettings { ExcludeFolders = [drive], Folders = [Path.Combine(dir.FullName, "docs")] };
            Assert.Equal(0, await Pdfs([dir.FullName], onlyDocs));
            Assert.Equal(6, await Pdfs([Path.Combine(dir.FullName, "docs")], onlyDocs));
            // Whole drives not indexed, and the folder not searched by name: nothing of it is ever read.
            Assert.Equal(0, await Pdfs([dir.FullName], new IndexSettings { IndexNtfsVolumes = false }));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Setups_count_leaves_out_drives_whose_kind_is_switched_off()
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "names-kind-" + Guid.NewGuid().ToString("N")));
        try
        {
            for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"d{i}.pdf"), new byte[100]);
            var settings = new ContentSettings { UseMyFolders = false };
            async Task<int> Pdfs(IndexSettings names, DriveKind kind)
                => (await ContentEstimator.CountFoldersAsync([dir.FullName], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None, names, _ => kind,
                    _ => null)).Kinds.GetValueOrDefault(ContentKind.Pdf).Files;

            Assert.Equal(4, await Pdfs(new IndexSettings { IndexRemovableDrives = false, IndexNetworkDrives = false }, DriveKind.Fixed));
            // The folder's drive is a USB drive or a network drive, and those are switched off: the name index never has it.
            Assert.Equal(0, await Pdfs(new IndexSettings { IndexRemovableDrives = false }, DriveKind.Removable));
            Assert.Equal(0, await Pdfs(new IndexSettings { IndexNetworkDrives = false }, DriveKind.Network));
            // 1.4.0's list of drives, without this one.
            string other = char.ToUpperInvariant(dir.FullName[0]) == 'Q' ? @"R:\" : @"Q:\";
            Assert.Equal(0, await Pdfs(new IndexSettings { Volumes = [other] }, DriveKind.Fixed));
            // Searched by name as a folder, it counts whatever its drive is.
            Assert.Equal(4, await Pdfs(new IndexSettings { IndexRemovableDrives = false, Folders = [dir.FullName] }, DriveKind.Removable));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Setups_count_takes_a_subst_drive_where_the_folder_it_shows_is()
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "names-subst-" + Guid.NewGuid().ToString("N")));
        try
        {
            for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"d{i}.pdf"), new byte[100]);
            var settings = new ContentSettings { UseMyFolders = false };
            // Q: (R: when the folder is on Q:) is a SUBST of the folder, and Windows calls it fixed: it is listed where its files are.
            char own = char.ToUpperInvariant(dir.FullName[0]);
            string letter = own == 'Q' ? "R:" : "Q:";
            string? Subst(string root) => string.Equals(root, letter + @"\", StringComparison.OrdinalIgnoreCase) ? dir.FullName : null;
            async Task<int> Pdfs(IndexSettings names)
                => (await ContentEstimator.CountFoldersAsync([letter + @"\"], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None, names,
                    _ => DriveKind.Fixed, Subst)).Kinds.GetValueOrDefault(ContentKind.Pdf).Files;

            // 1.4.0's list of drives names the drive it shows, not the SUBST letter: its files are searched there.
            Assert.Equal(3, await Pdfs(new IndexSettings { Volumes = [own + @":\"] }));
            Assert.Equal(0, await Pdfs(new IndexSettings { Volumes = [letter + @"\"] }));
            Assert.Equal(0, await Pdfs(new IndexSettings { SkipDrives = [own + @":\"] }));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Setups_count_of_a_subst_folder_follows_the_rules_where_the_folder_it_shows_is()
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "names-subst-rules-" + Guid.NewGuid().ToString("N")));
        try
        {
            void Put(string rel)
            {
                string p = Path.Combine(dir.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[100]);
            }
            for (int i = 0; i < 3; i++) Put($@"Proj\d{i}.pdf");
            for (int i = 0; i < 4; i++) Put($@"Proj\Archive\a{i}.pdf");
            // Q: (R: when the folder is on Q:) is a SUBST of the folder, and a folder on it is searched inside.
            char own = char.ToUpperInvariant(dir.FullName[0]);
            string letter = own == 'Q' ? "R:" : "Q:";
            string? Subst(string root) => string.Equals(root, letter + @"\", StringComparison.OrdinalIgnoreCase) ? dir.FullName : null;
            async Task<int> Pdfs(ContentSettings settings, IndexSettings names)
                => (await ContentEstimator.CountFoldersAsync([letter + @"\Proj"], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None, names,
                    _ => DriveKind.Fixed, Subst)).Kinds.GetValueOrDefault(ContentKind.Pdf).Files;
            string archive = Path.Combine(dir.FullName, "Proj", "Archive");
            var inside = new ContentSettings { UseMyFolders = false };

            // The name index leaves the archive out by the paths it builds, under the folder the SUBST shows.
            Assert.Equal(3, await Pdfs(inside, new IndexSettings { ExcludeFolders = [archive] }));
            // A rule spelled with the SUBST letter matches none of those paths: the name index holds the archive.
            Assert.Equal(7, await Pdfs(inside, new IndexSettings { ExcludeFolders = [letter + @"\Proj\Archive"] }));
            // Search inside files' own rules are resolved by volume: spelled either way, they leave the archive out.
            Assert.Equal(3, await Pdfs(new ContentSettings { UseMyFolders = false, ExcludeFolders = [letter + @"\Proj\Archive"] }, new IndexSettings()));
            Assert.Equal(3, await Pdfs(new ContentSettings { UseMyFolders = false, ExcludeFolders = [archive] }, new IndexSettings()));
        }
        finally { dir.Delete(true); }
    }
}
