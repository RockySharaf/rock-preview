using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Core.Indexing;
using Anything.Core.Search;
using SortKey = Anything.Core.SortKey;

namespace ContentProbe;

/// <summary>
/// The whole pipeline on this PC's real files, read-only: the files measured are only ever opened for reading (the real
/// readers, as the app opens them); everything written goes to the --work folder.
///   esample --root DIR --count N --out LIST [--seed S]
///       a sample stratified by kind of the candidates under a drive (PDF, Office, text, design, mail, pictures), with the
///       whole drive's census beside it (LIST.census.json)
///   build (--folders DIR... | --list LIST) --work DIR [--phases docs,pictures] [--active] [--estimate-only]
///       names by scanning into a FileIndex; Setup's and Settings' estimates (census, calibration, estimate) before reading;
///       then the real follower and store build the index in DIR\Content (documents first, then pictures' details), and
///       the index is taken apart per kind (stored text, postings and dictionary, rows, statuses, bytes read, time)
///   search (--folders DIR... | --list LIST) --work DIR [--synthetic N]
///       search latency, cold and warm, with the built index loaded beside the names (and N synthetic names)
///   calib --folders DIR... --work DIR [--times N]; jit --folders DIR...; cpu FILE...
///       Setup's calibration repeated in one process; each sample's time in order; a reader's CPU alone, from memory
/// </summary>
static partial class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly ContentKind[] Kinds = [ContentKind.Pdf, ContentKind.Office, ContentKind.Text, ContentKind.Mail, ContentKind.Design, ContentKind.Picture];
    const FileAttributes CloudBits = (FileAttributes)(0x1000 | 0x40000 | 0x400000);

    // ───────────────────────────── stratified sample ─────────────────────────────

    sealed record Candidate(string Path, ContentKind Kind, long Size);

    /// <summary>Every candidate under a folder by the census's own rules (deny-lists, cloud placeholders, temporary files, the size limit).</summary>
    static List<Candidate> Candidates(string root, ContentSettings s, out long listedBytes)
    {
        var found = new ConcurrentBag<Candidate>();
        var extras = ContentTypes.ExtraSet(s.ExtraTextExtensions);
        long maxBytes = s.MaxFileSizeMB > 0 ? (long)s.MaxFileSizeMB << 20 : long.MaxValue;
        var filter = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = [.. ContentTypes.DeniedFolderNames, .. s.ExcludeFolders, .. ContentTypes.DeniedFolderPaths()],
        });
        long listed = 0;
        FolderScanner.Scan(root, filter, CancellationToken.None, onScanned: dir =>
        {
            foreach (var part in dir.FullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                if (ContentTypes.IsDeniedFolder(part)) return;
            foreach (var e in dir.Entries)
            {
                if ((e.Attributes & FileAttributes.Directory) != 0) continue;
                Interlocked.Add(ref listed, Math.Max(0, e.Size));
                if ((e.Attributes & FileAttributes.Temporary) != 0 || ContentTypes.IsDeniedFile(e.Name)) continue;
                var kind = ContentTypes.KindOf(e.Name, extras);
                if (kind == ContentKind.None || (e.Attributes & CloudBits) != 0) continue;
                if (kind != ContentKind.Picture && e.Size > maxBytes) continue;
                found.Add(new Candidate(dir.FullPath.TrimEnd('\\') + "\\" + e.Name, kind, e.Size));
            }
        });
        listedBytes = listed;
        return [.. found.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];
    }

    static int StratifiedSample(Options o)
    {
        string root = o.Get("root")!;
        int count = o.Int("count", 10_000), seed = o.Int("seed", 11);
        string output = o.Get("out")!;
        var sw = Stopwatch.StartNew();
        var all = Candidates(root, new ContentSettings(), out long listed);
        Console.WriteLine($"{all.Count:N0} candidates under {root} in {sw.Elapsed.TotalSeconds:F1} s ({listed / 1e9:F1} GB listed)");
        var byKind = all.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.ToList());
        // The brief's design: every file of the rare kinds (Office, design, mail), the rest split 3 : 2 : 3 between PDF,
        // text and pictures, each a uniform random sample of its kind.
        int rare = 0;
        foreach (var k in (ContentKind[])[ContentKind.Office, ContentKind.Design, ContentKind.Mail]) rare += Math.Min(byKind.GetValueOrDefault(k)?.Count ?? 0, 500);
        int rest = Math.Max(0, count - rare);
        var quota = new Dictionary<ContentKind, int>
        {
            [ContentKind.Office] = 500, [ContentKind.Design] = 500, [ContentKind.Mail] = 500,
            [ContentKind.Pdf] = rest * 3 / 8, [ContentKind.Text] = rest * 2 / 8, [ContentKind.Picture] = rest - rest * 3 / 8 - rest * 2 / 8,
        };
        var rng = new Random(seed);
        var picked = new List<Candidate>();
        foreach (var kind in Kinds)
        {
            if (!byKind.TryGetValue(kind, out var list)) continue;
            picked.AddRange(list.OrderBy(_ => rng.Next()).Take(quota[kind]));
        }
        File.WriteAllLines(output, picked.Select(c => $"{c.Kind}\t{c.Size}\t{c.Path}"), new UTF8Encoding(false));
        var census = Kinds.Where(byKind.ContainsKey).ToDictionary(k => k.ToString(), k => new { Files = byKind[k].Count, Bytes = byKind[k].Sum(c => c.Size),
            Sampled = picked.Count(c => c.Kind == k), SampledBytes = picked.Where(c => c.Kind == k).Sum(c => c.Size) });
        File.WriteAllText(output + ".census.json", JsonSerializer.Serialize(new { Root = root, ListedBytes = listed, Kinds = census }, Json));
        foreach (var (k, v) in census) Console.WriteLine($"  {k,-8} {v.Files,8:N0} files {v.Bytes / 1e9,8:F2} GB   sampled {v.Sampled,6:N0} ({v.SampledBytes / 1e9:F2} GB)");
        Console.WriteLine($"{picked.Count:N0} sampled into {output}");
        return 0;
    }

    // ───────────────────────────── names ─────────────────────────────

    /// <summary>What the name index would hold: each folder scanned as a folder root, or a sample's files under a drive root.</summary>
    sealed class Names(FileIndex index) : IIndexManager
    {
        public FileIndex Index { get; } = index;
        public IndexState State => IndexState.Ready;
        public string StatusText => "";
        public event Action? StateChanged { add { } remove { } }
        public bool IsElevated => false;
        public bool UsesService => false;
        public void Start(IndexSettings settings) { }
        public void Rebuild() { }
        public void Save() { }
        public void Dispose() { }
    }

    /// <summary>Every root Ready and complete (the name index has finished scanning).</summary>
    sealed class ReadyGate : IRootGate
    {
        public RootGateState? StateOf(string rootName) => new RootGateState(RootStatus.Ready, true, true);
        public void Refresh() { }
    }

    /// <summary>The PC's real power, full-screen and disk state, with the user taken as idle (full speed) or active (50% duty cycle).</summary>
    sealed class Power(bool active) : IPowerPolicy
    {
        public int Polls, PausedPolls;
        public PowerState Now(string contentFolder)
        {
            var s = SystemPowerPolicy.Instance.Now(contentFolder) with { Idle = active ? TimeSpan.Zero : TimeSpan.FromHours(1) };
            Interlocked.Increment(ref Polls);
            if (PowerRules.ReadPause(s, false, false) != null) Interlocked.Increment(ref PausedPolls);
            return s;
        }
    }

    sealed record Scope(FileIndex Index, List<string> Folders, double ScanMs, int Entries);

    static Scope NamesFor(Options o, int synthetic = 0)
    {
        var sw = Stopwatch.StartNew();
        var index = new FileIndex();
        var folders = new List<string>();
        if (o.Get("list") is { } list)
        {
            // Each file under its drive or share root (as the name index holds it), the roots in scope.
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int Folder(string full)
            {
                if (ids.TryGetValue(full, out int id)) return id;
                string root = Path.GetPathRoot(full)!.TrimEnd('\\');
                if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
                {
                    folders.Add(root + "\\");
                    return ids[full] = index.AddRoot(root);
                }
                string parent = Path.GetDirectoryName(full)!.TrimEnd('\\');
                Native.TryGetMetadata(full, out _, out _, out long fm, out long fc, out _);
                id = index.Add(Path.GetFileName(full), Folder(parent), FileAttributes.Directory, -1, fm, fc);
                ids[full] = id;
                return id;
            }
            foreach (var line in File.ReadLines(list))
            {
                var parts = line.Split('\t');
                string path = parts[^1];
                if (path.Length == 0 || !Native.TryGetMetadata(path, out var attrs, out long size, out long modified, out long created, out _)) continue;
                index.Add(Path.GetFileName(path), Folder(Path.GetDirectoryName(path)!.TrimEnd('\\')), attrs, size, modified, created);
            }
        }
        else
        {
            foreach (var f in o.All("folders"))
            {
                string folder = f.TrimEnd('\\');
                folders.Add(folder);
                var tree = FolderScanner.Scan(folder, ExclusionFilter.None, CancellationToken.None);
                Native.TryGetMetadata(folder, out _, out _, out long mod, out long created, out _);
                index.BulkAdd(b => FolderScanner.AddTree(b, tree, folder, FileIndex.NoParent, FileAttributes.Directory, mod, created, null));
            }
        }
        if (synthetic > 0) AddSynthetic(index, synthetic);
        return new Scope(index, folders, sw.Elapsed.TotalMilliseconds, index.LiveCount);
    }

    /// <summary>SearchPerfTests' synthetic tree (1,000 files per folder, same words and seed) under a root no scope holds.</summary>
    static void AddSynthetic(FileIndex index, int entries)
    {
        string[] words = ["alpha", "beta", "gamma", "delta", "report", "image", "music", "video", "notes", "backup", "Café", "draft"];
        string[] exts = ["txt", "jpg", "mp3", "dll", "cs", "pdf", "png", "exe", "json", "xml"];
        var rnd = new Random(1234);
        int folders = Math.Max(1, entries / 1000);
        index.BulkAdd(add =>
        {
            int root = add.Add("X:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            var tops = new int[10];
            for (int t = 0; t < tops.Length; t++) tops[t] = add.Add("top" + t, root, FileAttributes.Directory, -1, 0, 0);
            for (int f = 0; f < folders; f++)
            {
                int folder = add.Add($"{words[f % words.Length]}_folder{f}", tops[f % tops.Length], FileAttributes.Directory, -1, 0, 0);
                for (int i = 0; i < 1000; i++)
                {
                    string name = $"{words[rnd.Next(words.Length)]}_{rnd.Next(100000)}_{words[rnd.Next(words.Length)]}.{exts[rnd.Next(exts.Length)]}";
                    add.Add(name, folder, FileAttributes.Archive, rnd.Next(10_000_000), 133_500_000_000_000_000L + rnd.Next(), 0);
                }
            }
        });
    }

    static ContentSettings SettingsFor(Scope scope, ContentMode mode) => new()
    {
        Mode = mode,
        ModeExplicit = true,
        UseMyFolders = false,
        Folders = [.. scope.Folders],
    };

    // ───────────────────────────── reads ─────────────────────────────

    sealed record ReadLog(string Path, ContentKind Kind, long Size, DocStatus Status, long BytesRead, int ElapsedMs, double WallMs, bool Network,
        bool Retry, bool Offline, int Pages, int BodyChars, int Fields, bool Picture);

    /// <summary>The real readers, each read logged (what it cost, how it went); nothing else changes.</summary>
    sealed class LoggingSource(IContentSource inner) : IContentSource
    {
        public readonly ConcurrentQueue<ReadLog> Log = new();
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            long t0 = Stopwatch.GetTimestamp();
            var read = inner.Read(request, ct);
            double wall = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var d = read.Doc;
            Log.Enqueue(new ReadLog(request.FullPath, request.Kind, read.Size, d.Status, d.BytesRead, d.ElapsedMs, wall, request.Network, read.Retry,
                read.Offline, d.Pages, d.Body.Length, d.Fields.Count, d.Picture != null));
            return read;
        }

        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
            => inner.TryStat(fullPath, out size, out modifiedUtc, out attributes);
    }

    // ───────────────────────────── build ─────────────────────────────

    sealed record EstimateRow(string Path, string Mode, int Documents, int Pictures, long IndexBytes, double FirstPassMinutes, bool AtLeast);

    sealed record Phase(string Name, double WallSeconds, double UpToDateSeconds, double LastChangeSeconds, long IndexBytesAtUpToDate, long IndexBytesSettled, int SegmentsSettled,
        int Reads, double ReadWallMsSum, double ReadElapsedMsSum, int Total, int Done, int Problems, string? Paused, int Commits);

    static int Build(Options o)
    {
        string work = Path.GetFullPath(o.Get("work")!);
        string content = Path.Combine(work, "Content");
        if (Directory.Exists(content) && Directory.EnumerateFileSystemEntries(content).Any())
        {
            Console.Error.WriteLine($"{content} isn't empty; give a new --work folder.");
            return 2;
        }
        Directory.CreateDirectory(content);
        var log = new StringBuilder();
        void Say(string line)
        {
            Console.WriteLine(line);
            log.AppendLine(line);
            File.WriteAllText(Path.Combine(work, "build-log.txt"), log.ToString(), new UTF8Encoding(false));
        }
        var phases = (o.Get("phases") ?? "docs,pictures").Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool active = o.Has("active");
        using var quiet = Drives.QuietErrors();

        // 1. Names, as the name index would hold them.
        var scope = NamesFor(o);
        Say($"names: {scope.Entries:N0} entries in {scope.ScanMs / 1000:F1} s; scope {string.Join(", ", scope.Folders)}");

        // 2. What Setup and Settings would show before anything is read.
        var all = SettingsFor(scope, ContentMode.DocumentsAndPictures);
        var estimates = new List<EstimateRow>();
        var calibrations = new Dictionary<string, ContentCalibration>();
        var censuses = new Dictionary<string, ContentCensus>();
        string marker = Path.Combine(work, "calibration.reading");
        void Price(string path, ContentCensus census, ContentCalibration calibration)
        {
            censuses[path] = census;
            calibrations[path] = calibration;
            foreach (var mode in (ContentMode[])[ContentMode.Documents, ContentMode.DocumentsAndPictures])
            {
                var e = ContentEstimator.Estimate(census, calibration, mode, all);
                estimates.Add(new EstimateRow(path, mode.ToString(), e.Documents, e.Pictures, e.IndexBytes, e.FirstPass.TotalMinutes, e.AtLeast));
                Say($"  {path,-22} {mode,-20} {e.Documents,7:N0} docs {e.Pictures,7:N0} pictures  {e.IndexBytes / 1e6,8:F1} MB  {e.FirstPass.TotalMinutes,6:F1} min"
                    + $"  \"{ContentPresentation.Size(e.IndexBytes)}; {ContentPresentation.Duration(e.FirstPass)}\"{(e.AtLeast ? " (at least)" : "")}");
            }
        }
        Say("estimates before reading:");
        var sw = Stopwatch.StartNew();
        var settingsCensus = ContentEstimator.CountIndex(scope.Index, all, CancellationToken.None);
        double countIndexMs = sw.Elapsed.TotalMilliseconds;
        Say($"  CountIndex (Settings) {countIndexMs:F0} ms: " + string.Join(", ", settingsCensus.Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Files:N0}")));
        Price("priors (Settings)", settingsCensus, ContentEstimator.Priors);
        // Setup lists the folders themselves (for a sample, the whole drive it was drawn from: compare with the
        // sample's per-kind costs scaled to the drive's census).
        {
            sw.Restart();
            var setupCensus = ContentEstimator.CountFoldersAsync(scope.Folders, all, TimeSpan.FromSeconds(20), null, CancellationToken.None).GetAwaiter().GetResult();
            Say($"  CountFoldersAsync (Setup) {sw.Elapsed.TotalSeconds:F1} s, complete {setupCensus.Complete}: "
                + string.Join(", ", setupCensus.Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Files:N0}")));
            sw.Restart();
            var setupCal = ContentEstimator.Calibrate(setupCensus, ContentServices.Files(), TimeSpan.FromSeconds(4), 10, CancellationToken.None, markerPath: marker);
            Say($"  Calibrate (Setup, 4 s) took {sw.Elapsed.TotalSeconds:F1} s: {Costs(setupCal)}");
            Price(o.Get("list") == null ? "Setup" : "Setup (whole drive)", setupCensus, setupCal);
        }
        sw.Restart();
        var settingsCal = ContentEstimator.Calibrate(settingsCensus, ContentServices.Files(), TimeSpan.FromSeconds(3), 10, CancellationToken.None, markerPath: marker);
        Say($"  Calibrate (Settings, 3 s) took {sw.Elapsed.TotalSeconds:F1} s: {Costs(settingsCal)}");
        Price("Settings", settingsCensus, settingsCal);
        if (o.Has("estimate-only")) return 0;

        // 3. The real index: follower, readers and store, documents first, then pictures' details.
        var source = new LoggingSource(ContentServices.Files());
        var power = new Power(active);
        var ci = new ContentIndex(() => ContentStore.Open(content), source, new ReadyGate(), power, new ContentIndexOptions
        {
            Folder = content,
            OpenReadOnly = () => ContentStore.Open(content, readOnly: true),
        });
        var names = new Names(scope.Index);
        var results = new List<Phase>();
        int logged = 0;
        foreach (var phase in phases)
        {
            var mode = phase == "docs" ? ContentMode.Documents : ContentMode.DocumentsAndPictures;
            var settings = SettingsFor(scope, mode);
            int before = source.Log.Count;
            sw.Restart();
            if (results.Count == 0) ci.Start(names, settings);
            else ci.Apply(settings);
            Say($"{phase}: reading ({mode}{(active ? ", user active: 50% duty cycle" : ", user idle: full speed")})");
            var (upToDate, status, paused) = WaitUpToDate(ci, sw, Say, TimeSpan.FromHours(3));
            long atUpToDate = status.IndexBytes;
            // Merges run after the queue empties (large ones only when idle on mains power): wait for the size to settle.
            var (settledBytes, segments, lastChange) = WaitSettled(ci, sw, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5));
            double wall = sw.Elapsed.TotalSeconds;
            var reads = source.Log.Skip(before).ToList();
            var final = ci.Status;
            results.Add(new Phase(phase, wall, upToDate.TotalSeconds, lastChange.TotalSeconds, atUpToDate, settledBytes, segments, reads.Count, reads.Sum(r => r.WallMs),
                reads.Sum(r => (double)r.ElapsedMs), final.Total, final.Done, final.Problems, paused, ci.Follower?.Commits ?? 0));
            Say($"{phase}: up to date in {upToDate.TotalSeconds:F1} s (last commit or merge at {lastChange.TotalSeconds:F1} s), {reads.Count:N0} reads (sum of read wall {reads.Sum(r => r.WallMs) / 1000:F1} s), "
                + $"{final.Done:N0}/{final.Total:N0} documents, {final.Problems} problems; index {atUpToDate / 1e6:F2} MB at up to date, "
                + $"{settledBytes / 1e6:F2} MB in {segments} segments after merges");
            logged = source.Log.Count;
        }
        var counts = ci.CountsByStatus();
        // Settings' calibration below reads through the same source: the build's reads are the ones logged so far.
        var buildReads = source.Log.ToList();
        var liveEstimate = ci.EstimateAsync(SettingsFor(scope, ContentMode.DocumentsAndPictures), CancellationToken.None).GetAwaiter().GetResult();
        Say($"Settings' estimate after the first pass (index statistics): {liveEstimate.Documents:N0} docs, {liveEstimate.Pictures:N0} pictures, "
            + $"{liveEstimate.IndexBytes / 1e6:F1} MB, {liveEstimate.FirstPass.TotalMinutes:F1} min");
        ci.Stop(TimeSpan.FromSeconds(10));
        ci.Dispose();
        Say("statuses: " + string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")) + $"; power polls {power.Polls}, paused {power.PausedPolls}");

        // 4. The index taken apart.
        var analysis = Analyse(content, work, buildReads, Say);
        File.WriteAllText(Path.Combine(work, "build.json"), JsonSerializer.Serialize(new
        {
            Scope = scope.Folders, scope.Entries, scope.ScanMs, CountIndexMs = countIndexMs,
            Censuses = censuses.ToDictionary(c => c.Key, c => c.Value.Kinds.ToDictionary(k => k.Key.ToString(), k => k.Value)),
            Calibrations = calibrations.ToDictionary(c => c.Key, c => c.Value.Costs.ToDictionary(k => k.Key.ToString(), k => new { k.Value.MsPerFile, k.Value.StoredBytesPerFile,
                k.Value.WordsPerFile, k.Value.BytesReadPerFile, Measured = c.Value.Measured.GetValueOrDefault(k.Key) })),
            Estimates = estimates,
            Phases = results,
            LiveEstimate = liveEstimate,
            Statuses = counts.ToDictionary(c => c.Key.ToString(), c => c.Value),
            Analysis = analysis,
        }, Json), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(work, "reads.tsv"), new[] { "path\tkind\tsize\tstatus\tbytesRead\telapsedMs\twallMs\tpages\tbodyChars\tfields\tpicture" }
            .Concat(buildReads.Select(r => string.Join('\t', r.Path, r.Kind, r.Size, r.Status, r.BytesRead, r.ElapsedMs, r.WallMs.ToString("0.##", CultureInfo.InvariantCulture),
                r.Pages, r.BodyChars, r.Fields, r.Picture))), new UTF8Encoding(false));
        return 0;
    }

    static string Costs(ContentCalibration c) => string.Join("; ", c.Costs.Where(k => c.Measured.GetValueOrDefault(k.Key) > 0).OrderBy(k => k.Key)
        .Select(k => $"{k.Key} n={c.Measured[k.Key]} {k.Value.MsPerFile:F1} ms {k.Value.StoredBytesPerFile:F0} B {k.Value.WordsPerFile:F0} words {k.Value.BytesReadPerFile / 1024:F0} KB read"));

    static (TimeSpan UpToDate, ContentStatus Status, string? Paused) WaitUpToDate(ContentIndex ci, Stopwatch sw, Action<string> say, TimeSpan max)
    {
        long lastSay = 0;
        TimeSpan? first = null;
        string? paused = null;
        while (sw.Elapsed < max)
        {
            Thread.Sleep(250);
            var st = ci.Status;
            var f = ci.Follower;
            if (st.Phase == ContentPhase.Paused) paused ??= st.Reason;
            bool done = st.Phase == ContentPhase.UpToDate && f != null && f.Waiting == 0 && !f.CatchingUp;
            if (done)
            {
                first ??= sw.Elapsed;
                if (sw.Elapsed - first.Value >= TimeSpan.FromSeconds(2)) return (first.Value, st, paused);
            }
            else first = null;
            if (sw.ElapsedMilliseconds - lastSay >= 15_000)
            {
                lastSay = sw.ElapsedMilliseconds;
                say($"  {sw.Elapsed.TotalSeconds,6:F0} s  {st.Phase} {st.Done:N0}/{st.Total:N0}  problems {st.Problems}  index {st.IndexBytes / 1e6:F1} MB  waiting {f?.Waiting}{(st.Reason != null ? "  (" + st.Reason + ")" : "")}");
            }
        }
        throw new TimeoutException("not up to date after " + max);
    }

    /// <summary>Waits until the index's size and segments stay the same for <paramref name="quiet"/>; returns them and when they last changed (on the phase's clock).</summary>
    static (long Bytes, int Segments, TimeSpan LastChange) WaitSettled(ContentIndex ci, Stopwatch phase, TimeSpan quiet, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        long last = -1;
        int lastSegments = -1;
        var lastChange = phase.Elapsed;
        var since = Stopwatch.StartNew();
        while (sw.Elapsed < max)
        {
            Thread.Sleep(200);
            long bytes = ci.Status.IndexBytes;
            int segments;
            using (var view = ci.AcquireView()) segments = view?.Store.Segments.Count ?? 0;
            if (bytes != last || segments != lastSegments)
            {
                if (last >= 0) lastChange = phase.Elapsed;
                last = bytes;
                lastSegments = segments;
                since.Restart();
            }
            else if (since.Elapsed >= quiet) break;
        }
        return (last, lastSegments, lastChange);
    }

    // ───────────────────────────── analysis ─────────────────────────────

    sealed class DistinctSink : ITokenSink
    {
        readonly HashSet<string> _set = new(StringComparer.Ordinal);
        readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        public DistinctSink() => _lookup = _set.GetAlternateLookup<ReadOnlySpan<char>>();
        public int Count => _set.Count;
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (_set.Count < ContentTokenizer.MaxDistinctPerDoc) _lookup.Add(folded);
        }
    }

    sealed record KindRow(string Kind, int Files, int WithText, long StoredBytes, long Pairs, long Terms, long PostingsBytes, long DictBytes,
        long RowsBytes, long PathsBytes, long MetaBytes, long SegmentBytes, double MsPerFile, double WallMsPerFile, double P95Ms, double BytesReadPerFile,
        double MeanFileBytes, Dictionary<string, int> Statuses, double? PdfReadMedianPct, double? PdfReadP95Pct, double? PdfReadPooledPct,
        double? PdfReadMedianKB);

    static long[] SectionLengths(string segmentFile)
    {
        var lengths = new long[SegmentFormat.SectionCount];
        using var fs = new FileStream(segmentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var footer = new byte[SegmentFormat.FooterSize];
        fs.Seek(-footer.Length, SeekOrigin.End);
        fs.ReadExactly(footer);
        int tocLength = BitConverter.ToInt32(footer, 0);
        var toc = new byte[tocLength];
        fs.Seek(-footer.Length - tocLength, SeekOrigin.End);
        fs.ReadExactly(toc);
        for (int i = 0; i + SegmentFormat.TocEntrySize <= toc.Length; i += SegmentFormat.TocEntrySize)
        {
            var e = SegmentFormat.TocEntry.Read(toc.AsSpan(i));
            if ((int)e.Id < lengths.Length) lengths[(int)e.Id] += e.Length;
        }
        return lengths;
    }

    static long[] SectionsOf(string folder)
    {
        var total = new long[SegmentFormat.SectionCount];
        foreach (var f in Directory.EnumerateFiles(folder, "seg-*.rpc"))
        {
            var s = SectionLengths(f);
            for (int i = 0; i < total.Length; i++) total[i] += s[i];
        }
        return total;
    }

    static long FolderBytes(string folder) => Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("reading", StringComparison.OrdinalIgnoreCase)).Sum(f => new FileInfo(f).Length);

    static double Percentile(IEnumerable<double> values, double p) => Pct(values.OrderBy(x => x).ToList(), p);

    static object Analyse(string content, string work, List<ReadLog> reads, Action<string> say)
    {
        using var store = ContentStore.Open(content, readOnly: true);
        using var view = store.Acquire();
        var stats = view.Stats;
        var docs = new List<(DocRow Row, string Path, StoredDoc Stored, int RawLength)>();
        foreach (var (r, row) in view.RowsByKey())
        {
            var seg = view.Segments[r.Segment];
            var stored = seg.ReadStored(r.Doc) ?? new StoredDoc("", [], null);
            docs.Add((row, seg.ReadPath(r.Doc) ?? "", stored, seg.ReadStoredRaw(r.Doc).Length));
        }
        var actual = SectionsOf(content);
        long actualBytes = store.BytesOnDisk;
        say($"index on disk: {actualBytes:N0} B in {view.Segments.Count} segments: text {actual[(int)SegmentFormat.Section.Text]:N0}, postings {actual[(int)SegmentFormat.Section.Postings]:N0}, "
            + $"dict {actual[(int)SegmentFormat.Section.Dict]:N0} + idx {actual[(int)SegmentFormat.Section.DictIndex]:N0}, rows {actual[(int)SegmentFormat.Section.Docs]:N0}, "
            + $"paths {actual[(int)SegmentFormat.Section.Paths] + actual[(int)SegmentFormat.Section.PathIndex]:N0}, meta {actual[(int)SegmentFormat.Section.Meta]:N0}");

        // Each kind alone, rebuilt from the same stored text into one segment (and every kind together, likewise): what
        // each kind costs in postings and dictionary, and what the multi-segment index costs over one merged segment.
        var volumes = view.Volumes;
        (long[] Sections, long Bytes, long Terms) Rebuild(string name, IEnumerable<(DocRow Row, string Path, StoredDoc Stored, int RawLength)> subset)
        {
            string folder = Path.Combine(work, "rebuild-" + name);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            using var s = ContentStore.Open(folder);
            var map = volumes.ToDictionary(v => v.Index, v => s.VolumeIndex(v.Key, v.DisplayRoot));
            using (var batch = s.BeginBatch())
            {
                foreach (var d in subset) batch.Add(d.Row with { Volume = map.GetValueOrDefault(d.Row.Volume) }, d.Path, d.Stored);
                if (batch.Count == 0) return (new long[SegmentFormat.SectionCount], 0, 0);
                batch.Commit().View.Dispose();
            }
            long terms = 0;
            using (var v = s.Acquire())
                foreach (var seg in v.Segments)
                    seg.Terms(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, (t, in p) => { terms++; return true; }, default);
            return (SectionsOf(folder), s.BytesOnDisk, terms);
        }

        var rows = new List<KindRow>();
        foreach (var kind in Kinds)
        {
            var mine = docs.Where(d => d.Row.Kind == kind).ToList();
            if (mine.Count == 0) continue;
            var (sec, bytes, terms) = Rebuild(kind.ToString(), mine);
            long pairs = 0;
            foreach (var d in mine)
            {
                var sink = new DistinctSink();
                ContentTokenizer.Tokenize(d.Stored.Body, sink);
                foreach (var (field, value) in d.Stored.Fields) ContentTokenizer.TokenizeField(field, value, sink);
                pairs += sink.Count;
            }
            var kr = reads.Where(r => r.Kind == kind && !r.Retry && !r.Offline).ToList();
            double? med = null, p95 = null, pooled = null, medKb = null;
            if (kind == ContentKind.Pdf && kr.Count > 0)
            {
                var ratios = kr.Where(r => r.Size > 0).Select(r => 100.0 * r.BytesRead / r.Size).ToList();
                med = Percentile(ratios, 0.5);
                p95 = Percentile(ratios, 0.95);
                pooled = 100.0 * kr.Sum(r => (double)r.BytesRead) / Math.Max(1, kr.Sum(r => (double)r.Size));
                medKb = Percentile(kr.Select(r => r.BytesRead / 1024.0), 0.5);
            }
            var st = stats.GetValueOrDefault(kind);
            rows.Add(new KindRow(kind.ToString(), mine.Count, mine.Count(d => (d.Row.Flags & DocFlags.HasText) != 0), mine.Sum(d => (long)d.RawLength), pairs, terms,
                sec[(int)SegmentFormat.Section.Postings], sec[(int)SegmentFormat.Section.Dict] + sec[(int)SegmentFormat.Section.DictIndex],
                sec[(int)SegmentFormat.Section.Docs], sec[(int)SegmentFormat.Section.Paths] + sec[(int)SegmentFormat.Section.PathIndex], sec[(int)SegmentFormat.Section.Meta],
                bytes, kr.Count > 0 ? kr.Average(r => r.ElapsedMs) : 0, kr.Count > 0 ? kr.Average(r => r.WallMs) : 0, kr.Count > 0 ? Percentile(kr.Select(r => r.WallMs), 0.95) : 0,
                kr.Count > 0 ? kr.Average(r => (double)r.BytesRead) : 0, mine.Average(d => (double)d.Row.Size),
                mine.GroupBy(d => d.Row.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()), med, p95, pooled, medKb));
        }
        var (allSec, allBytes, allTerms) = Rebuild("all", docs);
        long allPairs = rows.Sum(r => r.Pairs);
        say("");
        say("kind      files  w/text   stored B/f  words/f  post+dict B/f  rows+paths+meta B/f  total B/f   ms/f (wall)  p95 ms  read KB/f  file KB");
        foreach (var r in rows)
        {
            double n = r.Files;
            say($"{r.Kind,-8} {r.Files,6:N0} {r.WithText,7:N0} {r.StoredBytes / n,11:F0} {r.Pairs / n,8:F0} {(r.PostingsBytes + r.DictBytes) / n,14:F0} {(r.RowsBytes + r.PathsBytes + r.MetaBytes) / n,20:F0} "
                + $"{r.SegmentBytes / n,10:F0} {r.MsPerFile,6:F1} ({r.WallMsPerFile,5:F1}) {r.P95Ms,7:F0} {r.BytesReadPerFile / 1024,10:F1} {r.MeanFileBytes / 1024,8:F0}"
                + (r.PdfReadMedianPct is { } m ? $"   PDF read/size median {m:F2}%, p95 {r.PdfReadP95Pct:F1}%, pooled {r.PdfReadPooledPct:F2}%, median {r.PdfReadMedianKB:F0} KB" : "")
                + "   " + string.Join(", ", r.Statuses.Select(s => $"{s.Key} {s.Value}")));
        }
        say($"all kinds in one segment: {allBytes:N0} B, {allTerms:N0} terms for {allPairs:N0} pairs (Heaps fit {ContentEstimator.HeapsK}·P^0.6 = {ContentEstimator.HeapsK * Math.Pow(allPairs, ContentEstimator.HeapsBeta):N0}); "
            + $"postings {allSec[(int)SegmentFormat.Section.Postings]:N0} B = {allSec[(int)SegmentFormat.Section.Postings] / (double)Math.Max(1, allPairs):F2} B/pair "
            + $"(plus inline in DICT), dict {allSec[(int)SegmentFormat.Section.Dict] + allSec[(int)SegmentFormat.Section.DictIndex]:N0} B = "
            + $"{(allSec[(int)SegmentFormat.Section.Dict] + allSec[(int)SegmentFormat.Section.DictIndex]) / (double)Math.Max(1, allTerms):F2} B/term; "
            + $"the live index is {actualBytes:N0} B in {view.Segments.Count} segments ({actualBytes / (double)Math.Max(1, allBytes):F2}×)");
        return new
        {
            ActualBytes = actualBytes, Segments = view.Segments.Count,
            ActualSections = Enum.GetValues<SegmentFormat.Section>().Where(s => (int)s < actual.Length).ToDictionary(s => s.ToString(), s => actual[(int)s]),
            OneSegment = new { Bytes = allBytes, Terms = allTerms, Pairs = allPairs,
                Sections = Enum.GetValues<SegmentFormat.Section>().Where(s => (int)s < allSec.Length).ToDictionary(s => s.ToString(), s => allSec[(int)s]) },
            Kinds = rows,
            Stats = stats.ToDictionary(s => s.Key.ToString(), s => s.Value),
        };
    }

    // ───────────────────────────── calibration repeatability ─────────────────────────────

    /// <summary>Setup's census and calibration, run several times in one process (the first pays the readers' JIT).</summary>
    static int CalibrateRepeat(Options o)
    {
        var folders = o.All("folders");
        var all = new ContentSettings { Mode = ContentMode.DocumentsAndPictures, ModeExplicit = true, UseMyFolders = false, Folders = [.. folders] };
        var census = ContentEstimator.CountFoldersAsync(folders, all, TimeSpan.FromSeconds(20), null, CancellationToken.None).GetAwaiter().GetResult();
        string marker = Path.Combine(Path.GetFullPath(o.Get("work")!), "calibration.reading");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        for (int i = 0; i < o.Int("times", 3); i++)
        {
            var sw = Stopwatch.StartNew();
            var c = ContentEstimator.Calibrate(census, ContentServices.Files(), TimeSpan.FromSeconds(o.Int("seconds", 4)), 10, CancellationToken.None, markerPath: marker);
            var docs = ContentEstimator.Estimate(census, c, ContentMode.Documents, all);
            var pics = ContentEstimator.Estimate(census, c, ContentMode.DocumentsAndPictures, all);
            Console.WriteLine($"#{i + 1} {sw.Elapsed.TotalSeconds:F1} s: {Costs(c)}");
            Console.WriteLine($"    documents {docs.IndexBytes / 1e6:F1} MB {docs.FirstPass.TotalMinutes:F2} min; with pictures {pics.IndexBytes / 1e6:F1} MB {pics.FirstPass.TotalMinutes:F2} min");
        }
        return 0;
    }

    /// <summary>Setup's census samples per kind, in calibration order, into a file (kind TAB path).</summary>
    static int Samples(Options o)
    {
        var folders = o.All("folders");
        var all = new ContentSettings { Mode = ContentMode.DocumentsAndPictures, ModeExplicit = true, UseMyFolders = false, Folders = [.. folders] };
        var census = ContentEstimator.CountFoldersAsync(folders, all, TimeSpan.FromSeconds(20), null, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllLines(o.Get("out")!, census.Samples.SelectMany(k => k.Value.Select((p, i) => $"{k.Key}	{i}	{p}")), new UTF8Encoding(false));
        return 0;
    }

    /// <summary>Each calibration sample's time in reading order, round-robin over kinds as Calibrate goes (first-use costs).</summary>
    static int JitDecay(Options o)
    {
        var folders = o.All("folders");
        var all = new ContentSettings { Mode = ContentMode.DocumentsAndPictures, ModeExplicit = true, UseMyFolders = false, Folders = [.. folders] };
        var census = ContentEstimator.CountFoldersAsync(folders, all, TimeSpan.FromSeconds(20), null, CancellationToken.None).GetAwaiter().GetResult();
        var source = ContentServices.Files();
        var times = Kinds.ToDictionary(k => k, _ => new List<double>());
        var cpus = Kinds.ToDictionary(k => k, _ => new List<double>());
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var t in times.Values) t.Clear();
            foreach (var t in cpus.Values) t.Clear();
            int most = census.Samples.Values.Max(v => v.Count);
            for (int i = 0; i < most; i++)
                foreach (var kind in Kinds)
                {
                    if (!census.Samples.TryGetValue(kind, out var paths) || i >= paths.Count) continue;
                    var request = new ExtractRequest(paths[i], kind, -1, 0, false, false, 0, ExtractLimits.Local);
                    long t0 = Stopwatch.GetTimestamp();
                    var cpu0 = ContentNative.ThreadCpuTime();
                    source.Read(request, CancellationToken.None);
                    times[kind].Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    cpus[kind].Add((ContentNative.ThreadCpuTime() - cpu0).TotalMilliseconds);
                }
            Console.WriteLine(pass == 0 ? "first pass (fresh process):" : "second pass (same files, same process):");
            foreach (var (kind, t) in times)
                if (t.Count > 0)
                    Console.WriteLine($"  {kind,-8} n={t.Count,2} mean {t.Average(),6:F1} (cpu {cpus[kind].Average(),5:F1})  without first 1: {t.Skip(1).DefaultIfEmpty().Average(),6:F1}  without first 5: {t.Skip(5).DefaultIfEmpty().Average(),6:F1}  "
                        + string.Join(" ", t.Take(12).Select(x => x.ToString("F1", CultureInfo.InvariantCulture))));
        }
        return 0;
    }

    /// <summary>Reader CPU alone: files read into memory once, then extracted again and again from memory (no I/O, no antivirus).</summary>
    static int ReaderCpu(Options o)
    {
        foreach (var path in o.Positional)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var kind = ContentTypes.KindOf(Path.GetFileName(path));
            var times = new List<double>();
            ExtractedDoc? doc = null;
            for (int i = 0; i < o.Int("times", 20); i++)
            {
                var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
                long t0 = Stopwatch.GetTimestamp();
                using (var source = new MemoryByteSource(bytes, budget)) doc = ContentReaders.Extract(source, ext, kind, budget);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            Console.WriteLine($"{bytes.Length / 1024,6} KB {doc!.Status,-9} body {doc.Body.Length,8:N0} chars  first {times[0],7:F1} ms  then median {Percentile(times.Skip(1), 0.5),7:F2} ms  ({bytes.Length / 1048576.0 / (Percentile(times.Skip(1), 0.5) / 1000):F0} MB/s)  {Path.GetFileName(path)}");
        }
        return 0;
    }

    // ───────────────────────────── search latency ─────────────────────────────

    static int SearchLatency(Options o)
    {
        string work = Path.GetFullPath(o.Get("work")!);
        string content = Path.Combine(work, "Content");
        int synthetic = o.Int("synthetic", 1_000_000);
        var report = new StringBuilder();
        void Say(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }
        using var quiet = Drives.QuietErrors();
        var engine = new SearchEngine();

        // The 1M-name baseline: SearchPerfTests' synthetic tree alone, no text index.
        var baseline = new FileIndex();
        AddSynthetic(baseline, synthetic);
        engine.Search(baseline, "warmup", new SearchOptions(), SortKey.Name, false, default);
        Say($"baseline: {baseline.LiveCount:N0} synthetic names, no text index");
        foreach (var q in (string[])["12345", "alpha", "report", "inv", "*.pdf"])
        {
            var times = Enumerable.Range(0, 7).Select(i => Time(engine, baseline, q, new SearchOptions(), out _)).ToList();
            Say($"  {q,-28} median {Percentile(times, 0.5),7:F1} ms  max {times.Max(),7:F1} ms");
        }

        var scope = NamesFor(o, synthetic);
        Say($"names: {scope.Entries:N0} entries ({scope.ScanMs / 1000:F1} s to scan), {synthetic:N0} of them synthetic");
        // The same names before the text index is attached.
        engine.Search(scope.Index, "warmup", new SearchOptions(), SortKey.Name, false, default);
        foreach (var q in (string[])["12345", "alpha", "report", "inv", "*.pdf"])
        {
            var times = Enumerable.Range(0, 7).Select(i => Time(engine, scope.Index, q, new SearchOptions(), out _)).ToList();
            Say($"  names only {q,-17} median {Percentile(times, 0.5),7:F1} ms  max {times.Max(),7:F1} ms");
        }
        var source = new LoggingSource(ContentServices.Files());
        var ci = new ContentIndex(() => ContentStore.Open(content), source, new ReadyGate(), new Power(false), new ContentIndexOptions
        {
            Folder = content,
            OpenReadOnly = () => ContentStore.Open(content, readOnly: true),
        });
        var sw = Stopwatch.StartNew();
        ci.Start(new Names(scope.Index), SettingsFor(scope, ContentMode.DocumentsAndPictures));
        var (upToDate, status, _) = WaitUpToDate(ci, sw, Say, TimeSpan.FromMinutes(30));
        Say($"text index attached and up to date in {upToDate.TotalSeconds:F1} s ({status.Total:N0} documents, {source.Log.Count} files read again since the build, index {status.IndexBytes / 1e6:F1} MB)");

        var off = new SearchOptions();
        var on = new SearchOptions { MatchContent = true };
        // JIT and first-use costs, on words no query below uses.
        Time(engine, scope.Index, "content:zqxjv", off, out _);
        Time(engine, scope.Index, "zqxjv", on, out _);
        Say("");
        Say("query                          matchContent  cold ms (pending → complete)   warm median / p95 ms   results (by name)");
        var queries = new List<(string Query, SearchOptions Options)>
        {
            ("12345", off), ("alpha", off), ("report", off), ("12345", on), ("alpha", on), ("report", on),
            ("content:invoice", off), ("content:inv*", off), ("inv", on), ("rep", on), ("pro", on),
            ("content:\"terms and conditions\"", off), ("content:\"annual report\"", off),
            ("dpi:<300", off), ("colormode:cmyk", off), ("cmyk: width:>2000", off), ("content:invoice ext:pdf", off),
        };
        foreach (var (q, opt) in queries)
        {
            double cold = Time(engine, scope.Index, q, opt, out var first);
            double complete = cold;
            var last = first;
            var pending = Stopwatch.StartNew();
            while (last.ContentPending && pending.ElapsedMilliseconds < 10_000)
            {
                Thread.Sleep(20);
                complete += 20 + Time(engine, scope.Index, q, opt, out last);
            }
            var warm = Enumerable.Range(0, 15).Select(i => Time(engine, scope.Index, q, opt, out last)).ToList();
            Say($"{q,-32} {(opt.MatchContent ? "on" : "off"),-5} {cold,9:F1}{(first.ContentPending ? $" (pending → {complete:F0})" : ""),-22} {Percentile(warm, 0.5),9:F1} / {Percentile(warm, 0.95),-8:F1} "
                + $"{last.Ids.Length,9:N0}{(last.NameMatches >= 0 ? $" ({last.NameMatches:N0})" : "")}{(last.Error != null ? "  " + last.Error : "")}");
        }
        ci.Stop(TimeSpan.FromSeconds(5));
        ci.Dispose();
        File.WriteAllText(Path.Combine(work, "search-report.txt"), report.ToString(), new UTF8Encoding(false));
        return 0;
    }

    static double Time(SearchEngine engine, FileIndex index, string query, SearchOptions options, out SearchResults results)
    {
        long t0 = Stopwatch.GetTimestamp();
        results = engine.Search(index, query, options, SortKey.Name, false, default);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
