using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Word, Excel and PowerPoint 2007 and later: document, sheets in order, slides and notes, core properties. Parts are
/// found through their relationships and elements matched by local name, so Strict OOXML (purl.oclc.org namespaces)
/// reads like Transitional. Word: body, headers, footers, footnotes, endnotes and comments; w:t text (whitespace and CDATA
/// kept), w:tab, w:br/w:cr and paragraph ends; deleted, moved-from and field-code text skipped. Excel: shared strings
/// (_xHHHH_ decoded, phonetic rPh runs left out) and each sheet in workbook order as its own page starting with its name,
/// cells separated by tabs. PowerPoint: slides in sldIdLst order with their notes, one page each. mc:Fallback copies
/// (the second, VML copy of every text box) are skipped. An OLE file with an EncryptedPackage is PasswordProtected. A part
/// that is damaged or over its decode cap is skipped and the document marked Truncated.
/// </summary>
internal sealed class OoxmlReader : IContentReader
{
    public ContentKind Kind => ContentKind.Office;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "docx" or "docm" or "dotx" or "dotm" or "xlsx" or "xlsm" or "xltx" or "xltm" or "pptx" or "pptm" or "ppsx" or "ppsm" or "potx" or "potm";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        if (CompoundFile.IsCompoundFile(source))
            return CompoundFile.Open(source, budget) is { } cfb && cfb.HasEncryptedPackage() ? DocStatus.PasswordProtected : DocStatus.Unsupported;
        var refused = OfficeZip.TryOpen(source, out var opened);
        if (refused != null || opened == null) return refused ?? DocStatus.Damaged;
        using var zip = opened;
        var reader = new Package(zip, sink, budget);
        reader.Run();
        return reader.Partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    /// <summary>One package's walk.</summary>
    sealed class Package(OfficeZip zip, ExtractSink sink, ExtractBudget budget)
    {
        readonly char[] _chunk = new char[4096];

        /// <summary>A part was skipped (damaged, or past its decode cap).</summary>
        public bool Partial { get; private set; }

        public void Run()
        {
            var root = Relationships("");
            string main = Target(root, "officeDocument") ?? FirstExisting("word/document.xml", "xl/workbook.xml", "ppt/presentation.xml") ?? "";
            if ((Target(root, "core-properties") ?? (zip.Entry("docProps/core.xml") != null ? "docProps/core.xml" : null)) is { } core) Part(core, CoreProperties);
            // The main part's root element decides; when it can't be read (damaged, or refused for a DTD) its name does.
            string? kind = RootElement(main) ?? Path.GetFileNameWithoutExtension(main).ToLowerInvariant() switch
            {
                "document" => "document",
                "workbook" => "workbook",
                "presentation" => "presentation",
                _ => null,
            };
            switch (kind)
            {
                case "document": Word(main); break;
                case "workbook": Excel(main); break;
                case "presentation": PowerPoint(main); break;
                default: throw new InvalidDataException("No Office document part.");
            }
            if (sink.Pages == 0 && (Target(root, "extended-properties") ?? (zip.Entry("docProps/app.xml") != null ? "docProps/app.xml" : null)) is { } app)
                Part(app, AppProperties);
        }

        string? FirstExisting(params string[] names)
        {
            foreach (var n in names) if (zip.Entry(n) != null) return n;
            return null;
        }

        // ------------------------------------------------------------------ Word

        void Word(string main)
        {
            Part(main, WalkText);
            var rels = Relationships(main);
            foreach (var type in (ReadOnlySpan<string>)["header", "footer", "footnotes", "endnotes", "comments"])
                foreach (var target in Targets(rels, type))
                {
                    if (sink.Full) return;
                    sink.LineBreak();
                    Part(target, WalkText);
                }
        }

