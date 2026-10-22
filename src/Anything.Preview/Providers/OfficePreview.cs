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
using Anything.Preview.Util;

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
        return XDocument.Load(s, LoadOptions.None);
    }

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
            var res = new PreviewResult(path, PreviewKind.Document, "OOXML") { FormatName = "Word document", Document = blocks };
            res.AddMeta("Paragraphs", blocks.Count.ToString(CultureInfo.InvariantCulture));
            res.AddMeta("Words", words.ToString("N0", CultureInfo.InvariantCulture));
            AddCoreProps(z, res);
            return res;
        }
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
        var doc = XDocument.Load(s);
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
            using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit }))
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
            var res = new PreviewResult(path, PreviewKind.Table, "OOXML")
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

    /// <summary>The item whose name is <paramref name="name"/> (exactly, else ignoring case, as Excel compares sheet names); null when none.</summary>
    private static T? ByName<T>(IReadOnlyList<T> items, string? name, Func<T, string?> nameOf) where T : class
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
                    var x = XDocument.Load(s);
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
            var res = new PreviewResult(path, PreviewKind.Document, "OOXML") { FormatName = "PowerPoint presentation", Document = blocks, ShapedByMatch = presentationOrder };
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
                return new PreviewResult(path, PreviewKind.Table, "OpenDocument")
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
                blocks.Add(new DocBlock(OdfText(el), st));
            }
            if (blocks.Count == 0) return null;
            return new PreviewResult(path, PreviewKind.Document, "OpenDocument") { FormatName = "OpenDocument text", Document = blocks };
        }
    }

    private static string OdfText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var n in el.Nodes())
        {
            if (n is XText t) sb.Append(t.Value);
            else if (n is XElement e)
            {
                // text:c counts the spaces; a hostile file can claim two billion of them.
                if (e.Name == OText + "s") sb.Append(' ', int.TryParse((string?)e.Attribute(OText + "c"), out int c) ? Math.Clamp(c, 1, MaxSpaces) : 1);
                else if (e.Name == OText + "tab") sb.Append('\t');
                else if (e.Name == OText + "line-break") sb.Append('\n');
                else sb.Append(OdfText(e));
            }
        }
        return sb.ToString();
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
