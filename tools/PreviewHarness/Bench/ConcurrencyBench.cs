using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace PreviewHarness.Bench;

/// <summary>
/// Throughput of Windows' extractor with 1, 2, 4, 8 and 12 calls at once on STA threads, per family (JPEG, RAW,
/// video), each level on its own set of fresh copies (every extraction cold, checked with a cache-only miss first),
/// levels in an interleaved order and each level twice. Also Rock Preview's own scaled decode at the same levels
/// (no cold state to keep: same files every time).
/// </summary>
internal static class ConcurrencyBench
{
    public sealed class LevelRow
    {
        public string Family { get; set; } = "";
        public string Source { get; set; } = "";
        public int Threads { get; set; }
        public int Rep { get; set; }
        public int Files { get; set; }
        public int Ok { get; set; }
        public int NotCold { get; set; }
        public double WallMs { get; set; }
        public double FilesPerSec { get; set; }
        public double MedianCallMs { get; set; }
        public double P90CallMs { get; set; }
        public double CpuMs { get; set; }
        public double DllhostCpuMs { get; set; }
        public double Load { get; set; }
        public double LoadMax { get; set; }
        public bool HighLoad { get; set; }
    }

    public static void Run(Corpus corpus, string benchDir, string coldDir, Action<string> log)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var outRows = new List<LevelRow>();
        int[] levels = { 1, 4, 12, 2, 8 };
        var families = new List<(string Name, List<CorpusFile> Pool, int Count)>
        {
            ("jpeg 12-24 MP", corpus.Files.Where(f => f.Ext is ".jpg" && f.Origin == "made" && f.Megapixels is >= 12 and <= 24).ToList(), 24),
            ("raw", corpus.Files.Where(f => Anything.Preview.WicImage.RawExtensions.Contains(f.Ext)).ToList(), 16),
            ("video", corpus.Files.Where(f => CorpusBuilder.RealVideos.Contains(f.Ext) && f.Bytes < 45_000_000).ToList(), 12),
        };
        foreach (var (name, pool0, count) in families)
        {
            if (pool0.Count == 0) { log($"concurrency: no {name} files"); continue; }
            var pool = pool0.OrderBy(f => f.Bytes).ToList();
            log($"concurrency: {name}: pool of {pool.Count} files, {count} cold copies per level");
            for (int rep = 0; rep < 2; rep++)
                foreach (int n in levels)
                {
                    var set = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        var f = pool[(i * pool.Count / count + rep + n) % pool.Count];
                        string dst = Path.Combine(coldDir, $"k{stamp}-{name}-{n}-{rep}-{i:00}{f.Ext}");
                        SourcesBench.CopyFile(corpus.Full(f), dst);
                        set.Add(dst);
                    }
                    Thread.Sleep(300);
                    try
                    {
                        var row = Shell(name, set, n, rep);
                        outRows.Add(row);
                        log($"  {name} shell x{n}: {row.FilesPerSec:0.0} files/s, median call {row.MedianCallMs:0} ms, cpu {row.CpuMs:0} ms, dllhost {row.DllhostCpuMs:0} ms, load {row.Load:0}%{(row.NotCold > 0 ? $", {row.NotCold} NOT cold" : "")}");
                        if (name != "video")
                        {
                            var own = Own(name, set, n, rep);
                            outRows.Add(own);
                            log($"  {name} own  x{n}: {own.FilesPerSec:0.0} files/s, median call {own.MedianCallMs:0} ms, cpu {own.CpuMs:0} ms, load {own.Load:0}%");
                        }
                    }
                    finally
                    {
                        foreach (var p in set) SourcesBench.TryDelete(p);
                    }
                }
        }
        File.WriteAllText(Path.Combine(benchDir, $"concurrency-{stamp}.json"), JsonSerializer.Serialize(outRows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LevelRow Shell(string family, List<string> files, int threads, int rep)
    {
        var workers = Enumerable.Range(0, threads).Select(i => new StaWorker("conc STA " + i)).ToList();
        try
        {
            // Cold check first, on one worker (cache-only never extracts: see the sources phase).
            int notCold = 0;
            foreach (var p in files)
                if (workers[0].Run(() => ShellProbe.Get(p, 256, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk)).Hr == 0) notCold++;
            return Measure(family, "shell-extract", files, threads, rep, notCold, (i, p) =>
                workers[i].Run(() => ShellProbe.Get(p, 256, ShellProbe.ThumbnailOnly | ShellProbe.BiggerSizeOk), 120000).Hr == 0);
        }
        finally { foreach (var w in workers) w.Dispose(); }
    }

    private static LevelRow Own(string family, List<string> files, int threads, int rep)
    {
        return Measure(family, "own-scaled", files, threads, rep, 0, (i, p) =>
        {
            var (s, _) = CountingStream.Open(p);
            using (s) return SourceProbes.OwnScaled(s, Path.GetExtension(p).ToLowerInvariant(), 256).Ok;
        });
    }

    private static LevelRow Measure(string family, string source, List<string> files, int threads, int rep, int notCold, Func<int, string, bool> call)
    {
        var queue = new ConcurrentQueue<string>(files);
        var times = new ConcurrentBag<double>();
        int ok = 0;
        var mon = LoadMonitor.Shared;
        var dll0 = Dllhost();
        var a = ProcessMeter.Take();
        double t0 = mon.Now;
        var sw = Stopwatch.StartNew();
        var threadsList = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            while (queue.TryDequeue(out var p))
            {
                var c = Stopwatch.StartNew();
                bool r = false;
                try { r = call(i, p); } catch { }
                times.Add(c.Elapsed.TotalMilliseconds);
                if (r) Interlocked.Increment(ref ok);
            }
        }) { IsBackground = true }).ToList();
        threadsList.ForEach(t => t.Start());
        threadsList.ForEach(t => t.Join());
        double wall = sw.Elapsed.TotalMilliseconds;
        var b = ProcessMeter.Take();
        var dll1 = Dllhost();
        var (load, max) = mon.BetweenSettled(t0, mon.Now);
        return new LevelRow
        {
            Family = family, Source = source, Threads = threads, Rep = rep, Files = files.Count, Ok = ok, NotCold = notCold, WallMs = Math.Round(wall),
            FilesPerSec = Math.Round(files.Count / (wall / 1000), 2), MedianCallMs = Stats.R(Stats.Median(times)), P90CallMs = Stats.R(Stats.Percentile(times, 90)),
            CpuMs = Math.Round(ProcessMeter.CpuMs(a, b)), DllhostCpuMs = Math.Round((dll1 - dll0).TotalMilliseconds), Load = Stats.R(load), LoadMax = Stats.R(max), HighLoad = load > 60,
        };
    }

    private static TimeSpan Dllhost()
    {
        var t = TimeSpan.Zero;
        foreach (var p in Process.GetProcessesByName("dllhost")) { try { t += p.TotalProcessorTime; } catch { } p.Dispose(); }
        return t;
    }
}
