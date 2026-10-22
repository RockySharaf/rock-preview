using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// §9 estimates. The golden numbers are the §9 formula on the priors (measured on the brief's PC with the real pipeline,
/// 2026-10-21) for the censuses counted there that day: My folders (834 PDFs, 192 Office, 3,017 text and 4 design files)
/// about 18 MB and 2.2 minutes; with 13,073 pictures about 20 MB and 3.9 minutes; E:\ (5,827 PDFs, 164 Office, 13,851
/// text, 10 design files, 47,283 pictures) about 74 MB and 18.9 minutes; the print shop about 0.75 GB, 32.7 hours of
/// reading and 209 GB over the network. Priors alone, as Settings prices an added folder: Setup and Settings calibrate
/// on the user's own files (the real first passes there gave 30, 32 and about 67 MB).
/// </summary>
[Collection(TimingCollection.Name)]
public class EstimatorTests
{
    static ContentCensus Census(bool complete = true, params (ContentKind Kind, int Files, long Bytes, int NetworkFiles)[] kinds)
        => new(kinds.ToDictionary(k => k.Kind, k => new KindTally(k.Files, k.Bytes, k.NetworkFiles, k.NetworkFiles > 0 ? k.Bytes : 0)),
            new Dictionary<ContentKind, IReadOnlyList<string>>(), 0, complete);

    static readonly ContentSettings Settings = new();