        /// <summary>Subtrees with no document text: properties, field codes, deleted text, phonetic guides, fallback copies.</summary>
        static bool Skipped(XmlReader r) => r.LocalName switch
        {
            "pPr" or "rPr" or "sectPr" or "tblPr" or "tblGrid" or "trPr" or "tcPr" or "tblPrEx" or "tabs" or "tabLst" or "bodyPr"
                or "lstStyle" or "spPr" or "nvSpPr" or "nvPicPr" or "nvGrpSpPr" or "nvGraphicFramePr" or "grpSpPr" or "instrText"
                or "delText" or "delInstrText" or "moveFrom" or "rt" or "rPh" or "phoneticPr" or "binData" or "fldData" or "extLst"
                or "style" or "blipFill" or "xfrm" => true,
            "Fallback" => OfficeXml.IsMarkupCompatibility(r.NamespaceURI),
            _ => false,
        };

        /// <summary>Text of a Word part, a slide or notes: t, tab, br/cr and paragraph ends, by local name.</summary>
        void WalkText(XmlReader r)
        {
            bool more = r.Read();
            int nodes = 0;
            while (more && !sink.Full)
            {
                if ((++nodes & 1023) == 0) budget.Check();
                if (r.Depth > OfficeXml.MaxDepth) throw new InvalidDataException("Too deep.");
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (Skipped(r)) { r.Skip(); more = !r.EOF; continue; }
                    switch (r.LocalName)
                    {
                        case "t": OfficeXml.ReadText(r, _chunk, s => sink.Text(s)); break;
                        case "tab": sink.Text("\t"); break;
                        case "br" or "cr": sink.LineBreak(); break;
                        case "noBreakHyphen": sink.Text("-"); break;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "p") sink.LineBreak();
                more = r.Read();
            }
        }

        // ------------------------------------------------------------------ Excel

        void Excel(string main)
        {
            var rels = Relationships(main);
            var sheets = new List<(string Name, string? Id)>();
            Part(main, r => Sheets(r, sheets));
            var shared = new SharedStrings(budget.Limits.MaxTextChars * 2);
            if ((Target(rels, "sharedStrings") ?? Resolved(main, "sharedStrings.xml")) is { } sst) Part(sst, r => ReadSharedStrings(r, shared));
            int read = 0;
            foreach (var (name, id) in sheets)
            {
                if (sink.Full) break;
                string? target = id != null && rels.TryGetValue(id, out var rel) ? rel.Target : null;
                if (target == null) continue;
                sink.StartSheet(name);
                read++;
                Part(target, r => Sheet(r, shared));
            }
            if (sink.Pages == 0) sink.Pages = read;
        }

        string? Resolved(string from, string name)
        {
            string p = OfficeZip.Resolve(from, name);
            return zip.Entry(p) != null ? p : null;
        }

