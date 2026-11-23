using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;

namespace PreviewHarness.Bench;

internal enum ThumbQuality { None, Low, Final }

/// <summary>
/// What the grid scenarios drive: the grid says what is on screen (and which way it scrolls), and is told as pictures
/// arrive (a quick low-quality stand-in, the final picture, or "nothing but the icon"). Today's pipeline and the new one
/// sit behind this, so that one run can time both on the same scenarios.
/// </summary>
internal interface IThumbPipeline : IDisposable
{
    string Name { get; }

    /// <summary>Called on the grid's (UI) thread whenever it paints: the whole list, the visible range, the columns, the cell size, the scroll direction (+1, -1, 0).</summary>
    void SetViewport(IReadOnlyList<string> list, int first, int count, int columns, int size, int direction);

    /// <summary>Raised from any thread: path, quality, pixel size.</summary>
    event Action<string, ThumbQuality, int, int>? Arrived;

    /// <summary>The grid has a picture (any quality) in memory for this path.</summary>
    bool HasPicture(string path);

    /// <summary>The grid shows this path's final answer: its picture, or its icon when no picture can be made.</summary>
    bool HasAnswer(string path);

    /// <summary>What the pipeline did, for the result row (null for today's).</summary>
    ThumbServiceStats? Stats { get; }
}

/// <summary>
/// Today's pipeline exactly as ThumbnailGrid uses it in 1.7.27: every visible cell without a picture asks
/// GetThumbnailAsync while painting (Request/LoadAsync), and after painting every pending request whose cell is no
/// longer visible is cancelled. The UI hop (BeginInvoke) is replaced by a lock. Runs the frozen copy of 1.7.27's
/// provider (TodayThumbnailProvider).
/// </summary>
internal sealed class TodayPipeline : IThumbPipeline
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _none = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "today";
    public event Action<string, ThumbQuality, int, int>? Arrived;
    public ThumbServiceStats? Stats => null;

    public void SetViewport(IReadOnlyList<string> list, int first, int count, int columns, int size, int direction)
    {
        lock (_lock)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = first; i < Math.Min(list.Count, first + count); i++)
            {
                string p = list[i];
                visible.Add(p);
                if (!_thumbs.Contains(p) && !_none.Contains(p)) Request(p, size);
            }
            foreach (var (p, cts) in _pending.ToArray())
                if (!visible.Contains(p)) { cts.Cancel(); _pending.Remove(p); }
        }
    }

    private void Request(string path, int size)
    {
        if (_pending.ContainsKey(path)) return;
        var cts = new CancellationTokenSource();
        _pending[path] = cts;
        _ = LoadAsync(path, size, cts);
    }

    private async Task LoadAsync(string path, int size, CancellationTokenSource cts)
    {
        Bitmap? bmp = null;
        try { bmp = await TodayThumbnailProvider.GetThumbnailAsync(path, size, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { }
        int w = bmp?.Width ?? 0, h = bmp?.Height ?? 0;
        lock (_lock)
        {
            if (_pending.TryGetValue(path, out var current) && current == cts) _pending.Remove(path);
            if (cts.IsCancellationRequested) { bmp?.Dispose(); return; }
            if (bmp == null) _none.Add(path); else _thumbs.Add(path);
        }
        bmp?.Dispose();
        Arrived?.Invoke(path, bmp == null ? ThumbQuality.None : ThumbQuality.Final, w, h);
    }

    public bool HasPicture(string path) { lock (_lock) return _thumbs.Contains(path); }

    // Today's provider hands back the icon as if it were the thumbnail: a "picture" here may be an icon.
    public bool HasAnswer(string path) { lock (_lock) return _thumbs.Contains(path) || _none.Contains(path); }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _pending.Values) c.Cancel();
            _pending.Clear();
        }
    }
}

/// <summary>
/// The new pipeline as the rebuilt grid will drive it: painting never asks; when the viewport changes the plan
/// (ThumbOrder.Viewport: visible cells, two screens ahead, one behind) goes to the thumbnail service with the stamps the
/// index would give; arrivals (Low, Final, None) are taken as they come. "new-on" is "Instant thumbnails" on: the
/// service has its own store in the set's folder. "new-off" is the setting every user starts with: no store, nothing
/// written to disk. Like the program, the service's threads pay their first shell call while it starts (WarmUp); the
/// store is opened by the first request, inside the timing.
/// </summary>
internal sealed class NewPipeline : IThumbPipeline
{
    private readonly ThumbnailService _service;
    private readonly ThumbClient _client;
    // Step 1 of the ladder is the program's memory, shared by every window (ThumbHost.Memory): a second pipeline in the
    // same process (a second window) finds what the first holds. A child process measures one size, so the path is the key.
    private static readonly object _lock = new();
    private static readonly Dictionary<string, ThumbQuality> _have = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, ThumbStamp> _stamps;
    private readonly string _name;
    private IReadOnlyList<string>? _list;
    private int _listNumber;
    private (int First, int Count, int Columns, int Size, int Direction) _last = (-1, -1, -1, -1, 0);

