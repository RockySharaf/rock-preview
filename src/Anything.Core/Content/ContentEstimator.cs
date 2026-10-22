using System.Diagnostics;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Core.Indexing;

namespace Anything.Core.Content;

/// <summary>
/// Counts candidates, measures a real sample, and works out size and time per option (§9). Every entry point copies the
/// settings (ContentSettings.Clone) before it first awaits or returns. Size = Σ n·(stored + 1.37·words + 85 + 44 for
/// pictures and design files) + 10·60·P^0.6 bytes (P = Σ n·words, Heaps' law for the dictionary), where text's stored
/// text is a ratio once sampled (<see cref="PricedByRatio"/>); time = 1.5·Σ (n·ms +
/// network files·(per-file overhead + bytes read / min(throttle, 8 MB/s))). Censuses count every kind, so one census
/// prices every choice; the choice's mode and kinds pick what is added up. Checked against real first passes on the
/// brief's PC (2026-10-21, tools/ContentProbe build): with the index's own per-kind averages the formula is within 6% of
/// the index built (My folders 17,120 files, a 10,000-file sample of E:\).
/// </summary>
public static class ContentEstimator
{
    /// <summary>
    /// F: background mode, commits, merges, the duty cycle and pauses make the first pass slower than the sum of reads.
    /// Measured (R9): 1.04–1.39 × the reads' own time while the user is idle (documents 1.04–1.18, pictures 1.10–1.39),
    /// 2.08 × while the user is active (the 50% duty cycle); 1.5 sits between the two (their geometric mean is 1.55).
    /// </summary>
    public const double TimeFactor = 1.5;
    /// <summary>The prior counts as this many samples when blended with a measured average.</summary>
    internal const int PseudoCount = 5;
    /// <summary>A kind with this many documents in the index is priced from the index's own statistics.</summary>
    internal const int IndexStatsMinimum = 200;
    internal const double PostingsBytesPerPair = 1.37, FixedBytesPerFile = 85, PictureRecordBytes = 44;
    /// <summary>
    /// Distinct terms ≈ HeapsK·pairs^0.6, at about 10 bytes each (front-coded, docFreq ≤ 2 inline). The real tokenizer's
    /// terms (words, code parts and compounds, field-scoped terms) on this PC: 431,829 at 2,728,065 pairs (My folders) and
    /// 324,786 at 1,387,568 (the E:\ sample), K = 59 and 67 at β = 0.6; 47, fitted to plain words, gave 26–43% too few.
    /// </summary>
    internal const double HeapsK = 60, HeapsBeta = 0.6, DictionaryBytesPerTerm = 10;
    /// <summary>Per network file, before calibration: open, stat, close round trips.</summary>
    internal const double NetworkOverheadPriorMs = 5;
    /// <summary>The network byte source's first reads (they double only on sequential access).</summary>
    internal const long NetworkBlockBytes = 16 * 1024;
    /// <summary>
    /// Average pages of a PDF before calibration: a PDF costs about 2 + pages blocks over the network (measured over
    /// \\localhost\E$ on 600 PDFs: 10 KB + 19 KB per page). The brief's figure; this PC's PDFs average 38 pages (E:\'s
    /// scanned boxes 41, My folders 19), so a share like E:\ reads about 3 times more than this prior says.
    /// </summary>
    internal const double PdfPagesPrior = 13;
    /// <summary>The fastest a share is assumed to be read, whatever the throttle allows.</summary>
    internal const double MaxNetworkBytesPerSecond = 8 << 20;
    /// <summary>
    /// A file counts at most this many bytes in the ratio estimator of stored text: the index keeps at most 1 MB of a
    /// document's text (ExtractLimits.MaxTextChars), so a 50 MB log gives it what a 1 MB one does.
    /// </summary>
    internal const long RatioCapBytes = 1 << 20;
    const int SamplesPerKind = 40, ReservoirPerKind = 160;
    const FileAttributes CloudBits = (FileAttributes)(0x1000 | 0x40000 | 0x400000);

    static readonly ContentKind[] AllKinds = [ContentKind.Pdf, ContentKind.Office, ContentKind.Text, ContentKind.Mail, ContentKind.Design, ContentKind.Picture];

    /// <summary>
    /// Costs measured on the brief's PC (2026-10-21), averaged over all files of a kind: the real readers, tokenizer and
    /// store on a first pass (tools/ContentProbe build), pooled by population over My folders (17,120 candidates) and E:\
    /// (67,135, priced from a 10,000-file sample); ms are wall time per file, the antivirus's scan on first open included.
    /// PDF keeps the brief's figures, which E:\'s print-like PDFs confirm (57 ms, 611 B, 59 terms; My folders' office PDFs
    /// hold five times more text, which calibration picks up). Office: 1,416 terms and 12,951 B stored per file (E:\ 1,066
    /// and 6,741, My folders 1,715 and 18,256). Text: 454 terms, 3,103 B (2,737–4,784) and My folders' 12 ms: E:\'s
    /// 54 ms comes from its saved web pages, each waiting about 0.6 ms per KB for the antivirus's scan on first open (the
    /// reader takes them at 110 MB/s and more from memory), which calibration measures where it applies; 47 ms pooled
    /// would put a priors-only time 275% over for My folders and 38% over for E:\, against 69% and 2%. Pictures: 5.3 ms
    /// (My folders 1.3, E:\ 6.4). Design keeps the brief's figures (14 files here, none with text); mail mirrors text
    /// (no mail on this PC).
    /// </summary>
    public static ContentCalibration Priors { get; } = new(new Dictionary<ContentKind, KindCost>
    {
        [ContentKind.Pdf] = new(58, 700, 52, BytesReadPrior(ContentKind.Pdf, 11.1 * (1 << 20))),
        [ContentKind.Office] = new(10, 13_000, 1_420, BytesReadPrior(ContentKind.Office, 0.9 * (1 << 20))),
        [ContentKind.Text] = new(12, 3_100, 450, BytesReadPrior(ContentKind.Text, 200 * 1024)),
        [ContentKind.Mail] = new(12, 3_100, 450, BytesReadPrior(ContentKind.Mail, 394 * 1024)),
        [ContentKind.Design] = new(11, 3700, 245, BytesReadPrior(ContentKind.Design, 2.9 * (1 << 20))),
        [ContentKind.Picture] = new(5.3, 6, 2, BytesReadPrior(ContentKind.Picture, 200 * 1024)),
    }, AllKinds.ToDictionary(k => k, _ => 0));