        static void Sheets(XmlReader r, List<(string, string?)> sheets)
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet") continue;
                string name = r.GetAttribute("name") ?? "";
                sheets.Add((name, RelationshipId(r)));
                if (sheets.Count >= 10_000) return;
            }
        }

        void ReadSharedStrings(XmlReader r, SharedStrings shared)
        {
            var current = new StringBuilder();
            bool inItem = false;
            bool more = r.Read();
            int nodes = 0;
            while (more)
            {
                if ((++nodes & 1023) == 0) budget.Check();
                if (r.Depth > OfficeXml.MaxDepth) throw new InvalidDataException("Too deep.");
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "si":
                            if (r.IsEmptyElement) shared.Add("");
                            else { inItem = true; current.Clear(); }
                            break;
                        case "rPh" or "phoneticPr" or "extLst":
                            r.Skip();
                            more = !r.EOF;
                            continue;
                        case "t" when inItem:
                            OfficeXml.ReadText(r, _chunk, s => { if (current.Length < ExtractSink.MaxFieldChars * 16) current.Append(s); });
                            break;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si" && inItem)
                {
                    shared.Add(OfficeXml.DecodeEscapes(current.ToString()));
                    inItem = false;
                }
                more = r.Read();
            }
        }

        void Sheet(XmlReader r, SharedStrings shared)
        {
            var value = new StringBuilder();
            string? type = null;
            bool inCell = false, rowHasCells = false, inline = false;
            bool more = r.Read();
            int nodes = 0;
            while (more && !sink.Full)
            {
                if ((++nodes & 1023) == 0) budget.Check();
                if (r.Depth > OfficeXml.MaxDepth) throw new InvalidDataException("Too deep.");
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "row":
                            rowHasCells = false;
                            break;
                        case "c":
                            type = r.GetAttribute("t");
                            value.Clear();
                            inline = false;
                            inCell = !r.IsEmptyElement;
                            break;
                        case "v" when inCell:
                            OfficeXml.ReadText(r, _chunk, s => { if (value.Length < ExtractSink.MaxFieldChars * 16) value.Append(s); });
                            break;
                        case "is" when inCell:
                            inline = true;
                            break;
                        case "t" when inCell && inline:
                            OfficeXml.ReadText(r, _chunk, s => { if (value.Length < ExtractSink.MaxFieldChars * 16) value.Append(s); });
                            break;
                        case "f" or "rPh" or "phoneticPr" or "extLst" or "headerFooter" or "sheetPr" or "cols" or "conditionalFormatting"
                            or "dataValidations" or "mergeCells" or "pageSetup" or "drawing" or "legacyDrawing":
                            r.Skip();
                            more = !r.EOF;
                            continue;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "c" && inCell)
                    {
                        inCell = false;
                        string text = CellText(type, value.ToString(), inline, shared);
                        if (text.Length > 0)
                        {
                            if (rowHasCells) sink.Text("\t");
                            sink.Text(text);
                            rowHasCells = true;
                        }
                    }
                    else if (r.LocalName == "row" && rowHasCells)
                    {
                        sink.LineBreak();
                        rowHasCells = false;
                    }
                }
                more = r.Read();
            }
        }

        static string CellText(string? type, string value, bool inline, SharedStrings shared)
        {
            if (inline) return OfficeXml.DecodeEscapes(value);
            switch (type)
            {
                case "s":
                    return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i) ? shared.Get(i) : "";
                case "b":
                    return value.Trim() == "1" ? "TRUE" : value.Trim() == "0" ? "FALSE" : "";
                case "str" or "inlineStr":
                    return OfficeXml.DecodeEscapes(value);
                default:
                    return value.Trim();
            }
        }

        // ------------------------------------------------------------------ PowerPoint

        void PowerPoint(string main)
        {
            var rels = Relationships(main);
            var ids = new List<string>();
            Part(main, r =>
            {
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sldId" && RelationshipId(r) is { } id)
                    {
                        ids.Add(id);
                        if (ids.Count >= 10_000) return;
                    }
            });
            int slides = 0;
            foreach (var id in ids)
            {
                if (sink.Full) break;
                if (!rels.TryGetValue(id, out var rel) || zip.Entry(rel.Target) == null) continue;
                if (slides > 0) sink.PageBreak();
                slides++;
                Part(rel.Target, WalkText);
                foreach (var notes in Targets(Relationships(rel.Target), "notesSlide"))
                {
                    sink.LineBreak();
                    Part(notes, WalkText);
                }
            }
            sink.Pages = slides;
        }

        // ------------------------------------------------------------------ Properties

        void CoreProperties(XmlReader r)
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.Depth != 1) continue;
                DocField? field = r.LocalName switch
                {
                    "title" => DocField.Title, "subject" => DocField.Subject, "creator" => DocField.Author, "keywords" => DocField.Keywords,
                    "description" => DocField.Comment, _ => null,
                };
                if (field is not { } f) continue;
                string text = OfficeXml.ElementText(r, ExtractSink.MaxKeywordChars);
                if (f == DocField.Keywords) sink.Keywords(text);
                else sink.Field(f, text);
            }
        }

        void AppProperties(XmlReader r)
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.Depth != 1 || r.LocalName is not ("Pages" or "Slides")) continue;
                if (int.TryParse(OfficeXml.ElementText(r, 16), out int pages) && pages > 0 && sink.Pages == 0) sink.Pages = pages;
            }
        }

        // ------------------------------------------------------------------ Parts and relationships

        /// <summary>
        /// Walks one part; a damaged part, a part past its decode cap or one that refuses a DTD is skipped and the
        /// document marked partial. Document-level limits (time, bytes, text) go on up.
        /// </summary>
        void Part(string name, Action<XmlReader> walk)
        {
            var entry = zip.Entry(name);
            if (entry == null) return;
            budget.Check();
            var stream = OfficeZip.Open(entry, budget);
            if (stream == null) { Partial = true; return; }
            try
            {
                using var r = OfficeXml.Create(stream);
                walk(r);
            }
            catch (ContentLimitException e) when (e.Limit == ContentLimitException.Stream) { Partial = true; }
            catch (Exception e) when (e is XmlException or InvalidDataException or DecoderFallbackException or InvalidOperationException
                || (e is IOException && e is not ContentIoException))
            {
                IoErrors.ThrowIfWrapped(e); // a file I/O error ZipArchive wrapped: not the part's fault
                Partial = true;
            }
            finally { stream.Dispose(); }
        }

        string? RootElement(string part)
        {
            string? local = null;
            Part(part, r =>
            {
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element) { local = r.LocalName; return; }
            });
            return local;
        }

        /// <summary>A part's relationships by id (external targets left out).</summary>
        Dictionary<string, (string Type, string Target)> Relationships(string part)
        {
            int slash = part.LastIndexOf('/');
            string rels = slash < 0 ? $"_rels/{part}.rels" : $"{part[..slash]}/_rels/{part[(slash + 1)..]}.rels";
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            Part(rels, r =>
            {
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                    if (string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                    string? id = r.GetAttribute("Id"), type = r.GetAttribute("Type"), target = r.GetAttribute("Target");
                    if (id == null || type == null || target == null) continue;
                    map.TryAdd(id, (TypeName(type), OfficeZip.Resolve(part, target)));
                    if (map.Count >= 20_000) return;
                }
            });
            return map;
        }

        /// <summary>The last segment of a relationship type ("…/relationships/officeDocument" → "officeDocument").</summary>
        static string TypeName(string type) => type[(type.LastIndexOf('/') + 1)..];

        static string? Target(Dictionary<string, (string Type, string Target)> rels, string type)
        {
            foreach (var (t, target) in rels.Values) if (t == type) return target;
            return null;
        }

        static IEnumerable<string> Targets(Dictionary<string, (string Type, string Target)> rels, string type)
        {
            // Stable order: by target name (header1, header2 …).
            var list = new List<string>();
            foreach (var (t, target) in rels.Values) if (t == type && !list.Contains(target)) list.Add(target);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>r:id of the current element in either relationships namespace (Transitional or Strict).</summary>
        static string? RelationshipId(XmlReader r)
        {
            if (!r.MoveToFirstAttribute()) return null;
            string? id = null;
            do
            {
                if (r.LocalName == "id" && r.NamespaceURI.EndsWith("relationships", StringComparison.Ordinal)) { id = r.Value; break; }
            }
            while (r.MoveToNextAttribute());
            r.MoveToElement();
            return id;
        }
    }

    /// <summary>A workbook's shared strings in one character buffer with offsets (no object per string), capped.</summary>
    internal sealed class SharedStrings(int maxChars)
    {
        const int MaxStrings = 1 << 20;
        readonly StringBuilder _chars = new();
        readonly List<int> _starts = [];
        readonly List<int> _lengths = [];

        public int Count => _starts.Count;

        public void Add(string s)
        {
            if (_starts.Count >= MaxStrings) return;
            int take = Math.Max(0, Math.Min(s.Length, maxChars - _chars.Length));
            _starts.Add(_chars.Length);
            _lengths.Add(take);
            _chars.Append(s, 0, take);
        }

        public string Get(int i) => i >= 0 && i < _starts.Count ? _chars.ToString(_starts[i], _lengths[i]) : "";
    }
}
