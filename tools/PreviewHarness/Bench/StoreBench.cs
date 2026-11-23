using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Anything.Preview;
using Anything.Preview.Util;

namespace PreviewHarness.Bench;

/// <summary>
/// What a thumbnail store on disk would cost: encoding and decoding 256 and 512 px thumbnails (JPEG 85 and PNG,
/// GDI+ and WPF), the quality of JPEG 85 on flat pictures with text (PSNR), scaling 256 down to 160 and 96, and the
/// file operations the store needs (random 15 KB reads, appends, loading a 100,000-entry index).
/// </summary>
internal static class StoreBench
{
    public sealed class Row
    {
        public string Test { get; set; } = "";
        public string Picture { get; set; } = "";
        public int Size { get; set; }
        public string Codec { get; set; } = "";
        public int N { get; set; }
        public double MedianUs { get; set; }
        public double P90Us { get; set; }
        public long Bytes { get; set; }
        public double Psnr { get; set; } = double.NaN;
        public double Load { get; set; }
        public bool HighLoad { get; set; }
        public string Note { get; set; } = "";
    }

    public static void Run(Corpus corpus, string benchDir, Action<string> log)
    {
        var rows = new List<Row>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var pictures = Pictures(corpus);
        log($"store: {pictures.Count} pictures");
        foreach (var (name, flat, src) in pictures)
        {
            foreach (int size in new[] { 256, 512 })
            {
                using var thumb = BitmapUtil.FitOrSelf(new Bitmap(src), new Size(size, size));
                using var t32 = To32(thumb);
                foreach (var codec in new[] { "gdi-jpeg85", "gdi-png", "wpf-jpeg85", "wpf-png" })
                {
                    byte[] enc = Encode(codec, t32);
                    var e = Time(30, () => Encode(codec, t32));
                    var d = Time(30, () => { using var b = Decode(codec, enc); });
                    double psnr;
                    using (var back = Decode(codec, enc)) psnr = Psnr(t32, back);
                    rows.Add(new Row { Test = "encode", Picture = name, Size = size, Codec = codec, N = 30, MedianUs = e.Med, P90Us = e.P90, Bytes = enc.Length, Psnr = Math.Round(psnr, 1), Load = e.Load, HighLoad = e.Load > 60, Note = flat ? "flat" : "photo" });
                    rows.Add(new Row { Test = "decode", Picture = name, Size = size, Codec = codec, N = 30, MedianUs = d.Med, P90Us = d.P90, Bytes = enc.Length, Load = d.Load, HighLoad = d.Load > 60, Note = flat ? "flat" : "photo" });
                }
                if (size == 256)
                {
                    foreach (int to in new[] { 160, 96 })
                    {
                        var target = BitmapUtil.FitSize(t32.Size, new Size(to, to));
                        var hq = Time(40, () => { using var b = Scale(t32, target, InterpolationMode.HighQualityBicubic); });
                        var bl = Time(40, () => { using var b = Scale(t32, target, InterpolationMode.Bilinear); });
                        var src96 = CorpusBuilder.ToWpf(ToRgb(t32));
                        var wpf = Time(40, () => { using var b = BitmapUtil.ToGdi(BitmapUtil.ScaleToFit(src96, new Size(to, to))); });
                        rows.Add(new Row { Test = "scale", Picture = name, Size = to, Codec = "gdi-highqualitybicubic", N = 40, MedianUs = hq.Med, P90Us = hq.P90, Load = hq.Load, HighLoad = hq.Load > 60 });
                        rows.Add(new Row { Test = "scale", Picture = name, Size = to, Codec = "gdi-bilinear", N = 40, MedianUs = bl.Med, P90Us = bl.P90, Load = bl.Load, HighLoad = bl.Load > 60 });
                        rows.Add(new Row { Test = "scale", Picture = name, Size = to, Codec = "wpf-fant", N = 40, MedianUs = wpf.Med, P90Us = wpf.P90, Load = wpf.Load, HighLoad = wpf.Load > 60 });
                    }
                }
            }
            src.Dispose();
        }
        rows.AddRange(FileOps(Path.Combine(benchDir, "..", "store-test"), log));
        File.WriteAllText(Path.Combine(benchDir, $"store-{stamp}.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        foreach (var g in rows.GroupBy(r => (r.Test, r.Codec, r.Size, r.Note)).OrderBy(g => g.Key.Test).ThenBy(g => g.Key.Size))
            log($"  {g.Key.Test,-7} {g.Key.Codec,-24} {g.Key.Size,4} {g.Key.Note,-6} median {Stats.Median(g.Select(r => r.MedianUs)) / 1000:0.000} ms, bytes {Stats.Median(g.Select(r => (double)r.Bytes)):0}, psnr {Stats.Median(g.Select(r => r.Psnr)):0.0}");
    }

    /// <summary>Photo-like (made photos, real camera pictures) and flat pictures (text page, shell icon, chart, PDF page).</summary>
    private static List<(string Name, bool Flat, Bitmap Src)> Pictures(Corpus corpus)
    {
        var list = new List<(string, bool, Bitmap)>();
        foreach (var f in corpus.Files.Where(f => f.Id.StartsWith("made/mix-2mp", StringComparison.Ordinal)).Take(3))
            list.Add((f.Id, false, new Bitmap(corpus.Full(f))));
        int n = 0;
        foreach (var f in corpus.Files.Where(f => f.Origin == "real" && f.Ext is ".jpg" or ".jpeg" && f.Bytes < 15_000_000).Take(4))
        {
            try
            {
                var (s, _) = CountingStream.Open(corpus.Full(f));
                using (s)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = s;
                    bi.DecodePixelWidth = 1024;
                    bi.EndInit();
                    list.Add(($"real photo {n++}", false, BitmapUtil.ToGdi(bi)));
                }
            }
            catch { }
        }
        list.Add(("text page", true, TodayThumbnailProvider.TextPage(File.ReadAllText(corpus.Full(corpus.Files.First(f => f.Ext == ".txt"))), 512)));
        foreach (var f in corpus.Files.Where(f => f.Id.StartsWith("made/mix-screen", StringComparison.Ordinal)).Take(2))
            list.Add((f.Id + " (chart/screen)", true, new Bitmap(corpus.Full(f))));
        using (var sta = new StaWorker("store icon"))
        {
            var docx = corpus.Full(corpus.Files.First(f => f.Ext == ".docx"));
            var icon = sta.Run(() => GetIcon(docx));
            if (icon != null) list.Add(("docx icon", true, icon));
        }
        var pdf = corpus.Files.FirstOrDefault(f => f.Id == "made/text-2-pages.pdf");
        if (pdf != null)
        {
            try
            {
                var t = TodayThumbnailProvider.PdfFirstPage(corpus.Full(pdf), new Size(768, 768), System.Threading.CancellationToken.None);
                var b = t.GetAwaiter().GetResult();
                if (b != null) list.Add(("pdf text page", true, b));
            }
            catch { }
        }
        return list;
    }

    private static Bitmap? GetIcon(string path)
    {
        // The product's own conversion (keeps alpha), flattened on white (what a grid cell shows).
        using var b = Anything.Preview.Interop.Shell.GetImageSta(path, 256, Anything.Preview.Interop.Shell.SIIGBF.IconOnly);
        if (b == null) return null;
        var flat = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImage(b, 0, 0); }
        return flat;
    }

