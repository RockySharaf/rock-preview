using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;
using Anything.Preview;
using Anything.Preview.Thumbnails;

namespace PreviewHarness.Bench;

/// <summary>
/// For the files of one grid scenario that the new pipeline answers with the icon only while today's pipeline finds a
/// picture: what each source of the ladder said, by kind of file (counts and answers only, no names).
///   PreviewHarness --thumbbench whynone --out PROBEDIR [--only SCENARIO]
/// </summary>
internal static class WhyNone
{
    public static int Run(Corpus corpus, string coldDir, string benchDir, string scenario, Action<string> log)
    {
        string set = Path.Combine(coldDir, "whynone-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(set);
        var lines = new List<string>();
        try
        {
            var sources = GridBench.SourcesOf(corpus, scenario);
            var files = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                string dst = Path.Combine(set, $"w{i:0000}{Path.GetExtension(sources[i]).ToLowerInvariant()}");
                SourcesBench.CopyFile(sources[i], dst);
                files.Add(dst);
            }
            var answers = new Dictionary<string, Anything.Core.Thumbs.ThumbQuality>(StringComparer.OrdinalIgnoreCase);
            using (var service = new ThumbnailService(new ThumbServiceOptions { Power = NoPowerPolicy.Instance, Name = "whynone" }))
            {
                using var done = new ManualResetEventSlim();
                var client = service.CreateClient(a =>
                {
                    a.Bitmap?.Dispose();
                    if (a.Quality == Anything.Core.Thumbs.ThumbQuality.Low) return;
                    lock (answers)
                    {
                        answers[a.Path] = a.Quality;
                        if (answers.Count >= files.Count) done.Set();
                    }
                });
                client.SubmitViewport(1, files.Select((p, i) => new ThumbRequest(p, ThumbStamp.Unknown, 160, ThumbPriority.Visible, i)).ToList());
                done.Wait(180000);
                client.Dispose();
            }
            var none = files.Where(f => !answers.TryGetValue(f, out var q) || q != Anything.Core.Thumbs.ThumbQuality.Final).ToList();
            lines.Add($"{scenario}: {files.Count} files, {none.Count} without a picture from the new pipeline: " +
                      string.Join(", ", none.GroupBy(f => Path.GetExtension(f)).Select(g => $"{g.Key} x{g.Count()}")));
            using var sta = new StaWorker("whynone");
            foreach (var f in none)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                var route = ThumbKinds.RouteOf(f, false);
                string own;
                try
                {
                    var bmp = route switch
                    {
                        ThumbRoute.Picture => sta.Run(() => WicImage.DecodeThumbnail(f, ext, 256, null, out _, out _)),
                        ThumbRoute.Psd => sta.Run(() => PsdDecoder.Decode(f, new System.Drawing.Size(256, 256), CancellationToken.None, out _, out _)),
                        ThumbRoute.Shell => null,
                        _ => sta.Run(() => ThumbnailProvider.ExtractAsync(f, ext, 256, CancellationToken.None).GetAwaiter().GetResult()),
                    };
                    own = bmp == null ? "nothing" : $"{bmp.Width}x{bmp.Height}";
                    bmp?.Dispose();
                }
                catch (Exception e)
                {
                    own = $"{e.GetType().Name} 0x{e.HResult:X8}";
                }
                var exact = sta.Run(() => ShellProbe.Get(f, 256, ShellProbe.ThumbnailOnly), 30000);
                var bigger = sta.Run(() => ShellProbe.Get(f, 160, ShellProbe.ThumbnailOnly | ShellProbe.BiggerSizeOk), 30000);
                System.Drawing.Bitmap? today = null;
                try { today = TodayThumbnailProvider.GetThumbnailAsync(f, 160, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                lines.Add($"  {ext} ({new FileInfo(f).Length / 1000} KB) route {route}: own {own}; Windows 256 exact {ShellProbe.Hr(exact.Hr)} {exact.Width}x{exact.Height}; " +
                          $"Windows 160 bigger-ok {ShellProbe.Hr(bigger.Hr)} {bigger.Width}x{bigger.Height}; today {(today == null ? "null" : $"{today.Width}x{today.Height}")}");
                today?.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(set, true); } catch { }
        }
        foreach (var l in lines) log(l);
        File.WriteAllLines(Path.Combine(benchDir, $"whynone-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), lines);
        return 0;
    }
}
