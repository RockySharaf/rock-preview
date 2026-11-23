using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PreviewHarness.Bench;

/// <summary>
/// The thumbnail benchmark (console only, nothing on screen).
///   PreviewHarness --thumbbench PHASE --out PROBEDIR [options]
/// PHASE: corpus | identity | sources | concurrency | store | grid | servicecheck | whynone | summary
///   corpus       [--stills-mb 800] [--video-mb 500] [--skip-made] [--skip-real]
///   sources      [--only TEXT] [--limit N]
///   grid         [--pipelines today,new-off,new-on] [--runs 3] [--only TEXT]   (TEXT picks scenarios by name, e.g. "mixed-screen")
/// PROBEDIR must be the .probe folder of a working copy; everything is written under it (corpus\, cold\, bench\).
/// Every child process this starts has a time limit and is ended by its id.
/// </summary>
internal static class ThumbBench
{
    private static StreamWriter? _log;

    public static int Run(string[] args)
    {
        string phase = Arg(args, "--thumbbench") ?? "";
        string? probe = Arg(args, "--out");
        if (probe == null || !Path.GetFileName(Path.TrimEndingDirectorySeparator(probe)).Equals(".probe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--out must name a .probe folder");
            return 2;
        }
        probe = Path.GetFullPath(probe);
        // A built-in time limit: the process ends itself even if a shell extension hangs.
        int maxMinutes = int.Parse(Arg(args, "--max-minutes") ?? (phase == "grid-run" ? "25" : "150"));
        var watchdog = new Thread(() => { Thread.Sleep(TimeSpan.FromMinutes(maxMinutes)); Console.Error.WriteLine("time limit reached"); Environment.Exit(3); }) { IsBackground = true };
        watchdog.Start();
        string corpusDir = Path.Combine(probe, "corpus"), coldDir = Path.Combine(probe, "cold"), benchDir = Path.Combine(probe, "bench");
        Directory.CreateDirectory(benchDir);
        Directory.CreateDirectory(coldDir);
        if (phase == "grid-run")
        {
            return GridBench.RunChild(Arg(args, "--set")!, Arg(args, "--folder")!, Arg(args, "--kind")!, int.Parse(Arg(args, "--cells")!),
                int.Parse(Arg(args, "--cols")!), int.Parse(Arg(args, "--size")!), Arg(args, "--temp")!, Arg(args, "--result")!, Arg(args, "--pipelines") ?? "today");
        }
        // Shared, so a second phase (a diagnosis) can log while a long one runs.
        string logName = phase is "whynone" or "servicecheck" ? $"log-{phase}.txt" : "log.txt";
        _log = new StreamWriter(new FileStream(Path.Combine(benchDir, logName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Log($"== {DateTime.Now:yyyy-MM-dd HH:mm:ss} phase {phase}, {Environment.ProcessorCount} logical processors, clock {ProcessMeter.Mhz} MHz");
        _ = LoadMonitor.Shared;
        try
        {
            switch (phase)
            {
                case "corpus":
                    var c = CorpusBuilder.Build(corpusDir, long.Parse(Arg(args, "--stills-mb") ?? "800") << 20, long.Parse(Arg(args, "--video-mb") ?? "500") << 20,
                        args.Contains("--skip-made"), args.Contains("--skip-real"), Log);
                    Log(CorpusTable(c));
                    break;
                case "identity":
                    Identity(Corpus.Load(corpusDir), coldDir);
                    break;
                case "sources":
                    SourcesBench.Run(Corpus.Load(corpusDir), benchDir, coldDir, Arg(args, "--only"), int.Parse(Arg(args, "--limit") ?? "0"), Log);
                    break;
                case "concurrency":
                    ConcurrencyBench.Run(Corpus.Load(corpusDir), benchDir, coldDir, Log);
                    break;
                case "store":
                    StoreBench.Run(Corpus.Load(corpusDir), benchDir, Log);
                    break;
                case "grid":
                    GridBench.Run(Corpus.Load(corpusDir), probe, benchDir, coldDir, Arg(args, "--pipelines") ?? "today,new-off,new-on",
                        Math.Max(1, int.Parse(Arg(args, "--runs") ?? "3")), Arg(args, "--only"), Log);
                    break;
                case "servicecheck":
                    return ServiceCheck.Run(Corpus.Load(corpusDir), coldDir, benchDir, Log);
                case "whynone":
                    return WhyNone.Run(Corpus.Load(corpusDir), coldDir, benchDir, Arg(args, "--only") ?? "mixed-screen-60at160", Log);
                case "summary":
                    var md = BenchSummary.Build(Corpus.Load(corpusDir), benchDir);
                    string path = Path.Combine(benchDir, "summary.md");
                    File.WriteAllText(path, md);
                    Console.WriteLine(md);
                    break;
                default:
                    Console.Error.WriteLine("unknown phase " + phase);
                    return 2;
            }
            var loads = LoadMonitor.Shared.AllLoads();
            if (loads.Count > 0)
                Log($"PC load during this phase: median {Stats.Median(loads):0}%, p90 {Stats.Percentile(loads, 90):0}%, {loads.Count(l => l > 60) * 100.0 / loads.Count:0}% of samples above 60%");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            return 1;
        }
        finally
        {
            _log?.Dispose();
        }
    }

    public static void Log(string s)
    {
        Console.WriteLine(s);
        _log?.WriteLine(s);
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public static string CorpusTable(Corpus c)
    {
        var sb = new StringBuilder("\n| Origin | Kind | Files | Total MB | Median MB | Median MP |\n|---|---|---|---|---|---|\n");
        foreach (var g in c.Files.GroupBy(f => (f.Origin, f.Ext)).OrderBy(g => g.Key.Origin).ThenBy(g => g.Key.Ext))
        {
            var mp = g.Where(f => f.Megapixels > 0).Select(f => f.Megapixels).ToList();
            sb.Append($"| {g.Key.Origin} | {g.Key.Ext} | {g.Count()} | {g.Sum(f => f.Bytes) / 1e6:0.0} | {Stats.Median(g.Select(f => f.Bytes / 1e6)):0.00} | {(mp.Count > 0 ? Stats.Median(mp).ToString("0.0") : "-")} |\n");
        }
        sb.Append($"\nTotal: {c.Files.Count} files, {c.Files.Sum(f => f.Bytes) / 1e6:0} MB\n");
        return sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);

    /// <summary>
    /// How Windows' thumbnail cache identifies a file: after one extraction, is a renamed file, a hard link, a copy,
    /// or the same file with a new date still a hit? (Tells what "cold" needs and what a store key must contain.)
    /// </summary>
    private static void Identity(Corpus corpus, string coldDir)
    {
        using var sta = new StaWorker("identity");
        string stamp = DateTime.Now.ToString("HHmmss");
        var src = corpus.Files.First(f => f.Id.StartsWith("made/mix-2mp-0", StringComparison.Ordinal));
        const int fl = ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk;
        string a = Path.Combine(coldDir, $"id{stamp}-a.jpg");
        SourcesBench.CopyFile(corpus.Full(src), a);
        string Probe(string p) { var r = sta.Run(() => ShellProbe.Get(p, 256, fl)); return r.Hr == 0 ? $"hit {r.Width}x{r.Height} ({r.Ms:0.0} ms)" : $"miss {ShellProbe.Hr(r.Hr)} ({r.Ms:0.0} ms)"; }
        Log($"identity: fresh copy, cache only: {Probe(a)}");
        var ex = sta.Run(() => ShellProbe.Get(a, 256, ShellProbe.ThumbnailOnly | ShellProbe.BiggerSizeOk));
        Log($"identity: extraction {ShellProbe.Hr(ex.Hr)} {ex.Width}x{ex.Height} {ex.Ms:0} ms; cache only now: {Probe(a)}");
        string b = Path.Combine(coldDir, $"id{stamp}-b-renamed.jpg");
        File.Move(a, b);
        Log($"identity: same file renamed: {Probe(b)}");
        string link = Path.Combine(coldDir, $"id{stamp}-c-hardlink.jpg");
        bool linked = CreateHardLink(link, b, IntPtr.Zero);
        Log($"identity: hard link to it: {(linked ? Probe(link) : "could not create")}");
        string copy = Path.Combine(coldDir, $"id{stamp}-d-copy.jpg");
        SourcesBench.CopyFile(b, copy);
        Log($"identity: byte-identical copy: {Probe(copy)}");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(-5));
        Log($"identity: same file, new date: {Probe(b)}");
        foreach (var p in new[] { b, link, copy }) SourcesBench.TryDelete(p);
    }
}
