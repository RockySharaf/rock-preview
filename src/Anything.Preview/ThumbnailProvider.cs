using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// One thumbnail at a time, for callers that just want a picture of a file: a thin wrapper over
/// <see cref="ThumbnailService.Shared"/> (the grid talks to the service directly, with whole viewports). Also home to
/// Rock Preview's own extractors for the kinds Windows handles poorly (CorelDRAW, PSD, TGA, DWG, DXF, InDesign and
/// Illustrator XMP, EPS, Office thumbnails, PDF, fonts, text pages), which the service runs as step 4 of its ladder.
/// </summary>
public static class ThumbnailProvider
{
    private static readonly object _lock = new();
    private static readonly List<Waiter> _waiters = new();
    private static ThumbClient? _client;

    private sealed class Waiter(ThumbRequest request, TaskCompletionSource<Bitmap?> tcs)
    {
        public readonly ThumbRequest Request = request;
        public readonly TaskCompletionSource<Bitmap?> Tcs = tcs;
    }

    /// <summary>
    /// Returns a thumbnail that fits in <paramref name="size"/>×<paramref name="size"/> pixels (the caller owns and must
    /// dispose it): the picture, or the file's icon when there is none; null if the file is gone. A request cancelled
    /// before a worker starts on it costs nothing; one cancelled later is finished and stored, not thrown away. Never
    /// throws except <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task<Bitmap?> GetThumbnailAsync(string path, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 16, 1024);
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new Waiter(new ThumbRequest(path, ThumbStamp.Unknown, size, ThumbPriority.Visible), tcs);
        lock (_lock)
        {
            _client ??= ThumbnailService.Shared.CreateClient(OnArrival);
            _waiters.Add(waiter);
            Resubmit();
        }
        if (ct.CanBeCanceled)
        {
            var reg = ct.Register(() =>
            {
                lock (_lock)
                {
                    if (!_waiters.Remove(waiter)) return;
                    Resubmit();
                }
                tcs.TrySetCanceled(ct);
            });
            tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        }
        return tcs.Task;
    }

    /// <summary>Forgets the icons and failures the service remembers in memory (the store on disk is kept).</summary>
    public static void ClearCache() => ThumbnailService.Shared.ClearMemory();

    // Every waiting call is one "viewport": the plan holds them all, in the order asked, so a cancelled one leaves it.
    private static void Resubmit() => _client!.SubmitViewport(1, _waiters.Select(w => w.Request).ToList());

    private static void OnArrival(ThumbArrival a)
    {
        if (a.Quality == ThumbQuality.Low)
        {
            // Callers here want the real thing: the stand-in is skipped.
            a.Bitmap?.Dispose();
            return;
        }
        List<Waiter> done;
        lock (_lock)
        {
            done = _waiters.Where(w => w.Request.DisplaySize == a.DisplaySize && w.Request.Key == a.Key).ToList();
            foreach (var w in done) _waiters.Remove(w);
        }
        if (done.Count == 0)
        {
            a.Bitmap?.Dispose();
            return;
        }
        for (int i = 0; i < done.Count; i++)
        {
            var bmp = a.Bitmap == null ? null : i == done.Count - 1 ? a.Bitmap : new Bitmap(a.Bitmap);
            if (!done[i].Tcs.TrySetResult(bmp)) bmp?.Dispose();
        }
    }

    /// <summary>
    /// Rock Preview's own extractor for <paramref name="path"/> (step 4 of the ladder for the kinds it renders itself),
    /// about <paramref name="size"/> on its long side; null when it has nothing. May throw for an unreadable file.
    /// </summary>
    internal static async Task<Bitmap?> ExtractAsync(string path, string ext, int size, CancellationToken ct)
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
            if (t != null)
            {
                // A saved thumbnail that is one flat colour is a library's empty template page, not this document's look
                // (one pass over a picture of the box's size, well under a millisecond): it is dropped, so a Word file
                // gets its text page and the other kinds end in their icon.
                bool blank;
                try { blank = ThumbBlank.IsBlank(t); }
                catch { t.Dispose(); throw; } // GDI+ could not read it: the picture is not left for the finalizer
                if (!blank) return t;
                t.Dispose();
            }
            if (OfficePreview.WordExt.Contains(ext))
            {
                // Only the start of the document is read: the page shows its first lines (about 40 at the largest size).
                // A document with no text at all (only pictures) gets no page: an empty one would pass for a thumbnail.
                string? text = OfficePreview.DocxFirstLines(path, TextPageMaxLines, ct);
                if (!string.IsNullOrWhiteSpace(text)) return TextPage(text, size);
            }
            return null;
        }
        if (WicImage.Extensions.Contains(ext))
        {
            var bmp = WicImage.DecodeThumbnail(path, ext, size, null, out _, out _);
            return bmp;
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

    /// <summary>The first page of a PDF rendered to fit <paramref name="box"/> (the master is made at the size class itself).</summary>
    private static async Task<Bitmap?> PdfFirstPage(string path, Size box, CancellationToken ct)
    {
        using var src = await PdfPreview.PdfPageSource.OpenAsync(path, ct).ConfigureAwait(false);
        if (src == null) return null;
        var bmp = await src.RenderAsync(0, box, ct).ConfigureAwait(false);
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

    // More lines than a page at the largest size class shows (about 37 at 512).
    private const int TextPageMaxLines = 64;

    // Marks a picture drawn from a file's own text (Image.Tag), so the thumbnail service never keeps it on disk.
    private static readonly object TextPageMark = new();

    /// <summary>Whether <paramref name="bmp"/> is a page drawn from a file's own text by <see cref="TextPage"/> (shown, never kept on disk).</summary>
    internal static bool IsTextPage(Bitmap? bmp) => bmp != null && ReferenceEquals(bmp.Tag, TextPageMark);

    /// <summary>A little "page" with the first lines of text (marked as such: <see cref="IsTextPage"/>).</summary>
    internal static Bitmap TextPage(string text, int size)
    {
        int h = size, w = Math.Max(16, (int)(size * 0.78));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb) { Tag = TextPageMark };
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
}
