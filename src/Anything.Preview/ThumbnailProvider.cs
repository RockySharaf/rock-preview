using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anything.Core.Content.Extract.Office;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// One thumbnail at a time, for callers that just want a picture of a file: a thin wrapper over
/// <see cref="ThumbnailService.Shared"/> (the grid talks to the service directly, with whole viewports). Also home to
/// Rock Preview's own extractors for the kinds Windows handles poorly (CorelDRAW, PSD, TGA, DWG, DXF, InDesign and
/// Illustrator XMP, EPS, Office thumbnails, PDF, fonts, text pages; and for Word and Excel files without a thumbnail of
/// their own, old .doc and .xls included, a page of the first lines or a sheet page of the first cells), which the
/// service runs as step 4 of its ladder.
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
            case ".doc": case ".dot":
            {
                // The service asks Windows first (the picture the author saved, if any); this runs after a miss. The
                // reader loads whole streams, so a big file gets no page; a file that is no Word 97-2003 document (HTML
                // or text named .doc) goes on to the text sniff, as when only Windows was asked. RTF named .doc gets no
                // page (its first lines are markup): its icon, as a real .rtf.
                if (OldOfficeReadable(path, OldOfficeMaxBytes))
                {
                    string? text = LegacyWord.ReadText(path, LegacyWordMaxChars, ct);
                    if (text != null) return FirstLines(text) is { } lines ? TextPage(lines, size) : null;
                }
                return ThumbSources.TextSniff(path, size, noPageForRtf: true);
            }
            case ".xls": case ".xlt":
            {
                // As for .doc: after Windows had no picture; a file that is no Excel 97-2003 workbook (HTML or CSV named
                // .xls) goes on to the text sniff, RTF named .xls to its icon. Its own, lower size cap.
                if (OldOfficeReadable(path, OldExcelMaxBytes))
                {
                    int chosen = -1;
                    var sheets = LegacyExcel.ReadSheets(path, SheetPageMaxRows, SheetPageMaxColumns, listed => chosen = FirstShownSheet(listed), ct);
                    if (sheets != null)
                        return chosen >= 0 && chosen < sheets.Count && !sheets[chosen].NotRead ? SheetPage(sheets[chosen].Rows, size) : null;
                }
                return ThumbSources.TextSniff(path, size, noPageForRtf: true);
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
                // gets its text page, an Excel file its sheet page, and the other kinds end in their icon.
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
            else if (OfficePreview.ExcelExt.Contains(ext))
            {
                // Only the first rows of the first visible sheet are read; a sheet with nothing in them gets no page.
                var rows = OfficePreview.XlsxFirstRows(path, SheetPageMaxRows, SheetPageMaxColumns, ct);
                if (rows != null) return SheetPage(rows, size);
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

    // An old Word file's page needs only its first lines: this many characters is more than 40 lines show.
    internal const int LegacyWordMaxChars = 8000;
    // Rows and columns read for a sheet page: more than the largest page shows (about 30 rows, 12 narrow columns).
    internal const int SheetPageMaxRows = 40, SheetPageMaxColumns = 12;
    // The old-format readers load whole streams: a bigger file gets no page of its own (Windows' picture or the icon).
    internal const long OldOfficeMaxBytes = 16L << 20;
    // The Excel reader walks the whole workbook while it holds the one text turn (60-90 ms for 1.5 MB): a lower cap.
    internal const long OldExcelMaxBytes = 4L << 20;

    // Whether an old Word or Excel file is small enough for its reader (a file that is gone throws, as other kinds do).
    private static bool OldOfficeReadable(string path, long maxBytes) => new FileInfo(path).Length <= maxBytes;

    /// <summary>
    /// The first lines of a document's text as the Word page shows them (the docx path's limits: <see cref="TextPageMaxLines"/>
    /// lines of at most 200 characters, empty lines at the end left out); null when there is no text to show.
    /// </summary>
    internal static string? FirstLines(string text)
    {
        const int MaxLine = 200;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int count = Math.Min(lines.Length, TextPageMaxLines);
        while (count > 0 && string.IsNullOrWhiteSpace(lines[count - 1])) count--;
        if (count == 0) return null;
        return string.Join("\n", lines.Take(count).Select(l => l.Length > MaxLine ? l[..MaxLine] : l));
    }

    /// <summary>The sheet an Excel thumbnail shows: the first that is not hidden, else the first; -1 for none.</summary>
    internal static int FirstShownSheet(IReadOnlyList<LegacyExcelSheet> sheets)
    {
        for (int i = 0; i < sheets.Count; i++)
            if (!sheets[i].Hidden) return i;
        return sheets.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// A "sheet page": the first rows and columns of a worksheet drawn as a small grid, with a header row of column letters
    /// and a gutter of row numbers, on a page of a text page's size, background and mark (<see cref="IsTextPage"/>: shown,
    /// never kept on disk). <paramref name="rows"/> are row by row from row 1, a null or empty string for an empty cell (a
    /// null row is an empty row). Column widths come from the cells shown, clamped so one long cell can't take the page;
    /// cell text is one line, clipped at its cell's edge. Null when no cell has anything to show (the icon, as for an empty
    /// Word file).
    /// </summary>
    internal static Bitmap? SheetPage(IReadOnlyList<string?[]?>? rows, int size)
    {
        if (rows == null || !rows.Any(r => r != null && r.Any(c => !string.IsNullOrWhiteSpace(c)))) return null;
        size = Math.Max(16, size);
        int h = size, w = Math.Max(16, (int)(size * 0.78));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb) { Tag = TextPageMark };
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float fontPx = Math.Clamp(size / 22f, 4f, 11f);
            using var font = new Font("Consolas", fontPx, GraphicsUnit.Pixel);
            using var labelFont = new Font("Consolas", Math.Max(4f, fontPx * 0.85f), GraphicsUnit.Pixel);
            float cw = g.MeasureString("0000000000", font, PointF.Empty, StringFormat.GenericTypographic).Width / 10f;
            if (!(cw > 0.5f)) cw = fontPx * 0.55f;

            int pad = Math.Max(1, (int)MathF.Round(fontPx * 0.3f));
            int rowH = Math.Max(4, (int)MathF.Ceiling(fontPx * 1.45f));
            int headH = Math.Max(4, (int)MathF.Ceiling(fontPx * 1.3f));
            const int left = 1, top = 1;
            int right = w - 1, bottom = h - 1;
            // Rows that fit, the last one may be cut by the page's edge as a sheet's window cuts it.
            int shownRows = Math.Max(1, (bottom - top - headH + rowH - 1) / rowH);
            int gutterW = (int)MathF.Ceiling(shownRows.ToString(CultureInfo.InvariantCulture).Length * cw * 0.85f) + 2 * pad;

            // Column widths from the cells shown: an empty column as wide as a few digits, one long cell at most 45% of
            // the grid's width; as many columns as fit (the last one cut by the page's edge).
            int gridW = right - left - gutterW;
            int minW = (int)MathF.Ceiling(3 * cw) + 2 * pad, emptyW = (int)MathF.Ceiling(6 * cw) + 2 * pad;
            int maxW = Math.Max(minW, (int)(gridW * 0.45f));
            int dataRows = Math.Min(rows.Count, shownRows);
            int dataCols = 0;
            for (int r = 0; r < dataRows; r++) dataCols = Math.Max(dataCols, rows[r]?.Length ?? 0);
            var widths = new List<int>();
            for (int x = left + gutterW, c = 0; x < right; c++)
            {
                int units = 0;
                if (c < dataCols)
                    for (int r = 0; r < dataRows; r++)
                        if (rows[r] is { } row && c < row.Length && row[c] is { } cell) units = Math.Max(units, DisplayUnits(cell));
                int cwPx = units == 0 ? emptyW : Math.Clamp((int)MathF.Ceiling(units * cw) + 2 * pad, minW, maxW);
                widths.Add(cwPx);
                x += cwPx;
            }

            var gridPen = new Pen(Color.FromArgb(222, 224, 228));
            var headPen = new Pen(Color.FromArgb(196, 199, 205));
            using (gridPen)
            using (headPen)
            using (var headBrush = new SolidBrush(Color.FromArgb(242, 243, 245)))
            using (var labelBrush = new SolidBrush(Color.FromArgb(118, 118, 118)))
            using (var textBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            using (var label = CellFormat(StringAlignment.Center, false))
            using (var cellNear = CellFormat(StringAlignment.Near, false))
            using (var cellFar = CellFormat(StringAlignment.Far, false))
            using (var cellRtl = CellFormat(StringAlignment.Near, true))
            {
                g.SetClip(new Rectangle(left, top, right - left, bottom - top));
                // The header row and the gutter, slim and light grey, so it reads as a spreadsheet at a glance.
                g.FillRectangle(headBrush, left, top, right - left, headH);
                g.FillRectangle(headBrush, left, top, gutterW, bottom - top);
                int gridTop = top + headH, gridLeft = left + gutterW;
                for (int r = 0; r < shownRows; r++)
                {
                    int y = gridTop + r * rowH;
                    g.DrawLine(gridPen, left, y + rowH, right, y + rowH);
                    g.DrawString((r + 1).ToString(CultureInfo.InvariantCulture), labelFont, labelBrush, new RectangleF(left, y, gutterW, rowH), label);
                }
                for (int c = 0, x = gridLeft; c < widths.Count; x += widths[c], c++)
                {
                    g.DrawLine(gridPen, x + widths[c], top, x + widths[c], bottom);
                    g.DrawString(OfficePreview.ColumnName(c + 1), labelFont, labelBrush, new RectangleF(x, top, widths[c], headH), label);
                }
                g.DrawLine(headPen, left, gridTop, right, gridTop);
                g.DrawLine(headPen, gridLeft, top, gridLeft, bottom);

                // The cells: one line each, numbers to the right as a sheet shows them (### when too wide), right-to-left
                // text from the right, clipped at the cell's edge (no NoClip flag), never wrapped.
                for (int r = 0; r < dataRows; r++)
                {
                    if (rows[r] is not { } row) continue;
                    int y = gridTop + r * rowH;
                    for (int c = 0, x = gridLeft; c < widths.Count && c < row.Length; x += widths[c], c++)
                    {
                        if (row[c] is not { } cell || string.IsNullOrWhiteSpace(cell)) continue;
                        var rect = new RectangleF(x + pad, y + 1, widths[c] - 2 * pad, rowH - 1);
                        if (!(rect.Width > 0)) continue;
                        var (text, number) = CellShown(cell, rect.Width, (int)(widths[c] / Math.Max(1f, cw)) + 2,
                            s => g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width);
                        var format = number ? cellFar : StartsRightToLeft(text) ? cellRtl : cellNear;
                        g.DrawString(text, font, textBrush, rect, format);
                    }
                }
                g.ResetClip();
            }
            using (var border = new Pen(Color.FromArgb(200, 200, 200))) g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
        return bmp;
    }

    // Typographic, as the widths were measured (the default format pads each side and cut the last character), one line,
    // centred in the row, and without NoClip so the text stops at the cell's edge.
    private static StringFormat CellFormat(StringAlignment alignment, bool rightToLeft)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags = StringFormatFlags.NoWrap | (rightToLeft ? StringFormatFlags.DirectionRightToLeft : 0);
        f.Alignment = alignment;
        f.LineAlignment = StringAlignment.Center;
        f.Trimming = StringTrimming.None;
        return f;
    }

    /// <summary>
    /// What a sheet page draws in a cell <paramref name="width"/> pixels wide: its first line, and whether that is a number
    /// (drawn at the right). A number wider than the cell is "###" (as a sheet shows it, drawn at the left), so it never
    /// loses digits at either end; text is cut to <paramref name="maxChars"/> characters and clipped at the cell's edge.
    /// </summary>
    internal static (string Text, bool Number) CellShown(string cell, float width, int maxChars, Func<string, float> measure)
    {
        string line = CellLine(cell, int.MaxValue);
        if (LooksNumeric(line))
            // Longer than maxChars can never fit (the font is fixed-width), so it is not measured.
            return line.Length > maxChars || measure(line) > width ? ("###", false) : (line, true);
        return (line.Length > maxChars ? line[..maxChars] : line, false);
    }

    // A cell's first line, tabs as spaces, at most max characters (more could never show in its cell).
    private static string CellLine(string cell, int max)
    {
        int end = cell.AsSpan().IndexOfAny('\r', '\n');
        var line = end >= 0 ? cell.AsSpan(0, end) : cell.AsSpan();
        line = line.Trim();
        if (line.Length > max) line = line[..max];
        return line.ToString().Replace('\t', ' ');
    }

    // How many narrow characters wide a cell's first line is: East Asian wide characters count two.
    private static int DisplayUnits(string cell)
    {
        int units = 0;
        foreach (char ch in cell)
        {
            if (ch is '\r' or '\n') break;
            units += IsWide(ch) ? 2 : 1;
            if (units >= 200) break;
        }
        return units;
    }

    private static bool IsWide(char ch) => ch is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3'
        or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6'
        or >= '\uD800' and <= '\uDFFF'; // a surrogate pair is most often a wide ideograph or emoji

    private static bool LooksNumeric(string text)
        => text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.' or ',')
            && (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out _)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    // Whether the first strongly directional character is Hebrew or Arabic (then the cell reads from the right).
    private static bool StartsRightToLeft(string text)
    {
        foreach (char ch in text)
        {
            if (ch is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') return true;
            if (char.IsLetter(ch)) return false;
        }
        return false;
    }
}
