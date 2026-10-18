using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Interop;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// Fast, thread-safe thumbnails for a results grid. Order: Explorer's shell thumbnail (cached by Windows, so cheap)
/// → our own extractors for formats the shell can't thumbnail (CorelDRAW, PSD, TGA, DWG, DXF, InDesign/Illustrator
/// XMP, EPS, Office docProps thumbnails, PDF, fonts, text pages) → the file-type icon. Results are kept in an LRU
/// keyed by path + size + last-write time. Shell calls run on STA threads (normal priority, behind preview-pane work);
/// our extractors are bounded to 4 at a time. A request whose token is cancelled before it reaches a worker costs
/// nothing, so a scrolling grid can request/cancel freely.
/// </summary>
public static class ThumbnailProvider
{
    private static readonly SemaphoreSlim _extractors = new(4);
    // 12M pixels = 48 MB of 32-bit thumbnails (~470 at 160 px). The grid keeps its own copies of what is on
    // screen, so this only needs to make scrolling back and forth cheap.
    private static readonly LruCache _cache = new(maxPixels: 12L * 1024 * 1024, maxCount: 2000);

    /// <summary>
    /// Returns a thumbnail that fits in <paramref name="size"/>×<paramref name="size"/> pixels (the caller owns and must
    /// dispose it), or null if the file is gone. Never throws except <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<Bitmap?> GetThumbnailAsync(string path, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 16, 1024);
        ct.ThrowIfCancellationRequested();
        long stamp;
        bool isDir;
        try
        {
            var fi = new FileInfo(path);
            isDir = (fi.Exists ? fi.Attributes : File.GetAttributes(path)).HasFlag(FileAttributes.Directory);
            stamp = isDir ? Directory.GetLastWriteTimeUtc(path).Ticks : fi.LastWriteTimeUtc.Ticks ^ fi.Length;
        }
        catch
        {
            return null;
        }
        string key = path.ToUpperInvariant() + "|" + size + "|" + stamp;
        if (_cache.TryGetCopy(key, out var hit)) return hit;

