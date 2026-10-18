using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>Raster images through Windows Imaging Component (all installed codecs: HEIC/AVIF/WebP/RAW/JXR/DDS…).</summary>
internal static class WicImage
{
    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw",
        ".x3f", ".kdc", ".dcr", ".mrw", ".3fr", ".erf", ".mef", ".mos", ".iiq", ".rwl",
    };

    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".bmp", ".dib", ".rle", ".gif", ".tif", ".tiff", ".ico", ".cur",
        ".webp", ".heic", ".heif", ".hif", ".avif", ".jxr", ".wdp", ".hdp", ".dds", ".jxl",
    };

    static WicImage()
    {
        foreach (var e in RawExtensions) Extensions.Add(e);
    }

    public static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md)
            {
                object? o = md.GetQuery("System.Photo.Orientation");
                if (o is ushort u) return u;
                if (o is int i) return i;
            }
        }
        catch
        {
            // formats without metadata readers throw NotSupportedException
        }
        return 1;
    }

    private static string FormatName(string ext, BitmapDecoder? dec)
    {
        string? codec = null;
        try { codec = dec?.CodecInfo?.FriendlyName; } catch { }
        return codec ?? ext.TrimStart('.').ToUpperInvariant() + " image";
    }

    public static async Task<PreviewResult?> RenderAsync(string path, string ext, Size target, CancellationToken ct)
    {
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var gif = TryRenderAnimatedGif(path, target, ct);
            if (gif != null) return gif;
        }

        var box = BitmapUtil.DecodeBox(target);
        using var fs = FileUtil.OpenShared(path, 1 << 16);
        ct.ThrowIfCancellationRequested();
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat |
                                           BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int frameCount = dec.Frames.Count;
        if (frameCount == 0) return null;

        var res = new PreviewResult(path, PreviewKind.Image, "WIC") { FormatName = FormatName(ext, dec) };

        // Icons/cursors: show the biggest, deepest frame.
        int frameIndex = 0;
        bool isIcon = ext is ".ico" or ".cur";
        if (isIcon)
        {
            long best = -1;
            for (int i = 0; i < frameCount; i++)
            {
                var f = dec.Frames[i];
                long score = (long)f.PixelWidth * f.PixelHeight * 64 + f.Format.BitsPerPixel;
                if (score > best) { best = score; frameIndex = i; }
            }
        }

        BitmapFrame frame = dec.Frames[frameIndex];
        int ow = frame.PixelWidth, oh = frame.PixelHeight;
        res.SourcePixelSize = new Size(ow, oh);
        res.HasAlpha = BitmapUtil.FormatHasAlpha(frame.Format) || BitmapUtil.PaletteHasAlpha(frame);
        int orientation = ReadOrientation(frame);

        ct.ThrowIfCancellationRequested();
        Bitmap? bmp = null;
        bool isRaw = RawExtensions.Contains(ext);
        if (isRaw)
        {
            // RAW codecs demosaic slowly; the embedded preview JPEG is usually large enough for a pane.
            try
            {
                var prev = dec.Preview;
                if (prev != null && Math.Max(prev.PixelWidth, prev.PixelHeight) >= Math.Min(1600, Math.Max(box.Width, box.Height) / 2))
                {
                    bmp = BitmapUtil.ToGdi(BitmapUtil.ScaleToFit(prev, BitmapUtil.OrientBox(box, orientation)));
                    res.AddMeta("Decoded", "embedded RAW preview");
                }
            }
            catch { }
        }

        if (bmp == null)
        {
            if (frameCount == 1 && !isIcon && (ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" || isRaw))
            {
                // BitmapImage + DecodePixelWidth lets the JPEG/RAW decoder scale during decode (much faster).
                bmp = DecodeScaled(path, ow, oh, BitmapUtil.OrientBox(box, orientation));
            }
            bmp ??= BitmapUtil.ToGdi(BitmapUtil.ScaleToFit(frame, BitmapUtil.OrientBox(box, orientation)));
        }
        BitmapUtil.ApplyOrientation(bmp, orientation);
        res.Image = bmp;

        res.AddMeta("Dimensions", $"{ow} × {oh}");
        res.AddMeta("Bit depth", frame.Format.BitsPerPixel + "-bit " + DescribeFormat(frame.Format));
        if (frame.DpiX > 0 && Math.Abs(frame.DpiX - 96) > 0.5) res.AddMeta("DPI", $"{frame.DpiX:0} × {frame.DpiY:0}");
        if (orientation != 1) res.AddMeta("Orientation", $"EXIF {orientation} (applied)");
        if (isIcon && frameCount > 1) res.AddMeta("Icon frames", $"{frameCount} (showing {ow}×{oh})");

        if (frameCount > 1 && !isIcon)
        {
            res.Pages = new WicPageSource(path, frameCount, ext is ".gif" ? "Frame" : "Page");
            res.AddMeta(ext is ".gif" ? "Frames" : "Pages", frameCount.ToString());
        }
        await Task.CompletedTask;
        return res;
    }

    private static string DescribeFormat(System.Windows.Media.PixelFormat f)
    {
        string s = f.ToString();
        return s.StartsWith("Indexed", StringComparison.Ordinal) ? "indexed" :
            s.Contains("Gray", StringComparison.Ordinal) ? "grayscale" :
            s.Contains("Cmyk", StringComparison.Ordinal) ? "CMYK" :
            BitmapUtil.FormatHasAlpha(f) ? "with alpha" : "color";
    }

    private static Bitmap? DecodeScaled(string path, int ow, int oh, Size box)
    {
        var fit = BitmapUtil.FitSize(new Size(ow, oh), box);
        if (fit.Width >= ow) return null; // no scaling needed: let the generic path handle it
        try
        {
            using var fs = FileUtil.OpenShared(path, 1 << 16, sequential: true);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = fs;
            // Only constrain one axis so aspect ratio is preserved.
            bi.DecodePixelWidth = fit.Width;
            bi.EndInit();
            bi.Freeze();
            return BitmapUtil.ToGdi(bi);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GDI+ composites GIF frames (disposal methods) for us; fully decode up to a memory budget.</summary>
    private static PreviewResult? TryRenderAnimatedGif(string path, Size target, CancellationToken ct)
    {
        var data = FileUtil.ReadAllCapped(path, 64L * 1024 * 1024);
        if (data == null) return null;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var img = System.Drawing.Image.FromStream(ms);
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int count = img.GetFrameCount(dim);
            if (count <= 1) return null; // static GIF: WIC path

            var box = BitmapUtil.DecodeBox(target);
            var fit = BitmapUtil.FitSize(img.Size, box);
            long perFrame = (long)fit.Width * fit.Height * 4;
            int maxFrames = (int)Math.Clamp(256L * 1024 * 1024 / Math.Max(1, perFrame), 1, 1000);
            int[] delays = new int[count];
            try
            {
                var pi = img.GetPropertyItem(0x5100);
                if (pi?.Value != null)
                    for (int i = 0; i < count && i * 4 + 3 < pi.Value.Length; i++)
                        delays[i] = BitConverter.ToInt32(pi.Value, i * 4) * 10;
            }
            catch { }

            var frames = new List<Bitmap>();
            var frameDelays = new List<int>();
            for (int i = 0; i < Math.Min(count, maxFrames); i++)
            {
                ct.ThrowIfCancellationRequested();
                img.SelectActiveFrame(dim, i);
                frames.Add(BitmapUtil.Resize(img, fit));
                int d = delays[i];
                frameDelays.Add(d <= 10 ? 100 : d); // browsers treat 0/10ms as 100ms
            }
            var res = new PreviewResult(path, PreviewKind.Image, "WIC/GIF")
            {
                FormatName = "Animated GIF",
                Image = frames[0],
                Frames = frames,
                FrameDelaysMs = frameDelays,
                HasAlpha = true,
                SourcePixelSize = img.Size,
            };
            res.AddMeta("Dimensions", $"{img.Width} × {img.Height}");
            res.AddMeta("Frames", count > frames.Count ? $"{count} (first {frames.Count} loaded)" : count.ToString());
            long total = 0;
            foreach (var d in frameDelays) total += d;
            res.AddMeta("Duration", FileUtil.FormatDuration(TimeSpan.FromMilliseconds(total)) + $" ({total / 1000.0:0.0}s)");
            return res;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>Multi-page TIFF/multi-frame sources: each page re-opens the decoder (WPF decoders are thread-affine).</summary>
    private sealed class WicPageSource : IPageSource
    {
        private readonly string _path;
        public WicPageSource(string path, int count, string unit) { _path = path; Count = count; Unit = unit; }
        public int Count { get; }
        public string Unit { get; }

        public Task<Bitmap> RenderAsync(int index, Size target, CancellationToken ct) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var fs = FileUtil.OpenShared(_path, 1 << 16);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat |
                                               BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = dec.Frames[Math.Clamp(index, 0, dec.Frames.Count - 1)];
            int o = ReadOrientation(frame);
            var bmp = BitmapUtil.ToGdi(BitmapUtil.ScaleToFit(frame, BitmapUtil.OrientBox(BitmapUtil.DecodeBox(target), o)));
            BitmapUtil.ApplyOrientation(bmp, o);
            return bmp;
        }, ct);

        public void Dispose() { }
    }
}