    /// <param name="storeFolder">The store's folder ("new-on"), or null for no store ("new-off").</param>
    public NewPipeline(string? storeFolder, IReadOnlyDictionary<string, ThumbStamp> stamps)
    {
        _stamps = stamps;
        _name = storeFolder == null ? "new-off" : "new-on";
        _service = new ThumbnailService(new ThumbServiceOptions
        {
            OpenStore = storeFolder == null ? null : () => ThumbStore.Open(storeFolder),
            Power = NoPowerPolicy.Instance,
            Name = "bench thumbnails",
        });
        _client = _service.CreateClient(OnArrival);
        _service.WarmUp(TimeSpan.FromSeconds(3), openStore: false);
    }

    public string Name => _name;
    public event Action<string, ThumbQuality, int, int>? Arrived;
    public ThumbServiceStats? Stats => _service.Stats;

    public void SetViewport(IReadOnlyList<string> list, int first, int count, int columns, int size, int direction)
    {
        // Painting never asks: only a changed viewport makes a new plan.
        var now = (first, count, columns, size, direction);
        if (ReferenceEquals(list, _list) && now == _last) return;
        if (!ReferenceEquals(list, _list)) { _list = list; _listNumber++; }
        _last = now;
        int rows = Math.Max(1, (count + columns - 1) / columns);
        var plan = ThumbOrder.Viewport(list.Count, columns, first / Math.Max(1, columns), rows,
            direction > 0 ? ScrollDirection.Down : direction < 0 ? ScrollDirection.Up : ScrollDirection.None);
        var requests = new List<ThumbRequest>(plan.Count);
        var held = new List<(string Path, ThumbQuality Quality)>();
        foreach (var item in plan)
        {
            string path = list[item.Index];
            // Step 1 of the ladder, the grid's memory cache: what the grid holds already (its final picture or its icon)
            // is not asked for again, and is drawn at once.
            lock (_lock)
            {
                if (_have.TryGetValue(path, out var had) && had != ThumbQuality.Low)
                {
                    held.Add((path, had));
                    continue;
                }
            }
            requests.Add(new ThumbRequest(path, _stamps.TryGetValue(path, out var s) ? s : ThumbStamp.Unknown, size, item.Priority, item.Index));
        }
        _client.SubmitViewport(_listNumber, requests);
        foreach (var (path, quality) in held) Arrived?.Invoke(path, quality, size, size);
    }

    private void OnArrival(ThumbArrival a)
    {
        var q = a.Quality switch
        {
            Anything.Core.Thumbs.ThumbQuality.Final => ThumbQuality.Final,
            Anything.Core.Thumbs.ThumbQuality.Low => ThumbQuality.Low,
            _ => ThumbQuality.None,
        };
        int w = a.Bitmap?.Width ?? 0, h = a.Bitmap?.Height ?? 0;
        a.Bitmap?.Dispose();
        lock (_lock)
        {
            // A Final or an icon answer replaces a stand-in; a late stand-in never replaces a Final.
            if (!_have.TryGetValue(a.Path, out var had) || had == ThumbQuality.Low) _have[a.Path] = q;
        }
        Arrived?.Invoke(a.Path, q, w, h);
    }

    public bool HasPicture(string path)
    {
        lock (_lock) return _have.TryGetValue(path, out var q) && q != ThumbQuality.None;
    }

    public bool HasAnswer(string path)
    {
        lock (_lock) return _have.TryGetValue(path, out var q) && q != ThumbQuality.Low;
    }

    /// <summary>
    /// Waits (untimed, up to <paramref name="max"/>) until Windows has made the pictures this pipeline decoded itself with
    /// no store, as it does in the program when nothing else waits. Returns how many were left.
    /// </summary>
    public int WaitForWindowsWarming(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (_service.WarmPending > 0 && DateTime.UtcNow < until) Thread.Sleep(100);
        return _service.WarmPending;
    }

    public void Dispose()
    {
        _client.Dispose();
        _service.Dispose();
    }
}

/// <summary>One scenario's result, as written to .probe\bench\grid-*.json.</summary>
internal sealed class GridResult
{
    public string Pipeline { get; set; } = "";
    public int Run { get; set; }
    public int Order { get; set; }                 // 0 = this pipeline ran first in its scenario this run
    public string Folder { get; set; } = "";        // "mixed", "large" or "exif"
    public string Scenario { get; set; } = "";      // "screen" or "scroll"
    public string Temperature { get; set; } = "";   // "cold", "warm-same-process", "warm-new-process"
    public int Cells { get; set; }
    public int Size { get; set; }
    public int Files { get; set; }
    public double FirstMs { get; set; } = double.NaN;
    public double HalfMs { get; set; } = double.NaN;
    public double AllMs { get; set; } = double.NaN;
    public double FirstLowMs { get; set; } = double.NaN;
    public int LowBeforeFinal { get; set; }          // cells whose stand-in came before their final picture
    public int WithPicture { get; set; }
    public int Resolved { get; set; }
    public bool TimedOut { get; set; }
    public double IssueMsTotal { get; set; }         // time the grid's thread spent inside SetViewport
    public double IssueMsMaxTick { get; set; }
    public double CpuMs { get; set; }
    public long ProcBytes { get; set; }
    public double Load { get; set; }
    public double LoadMax { get; set; }
    public bool HighLoad { get; set; }
    public int ColdChecked { get; set; } = -1;        // files checked with a cache-only probe just before (cold runs)
    public int ColdMisses { get; set; } = -1;
    // The new pipeline's counts during the scenario (-1 for today's).
    public long StoreHits { get; set; } = -1;
    public long StoreBytes { get; set; } = -1;
    public long CacheProbes { get; set; } = -1;
    public long EmbeddedLows { get; set; } = -1;
    public long Decodes { get; set; } = -1;          // Rock Preview's own decodes and extractions
    public long ShellExtracts { get; set; } = -1;
    public long FinalsStored { get; set; } = -1;
    public long IconsFetched { get; set; } = -1;
    // Scroll only.
    public int CellsSeen { get; set; }
    public double ShareCellsWithPictureWhileVisible { get; set; } = double.NaN;
    public double ShareVisibleTimeWithPicture { get; set; } = double.NaN;
    public double ShareCellsAnsweredWhileVisible { get; set; } = double.NaN;   // picture or icon (today can't tell them apart)
    public double ShareVisibleTimeAnswered { get; set; } = double.NaN;
    public double ScrollSeconds { get; set; }
    public double LastScreenCompleteMs { get; set; } = double.NaN;

