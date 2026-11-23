using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Anything.Preview;
using Anything.Preview.Util;

namespace PreviewHarness.Bench;

/// <summary>One timed attempt at a thumbnail from one source.</summary>
internal sealed class ProbeRow
{
    public string Run { get; set; } = "";
    public string File { get; set; } = "";       // corpus id (no names for real files)
    public string Kind { get; set; } = "";
    public string Ext { get; set; } = "";
    public string Origin { get; set; } = "";
    public double Mp { get; set; }
    public long FileBytes { get; set; }
    public int Copy { get; set; }
    public string Source { get; set; } = "";
    public int Size { get; set; }
    public int Round { get; set; }
    public double Ms { get; set; }
    public double CpuMs { get; set; }
    public bool Ok { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public long BytesRead { get; set; } = -1;     // counted on the stream we handed the decoder; -1 = not counted
    public long Distinct { get; set; } = -1;
    public int Ranges { get; set; } = -1;
    public long ProcBytes { get; set; }           // the whole process's file reads during the probe
    public long PixelsDecoded { get; set; } = -1;
    public string Hr { get; set; } = "";
    public double Load { get; set; }
    public double LoadMax { get; set; }
    public bool HighLoad { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>The decoders a thumbnail can come from inside the process (no Windows thumbnail cache involved).</summary>
internal static class SourceProbes
{
    public sealed record Res(bool Ok, int W, int H, string Note, long Pixels = -1);

    /// <summary>Runs <paramref name="f"/> with a counted stream and fills the row.</summary>
    public static void Counted(ProbeRow row, string path, Func<Stream, Res> f, int buffer = 1 << 16)
    {
        CountingStream? counter = null;
        Timed(row, () =>
        {
            // The open is part of the time (a decoder has to open the file too).
            var (stream, c) = CountingStream.Open(path, buffer);
            counter = c;
            using (stream) return f(stream);
        });
        if (counter == null) return;
        row.BytesRead = counter.BytesRead;
        (row.Distinct, row.Ranges) = counter.Coverage();
    }

    /// <summary>Times <paramref name="f"/>: wall clock, this process's processor time and file reads, and the PC's load.</summary>
    public static void Timed(ProbeRow row, Func<Res> f)
    {
        var mon = LoadMonitor.Shared;
        var a = ProcessMeter.Take();
        double t0 = mon.Now;
        var sw = Stopwatch.StartNew();
        Res r;
        try { r = f(); }
        // A shell call that timed out has a wedged STA thread behind it: the caller must replace that thread, so the
        // time-out is not turned into a failed row here.
        catch (TimeoutException) { row.Ms = sw.Elapsed.TotalMilliseconds; row.Note = "timed out"; throw; }
        catch (NotSupportedException ex) { r = new Res(false, 0, 0, "not supported: " + Short(ex.Message)); }
        catch (Exception ex) { r = new Res(false, 0, 0, ex.GetType().Name + ": " + Short(ex.InnerException?.Message ?? ex.Message)); }
        row.Ms = sw.Elapsed.TotalMilliseconds;
        double t1 = mon.Now;
        var b = ProcessMeter.Take();
        row.CpuMs = ProcessMeter.CpuMs(a, b);
        row.ProcBytes = (long)(b.BytesRead - a.BytesRead);
        row.Ok = r.Ok;
        row.W = r.W;
        row.H = r.H;
        row.PixelsDecoded = r.Pixels;
        row.Note = r.Note;
        (row.Load, row.LoadMax) = mon.Between(t0, t1);
    }

    public static string Short(string s) => s.Length > 90 ? s[..90] : s;

    private static BitmapDecoder Decoder(Stream s) =>
        BitmapDecoder.Create(s, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);

    /// <summary>Whether a small picture is stored turned already: compare its shape with the stored (unturned) frame's.</summary>
    private static string Turned(BitmapSource small, BitmapFrame frame, int orientation)
    {
        if (orientation is < 5 or > 8) return orientation == 1 ? "orientation normal" : $"orientation {orientation} (no axis swap)";
        bool sameShape = (small.PixelWidth >= small.PixelHeight) == (frame.PixelWidth >= frame.PixelHeight);
        return sameShape ? $"orientation {orientation}: stored unturned, must be turned" : $"orientation {orientation}: already turned";
    }

    /// <summary>BitmapFrame.Thumbnail (EXIF IFD1 thumbnail for JPEG/TIFF; codec-defined for others).</summary>
    public static Res FrameThumbnail(Stream s)
    {
        var dec = Decoder(s);
        var frame = dec.Frames[0];
        var t = frame.Thumbnail;
        if (t == null) return new Res(false, 0, 0, "none");
        using var bmp = BitmapUtil.ToGdi(t);
        return new Res(true, bmp.Width, bmp.Height, Turned(t, frame, WicImage.ReadOrientation(frame)), (long)bmp.Width * bmp.Height);
    }

    public static Res DecoderThumbnail(Stream s)
    {
        var dec = Decoder(s);
        var t = dec.Thumbnail;
        if (t == null) return new Res(false, 0, 0, "none");
        using var bmp = BitmapUtil.ToGdi(t);
        return new Res(true, bmp.Width, bmp.Height, Turned(t, dec.Frames[0], WicImage.ReadOrientation(dec.Frames[0])), (long)bmp.Width * bmp.Height);
    }

    public static Res DecoderPreview(Stream s)
    {
        var dec = Decoder(s);
        var t = dec.Preview;
        if (t == null) return new Res(false, 0, 0, "none");
        // A preview can be large (RAW: often the full-size JPEG): scale it to 512 as a stand-in would.
        var scaled = BitmapUtil.ScaleToFit(t, new Size(512, 512));
        using var bmp = BitmapUtil.ToGdi(scaled);
        return new Res(true, t.PixelWidth, t.PixelHeight, Turned(t, dec.Frames[0], WicImage.ReadOrientation(dec.Frames[0])) + $"; shown at {bmp.Width}x{bmp.Height}",
            (long)t.PixelWidth * t.PixelHeight);
    }

    /// <summary>
    /// Rock Preview's own decode at a size class, as WicImage does it: JPEG and RAW through BitmapImage with
    /// DecodePixelWidth (WicImage.DecodeScaled), everything else frame 0 through ScaleToFit (Fant).
    /// </summary>
    public static Res OwnScaled(Stream s, string ext, int box)
    {
        var dec = Decoder(s);
        var frame = dec.Frames[0];
        int ow = frame.PixelWidth, oh = frame.PixelHeight, o = WicImage.ReadOrientation(frame);
        var fit = BitmapUtil.FitSize(new Size(ow, oh), BitmapUtil.OrientBox(new Size(box, box), o));
        bool jpegOrRaw = ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" || WicImage.RawExtensions.Contains(ext);
        Bitmap bmp;
        string how;
        if (jpegOrRaw && dec.Frames.Count == 1 && fit.Width < ow)
        {
            s.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = s;
            bi.DecodePixelWidth = fit.Width;
            bi.EndInit();
            bi.Freeze();
            bmp = BitmapUtil.ToGdi(bi);
            how = "BitmapImage DecodePixelWidth";
        }
        else
        {
            bmp = BitmapUtil.ToGdi(BitmapUtil.ScaleToFit(frame, BitmapUtil.OrientBox(new Size(box, box), o)));
            how = "frame + ScaleToFit";
        }
        BitmapUtil.ApplyOrientation(bmp, o);
        using (bmp) return new Res(true, bmp.Width, bmp.Height, how, (long)bmp.Width * bmp.Height);
    }

    /// <summary>What ThumbnailProvider does today when Windows has no thumbnail: ExtractAsync, then the fit to the cell.</summary>
    public static Res OwnToday(string path, string ext, int size)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var task = TodayThumbnailProvider.ExtractAsync(path, ext, size, cts.Token);
        var bmp = task.GetAwaiter().GetResult();
        if (bmp == null) return new Res(false, 0, 0, "no picture (falls to the icon)");
        int dw = bmp.Width, dh = bmp.Height;
        bmp = BitmapUtil.FitOrSelf(bmp, new Size(size, size));
        using (bmp) return new Res(true, bmp.Width, bmp.Height, $"decoded {dw}x{dh}");
    }

    /// <summary>A PSD/PSB's JPEG thumbnail resource (1036, or 1033 for Photoshop 4) alone: header, resources, decode.</summary>
    public static Res PsdThumbnail(Stream s, int box)
    {
        var br = new BinaryReader(s);
        byte[] hdr = br.ReadBytes(26);
        if (hdr.Length < 26 || hdr[0] != (byte)'8' || hdr[1] != (byte)'B') return new Res(false, 0, 0, "not a PSD");
        uint cm = BE32(br);
        s.Seek(cm, SeekOrigin.Current);
        uint resLen = BE32(br);
        long end = s.Position + resLen;
        while (s.Position + 12 <= end)
        {
            byte[] sig = br.ReadBytes(4);
            if (sig.Length < 4 || sig[0] != (byte)'8') break;
            int id = br.ReadByte() << 8 | br.ReadByte();
            int nameLen = br.ReadByte();
            s.Seek((nameLen + 1) % 2 == 0 ? nameLen : nameLen + 1, SeekOrigin.Current);
            uint size = BE32(br);
            long start = s.Position;
            if ((id == 1036 || id == 1033) && size > 28)
            {
                byte[] t = br.ReadBytes((int)size);
                if (t[3] == 1)
                {
                    using var bmp = BitmapUtil.Decode(t[28..], new Size(box, box));
                    if (bmp != null) return new Res(true, bmp.Width, bmp.Height, id == 1033 ? "resource 1033 (BGR)" : "resource 1036", (long)bmp.Width * bmp.Height);
                }
            }
            s.Position = start + size + (size & 1);
        }
        return new Res(false, 0, 0, "no thumbnail resource");
    }

    public static Res PsdComposite(string path, int box)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var bmp = PsdDecoder.Decode(path, new Size(box, box), cts.Token, out var info, out bool fromThumb);
        if (bmp == null) return new Res(false, 0, 0, "no picture");
        return new Res(true, bmp.Width, bmp.Height, fromThumb ? "fell back to the thumbnail resource" : $"composite of {info?.Width}x{info?.Height}");
    }

    public static Res Wic(WicDirect.Outcome o) => new(o.Ok, o.DecodedWidth, o.DecodedHeight, o.Note, o.PixelsDecoded);

    private static uint BE32(BinaryReader br) { var b = br.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
}