    /// <summary>A scratch folder outside %TEMP% and AppData, which the census rightly never lists.</summary>
    static DirectoryInfo OutsideTemp() => Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "census-" + Guid.NewGuid().ToString("N")));

    static void Near(double expected, double actual, double tolerance = 0.001)
        => Assert.True(Math.Abs(actual - expected) <= Math.Abs(expected) * tolerance, $"expected about {expected:N0}, got {actual:N0}");

    static ContentCensus MyFolders() => Census(true, (ContentKind.Pdf, 834, 834L * 6_344_009, 0), (ContentKind.Office, 192, 192L * 1_514_024, 0),
        (ContentKind.Text, 3017, 3017L * 719_560, 0), (ContentKind.Design, 4, 4L * 10_258_246, 0), (ContentKind.Picture, 13_073, 13_073L * 254_944, 0));

    [Fact]
    public void My_folders_documents_about_18_MB_and_three_minutes()
    {
        var e = ContentEstimator.Estimate(MyFolders(), ContentEstimator.Priors, ContentMode.Documents, Settings);
        Near(18_340_371, e.IndexBytes);
        Assert.Equal(4047, e.Documents);
        Assert.Equal(0, e.Pictures);
        Near(129_810, e.FirstPass.TotalMilliseconds);
        Assert.Equal(0, e.NetworkBytesToRead);
        Assert.False(e.AtLeast);
    }

    [Fact]
    public void With_thirteen_thousand_pictures_about_20_MB_and_four_minutes()
    {
        var e = ContentEstimator.Estimate(MyFolders(), ContentEstimator.Priors, ContentMode.DocumentsAndPictures, Settings);
        Near(20_171_441, e.IndexBytes);
        Assert.Equal(13_073, e.Pictures);
        Near(233_740, e.FirstPass.TotalMilliseconds);
    }

    [Fact]
    public void Drive_E_about_74_MB_and_20_minutes()
    {
        var census = Census(true, (ContentKind.Pdf, 5827, 70_620_000_000, 0), (ContentKind.Office, 164, 0, 0), (ContentKind.Text, 13_851, 0, 0),
            (ContentKind.Design, 10, 0, 0), (ContentKind.Picture, 47_283, 0, 0));
        var e = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.DocumentsAndPictures, Settings);
        Near(74_251_987, e.IndexBytes);
        Near(1_134_792, e.FirstPass.TotalMilliseconds);
    }

    [Fact]
    public void The_print_shop_about_three_quarters_of_a_GB_33_hours_and_200_GB_over_the_network()
    {
        var census = Census(true, (ContentKind.Pdf, 850_000, 850_000L * (12L << 20), 850_000));
        var e = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, Settings);
        Near(750_999_547, e.IndexBytes);
        Near(32.688 * 3_600_000, e.FirstPass.TotalMilliseconds, 0.002);
        Near(208_896_000_000, e.NetworkBytesToRead);
    }

    [Fact]
    public void Calibration_samples_one_file_at_random_from_each_size_stratum_every_quartile_in_turn()
    {
        var items = Enumerable.Range(0, 1000).ToList();
        var picks = ContentEstimator.StratifiedPicks(items, new Random(3));
        Assert.Equal(40, picks.Count);
        // One per stratum of 25 (10 per quartile of 250), the quartiles in turn: a sample cut short still spans every size.
        Assert.Equal(40, picks.Select(p => p / 25).Distinct().Count());
        for (int i = 0; i < 40; i += 4) Assert.Equal([0, 1, 2, 3], picks.Skip(i).Take(4).Select(p => p / 250));
        // Unbiased for a long tail (the largest files hold most of the text), whole or cut short to its first 20: taking
        // each stratum's first item was 5% low here, and cutting a sample ordered by size short far more.
        double population = items.Average(i => Math.Pow(i, 3)), whole = 0, half = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            var sample = ContentEstimator.StratifiedPicks(items, new Random(seed));
            whole += sample.Average(i => Math.Pow(i, 3)) / 400;
            half += sample.Take(20).Average(i => Math.Pow(i, 3)) / 400;
        }
        Assert.InRange(whole / population, 0.98, 1.02);
        Assert.InRange(half / population, 0.95, 1.05);
        // Fewer than 40: every one, still a quartile at a time.
        var few = ContentEstimator.StratifiedPicks(Enumerable.Range(0, 12).ToList(), new Random(1));
        Assert.Equal(Enumerable.Range(0, 12), few.Order());
        Assert.Equal([0, 1, 2, 3], few.Take(4).Select(p => p / 3));
    }

    [Fact]
    public void Names_only_costs_nothing_and_switched_off_kinds_are_left_out()
    {
        var census = Census(false, (ContentKind.Pdf, 830, 0, 0), (ContentKind.Text, 4200, 0, 0));
        var off = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.NamesOnly, Settings);
        Assert.Equal((0, 0L, TimeSpan.Zero), (off.Documents, off.IndexBytes, off.FirstPass));
        Assert.True(off.AtLeast);
        var pdfOnly = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, new ContentSettings { Kinds = ContentKinds.Pdf });
        Assert.Equal(830, pdfOnly.Documents);
        Assert.True(pdfOnly.AtLeast);
    }

    [Fact]
    public void A_slower_throttle_lengthens_the_network_time()
    {
        var census = Census(true, (ContentKind.Pdf, 1000, 1000L * (12L << 20), 1000));
        var fast = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, Settings);
        var slow = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, new ContentSettings { NetworkKilobytesPerSecond = 1024 });
        Assert.True(slow.FirstPass > fast.FirstPass);
        Assert.Equal(fast.NetworkBytesToRead, slow.NetworkBytesToRead);
    }

    [Fact]
    public void Priors_blend_with_a_pseudo_count_of_five()
    {
        Assert.Equal(79, ContentEstimator.BlendOne(5, 100, 58));
        Assert.Equal(58, ContentEstimator.BlendOne(0, 100, 58));
        Assert.True(Math.Abs(ContentEstimator.BlendOne(200, 100, 58) - 100) < 1.1);
    }

    /// <summary>A calibration of text from samples: the per-file blend Blend makes, and the ratio's sums.</summary>
    static ContentCalibration TextDraw(int n, double stored, double capped)
    {
        var prior = ContentEstimator.Priors.Costs[ContentKind.Text];
        var costs = new Dictionary<ContentKind, KindCost>(ContentEstimator.Priors.Costs)
        {
            [ContentKind.Text] = prior with { StoredBytesPerFile = ContentEstimator.BlendOne(n, stored / n, prior.StoredBytesPerFile) },
        };
        var measured = new Dictionary<ContentKind, int>(ContentEstimator.Priors.Measured) { [ContentKind.Text] = n };
        return new ContentCalibration(costs, measured)
        {
            StoredRatios = new Dictionary<ContentKind, StoredRatio> { [ContentKind.Text] = new(n, stored, capped) },
        };
    }

    [Fact]
    public void Text_is_priced_as_stored_bytes_per_file_byte_once_sampled()
    {
        // 990 notes of 10 KB with 100 B of stored text each, and 10 logs of 10 MB that give the index 1 MB of text each
        // (10,486 B stored): text follows the bytes a file can give, at 1 B per 100.
        const long Cap = ContentEstimator.RatioCapBytes;
        var tally = new KindTally(1000, 990L * 10_000 + 10L * 10_000_000, 0, 0) { CappedBytes = 990L * 10_000 + 10 * Cap };
        var census = Census(true) with { Kinds = new Dictionary<ContentKind, KindTally> { [ContentKind.Text] = tally } };
        // Two draws of 10: one with a log, one without. Per file they are 11 times apart; as a ratio both say 1 B per 100.
        var withLog = TextDraw(10, 9 * 100 + 10_485.76, 9 * 10_000 + Cap);
        var notesOnly = TextDraw(10, 10 * 100, 10 * 10_000);
        var a = ContentEstimator.Estimate(census, withLog, ContentMode.Documents, Settings);
        var b = ContentEstimator.Estimate(census, notesOnly, ContentMode.Documents, Settings);
        Assert.Equal(a.IndexBytes, b.IndexBytes);
        // The formula: 1,000 × BlendOne(10, 0.01 × capped bytes per file, the prior), the rest as on the priors.
        var priors = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, Settings);
        double prior = ContentEstimator.Priors.Costs[ContentKind.Text].StoredBytesPerFile;
        double perFile = ContentEstimator.BlendOne(10, 0.01 * (tally.CappedBytes / 1000.0), prior);
        Near(priors.IndexBytes + 1000 * (perFile - prior), a.IndexBytes, 0.0001);
        Assert.Equal(1_169, perFile, 0); // (10 × 203.86 + 5 × 3,100) / 15
        // A census that didn't count capped bytes, and the per-file figures: the two draws apart again.
        var plain = census with { Kinds = new Dictionary<ContentKind, KindTally> { [ContentKind.Text] = tally with { CappedBytes = 0 } } };
        Assert.NotEqual(ContentEstimator.Estimate(plain, withLog, ContentMode.Documents, Settings).IndexBytes,
            ContentEstimator.Estimate(plain, notesOnly, ContentMode.Documents, Settings).IndexBytes);
        // A kind the index holds 200 documents of is priced from its own average, not the samples.
        var stats = new Dictionary<ContentKind, KindStats> { [ContentKind.Text] = new(400, 300, 400_000, 400_000, 40_000, 4_000_000, 4_000, 0) };
        Assert.False(ContentEstimator.WithIndexStats(withLog, stats).StoredRatios.ContainsKey(ContentKind.Text));
    }

    [Fact]
    public void Bytes_read_priors_follow_the_block_model()
    {
        Assert.Equal(15 * 16 * 1024, ContentEstimator.BytesReadPrior(ContentKind.Pdf, 12 << 20));
        Assert.Equal(1000, ContentEstimator.BytesReadPrior(ContentKind.Pdf, 1000));
        Assert.Equal(16 * 1024, ContentEstimator.BytesReadPrior(ContentKind.Picture, 5 << 20));
        Assert.Equal(1 << 20, ContentEstimator.BytesReadPrior(ContentKind.Design, 900 << 20));
        Assert.Equal(4 << 20, ContentEstimator.BytesReadPrior(ContentKind.Text, 50 << 20));
        Assert.Equal(1 << 20, ContentEstimator.BytesReadPrior(ContentKind.Office, 4 << 20));
    }

    [Fact]
    public void Kinds_the_index_holds_two_hundred_documents_of_are_priced_from_its_statistics()
    {
        var stats = new Dictionary<ContentKind, KindStats>
        {
            [ContentKind.Pdf] = new(Docs: 400, WithText: 100, RawTextBytes: 0, StoredBytes: 400_000, Pairs: 40_000, BytesRead: 4_000_000, MsTotal: 8000, Problems: 0),
            [ContentKind.Text] = new(Docs: 10, WithText: 10, RawTextBytes: 0, StoredBytes: 1, Pairs: 1, BytesRead: 1, MsTotal: 1, Problems: 0),
        };
        var c = ContentEstimator.WithIndexStats(ContentEstimator.Priors, stats);
        Assert.Equal(new KindCost(20, 1000, 100, 10_000), c.Costs[ContentKind.Pdf]);
        Assert.Equal(400, c.Measured[ContentKind.Pdf]);
        Assert.Equal(ContentEstimator.Priors.Costs[ContentKind.Text], c.Costs[ContentKind.Text]);
    }

    sealed class TimedSource(int sleepMs, string body) : IContentSource
    {
        public int Reads;
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
            var doc = new ExtractedDoc { Kind = request.Kind, Status = DocStatus.Ok, Body = body, BytesRead = 8192, ElapsedMs = sleepMs };
            return new ContentRead(doc, 100, 200, false, false);
        }
        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
        {
            size = 100; modifiedUtc = 200; attributes = FileAttributes.Archive;
            return true;
        }
    }

    static ContentCensus WithSamples(ContentKind kind, IEnumerable<string> paths)
        => new(new Dictionary<ContentKind, KindTally> { [kind] = new(100, 100 * 50_000, 0, 0) },
            new Dictionary<ContentKind, IReadOnlyList<string>> { [kind] = [.. paths] }, 0, true);

    [Fact]
    public void Calibration_measures_real_reads_discarding_each_readers_first()
    {
        var dir = Directory.CreateTempSubdirectory("rp-cal-");
        try
        {
            string body = string.Join(' ', Enumerable.Range(0, 300).Select(i => "word" + i));
            var source = new TimedSource(0, body);
            var census = WithSamples(ContentKind.Text, Enumerable.Range(0, 6).Select(i => $@"C:\samples\t{i}.txt"));
            var c = ContentEstimator.Calibrate(census, source, TimeSpan.FromSeconds(5), 10, CancellationToken.None, Path.Combine(dir.FullName, "cal"));
            Assert.Equal(6, source.Reads);
            Assert.Equal(5, c.Measured[ContentKind.Text]);
            // 300 distinct words (plus nothing else) blended with the prior at weight 5 against 5.
            Assert.Equal((300 + ContentEstimator.Priors.Costs[ContentKind.Text].WordsPerFile) / 2.0, c.Costs[ContentKind.Text].WordsPerFile, 0);
            // The ratio's sums: the five counted samples' stored text and their sizes (100 B each, under the 1 MB cap).
            long stored = ContentEstimator.StoredBytes(new ExtractedDoc { Kind = ContentKind.Text, Status = DocStatus.Ok, Body = body });
            Assert.Equal(new StoredRatio(5, 5 * stored, 500), c.StoredRatios[ContentKind.Text]);
            Assert.Equal(0, c.Measured[ContentKind.Pdf]);
            Assert.False(c.StoredRatios.ContainsKey(ContentKind.Pdf));
            Assert.Equal(ContentEstimator.Priors.Costs[ContentKind.Pdf].MsPerFile, c.Costs[ContentKind.Pdf].MsPerFile);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Calibration_measures_the_per_file_network_overhead_from_wall_minus_cpu_time()
    {
        var dir = Directory.CreateTempSubdirectory("rp-cal-");
        try
        {
            var source = new TimedSource(60, "network words");
            var census = WithSamples(ContentKind.Pdf, Enumerable.Range(0, 7).Select(i => $@"\\nas\jobs\p{i}.pdf"));
            var c = ContentEstimator.Calibrate(census, source, TimeSpan.FromSeconds(10), maxNetworkSamples: 6, CancellationToken.None, Path.Combine(dir.FullName, "cal"));
            Assert.Equal(6, source.Reads); // at most six network samples
            Assert.Equal(5, c.NetworkMeasured);
            Assert.False(c.Costs.ContainsKey(ContentKind.None));
            double overhead = c.NetworkOverheadMs;
            // Five samples of at least 60 ms waiting each, blended with the 5 ms prior at weight five: 32 ms or more (a busy
            // PC only adds waiting).
            Assert.InRange(overhead, 25, 1000);
            // The waiting is overhead, not reading: ms per file stays near the CPU time, far below 60.
            Assert.True(c.Costs[ContentKind.Pdf].MsPerFile < 45, c.Costs[ContentKind.Pdf].MsPerFile.ToString());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_calibration_that_stopped_in_a_file_falls_back_to_the_priors_then_skips_that_file()
    {
        var dir = Directory.CreateTempSubdirectory("rp-cal-");
        string marker = Path.Combine(dir.FullName, "cal");
        try
        {
            var m = CrashMarker.Open(marker, out _)!;
            m.Enter(m.Acquire(), new MarkerEntry(ContentKeys.Of("path:", @"C:\SAMPLES\T1.TXT"), 0, MarkerLane.OnDemand, 0, 0));
            CrashMarkerTests.CloseWithoutClearing(m);
            var source = new TimedSource(0, "words words");
            var census = WithSamples(ContentKind.Text, Enumerable.Range(0, 4).Select(i => $@"C:\samples\t{i}.txt"));
            Assert.Same(ContentEstimator.Priors, ContentEstimator.Calibrate(census, source, TimeSpan.FromSeconds(5), 10, CancellationToken.None, marker));
            Assert.Equal(0, source.Reads);
            var again = ContentEstimator.Calibrate(census, source, TimeSpan.FromSeconds(5), 10, CancellationToken.None, marker);
            Assert.Equal(3, source.Reads); // t1 skipped
            Assert.Equal(2, again.Measured[ContentKind.Text]);
        }
        finally { dir.Delete(true); }
    }

    /// <summary>A reader that ignores cancellation for a while on one file (a hung read).</summary>
    sealed class StuckSource(string stuck) : IContentSource
    {
        public readonly ManualResetEventSlim Release = new(false);
        public volatile bool Reading;
        public volatile bool Cancelled;
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            if (request.FullPath.EndsWith(stuck, StringComparison.Ordinal))
            {
                Reading = true;
                Release.Wait();
                Cancelled = ct.IsCancellationRequested;
                Reading = false;
            }
            var doc = new ExtractedDoc { Kind = request.Kind, Status = DocStatus.Ok, Body = "words", BytesRead = 100 };
            return new ContentRead(doc, 100, 200, false, false);
        }
        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
        {
            size = 100; modifiedUtc = 200; attributes = FileAttributes.Archive;
            return true;
        }
    }

    static int LiveSlots(string markerPath)
    {
        var bytes = new byte[4096];
        using (var fs = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            fs.ReadExactly(bytes, 0, (int)Math.Min(4096, fs.Length));
        int named = 0;
        for (int i = 0; i < CrashMarker.LiveSlots; i++) if (BitConverter.ToUInt64(bytes, 64 + i * 32) != 0) named++;
        return named;
    }

    [Fact]
    public void A_calibration_read_left_behind_keeps_naming_its_file_until_it_really_ends_and_is_cancelled_at_its_time()
    {
        var dir = Directory.CreateTempSubdirectory("rp-cal-");
        string marker = Path.Combine(dir.FullName, "cal");
        var source = new StuckSource("slow.pdf");
        try
        {
            var census = WithSamples(ContentKind.Pdf, [@"C:\samples\warm.pdf", @"C:\samples\slow.pdf"]);
            ContentEstimator.Calibrate(census, source, TimeSpan.FromMilliseconds(300), 10, CancellationToken.None, marker);
            Assert.True(source.Reading, "the slow read should still be running");
            // The process could die now: the marker still names the file being read.
            Assert.Equal(1, LiveSlots(marker));
            source.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => !source.Reading, 5000));
            Assert.True(source.Cancelled, "the read's token is cancelled at its time");
            // Once it has ended: its slot is cleared and the marker closed (it can be opened again).
            Assert.True(SpinWait.SpinUntil(() => LiveSlots(marker) == 0, 5000));
            CrashMarker? again = null;
            Assert.True(SpinWait.SpinUntil(() => (again = CrashMarker.Open(marker, out _)) != null, 5000));
            again!.Dispose();
        }
        finally
        {
            source.Release.Set();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_calibration_in_the_index_marker_skips_its_culprits_and_names_files_by_document_key()
    {
        var dir = Directory.CreateTempSubdirectory("rp-cal-");
        try
        {
            using var m = CrashMarker.Open(Path.Combine(dir.FullName, "reading"), out _)!;
            ulong Key(string p) => ContentKeys.Of("vol:test", p.ToUpperInvariant());
            m.Carry(new MarkerEntry(Key(@"C:\samples\t1.txt"), 0, MarkerLane.OnDemand, 100, 200));
            var source = new TimedSource(0, "words words");
            var census = WithSamples(ContentKind.Text, Enumerable.Range(0, 4).Select(i => $@"C:\samples\t{i}.txt"));
            var c = ContentEstimator.Calibrate(census, source, TimeSpan.FromSeconds(5), 10, CancellationToken.None, marker: m, keyOf: Key);
            Assert.Equal(3, source.Reads); // t1 skipped
            Assert.Equal(2, c.Measured[ContentKind.Text]);
            Assert.False(m.IsDisposed); // the index's marker stays open
            Assert.Equal(0, LiveSlots(Path.Combine(dir.FullName, "reading")));
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Folders_are_counted_by_kind_leaving_out_denied_folders_secrets_and_other_files()
    {
        var dir = OutsideTemp();
        try
        {
            void Put(string rel, int bytes = 10)
            {
                string p = Path.Combine(dir.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[bytes]);
            }
            for (int i = 0; i < 60; i++) Put($@"docs\d{i}.pdf", 100 + i);
            Put(@"docs\a.docx");
            Put(@"docs\notes.txt");
            Put(@"docs\app.log");
            Put(@"docs\.env");
            Put(@"docs\key.pem");
            Put(@"docs\song.mp3");
            Put(@"docs\pic.jpg");
            Put(@"node_modules\pkg\readme.md");
            Put(@"AppData\Roaming\x.txt");
            Put(@"src\.git\HEAD.txt");
            var settings = new ContentSettings { ExtraTextExtensions = ["log"] };
            var census = await ContentEstimator.CountFoldersAsync([dir.FullName], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None);
            Assert.True(census.Complete);
            Assert.Equal(60, census.Kinds[ContentKind.Pdf].Files);
            Assert.Equal(Enumerable.Range(0, 60).Sum(i => 100L + i), census.Kinds[ContentKind.Pdf].Bytes);
            Assert.Equal(1, census.Kinds[ContentKind.Office].Files);
            Assert.Equal(2, census.Kinds[ContentKind.Text].Files); // notes.txt and the extra app.log
            Assert.Equal(20, census.Kinds[ContentKind.Text].CappedBytes);
            Assert.Equal(1, census.Kinds[ContentKind.Picture].Files);
            Assert.Equal(40, census.Samples[ContentKind.Pdf].Count);
            Assert.All(census.Samples[ContentKind.Pdf], p => Assert.True(File.Exists(p)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task A_census_cut_short_says_at_least_and_settings_edited_meanwhile_do_no_harm()
    {
        var dir = OutsideTemp();
        try
        {
            for (int i = 0; i < 50; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"f{i}.txt"), [1]);
            var settings = new ContentSettings();
            var task = ContentEstimator.CountFoldersAsync([dir.FullName], settings, TimeSpan.Zero, null, CancellationToken.None);
            for (int i = 0; i < 1000; i++) { settings.ExcludeFolders.Add("x" + i); settings.ExtraTextExtensions.Add("e" + i); }
            var census = await task;
            Assert.False(census.Complete);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_live_index_is_counted_in_scope_only()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
        int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
        int app = index.Add("AppData", me, FileAttributes.Directory, -1, 0, 0);
        int other = index.Add("Other", users, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 30; i++) index.Add($"r{i}.pdf", me, FileAttributes.Archive, 1000, 1, 0);
        index.Add("cloud.pdf", me, FileAttributes.Archive | (FileAttributes)0x400000, 1000, 1, 0);
        index.Add("x.txt", app, FileAttributes.Archive, 10, 1, 0);
        index.Add("y.pdf", other, FileAttributes.Archive, 10, 1, 0);
        index.Add("z.jpg", me, FileAttributes.Archive, 10, 1, 0);
        index.Add("big.txt", me, FileAttributes.Archive, 3 << 20, 1, 0);
        var settings = new ContentSettings { UseMyFolders = false, Folders = [@"C:\Users\Me"] };
        var census = ContentEstimator.CountIndex(index, settings, CancellationToken.None);
        Assert.Equal(30, census.Kinds[ContentKind.Pdf].Files);
        Assert.Equal(30_000, census.Kinds[ContentKind.Pdf].Bytes);
        Assert.Equal(1, census.Kinds[ContentKind.Picture].Files);
        // Text: big.txt only (x.txt is in AppData), counted at most 1 MB for the ratio of stored text.
        var text = census.Kinds[ContentKind.Text];
        Assert.Equal((1, 3L << 20, ContentEstimator.RatioCapBytes), (text.Files, text.Bytes, text.CappedBytes));
        Assert.Equal(30_000, census.Kinds[ContentKind.Pdf].CappedBytes);
        Assert.Equal(1, census.CloudOnly);
        Assert.Equal(30, census.Samples[ContentKind.Pdf].Count);
        Assert.StartsWith(@"C:\Users\Me\r", census.Samples[ContentKind.Pdf][0]);
        // Every root in scope (the demo): the other folder counts too, AppData still doesn't.
        var demo = ContentEstimator.CountIndex(index, settings, allRootsInScope: true, CancellationToken.None);
        Assert.Equal(31, demo.Kinds[ContentKind.Pdf].Files);
        Assert.Equal(1, demo.Kinds[ContentKind.Text].Files);
    }

    [Fact]
    public void Stored_bytes_and_distinct_words_are_measured_as_the_index_makes_them()
    {
        var small = new ExtractedDoc { Kind = ContentKind.Text, Status = DocStatus.Ok, Body = "hello world" };
        Assert.True(ContentEstimator.StoredBytes(small) < 128);
        var big = new ExtractedDoc { Kind = ContentKind.Text, Status = DocStatus.Ok, Body = string.Concat(Enumerable.Repeat("the same line again\n", 500)) };
        Assert.True(ContentEstimator.StoredBytes(big) < big.Body.Length / 10);
        var fields = new ExtractedDoc { Kind = ContentKind.Text, Status = DocStatus.Ok, Body = "alpha beta alpha", Fields = [(DocField.Title, "gamma")] };
        Assert.Equal(4, ContentEstimator.DistinctWords(fields)); // alpha, beta, gamma, and gamma scoped to the title
    }
}
