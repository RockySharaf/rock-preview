using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PreviewHarness.Bench;

/// <summary>Turns the latest result files in .probe\bench into tables (counts, sizes and times per kind only).</summary>
internal static class BenchSummary
{
    private static readonly JsonSerializerOptions Opts = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string Build(Corpus corpus, string benchDir)
    {
        var sb = new StringBuilder($"# Thumbnail benchmark summary ({DateTime.Now:yyyy-MM-dd HH:mm})\n");
        sb.Append(ThumbBench.CorpusTable(corpus));
        var rows = new List<ProbeRow>();
        foreach (var f in Directory.GetFiles(benchDir, "sources-*.jsonl").OrderBy(f => f))
            foreach (var line in File.ReadLines(f))
                if (line.Length > 0) rows.Add(JsonSerializer.Deserialize<ProbeRow>(line, Opts)!);
        // Keep the latest run per file (a later run of the same file replaces an earlier one).
        var latest = rows.GroupBy(r => r.File).ToDictionary(g => g.Key, g => g.Max(r => r.Run));
        rows = rows.Where(r => r.Run == latest[r.File]).ToList();
        if (rows.Count > 0) Sources(sb, rows, benchDir);
        var conc = Directory.GetFiles(benchDir, "concurrency-*.json").OrderBy(f => f).LastOrDefault();
        if (conc != null) Concurrency(sb, JsonSerializer.Deserialize<List<ConcurrencyBench.LevelRow>>(File.ReadAllText(conc), Opts)!);
        var store = Directory.GetFiles(benchDir, "store-*.json").OrderBy(f => f).LastOrDefault();
        if (store != null) Store(sb, JsonSerializer.Deserialize<List<StoreBench.Row>>(File.ReadAllText(store), Opts)!);
        var grid = Directory.GetFiles(benchDir, "grid-*.json").OrderBy(f => f).LastOrDefault();
        if (grid != null) Grid(sb, grid);
        return sb.ToString();
    }

    private static string Group(ProbeRow r) => r.Origin == "made" ? r.Kind[5..] + r.Ext : "real " + r.Ext;

    /// <summary>"median ms (ok/n)" of one source in a group; "-" when the source does not apply.</summary>
    private static string Cell(IEnumerable<ProbeRow> g, string source, bool px = false)
    {
        var r = g.Where(x => x.Source == source).ToList();
        if (r.Count == 0) return "-";
        var ok = r.Where(x => x.Ok).ToList();
        var use = ok.Count > 0 ? ok : r;
        string s = Ms(Stats.Median(use.Select(x => x.Ms)));
        if (ok.Count < r.Count) s += $" ({ok.Count}/{r.Count})";
        if (px && ok.Count > 0) s += " " + ok.GroupBy(x => $"{x.W}x{x.H}").OrderByDescending(x => x.Count()).First().Key;
        return s;
    }

