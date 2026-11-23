using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Anything.Core.Content.Extract.Office;

namespace Anything.Preview;

/// <summary>
/// Built-in text renderers for the Office 97-2003 binary formats (.doc, .xls, .ppt), shaped like their Office Open XML
/// counterparts in <see cref="OfficePreview"/> so the pane shows them the same way. Used when Office's own previewer is
/// missing, busy, failed or not allowed, and ahead of it when a search match asks for text. The readers in
/// Anything.Core give the text only (no styles, no document properties); a file they can't read gives null, so the
/// chain goes on.
/// </summary>
internal static class LegacyOfficePreview
{
    /// <summary>Most characters read from a Word document: far more than <see cref="OfficePreview.MaxParagraphs"/> paragraphs need.</summary>
    private const int MaxDocChars = 2_000_000;
    /// <summary>Most characters read from a presentation over all its slides.</summary>
    private const int MaxSlideChars = 1_000_000;
    /// <summary>
    /// The provider the header names ("via ..."): not the Office program's name, which would read as if Word, Excel or
    /// PowerPoint had made the preview, often right above a line saying that program could not show the file. The new
    /// Office and OpenDocument renderers in <see cref="OfficePreview"/> use it too.
    /// </summary>
    internal const string BuiltInReader = "built-in reader";