    /// <summary>Fills the counters from the difference of two snapshots of the new pipeline's service.</summary>
    public void Counts(ThumbServiceStats? a, ThumbServiceStats? b)
    {
        if (a == null || b == null) return;
        StoreHits = b.StoreHits - a.StoreHits;
        StoreBytes = b.StoreBytes - a.StoreBytes;
        CacheProbes = b.CacheProbes - a.CacheProbes;
        EmbeddedLows = b.EmbeddedLows - a.EmbeddedLows;
        Decodes = b.OwnDecodes - a.OwnDecodes + b.OwnExtracts - a.OwnExtracts;
        ShellExtracts = b.ShellExtracts - a.ShellExtracts;
        FinalsStored = b.FinalsStored - a.FinalsStored;
        IconsFetched = b.IconsFetched - a.IconsFetched;
    }
}

/// <summary>Runs the screen and scroll scenarios on a pipeline.</summary>
internal static class GridScenarios
{
    public static GridResult Screen(IThumbPipeline p, IReadOnlyList<string> files, int cells, int cols, int size, int timeoutMs)
    {
        int n = Math.Min(cells, files.Count);
        var arrivals = new List<(double T, ThumbQuality Q)>();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int lowBeforeFinal = 0;
        var visible = new HashSet<string>(files.Take(n), StringComparer.OrdinalIgnoreCase);
        var list = files.Take(n).ToList();
        var sw = new Stopwatch();
        object gate = new();
        double firstLow = double.NaN;
        using var done = new ManualResetEventSlim();
        p.Arrived += (path, q, w, h) =>
        {
            lock (gate)
            {
                if (!visible.Contains(path)) return;
                double t = sw.Elapsed.TotalMilliseconds;
                if (q == ThumbQuality.Low)
                {
                    if (double.IsNaN(firstLow)) firstLow = t;
                    lowAt.TryAdd(path, t);
                    return;
                }
                if (resolved.Add(path))
                {
                    arrivals.Add((t, q));
                    if (q == ThumbQuality.Final && lowAt.TryGetValue(path, out double lt) && lt < t) lowBeforeFinal++;
                }
                if (resolved.Count >= n) done.Set();
            }
        };
        var stats0 = p.Stats;
        var mon = LoadMonitor.Shared;
        var a = ProcessMeter.Take();
        double t0 = mon.Now;
        sw.Start();
        p.SetViewport(list, 0, n, cols, size, 0);
        double issue = sw.Elapsed.TotalMilliseconds;
        // The grid repaints as pictures arrive; repaint every 50 ms like an invalidate storm would.
        double maxTick = issue;
        while (!done.Wait(50) && sw.ElapsedMilliseconds < timeoutMs)
        {
            double s0 = sw.Elapsed.TotalMilliseconds;
            p.SetViewport(list, 0, n, cols, size, 0);
            double d = sw.Elapsed.TotalMilliseconds - s0;
            issue += d;
            maxTick = Math.Max(maxTick, d);
        }
        var b = ProcessMeter.Take();
        var stats1 = p.Stats;
        var (load, lmax) = mon.BetweenSettled(t0, mon.Now);
        lock (gate)
        {
            var times = arrivals.Select(x => x.T).OrderBy(x => x).ToList();
            var r = new GridResult
            {
                Pipeline = p.Name, Scenario = "screen", Cells = cells, Size = size, Files = n,
                FirstMs = times.Count > 0 ? Math.Round(times[0]) : double.NaN,
                HalfMs = times.Count >= (n + 1) / 2 ? Math.Round(times[(n + 1) / 2 - 1]) : double.NaN,
                AllMs = times.Count >= n ? Math.Round(times[n - 1]) : double.NaN,
                FirstLowMs = Math.Round(firstLow), LowBeforeFinal = lowBeforeFinal,
                WithPicture = arrivals.Count(x => x.Q != ThumbQuality.None), Resolved = arrivals.Count, TimedOut = arrivals.Count < n,
                IssueMsTotal = Math.Round(issue, 1), IssueMsMaxTick = Math.Round(maxTick, 1),
                CpuMs = Math.Round(ProcessMeter.CpuMs(a, b)), ProcBytes = (long)(b.BytesRead - a.BytesRead),
                Load = Stats.R(load), LoadMax = Stats.R(lmax), HighLoad = load > 60,
            };
            r.Counts(stats0, stats1);
            return r;
        }
    }

