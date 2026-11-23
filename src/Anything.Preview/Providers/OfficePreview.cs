using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Anything.Core.Content.Extract;
using Anything.Preview.Util;
using LegacyExcel = Anything.Core.Content.Extract.Office.LegacyExcel;

namespace Anything.Preview;

/// <summary>Built-in Office Open XML / OpenDocument / CSV renderers (used when no shell preview handler is registered).</summary>
internal static class OfficePreview
{
    public const int MaxRows = 2000, MaxCols = 200, MaxParagraphs = 5000;
    /// <summary>Most spaces one OpenDocument text:s element gives.</summary>
    private const int MaxSpaces = 256;

    public static readonly HashSet<string> WordExt = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".dotm" };
    public static readonly HashSet<string> ExcelExt = new(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xlsm", ".xltx", ".xltm" };
    public static readonly HashSet<string> PowerPointExt = new(StringComparer.OrdinalIgnoreCase) { ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm" };
    public static readonly HashSet<string> OdfExt = new(StringComparer.OrdinalIgnoreCase) { ".odt", ".ott", ".ods", ".ots", ".odp", ".otp", ".odg" };

    private static ZipArchive OpenZip(string path, out FileStream fs)
    {
        fs = FileUtil.OpenShared(path);
        try { return new ZipArchive(fs, ZipArchiveMode.Read); }
        catch { fs.Dispose(); throw; }
    }

    private static ZipArchiveEntry? Entry(ZipArchive z, string name) =>
        z.GetEntry(name) ?? z.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XDocument? LoadXml(ZipArchive z, string name)
    {
        var e = Entry(z, name);
        if (e == null) return null;
        using var s = e.Open();
        return LoadPart(s);
    }

    /// <summary>
    /// A part's XML read through the indexer's guards (nesting past 256 levels, tags too long, too many names throw
    /// InvalidDataException): XElement.Value and OdfText walk a tree once per level, and a crafted part nested tens of
    /// thousands deep would overflow the stack, which ends the app. A refused part fails its renderer like a broken one.
    /// No character cap, as before: large shared strings or a long document still load.
    /// </summary>
    private static XDocument LoadPart(Stream s) => OfficeXml.LoadTree(s);

    private static byte[]? ReadEntry(ZipArchive z, string name, long max = 64 << 20)
    {
        var e = Entry(z, name);
        if (e == null || e.Length > max) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool IsThumbnailEntry(ZipArchiveEntry x) =>
        x.FullName.StartsWith("docProps/thumbnail", StringComparison.OrdinalIgnoreCase) ||
        x.FullName.Equals("Thumbnails/thumbnail.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>The document carries a saved thumbnail (what its preview shows when no preview handler is registered).</summary>
    public static bool HasEmbeddedThumbnail(string path)
    {
        try
        {
            using var z = OpenZip(path, out var fs);
            using (fs) return z.Entries.Any(IsThumbnailEntry);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>docProps/thumbnail.* (OOXML) or Thumbnails/thumbnail.png (ODF), if the author saved one.</summary>
    public static Bitmap? EmbeddedThumbnail(string path, Size box)
    {
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var e = z.Entries.FirstOrDefault(IsThumbnailEntry);
            if (e == null) return null;
            var bytes = ReadEntry(z, e.FullName);
            if (bytes == null) return null;
            // docProps/thumbnail.wmf/emf exist too
            if (e.FullName.EndsWith(".wmf", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".emf", StringComparison.OrdinalIgnoreCase))
                return MetafileRenderer.RenderBytes(bytes, box, out _, out _);
            return BitmapUtil.Decode(bytes, box);
        }
    }

    // ------------------------------------------------------------------ Word

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static PreviewResult? RenderDocx(string path, CancellationToken ct)
    {
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var doc = LoadXml(z, "word/document.xml");
            if (doc?.Root == null) return null;
            var blocks = new List<DocBlock>();
            int words = 0;
            foreach (var p in doc.Root.Descendants(W + "p"))
            {
                if (blocks.Count >= MaxParagraphs) break;
                if ((blocks.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                var sb = new StringBuilder();
                foreach (var node in p.Descendants())
                {
                    if (node.Name == W + "t") sb.Append(node.Value);
                    else if (node.Name == W + "tab") sb.Append('\t');
                    else if (node.Name == W + "br" || node.Name == W + "cr") sb.Append('\n');
                }
                string style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
                bool list = p.Element(W + "pPr")?.Element(W + "numPr") != null;
                var st = StyleFromName(style, list);
                // Paragraphs nested in table cells come out one per cell: fine for a preview.
                string text = sb.ToString();
                words += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                blocks.Add(new DocBlock(text, st));
            }
            // drop leading/trailing empties
            while (blocks.Count > 0 && blocks[^1].Text.Length == 0) blocks.RemoveAt(blocks.Count - 1);
            var res = new PreviewResult(path, PreviewKind.Document, LegacyOfficePreview.BuiltInReader) { FormatName = "Word document", Document = blocks };
            res.AddMeta("Paragraphs", blocks.Count.ToString(CultureInfo.InvariantCulture));
            res.AddMeta("Words", words.ToString("N0", CultureInfo.InvariantCulture));
            AddCoreProps(z, res);
            return res;
        }
    }

    /// <summary>
    /// The text of a Word document's first paragraphs, as <see cref="RenderDocx"/> gives it joined line by line, but read
    /// as a stream and only as far as <paramref name="lines"/> lines: a thumbnail's page shows about 40 lines of at most
    /// 200 characters, and building the whole document took up to 220 ms for a big one. The same rules as RenderDocx
    /// (every w:p in document order with the w:t, w:tab, w:br and w:cr inside it, nested paragraphs in their own place
    /// too, empty paragraphs at the end dropped), with each line kept to its first 200 characters (more are never drawn).
    /// Null when the document has no word/document.xml or it can't be read before enough lines were found.
    /// </summary>
    public static string? DocxFirstLines(string path, int lines, CancellationToken ct)
    {
        const int MaxLine = 200;
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var e = Entry(z, "word/document.xml");
            if (e == null) return null;
            var blocks = new List<LineBlock>();
            var open = new Stack<LineBlock>();
            int inText = 0, ready = 0, readyLines = 0;
            string w = W.NamespaceName;
            void Append(ReadOnlySpan<char> s)
            {
                foreach (var b in open)
                    foreach (char c in s)
                    {
                        if (b.Breaks >= lines) break;
                        if (c == '\n') { b.Text.Append(c); b.Breaks++; b.LineLength = 0; }
                        else if (b.LineLength < MaxLine) { b.Text.Append(c); b.LineLength++; }
                    }
            }
            try
            {
                using var s = e.Open();
                using var r = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0);
                while (r.Read())
                {
                    switch (r.NodeType)
                    {
                        case XmlNodeType.Element when r.NamespaceURI == w:
                            switch (r.LocalName)
                            {
                                case "p":
                                    var block = new LineBlock();
                                    blocks.Add(block);
                                    if (r.IsEmptyElement) block.Done = true;
                                    else open.Push(block);
                                    if ((blocks.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                                    break;
                                case "t":
                                    if (!r.IsEmptyElement) inText++;
                                    break;
                                case "tab":
                                    Append("\t");
                                    break;
                                case "br" or "cr":
                                    Append("\n");
                                    break;
                            }
                            break;
                        case XmlNodeType.EndElement when r.NamespaceURI == w:
                            if (r.LocalName == "p" && open.Count > 0) open.Pop().Done = true;
                            else if (r.LocalName == "t" && inText > 0) inText--;
                            break;
                        case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText > 0:
                            Append(r.Value);
                            break;
                    }
                    // Enough lines once every paragraph begun before them has ended: the rest can't come before them.
                    while (ready < blocks.Count && blocks[ready].Done) readyLines += blocks[ready++].Breaks + 1;
                    if (readyLines >= lines) return string.Join("\n", blocks.Take(ready).Select(b => b.Text.ToString()));
                }
            }
            catch (Exception x) when (x is XmlException or InvalidDataException)
            {
                // A part that can't be read (broken, or refused by the guards): no page, as RenderDocx gives none.
                return null;
            }
            int end = blocks.Count;
            while (end > 0 && blocks[end - 1].Text.Length == 0) end--;
            return string.Join("\n", blocks.Take(end).Select(b => b.Text.ToString()));
        }
    }

    private sealed class LineBlock
    {
        public readonly StringBuilder Text = new();
        public int Breaks, LineLength;
        public bool Done;
    }

    private static DocBlockStyle StyleFromName(string style, bool list)
    {
        string s = style.Replace(" ", "").ToLowerInvariant();
        if (s is "title") return DocBlockStyle.Title;
        if (s.StartsWith("heading1") || s == "berschrift1" || s == "titre1") return DocBlockStyle.Heading1;
        if (s.StartsWith("heading2")) return DocBlockStyle.Heading2;
        if (s.StartsWith("heading")) return DocBlockStyle.Heading3;
        if (s.StartsWith("subtitle")) return DocBlockStyle.Heading2;
        if (s.StartsWith("caption")) return DocBlockStyle.Caption;
        if (list || s.StartsWith("list")) return DocBlockStyle.ListItem;
        return DocBlockStyle.Normal;
    }

    private static void AddCoreProps(ZipArchive z, PreviewResult res)
    {
        try
        {
            var core = LoadXml(z, "docProps/core.xml");
            if (core?.Root == null) return;
            string? Get(string local) => core.Root.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
            res.AddMeta("Title", Get("title"));
            res.AddMeta("Author", Get("creator"));
            res.AddMeta("Last modified by", Get("lastModifiedBy"));
        }
        catch { }
    }

    // ------------------------------------------------------------------ Excel

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Shared strings parsed with XDocument: each &lt;si&gt; is either &lt;t&gt; or rich runs &lt;r&gt;&lt;t&gt;; phonetic
    /// &lt;rPh&gt; runs are excluded. (An XmlReader version that called Read() after ReadElementContentAsString skipped
    /// every other plain string.)
    /// </summary>
    internal static List<string> ReadSharedStrings(Stream s)
    {
        var list = new List<string>();
        var doc = LoadPart(s);
        if (doc.Root == null) return list;
        foreach (var si in doc.Root.Elements(S + "si"))
        {
            var direct = si.Element(S + "t");
            if (direct != null && si.Element(S + "r") == null) { list.Add(direct.Value); continue; }
            var sb = new StringBuilder();
            foreach (var r in si.Elements(S + "r"))
                foreach (var t in r.Elements(S + "t")) sb.Append(t.Value);
            if (sb.Length == 0 && direct != null) sb.Append(direct.Value);
            list.Add(sb.ToString());
        }
        return list;
    }

    /// <param name="sheet">The sheet to show, by name (a search matched there); null or unknown = the first visible one.</param>
    public static PreviewResult? RenderXlsx(string path, CancellationToken ct, string? sheet = null)
    {
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var wb = LoadXml(z, "xl/workbook.xml");
            if (wb?.Root == null) return null;
            var sheets = wb.Root.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? new();
            if (sheets.Count == 0) return null;
            var visible = sheets.FirstOrDefault(sh => (string?)sh.Attribute("state") is null or "visible") ?? sheets[0];
            var named = ByName(sheets, sheet, sh => (string?)sh.Attribute("name"));
            var first = named ?? visible;
            string sheetName = (string?)first.Attribute("name") ?? "Sheet1";
            string? rid = (string?)first.Attribute(R + "id");
            string target = "xl/worksheets/sheet1.xml";
            var rels = LoadXml(z, "xl/_rels/workbook.xml.rels");
            var rel = rels?.Root?.Elements(PR + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == rid);
            if (rel != null)
            {
                string t = (string?)rel.Attribute("Target") ?? "";
                target = t.StartsWith('/') ? t.TrimStart('/') : "xl/" + t;
            }
            var shared = new List<string>();
            var sse = Entry(z, "xl/sharedStrings.xml");
            if (sse != null) using (var s = sse.Open()) shared = ReadSharedStrings(s);
            var dateStyles = ReadDateStyles(z);
            ct.ThrowIfCancellationRequested();

            var se = Entry(z, target);
            if (se == null) return null;
            var rows = new SortedDictionary<int, Dictionary<int, string>>();
            int maxCol = 0;
            bool truncated = false;
            using (var s = se.Open())
            using (var xr = OfficeXml.Create(s, ignoreWhitespace: true))
            {
                int curRow = 0, nextCol = 0;
                xr.MoveToContent();
                while (!xr.EOF)
                {
                    if (xr.NodeType != XmlNodeType.Element) { xr.Read(); continue; }
                    if (xr.LocalName == "row")
                    {
                        curRow = int.TryParse(xr.GetAttribute("r"), out int rr) ? rr : curRow + 1;
                        nextCol = 1;
                        if (curRow > MaxRows) { truncated = true; break; }
                        if ((curRow & 127) == 0) ct.ThrowIfCancellationRequested();
                        xr.Read();
                        continue;
                    }
                    if (xr.LocalName != "c") { xr.Read(); continue; }
                    // ReadFrom consumes exactly this element and leaves the reader on the next node: don't Read() again.
                    var c = (XElement)XNode.ReadFrom(xr);
                    string? refAttr = (string?)c.Attribute("r");
                    int col = refAttr != null ? ColumnIndex(refAttr) : nextCol;
                    nextCol = col + 1;
                    if (col > MaxCols) { truncated = true; continue; }
                    string? type = (string?)c.Attribute("t");
                    string? v = c.Element(S + "v")?.Value;
                    string text;
                    switch (type)
                    {
                        case "s":
                            text = int.TryParse(v, out int si) && si >= 0 && si < shared.Count ? shared[si] : "";
                            break;
                        case "inlineStr":
                            text = string.Concat(c.Element(S + "is")?.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value) ?? Array.Empty<string>());
                            break;
                        case "b":
                            text = v == "1" ? "TRUE" : "FALSE";
                            break;
                        case "str":
                        case "e":
                            text = v ?? "";
                            break;
                        default:
                            text = FormatNumber(v, (int?)c.Attribute("s") is int st && dateStyles.Contains(st));
                            break;
                    }
                    if (text.Length == 0) continue;
                    if (!rows.TryGetValue(curRow, out var cells)) rows[curRow] = cells = new Dictionary<int, string>();
                    cells[col] = text;
                    if (col > maxCol) maxCol = col;
                }
            }
            int lastRow = rows.Count == 0 ? 0 : rows.Keys.Max();
            var columns = Enumerable.Range(1, Math.Max(1, maxCol)).Select(ColumnName).ToList();
            var outRows = new List<string[]>(lastRow);
            for (int r = 1; r <= lastRow; r++)
            {
                var arr = new string[columns.Count];
                if (rows.TryGetValue(r, out var cells))
                    foreach (var kv in cells) arr[kv.Key - 1] = kv.Value;
                for (int i = 0; i < arr.Length; i++) arr[i] ??= "";
                outRows.Add(arr);
            }
            var res = new PreviewResult(path, PreviewKind.Table, LegacyOfficePreview.BuiltInReader)
            {
                FormatName = "Excel workbook",
                Table = new TableData
                {
                    Columns = columns, Rows = outRows, ShowRowNumbers = true, Truncated = truncated,
                    Caption = sheets.Count <= 1 ? sheetName
                        : named != null ? $"{sheetName}  (sheet {sheets.IndexOf(first) + 1} of {sheets.Count})"
                        : $"{sheetName}  (sheet 1 of {sheets.Count})",
                },
                Sheet = sheetName,
                SheetFromOptions = named != null && !ReferenceEquals(named, visible),
            };
            res.AddMeta("Sheets", string.Join(", ", sheets.Select(sh => (string?)sh.Attribute("name")).Take(12)) + (sheets.Count > 12 ? ", …" : ""));
            res.AddMeta("Shown", $"{outRows.Count} rows × {columns.Count} columns" + (truncated ? " (truncated)" : ""));
            AddCoreProps(z, res);
            return res;
        }
    }

    /// <summary>Most uncompressed XML read from the sheet for <see cref="XlsxFirstRows"/>; the rows found by then are kept.</summary>
    internal const long FirstRowsMaxSheetBytes = 4L << 20;
    /// <summary>Most uncompressed XML read from the shared strings for <see cref="XlsxFirstRows"/>; past it, no rows.</summary>
    internal const long FirstRowsMaxSharedBytes = 8L << 20;
    /// <summary>Highest shared-string index <see cref="XlsxFirstRows"/> reads up to; a cell needing a later one gives no rows.</summary>
    internal const int FirstRowsMaxSharedIndex = 200_000;
    // workbook.xml and its relationships are small in real files; a larger part is not read further.
    private const long FirstRowsMaxPartBytes = 4L << 20;
    /// <summary>Most uncompressed XML read from xl/styles.xml for <see cref="XlsxFirstRows"/>' dates; a larger part leaves numbers as numbers.</summary>
    internal const long FirstRowsMaxStylesBytes = 2L << 20;

    /// <summary>
    /// The first rows of a workbook's first visible sheet (as <see cref="RenderXlsx"/> picks it with no sheet named), for a
    /// thumbnail: read as streams and only as far as needed, as <see cref="DocxFirstLines"/> reads a Word document. Rows by
    /// row number from row 1 up to <paramref name="maxRows"/>, columns from A up to <paramref name="maxColumns"/>; a null
    /// for an empty cell, trailing empty cells and trailing empty rows cut (an empty list: nothing to show). The sheet's
    /// XML is read up to row <paramref name="maxRows"/> or <see cref="FirstRowsMaxSheetBytes"/>, whichever comes first
    /// (the rows found by then are kept); the shared strings only up to the highest index those cells use. Cell text
    /// follows RenderXlsx (shared and inline strings, numbers, booleans, errors, formula strings), except that a number whose
    /// cell style is a date, a time, both or an elapsed time shows as the old-format reader shows it (LegacyExcel's kinds
    /// and text, in the workbook's 1900 or 1904 system): the styles' number formats are read only when a sheet has numbers,
    /// and not at all past <see cref="FirstRowsMaxStylesBytes"/> or when broken (numbers then show as numbers, as before).
    /// Null when the workbook can't be read (not a zip, no workbook part, no sheet part, broken XML), or when a cell needs
    /// a shared string past <see cref="FirstRowsMaxSharedIndex"/> or past <see cref="FirstRowsMaxSharedBytes"/> of shared
    /// strings. May throw IOException or UnauthorizedAccessException for a file that can't be opened, and
    /// OperationCanceledException.
    /// </summary>
    public static List<string?[]>? XlsxFirstRows(string path, int maxRows, int maxColumns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maxRows <= 0 || maxColumns <= 0) return new List<string?[]>();
        ZipArchive z;
        FileStream fs;
        try { z = OpenZip(path, out fs); }
        catch (InvalidDataException) { return null; }
        using (fs)
        using (z)
        {
            try
            {
                string? target = FirstVisibleSheetPart(z, out bool date1904);
                if (target == null) return null;
                var se = Entry(z, target);
                if (se == null) return null;
                ct.ThrowIfCancellationRequested();

                // Cells by row number, then column; shared-string cells hold their index until the strings are read.
                var rows = new SortedDictionary<int, Dictionary<int, string>>();
                var sharedCells = new List<(int Row, int Col, int Index)>();
                // Number cells with their style, shown as dates afterwards when their style says so.
                var numberCells = new List<(int Row, int Col, string Value, int Style)>();
                int maxShared = -1;
                using (var s = new CappedStream(se.Open(), FirstRowsMaxSheetBytes))
                {
                    try
                    {
                        using var xr = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0);
                        int curRow = 0, nextCol = 1;
                        xr.MoveToContent();
                        while (!xr.EOF)
                        {
                            if (xr.NodeType != XmlNodeType.Element) { xr.Read(); continue; }
                            if (xr.LocalName == "row")
                            {
                                curRow = int.TryParse(xr.GetAttribute("r"), out int rr) ? rr : curRow + 1;
                                nextCol = 1;
                                if (curRow > maxRows) break;
                                if ((curRow & 127) == 0) ct.ThrowIfCancellationRequested();
                                xr.Read();
                                continue;
                            }
                            if (xr.LocalName != "c" || curRow < 1) { xr.Read(); continue; }
                            // ReadFrom consumes exactly this element and leaves the reader on the next node.
                            var c = (XElement)XNode.ReadFrom(xr);
                            string? refAttr = (string?)c.Attribute("r");
                            int col = refAttr != null ? ColumnIndex(refAttr) : nextCol;
                            nextCol = col + 1;
                            if (col > maxColumns) continue;
                            string? type = (string?)c.Attribute("t");
                            string? v = c.Element(S + "v")?.Value;
                            string text;
                            switch (type)
                            {
                                case "s":
                                    if (int.TryParse(v, out int si) && si >= 0)
                                    {
                                        if (si > FirstRowsMaxSharedIndex) return null;
                                        sharedCells.Add((curRow, col, si));
                                        if (si > maxShared) maxShared = si;
                                    }
                                    continue;
                                case "inlineStr":
                                    text = string.Concat(c.Element(S + "is")?.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value) ?? Array.Empty<string>());
                                    break;
                                case "b":
                                    text = v == "1" ? "TRUE" : "FALSE";
                                    break;
                                case "str":
                                case "e":
                                    text = v ?? "";
                                    break;
                                default:
                                    text = FormatNumber(v, false);
                                    if (text.Length > 0 && type is null or "n")
                                        numberCells.Add((curRow, col, v!, int.TryParse((string?)c.Attribute("s"), out int style) ? style : 0));
                                    break;
                            }
                            if (text.Length == 0) continue;
                            if (!rows.TryGetValue(curRow, out var cells)) rows[curRow] = cells = new Dictionary<int, string>();
                            cells[col] = text;
                        }
                    }
                    catch (XmlException) when (s.Capped)
                    {
                        // The sheet's XML ended at the cap mid-way: the rows found before it are kept.
                    }
                }

                if (numberCells.Count > 0 && CellDateKinds(z) is { } kinds)
                {
                    foreach (var (row, col, value, style) in numberCells)
                    {
                        if (style < 0 || style >= kinds.Length || kinds[style] == LegacyExcel.DateKind.None) continue;
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial)) continue;
                        if (LegacyExcel.DateText(serial, kinds[style], date1904) is { } date) rows[row][col] = date;
                    }
                }

                if (maxShared >= 0)
                {
                    var shared = SharedStringsUpTo(z, maxShared, ct);
                    if (shared == null) return null;
                    foreach (var (row, col, index) in sharedCells)
                    {
                        string text = index < shared.Count ? shared[index] : "";
                        if (text.Length == 0) continue;
                        if (!rows.TryGetValue(row, out var cells)) rows[row] = cells = new Dictionary<int, string>();
                        cells[col] = text;
                    }
                }

                var result = new List<string?[]>();
                if (rows.Count == 0) return result;
                int lastRow = rows.Keys.Max();
                for (int r = 1; r <= lastRow; r++)
                {
                    if (!rows.TryGetValue(r, out var cells)) { result.Add(Array.Empty<string?>()); continue; }
                    var arr = new string?[cells.Keys.Max()];
                    foreach (var kv in cells) arr[kv.Key - 1] = kv.Value;
                    result.Add(arr);
                }
                return result;
            }
            catch (Exception e) when (e is XmlException or InvalidDataException)
            {
                // A part that can't be read (broken, or refused by the guards): no rows, as RenderXlsx gives none.
                return null;
            }
        }
    }

    /// <summary>
    /// The part of the first visible sheet (as RenderXlsx picks it), from xl/workbook.xml and its relationships read as
    /// streams; null when the workbook lists no sheet. <paramref name="date1904"/>: the workbook counts dates from 1904
    /// (workbookPr, which comes before the sheets).
    /// </summary>
    private static string? FirstVisibleSheetPart(ZipArchive z, out bool date1904)
    {
        date1904 = false;
        var wb = Entry(z, "xl/workbook.xml");
        if (wb == null) return null;
        string? firstId = null, visibleId = null;
        bool any = false, visibleFound = false;
        using (var s = new CappedStream(wb.Open(), FirstRowsMaxPartBytes))
        using (var xr = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0))
        {
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.NamespaceURI != S.NamespaceName) continue;
                if (xr.LocalName == "workbookPr")
                {
                    date1904 = xr.GetAttribute("date1904") is "1" or "true";
                    continue;
                }
                if (xr.LocalName != "sheet") continue;
                string? state = xr.GetAttribute("state");
                string? rid = xr.GetAttribute("id", R.NamespaceName);
                if (!any) { any = true; firstId = rid; }
                if (state is null or "visible") { visibleFound = true; visibleId = rid; break; }
            }
        }
        if (!any) return null;
        string? id = visibleFound ? visibleId : firstId;
        string target = "xl/worksheets/sheet1.xml";
        var re = Entry(z, "xl/_rels/workbook.xml.rels");
        if (re != null && id != null)
        {
            using var s = new CappedStream(re.Open(), FirstRowsMaxPartBytes);
            using var xr = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0);
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "Relationship" || xr.NamespaceURI != PR.NamespaceName) continue;
                if (xr.GetAttribute("Id") != id) continue;
                target = PartPath("xl/", xr.GetAttribute("Target") ?? "");
                break;
            }
        }
        return target;
    }

    /// <summary>
    /// Shared strings 0 to <paramref name="last"/> (fewer when the part has fewer), by RenderXlsx's rules (a plain t, else
    /// the runs' t, phonetic runs left out), read as a stream that stops once the last one needed is read. Null when more
    /// than <see cref="FirstRowsMaxSharedBytes"/> of the part would have to be read; an empty list when there is no part.
    /// </summary>
    private static List<string>? SharedStringsUpTo(ZipArchive z, int last, CancellationToken ct)
    {
        var list = new List<string>();
        var e = Entry(z, "xl/sharedStrings.xml");
        if (e == null) return list;
        string ns = S.NamespaceName;
        using var s = new CappedStream(e.Open(), FirstRowsMaxSharedBytes);
        try
        {
            // Whitespace between elements left out, as ReadSharedStrings loads the part.
            using var xr = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0);
            StringBuilder? direct = null, runs = null, into = null;
            string? child = null; // the element straight in the si that the reader is in (r, rPh...)
            int siDepth = -1, tDepth = -1;
            while (xr.Read())
            {
                switch (xr.NodeType)
                {
                    case XmlNodeType.Element when xr.NamespaceURI == ns:
                        if (siDepth < 0)
                        {
                            if (xr.LocalName != "si") break;
                            if (xr.IsEmptyElement) list.Add("");
                            else { siDepth = xr.Depth; direct = null; runs = null; child = null; }
                        }
                        else if (xr.LocalName == "t" && !xr.IsEmptyElement && into == null)
                        {
                            // A t straight in the si is the plain string; one in an r (straight in the si) is a run; any
                            // other t (in a phonetic rPh) is left out.
                            if (xr.Depth == siDepth + 1) into = direct ??= new StringBuilder();
                            else if (xr.Depth == siDepth + 2 && child == "r") into = runs ??= new StringBuilder();
                            if (into != null) tDepth = xr.Depth;
                        }
                        else if (xr.Depth == siDepth + 1) child = xr.LocalName;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when into != null:
                        into.Append(xr.Value);
                        break;
                    case XmlNodeType.EndElement when siDepth >= 0:
                        if (into != null && xr.Depth == tDepth) into = null;
                        else if (xr.Depth == siDepth + 1) child = null;
                        else if (xr.Depth == siDepth)
                        {
                            list.Add(runs is { Length: > 0 } ? runs.ToString() : direct?.ToString() ?? "");
                            siDepth = -1;
                            if ((list.Count & 1023) == 0) ct.ThrowIfCancellationRequested();
                        }
                        break;
                }
                if (list.Count > last) return list;
            }
            return list;
        }
        catch (XmlException) when (s.Capped)
        {
            return null;
        }
    }

    /// <summary>
    /// What each cell style (by its index in cellXfs, the s of a cell) shows of a serial date, by the old-format reader's
    /// rules (LegacyExcel: a custom format of the workbook by its letters, else the built-in format's kind), from
    /// xl/styles.xml read as a stream up to the end of cellXfs. Null when there is no styles part, or when it is larger
    /// than <see cref="FirstRowsMaxStylesBytes"/> uncompressed, or broken: the numbers then show as numbers.
    /// </summary>
    private static LegacyExcel.DateKind[]? CellDateKinds(ZipArchive z)
    {
        var e = Entry(z, "xl/styles.xml");
        if (e == null || e.Length > FirstRowsMaxStylesBytes) return null;
        var custom = new Dictionary<int, LegacyExcel.DateKind>();
        var formats = new List<int>();
        string ns = S.NamespaceName;
        CappedStream? s = null;
        try
        {
            // Opened inside the try: a damaged entry (bad header, unknown compression) costs the dates, not the page.
            // One byte over the cap is let through: a part of exactly the cap reads to its end without counting as cut.
            s = new CappedStream(e.Open(), FirstRowsMaxStylesBytes + 1);
            using var xr = OfficeXml.Create(s, ignoreWhitespace: true, maxCharacters: 0);
            // Only the numFmts and cellXfs straight under the root: a dxf and cellStyleXfs have numFmt and xf elements too.
            bool inFormats = false, inCellXfs = false;
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == 1)
                {
                    if (inCellXfs) break; // the styles after it are not needed
                    inFormats = false;
                    continue;
                }
                if (xr.NodeType != XmlNodeType.Element || xr.NamespaceURI != ns) continue;
                if (xr.Depth == 1)
                {
                    inFormats = xr.LocalName == "numFmts" && !xr.IsEmptyElement;
                    inCellXfs = xr.LocalName == "cellXfs" && !xr.IsEmptyElement;
                }
                else if (xr.Depth == 2 && inFormats && xr.LocalName == "numFmt")
                {
                    if (int.TryParse(xr.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        custom[id] = LegacyExcel.FormatKind(xr.GetAttribute("formatCode") ?? "");
                }
                else if (xr.Depth == 2 && inCellXfs && xr.LocalName == "xf")
                    formats.Add(int.TryParse(xr.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0);
            }
            if (s.Capped) return null; // more than the cap after all (the size the zip gave was wrong)
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return null; // damaged, broken, refused by the guards, or cut at the cap
        }
        finally
        {
            s?.Dispose();
        }
        var kinds = new LegacyExcel.DateKind[formats.Count];
        for (int i = 0; i < kinds.Length; i++)
            kinds[i] = custom.TryGetValue(formats[i], out var kind) ? kind : LegacyExcel.BuiltInKind(formats[i]);
        return kinds;
    }

    /// <summary>A read-only stream that ends after <c>limit</c> bytes of the one it wraps (and owns); Capped says it did.</summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long _left = limit;
        public bool Capped { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_left <= 0)
            {
                if (buffer.Length > 0) Capped = true;
                return 0;
            }
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The item whose name is <paramref name="name"/> (exactly, else ignoring case, as Excel compares sheet names); null when none.</summary>
    internal static T? ByName<T>(IReadOnlyList<T> items, string? name, Func<T, string?> nameOf) where T : class
    {
        if (string.IsNullOrEmpty(name)) return null;
        return items.FirstOrDefault(i => string.Equals(nameOf(i), name, StringComparison.Ordinal))
            ?? items.FirstOrDefault(i => string.Equals(nameOf(i), name, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<int> ReadDateStyles(ZipArchive z)
    {
        var set = new HashSet<int>();
        try
        {
            var st = LoadXml(z, "xl/styles.xml");
            if (st?.Root == null) return set;
            var custom = new Dictionary<int, string>();
            foreach (var nf in st.Root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? Enumerable.Empty<XElement>())
                if (int.TryParse((string?)nf.Attribute("numFmtId"), out int id)) custom[id] = (string?)nf.Attribute("formatCode") ?? "";
            int i = 0;
            foreach (var xf in st.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
            {
                int id = int.TryParse((string?)xf.Attribute("numFmtId"), out int n) ? n : 0;
                bool isDate = id is >= 14 and <= 22 or >= 45 and <= 47;
                if (!isDate && custom.TryGetValue(id, out var code))
                {
                    string c = System.Text.RegularExpressions.Regex.Replace(code, "\\[[^\\]]*\\]|\"[^\"]*\"", "").ToLowerInvariant();
                    isDate = (c.Contains('y') || c.Contains('d') || c.Contains("mm") || c.Contains('h')) && !c.Contains('0') && !c.Contains('#');
                }
                if (isDate) set.Add(i);
                i++;
            }
        }
        catch { }
        return set;
    }

    private static string FormatNumber(string? v, bool isDate)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return v;
        if (isDate && d > 0 && d < 2958466)
        {
            var dt = DateTime.FromOADate(d);
            return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d < 1 ? dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        return d.ToString("G15", CultureInfo.CurrentCulture);
    }

    private static int ColumnIndex(string cellRef)
    {
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return Math.Max(1, col);
    }

    internal static string ColumnName(int col)
    {
        var sb = new StringBuilder();
        while (col > 0) { col--; sb.Insert(0, (char)('A' + col % 26)); col /= 26; }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ PowerPoint (text outline)

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <param name="presentationOrder">
    /// As the index reads the deck (a search match points at a slide): slides in the presentation's own order
    /// (sldIdLst), numbered by position, each followed by its speaker notes. Otherwise slides by part number, no notes.
    /// </param>
    public static PreviewResult? RenderPptxText(string path, CancellationToken ct, bool presentationOrder = false)
    {
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var slides = z.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entry: e, Num: int.TryParse(Path.GetFileNameWithoutExtension(e.Name).AsSpan(5), out int n) ? n : 0))
                .OrderBy(x => x.Num).ToList();
            if (slides.Count == 0) return null;
            if (presentationOrder && PresentationOrder(z) is { Count: > 0 } ordered)
                slides = ordered.Select((e, i) => (e, i + 1)).ToList();
            var blocks = new List<DocBlock>();
            foreach (var (e, num) in slides)
            {
                ct.ThrowIfCancellationRequested();
                blocks.Add(new DocBlock($"Slide {num}", DocBlockStyle.Heading2));
                using (var s = e.Open())
                {
                    var x = LoadPart(s);
                    foreach (var p in x.Descendants(A + "p"))
                    {
                        string t = string.Concat(p.Descendants(A + "t").Select(t => t.Value));
                        if (t.Length > 0) blocks.Add(new DocBlock(t, DocBlockStyle.ListItem));
                    }
                }
                if (presentationOrder)
                    foreach (var note in SlideNotes(z, e)) blocks.Add(new DocBlock(note, DocBlockStyle.Caption));
                if (blocks.Count > MaxParagraphs) break;
            }
            var res = new PreviewResult(path, PreviewKind.Document, LegacyOfficePreview.BuiltInReader) { FormatName = "PowerPoint presentation", Document = blocks, ShapedByMatch = presentationOrder };
            res.AddMeta("Slides", slides.Count.ToString(CultureInfo.InvariantCulture));
            AddCoreProps(z, res);
            return res;
        }
    }

    /// <summary>The slide parts in presentation order (ppt/presentation.xml sldIdLst through its rels); null when that can't be read.</summary>
    private static List<ZipArchiveEntry>? PresentationOrder(ZipArchive z)
    {
        try
        {
            var pres = LoadXml(z, "ppt/presentation.xml");
            var rels = LoadXml(z, "ppt/_rels/presentation.xml.rels");
            if (pres?.Root == null || rels?.Root == null) return null;
            var targets = rels.Root.Elements(PR + "Relationship")
                .Where(r => (string?)r.Attribute("Id") != null)
                .GroupBy(r => (string)r.Attribute("Id")!)
                .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("Target") ?? "");
            var list = new List<ZipArchiveEntry>();
            foreach (var id in pres.Root.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>())
            {
                if ((string?)id.Attribute(R + "id") is not { } rid || !targets.TryGetValue(rid, out var target)) return null;
                if (Entry(z, PartPath("ppt/", target)) is not { } e) return null;
                list.Add(e);
            }
            return list;
        }
        catch (Exception e) when (e is XmlException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The speaker notes of a slide (its notesSlide's body placeholder), one string per paragraph.</summary>
    private static List<string> SlideNotes(ZipArchive z, ZipArchiveEntry slide)
    {
        var notes = new List<string>();
        try
        {
            string dir = slide.FullName[..(slide.FullName.LastIndexOf('/') + 1)];
            var rels = LoadXml(z, dir + "_rels/" + slide.Name + ".rels");
            string? target = rels?.Root?.Elements(PR + "Relationship")
                .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true)
                ?.Attribute("Target")?.Value;
            if (target == null) return notes;
            var doc = LoadXml(z, PartPath(dir, target));
            if (doc?.Root == null) return notes;
            foreach (var sp in doc.Root.Descendants(P + "sp"))
            {
                var ph = sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
                if ((string?)ph?.Attribute("type") != "body") continue;
                foreach (var p in sp.Descendants(A + "p"))
                {
                    string t = string.Concat(p.Descendants(A + "t").Select(t => t.Value));
                    if (t.Length > 0) notes.Add(t);
                }
            }
        }
        catch (Exception e) when (e is XmlException or IOException or InvalidDataException) { }
        return notes;
    }

    /// <summary>A relationship target resolved against the folder of its source part ("../notesSlides/n.xml", "/ppt/x.xml").</summary>
    private static string PartPath(string folder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg is not ("" or ".")) parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    // ------------------------------------------------------------------ OpenDocument

    private static readonly XNamespace OText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    /// <param name="sheet">For spreadsheets: the table to show, by name (a search matched there); null or unknown = the first.</param>
    public static PreviewResult? RenderOdf(string path, string ext, CancellationToken ct, string? sheet = null)
    {
        using var z = OpenZip(path, out var fs);
        using (fs)
        {
            var content = LoadXml(z, "content.xml");
            if (content?.Root == null) return null;
            bool isSheet = ext is ".ods" or ".ots";
            if (isSheet)
            {
                var tables = content.Descendants(OTable + "table").ToList();
                var named = ByName(tables, sheet, t => (string?)t.Attribute(OTable + "name"));
                var table = named ?? tables.FirstOrDefault();
                if (table == null) return null;
                var rows = new List<string[]>();
                int maxCols = 0;
                foreach (var row in table.Descendants(OTable + "table-row"))
                {
                    int rep = Math.Min(int.TryParse((string?)row.Attribute(OTable + "number-rows-repeated"), out int rr) ? rr : 1, 50);
                    var cells = new List<string>();
                    foreach (var cell in row.Elements().Where(e => e.Name == OTable + "table-cell" || e.Name == OTable + "covered-table-cell"))
                    {
                        int crep = int.TryParse((string?)cell.Attribute(OTable + "number-columns-repeated"), out int cr) ? cr : 1;
                        string text = string.Join("\n", cell.Elements(OText + "p").Select(p => p.Value));
                        for (int i = 0; i < Math.Min(crep, MaxCols - cells.Count); i++) cells.Add(text);
                        if (cells.Count >= MaxCols) break;
                    }
                    while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                    for (int i = 0; i < rep && rows.Count < MaxRows; i++) rows.Add(cells.ToArray());
                    maxCols = Math.Max(maxCols, cells.Count);
                    if (rows.Count >= MaxRows) break;
                }
                while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
                var norm = rows.Select(r => r.Length == maxCols ? r : r.Concat(Enumerable.Repeat("", maxCols - r.Length)).ToArray()).ToList();
                return new PreviewResult(path, PreviewKind.Table, LegacyOfficePreview.BuiltInReader)
                {
                    FormatName = "OpenDocument spreadsheet",
                    Table = new TableData
                    {
                        Columns = Enumerable.Range(1, Math.Max(1, maxCols)).Select(ColumnName).ToList(), Rows = norm, ShowRowNumbers = true,
                        Caption = (string?)table.Attribute(OTable + "name"),
                    },
                    Sheet = (string?)table.Attribute(OTable + "name") ?? "",
                    SheetFromOptions = named != null && !ReferenceEquals(named, tables[0]),
                };
            }
            var blocks = new List<DocBlock>();
            foreach (var el in content.Descendants().Where(e => e.Name == OText + "p" || e.Name == OText + "h"))
            {
                if (blocks.Count >= MaxParagraphs) break;
                var st = DocBlockStyle.Normal;
                if (el.Name == OText + "h")
                {
                    int lvl = int.TryParse((string?)el.Attribute(OText + "outline-level"), out int l) ? l : 1;
                    st = lvl <= 1 ? DocBlockStyle.Heading1 : lvl == 2 ? DocBlockStyle.Heading2 : DocBlockStyle.Heading3;
                }
                else if (el.Parent?.Name == OText + "list-item") st = DocBlockStyle.ListItem;
                var sb = new StringBuilder();
                OdfText(el, sb, 0);
                blocks.Add(new DocBlock(sb.ToString(), st));
            }
            if (blocks.Count == 0) return null;
            return new PreviewResult(path, PreviewKind.Document, LegacyOfficePreview.BuiltInReader) { FormatName = "OpenDocument text", Document = blocks };
        }
    }

    /// <summary>A paragraph's text; elements deeper than the XML guard allows (it refuses such parts anyway) are left out.</summary>
    private static void OdfText(XElement el, StringBuilder sb, int depth)
    {
        foreach (var n in el.Nodes())
        {
            if (n is XText t) sb.Append(t.Value);
            else if (n is XElement e)
            {
                // text:c counts the spaces; a hostile file can claim two billion of them.
                if (e.Name == OText + "s") sb.Append(' ', int.TryParse((string?)e.Attribute(OText + "c"), out int c) ? Math.Clamp(c, 1, MaxSpaces) : 1);
                else if (e.Name == OText + "tab") sb.Append('\t');
                else if (e.Name == OText + "line-break") sb.Append('\n');
                else if (depth < OfficeXml.MaxDepth) OdfText(e, sb, depth + 1);
            }
        }
    }

    // ------------------------------------------------------------------ CSV / TSV

    public static PreviewResult? RenderCsv(string path, CancellationToken ct)
    {
        var bytes = FileUtil.ReadHead(path, 4 * 1024 * 1024);
        var (text, enc) = TextPreview.Decode(bytes);
        if (text == null) return null;
        char delim = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tab", StringComparison.OrdinalIgnoreCase)
            ? '\t' : DetectDelimiter(text);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        int maxCols = 0;
        bool truncated = bytes.Length >= 4 * 1024 * 1024;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) { inQuotes = true; continue; }
            if (ch == delim) { row.Add(field.ToString()); field.Clear(); continue; }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > MaxCols) row.RemoveRange(MaxCols, row.Count - MaxCols);
                rows.Add(row.ToArray());
                maxCols = Math.Max(maxCols, row.Count);
                row.Clear();
                if (rows.Count >= MaxRows * 5) { truncated = true; break; }
                if ((rows.Count & 1023) == 0) ct.ThrowIfCancellationRequested();
                continue;
            }
            field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); maxCols = Math.Max(maxCols, row.Count); }
        if (rows.Count == 0) return null;
        // First row as header when it looks like one (no numbers).
        bool header = rows.Count > 1 && rows[0].All(c => !double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        var cols = header ? rows[0].Concat(Enumerable.Range(rows[0].Length + 1, Math.Max(0, maxCols - rows[0].Length)).Select(ColumnName)).ToList()
                          : Enumerable.Range(1, maxCols).Select(ColumnName).ToList();
        var body = (header ? rows.Skip(1) : rows).Select(r => r.Length == maxCols ? r : r.Concat(Enumerable.Repeat("", maxCols - r.Length)).ToArray()).ToList();
        var res = new PreviewResult(path, PreviewKind.Table, "CSV")
        {
            FormatName = delim == '\t' ? "Tab-separated values" : "Comma-separated values",
            Table = new TableData { Columns = cols, Rows = body, ShowRowNumbers = true, Truncated = truncated },
            TextEncoding = enc,
        };
        res.AddMeta("Rows", body.Count.ToString("N0", CultureInfo.InvariantCulture) + (truncated ? "+" : ""));
        res.AddMeta("Columns", maxCols.ToString(CultureInfo.InvariantCulture));
        res.AddMeta("Delimiter", delim == '\t' ? "Tab" : $"'{delim}'");
        return res;
    }

    private static char DetectDelimiter(string text)
    {
        int nl = text.IndexOf('\n');
        string first = nl > 0 ? text[..nl] : text;
        var cands = new[] { ',', ';', '\t', '|' };
        return cands.OrderByDescending(c => first.Count(ch => ch == c)).First();
    }
}