    public static readonly HashSet<string> WordExt = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".dot" };
    public static readonly HashSet<string> ExcelExt = new(StringComparer.OrdinalIgnoreCase) { ".xls", ".xlt" };
    public static readonly HashSet<string> PowerPointExt = new(StringComparer.OrdinalIgnoreCase) { ".ppt", ".pps", ".pot" };

    // ------------------------------------------------------------------ Word

    /// <summary>
    /// A Word 97-2003 document as paragraphs, like <see cref="OfficePreview.RenderDocx"/>: every paragraph plain (the
    /// reader gives no styles), a table row with its tabs between cells kept as one paragraph, at most
    /// <see cref="OfficePreview.MaxParagraphs"/> of them.
    /// </summary>
    public static PreviewResult? RenderDoc(string path, CancellationToken ct)
    {
        string? text = LegacyWord.ReadText(path, MaxDocChars, ct);
        if (text == null) return null;
        var blocks = new List<DocBlock>();
        int words = 0, start = 0;
        while (start <= text.Length && blocks.Count < OfficePreview.MaxParagraphs)
        {
            if ((blocks.Count & 255) == 0) ct.ThrowIfCancellationRequested();
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            string p = text[start..end];
            words += p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            blocks.Add(new DocBlock(p));
            start = end + 1;
        }
        while (blocks.Count > 0 && blocks[^1].Text.Length == 0) blocks.RemoveAt(blocks.Count - 1);
        var res = new PreviewResult(path, PreviewKind.Document, BuiltInReader) { FormatName = "Word 97-2003 document", Document = blocks };
        res.AddMeta("Paragraphs", blocks.Count.ToString(CultureInfo.InvariantCulture));
        res.AddMeta("Words", words.ToString("N0", CultureInfo.InvariantCulture));
        return res;
    }

    // ------------------------------------------------------------------ Excel

    /// <summary>
    /// One sheet of an Excel 97-2003 workbook as a table, like <see cref="OfficePreview.RenderXlsx"/>: the same row and
    /// column limits, caption, sheet list and "Shown" line.
    /// </summary>
    /// <param name="sheet">
    /// The sheet to show, by name (a search matched there; a hidden sheet too); null or unknown = the first visible one,
    /// or the first one when every sheet is hidden. Hidden sheets are listed and counted like the others, as for .xlsx.
    /// </param>
    public static PreviewResult? RenderXls(string path, CancellationToken ct, string? sheet = null)
    {
        // Only the sheet that will be shown is read: the asked one by name, else the first visible one, else the first.
        int shownIndex = 0;
        bool fromOptions = false;
        var sheets = LegacyExcel.ReadSheets(path, OfficePreview.MaxRows, OfficePreview.MaxCols, listed =>
        {
            int visibleIndex = 0;
            for (int i = 0; i < listed.Count; i++) if (!listed[i].Hidden) { visibleIndex = i; break; }
            var named = OfficePreview.ByName(listed, sheet, s => s.Name);
            int namedIndex = named == null ? -1 : IndexOf(listed, named);
            shownIndex = namedIndex >= 0 ? namedIndex : visibleIndex;
            fromOptions = namedIndex >= 0 && namedIndex != visibleIndex;
            return shownIndex;
        }, ct);
        if (sheets == null || sheets.Count == 0 || shownIndex >= sheets.Count) return null;
        var shown = sheets[shownIndex];
        // A sheet the reader had no room left to read comes back NotRead: say so rather than show a bare table.
        bool unread = shown.NotRead;
        // Read, but every cell lies beyond the row and column limits.
        bool beyond = !unread && shown.Truncated && shown.Rows.Count == 0;
        int maxCol = 0;
        foreach (var row in shown.Rows) maxCol = Math.Max(maxCol, Math.Min(row.Length, OfficePreview.MaxCols));
        var columns = Enumerable.Range(1, Math.Max(1, maxCol)).Select(OfficePreview.ColumnName).ToList();
        var outRows = new List<string[]>(Math.Min(shown.Rows.Count, OfficePreview.MaxRows));
        foreach (var row in shown.Rows)
        {
            if (outRows.Count >= OfficePreview.MaxRows) break;
            if ((outRows.Count & 1023) == 0) ct.ThrowIfCancellationRequested();
            var arr = new string[columns.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = i < row.Length ? row[i] ?? "" : "";
            outRows.Add(arr);
        }
        bool truncated = shown.Truncated || shown.Rows.Count > outRows.Count;
        string name = shown.Name;
        string caption = sheets.Count <= 1 ? name : $"{name}  (sheet {shownIndex + 1} of {sheets.Count})";
        var res = new PreviewResult(path, PreviewKind.Table, BuiltInReader)
        {
            FormatName = "Excel 97-2003 workbook",
            Table = new TableData
            {
                // Not marked truncated with no rows: the table's caption would add "showing the first 0 rows".
                Columns = columns, Rows = outRows, ShowRowNumbers = true, Truncated = truncated && !unread && !beyond,
                Caption = unread ? caption + "  -  not read, the workbook is too large" : caption,
            },
            Sheet = name,
            SheetFromOptions = fromOptions,
        };
        res.AddMeta("Sheets", string.Join(", ", sheets.Select(s => s.Name).Take(12)) + (sheets.Count > 12 ? ", …" : ""));
        // The footer shows "Shown: " before the value, so the value itself has no colon.
        res.AddMeta("Shown", unread ? "none (this sheet was not read, the workbook is too large)"
            : beyond ? string.Create(CultureInfo.InvariantCulture, $"none within the first {OfficePreview.MaxRows} rows and {OfficePreview.MaxCols} columns")
            : $"{outRows.Count} rows × {columns.Count} columns" + (truncated ? " (truncated)" : ""));
        return res;
    }

    private static int IndexOf(IReadOnlyList<LegacyExcelSheet> sheets, LegacyExcelSheet s)
    {
        for (int i = 0; i < sheets.Count; i++) if (ReferenceEquals(sheets[i], s)) return i;
        return 0;
    }

    // ------------------------------------------------------------------ PowerPoint

    /// <summary>
    /// A PowerPoint 97-2003 presentation as a text outline, like <see cref="OfficePreview.RenderPptxText"/>: a
    /// "Slide N" heading for each slide in presentation order, then its lines. When the text was cut at the reader's
    /// limit (or at the paragraph limit), a last line says that the rest of the presentation is not shown.
    /// </summary>
    public static PreviewResult? RenderPpt(string path, CancellationToken ct)
    {
        var slides = LegacyPowerPoint.ReadSlides(path, MaxSlideChars, ct);
        if (slides == null || slides.Count == 0) return null;
        // The reader stops at its limit, cutting the slide that reaches it and leaving out the ones after.
        long total = 0;
        foreach (var s in slides) total += s.Length;
        bool cut = total >= MaxSlideChars;
        // Cut at the limit, the last slide with text can end in half a surrogate pair (U+FFFD) or a bare line end.
        int lastWithText = -1;
        if (cut)
            for (int i = slides.Count - 1; i >= 0 && lastWithText < 0; i--)
                if (slides[i].Length > 0) lastWithText = i;
        var blocks = new List<DocBlock>();
        int shown = 0;
        for (int i = 0; i < slides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string slide = i == lastWithText ? slides[i].TrimEnd('\uFFFD', '\n', '\r') : slides[i];
            // A slide with text needs room for its heading and at least one line, so a heading is never left with no
            // lines under it; an empty slide needs room for its heading only.
            bool hasText = slide.AsSpan().IndexOfAnyExcept('\n') >= 0;
            if (blocks.Count + (hasText ? 1 : 0) >= OfficePreview.MaxParagraphs) { cut = true; break; }
            blocks.Add(new DocBlock($"Slide {i + 1}", DocBlockStyle.Heading2));
            bool full = false;
            for (int start = 0; start < slide.Length;)
            {
                int end = slide.IndexOf('\n', start);
                if (end < 0) end = slide.Length;
                if (end > start)
                {
                    // One slide of very many short lines must not go far past the limit either.
                    if (blocks.Count >= OfficePreview.MaxParagraphs) { full = true; break; }
                    blocks.Add(new DocBlock(slide[start..end], DocBlockStyle.ListItem));
                }
                start = end + 1;
            }
            shown++;
            if (full) { cut = true; break; }
        }
        // Not "more slides": the cut can fall inside the last slide.
        if (cut) blocks.Add(new DocBlock("The rest of the presentation is not shown.", DocBlockStyle.Caption));
        var res = new PreviewResult(path, PreviewKind.Document, BuiltInReader) { FormatName = "PowerPoint 97-2003 presentation", Document = blocks };
        res.AddMeta("Slides", shown.ToString(CultureInfo.InvariantCulture) + (cut ? "+" : ""));
        return res;
    }
}