    /// <summary>
    /// Shows the list a screen of <paramref name="cells"/> at a time and waits for each screen's answers (Final or icon),
    /// up to <paramref name="timeoutMs"/> a screen. Untimed: it only fills what the pipeline keeps between processes.
    /// Returns how many files were answered.
    /// </summary>
    public static int Fill(IThumbPipeline p, IReadOnlyList<string> files, int cols, int size, int cells, int timeoutMs)
    {
        int answered = 0;
        for (int start = 0; start < files.Count; start += cells)
        {
            int n = Math.Min(cells, files.Count - start);
            var waiting = new HashSet<string>(files.Skip(start).Take(n), StringComparer.OrdinalIgnoreCase);
            // Not disposed: an arrival raised just as the handler is removed may still set it.
            var done = new ManualResetEventSlim();
            void OnArrived(string path, ThumbQuality q, int w, int h)
            {
                if (q == ThumbQuality.Low) return;
                lock (waiting)
                {
                    if (waiting.Remove(path) && waiting.Count == 0) done.Set();
                }
            }
            p.Arrived += OnArrived;
            var sw = Stopwatch.StartNew();
            p.SetViewport(files, start, n, cols, size, 1);
            while (!done.Wait(50) && sw.ElapsedMilliseconds < timeoutMs) p.SetViewport(files, start, n, cols, size, 1);
            p.Arrived -= OnArrived;
            lock (waiting) answered += n - waiting.Count;
        }
        return answered;
    }

    /// <summary>
    /// Scrolls through the list at <paramref name="screensPerSecond"/> (a repaint every 33 ms), then waits at the end
    /// up to 20 s for the last screen. Counts, for every cell that was on screen, whether it had its picture at some
    /// repaint while visible, and the share of (cell, repaint) pairs that showed a picture.
    /// </summary>
    public static GridResult Scroll(IThumbPipeline p, IReadOnlyList<string> files, int cols, int rows, int size, double screensPerSecond)
    {
        int total = files.Count;
        int totalRows = (total + cols - 1) / cols;
        var seen = new HashSet<int>();
        var gotWhileVisible = new HashSet<int>();
        var answeredWhileVisible = new HashSet<int>();
        long cellTicks = 0, cellTicksWithPicture = 0, cellTicksAnswered = 0;
        double issue = 0, maxTick = 0;
        var stats0 = p.Stats;
        var mon = LoadMonitor.Shared;
        var a = ProcessMeter.Take();
        double t0 = mon.Now;
        var sw = Stopwatch.StartNew();
        double rowsPerMs = rows * screensPerSecond / 1000.0;
        double scrollEnd = double.NaN, lastComplete = double.NaN;
        while (true)
        {
            double t = sw.Elapsed.TotalMilliseconds;
            double offsetRows = Math.Min(t * rowsPerMs, Math.Max(0, totalRows - rows));
            int firstRow = (int)Math.Floor(offsetRows);
            bool partial = offsetRows > firstRow;
            int first = firstRow * cols;
            int count = Math.Min(total - first, (rows + (partial ? 1 : 0)) * cols);
            double s0 = sw.Elapsed.TotalMilliseconds;
            p.SetViewport(files, first, count, cols, size, double.IsNaN(scrollEnd) ? 1 : 0);
            double d = sw.Elapsed.TotalMilliseconds - s0;
            issue += d;
            maxTick = Math.Max(maxTick, d);
            bool allHave = true;
            for (int i = first; i < first + count; i++)
            {
                seen.Add(i);
                cellTicks++;
                if (p.HasPicture(files[i])) { cellTicksWithPicture++; gotWhileVisible.Add(i); }
                // The last screen is complete when every cell shows its answer (picture or, when none can be made, icon).
                if (p.HasAnswer(files[i])) { cellTicksAnswered++; answeredWhileVisible.Add(i); }
                else allHave = false;
            }
            if (double.IsNaN(scrollEnd) && offsetRows >= totalRows - rows) scrollEnd = t;
            if (!double.IsNaN(scrollEnd))
            {
                if (allHave) { lastComplete = t - scrollEnd; break; }
                if (t - scrollEnd > 20000) break;
            }
            int sleep = (int)Math.Max(1, 33 - (sw.Elapsed.TotalMilliseconds - t));
            Thread.Sleep(sleep);
        }
        var b = ProcessMeter.Take();
        var stats1 = p.Stats;
        var (load, lmax) = mon.BetweenSettled(t0, mon.Now);
        var r = new GridResult
        {
            Pipeline = p.Name, Scenario = "scroll", Cells = cols * rows, Size = size, Files = total,
            CellsSeen = seen.Count,
            ShareCellsWithPictureWhileVisible = Math.Round(gotWhileVisible.Count / (double)Math.Max(1, seen.Count), 3),
            ShareVisibleTimeWithPicture = Math.Round(cellTicksWithPicture / (double)Math.Max(1, cellTicks), 3),
            ShareCellsAnsweredWhileVisible = Math.Round(answeredWhileVisible.Count / (double)Math.Max(1, seen.Count), 3),
            ShareVisibleTimeAnswered = Math.Round(cellTicksAnswered / (double)Math.Max(1, cellTicks), 3),
            ScrollSeconds = Math.Round(scrollEnd / 1000, 1), LastScreenCompleteMs = Math.Round(lastComplete),
            IssueMsTotal = Math.Round(issue, 1), IssueMsMaxTick = Math.Round(maxTick, 1),
            CpuMs = Math.Round(ProcessMeter.CpuMs(a, b)), ProcBytes = (long)(b.BytesRead - a.BytesRead),
            Load = Stats.R(load), LoadMax = Stats.R(lmax), HighLoad = load > 60,
        };
        r.Counts(stats0, stats1);
        return r;
    }
}

