using System.Text;
using System.Xml;

namespace Anything.Core.Content.Extract;

/// <summary>
/// OpenDocument text, spreadsheets and presentations: content.xml and meta.xml. Paragraphs and headings end lines,
/// text:s is one space whatever its count (text:c is never trusted), text:tab a tab, text:line-break a line end. Each
/// spreadsheet table is a page starting with its name, cells with text separated by tabs; repeated rows and cells are
/// never expanded (a repeat of 1,048,553 empty rows writes nothing), so later sheets are still read. Each presentation
/// or drawing page is a page, its notes included. Tracked deletions, note citations and embedded binaries are skipped.
/// A manifest with encryption data is PasswordProtected.
/// </summary>
internal sealed class OdfReader : IContentReader
{
    const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    const string DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    const string MetaNs = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    const int MaxCellChars = 64 << 10;

    public ContentKind Kind => ContentKind.Office;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "odt" or "ott" or "ods" or "ots" or "odp" or "otp";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        var refused = OfficeZip.TryOpen(source, out var opened);
        if (refused != null || opened == null) return refused ?? DocStatus.Damaged;
        using var zip = opened;
        bool partial = false;
        bool encrypted = false;
        Part(zip, "META-INF/manifest.xml", budget, ref partial, r =>
        {
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "encryption-data") { encrypted = true; return; }
        });
        if (encrypted) return DocStatus.PasswordProtected;
        if (zip.Entry("content.xml") == null) throw new InvalidDataException("No content.xml.");
        int statisticPages = 0;
        Part(zip, "meta.xml", budget, ref partial, r => Meta(r, sink, ref statisticPages));
        int counted = 0;
        Part(zip, "content.xml", budget, ref partial, r => counted = Content(r, sink, budget));
        sink.Pages = counted > 0 ? counted : statisticPages;
        return partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    delegate void Walker(XmlReader r);

    static void Part(OfficeZip zip, string name, ExtractBudget budget, ref bool partial, Walker walk)
    {
        var entry = zip.Entry(name);
        if (entry == null) return;
        budget.Check();
        var stream = OfficeZip.Open(entry, budget);
        if (stream == null) { partial = true; return; }
        try
        {
            using var r = OfficeXml.Create(stream);
            walk(r);
        }
        catch (ContentLimitException e) when (e.Limit == ContentLimitException.Stream) { partial = true; }
        catch (Exception e) when (e is XmlException or InvalidDataException or DecoderFallbackException or InvalidOperationException
            || (e is IOException && e is not ContentIoException))
        {
            IoErrors.ThrowIfWrapped(e); // a file I/O error ZipArchive wrapped: not the part's fault
            partial = true;
        }
        finally { stream.Dispose(); }
    }

    static void Meta(XmlReader r, ExtractSink sink, ref int pages)
    {
        string? creator = null, initialCreator = null;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            switch (r.LocalName)
            {
                case "title": sink.Field(DocField.Title, OfficeXml.ElementText(r, ExtractSink.MaxFieldChars)); break;
                case "subject": sink.Field(DocField.Subject, OfficeXml.ElementText(r, ExtractSink.MaxFieldChars)); break;
                case "description": sink.Field(DocField.Comment, OfficeXml.ElementText(r, ExtractSink.MaxFieldChars)); break;
                case "creator": creator ??= OfficeXml.ElementText(r, ExtractSink.MaxFieldChars); break;
                case "initial-creator": initialCreator ??= OfficeXml.ElementText(r, ExtractSink.MaxFieldChars); break;
                case "keyword": sink.Field(DocField.Keywords, OfficeXml.ElementText(r, ExtractSink.MaxFieldChars)); break;
                case "document-statistic":
                    if (int.TryParse(r.GetAttribute("page-count", MetaNs), out int p) && p > 0) pages = p;
                    else if (int.TryParse(r.GetAttribute("table-count", MetaNs), out int t) && t > 0 && pages == 0) pages = t;
                    break;
            }
        }
        if ((initialCreator is { Length: > 0 } ? initialCreator : creator) is { Length: > 0 } author) sink.Field(DocField.Author, author);
    }

    /// <summary>Walks content.xml; returns the sheets or pages it counted (0 for text documents).</summary>
    static int Content(XmlReader r, ExtractSink sink, ExtractBudget budget)
    {
        var chunk = new char[4096];
        var cell = new StringBuilder();
        string body = "";
        int sheetDepth = -1, rowDepth = -1, cellDepth = -1, paragraphs = 0, pages = 0, sheets = 0, nodes = 0;
        bool rowHasCells = false;
        void Out(ReadOnlySpan<char> s)
        {
            if (cellDepth >= 0) { if (cell.Length < MaxCellChars) cell.Append(s[..Math.Min(s.Length, MaxCellChars - cell.Length)]); }
            else sink.Text(s);
        }
        bool more = r.Read();
        while (more && !sink.Full)
        {
            if ((++nodes & 1023) == 0) budget.Check();
            if (r.Depth > OfficeXml.MaxDepth) throw new InvalidDataException("Too deep.");
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                {
                    string ns = r.NamespaceURI, local = r.LocalName;
                    if (Skipped(ns, local)) { r.Skip(); more = !r.EOF; continue; }
                    bool empty = r.IsEmptyElement;
                    if (ns == OfficeNs && local is "text" or "spreadsheet" or "presentation" or "drawing" && body.Length == 0) body = local;
                    else if (ns == TableNs && local == "table" && body == "spreadsheet" && sheetDepth < 0)
                    {
                        sheetDepth = r.Depth;
                        sheets++;
                        sink.StartSheet(r.GetAttribute("name", TableNs) ?? "");
                        if (empty) sheetDepth = -1;
                    }
                    else if (ns == TableNs && local == "table-row" && sheetDepth >= 0 && cellDepth < 0 && !empty)
                    {
                        rowDepth = r.Depth;
                        rowHasCells = false;
                    }
                    else if (ns == TableNs && local is "table-cell" or "covered-table-cell" && rowDepth >= 0 && cellDepth < 0 && !empty)
                    {
                        cellDepth = r.Depth;
                        cell.Clear();
                    }
                    else if (ns == DrawNs && local == "page" && body is "presentation" or "drawing")
                    {
                        if (pages > 0) sink.PageBreak();
                        pages++;
                    }
                    else if (ns == TextNs)
                    {
                        switch (local)
                        {
                            case "p" or "h": if (!empty) paragraphs++; break;
                            case "s": Out(" "); break; // text:c is never expanded: one space separates words
                            case "tab": Out("\t"); break;
                            case "line-break": if (cellDepth >= 0) Out(" "); else sink.LineBreak(); break;
                        }
                    }
                    break;
                }
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    if (paragraphs > 0)
                    {
                        if (!r.CanReadValueChunk) Out(r.Value);
                        else
                        {
                            int n;
                            while ((n = r.ReadValueChunk(chunk, 0, chunk.Length)) > 0) Out(chunk.AsSpan(0, n));
                        }
                    }
                    break;
                case XmlNodeType.EndElement:
                {
                    string ns = r.NamespaceURI, local = r.LocalName;
                    if (ns == TextNs && local is "p" or "h" && paragraphs > 0)
                    {
                        paragraphs--;
                        if (cellDepth >= 0) Out(" ");
                        else sink.LineBreak();
                    }
                    else if (ns == TableNs && r.Depth == cellDepth)
                    {
                        cellDepth = -1;
                        string text = cell.ToString().Trim();
                        if (text.Length > 0)
                        {
                            if (rowHasCells) sink.Text("\t");
                            sink.Text(text);
                            rowHasCells = true;
                        }
                    }
                    else if (ns == TableNs && r.Depth == rowDepth)
                    {
                        rowDepth = -1;
                        if (rowHasCells) sink.LineBreak();
                    }
                    else if (ns == TableNs && r.Depth == sheetDepth) sheetDepth = -1;
                    break;
                }
            }
            more = r.Read();
        }
        return sheets > 0 ? sheets : pages;
    }

    /// <summary>Subtrees with no document text, or with text that isn't there any more (tracked deletions).</summary>
    static bool Skipped(string ns, string local) => ns switch
    {
        TextNs => local is "note-citation" or "tracked-changes" or "deletion" or "sequence-decls" or "variable-decls" or "user-field-decls",
        OfficeNs => local is "binary-data" or "forms" or "scripts" or "font-face-decls" or "automatic-styles" or "styles" or "event-listeners" or "settings",
        _ => false,
    };
}