    /// <summary>
    /// Bytes read from one file of a kind and average size, before calibration: PDF 2 + pages blocks of 16 KB (text
    /// objects only), Office a quarter of the file (text XML only), text up to 4 MB, mail up to 8 MB, design files up to
    /// 1 MB, pictures 16 KB of headers; never more than the network read limit.
    /// </summary>
    internal static double BytesReadPrior(ContentKind kind, double averageSize)
    {
        double size = Math.Max(0, averageSize);
        double r = kind switch
        {
            ContentKind.Pdf => Math.Min(size, (2 + PdfPagesPrior) * NetworkBlockBytes),
            ContentKind.Office => size * 0.25,
            ContentKind.Text => Math.Min(size, 4 << 20),
            ContentKind.Mail => Math.Min(size, 8 << 20),
            ContentKind.Design => Math.Min(size, 1 << 20),
            ContentKind.Picture => Math.Min(size, 16 * 1024),
            _ => 0,
        };
        return Math.Min(r, ExtractLimits.Network.MaxBytesRead);
    }

    /// <summary>
    /// Whether a kind's stored text is priced as a ratio: its samples' stored bytes per file byte (each file counted at most
    /// 1 MB) times the census's bytes counted the same way, the per-file figure that gives blended with the prior as any
    /// other (BlendOne); the word and per-file terms stay per file. Text only. Replaying the calibration's stratified
    /// sample on this PC's files (2,000 draws, body length for stored text), it cut the spread of text's estimate from 28%
    /// to 12% at 40 samples and from 48% to 19% at 22 (My folders; E:\ 75% to 73%, 95% to 92%). PDF, Office and design
    /// files gain nothing (their text doesn't follow their size: pictures, fonts and layout make it), and against bytes
    /// not capped it was worse for every kind, 1.2 to 4 times too high with a spread of 70–640%: a tail of big files holds
    /// the bytes, not the text.
    /// </summary>
    internal static bool PricedByRatio(ContentKind kind) => kind == ContentKind.Text;

    /// <summary>A census with nothing in it.</summary>
    internal static ContentCensus EmptyCensus(bool complete)
        => new(new Dictionary<ContentKind, KindTally>(), new Dictionary<ContentKind, IReadOnlyList<string>>(), 0, complete);

    // ───────────────────────────── estimate ─────────────────────────────

    /// <summary>Size, time and network bytes of one choice.</summary>
    public static ContentEstimate Estimate(ContentCensus census, ContentCalibration calibration, ContentMode mode, ContentSettings settings)
    {
        var s = settings.Clone();
        if (mode == ContentMode.NamesOnly) return new ContentEstimate(mode, 0, 0, 0, TimeSpan.Zero, 0, !census.Complete);
        double throttle = Math.Min(Math.Max(64, s.NetworkKilobytesPerSecond) * 1024.0, MaxNetworkBytesPerSecond);
        double overheadMs = calibration.NetworkOverheadMs;
        double bytes = 0, pairs = 0, ms = 0, networkBytes = 0;
        int documents = 0, pictures = 0;
        foreach (var kind in AllKinds)
        {
            if (!census.Kinds.TryGetValue(kind, out var t) || t.Files <= 0) continue;
            bool enabled = kind == ContentKind.Picture ? mode == ContentMode.DocumentsAndPictures : (s.Kinds & ContentTypes.FlagOf(kind)) != 0;
            if (!enabled) continue;
            var cost = CostOf(calibration, kind);
            double n = t.Files, nNet = Math.Min(t.NetworkFiles, t.Files);
            double m = kind is ContentKind.Picture or ContentKind.Design ? PictureRecordBytes : 0;
            double stored = cost.StoredBytesPerFile;
            if (PricedByRatio(kind) && t.CappedBytes > 0 && calibration.StoredRatios.TryGetValue(kind, out var ratio) && ratio is { Samples: > 0, CappedFileBytes: > 0 })
                stored = BlendOne(ratio.Samples, ratio.StoredBytes / ratio.CappedFileBytes * (t.CappedBytes / n), Priors.Costs[kind].StoredBytesPerFile);
            bytes += n * (stored + PostingsBytesPerPair * cost.WordsPerFile + FixedBytesPerFile + m);
            pairs += n * cost.WordsPerFile;
            double r = calibration.Measured.GetValueOrDefault(kind) > 0 || t.Files == 0
                ? cost.BytesReadPerFile
                : BytesReadPrior(kind, nNet > 0 && t.NetworkFiles > 0 ? t.NetworkBytes / (double)t.NetworkFiles : t.Bytes / (double)t.Files);
            ms += n * cost.MsPerFile + nNet * (overheadMs + r / throttle * 1000);
            networkBytes += nNet * r;
            if (kind == ContentKind.Picture) pictures += t.Files;
            else documents += t.Files;
        }
        if (pairs > 0) bytes += DictionaryBytesPerTerm * HeapsK * Math.Pow(pairs, HeapsBeta);
        return new ContentEstimate(mode, documents, pictures, (long)Math.Round(bytes), TimeSpan.FromMilliseconds(TimeFactor * ms),
            (long)Math.Round(networkBytes), !census.Complete);
    }

    /// <summary>Documents larger than this are left out (TooBig at dispatch, so a census doesn't count them); 0 MB = no limit.</summary>
    static long MaxDocumentBytes(ContentSettings s) => s.MaxFileSizeMB > 0 ? (long)s.MaxFileSizeMB << 20 : long.MaxValue;

    /// <summary>
    /// A count cut short, scaled up to what its drive or share holds: every figure times usedBytes / listedBytes (now an
    /// estimate, no longer "at least"). Unchanged when the count was complete, either size is unknown, or nothing more is
    /// held than was listed. Meant for a drive or share root (a folder's drive holds more than the folder).
    /// </summary>
    public static ContentEstimate Extrapolate(ContentEstimate partial, long listedBytes, long usedBytes)
    {
        if (!partial.AtLeast || listedBytes <= 0 || usedBytes <= listedBytes) return partial;
        double f = usedBytes / (double)listedBytes;
        static int Times(int n, double f) => (int)Math.Min(int.MaxValue, Math.Round(n * f));
        static long TimesL(long n, double f) => n * f >= long.MaxValue ? long.MaxValue : (long)Math.Round(n * f);
        return partial with
        {
            Documents = Times(partial.Documents, f),
            Pictures = Times(partial.Pictures, f),
            IndexBytes = TimesL(partial.IndexBytes, f),
            FirstPass = TimeSpan.FromTicks(TimesL(partial.FirstPass.Ticks, f)),
            NetworkBytesToRead = TimesL(partial.NetworkBytesToRead, f),
            AtLeast = false,
        };
    }