/// <summary>
/// Orchestrates the grid scenarios. For every run, every scenario and every pipeline (their order rotating from run to
/// run and scenario to scenario) the parent makes fresh copies of that scenario's files (new files: Windows' cache has
/// nothing for them, and the new pipeline's store starts empty), checks them cold with a cache-only probe, then starts
/// one child process for the cold run (and a second look in that same process) and another for "warm in a new
/// process" (today: Windows' cache filled by the cold run; new: its store filled). Each viewport runs in its own child
/// processes. Results go to .probe\bench\grid-*.json, with a table of medians and ranges in grid-*.md.
/// </summary>
internal static class GridBench
{
    private sealed record Scenario(string Folder, string Kind, int Cells, int Cols, int Size, List<string> Sources)
    {
        public string Name => $"{Folder}-{Kind}-{Cells}at{Size}";
    }

    /// <summary>The parent side.</summary>
    public static void Run(Corpus corpus, string probeDir, string benchDir, string coldDir, string pipelines, int runs, string? only, Action<string> log)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var names = pipelines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scenarios = Plan(corpus);
        if (only != null) scenarios = scenarios.Where(s => s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        log($"grid: {scenarios.Count} scenarios x {names.Length} pipelines x {runs} runs: {string.Join(", ", scenarios.Select(s => s.Name))}");
        var all = new List<GridResult>();
        for (int run = 0; run < runs; run++)
        {
            for (int si = 0; si < scenarios.Count; si++)
            {
                var s = scenarios[si];
                // The order rotates, so neither pipeline always runs on a cooler or warmer PC.
                var order = names.Select((n, i) => names[(i + run + si) % names.Length]).ToList();
                for (int oi = 0; oi < order.Count; oi++)
                {
                    string name = order[oi];
                    string set = Path.Combine(coldDir, $"grid-{stamp}-r{run}-{s.Name}-{name}");
                    try
                    {
                        var files = CopySet(s.Sources, set);
                        File.WriteAllLines(Path.Combine(set, "files.txt"), files);
                        var (checkedN, misses) = VerifyCold(files);
                        log($"grid run {run + 1}/{runs} {s.Name} {name}: {files.Count} fresh files ({files.Sum(f => new FileInfo(f).Length) / 1e6:0} MB), cold check {misses}/{checkedN} misses");
                        // A screen is complete after its cold run, so what the pipeline keeps holds it all already. A scroll
                        // only sees part of the list cold: a fill process goes through the rest before the second visit.
                        // new-off keeps nothing of its own: what it decoded itself it has Windows make later, when nothing
                        // else waits, so Windows' cache has it after a restart. Its fill runs the pipeline itself and waits
                        // for that (the user stayed a while), so the second visit shows what the product really leaves.
                        var temps = s.Kind == "scroll" ? new[] { "cold", "fill", "filled-new-process" }
                            : name == "new-off" ? new[] { "cold", "fill", "warm-new-process" }
                            : new[] { "cold", "warm-new-process" };
                        foreach (var temp in temps)
                        {
                            var res = Child(probeDir, set, s, name, temp, log);
                            foreach (var r in res)
                            {
                                r.Run = run;
                                r.Order = oi;
                                if (r.Temperature == "cold") { r.ColdChecked = checkedN; r.ColdMisses = misses; }
                            }
                            all.AddRange(res);
                        }
                    }
                    finally { TryDeleteDir(set); }
                }
            }
            Save(benchDir, stamp, probeDir, all);
        }
        string md = GridCompare.Markdown(all);
        File.WriteAllText(Path.Combine(benchDir, $"grid-{stamp}.md"), md);
        log(md);
    }

    /// <summary>
    /// The scenarios and their source files, the same in every run: a mixed camera/download folder (mostly 2 and 6 MP
    /// photos, the user's smaller pictures, screenshots and documents; shuffled), its 1,000-file scroll, the large-files
    /// folder (50 and 100 MP pictures, the large PSD/PSB/TIFF, the user's videos up to 600 MB), and a screen of JPEGs that
    /// carry EXIF thumbnails.
    /// </summary>
    /// <summary>The source files of the scenario named <paramref name="name"/> (as in the results, e.g. "mixed-screen-60at160").</summary>
    internal static List<string> SourcesOf(Corpus corpus, string name) => Plan(corpus).First(s => s.Name == name).Sources;