        Bitmap? bmp = null;
        // 1. Shell thumbnail (thumbnail cache first; the extractor runs only on a cache miss).
        bmp = await Shell.GetThumbnailAsync(path, size, ct, highPriority: false, biggerOk: true).ConfigureAwait(false);
        string ext = PreviewRenderer.GetExtension(path);
        // 2. Our extractors.
        if (bmp == null && !isDir)
        {
            await _extractors.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                bmp = await Task.Run(() => ExtractAsync(path, ext, size, ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { bmp = null; }
            finally { _extractors.Release(); }
        }
        // 3. Icon.
        bmp ??= await Shell.GetIconAsync(path, size, ct, highPriority: false).ConfigureAwait(false);
        if (bmp == null) return null;
        bmp = BitmapUtil.FitOrSelf(bmp, new Size(size, size));
        return _cache.AddAndCopy(key, bmp);
    }

    /// <summary>Drops all cached thumbnails.</summary>
    public static void ClearCache() => _cache.Clear();

    private static async Task<Bitmap?> ExtractAsync(string path, string ext, int size, CancellationToken ct)
    {
        var box = new Size(size, size);
        switch (ext)
        {
            case ".cdr": case ".cdt": case ".cdrx": case ".cmx": case ".des":
                return EmbeddedPreviews.FromCorel(path, box, out _, out _);
            case ".psd": case ".psb":
                return PsdDecoder.Decode(path, box, ct, out _, out _);
            case ".tga": case ".icb": case ".vda":
            {
                var data = FileUtil.ReadAllCapped(path, 256L * 1024 * 1024);
                return data == null ? null : TgaDecoder.Decode(data, out _) is { } t ? BitmapUtil.FitOrSelf(t, box) : null;
            }
            case ".dwg": case ".dwt":
                return EmbeddedPreviews.FromDwg(path, box, out _, out _);
            case ".dxf":
            {
                using var r = DxfRenderer.Render(path, new Size(size, size), ct);
                return Detach(r);
            }
            case ".indd": case ".indt": case ".ai": case ".ait":
            {
                var bmp = Xmp(path, box);
                if (bmp != null || ext is ".indd" or ".indt") return bmp;
                var head = FileUtil.ReadHead(path, 8);
                if (Bytes.StartsWithAscii(head, "%PDF")) return await PdfFirstPage(path, box, ct).ConfigureAwait(false);
                return EmbeddedPreviews.FromEpsBinaryHeader(path, box, out _);
            }
            case ".eps": case ".epsf": case ".ps":
                return EmbeddedPreviews.FromEpsBinaryHeader(path, box, out _) ?? Xmp(path, box);
            case ".pdf":
                return await PdfFirstPage(path, box, ct).ConfigureAwait(false);
            case ".blend":
                return EmbeddedPreviews.FromBlend(path, box);
            case ".afdesign": case ".afphoto": case ".afpub":
            {
                var img = EmbeddedPreviews.FindLargestEmbeddedImage(FileUtil.ReadHead(path, 32 * 1024 * 1024));
                return img == null ? null : BitmapUtil.Decode(img, box);
            }
            case ".emf": case ".wmf":
            {
                var data = FileUtil.ReadAllCapped(path, 64L * 1024 * 1024);
                return data == null ? null : MetafileRenderer.RenderBytes(data, box, out _, out _);
            }
        }
        if (OfficePreview.WordExt.Contains(ext) || OfficePreview.ExcelExt.Contains(ext) || OfficePreview.PowerPointExt.Contains(ext) ||
            OfficePreview.OdfExt.Contains(ext))
        {
            var t = OfficePreview.EmbeddedThumbnail(path, box);
            if (t != null) return t;
            if (OfficePreview.WordExt.Contains(ext))
            {
                using var doc = OfficePreview.RenderDocx(path, ct);
                if (doc?.Document != null) return TextPage(string.Join("\n", doc.Document.Select(d => d.Text)), size);
            }
            return null;
        }
        if (WicImage.Extensions.Contains(ext))
        {
            using var r = await WicImage.RenderAsync(path, ext, new Size(size / 2, size / 2), ct).ConfigureAwait(false);
            return Detach(r);
        }
        if (FontPreview.Extensions.Contains(ext))
        {
            using var r = await FontPreview.RenderAsync(path, new Size(480, 480), ct).ConfigureAwait(false);
            return Detach(r);
        }
        // Text: render the first lines onto a small page.
        var bytes = FileUtil.ReadHead(path, 16 * 1024);
        if (TextPreview.KnownTextExtensions.Contains(ext) || TextPreview.LooksLikeText(bytes, out _))
        {
            var (text, _) = TextPreview.Decode(bytes);
            if (text != null) return TextPage(text, size);
        }
        return null;
    }

    private static Bitmap? Xmp(string path, Size box)
    {
        var jpg = EmbeddedPreviews.FindXmpThumbnail(path);
        return jpg == null ? null : BitmapUtil.Decode(jpg, box);
    }

    private static async Task<Bitmap?> PdfFirstPage(string path, Size box, CancellationToken ct)
    {
        using var src = await PdfPreview.PdfPageSource.OpenAsync(path, ct).ConfigureAwait(false);
        if (src == null) return null;
        var bmp = await src.RenderAsync(0, new Size(box.Width * 2 / 3, box.Height * 2 / 3), ct).ConfigureAwait(false);
        return BitmapUtil.FitOrSelf(bmp, box);
    }

    private static Bitmap? Detach(PreviewResult? r)
    {
        if (r?.Image == null) return null;
        var bmp = r.Image;
        r.Image = null;
        if (r.Frames != null)
        {
            foreach (var f in r.Frames) if (!ReferenceEquals(f, bmp)) f.Dispose();
            r.Frames = null;
        }
        return bmp;
    }

    /// <summary>A little "page" with the first lines of text.</summary>
    internal static Bitmap TextPage(string text, int size)
    {
        int h = size, w = Math.Max(16, (int)(size * 0.78));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(Color.FromArgb(200, 200, 200))) g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        float fontPx = Math.Clamp(size / 22f, 4f, 11f);
        using var font = new Font("Consolas", fontPx, GraphicsUnit.Pixel);
        var lines = text.Replace("\r", "").Replace("\t", "    ").Split('\n');
        float y = Math.Max(3, size / 24f), lh = fontPx * 1.2f, x = Math.Max(3, size / 24f);
        using var brush = new SolidBrush(Color.FromArgb(40, 40, 40));
        foreach (var line in lines)
        {
            if (y + lh > h - 2) break;
            string l = line.Length > 200 ? line[..200] : line;
            g.DrawString(l, font, brush, new RectangleF(x, y, w - x - 2, lh + 2), StringFormat.GenericTypographic);
            y += lh;
        }
        return bmp;
    }

    /// <summary>LRU of bitmaps bounded by count and total pixels. Copies are made under the lock (GDI+ isn't thread-safe).</summary>
    private sealed class LruCache
    {
        private readonly long _maxPixels;
        private readonly int _maxCount;
        private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> _map = new();
        private readonly LinkedList<(string Key, Bitmap Bmp)> _list = new();
        private long _pixels;
        private readonly object _lock = new();

        public LruCache(long maxPixels, int maxCount) { _maxPixels = maxPixels; _maxCount = maxCount; }

        public bool TryGetCopy(string key, out Bitmap? copy)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    copy = new Bitmap(node.Value.Bmp);
                    return true;
                }
            }
            copy = null;
            return false;
        }

        public Bitmap AddAndCopy(string key, Bitmap bmp)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var old))
                {
                    _list.Remove(old);
                    _pixels -= (long)old.Value.Bmp.Width * old.Value.Bmp.Height;
                    old.Value.Bmp.Dispose();
                }
                var node = _list.AddFirst((key, bmp));
                _map[key] = node;
                _pixels += (long)bmp.Width * bmp.Height;
                while ((_pixels > _maxPixels || _map.Count > _maxCount) && _list.Last != null && _list.Last != node)
                {
                    var last = _list.Last;
                    _list.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _pixels -= (long)last.Value.Bmp.Width * last.Value.Bmp.Height;
                    last.Value.Bmp.Dispose();
                }
                return new Bitmap(bmp);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var n in _list) n.Bmp.Dispose();
                _list.Clear();
                _map.Clear();
                _pixels = 0;
            }
        }
    }
}
