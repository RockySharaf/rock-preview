using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using Anything.Core.Content;
using Anything.Preview;
using Anything.Preview.UI;

namespace PreviewHarness;

/// <summary>
/// Headless check of the match path on real files (PreviewHarness --match --real LISTFILE [--word WORD]): each file is
/// rendered plainly and with PreferBuiltInText (and the last sheet of workbooks), the built text is highlighted, and a
/// text window past the first megabyte is loaded. Files are only read; only kinds, counts and times are logged, never text.
/// </summary>
internal static class MatchReal
{
    public static int Run(string listFile, string word, Action<string> log)
    {
        log($"\n## Real files at a match (read-only), word '{word}'\n");
        log("| File | Plain | With match | ms plain / match | Matches | Extra |");
        log("|---|---|---|---|---|---|");
        var find = Finder(word);
        int failures = 0, n = 0, missing = 0;
        foreach (var raw in File.ReadAllLines(listFile))
        {
            // "ext<TAB>path" or just a path, as for the plain --real pass.
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string path = (line.Contains('\t') ? line[(line.IndexOf('\t') + 1)..] : line).Trim();
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }
            n++;
            string name = $"#{n} {Path.GetExtension(path).ToLowerInvariant()} {new FileInfo(path).Length / 1024:N0} KB";
            try
            {
                var target = new Size(900, 700);
                var sw = Stopwatch.StartNew();
                using var plain = PreviewRenderer.RenderAsync(path, target, new PreviewOptions { AllowHostedHandler = false }, CancellationToken.None).GetAwaiter().GetResult();
                long plainMs = sw.ElapsedMilliseconds;
                sw.Restart();
                using var match = PreviewRenderer.RenderAsync(path, target, new PreviewOptions { AllowHostedHandler = false, PreferBuiltInText = true }, CancellationToken.None).GetAwaiter().GetResult();
                long matchMs = sw.ElapsedMilliseconds;
                string matches = Count(match, find, out string extra);
                if (match.Kind == PreviewKind.Table && plain.GetMeta("Sheets") is { } sheets && sheets.Split(", ") is { Length: > 1 } names)
                {
                    // The Sheets line lists 12 names at most, then an ellipsis.
                    string last = names.Last(nm => nm.Trim() is { Length: > 0 } t && t != "…").Trim();
                    using var other = PreviewRenderer.RenderAsync(path, target, new PreviewOptions { AllowHostedHandler = false, PreferBuiltInText = true, Sheet = last },
                        CancellationToken.None).GetAwaiter().GetResult();
                    extra += $"; sheet {Array.IndexOf(names, names.First(nm => nm.Trim() == last)) + 1} by name: {(other.Sheet == last ? "shown" : "**not shown**")}";
                    if (other.Sheet != last) failures++;
                }
                if (plain.Kind != match.Kind && !(match.Kind == PreviewKind.Document && plain.Kind == PreviewKind.Image)) extra += $"; kind changed";
                log($"| {name} | {plain.Kind}/{plain.Provider} | {match.Kind}/{match.Provider}{(match.ShapedByMatch ? " (shaped)" : "")} | {plainMs} / {matchMs} | {matches} | {extra} |");
            }
            catch (Exception ex)
            {
                failures++;
                log($"| {name} | **{ex.GetType().Name}** | | | | {ex.Message.Replace('|', '/')} |");
            }
        }
        log($"\n- real files: {n}, failures: {failures}, listed but not found: {missing}");
        if (n == 0)
        {
            // An empty run is not a pass.
            log("- **no listed file could be read**");
            failures++;
        }
        return failures;
    }

    /// <summary>Matches in what the pane would show, and the cost of showing them (no text is logged).</summary>
    private static string Count(PreviewResult r, Func<string, IReadOnlyList<(int Start, int Length)>> find, out string extra)
    {
        extra = "";
        var sw = Stopwatch.StartNew();
        switch (r.Kind)
        {
            case PreviewKind.Text when r.Text != null:
            {
                var lines = r.Text.Split((char)10); // stats only (the view splits on every line end)
                int hits = lines.Sum(l => find(l).Count);
                extra = $"{lines.Length:N0} lines, find {sw.ElapsedMilliseconds} ms";
                if (r.TextTruncated)
                {
                    sw.Restart();
                    var w = TextWindow.LoadAsync(r.Path, lines.Length + 5000, 1 << 20, CancellationToken.None).GetAwaiter().GetResult();
                    extra += w is { } win ? $"; window at line {lines.Length + 5000:N0}: from {win.FirstLine:N0}, {win.Text.Length / 1024:N0} K chars, {sw.ElapsedMilliseconds} ms"
                                          : $"; window: none ({sw.ElapsedMilliseconds} ms)";
                }
                return hits.ToString("N0");
            }
            case PreviewKind.Document when r.Document != null:
            {
                int hits = r.Document.Sum(b => find(b.Text).Count);
                string rtf = DocumentView.BuildRtf(r.Document, find);
                extra = $"{r.Document.Count:N0} paragraphs, RTF with matches {rtf.Length / 1024:N0} KB in {sw.ElapsedMilliseconds} ms";
                return hits.ToString("N0");
            }
            case PreviewKind.Document when r.Rtf != null:
                return find(r.Rtf).Count.ToString("N0") + " (in the RTF source)";
            case PreviewKind.Table when r.Table != null:
            {
                int hits = r.Table.Rows.Sum(row => row.Count(c => c.Length > 0 && find(c).Count > 0));
                extra = $"{r.Table.Rows.Count:N0} rows, scan {sw.ElapsedMilliseconds} ms" + (r.Sheet != null ? $", sheet shown {(r.SheetFromOptions ? "by name" : "first")}" : "");
                return hits.ToString("N0") + " cells";
            }
            default:
                return "-";
        }
    }

    private static Func<string, IReadOnlyList<(int Start, int Length)>> Finder(string word) => text =>
    {
        var found = new List<(int Start, int Length)>();
        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase))
            found.Add((i, word.Length));
        return found;
    };
}