    private static void Pivot(StringBuilder sb, List<ProbeRow> rows, int size)
    {
        sb.Append($"\n### Median ms per source at {size} px (ok/n shown when some failed; px = most common result size)\n\n");
        sb.Append("| Kind | files | cache miss | extract (cold) | extract cpu/wall | cache hit | frame thumbnail | decoder preview | own scaled | WIC transform | WIC scaler | full decode | own today | PSD resource | PSD composite | icon |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in rows.Where(r => r.Size == size || r.Source == "icon").GroupBy(Group).OrderBy(g => g.First().Origin).ThenBy(g => g.First().Ext).ThenBy(g => g.First().Mp))
        {
            var ex = g.Where(r => r.Source == "shell-extract" && r.Ok && r.Ms > 5).ToList();
            string ratio = ex.Count > 0 ? Stats.Median(ex.Select(r => r.CpuMs / r.Ms)).ToString("0.00") : "-";
            sb.Append($"| {g.Key} | {g.Select(r => r.File).Distinct().Count()} | {Cell(g, "shell-cache-before")} | {Cell(g, "shell-extract", true)} | {ratio} | {Cell(g, "shell-cache-after")} | " +
                      $"{Cell(g, "wpf-frame-thumbnail", true)} | {Cell(g, "wpf-decoder-preview", true)} | {Cell(g, "own-scaled")} | {Cell(g, "wic-transform", true)} | {Cell(g, "wic-scaler")} | " +
                      $"{Cell(g, "full-decode")} | {Cell(g, "own-today")} | {Cell(g, "psd-thumbnail-resource", true)} | {Cell(g, "psd-composite")} | {Cell(g, "icon")} |\n");
        }
    }

    private static string Ms(double v) => double.IsNaN(v) ? "-" : v < 10 ? v.ToString("0.00") : v.ToString("0");

    private static void Sources(StringBuilder sb, List<ProbeRow> rows, string benchDir)
    {
        sb.Append($"\n## Sources per kind of file\n\n{rows.Select(r => r.File).Distinct().Count()} files, {rows.Count} timed calls. ");
        sb.Append($"Calls above 60 % PC load: {rows.Count(r => r.HighLoad)} ({rows.Count(r => r.HighLoad) * 100.0 / rows.Count:0}%). Every row of every call: sources-detail.md.\n");
        Pivot(sb, rows, 256);
        Pivot(sb, rows, 512);
        var detail = new StringBuilder();
        detail.Append("| Kind | Source | Size | n | ok | median ms | p90 ms | cpu ms | bytes read (counted) | process bytes | result px | high-load n | note |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in rows.GroupBy(r => (Group(r), r.Source, r.Size)).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Source).ThenBy(g => g.Key.Size))
        {
            var ok = g.Where(r => r.Ok).ToList();
            var use = ok.Count > 0 ? ok : g.ToList();
            string px = ok.Count > 0 ? ok.GroupBy(r => $"{r.W}x{r.H}").OrderByDescending(x => x.Count()).First().Key : "-";
            string counted = g.Any(r => r.BytesRead >= 0) ? $"{Stats.Median(g.Where(r => r.BytesRead >= 0).Select(r => (double)r.BytesRead)) / 1024:0} KB" : "not counted";
            string note = g.GroupBy(r => r.Note.Split(';')[0]).OrderByDescending(x => x.Count()).First().Key;
            string hr = g.Select(r => r.Hr).Where(h => h.Length > 0).Distinct().Take(3).Aggregate("", (a, b) => a.Length == 0 ? b : a + "," + b);
            detail.Append($"| {g.Key.Item1} | {g.Key.Source} | {g.Key.Size} | {g.Count()} | {ok.Count}/{g.Count()} | {Ms(Stats.Median(use.Select(r => r.Ms)))} | {Ms(Stats.Percentile(use.Select(r => r.Ms), 90))} | " +
                      $"{Ms(Stats.Median(use.Select(r => r.CpuMs)))} | {counted} | {Stats.Median(use.Select(r => (double)r.ProcBytes)) / 1024:0} KB | {px} | {g.Count(r => r.HighLoad)} | {SourceProbes.Short(note)}{(hr.Length > 0 ? " hr " + hr : "")} |\n");
        }
        File.WriteAllText(Path.Combine(benchDir, "sources-detail.md"), detail.ToString());

        sb.Append("\n## Windows' cache\n\n");
        var extractSize = rows.Where(r => r.Source == "shell-extract").GroupBy(r => (r.File, r.Copy)).ToDictionary(g => g.Key, g => g.First().Size);
        var before = rows.Where(r => r.Source == "shell-cache-before").ToList();
        sb.Append($"- Cache-only before extraction: {before.Count(r => !r.Ok)}/{before.Count} misses; HRESULTs {string.Join(", ", before.Where(r => !r.Ok).GroupBy(r => r.Hr).Select(x => $"{x.Key} x{x.Count()}"))}; " +
                  $"miss time median {Ms(Stats.Median(before.Where(r => !r.Ok).Select(r => r.Ms)))} ms, p90 {Ms(Stats.Percentile(before.Where(r => !r.Ok).Select(r => r.Ms), 90))} ms.\n");
        var again = rows.Where(r => r.Source == "shell-cache-after-miss-2s").ToList();
        sb.Append($"- Miss, wait 2 s, ask again: {again.Count(r => r.Ok)}/{again.Count} hits (a hit would mean the miss started an extraction).\n");
        foreach (var src in new[] { "shell-cache-after", "shell-cache-after-96", "shell-cache-after-512-bigger", "shell-cache-after-512-exact", "shell-cache-after-256-exact" })
        {
            foreach (var g in rows.Where(r => r.Source == src).GroupBy(r => extractSize.TryGetValue((r.File, r.Copy), out int es) ? $"after a {es} px extraction" : "?"))
            {
                var l = g.ToList();
                var sizes = l.Where(r => r.Ok).GroupBy(r => Math.Max(r.W, r.H)).OrderByDescending(x => x.Count()).Take(4).Select(x => $"{x.Key}px x{x.Count()}");
                sb.Append($"- {src} ({g.Key}): {l.Count(r => r.Ok)}/{l.Count} hits, median {Ms(Stats.Median(l.Select(r => r.Ms)))} ms; long side of what came back: {string.Join(", ", sizes)}; misses {string.Join(", ", l.Where(r => !r.Ok).GroupBy(r => r.Hr).Select(x => $"{x.Key} x{x.Count()}"))}\n");
            }
        }
        var ex = rows.Where(r => r.Source == "shell-extract").ToList();
        sb.Append($"- Cold extractions: {ex.Count}; verified cold (cache-only missed just before): {ex.Count(r => r.Note.StartsWith("cold", StringComparison.Ordinal))}; " +
                  $"processor time of this process / wall time, median: {Stats.Median(ex.Where(r => r.Ms > 20).Select(r => r.CpuMs / r.Ms)):0.00}.\n");

        sb.Append("\n## Embedded previews\n\n| Kind | Source | has one | median px | orientation notes |\n|---|---|---|---|---|\n");
        foreach (var g in rows.Where(r => r.Source.StartsWith("wpf-", StringComparison.Ordinal) && r.Round == 0).GroupBy(r => (Group(r), r.Source)).OrderBy(g => g.Key.Item1))
        {
            var ok = g.Where(r => r.Ok).ToList();
            if (ok.Count == 0 && g.All(r => r.Note.StartsWith("not supported", StringComparison.Ordinal) || r.Note == "none")) { sb.Append($"| {g.Key.Item1} | {g.Key.Source} | 0/{g.Count()} | - | - |\n"); continue; }
            string px = ok.Count > 0 ? $"{Stats.Median(ok.Select(r => (double)r.W)):0}x{Stats.Median(ok.Select(r => (double)r.H)):0}" : "-";
            string notes = string.Join("; ", ok.GroupBy(r => r.Note.Split(';')[0]).Select(x => $"{x.Key} x{x.Count()}"));
            sb.Append($"| {g.Key.Item1} | {g.Key.Source} | {ok.Count}/{g.Count()} | {px} | {notes} |\n");
        }

        sb.Append("\n## Icons\n\n| Extension | files | distinct icons | median ms |\n|---|---|---|---|\n");
        foreach (var g in rows.Where(r => r.Source == "icon").GroupBy(r => r.Ext).OrderBy(g => g.Key))
            sb.Append($"| {g.Key} | {g.Count()} | {g.Select(r => r.Note).Distinct().Count()} | {Ms(Stats.Median(g.Select(r => r.Ms)))} |\n");
    }

    private static void Concurrency(StringBuilder sb, List<ConcurrencyBench.LevelRow> rows)
    {
        sb.Append("\n## Calls at once\n\n| Family | Source | threads | reps | files/s (median of reps) | median call ms | process cpu ms / file | dllhost cpu ms | load | not cold |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in rows.GroupBy(r => (r.Family, r.Source, r.Threads)).OrderBy(g => g.Key.Family).ThenBy(g => g.Key.Source).ThenBy(g => g.Key.Threads))
            sb.Append($"| {g.Key.Family} | {g.Key.Source} | {g.Key.Threads} | {g.Count()} | {Stats.Median(g.Select(r => r.FilesPerSec)):0.0} | {Ms(Stats.Median(g.Select(r => r.MedianCallMs)))} | " +
                      $"{Stats.Median(g.Select(r => r.CpuMs / Math.Max(1, r.Files))):0} | {Stats.Median(g.Select(r => r.DllhostCpuMs)):0} | {Stats.Median(g.Select(r => r.Load)):0}% | {g.Sum(r => r.NotCold)} |\n");
    }

    private static void Store(StringBuilder sb, List<StoreBench.Row> rows)
    {
        sb.Append("\n## Store costs\n\n| Test | Codec | Size | Picture | median ms | p90 ms | median bytes | median PSNR dB |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var g in rows.Where(r => r.Test != "file").GroupBy(r => (r.Test, r.Codec, r.Size, r.Note)).OrderBy(g => g.Key.Test).ThenBy(g => g.Key.Size).ThenBy(g => g.Key.Codec))
            sb.Append($"| {g.Key.Test} | {g.Key.Codec} | {g.Key.Size} | {(g.Key.Note.Length > 0 ? g.Key.Note : "all")} | {Stats.Median(g.Select(r => r.MedianUs)) / 1000:0.000} | {Stats.Median(g.Select(r => r.P90Us)) / 1000:0.000} | " +
                      $"{(g.Key.Test == "scale" ? "-" : Stats.Median(g.Select(r => (double)r.Bytes)).ToString("0"))} | {(g.Key.Test == "encode" ? Stats.Median(g.Select(r => r.Psnr)).ToString("0.0") : "-")} |\n");
        sb.Append("\nPSNR per flat picture (JPEG 85): ");
        sb.Append(string.Join("; ", rows.Where(r => r.Test == "encode" && r.Note == "flat" && r.Codec.EndsWith("jpeg85", StringComparison.Ordinal)).Select(r => $"{r.Picture} {r.Size}px {r.Codec} {r.Psnr:0.0} dB")));
        sb.Append("\n\n| File operation | median us | p90 us | load |\n|---|---|---|---|\n");
        foreach (var r in rows.Where(r => r.Test == "file")) sb.Append($"| {r.Codec} | {r.MedianUs:0.0} | {r.P90Us:0.0} | {r.Load:0}% |\n");
    }

    private static void Grid(StringBuilder sb, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        sb.Append($"\n## The grid ({Path.GetFileName(path)}, commit {doc.RootElement.GetProperty("commit").GetString()})\n");
        var results = JsonSerializer.Deserialize<List<GridResult>>(doc.RootElement.GetProperty("results").GetRawText(), Opts)!;
        sb.Append(GridCompare.Markdown(results));
    }
}