    static KindCost CostOf(ContentCalibration calibration, ContentKind kind)
        => calibration.Costs.TryGetValue(kind, out var c) ? c : Priors.Costs.GetValueOrDefault(kind);

    /// <summary>The calibration with the index's own per-kind statistics for kinds it holds 200 or more documents of.</summary>
    internal static ContentCalibration WithIndexStats(ContentCalibration calibration, IReadOnlyDictionary<ContentKind, KindStats> stats)
    {
        var costs = new Dictionary<ContentKind, KindCost>(calibration.Costs);
        var measured = new Dictionary<ContentKind, int>(calibration.Measured);
        var ratios = new Dictionary<ContentKind, StoredRatio>(calibration.StoredRatios);
        foreach (var (kind, st) in stats)
        {
            if (st.Docs < IndexStatsMinimum || kind == ContentKind.None) continue;
            double d = st.Docs;
            costs[kind] = new KindCost(st.MsTotal / d, st.StoredBytes / d, st.Pairs / d, st.BytesRead / d);
            measured[kind] = (int)Math.Min(int.MaxValue, st.Docs);
            ratios.Remove(kind); // the index's own average over 200 or more documents
        }
        return calibration with { Costs = costs, Measured = measured, StoredRatios = ratios };
    }

    // ───────────────────────────── calibration ─────────────────────────────

    /// <summary>
    /// Reads samples with the real readers, tokenizer and encoder (nothing written), time-boxed; blends with the priors.
    /// Each sample is read inside a crash marker the estimator keeps itself (a file under %TEMP%, since Setup has no
    /// index): a file named by a leftover marker is skipped, and when the previous run left one it returns Priors
    /// without sampling.
    /// </summary>
    public static ContentCalibration Calibrate(ContentCensus census, IContentSource source, TimeSpan budget, CancellationToken ct)
        => Calibrate(census, source, budget, maxNetworkSamples: 10, ct);

    /// <summary>
    /// Calibrate with its crash marker: <paramref name="marker"/> is the text index's own (Settings: a file that takes the
    /// app down is then recorded Crashed by the index and never read by it either; its culprits are skipped here), keyed
    /// by <paramref name="keyOf"/> (document keys); without one, the file at <paramref name="markerPath"/> (Setup, names
    /// only: %TEMP% by default), keyed by "path:" + the full path.
    /// </summary>
    internal static ContentCalibration Calibrate(ContentCensus census, IContentSource source, TimeSpan budget, int maxNetworkSamples,
        CancellationToken ct, string? markerPath = null, CrashMarker? marker = null, Func<string, ulong>? keyOf = null)
    {
        keyOf ??= PathKey;
        if (marker != null)
        {
            var shared = new MarkerLease(marker, owned: false);
            try { return Sample(census, source, budget, maxNetworkSamples, shared, [.. marker.Carried.Select(e => e.Key)], keyOf, ct); }
            finally { shared.Release(); }
        }
        markerPath ??= Path.Combine(Path.GetTempPath(), "Rock Preview calibration.reading");
        var own = CrashMarker.Open(markerPath, out var leftovers) ?? CrashMarker.InMemory();
        var lease = new MarkerLease(own, owned: true);
        try
        {
            // The last calibration stopped in the middle of a file: no sampling this time (that file is remembered and
            // skipped the next time).
            if (own.FreshCulprits > 0) return Priors;
            var skip = leftovers.Select(e => e.Key).ToHashSet();
            return Sample(census, source, budget, maxNetworkSamples, lease, skip, keyOf, ct);
        }
        finally { lease.Release(); }
    }

    static ulong PathKey(string path) => ContentKeys.Of("path:", path.ToUpperInvariant());