    private static Bitmap To32(Bitmap b)
    {
        var o = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(o);
        g.Clear(Color.White);
        g.DrawImage(b, 0, 0, b.Width, b.Height);
        return o;
    }

    private static Bitmap ToRgb(Bitmap b)
    {
        var o = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(o);
        g.DrawImage(b, 0, 0, b.Width, b.Height);
        return o;
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static byte[] Encode(string codec, Bitmap b)
    {
        using var ms = new MemoryStream();
        switch (codec)
        {
            case "gdi-jpeg85":
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                b.Save(ms, JpegCodec, ep);
                break;
            }
            case "gdi-png":
                b.Save(ms, ImageFormat.Png);
                break;
            default:
            {
                BitmapEncoder enc = codec == "wpf-jpeg85" ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(ToWpf32(b, codec == "wpf-jpeg85")));
                enc.Save(ms);
                break;
            }
        }
        return ms.ToArray();
    }

    private static BitmapSource ToWpf32(Bitmap b, bool opaque)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var s = BitmapSource.Create(b.Width, b.Height, 96, 96, opaque ? System.Windows.Media.PixelFormats.Bgr32 : System.Windows.Media.PixelFormats.Bgra32,
                null, d.Scan0, d.Stride * b.Height, d.Stride);
            s.Freeze();
            return s;
        }
        finally { b.UnlockBits(d); }
    }

    private static Bitmap Decode(string codec, byte[] data)
    {
        using var ms = new MemoryStream(data, false);
        if (codec.StartsWith("gdi", StringComparison.Ordinal))
        {
            using var img = System.Drawing.Image.FromStream(ms, false, false);
            return new Bitmap(img);
        }
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return BitmapUtil.ToGdi(dec.Frames[0]);
    }

    private static Bitmap Scale(Bitmap src, Size to, InterpolationMode mode)
    {
        var dst = new Bitmap(to.Width, to.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = mode;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, to.Width, to.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    /// <summary>PSNR over RGB (pictures flattened on white), in dB. 40+ is hard to tell apart; below ~32 text edges ring.</summary>
    private static double Psnr(Bitmap a, Bitmap b)
    {
        var pa = Pixels(a);
        var pb = Pixels(b);
        if (pa.Length != pb.Length) return double.NaN;
        double se = 0;
        long n = 0;
        for (int i = 0; i < pa.Length; i += 4)
            for (int c = 0; c < 3; c++) { double d = pa[i + c] - pb[i + c]; se += d * d; n++; }
        double mse = se / n;
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte[] Pixels(Bitmap b)
    {
        using var flat = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImageUnscaled(b, 0, 0); }
        var d = flat.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[b.Width * b.Height * 4];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, buf, y * b.Width * 4, b.Width * 4);
            return buf;
        }
        finally { flat.UnlockBits(d); }
    }

    private static (double Med, double P90, double Load) Time(int n, Action a)
    {
        a(); // warm-up
        var t = new List<double>(n);
        var mon = LoadMonitor.Shared;
        double t0 = mon.Now;
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            a();
            t.Add(sw.Elapsed.TotalMilliseconds * 1000);
        }
        var (load, _) = mon.Between(t0, mon.Now);
        return (Math.Round(Stats.Median(t), 1), Math.Round(Stats.Percentile(t, 90), 1), Math.Round(load, 1));
    }

    /// <summary>Random 15 KB reads from a 16 MB file (warm), appends of 15 KB records, and loading a 100,000 x 24-byte index.</summary>
    private static List<Row> FileOps(string dir, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        var rows = new List<Row>();
        string data = Path.Combine(dir, "store-16mb.bin");
        var rnd = new Random(7);
        var buf16 = new byte[16 << 20];
        rnd.NextBytes(buf16);
        File.WriteAllBytes(data, buf16);
        using (var h = File.OpenHandle(data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var rec = new byte[15 * 1024];
            for (long off = 0; off < buf16.Length; off += 1 << 20) RandomAccess.Read(h, rec, off); // warm
            var r = Time(2000, () => RandomAccess.Read(h, rec, rnd.NextInt64(0, buf16.Length - rec.Length)));
            rows.Add(new Row { Test = "file", Codec = "RandomAccess.Read 15 KB at a random offset (warm, handle open)", N = 2000, MedianUs = r.Med, P90Us = r.P90, Load = r.Load, HighLoad = r.Load > 60 });
        }
        var open = Time(300, () =>
        {
            using var h = File.OpenHandle(data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var rec = new byte[15 * 1024];
            RandomAccess.Read(h, rec, rnd.NextInt64(0, buf16.Length - rec.Length));
        });
        rows.Add(new Row { Test = "file", Codec = "open + read 15 KB + close", N = 300, MedianUs = open.Med, P90Us = open.P90, Load = open.Load, HighLoad = open.Load > 60 });
        string app = Path.Combine(dir, "store-append.bin");
        File.Delete(app);
        using (var h = File.OpenHandle(app, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
        {
            var rec = new byte[15 * 1024];
            rnd.NextBytes(rec);
            long pos = 0;
            var r = Time(1000, () => { RandomAccess.Write(h, rec, pos); pos += rec.Length; });
            rows.Add(new Row { Test = "file", Codec = "append 15 KB record (RandomAccess.Write, no flush)", N = 1000, MedianUs = r.Med, P90Us = r.P90, Load = r.Load, HighLoad = r.Load > 60 });
        }
        using (var fs = new FileStream(app, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var rec = new byte[15 * 1024];
            var r = Time(40, () => { fs.Write(rec); fs.Flush(true); });
            rows.Add(new Row { Test = "file", Codec = "append 15 KB record + flush to disk", N = 40, MedianUs = r.Med, P90Us = r.P90, Load = r.Load, HighLoad = r.Load > 60 });
        }
        string idx = Path.Combine(dir, "store-index.bin");
        var ib = new byte[100_000 * 24];
        rnd.NextBytes(ib);
        File.WriteAllBytes(idx, ib);
        File.ReadAllBytes(idx);
        var load = Time(20, () =>
        {
            var bytes = File.ReadAllBytes(idx);
            var map = new Dictionary<ulong, (long Off, int Len)>(100_000);
            for (int i = 0; i < 100_000; i++)
            {
                var span = bytes.AsSpan(i * 24, 24);
                map[BitConverter.ToUInt64(span)] = (BitConverter.ToInt64(span[8..]), BitConverter.ToInt32(span[16..]));
            }
        });
        rows.Add(new Row { Test = "file", Codec = "load 100,000 x 24-byte index into a Dictionary", N = 20, MedianUs = load.Med, P90Us = load.P90, Load = load.Load, HighLoad = load.Load > 60 });
        foreach (var r in rows) log($"  {r.Codec}: median {r.MedianUs:0.0} us, p90 {r.P90Us:0.0} us (load {r.Load:0}%)");
        File.Delete(data);
        File.Delete(app);
        File.Delete(idx);
        return rows;
    }
}