    private static List<Scenario> Plan(Corpus corpus)
    {
        var rnd = new Random(2026);
        var made2 = corpus.Files.Where(f => f.Id.StartsWith("made/mix-2mp", StringComparison.Ordinal)).ToList();
        var made6 = corpus.Files.Where(f => f.Id.StartsWith("made/mix-6mp", StringComparison.Ordinal)).ToList();
        var screens = corpus.Files.Where(f => f.Id.StartsWith("made/mix-screen", StringComparison.Ordinal)).ToList();
        var realSmall = corpus.Files.Where(f => f.Origin == "real" && f.Bytes <= 6_000_000 &&
            f.Ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".png" or ".webp").ToList();
        var docs = corpus.Files.Where(f => (f.Origin == "made" && f.Ext is ".pdf" or ".docx" or ".txt" or ".cs" or ".ttf") ||
            (f.Origin == "real" && f.Ext is ".pdf" or ".docx" or ".xlsx" or ".pptx" && f.Bytes <= 6_000_000)).ToList();
        var buckets = new List<(List<CorpusFile> Pool, double Weight)> { (made2, 0.35), (made6, 0.15), (screens, 0.10), (docs, 0.15) };
        buckets.Add(realSmall.Count > 0 ? (realSmall, 0.25) : (made2, 0.25));
        var picks = new List<string>();
        long bytes = 0;
        for (int i = 0; i < 1228; i++)
        {
            double x = rnd.NextDouble(), acc = 0;
            var pool = buckets[^1].Pool;
            foreach (var (pl, wgt) in buckets) { acc += wgt; if (x < acc && pl.Count > 0) { pool = pl; break; } }
            if (bytes > 1_150_000_000) pool = made2;
            var f = pool[rnd.Next(pool.Count)];
            bytes += f.Bytes;
            picks.Add(corpus.Full(f));
        }
        picks = picks.OrderBy(_ => rnd.Next()).ToList();

        var large = corpus.Files.Where(f => f.Origin == "made" && f.Tags.Contains("large")).ToList();
        long videoBytes = 0;
        foreach (var v in corpus.Files.Where(f => CorpusBuilder.RealVideos.Contains(f.Ext)).OrderByDescending(f => f.Bytes))
        {
            if (videoBytes + v.Bytes > 600_000_000) continue;
            large.Add(v);
            videoBytes += v.Bytes;
        }
        var lrnd = new Random(11);
        var largePaths = large.OrderBy(_ => lrnd.Next()).Select(corpus.Full).ToList();

        var exifSources = corpus.Files.Where(f => f.Origin == "made" && f.Ext == ".jpg" && f.Id.Contains("exifthumb", StringComparison.Ordinal)
            && f.Megapixels <= 24).Select(corpus.Full).ToList();
        var exif = Enumerable.Range(0, 24).Select(i => exifSources[i % exifSources.Count]).ToList();

        return new List<Scenario>
        {
            new("mixed", "screen", 60, 10, 160, picks.Take(60).ToList()),
            new("mixed", "screen", 144, 16, 96, picks.Skip(60).Take(144).ToList()),
            new("mixed", "screen", 24, 6, 256, picks.Skip(204).Take(24).ToList()),
            new("mixed", "scroll", 60, 10, 160, picks.Skip(228).ToList()),
            new("large", "screen", 24, 6, 256, largePaths),
            new("large", "screen", 60, 10, 160, largePaths),
            new("exif", "screen", 24, 6, 160, exif),
        };
    }

