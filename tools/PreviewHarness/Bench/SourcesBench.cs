using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Anything.Preview;

namespace PreviewHarness.Bench;

/// <summary>
/// Every source of a thumbnail on every file of the corpus, keeping cold numbers cold. For each file a fresh copy
/// is made (Windows has never seen it), then, in this order:
///   1. Windows' cache only (InCacheOnly) at the size class: must miss; that miss is the proof the copy is cold.
///      For the first copies of each kind: wait 2 s and ask the cache again (did the miss start an extraction?).
///   2. In-process sources, several rounds, in a rotated order each round (they don't touch Windows' cache).
///   3. Windows' extractor (ThumbnailOnly|BiggerSizeOk): the one cold extraction this copy gives.
///   4. Windows' cache only again at the same size (expect a hit), at 96, and at 512 (with and without BiggerSizeOk).
///   5. The icon.
/// Made files get three copies (three cold extractions of the same content); real files one each.
/// Every fourth file (and the second copy of made files) goes through at 512 instead of 256.
/// The copy is deleted afterwards.
/// </summary>
internal static class SourcesBench
{
    public static void Run(Corpus corpus, string benchDir, string coldDir, string? only, int limit, Action<string> log)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string rowsPath = Path.Combine(benchDir, $"sources-{stamp}.jsonl");
        Directory.CreateDirectory(coldDir);
        using var rows = new StreamWriter(rowsPath);
        var sta = new StaWorker("bench shell STA");
        int fileNo = 0, missTests = 0;
        var perExtMissTests = new Dictionary<string, int>();
        var files = corpus.Files.Where(f => !f.Tags.Contains("mix")).Where(f => only == null || f.Id.Contains(only, StringComparison.OrdinalIgnoreCase) || f.Ext == only).ToList();
        if (limit > 0) files = files.Take(limit).ToList();
        log($"sources: {files.Count} files, rows to {rowsPath}");
        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            int copies = f.Origin == "made" ? 3 : 1;
            for (int copy = 0; copy < copies; copy++)
            {
                bool at512 = f.Origin == "made" ? copy == 1 : fileNo % 4 == 3;
                int size = at512 ? 512 : 256;
                string src = corpus.Full(f);
                string cold = Path.Combine(coldDir, $"c{stamp}-{fileNo:0000}-{copy}{f.Ext}");
                CopyFile(src, cold);
                Thread.Sleep(150); // let the write settle (and any scan of the new file start) before timing
                string kind = KindOf(f);
                ProbeRow New(string source, int sz, int round = 0) => new()
                {
                    Run = stamp, File = f.Id, Kind = kind, Ext = f.Ext, Origin = f.Origin, Mp = f.Megapixels, FileBytes = f.Bytes,
                    Copy = copy, Source = source, Size = sz, Round = round,
                };
                void Emit(ProbeRow r)
                {
                    r.HighLoad = r.Load > 60;
                    rows.WriteLine(JsonSerializer.Serialize(r));
                }
                try
                {
                    // 1. cache only, before any extraction.
                    var before = Shell(sta, cold, size, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk, New("shell-cache-before", size));
                    before.Note = before.Ok ? "HIT before extraction: not cold" : "miss (cold)";
                    Emit(before);
                    perExtMissTests.TryGetValue(f.Ext, out int mt);
                    if (copy == 0 && mt < 2)
                    {
                        perExtMissTests[f.Ext] = mt + 1;
                        missTests++;
                        Thread.Sleep(2000);
                        var again = Shell(sta, cold, size, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk, New("shell-cache-after-miss-2s", size));
                        again.Note = again.Ok ? "HIT: the earlier miss started an extraction" : "still a miss: a miss does not extract";
                        Emit(again);
                    }

                    // 2. in-process sources, rotated order per round.
                    var probes = InProcess(f, cold, size);
                    int rounds = f.Megapixels >= 50 || f.Bytes > 80_000_000 ? 3 : 5;
                    for (int round = 0; round < rounds; round++)
                    {
                        for (int k = 0; k < probes.Count; k++)
                        {
                            var (name, heavy, run) = probes[(k + round) % probes.Count];
                            if (heavy && round >= 2) continue; // full decodes of big files: two rounds are enough
                            var row = New(name, size, round);
                            run(row);
                            Emit(row);
                        }
                    }

                    // 3. the one cold extraction.
                    var dll0 = DllhostCpu();
                    var ex = Shell(sta, cold, size, ShellProbe.ThumbnailOnly | ShellProbe.BiggerSizeOk, New("shell-extract", size), 120000);
                    var dll1 = DllhostCpu();
                    ex.Note = (before.Ok ? "NOT cold" : "cold (cache missed just before)") + $"; dllhost cpu +{(dll1 - dll0).TotalMilliseconds:0} ms";
                    Emit(ex);

                    // 4. cache only, after.
                    var hit = Shell(sta, cold, size, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk, New("shell-cache-after", size));
                    Emit(hit);
                    foreach (var (sz, fl, label) in new[]
                    {
                        (96, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk, "shell-cache-after-96"),
                        (512, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly | ShellProbe.BiggerSizeOk, "shell-cache-after-512-bigger"),
                        (512, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly, "shell-cache-after-512-exact"),
                        (256, ShellProbe.ThumbnailOnly | ShellProbe.InCacheOnly, "shell-cache-after-256-exact"),
                    })
                    {
                        if (sz == size && label.EndsWith("bigger", StringComparison.Ordinal)) continue;
                        var r = Shell(sta, cold, sz, fl, New(label, sz));
                        Emit(r);
                    }

                    // 5. the icon (pixels hashed to compare files of one extension).
                    if (copy == 0)
                    {
                        var ic = New("icon", 160);
                        string? hash = null;
                        SourceProbes.Timed(ic, () =>
                        {
                            var r = sta.Run(() => ShellProbe.Get(cold, 160, ShellProbe.IconOnly, hashPixels: true));
                            hash = r.PixelHash;
                            return new SourceProbes.Res(r.Hr == 0, r.Width, r.Height, "");
                        });
                        ic.Note = hash ?? "";
                        Emit(ic);
                    }
                }
                catch (TimeoutException)
                {
                    log($"  {f.Id}: the shell call timed out; new STA thread");
                    // The wedged thread ends by itself once its call returns: no more work is queued behind it.
                    sta.Dispose();
                    sta = new StaWorker("bench shell STA");
                }
                catch (Exception e)
                {
                    log($"  {f.Id}: {e.GetType().Name} {SourceProbes.Short(e.Message)}");
                }
                finally
                {
                    rows.Flush();
                    TryDelete(cold);
                }
            }
            fileNo++;
            if (fileNo % 10 == 0) log($"  {fileNo}/{files.Count} files, {sw.Elapsed.TotalMinutes:0.0} min");
        }
        sta.Dispose();
        log($"sources: done in {sw.Elapsed.TotalMinutes:0.0} min ({missTests} miss-then-wait tests)");
    }

    public static string KindOf(CorpusFile f)
    {
        if (f.Origin == "made") return "made " + Path.GetFileNameWithoutExtension(f.RelPath);
        string mp = f.Megapixels <= 0 ? "" : f.Megapixels < 6 ? " <6MP" : f.Megapixels < 16 ? " 6-16MP" : f.Megapixels < 30 ? " 16-30MP" : " 30MP+";
        return "real " + f.Ext + mp;
    }

    private static ProbeRow Shell(StaWorker sta, string path, int size, int flags, ProbeRow row, int timeoutMs = 30000)
    {
        ShellProbe.Result? res = null;
        SourceProbes.Timed(row, () =>
        {
            res = sta.Run(() => ShellProbe.Get(path, size, flags), timeoutMs);
            return new SourceProbes.Res(res.Hr == 0 && res.Width > 0, res.Width, res.Height, "");
        });
        row.Hr = res != null ? ShellProbe.Hr(res.Hr) : "";
        return row;
    }

    /// <summary>The in-process sources that apply to this kind of file: (name, heavy, run).</summary>
    private static List<(string Name, bool Heavy, Action<ProbeRow> Run)> InProcess(CorpusFile f, string path, int size)
    {
        var list = new List<(string, bool, Action<ProbeRow>)>();
        string ext = f.Ext;
        bool raster = WicImage.Extensions.Contains(ext);
        bool psd = ext is ".psd" or ".psb";
        if (raster)
        {
            list.Add(("wpf-frame-thumbnail", false, r => SourceProbes.Counted(r, path, SourceProbes.FrameThumbnail)));
            list.Add(("wpf-decoder-thumbnail", false, r => SourceProbes.Counted(r, path, SourceProbes.DecoderThumbnail)));
            list.Add(("wpf-decoder-preview", false, r => SourceProbes.Counted(r, path, SourceProbes.DecoderPreview)));
            list.Add(($"own-scaled", false, r => SourceProbes.Counted(r, path, s => SourceProbes.OwnScaled(s, ext, size))));
            list.Add(($"wic-transform", false, r => SourceProbes.Counted(r, path, s => SourceProbes.Wic(WicDirect.ScaledDecode(s, size)))));
            list.Add(($"wic-scaler", false, r => SourceProbes.Counted(r, path, s => SourceProbes.Wic(WicDirect.ScalerDecode(s, size)))));
            list.Add(("full-decode", f.Megapixels >= 50 || f.Bytes > 80_000_000, r => SourceProbes.Counted(r, path, s => SourceProbes.Wic(WicDirect.FullDecode(s)))));
        }
        if (psd)
        {
            list.Add(("psd-thumbnail-resource", false, r => SourceProbes.Counted(r, path, s => SourceProbes.PsdThumbnail(s, size))));
            list.Add(("psd-composite", false, r => SourceProbes.Timed(r, () => SourceProbes.PsdComposite(path, size))));
        }
        // What ThumbnailProvider runs today when Windows gives nothing (decodes to a 512 box for 160/256 cells).
        list.Add(("own-today", f.Megapixels >= 50 || f.Bytes > 80_000_000, r => SourceProbes.Timed(r, () => SourceProbes.OwnToday(path, ext, size))));
        return list;
    }

    private static TimeSpan DllhostCpu()
    {
        var t = TimeSpan.Zero;
        foreach (var p in Process.GetProcessesByName("dllhost"))
        {
            try { t += p.TotalProcessorTime; } catch { }
            p.Dispose();
        }
        return t;
    }

    public static void CopyFile(string src, string dst)
    {
        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        i.CopyTo(o, 1 << 20);
    }

    public static void TryDelete(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch { Thread.Sleep(200); }
        }
    }
}