    /// <summary>
    /// The calibration's crash marker and its users: the calibration, and each read on its guarded thread. The last one
    /// out closes it (when the calibration opened it), so a read left behind past its time keeps naming its file until
    /// it really ends; a crash meanwhile is still blamed on it.
    /// </summary>
    sealed class MarkerLease(CrashMarker marker, bool owned)
    {
        int _users = 1;
        /// <summary>The marker.</summary>
        public CrashMarker Marker => marker;
        /// <summary>One more user (a read starting on its guarded thread).</summary>
        public void AddUser() => Interlocked.Increment(ref _users);
        /// <summary>A user is done; the last one closes the marker when the calibration opened it.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _users) == 0 && owned) marker.Dispose();
        }
    }

    sealed class Totals
    {
        public int N;
        public double Ms, Stored, Words, Bytes, Capped;
    }

    static ContentCalibration Sample(ContentCensus census, IContentSource source, TimeSpan budget, int maxNetworkSamples, MarkerLease lease,
        HashSet<ulong> skip, Func<string, ulong> keyOf, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var totals = AllKinds.ToDictionary(k => k, _ => new Totals());
        var warmed = new HashSet<ContentKind>();
        var next = AllKinds.ToDictionary(k => k, _ => 0);
        int networkSamples = 0, networkN = 0;
        double networkOverhead = 0;
        const double throttle = MaxNetworkBytesPerSecond;
        bool any = true;
        while (any && watch.Elapsed < budget)
        {
            any = false;
            foreach (var kind in AllKinds)
            {
                ct.ThrowIfCancellationRequested();
                if (!census.Samples.TryGetValue(kind, out var paths) || next[kind] >= paths.Count) continue;
                any = true;
                var remaining = budget - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                string path = paths[next[kind]++];
                ulong key = keyOf(path);
                if (skip.Contains(key)) continue;
                bool network = FileByteSource.IsNetworkPath(path);
                if (network && networkSamples >= maxNetworkSamples) continue;
                if (!source.TryStat(path, out long size, out long modified, out var attributes) || (attributes & CloudBits) != 0) continue;
                // No free marker slot: nothing is read without crash protection.
                var marker = lease.Marker;
                int slot = marker.Acquire();
                if (slot < 0) return Blend(census, totals, networkN, networkOverhead);
                if (network) networkSamples++;
                var request = new ExtractRequest(path, kind, size, modified, network, false, 0, network ? ExtractLimits.Network : ExtractLimits.Local);
                uint epoch = marker.Enter(slot, new MarkerEntry(key, 0, MarkerLane.OnDemand, size, modified));
                lease.AddUser();
                // The slot is cleared and released when the read really ends, which is after this call when it hangs.
                var result = RunGuarded(token =>
                {
                    var cpu0 = ContentNative.ThreadCpuTime();
                    var wall = Stopwatch.StartNew();
                    var read = source.Read(request, token);
                    return (read, wall.Elapsed, ContentNative.ThreadCpuTime() - cpu0);
                }, (int)Math.Min(int.MaxValue, remaining.TotalMilliseconds + 1000), ct, () =>
                {
                    marker.Leave(slot, epoch);
                    marker.Release(slot);
                    lease.Release();
                });
                if (!result.Finished) return Blend(census, totals, networkN, networkOverhead); // a read hung: stop measuring
                if (!result.Ok) continue;
                var (read, wallTime, cpuTime) = result.Value;
                if (read.Retry || read.Offline) continue;
                // The first file of each kind warms its reader up (JIT); it isn't counted.
                if (warmed.Add(kind)) continue;
                var doc = read.Doc;
                var t = totals[kind];
                t.N++;
                t.Ms += network ? cpuTime.TotalMilliseconds : wallTime.TotalMilliseconds;
                t.Stored += StoredBytes(doc);
                t.Capped += Math.Min(Math.Max(0, size), RatioCapBytes);
                t.Words += DistinctWords(doc);
                t.Bytes += doc.BytesRead;
                if (network)
                {
                    // Per-file network overhead: the wall time not spent computing nor transferring at the throttle.
                    networkOverhead += Math.Max(0, (wallTime - cpuTime).TotalMilliseconds - doc.BytesRead / throttle * 1000);
                    networkN++;
                }
            }
        }
        return Blend(census, totals, networkN, networkOverhead);
    }

    static ContentCalibration Blend(ContentCensus census, Dictionary<ContentKind, Totals> totals, int networkN, double networkOverhead)
    {
        var costs = new Dictionary<ContentKind, KindCost>();
        var measured = new Dictionary<ContentKind, int>();
        var ratios = new Dictionary<ContentKind, StoredRatio>();
        foreach (var kind in AllKinds)
        {
            var prior = Priors.Costs[kind];
            double priorBytes = census.Kinds.TryGetValue(kind, out var tally) && tally.Files > 0
                ? BytesReadPrior(kind, tally.Bytes / (double)tally.Files) : prior.BytesReadPerFile;
            var t = totals[kind];
            measured[kind] = t.N;
            costs[kind] = t.N == 0 ? prior with { BytesReadPerFile = priorBytes } : new KindCost(
                BlendOne(t.N, t.Ms / t.N, prior.MsPerFile),
                BlendOne(t.N, t.Stored / t.N, prior.StoredBytesPerFile),
                BlendOne(t.N, t.Words / t.N, prior.WordsPerFile),
                BlendOne(t.N, t.Bytes / t.N, priorBytes));
            if (PricedByRatio(kind) && t.N > 0 && t.Capped > 0) ratios[kind] = new StoredRatio(t.N, t.Stored, t.Capped);
        }
        return new ContentCalibration(costs, measured)
        {
            StoredRatios = ratios,
            NetworkOverheadMs = networkN == 0 ? NetworkOverheadPriorMs : BlendOne(networkN, networkOverhead / networkN, NetworkOverheadPriorMs),
            NetworkMeasured = networkN,
        };
    }

    /// <summary>x = (n·x̄ + k·prior) / (n + k), k = 5: a handful of real files moves the estimate, a single odd one doesn't.</summary>
    internal static double BlendOne(int n, double mean, double prior) => (n * mean + PseudoCount * prior) / (n + PseudoCount);

    /// <summary>What a guarded read gave: Finished false = it hung and was left behind; Ok false = it threw.</summary>
    readonly record struct Guarded<T>(bool Finished, bool Ok, T Value);

    /// <summary>
    /// Runs a read on a thread of its own in background mode, with a token cancelled at the timeout (the reader stops at
    /// its next budget check); past it, its synchronous I/O is cancelled and, if it still runs, it is left behind.
    /// <paramref name="onEnd"/> runs once the read has really ended, on whichever thread is last.
    /// </summary>
    static Guarded<T> RunGuarded<T>(Func<CancellationToken, T> work, int timeoutMs, CancellationToken ct, Action onEnd)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(1, timeoutMs));
        var token = cts.Token;
        int users = 2; // this call and the thread
        void Release()
        {
            if (Interlocked.Decrement(ref users) != 0) return;
            cts.Dispose();
            try { onEnd(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        T value = default!;
        Exception? failure = null;
        uint nativeId = 0;
        var thread = new Thread(() =>
        {
            Volatile.Write(ref nativeId, Native.GetCurrentThreadId());
            ContentNative.EnterBackgroundMode();
            try
            {
                using var quiet = Drives.QuietErrors();
                value = work(token);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { failure = e; }
            finally { Release(); }
        }) { IsBackground = true, Name = "Rock Preview content calibration" };
        thread.Start();
        bool finished = thread.Join(Math.Max(1, timeoutMs));
        if (!finished)
        {
            uint id = Volatile.Read(ref nativeId);
            if (id != 0)
            {
                nint h = Native.OpenThread(Native.THREAD_TERMINATE, 0, id);
                if (h != 0) { Native.CancelSynchronousIo(h); Native.CloseHandle(h); }
            }
            finished = thread.Join(500);
        }
        Release();
        if (!finished) return new Guarded<T>(false, false, default!);
        if (failure is OperationCanceledException && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        return failure != null ? new Guarded<T>(true, false, default!) : new Guarded<T>(true, true, value);
    }

    /// <summary>Encoded size of a document's stored text (fields then body, UTF-8; Brotli q5, raw under 128 B), as the store writes it.</summary>
    internal static long StoredBytes(ExtractedDoc doc) => StoredText.Encode(new StoredDoc(doc.Body, doc.Fields, doc.Picture), out _).Length;

    /// <summary>Distinct terms of a document as the index makes them (body and fields, field-scoped terms included), at most 100,000.</summary>
    internal static int DistinctWords(ExtractedDoc doc)
    {
        var sink = new DistinctSink();
        ContentTokenizer.Tokenize(doc.Body, sink);
        foreach (var (field, value) in doc.Fields) ContentTokenizer.TokenizeField(field, value, sink);
        return sink.Count;
    }

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

    // ───────────────────────────── census: folders ─────────────────────────────

    /// <summary>
    /// Counts by listing folders (no administrator, no service); stops at the budget (Complete = false). With the name
    /// index's settings (<paramref name="names"/>: Setup on an update), only what the name index holds is counted, since the
    /// text index reads nothing else: its excluded folders, patterns and hidden or system files are left out, and a folder
    /// it doesn't hold at all (its drive left out or not indexed, USB or network drives switched off, and no "also search"
    /// folder covering it) counts nothing.
    /// </summary>
    public static Task<ContentCensus> CountFoldersAsync(IReadOnlyList<string> folders, ContentSettings settings, TimeSpan budget, IProgress<ContentCensus>? progress,
        CancellationToken ct, IndexSettings? names = null)
        => CountFoldersAsync(folders, settings, budget, progress, ct, names, Drives.KindOf, Drives.SubstTargetOf);

    /// <param name="kindOf">The kind of a drive ("D:\"), read without touching it (tests pass their own).</param>
    /// <param name="substOf">The folder a SUBST drive ("S:\") shows, null for a real one, read without touching it (tests pass their own).</param>
    internal static Task<ContentCensus> CountFoldersAsync(IReadOnlyList<string> folders, ContentSettings settings, TimeSpan budget, IProgress<ContentCensus>? progress,
        CancellationToken ct, IndexSettings? names, Func<string, DriveKind> kindOf, Func<string, string?> substOf)
    {
        var s = settings.Clone();
        var list = ContentSettings.MergeNested(folders);
        // All of it: the drive switches (USB and network drives, 1.4.0's list of drives) decide what is held too.
        var n = names?.Clone();
        return Task.Run(() => CountFolders(list, s, budget, progress, ct, n, kindOf, substOf), ct);
    }

    /// <summary>
    /// Whether the name index holds <paramref name="folder"/> under these settings, and the rules a listing of it follows:
    /// the content rules (deny-lists, content_exclude_folders) plus the name index's own (its excluded folders, patterns,
    /// hidden or system files). Inside an "also search these folders" root, rules leaving out that root or a folder above
    /// it don't apply (ExclusionFilter.WithoutAncestorsOf), as in the name index. A folder on a SUBST drive is held when the
    /// folder the drive shows is, as the plan finds it there. Without a drive list (setup), whether it or a folder above it
    /// is hidden or system, a link or a volume mounted in a folder isn't looked at: the app asks
    /// <see cref="IndexPlan.Coverage"/>, which knows (<see cref="FoldersNotSearchedByName"/>).
    /// </summary>
    /// <param name="kindOf">The kind of a drive ("D:\"), read without touching it; <see cref="Drives.KindOf"/> when null (tests pass their own).</param>
    /// <param name="substOf">
    /// The folder a SUBST drive ("S:\") shows, null for a real one, read without touching it; <see cref="Drives.SubstTargetOf"/>
    /// when null (tests pass their own).
    /// </param>
    internal static (bool Held, ExclusionFilter Filter) NameIndexRules(string folder, IndexSettings names, IReadOnlyList<string> contentRules,
        Func<string, DriveKind>? kindOf = null, Func<string, string?>? substOf = null)
        => NameIndexRules(folder, names, contentRules, kindOf ?? Drives.KindOf, substOf ?? Drives.SubstTargetOf, hops: 0);

    static (bool Held, ExclusionFilter Filter) NameIndexRules(string folder, IndexSettings names, IReadOnlyList<string> contentRules,
        Func<string, DriveKind> kindOf, Func<string, string?> substOf, int hops)
    {
        string path = folder.Trim().TrimEnd('\\');
        string? root = null;
        foreach (var f in names.Folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string r = f.Trim().Trim('"').TrimEnd('\\');
            if (ContentSettings.IsInside(path, r) && (root == null || r.Length > root.Length)) root = r;
        }
        var nameRules = root == null ? names.ExcludeFolders
            : [.. names.ExcludeFolders.Where(e => !new ExclusionFilter(new IndexSettings { ExcludeFolders = [e] }).WithoutAncestorsOf(root).IsEmpty)];
        var nameOnly = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = [.. nameRules], ExcludePatterns = names.ExcludePatterns, ExcludeHidden = names.ExcludeHidden, ExcludeSystem = names.ExcludeSystem,
        });
        var filter = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = [.. contentRules, .. nameRules], ExcludePatterns = names.ExcludePatterns, ExcludeHidden = names.ExcludeHidden, ExcludeSystem = names.ExcludeSystem,
        });
        if (root != null) return (!nameOnly.ExcludesPathOrAncestor(root, path), filter);
        // A share (\\server\share) is searched by name only as an "also search" folder (Settings adds one there too). One
        // mapped to an indexed letter is held under that letter, which isn't known without asking Windows; adding it there
        // anyway is harmless: the name index skips a folder a root it plans already holds.
        if (IndexPlan.IsUnc(path)) return (false, filter);
        // Not below an "also search" folder: only a whole indexed drive holds it, by the plan's own rule for the drive (its
        // switch, its kind's switch, the folders left out). Taken as ready: one that isn't is searched once it is.
        bool letter = path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);
        string drive = letter ? path[..2] : path;
        // A SUBST drive isn't indexed itself (Windows calls it the kind of drive it is on, so 1.4.0's list would turn it
        // off): its files are searched under the folder it shows, so that name decides, as in IndexPlan.Coverage. A SUBST
        // of a SUBST takes more than one step; the cap stops a loop, and the plan holds nothing on the drive then.
        string? target = letter ? substOf(drive + "\\") : null;
        if (target != null && hops < 4) return NameIndexRules(target.TrimEnd('\\') + path[2..], names, contentRules, kindOf, substOf, hops + 1);
        var kind = letter ? kindOf(drive + "\\") : DriveKind.Unknown;
        var entry = new DriveEntry(drive + "\\", kind, "", "", IsReady: true, NetworkPath: null, SubstTarget: target);
        if (IndexPlan.ForDrive(names, entry, nameOnly) != DriveIndexing.Indexed || nameOnly.ExcludesPathOrAncestor(drive, path)) return (false, filter);
        return (true, filter);
    }

    /// <summary>
    /// The folders searched inside (My folders while they're used, then the folders added, nested ones merged) that the
    /// name index doesn't hold under <paramref name="names"/>: the text index reads only what the name index holds, so
    /// nothing in them is ever read inside. A folder the content rules leave out (content_exclude_folders) isn't listed:
    /// it isn't to be read anyway. Adding the folders listed to <see cref="IndexSettings.Folders"/> makes the name index
    /// hold them. <paramref name="myFolders"/>: My folders' paths; <see cref="ContentSettings.MyFolders"/> when null.
    /// </summary>
    /// <param name="coverageOf">
    /// The plan's own answer for a folder under <paramref name="names"/> (<see cref="IndexPlan.Coverage"/> with the app's
    /// drive list, as the folder bar gets it), read by <see cref="HeldByName"/>. Null: <see cref="NameIndexRules"/>, which
    /// knows no drive list (setup).
    /// </param>
    public static IReadOnlyList<string> FoldersNotSearchedByName(ContentSettings settings, IndexSettings names, IReadOnlyList<string>? myFolders = null,
        Func<string, FolderCoverage?>? coverageOf = null)
    {
        IReadOnlyList<string> mine = settings.UseMyFolders ? myFolders ?? ContentSettings.MyFolders() : [];
        var leftOut = new ExclusionFilter(new IndexSettings { ExcludeFolders = [.. settings.ExcludeFolders] });
        var missing = new List<string>();
        foreach (var folder in ContentSettings.MergeNested([.. mine, .. settings.Folders]))
        {
            string root = Path.GetPathRoot(folder) is { Length: > 0 } r ? r : folder;
            if (leftOut.ExcludesFolder(root, root) || leftOut.ExcludesPathOrAncestor(root, folder)) continue;
            bool held = coverageOf != null ? HeldByName(coverageOf(folder)) : NameIndexRules(folder, names, []).Held;
            if (!held) missing.Add(folder);
        }
        return missing;
    }

    /// <summary>
    /// Whether the name index holds a folder, from the plan's answer (<see cref="IndexPlan.Coverage"/>): it is covered, or
    /// on a drive that isn't ready, which every switch lets in, so it is searched once the drive is (the folder bar offers
    /// nothing there either, and adding it to the folders searched by name would only stay behind). No answer (the demo)
    /// counts as held.
    /// </summary>
    public static bool HeldByName(FolderCoverage? coverage) =>
        coverage is not { } c || c.Covered || c.Reason == CoverageReason.DriveNotReady;

    sealed class Tallies
    {
        readonly object _gate = new();
        readonly Dictionary<ContentKind, (int Files, long Bytes, int NetFiles, long NetBytes, long Capped)> _kinds = [];
        readonly Dictionary<ContentKind, Reservoir> _samples = [];
        int _cloud;
        long _listed;

        public void Listed(long bytes) { lock (_gate) _listed += Math.Max(0, bytes); }

        public void File(ContentKind kind, string path, long size, bool network, Random random)
        {
            lock (_gate)
            {
                var (f, b, nf, nb, c) = _kinds.GetValueOrDefault(kind);
                _kinds[kind] = (f + 1, b + Math.Max(0, size), nf + (network ? 1 : 0), nb + (network ? Math.Max(0, size) : 0),
                    c + Math.Min(Math.Max(0, size), RatioCapBytes));
                if (!_samples.TryGetValue(kind, out var r)) _samples[kind] = r = new Reservoir();
                r.Offer(path, size, random);
            }
        }

        public void Cloud() { lock (_gate) _cloud++; }

        public ContentCensus Census(bool complete)
        {
            lock (_gate)
                return new ContentCensus(
                    _kinds.ToDictionary(p => p.Key, p => new KindTally(p.Value.Files, p.Value.Bytes, p.Value.NetFiles, p.Value.NetBytes) { CappedBytes = p.Value.Capped }),
                    _samples.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Stratified()), _cloud, complete) { ListedBytes = _listed };
        }
    }

    /// <summary>A uniform sample of a kind's files, then 10 per size quartile (40 in all) for calibration.</summary>
    sealed class Reservoir
    {
        readonly List<(string Path, long Size)> _items = new(ReservoirPerKind);
        long _seen;

        public void Offer(string path, long size, Random random)
        {
            _seen++;
            if (_items.Count < ReservoirPerKind) _items.Add((path, size));
            else if (random.NextInt64(_seen) is var j && j < ReservoirPerKind) _items[(int)j] = (path, size);
        }

        public List<string> Stratified()
            => [.. StratifiedPicks(_items.OrderBy(i => i.Size).ToList(), new Random(unchecked((int)_seen * 31 + _items.Count))).Select(i => i.Path)];
    }

    /// <summary>
    /// At most 40 of the items (sorted by size): cut into 40 equal strata, 10 per quartile, one drawn at random from each,
    /// so the sample's mean is unbiased however long the tail (each stratum's first item undercounted the largest files:
    /// stored text per text file 15% low on My folders, in a replay of the real files). The order takes one stratum of
    /// each quartile in turn, the strata of a quartile in random order: a calibration its time budget cuts short has read
    /// a random part of every quartile (smallest first, or each quartile's lower strata first, left out the largest files,
    /// which hold most of the text: 61% of My folders' text is in 4% of its text files).
    /// </summary>
    internal static List<T> StratifiedPicks<T>(List<T> sortedBySize, Random random)
    {
        int count = sortedBySize.Count, per = (Math.Min(count, SamplesPerKind) + 3) / 4;
        var quartiles = new List<T>[4];
        for (int q = 0; q < 4; q++)
        {
            int from = count * q / 4, n = count * (q + 1) / 4 - from;
            var picks = quartiles[q] = new List<T>(per);
            if (count <= SamplesPerKind) picks.AddRange(sortedBySize.GetRange(from, n));
            else
                for (int k = 0; k < per; k++)
                {
                    int lo = from + k * n / per, hi = from + (k + 1) * n / per;
                    if (hi > lo) picks.Add(sortedBySize[lo + random.Next(hi - lo)]);
                }
            for (int i = picks.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (picks[i], picks[j]) = (picks[j], picks[i]);
            }
        }
        var result = new List<T>(Math.Min(count, SamplesPerKind));
        for (int k = 0; k < per; k++)
            foreach (var picks in quartiles)
                if (k < picks.Count) result.Add(picks[k]);
        return result;
    }

    static ContentCensus CountFolders(IReadOnlyList<string> folders, ContentSettings s, TimeSpan budget, IProgress<ContentCensus>? progress, CancellationToken ct,
        IndexSettings? names, Func<string, DriveKind> kindOf, Func<string, string?> substOf)
    {
        var tallies = new Tallies();
        var extras = ContentTypes.ExtraSet(s.ExtraTextExtensions);
        long maxBytes = MaxDocumentBytes(s);
        List<string> contentRules = [.. ContentTypes.DeniedFolderNames, .. s.ExcludeFolders, .. ContentTypes.DeniedFolderPaths()];
        var contentFilter = new ExclusionFilter(new IndexSettings { ExcludeFolders = contentRules });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        long lastReport = Environment.TickCount64;
        var tasks = folders.Select(folder => Task.Run(() =>
        {
            bool network = FileByteSource.IsNetworkPath(folder);
            var random = new Random(StringComparer.OrdinalIgnoreCase.GetHashCode(folder));
            var filter = contentFilter;
            if (names != null)
            {
                (bool held, filter) = NameIndexRules(folder, names, contentRules, kindOf, substOf);
                if (!held) return; // the name index doesn't hold it: nothing in it is ever read inside
            }
            try
            {
                FolderScanner.Scan(folder, filter, timeout.Token, onScanned: dir =>
                {
                    // Everything listed counts towards what was gone through (a count cut short is scaled up by it).
                    long listed = 0;
                    foreach (var e in dir.Entries)
                        if ((e.Attributes & FileAttributes.Directory) == 0) listed += Math.Max(0, e.Size);
                    tallies.Listed(listed);
                    if (HasDeniedFolder(dir.FullPath)) return;
                    foreach (var e in dir.Entries)
                    {
                        if ((e.Attributes & (FileAttributes.Directory | FileAttributes.Temporary)) != 0) continue;
                        if (ContentTypes.IsDeniedFile(e.Name)) continue;
                        var kind = ContentTypes.KindOf(e.Name, extras);
                        if (kind == ContentKind.None) continue;
                        if ((e.Attributes & CloudBits) != 0) { tallies.Cloud(); continue; }
                        if (kind != ContentKind.Picture && e.Size > maxBytes) continue; // TooBig: never read
                        tallies.File(kind, dir.FullPath + "\\" + e.Name, e.Size, network, random);
                    }
                    long now = Environment.TickCount64;
                    if (progress != null && now - Interlocked.Read(ref lastReport) >= 250)
                    {
                        Interlocked.Exchange(ref lastReport, now);
                        progress.Report(tallies.Census(complete: false));
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is RootUnavailableException or IOException or UnauthorizedAccessException) { }
        }, CancellationToken.None)).ToArray();
        Task.WaitAll(tasks);
        ct.ThrowIfCancellationRequested();
        var census = tallies.Census(complete: !timeout.IsCancellationRequested);
        progress?.Report(census);
        return census;
    }

    static bool HasDeniedFolder(string path)
    {
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (ContentTypes.IsDeniedFolder(part)) return true;
        return false;
    }

    // ───────────────────────────── census: the live name index ─────────────────────────────

    /// <summary>Runs on the counting thread between CountIndex's walk and the hold that reads its samples' paths (tests: a Compact in that gap).</summary>
    [ThreadStatic] internal static Action? AfterWalk;

    /// <summary>Counts candidates in the live name index, in read-lock slices. Call off the UI thread.</summary>
    public static ContentCensus CountIndex(FileIndex index, ContentSettings settings, CancellationToken ct)
        => CountIndex(index, settings, allRootsInScope: false, ct);

    /// <summary>
    /// CountIndex with every root in scope (the demo). Roots' volume keys are resolved once per call (Win32, outside the
    /// lock). OneDrive folders: NTFS roots mask the cloud bits, so a sample of up to 200 of their files is stat'ed and the
    /// placeholder share taken off.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static ContentCensus CountIndex(FileIndex index, ContentSettings settings, bool allRootsInScope, CancellationToken ct)
    {
        var s = settings.Clone();
        s.Mode = ContentMode.DocumentsAndPictures;
        s.Kinds = ContentKinds.All;
        var roots = new RootTable();
        var oneDrive = OneDriveFolders();
        var scope = Scope(s, allRootsInScope, roots, null);
        var cloudScope = oneDrive.Count > 0 && !allRootsInScope ? Scope(s, false, roots, oneDrive) : null;
        var walker = new FolderWalker();
        var cloudWalker = cloudScope != null ? new FolderWalker() : null;
        var kinds = new Dictionary<ContentKind, (int Files, long Bytes, int NetFiles, long NetBytes, long Capped)>();
        var cloudCounts = new Dictionary<ContentKind, int>();
        var reservoirs = new Dictionary<ContentKind, List<(int Id, long Size)>>();
        var cloudSample = new List<int>();
        long seenCloudCandidates = 0;
        var seen = new Dictionary<ContentKind, long>();
        var random = new Random(12345);
        int cloud = 0;
        long maxBytes = MaxDocumentBytes(s);
        WarmUp(scope);
        // The generation the last walk went through whole: the reservoirs' ids are of that generation only.
        long walked = -1;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            kinds.Clear(); cloudCounts.Clear(); reservoirs.Clear(); cloudSample.Clear(); seen.Clear();
            seenCloudCandidates = 0;
            cloud = 0;
            walker.Reset(index, scope, roots);
            cloudWalker?.Reset(index, cloudScope!, roots);
            long generation = index.Generation;
            bool restart = false;
            // Slices that adapt to how long they take (a cold walker, a busy PC), starting small, and held out of
            // background mode: a writer waiting behind a background-priority holder (the journal) stalls every search.
            var slice = new AdaptiveSlice(16_384, start: 256);
            for (int start = 0; ; )
            {
                ct.ThrowIfCancellationRequested();
                int sliceSize = slice.Size;
                var watch = Stopwatch.StartNew();
                bool background = ContentNative.LeaveBackgroundMode();
                index.Lock.EnterReadLock();
                try
                {
                    if (index.Generation != generation) { restart = true; break; }
                    int count = index.Count;
                    if (start >= count) break;
                    for (int id = start; id < Math.Min(count, start + sliceSize); id++)
                    {
                        if (!index.IsAlive(id) || index.IsDirectory(id)) continue;
                        if (!walker.TryKeyOf(id, out _, out var folder, out var kind)) continue;
                        var attrs = index.Attributes(id);
                        if ((attrs & CloudBits) != 0) { cloud++; continue; }
                        long size = Math.Max(0, index.Size(id));
                        if (kind != ContentKind.Picture && size > maxBytes) continue; // TooBig: never read
                        bool network = roots[folder.Slot].Network;
                        var (f, b, nf, nb, capped) = kinds.GetValueOrDefault(kind);
                        kinds[kind] = (f + 1, b + size, nf + (network ? 1 : 0), nb + (network ? size : 0), capped + Math.Min(size, RatioCapBytes));
                        long n = seen[kind] = seen.GetValueOrDefault(kind) + 1;
                        if (!reservoirs.TryGetValue(kind, out var r)) reservoirs[kind] = r = [];
                        if (r.Count < ReservoirPerKind) r.Add((id, size));
                        else if (random.NextInt64(n) is var j && j < ReservoirPerKind) r[(int)j] = (id, size);
                        if (cloudWalker != null && cloudWalker.TryKeyOf(id, out _, out _, out _))
                        {
                            cloudCounts[kind] = cloudCounts.GetValueOrDefault(kind) + 1;
                            seenCloudCandidates++;
                            if (cloudSample.Count < 200) cloudSample.Add(id);
                            else if (random.NextInt64(seenCloudCandidates) is var c && c < 200) cloudSample[(int)c] = id;
                        }
                    }
                }
                finally
                {
                    index.Lock.ExitReadLock();
                    if (background) ContentNative.EnterBackgroundMode();
                }
                slice.Took(watch.Elapsed.TotalMilliseconds);
                start += sliceSize;
            }
            bool resolved = false;
            foreach (var w in new[] { walker, cloudWalker })
            {
                if (w == null || w.UnknownRoots.Count == 0) continue;
                roots.Add([.. w.UnknownRoots], VolumeKeys.For, (_, _) => 0, scope);
                resolved = true;
            }
            if (resolved)
            {
                // New roots: the include folders' keys are resolved through them now.
                scope = Scope(s, allRootsInScope, roots, null);
                roots.Rescope(scope);
                if (cloudScope != null) cloudScope = Scope(s, false, roots, oneDrive);
                restart = true;
            }
            if (!restart) { walked = generation; break; }
        }

        // Paths of the samples (and of the OneDrive sample, which is stat'ed outside the lock), in one short hold that
        // checks the generation the reservoirs were filled in: after a Compact (or a Clear) their ids name other entries
        // (files outside the scope, denied ones, folders), so the census then has no samples and isn't complete.
        var samples = new Dictionary<ContentKind, IReadOnlyList<string>>();
        var cloudPaths = new List<string>();
        bool current = false;
        AfterWalk?.Invoke();
        if (walked >= 0)
        {
            bool background = ContentNative.LeaveBackgroundMode();
            index.Lock.EnterReadLock();
            try
            {
                if (index.Generation == walked)
                {
                    current = true;
                    foreach (var (kind, r) in reservoirs)
                    {
                        var picked = new List<string>();
                        var sorted = r.Where(i => index.IsAlive(i.Id)).OrderBy(i => i.Size).ToList();
                        foreach (var (id, _) in StratifiedPicks(sorted, random)) picked.Add(index.GetFullPath(id));
                        samples[kind] = picked;
                    }
                    foreach (int id in cloudSample) if (index.IsAlive(id)) cloudPaths.Add(index.GetFullPath(id));
                }
            }
            finally
            {
                index.Lock.ExitReadLock();
                if (background) ContentNative.EnterBackgroundMode();
            }
        }
        if (cloudPaths.Count > 0)
        {
            int placeholders = 0, statted = 0;
            using (Drives.QuietErrors())
                foreach (var p in cloudPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Native.TryGetMetadata(p, out var a, out _, out _, out _)) continue;
                    statted++;
                    if ((a & CloudBits) != 0) placeholders++;
                }
            if (statted > 0 && placeholders > 0)
            {
                double share = placeholders / (double)statted;
                foreach (var (kind, n) in cloudCounts)
                {
                    int drop = (int)Math.Round(n * share);
                    var (f, b, nf, nb, c) = kinds[kind];
                    double keep = f > 0 ? (f - drop) / (double)f : 1;
                    kinds[kind] = (Math.Max(0, f - drop), (long)(b * keep), nf, nb, (long)(c * keep));
                    cloud += drop;
                }
            }
        }
        return new ContentCensus(kinds.ToDictionary(p => p.Key, p => new KindTally(p.Value.Files, p.Value.Bytes, p.Value.NetFiles, p.Value.NetBytes) { CappedBytes = p.Value.Capped }),
            samples, cloud, Complete: current);
    }

    /// <summary>
    /// Runs the walker and the scope rules once on a tiny index of its own, so their first-use costs (JIT, static tables)
    /// are paid before the name index's lock is taken, not inside the first slice.
    /// </summary>
    static void WarmUp(ContentScope scope)
    {
        try
        {
            var index = new FileIndex();
            int root = index.AddRoot("W:");
            int folder = index.Add("Docs", root, FileAttributes.Directory, -1, 0, 0);
            int file = index.Add("warm.txt", folder, FileAttributes.Archive, 1, 1, 0);
            var roots = new RootTable();
            roots.Add(["W:"], _ => ("path:W:", "", @"W:\"), (_, _) => 0, scope);
            var walker = new FolderWalker();
            walker.Reset(index, scope, roots);
            walker.TryKeyOf(file, out _, out _, out _);
            index.GetFullPath(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    static ContentScope Scope(ContentSettings s, bool allRoots, RootTable roots, IReadOnlyList<string>? onlyFolders)
    {
        var settings = s;
        if (onlyFolders != null)
        {
            settings = s.Clone();
            settings.UseMyFolders = false;
            settings.Folders = [.. onlyFolders];
        }
        var snapshot = roots.Snapshot;
        return ContentScope.Build(settings, allRoots, [], folder =>
        {
            if (snapshot.Resolve(folder) is { } r) return r;
            var (key, prefix, _) = VolumeKeys.For(folder);
            return (key, prefix);
        });
    }

    static List<string> OneDriveFolders()
    {
        var list = new List<string>();
        foreach (var name in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } p && ContentSettings.Normalize(p) is { } n) list.Add(n);
        return ContentSettings.MergeNested(list);
    }

    /// <summary>A fingerprint of what a census depends on (cache keys).</summary>
    internal static ulong ScopeFingerprint(ContentSettings s)
    {
        ulong h = 14695981039346656037UL;
        void Add(string? v)
        {
            foreach (char c in (v ?? "").ToUpperInvariant()) h = (h ^ c) * 1099511628211UL;
            h = (h ^ 0xFFFF) * 1099511628211UL;
        }
        Add(s.UseMyFolders ? "1" : "0");
        foreach (var f in s.Folders) Add("f" + f);
        foreach (var f in s.ExcludeFolders) Add("x" + f);
        foreach (var f in s.ExtraTextExtensions) Add("e" + f);
        Add("m" + s.MaxFileSizeMB.ToString(System.Globalization.CultureInfo.InvariantCulture)); // documents over it aren't counted
        return h;
    }
}
