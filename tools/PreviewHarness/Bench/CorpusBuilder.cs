using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PreviewHarness.Bench;

/// <summary>One file of the test set. Real files carry no name or folder, only their kind and measurements.</summary>
internal sealed class CorpusFile
{
    public string Id { get; set; } = "";
    public string Origin { get; set; } = "";      // "made" or "real"
    public string Ext { get; set; } = "";
    public string RelPath { get; set; } = "";     // relative to the corpus folder
    public long Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Orientation { get; set; } = 1;
    public bool? ExifThumb { get; set; }          // known for made files
    public string Tags { get; set; } = "";        // e.g. "large", "mix", "alpha"
    public double Megapixels => Width > 0 ? Math.Round(Width * (double)Height / 1e6, 1) : 0;
}

internal sealed class Corpus
{
    public string Dir { get; set; } = "";
    public List<CorpusFile> Files { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public string Full(CorpusFile f) => Path.Combine(Dir, f.RelPath);

    public static Corpus Load(string corpusDir)
    {
        var c = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(corpusDir, "manifest.json")))!;
        c.Dir = corpusDir;
        return c;
    }

    public void Save() =>
        File.WriteAllText(Path.Combine(Dir, "manifest.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Builds .probe\corpus: files made by code under made\, and copies of the user's own files under real\ picked by
/// kind and size only. Originals are opened read-only and shared; copies get anonymous names.
/// </summary>
internal static class CorpusBuilder
{
    public static readonly string[] RealStills =
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf",
        ".psd", ".psb", ".pdf", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt",
    };

    public static readonly string[] RealVideos = { ".mp4", ".mov", ".mkv", ".avi" };

    public static Corpus Build(string corpusDir, long stillsBudget, long videoBudget, bool skipMade, bool skipReal, Action<string> log)
    {
        Directory.CreateDirectory(corpusDir);
        Corpus corpus = File.Exists(Path.Combine(corpusDir, "manifest.json")) ? Corpus.Load(corpusDir) : new Corpus { Dir = corpusDir };
        if (!skipMade)
        {
            corpus.Files.RemoveAll(f => f.Origin == "made");
            corpus.Notes.RemoveAll(n => n.StartsWith("made:", StringComparison.Ordinal));
            MakeAll(corpus, log);
            corpus.Save();
        }
        if (!skipReal)
        {
            corpus.Files.RemoveAll(f => f.Origin == "real");
            corpus.Notes.RemoveAll(n => n.StartsWith("real:", StringComparison.Ordinal));
            string realDir = Path.Combine(corpusDir, "real");
            if (Directory.Exists(realDir)) Directory.Delete(realDir, true);
            PickReal(corpus, stillsBudget, videoBudget, log);
            corpus.Save();
        }
        return corpus;
    }

    // ------------------------------------------------------------------ made by code

    private static void MakeAll(Corpus corpus, Action<string> log)
    {
        string dir = Path.Combine(corpus.Dir, "made");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        void Add(string name, int w, int h, string tags, int orientation = 1, bool? thumb = null)
        {
            string full = Path.Combine(dir, name);
            corpus.Files.Add(new CorpusFile
            {
                Id = "made/" + name, Origin = "made", Ext = Path.GetExtension(name).ToLowerInvariant(), RelPath = Path.Combine("made", name),
                Bytes = new FileInfo(full).Length, Width = w, Height = h, Orientation = orientation, ExifThumb = thumb, Tags = tags,
            });
        }
        var sw = Stopwatch.StartNew();
        // name, width, height (3:2 like most cameras)
        var sizes = new (int Mp, int W, int H)[] { (2, 1800, 1200), (12, 4240, 2832), (24, 6000, 4000), (50, 8688, 5792), (100, 12288, 8192) };
        foreach (var (mp, w, h) in sizes)
        {
            using var photo = PhotoPainter.Photo(w, h, 1000 + mp);
            byte[] main = EncodeJpeg(photo, 92);
            byte[] thumb = ThumbJpeg(photo, 160);
            string large = mp >= 50 ? "large" : "";
            File.WriteAllBytes(Path.Combine(dir, $"photo-{mp}mp-exifthumb.jpg"), WithExif(main, 1, thumb));
            Add($"photo-{mp}mp-exifthumb.jpg", w, h, large, 1, true);
            File.WriteAllBytes(Path.Combine(dir, $"photo-{mp}mp-nothumb.jpg"), WithExif(main, 0, null));
            Add($"photo-{mp}mp-nothumb.jpg", w, h, large, 1, false);
            if (mp == 12)
            {
                File.WriteAllBytes(Path.Combine(dir, "photo-12mp-orient6-exifthumb.jpg"), WithExif(main, 6, thumb));
                Add("photo-12mp-orient6-exifthumb.jpg", w, h, "orient", 6, true);
                File.WriteAllBytes(Path.Combine(dir, "photo-12mp-orient3-nothumb.jpg"), WithExif(main, 3, null));
                Add("photo-12mp-orient3-nothumb.jpg", w, h, "orient", 3, false);
            }
            if (mp == 24)
            {
                File.WriteAllBytes(Path.Combine(dir, "photo-24mp-orient8-exifthumb.jpg"), WithExif(main, 8, thumb));
                Add("photo-24mp-orient8-exifthumb.jpg", w, h, "orient", 8, true);
                SaveTiffLzw(photo, Path.Combine(dir, "photo-24mp-lzw.tif"));
                Add("photo-24mp-lzw.tif", w, h, "");
                photo.Save(Path.Combine(dir, "photo-24mp.bmp"), ImageFormat.Bmp);
                Add("photo-24mp.bmp", w, h, "");
                // Photoshop file with a thumbnail resource.
                WritePsd(Path.Combine(dir, "photo-24mp-thumb.psd"), photo, false, thumb);
                Add("photo-24mp-thumb.psd", w, h, "");
            }
            if (mp is 2 or 12 or 50)
            {
                photo.Save(Path.Combine(dir, $"photo-{mp}mp.png"), ImageFormat.Png);
                Add($"photo-{mp}mp.png", w, h, large);
            }
            if (mp == 100)
            {
                WriteTiffUncompressed(photo, Path.Combine(dir, "photo-100mp-uncompressed.tif"));
                Add("photo-100mp-uncompressed.tif", w, h, "large");
            }
            if (mp is 2 or 12) TryWic(photo, dir, mp, Add);
            log($"  made {mp} MP pictures ({sw.Elapsed.TotalSeconds:0} s)");
        }
        foreach (var (mp, w, h) in new[] { (2, 1800, 1200), (12, 4240, 2832) })
        {
            using var g = PhotoPainter.Graphic(w, h, 77 + mp, alpha: true);
            g.Save(Path.Combine(dir, $"graphic-{mp}mp-alpha.png"), ImageFormat.Png);
            Add($"graphic-{mp}mp-alpha.png", w, h, "alpha");
        }
        // A large Photoshop big document: a poster (flat areas, text) with a photo in one quarter, RLE, thumbnail resource.
        {
            const int W = 10000, H = 10000;
            using var poster = PhotoPainter.Graphic(W, H, 4242, alpha: false);
            using (var ph = PhotoPainter.Photo(W / 2, H / 2, 4343))
            using (var g = Graphics.FromImage(poster))
                g.DrawImageUnscaled(ph, W / 2, H / 2);
            WritePsd(Path.Combine(dir, "poster-100mp-thumb.psb"), poster, true, ThumbJpeg(poster, 160));
            Add("poster-100mp-thumb.psb", W, H, "large");
            log($"  made the PSB ({sw.Elapsed.TotalSeconds:0} s)");
        }
        WriteAnimatedGif(Path.Combine(dir, "animated-300-frames.gif"), 300, 480, 270);
        Add("animated-300-frames.gif", 480, 270, "");
        WritePdf(Path.Combine(dir, "text-2-pages.pdf"), null);
        Add("text-2-pages.pdf", 0, 0, "");
        using (var scan = PhotoPainter.Photo(4240, 2832, 555))
        {
            WritePdf(Path.Combine(dir, "scan-12mp-image.pdf"), EncodeJpeg(scan, 85));
            Add("scan-12mp-image.pdf", 0, 0, "");
            WriteDocx(Path.Combine(dir, "report-with-thumbnail.docx"), ThumbJpeg(scan, 256));
            Add("report-with-thumbnail.docx", 0, 0, "", thumb: true);
        }
        WriteDocx(Path.Combine(dir, "report-no-thumbnail.docx"), null);
        Add("report-no-thumbnail.docx", 0, 0, "", thumb: false);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"Line {i}: the quick brown fox jumps over the lazy dog.")));
        Add("notes.txt", 0, 0, "");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "using System;\r\nclass P\r\n{\r\n    static void Main() => Console.WriteLine(\"hi\");\r\n}\r\n");
        Add("Program.cs", 0, 0, "");
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var f in new[] { "arial.ttf", "segoeui.ttf", "consola.ttf" })
        {
            string src = Path.Combine(fonts, f);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(dir, "font-" + f), true);
            Add("font-" + f, 0, 0, "");
        }
        for (int i = 0; i < 3; i++)
        {
            WriteIco(Path.Combine(dir, $"icon-{i}.ico"), i);
            Add($"icon-{i}.ico", 256, 256, "");
        }
        // Masters for the mixed folders (copied many times; each copy is a new file to Windows).
        for (int i = 0; i < 8; i++)
        {
            using var p = PhotoPainter.Photo(1800, 1200, 9000 + i);
            File.WriteAllBytes(Path.Combine(dir, $"mix-2mp-{i}.jpg"), WithExif(EncodeJpeg(p, 92), 1, ThumbJpeg(p, 160)));
            Add($"mix-2mp-{i}.jpg", 1800, 1200, "mix", 1, true);
        }
        for (int i = 0; i < 6; i++)
        {
            using var p = PhotoPainter.Photo(3000, 2000, 9100 + i);
            File.WriteAllBytes(Path.Combine(dir, $"mix-6mp-{i}.jpg"), WithExif(EncodeJpeg(p, 92), 1, ThumbJpeg(p, 160)));
            Add($"mix-6mp-{i}.jpg", 3000, 2000, "mix", 1, true);
        }
        for (int i = 0; i < 4; i++)
        {
            using var p = PhotoPainter.Graphic(1920, 1080, 9200 + i, alpha: false);
            p.Save(Path.Combine(dir, $"mix-screen-{i}.png"), ImageFormat.Png);
            Add($"mix-screen-{i}.png", 1920, 1080, "mix");
        }
        log($"  made everything ({sw.Elapsed.TotalSeconds:0} s)");
    }

    private static void TryWic(Bitmap photo, string dir, int mp, Action<string, int, int, string, int, bool?> add)
    {
        foreach (var (ext, guid) in new[] { (".webp", "e094b0e2-67f2-45b3-b0ea-115337ca7cf3"), (".heic", "e1e62521-6787-405b-a339-500715b5763f") })
        {
            string name = $"photo-{mp}mp{ext}";
            try
            {
                var enc = BitmapEncoder.Create(new Guid(guid));
                enc.Frames.Add(BitmapFrame.Create(ToWpf(photo)));
                using (var fs = File.Create(Path.Combine(dir, name))) enc.Save(fs);
                add(name, photo.Width, photo.Height, "", 1, null);
            }
            catch (Exception ex)
            {
                try { File.Delete(Path.Combine(dir, name)); } catch { }
                Console.WriteLine($"  {ext}: this PC cannot write it ({ex.GetType().Name})");
            }
        }
    }

    public static BitmapSource ToWpf(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var src = BitmapSource.Create(b.Width, b.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, d.Scan0, d.Stride * b.Height, d.Stride);
            src.Freeze();
            return src;
        }
        finally { b.UnlockBits(d); }
    }

    public static byte[] EncodeJpeg(Bitmap b, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        b.Save(ms, codec, ep);
        return ms.ToArray();
    }

    public static byte[] ThumbJpeg(Bitmap b, int max)
    {
        double s = Math.Min((double)max / b.Width, (double)max / b.Height);
        int w = Math.Max(1, (int)Math.Round(b.Width * s)), h = Math.Max(1, (int)Math.Round(b.Height * s));
        using var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(b, new Rectangle(0, 0, w, h));
        }
        return EncodeJpeg(t, 80);
    }

    /// <summary>
    /// Replaces any APP0 with an EXIF APP1 right after SOI: IFD0 with the orientation (when not 0), IFD1 with the
    /// JPEG thumbnail (when given). <paramref name="orientation"/> 0 and no thumbnail = no EXIF at all.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, int orientation, byte[]? thumb)
    {
        int p = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) p = 4 + (jpeg[4] << 8 | jpeg[5]);
        using var o = new MemoryStream();
        o.Write(jpeg, 0, 2);
        if (orientation != 0 || thumb != null)
        {
            using var t = new MemoryStream();
            var bw = new BinaryWriter(t);
            bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)42); bw.Write(8u);
            // IFD0: orientation.
            bw.Write((ushort)1);
            bw.Write((ushort)0x0112); bw.Write((ushort)3); bw.Write(1u); bw.Write((ushort)Math.Max(1, orientation)); bw.Write((ushort)0);
            uint ifd1 = thumb != null ? 8u + 2 + 12 + 4 : 0u;
            bw.Write(ifd1);
            if (thumb != null)
            {
                uint thumbOff = ifd1 + 2 + 12 * 3 + 4;
                bw.Write((ushort)3);
                bw.Write((ushort)0x0103); bw.Write((ushort)3); bw.Write(1u); bw.Write((ushort)6); bw.Write((ushort)0);
                bw.Write((ushort)0x0201); bw.Write((ushort)4); bw.Write(1u); bw.Write(thumbOff);
                bw.Write((ushort)0x0202); bw.Write((ushort)4); bw.Write(1u); bw.Write((uint)thumb.Length);
                bw.Write(0u);
                bw.Write(thumb);
            }
            bw.Flush();
            byte[] tiff = t.ToArray();
            int len = 2 + 6 + tiff.Length;
            if (len > 65535) throw new InvalidOperationException("EXIF too large");
            o.WriteByte(0xFF); o.WriteByte(0xE1); o.WriteByte((byte)(len >> 8)); o.WriteByte((byte)len);
            o.Write("Exif\0\0"u8);
            o.Write(tiff);
        }
        o.Write(jpeg, p, jpeg.Length - p);
        return o.ToArray();
    }

    private static void SaveTiffLzw(Bitmap b, string path)
    {
        var enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        enc.Frames.Add(BitmapFrame.Create(ToWpf(b)));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Baseline uncompressed RGB TIFF, one strip per 64 rows, written row by row.</summary>
    private static void WriteTiffUncompressed(Bitmap b, string path)
    {
        int w = b.Width, h = b.Height, rowsPerStrip = 64, strips = (h + rowsPerStrip - 1) / rowsPerStrip;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var bw = new BinaryWriter(fs);
        // Header, then pixel data from offset 8, then IFD and tables at the end.
        bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)42);
        long ifdPos = 8L + 3L * w * h;
        bw.Write((uint)ifdPos);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x += 3) (row[x], row[x + 2]) = (row[x + 2], row[x]); // BGR -> RGB
                bw.Write(row);
            }
        }
        finally { b.UnlockBits(d); }
        const int n = 11;
        long tablesPos = ifdPos + 2 + 12 * n + 4;
        long offsetsPos = tablesPos, countsPos = offsetsPos + 4L * strips, bpsPos = countsPos + 4L * strips;
        bw.Write((ushort)n);
        void E(ushort tag, ushort type, uint count, uint value) { bw.Write(tag); bw.Write(type); bw.Write(count); bw.Write(value); }
        E(256, 4, 1, (uint)w);
        E(257, 4, 1, (uint)h);
        E(258, 3, 3, (uint)bpsPos);
        E(259, 3, 1, 1);
        E(262, 3, 1, 2);
        E(273, 4, (uint)strips, (uint)offsetsPos);
        E(277, 3, 1, 3);
        E(278, 4, 1, (uint)rowsPerStrip);
        E(279, 4, (uint)strips, (uint)countsPos);
        E(284, 3, 1, 1);
        E(296, 3, 1, 1);
        bw.Write(0u);
        for (int s = 0; s < strips; s++) bw.Write((uint)(8 + (long)s * rowsPerStrip * w * 3));
        for (int s = 0; s < strips; s++) bw.Write((uint)((long)Math.Min(rowsPerStrip, h - s * rowsPerStrip) * w * 3));
        bw.Write((ushort)8); bw.Write((ushort)8); bw.Write((ushort)8);
    }

    /// <summary>PSD (or PSB) RGB 8-bit, RLE composite, optional JPEG thumbnail resource 1036.</summary>
    private static void WritePsd(string path, Bitmap src, bool psb, byte[]? thumb)
    {
        int w = src.Width, h = src.Height;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        void BE16(int v) { fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
        void BE32(long v) { fs.WriteByte((byte)(v >> 24)); fs.WriteByte((byte)(v >> 16)); fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
        fs.Write("8BPS"u8); BE16(psb ? 2 : 1); fs.Write(new byte[6]); BE16(3); BE32(h); BE32(w); BE16(8); BE16(3);
        BE32(0); // color mode data
        if (thumb != null)
        {
            int tw, th;
            using (var ms = new MemoryStream(thumb)) using (var ti = System.Drawing.Image.FromStream(ms)) { tw = ti.Width; th = ti.Height; }
            int dataLen = 28 + thumb.Length, padded = dataLen + (dataLen & 1);
            BE32(4 + 2 + 2 + 4 + padded);
            fs.Write("8BIM"u8); BE16(1036); BE16(0); BE32(dataLen);
            int widthBytes = (tw * 24 + 31) / 32 * 4;
            BE32(1); BE32(tw); BE32(th); BE32(widthBytes); BE32((long)widthBytes * th); BE32(thumb.Length); BE16(24); BE16(1);
            fs.Write(thumb);
            if ((dataLen & 1) != 0) fs.WriteByte(0);
        }
        else BE32(0);
        if (psb) { BE32(0); BE32(0); } else BE32(0); // layer and mask info
        BE16(1); // RLE
        long countsPos = fs.Position;
        int rows = 3 * h, cw = psb ? 4 : 2;
        fs.Write(new byte[(long)rows * cw]);
        var lens = new int[rows];
        var d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, src.PixelFormat);
        int bpp = Image.GetPixelFormatSize(src.PixelFormat) / 8;
        try
        {
            var line = new byte[d.Stride];
            var plane = new byte[w];
            for (int c = 0; c < 3; c++)
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, line, 0, w * bpp);
                    int off = 2 - c; // BGR(A) -> R, G, B
                    for (int x = 0; x < w; x++) plane[x] = line[x * bpp + off];
                    var packed = PackBits(plane);
                    lens[c * h + y] = packed.Length;
                    fs.Write(packed);
                }
        }
        finally { src.UnlockBits(d); }
        fs.Position = countsPos;
        foreach (var l in lens) { if (psb) BE32(l); else BE16(l); }
    }

    private static byte[] PackBits(byte[] d)
    {
        var o = new List<byte>(d.Length + d.Length / 64 + 2);
        int i = 0;
        while (i < d.Length)
        {
            int run = 1;
            while (i + run < d.Length && run < 128 && d[i + run] == d[i]) run++;
            if (run >= 2) { o.Add((byte)(257 - run)); o.Add(d[i]); i += run; continue; }
            int start = i, lit = 0;
            while (i < d.Length && lit < 128 && !(i + 1 < d.Length && d[i + 1] == d[i])) { i++; lit++; }
            if (lit == 0) { i++; lit = 1; }
            o.Add((byte)(lit - 1));
            for (int k = start; k < start + lit; k++) o.Add(d[k]);
        }
        return o.ToArray();
    }

    /// <summary>An animated GIF: GDI+-encoded frames stitched with local color tables and a NETSCAPE loop block.</summary>
    private static void WriteAnimatedGif(string path, int frames, int w, int h)
    {
        using var o = new FileStream(path, FileMode.Create);
        for (int i = 0; i < frames; i++)
        {
            byte[] g;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(30, 60 + i % 100, 140), Color.FromArgb(200, 120, 40 + i % 150), i * 1.2f))
                        gr.FillRectangle(bg, 0, 0, w, h);
                    double a = i * Math.PI * 2 / 60;
                    gr.FillEllipse(Brushes.Gold, (float)(w / 2 + Math.Cos(a) * w / 3) - 30, (float)(h / 2 + Math.Sin(a) * h / 3) - 30, 60, 60);
                    using var f = new Font("Segoe UI", 28, FontStyle.Bold);
                    gr.DrawString("Frame " + (i + 1), f, Brushes.White, 16, 12);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Gif);
                g = ms.ToArray();
            }
            int p = 6;
            int lsdPacked = g[p + 4];
            p += 7;
            byte[] gct = Array.Empty<byte>();
            int gctBits = lsdPacked & 7;
            if ((lsdPacked & 0x80) != 0) { gct = g.AsSpan(p, 3 * (1 << (gctBits + 1))).ToArray(); p += gct.Length; }
            if (i == 0)
            {
                o.Write(g.AsSpan(0, 6));
                o.Write(g.AsSpan(6, 4));
                o.WriteByte((byte)(lsdPacked & 0x70));
                o.WriteByte(0); o.WriteByte(0);
                o.Write(new byte[] { 0x21, 0xFF, 0x0B }); o.Write("NETSCAPE2.0"u8); o.Write(new byte[] { 3, 1, 0, 0, 0 });
            }
            while (g[p] == 0x21)
            {
                p += 2;
                while (g[p] != 0) p += g[p] + 1;
                p++;
            }
            o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04, 4, 0, 0, 0 }); // 40 ms
            o.Write(g.AsSpan(p, 9));
            int idPacked = g[p + 9];
            o.WriteByte((byte)((idPacked & 0x40) | 0x80 | gctBits));
            p += 10;
            o.Write(gct);
            int start = p;
            p++;
            while (g[p] != 0) p += g[p] + 1;
            p++;
            o.Write(g.AsSpan(start, p - start));
        }
        o.WriteByte(0x3B);
    }

    /// <summary>A text PDF of two pages, or (with <paramref name="jpeg"/>) one page showing a JPEG like a scan.</summary>
    private static void WritePdf(string path, byte[]? jpeg)
    {
        var objs = new List<byte[]>();
        byte[] A(string s) => Encoding.ASCII.GetBytes(s);
        byte[] Stream(string dict, byte[] data) => A($"<< {dict} /Length {data.Length} >>\nstream\n").Concat(data).Concat(A("\nendstream")).ToArray();
        objs.Add(A("<< /Type /Catalog /Pages 2 0 R >>"));
        if (jpeg == null)
        {
            objs.Add(A("<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"));
            objs.Add(A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
            var sb = new StringBuilder("BT /F1 28 Tf 72 720 Td (Quarterly report) Tj ET ");
            for (int i = 0; i < 40; i++) sb.Append($"BT /F1 11 Tf 72 {680 - i * 15} Td (Line {i + 1}: revenue, costs and the margin for each region.) Tj ET ");
            sb.Append("0 0.4 0.8 rg 72 60 200 40 re f");
            objs.Add(Stream("", A(sb.ToString())));
            objs.Add(A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>"));
            objs.Add(Stream("", A("BT /F1 36 Tf 72 700 Td (Page two) Tj ET 1 0 0 rg 100 300 200 200 re f")));
            objs.Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        }
        else
        {
            int iw, ih;
            using (var ms = new MemoryStream(jpeg)) using (var im = System.Drawing.Image.FromStream(ms)) { iw = im.Width; ih = im.Height; }
            objs.Add(A("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            objs.Add(A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>"));
            objs.Add(Stream("", A("q 842 0 0 595 0 0 cm /Im1 Do Q")));
            objs.Add(Stream($"/Type /XObject /Subtype /Image /Width {iw} /Height {ih} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));
        }
        using var o = new MemoryStream();
        o.Write(A("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(o.Position);
            o.Write(A($"{i + 1} 0 obj\n"));
            o.Write(objs[i]);
            o.Write(A("\nendobj\n"));
        }
        long xref = o.Position;
        var x = new StringBuilder($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) x.Append($"{off:D10} 00000 n \n");
        x.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        o.Write(A(x.ToString()));
        File.WriteAllBytes(path, o.ToArray());
    }

    private static void WriteDocx(string path, byte[]? thumb)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Text(string name, string content)
        {
            using var s = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Text("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Text("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            (thumb != null ? "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail\" Target=\"docProps/thumbnail.jpeg\"/>" : "") + "</Relationships>");
        var body = new StringBuilder("<w:p><w:r><w:t>Quarterly report</w:t></w:r></w:p>");
        for (int i = 0; i < 60; i++) body.Append($"<w:p><w:r><w:t>Paragraph {i + 1}: revenue, costs and the margin for each region, with notes.</w:t></w:r></w:p>");
        Text("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>");
        if (thumb != null)
        {
            using var s = z.CreateEntry("docProps/thumbnail.jpeg").Open();
            s.Write(thumb);
        }
    }

    private static void WriteIco(string path, int variant)
    {
        var colors = new[] { Color.SteelBlue, Color.OrangeRed, Color.SeaGreen };
        var pngs = new List<(int Size, byte[] Png)>();
        foreach (int s in new[] { 32, 256 })
        {
            using var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var br = new SolidBrush(colors[variant]);
                g.FillEllipse(br, s / 8, s / 8, s * 3 / 4, s * 3 / 4);
                using var f = new Font("Segoe UI", s / 3f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString(((char)('A' + variant)).ToString(), f, Brushes.White, s / 3.2f, s / 3.6f);
            }
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            pngs.Add((s, ms.ToArray()));
        }
        using var fs = File.Create(path);
        var bw = new BinaryWriter(fs);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)pngs.Count);
        uint off = (uint)(6 + 16 * pngs.Count);
        foreach (var (s, png) in pngs)
        {
            bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32); bw.Write((uint)png.Length); bw.Write(off);
            off += (uint)png.Length;
        }
        foreach (var (_, png) in pngs) bw.Write(png);
    }

    // ------------------------------------------------------------------ the user's own files

    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    /// Looks through Pictures, Videos, Downloads, Desktop and Documents for the kinds above, skipping cloud-only
    /// placeholders, hidden and system files and links, picks a spread of sizes per extension (largest that fits, smallest,
    /// median, then evenly spaced) within the budgets, and copies them read-only under anonymous names.
    /// Nothing about a file's name or folder is kept or logged.
    /// </summary>
    private static void PickReal(Corpus corpus, long stillsBudget, long videoBudget, Action<string> log)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "Pictures", "Videos", "Downloads", "Desktop", "Documents" }.Select(r => Path.Combine(home, r)).Where(Directory.Exists).ToList();
        var wanted = new HashSet<string>(RealStills.Concat(RealVideos), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, List<(string Path, long Size)>>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Hidden | FileAttributes.System | RecallOnOpen | RecallOnDataAccess,
        };
        var sw = Stopwatch.StartNew();
        long seen = 0;
        bool timedOut = false;
        foreach (var root in roots)
        {
            try
            {
                foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
                {
                    if (++seen % 2000 == 0 && sw.Elapsed.TotalSeconds > 240) { timedOut = true; break; }
                    string ext = fi.Extension;
                    if (!wanted.Contains(ext)) continue;
                    long len;
                    try { len = fi.Length; } catch { continue; }
                    if (len < 1024) continue;
                    ext = ext.ToLowerInvariant();
                    if (!found.TryGetValue(ext, out var list)) found[ext] = list = new();
                    list.Add((fi.FullName, len));
                }
            }
            catch { /* a folder that cannot be read */ }
            if (timedOut) break;
        }
        corpus.Notes.Add($"real: looked at {seen} files in the five folders in {sw.Elapsed.TotalSeconds:0} s{(timedOut ? " (stopped at the time limit)" : "")}");
        log($"  looked at {seen} files in {sw.Elapsed.TotalSeconds:0} s; kinds found: {string.Join(", ", found.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Count}"))}");

        Copy(corpus, found.Where(k => RealStills.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value), stillsBudget, 40, 150L << 20, log);
        Copy(corpus, found.Where(k => RealVideos.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value), videoBudget, 10, 200L << 20, log);
    }

    private static void Copy(Corpus corpus, Dictionary<string, List<(string Path, long Size)>> found, long budget, int perExt, long perFileMax, Action<string> log)
    {
        if (found.Count == 0) return;
        long left = budget;
        // Share the budget between extensions; what one leaves goes to the rest (smallest needs first).
        var order = found.OrderBy(k => k.Value.Sum(v => Math.Min(v.Size, perFileMax))).ToList();
        int remainingExts = order.Count;
        foreach (var (ext, list) in order)
        {
            long share = left / Math.Max(1, remainingExts--);
            var sorted = list.Where(v => v.Size <= perFileMax).OrderByDescending(v => v.Size).ToList();
            if (sorted.Count == 0) continue;
            var pick = new List<(string Path, long Size)>();
            void Try((string Path, long Size) c)
            {
                if (pick.Count >= perExt || pick.Contains(c)) return;
                if (pick.Sum(p => p.Size) + c.Size > share) return;
                pick.Add(c);
            }
            // Largest that fits the share, smallest, median, then sizes evenly spaced on a log scale (most folders hold
            // many small files, so spacing by rank alone would pick almost only small ones).
            foreach (var c in sorted) { if (c.Size <= share) { Try(c); break; } }
            Try(sorted[^1]);
            Try(sorted[sorted.Count / 2]);
            int k = Math.Min(perExt, sorted.Count);
            double lnMax = Math.Log(Math.Max(1, Math.Min(share, sorted[0].Size))), lnMin = Math.Log(Math.Max(1, sorted[^1].Size));
            for (int i = 0; i < k; i++)
            {
                double target = lnMax - (lnMax - lnMin) * i / Math.Max(1, k - 1);
                var near = sorted.Where(c => !pick.Contains(c)).OrderBy(c => Math.Abs(Math.Log(c.Size) - target)).FirstOrDefault();
                if (near.Path != null) Try(near);
            }
            foreach (var c in sorted) Try(c);
            int n = 0;
            string dir = Path.Combine(corpus.Dir, "real", ext.TrimStart('.'));
            Directory.CreateDirectory(dir);
            foreach (var (src, size) in pick.OrderBy(p => p.Size))
            {
                string name = $"{ext.TrimStart('.')}-{n:000}{ext}";
                string dst = Path.Combine(dir, name);
                try
                {
                    using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
                    using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                        i.CopyTo(o, 1 << 20);
                }
                catch
                {
                    try { File.Delete(dst); } catch { }
                    continue;
                }
                var cf = new CorpusFile { Id = $"real/{ext.TrimStart('.')}/{n:000}", Origin = "real", Ext = ext, RelPath = Path.Combine("real", ext.TrimStart('.'), name), Bytes = size };
                ReadPixelSize(dst, cf);
                corpus.Files.Add(cf);
                left -= size;
                n++;
            }
            log($"  copied {n} {ext} files ({pick.Sum(p => p.Size) / 1e6:0} MB)");
        }
    }

    /// <summary>Pixel size and EXIF orientation from the copy's header (WIC), when a codec can read it.</summary>
    public static void ReadPixelSize(string path, CorpusFile cf)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (dec.Frames.Count == 0) return;
            cf.Width = dec.Frames[0].PixelWidth;
            cf.Height = dec.Frames[0].PixelHeight;
            cf.Orientation = Anything.Preview.WicImage.ReadOrientation(dec.Frames[0]);
        }
        catch { }
    }
}