    private static List<string> CopySet(List<string> sources, string dir)
    {
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < sources.Count; i++)
        {
            string dst = Path.Combine(dir, $"m{i:0000}{Path.GetExtension(sources[i]).ToLowerInvariant()}");
            SourcesBench.CopyFile(sources[i], dst);
            paths.Add(dst);
        }
        return paths;
    }

    private static void Save(string benchDir, string stamp, string probeDir, List<GridResult> all)
    {
        var doc = new Dictionary<string, object?>
        {
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["commit"] = GitInfo.Commit(Path.GetDirectoryName(probeDir.TrimEnd('\\'))!),
            ["machine"] = $"{Environment.ProcessorCount} logical processors",
            ["load_note"] = "load = whole-PC processor load during the scenario (GetSystemTimes, 200 ms samples); high_load = average above 60 %",
            ["results"] = all,
        };
        File.WriteAllText(Path.Combine(benchDir, $"grid-{stamp}.json"), JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));
    }

    private static List<GridResult> Child(string probeDir, string set, Scenario s, string pipeline, string temp, Action<string> log)
    {
        string result = Path.Combine(set, $"result-{temp}.json");
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "--thumbbench", "grid-run", "--out", probeDir, "--set", set, "--folder", s.Folder, "--kind", s.Kind,
            "--cells", s.Cells.ToString(), "--cols", s.Cols.ToString(), "--size", s.Size.ToString(), "--temp", temp,
            "--result", result, "--pipelines", pipeline, "--max-minutes", "10",
        })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) log("    | " + e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log("    ! " + e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(11 * 60 * 1000))
        {
            log("    the child process ran out of time; ending it");
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(10000);
        }
        if (!File.Exists(result)) return new List<GridResult>();
        var list = JsonSerializer.Deserialize<List<GridResult>>(File.ReadAllText(result), new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }) ?? new List<GridResult>();
        File.Delete(result);
        return list;
    }

    /// <summary>The child side: one pipeline, one viewport, one temperature (plus a second look in the same process after a cold run).</summary>
    public static int RunChild(string set, string folder, string kind, int cells, int cols, int size, string temp, string result, string pipeline)
    {
        var files = File.ReadAllLines(Path.Combine(set, "files.txt")).ToList();
        // The stamps the index would hand the grid (length and last write), read before the timing as the index has them.
        var stamps = files.ToDictionary(f => f, f =>
        {
            var fi = new FileInfo(f);
            return new ThumbStamp(fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc());
        }, StringComparer.OrdinalIgnoreCase);
        string store = Path.Combine(set, "thumbs-store");
        var results = new List<GridResult>();
        int timeout = folder == "large" ? 240000 : 120000;
        IThumbPipeline Create() => pipeline switch
        {
            "today" => new TodayPipeline(),
            "new-off" => new NewPipeline(null, stamps),
            "new-on" or "new" => new NewPipeline(store, stamps),
            _ => throw new ArgumentException("unknown pipeline " + pipeline),
        };
        if (temp == "fill")
        {
            // Not timed: the pipeline itself goes through every file, a screen at a time, so that what it keeps between
            // processes (Windows' thumbnail cache; for new-on also its store) holds the whole list before the next process.
            using (var p = Create())
            {
                int got = GridScenarios.Fill(p, files, cols, size, cells, 120000);
                Console.WriteLine($"{pipeline} {folder} fill: {got}/{files.Count} files answered");
                if (p is NewPipeline np && pipeline == "new-off")
                {
                    var waited = Stopwatch.StartNew();
                    int left = np.WaitForWindowsWarming(TimeSpan.FromMinutes(6));
                    Console.WriteLine($"{pipeline} {folder} fill: Windows made the pictures decoded here in {waited.ElapsedMilliseconds} ms ({left} left)");
                }
            }
            File.WriteAllText(result, "[]");
            return 0;
        }
        GridResult Once(string label)
        {
            using var p = Create();
            var r = kind == "scroll"
                ? GridScenarios.Scroll(p, files, cols, 6, size, 3)
                : GridScenarios.Screen(p, files, cells, cols, size, timeout);
            r.Folder = folder;
            r.Temperature = label;
            if (kind == "scroll")
                Console.WriteLine($"{pipeline} {folder} scroll {label}: {r.ShareCellsWithPictureWhileVisible:P0} of cells had their picture while visible, {r.ShareVisibleTimeWithPicture:P0} of visible time, last screen +{r.LastScreenCompleteMs} ms");
            else
                Console.WriteLine($"{pipeline} {folder} screen {cells}@{size} {label}: first {r.FirstMs} ms, half {r.HalfMs} ms, all {r.AllMs} ms, {r.WithPicture}/{r.Files} pictures, first stand-in {r.FirstLowMs} ms, load {r.Load:0}%");
            return r;
        }
        results.Add(Once(temp));
        if (temp == "cold") results.Add(Once("warm-same-process"));
        File.WriteAllText(result, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));
        return 0;
    }

    private static (int Checked, int Misses) VerifyCold(List<string> files)
    {
        using var sta = new StaWorker("cold check");
        int misses = 0;
        foreach (var f in files)
            if (sta.Run(() => ShellProbe.Get(f, 256, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk)).Hr != 0) misses++;
        return (files.Count, misses);
    }

    private static void TryDeleteDir(string dir)
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
            catch { Thread.Sleep(500); }
        }
    }
}

