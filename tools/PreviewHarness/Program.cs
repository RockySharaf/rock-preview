using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Anything.Preview;
using Anything.Preview.Interop;

namespace PreviewHarness;

/// <summary>
/// Headless + UI smoke harness for Anything.Preview.
///   PreviewHarness [--out DIR] [--real LISTFILE] [--pane] [--save] [--no-thumbs]
///   PreviewHarness --match [--out DIR]   (the preview at a search match: samples, steps, screenshots)
///   PreviewHarness --match --real LISTFILE [--word WORD]   (headless, real files read-only: counts and times only)
/// LISTFILE lines: "ext&lt;TAB&gt;path" (or just a path). Real files are only read.
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Report = new();

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = Arg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "AnythingPreviewHarness");
        string? realList = Arg(args, "--real");
        bool pane = args.Contains("--pane");
        bool save = args.Contains("--save");
        bool thumbs = !args.Contains("--no-thumbs");
        string sampleDir = Path.Combine(outDir, "samples");
        string renderDir = Path.Combine(outDir, "renders");
        Directory.CreateDirectory(renderDir);

        Log($"# Anything.Preview harness  {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (args.Contains("--match"))
        {
            if (realList != null && !File.Exists(realList))
            {
                Log($"- **the list {realList} doesn't exist**");
                return 1;
            }
            int matchErrors = realList != null
                ? MatchReal.Run(realList, Arg(args, "--word") ?? "the", Log)
                : MatchPass.Run(outDir, Log);
            File.WriteAllText(Path.Combine(outDir, "report-match.md"), Report.ToString());
            Log($"\nReport: {Path.Combine(outDir, "report-match.md")}");
            return matchErrors == 0 ? 0 : 1;
        }
        var generated = SampleGenerator.Generate(sampleDir);
        var real = new List<string>();
        if (realList != null && File.Exists(realList))
        {
            var perExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(realList))
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                string p = line.Contains('\t') ? line[(line.IndexOf('\t') + 1)..] : line;
                if (!File.Exists(p)) continue;
                string e = Path.GetExtension(p).ToLowerInvariant();
                perExt.TryGetValue(e, out int n);
                if (n >= 2) continue;
                perExt[e] = n + 1;
                real.Add(p);
            }
        }

        int failures = 0;
        if (!args.Contains("--pane-only"))
        {
            failures += RunRenderer("Generated samples", generated.Append(sampleDir).ToList(), save ? renderDir : null, thumbs);
            if (real.Count > 0) failures += RunRenderer("Real files on this machine (read-only)", real, save ? renderDir : null, thumbs);
            if (thumbs) RunThumbnailStress(generated.Concat(real).ToList());
        }
        if (pane) failures += RunPane(generated, real, renderDir);

        File.WriteAllText(Path.Combine(outDir, "report.md"), Report.ToString());
        Log($"\nReport: {Path.Combine(outDir, "report.md")}");
        return failures == 0 ? 0 : 1;
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static void Log(string s)
    {
        Console.WriteLine(s);
        Report.AppendLine(s);
    }

    private static string Describe(PreviewResult r)
    {
        var parts = new List<string>();
        if (r.Image != null) parts.Add($"{r.Image.Width}x{r.Image.Height}");
        if (r.PageCount > 1) parts.Add($"{r.PageCount} {(r.Frames != null ? "frames" : "pages")}");
        if (r.Text != null) parts.Add($"{r.Text.Length} chars");
        if (r.Document != null) parts.Add($"{r.Document.Count} paras");
        if (r.Rtf != null) parts.Add("rtf");
        if (r.Table != null) parts.Add($"{r.Table.Rows.Count}x{r.Table.Columns.Count} cells");
        if (r.HexDump != null) parts.Add("hex");
        if (r.Media != null) parts.Add(r.Media.Duration is { } d ? $"{d.TotalSeconds:0.0}s" : "media");
        foreach (var k in new[] { "Dimensions", "Pages", "Preview", "Version", "Family", "Resolution" })
            if (r.GetMeta(k) is { } v) parts.Add($"{k}={v}");
        if (r.Diagnostics.Count > 0) parts.Add("tried: " + string.Join(" | ", r.Diagnostics.Select(d => d.Length > 80 ? d[..80] : d)));
        return string.Join("; ", parts);
    }

    private static int RunRenderer(string title, List<string> files, string? saveDir, bool thumbs)
    {
        Log($"\n## {title}\n");
        Log("| File | Kind | Provider | OK | ms | Details | Handler | Thumb |");
        Log("|---|---|---|---|---|---|---|---|");
        int failures = 0;
        foreach (var f in files)
        {
            var sw = Stopwatch.StartNew();
            string kind, provider, ok, details;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var r = PreviewRenderer.RenderAsync(f, new Size(900, 700), cts.Token).GetAwaiter().GetResult();
                sw.Stop();
                kind = r.Kind.ToString();
                provider = r.Provider;
                bool content = r.Kind != PreviewKind.Info || Directory.Exists(f);
                ok = content ? "yes" : "fallback";
                details = Describe(r);
                if (saveDir != null && r.Image != null)
                    r.Image.Save(Path.Combine(saveDir, Path.GetFileName(f.TrimEnd('\\')) + ".png"), ImageFormat.Png);
                // Exercise lazy pages.
                if (r.Pages != null && r.Pages.Count > 1)
                {
                    using var p2 = r.Pages.RenderAsync(1, new Size(900, 700), CancellationToken.None).GetAwaiter().GetResult();
                    details += $"; page2={p2.Width}x{p2.Height}";
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                kind = "EXCEPTION";
                provider = "-";
                ok = "NO";
                details = ex.GetType().Name + ": " + ex.Message;
                failures++;
            }
            string ext = PreviewRendererExt(f);
            string handler = PreviewHandlerRegistry.TryFind(ext, out _, out var hn) ? (hn ?? "yes") : "-";
            string thumb = "-";
            if (thumbs)
            {
                var tsw = Stopwatch.StartNew();
                try
                {
                    using var t = ThumbnailProvider.GetThumbnailAsync(f, 160, CancellationToken.None).GetAwaiter().GetResult();
                    thumb = t == null ? "null" : $"{t.Width}x{t.Height} {tsw.ElapsedMilliseconds}ms";
                }
                catch (Exception ex) { thumb = "EX " + ex.GetType().Name; }
            }
            Log($"| {Short(f)} | {kind} | {provider} | {ok} | {sw.ElapsedMilliseconds} | {Escape(details)} | {Escape(handler)} | {thumb} |");
        }
        return failures;
    }

    private static string PreviewRendererExt(string f) => Directory.Exists(f) ? "" : Path.GetExtension(f).ToLowerInvariant();
    private static string Short(string f) => f.Length > 70 ? "…" + f[^69..] : f;
    private static string Escape(string s) => s.Replace("|", "/").Replace("\n", " ");

    private static void RunThumbnailStress(List<string> files)
    {
        Log("\n## Thumbnail stress (grid simulation)\n");
        // 1) cached re-request should be instant
        var sw = Stopwatch.StartNew();
        int ok = 0;
        foreach (var f in files)
        {
            using var b = ThumbnailProvider.GetThumbnailAsync(f, 160, CancellationToken.None).GetAwaiter().GetResult();
            if (b != null) ok++;
        }
        Log($"- cached pass: {ok}/{files.Count} thumbnails in {sw.ElapsedMilliseconds} ms");
        // 2) scrolling: request everything at a new size in parallel, cancel most of them immediately
        ThumbnailProvider.ClearCache();
        sw.Restart();
        var tasks = new List<Task>();
        int cancelled = 0, done = 0, nulls = 0;
        foreach (var f in files.Concat(files))
        {
            var cts = new CancellationTokenSource();
            var t = ThumbnailProvider.GetThumbnailAsync(f, 96, cts.Token);
            if (tasks.Count % 3 != 0) cts.Cancel();
            tasks.Add(t.ContinueWith(x =>
            {
                if (x.IsCanceled) Interlocked.Increment(ref cancelled);
                else if (x.IsCompletedSuccessfully) { if (x.Result == null) Interlocked.Increment(ref nulls); else Interlocked.Increment(ref done); x.Result?.Dispose(); }
            }));
        }
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(120));
        Log($"- scroll simulation: {tasks.Count} requests, {cancelled} cancelled, {done} completed, {nulls} null, {sw.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ UI smoke test

    private static int RunPane(List<string> generated, List<string> real, string renderDir)
    {
        Log("\n## PreviewPane in a WinForms host\n");
        int errors = 0;
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { errors++; Log("- UI EXCEPTION: " + e.Exception); };
        var form = new Form { Text = "Anything.Preview harness", Width = 1000, Height = 760, StartPosition = FormStartPosition.Manual, Left = 40, Top = 40 };
        var pane = new PreviewPane { Dock = DockStyle.Fill };
        form.Controls.Add(pane);
        string Pick(string name) => generated.FirstOrDefault(g => g.EndsWith(name, StringComparison.OrdinalIgnoreCase)) ?? "";
        string? RealExt(string ext) => real.FirstOrDefault(r => r.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var sequence = new List<string?>
        {
            Pick("alpha.png"), Pick("multipage.tif"), Pick("two-pages.pdf"), Pick("animated.gif"), Pick("Program.cs"), Pick("report.docx"),
            Pick("numbers.xlsx"), Pick("data.csv"), Pick("bundle.zip"), Pick("drawing.dxf"), Pick("random.bin"), Pick("letter.rtf"),
            Pick("tone.wav"), Path.GetDirectoryName(Pick("alpha.png")), RealExt(".mp4"), RealExt(".mp3"), RealExt(".docx"), RealExt(".xlsx"),
            RealExt(".pptx"), RealExt(".cdr"), RealExt(".ai"), RealExt(".ttf"), RealExt(".otf"), RealExt(".pdf"),
        };
        form.Shown += async (_, _) =>
        {
            try
            {
                int shot = 0;
                foreach (var f in sequence.Where(s => !string.IsNullOrEmpty(s)))
                {
                    var sw = Stopwatch.StartNew();
                    await pane.ShowAsync(f);
                    await Task.Delay(f!.EndsWith(".mp4") || f.EndsWith(".docx") || f.EndsWith(".xlsx") || f.EndsWith(".pptx") ? 2500 : 500);
                    var r = pane.CurrentResult;
                    bool match = r != null && string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase);
                    string diag = r != null && r.Diagnostics.Count > 0 ? "  [" + string.Join(" | ", r.Diagnostics) + "]" : "";
                    Log($"- {Short(f)} → {(r == null ? "null" : r.Kind + " via " + r.Provider)} ({sw.ElapsedMilliseconds} ms){(match ? "" : "  **MISMATCH**")}{diag}");
                    if (!match) errors++;
                    if (shot < 40) Screenshot(form, Path.Combine(renderDir, $"pane_{shot++:00}_{Path.GetFileName(f.TrimEnd('\\'))}.png"));
                }
                // Rapid selection changes: only the last one may be displayed.
                var burst = new[] { Pick("alpha.png"), Pick("two-pages.pdf"), Pick("numbers.xlsx"), Pick("drawing.dxf"), Pick("readme.txt") };
                Task last = Task.CompletedTask;
                foreach (var f in burst) last = pane.ShowAsync(f);
                await last;
                await Task.Delay(300);
                bool okBurst = pane.CurrentPath == burst[^1] && pane.CurrentResult?.Path == burst[^1];
                Log($"- rapid burst of {burst.Length} selections → shows {Path.GetFileName(pane.CurrentResult?.Path)} {(okBurst ? "(correct)" : "**WRONG**")}");
                if (!okBurst) errors++;
                // Hidden pane, then re-shown and resized: the image must refit (no 1px sliver).
                pane.Visible = false;
                await pane.ShowAsync(Pick("photo.bmp"));
                pane.Visible = true;
                await Task.Delay(200);
                Screenshot(form, Path.Combine(renderDir, "pane_reshown.png"));
                form.Width = 600; form.Height = 500;
                await Task.Delay(300);
                Screenshot(form, Path.Combine(renderDir, "pane_resized_small.png"));
                form.Width = 1200; form.Height = 850;
                await Task.Delay(300);
                Screenshot(form, Path.Combine(renderDir, "pane_resized_big.png"));
                pane.GoToPage(1);
                await pane.ShowAsync(Pick("multipage.tif"));
                pane.GoToPage(2);
                await Task.Delay(500);
                Screenshot(form, Path.Combine(renderDir, "pane_tif_page3.png"));
                await pane.ShowAsync(null);
                Log($"- cleared: CurrentPath={(pane.CurrentPath ?? "null")}");
            }
            catch (Exception ex)
            {
                errors++;
                Log("- PANE EXCEPTION: " + ex);
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        pane.Dispose();
        Log($"- UI errors: {errors}");
        return errors;
    }

    private static void Screenshot(Form f, string path)
    {
        try
        {
            using var bmp = new Bitmap(f.Width, f.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Left, f.Top, 0, 0, f.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }
    }
}