/// <summary>The grid results as a table: medians and ranges over the runs, today and new side by side.</summary>
internal static class GridCompare
{
    public static string Markdown(List<GridResult> all)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\n## Grid: today and new, medians over the runs [min-max]\n\n");
        sb.Append("new-off: \"Instant thumbnails\" off (no store; how every user starts). new-on: the option on (store under .probe). " +
                  "cold: fresh copies, Windows' cache empty, store empty. warm-same-process: the same viewport again in the cold run's process. " +
                  "warm-new-process / filled-new-process: a new process; today and new-off with Windows' cache filled (today by its own run, " +
                  "new-off by its own pass, which has Windows make the pictures it decoded once nothing else waits), new-on with its store filled. Scroll: filled by a pass over the whole list first. " +
                  "Bytes read count this process only (Windows' thumbnail helper process reads files for today).\n\n");
        sb.Append("| Folder | Scenario | Cells@px | Temperature | Pipeline | runs | first ms | half ms | all ms | first stand-in ms | pictures | grid-thread ms worst repaint | store hits | decodes | shell extracts | bytes read (process) | store bytes MB | load |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in all.Where(r => r.Scenario == "screen")
                     .GroupBy(r => (r.Folder, r.Cells, r.Size, r.Temperature, r.Pipeline))
                     .OrderBy(g => g.Key.Folder).ThenBy(g => g.Key.Cells).ThenBy(g => TempOrder(g.Key.Temperature)).ThenBy(g => PipelineOrder(g.Key.Pipeline)))
        {
            var r = g.ToList();
            sb.Append($"| {g.Key.Folder} | screen | {g.Key.Cells}@{g.Key.Size} | {g.Key.Temperature} | {g.Key.Pipeline} | {r.Count} | {MR(r, x => x.FirstMs)} | {MR(r, x => x.HalfMs)} | {MR(r, x => x.AllMs)}{(r.Any(x => x.TimedOut) ? " (timed out)" : "")} | {MR(r, x => x.FirstLowMs)} | " +
                      $"{MR(r, x => x.WithPicture, "0")}/{r[0].Files} | {MR(r, x => x.IssueMsMaxTick, "0.0")} | {Cnt(r, x => x.StoreHits)} | {Cnt(r, x => x.Decodes)} | {Cnt(r, x => x.ShellExtracts)} | {MR(r, x => x.ProcBytes / 1e6, "0.0")} MB | {Cnt(r, x => x.StoreBytes, 1e6, "0.00")} | {MR(r, x => x.Load, "0")}% |\n");
        }
        sb.Append("\n| Folder | Temperature | Pipeline | runs | files | cells with picture while visible | visible time with picture | cells answered (picture or icon) while visible | visible time answered | last screen complete after stop ms | grid-thread ms worst | store hits | decodes | load |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in all.Where(r => r.Scenario == "scroll").GroupBy(r => (r.Folder, r.Temperature, r.Pipeline))
                     .OrderBy(g => TempOrder(g.Key.Temperature)).ThenBy(g => PipelineOrder(g.Key.Pipeline)))
        {
            var r = g.ToList();
            sb.Append($"| {g.Key.Folder} | {g.Key.Temperature} | {g.Key.Pipeline} | {r.Count} | {r[0].Files} | {MR(r, x => x.ShareCellsWithPictureWhileVisible * 100, "0")} % | {MR(r, x => x.ShareVisibleTimeWithPicture * 100, "0")} % | {MR(r, x => x.ShareCellsAnsweredWhileVisible * 100, "0")} % | {MR(r, x => x.ShareVisibleTimeAnswered * 100, "0")} % | {MR(r, x => x.LastScreenCompleteMs)} | {MR(r, x => x.IssueMsMaxTick, "0.0")} | {Cnt(r, x => x.StoreHits)} | {Cnt(r, x => x.Decodes)} | {MR(r, x => x.Load, "0")}% |\n");
        }
        foreach (var pipe in new[] { "new-off", "new-on", "new" })
        {
            var exif = all.Where(r => r.Folder == "exif" && r.Pipeline == pipe && r.Temperature == "cold").ToList();
            if (exif.Count > 0)
                sb.Append($"\nScreen of {exif[0].Files} JPEGs with EXIF thumbnails, {pipe}, cold: cells whose stand-in arrived before their final picture, per run: {string.Join(", ", exif.Select(x => $"{x.LowBeforeFinal}/{x.Files}"))} (EXIF stand-ins read: {string.Join(", ", exif.Select(x => x.EmbeddedLows))}; first stand-in at {string.Join(", ", exif.Select(x => x.FirstLowMs))} ms, first final at {string.Join(", ", exif.Select(x => x.FirstMs))} ms).\n");
        }
        var second = all.Where(r => r.Pipeline.StartsWith("new", StringComparison.Ordinal) && r.Temperature is "warm-new-process" or "filled-new-process").ToList();
        if (second.Count > 0)
        {
            sb.Append("\nSecond visit, new pipelines in a new process (new-on: store filled), per scenario: store hits / own decodes and extractions / Windows extractions / Windows cache lookups / bytes read by the process / of which store payload:\n\n");
            foreach (var r in second.OrderBy(r => PipelineOrder(r.Pipeline)).ThenBy(r => r.Folder).ThenBy(r => r.Cells).ThenBy(r => r.Run))
                sb.Append($"- {r.Pipeline} {r.Folder} {r.Scenario} {r.Cells}@{r.Size} run {r.Run + 1}: {r.StoreHits} / {r.Decodes} / {r.ShellExtracts} / {r.CacheProbes} / {r.ProcBytes / 1e6:0.00} MB / {r.StoreBytes / 1e6:0.00} MB\n");
        }
        return sb.ToString();
    }

    private static int TempOrder(string t) => t switch { "cold" => 0, "warm-same-process" => 1, "warm-new-process" => 2, _ => 3 };

    private static int PipelineOrder(string p) => p switch { "today" => 0, "new-off" => 1, _ => 2 };

    private static string MR(List<GridResult> r, Func<GridResult, double> f, string fmt = "0")
    {
        var v = r.Select(f).Where(x => !double.IsNaN(x)).ToList();
        if (v.Count == 0) return "-";
        string med = Stats.Median(v).ToString(fmt);
        return v.Count == 1 ? med : $"{med} [{v.Min().ToString(fmt)}-{v.Max().ToString(fmt)}]";
    }

    private static string Cnt(List<GridResult> r, Func<GridResult, long> f, double div = 1, string fmt = "0")
    {
        var v = r.Select(f).Where(x => x >= 0).Select(x => x / div).ToList();
        if (v.Count == 0) return "-";
        string med = Stats.Median(v).ToString(fmt);
        return v.Min() == v.Max() ? med : $"{med} [{v.Min().ToString(fmt)}-{v.Max().ToString(fmt)}]";
    }
}
