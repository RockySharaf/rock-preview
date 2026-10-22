using System.IO.Compression;
using System.Text;

namespace Anything.Tests.Content.Readers;

/// <summary>Writes small but real Office, OpenDocument and IDML packages with System.IO.Compression (stored entries on request, for fuzzing).</summary>
internal static class OfficeFixtures
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string Core = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

    public static byte[] Zip(bool stored, params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }

    static string Relationships(params (string Id, string Type, string Target)[] rels)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{Rels}\">");
        foreach (var (id, type, target) in rels)
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{R}/{type}\" Target=\"{target}\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    const string PackageRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{0}\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
        "</Relationships>";

    static string CoreXml(string title) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\">" +
        $"<dc:title>{title}</dc:title><dc:subject>Budget planning</dc:subject><dc:creator>Rocky Writer</dc:creator>" +
        "<cp:keywords>finance; quarterly, forecast</cp:keywords><dc:description>Internal draft</dc:description></cp:coreProperties>";

    static string AppXml(string element, int value) =>
        $"<?xml version=\"1.0\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><{element}>{value}</{element}></Properties>";

    /// <summary>A Word document exercising the rules: preserved spaces, tabs, breaks, deleted text, field codes, text box fallback, tab stops.</summary>
    public static byte[] Docx(bool stored = false, string? body = null) => Zip(stored,
        ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("_rels/.rels", string.Format(PackageRels, "word/document.xml")),
        ("docProps/core.xml", CoreXml("Quarterly Report")),
        ("docProps/app.xml", AppXml("Pages", 7)),
        ("word/_rels/document.xml.rels", Relationships(("rId1", "header", "header1.xml"), ("rId2", "footer", "footer1.xml"),
            ("rId3", "footnotes", "footnotes.xml"), ("rId4", "comments", "comments.xml"), ("rId5", "hyperlink", "http://example.com"))),
        ("word/document.xml", body ?? DocxBody()),
        ("word/header1.xml", $"<w:hdr xmlns:w=\"{W}\"><w:p><w:r><w:t>Confidential header</w:t></w:r></w:p></w:hdr>"),
        ("word/footer1.xml", $"<w:ftr xmlns:w=\"{W}\"><w:p><w:r><w:t>Footer line</w:t></w:r></w:p></w:ftr>"),
        ("word/footnotes.xml", $"<w:footnotes xmlns:w=\"{W}\"><w:footnote w:id=\"1\"><w:p><w:r><w:t>Footnote source</w:t></w:r></w:p></w:footnote></w:footnotes>"),
        ("word/comments.xml", $"<w:comments xmlns:w=\"{W}\"><w:comment w:id=\"0\"><w:p><w:r><w:t>Reviewer remark</w:t></w:r></w:p></w:comment></w:comments>"));

    public static string DocxBody() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{W}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>" +
        "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Invoice</w:t></w:r>" +
        "<w:r><w:t xml:space=\"preserve\"> </w:t></w:r><w:r><w:t>INV-2024-001</w:t></w:r><w:r><w:tab/><w:t>total</w:t></w:r></w:p>" +
        "<w:p><w:r><w:t>first</w:t><w:br/><w:t>second</w:t></w:r><w:del><w:r><w:delText>removed words</w:delText></w:r></w:del></w:p>" +
        "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>HYPERLINK \"http://fieldcode.example\"</w:instrText></w:r>" +
        "<w:r><w:t>shown link</w:t></w:r></w:p>" +
        "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wps:txbx><w:txbxContent><w:p><w:r><w:t>boxed text</w:t></w:r></w:p></w:txbxContent></wps:txbx></w:drawing></mc:Choice>" +
        "<mc:Fallback><w:pict><v:textbox><w:txbxContent><w:p><w:r><w:t>boxed text</w:t></w:r></w:p></w:txbxContent></v:textbox></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>" +
        "<w:p><w:r><w:t><![CDATA[cdata words]]></w:t></w:r></w:p>" +
        "<w:sectPr><w:pgSz w:w=\"12240\"/></w:sectPr></w:body></w:document>";

    /// <summary>A workbook whose first sheet in workbook order is sheet2.xml, with shared strings (escapes, phonetic runs, rich runs) and every cell type.</summary>
    public static byte[] Xlsx(bool stored = false, string? secondSheet = null) => Zip(stored,
        ("_rels/.rels", string.Format(PackageRels, "xl/workbook.xml")),
        ("docProps/core.xml", CoreXml("Budget Book")),
        ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"" + R + "\"><sheets>" +
            "<sheet name=\"Budget 2024\" sheetId=\"1\" r:id=\"rId2\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId1\" state=\"hidden\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", Relationships(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "worksheet", "worksheets/sheet2.xml"),
            ("rId3", "sharedStrings", "sharedStrings.xml"))),
        ("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"4\" uniqueCount=\"4\">" +
            "<si><t>Revenue</t></si>" +
            "<si><t>line_x000D_break</t></si>" +
            "<si><r><t>東京</t></r><rPh sb=\"0\" eb=\"2\"><t>トウキョウ</t></rPh><phoneticPr fontId=\"1\"/></si>" +
            "<si><r><rPr><b/></rPr><t xml:space=\"preserve\">rich </t></r><r><t>run</t></r></si></sst>"),
        ("xl/worksheets/sheet2.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>1250.5</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>inline words</t></is></c><c r=\"B2\" t=\"str\"><f>CONCAT(\"form\",\"ula\")</f><v>formula result</v></c>" +
            "<c r=\"C2\" t=\"b\"><v>1</v></c><c r=\"D2\" t=\"s\"><v>1</v></c><c r=\"E2\" t=\"s\"><v>3</v></c></row></sheetData>" +
            "<headerFooter><oddHeader>&amp;CHeader code</oddHeader></headerFooter></worksheet>"),
        ("xl/worksheets/sheet1.xml", secondSheet ?? "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>hidden sheet words</t></is></c></row></sheetData></worksheet>"));

    /// <summary>A presentation whose sldIdLst order is slide2.xml then slide1.xml, the first with notes.</summary>
    public static byte[] Pptx(bool stored = false) => Zip(stored,
        ("_rels/.rels", string.Format(PackageRels, "ppt/presentation.xml")),
        ("docProps/core.xml", CoreXml("Pitch Deck")),
        ("docProps/app.xml", AppXml("Slides", 2)),
        ("ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"" + R + "\">" +
            "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId3\"/><p:sldId id=\"257\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels", Relationships(("rId2", "slide", "slides/slide1.xml"), ("rId3", "slide", "slides/slide2.xml"))),
        ("ppt/slides/slide2.xml", Slide("Opening slide title", "agenda item")),
        ("ppt/slides/_rels/slide2.xml.rels", Relationships(("rId1", "notesSlide", "../notesSlides/notesSlide1.xml"))),
        ("ppt/notesSlides/notesSlide1.xml", Slide("speaker notes here", "")),
        ("ppt/slides/slide1.xml", Slide("Closing thanks", "contact details")));

    static string Slide(string title, string bullet) =>
        "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
        "<p:cSld><p:spTree><p:sp><p:txBody><a:bodyPr/><a:p><a:pPr><a:tabLst><a:tab pos=\"100\"/></a:tabLst></a:pPr><a:r><a:t>" + title + "</a:t></a:r></a:p>" +
        "<a:p><a:r><a:t>" + bullet + "</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";

    const string Odf = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
        "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\"";

    static string Meta(string title, string stat) =>
        $"<office:document-meta {Odf}><office:meta><dc:title>{title}</dc:title><dc:description>Odf comment</dc:description>" +
        "<meta:initial-creator>First Author</meta:initial-creator><dc:creator>Last Editor</dc:creator><meta:keyword>alpha</meta:keyword>" +
        $"<meta:keyword>beta</meta:keyword><meta:document-statistic {stat}/></office:meta></office:document-meta>";

    static byte[] OdfPackage(bool stored, string content, string meta, bool encrypted = false) => Zip(stored,
        ("mimetype", "application/vnd.oasis.opendocument.text"),
        ("META-INF/manifest.xml", "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
            "<manifest:file-entry manifest:full-path=\"content.xml\">" + (encrypted ? "<manifest:encryption-data manifest:checksum-type=\"SHA1\"/>" : "") +
            "</manifest:file-entry></manifest:manifest>"),
        ("meta.xml", meta),
        ("content.xml", content));

    public static byte[] Odt(bool stored = false, bool encrypted = false) => OdfPackage(stored,
        $"<office:document-content {Odf}><office:body><office:text>" +
        "<text:h text:outline-level=\"1\">Project Plan</text:h>" +
        "<text:p>Words<text:s text:c=\"2147483647\"/>apart<text:tab/>tabbed<text:line-break/>next line <text:span>spanned</text:span></text:p>" +
        "<text:p>See note<text:note><text:note-citation>1</text:note-citation><text:note-body><text:p>note body text</text:p></text:note-body></text:note></text:p>" +
        "<text:tracked-changes><text:changed-region><text:deletion><text:p>deleted words</text:p></text:deletion></text:changed-region></text:tracked-changes>" +
        "</office:text></office:body></office:document-content>",
        Meta("Plan Title", "meta:page-count=\"3\""), encrypted);

    /// <summary>A spreadsheet whose first sheet ends with LibreOffice's huge repeats; the second sheet must still be read.</summary>
    public static byte[] Ods(bool stored = false) => OdfPackage(stored,
        $"<office:document-content {Odf}><office:body><office:spreadsheet>" +
        "<table:table table:name=\"Sales\"><table:table-row><table:table-cell><text:p>Region</text:p></table:table-cell>" +
        "<table:table-cell table:number-columns-repeated=\"3\"><text:p>same</text:p></table:table-cell>" +
        "<table:table-cell table:number-columns-repeated=\"1020\"/></table:table-row>" +
        "<table:table-row table:number-rows-repeated=\"1048553\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row></table:table>" +
        "<table:table table:name=\"Second\"><table:table-row><table:table-cell><text:p>second sheet text</text:p></table:table-cell></table:table-row></table:table>" +
        "</office:spreadsheet></office:body></office:document-content>",
        Meta("Sales Sheet", "meta:table-count=\"2\""));

    public static byte[] Odp(bool stored = false) => OdfPackage(stored,
        $"<office:document-content {Odf}><office:body><office:presentation>" +
        "<draw:page draw:name=\"p1\"><draw:frame><draw:text-box><text:p>First page words</text:p></draw:text-box></draw:frame>" +
        "<presentation:notes><draw:frame><draw:text-box><text:p>odp speaker notes</text:p></draw:text-box></draw:frame></presentation:notes></draw:page>" +
        "<draw:page draw:name=\"p2\"><draw:frame><draw:text-box><text:p>Second page words</text:p></draw:text-box></draw:frame></draw:page>" +
        "</office:presentation></office:body></office:document-content>",
        Meta("Slides Title", "meta:object-count=\"4\""));

    public static string XmpPacket(string title, string format, params string[] keywords)
    {
        var bag = string.Concat(keywords.Select(k => $"<rdf:li>{k}</rdf:li>"));
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmpTPg=\"http://ns.adobe.com/xap/1.0/t/pg/\">" +
            $"<dc:format>{format}</dc:format><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>" +
            $"<dc:subject><rdf:Bag>{bag}</rdf:Bag></dc:subject><xmpTPg:NPages>12</xmpTPg:NPages></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    public static byte[] Idml(bool stored = false) => Zip(stored,
        ("mimetype", "application/vnd.adobe.indesign-idml-package"),
        ("designmap.xml", "<?xml version=\"1.0\"?><Document xmlns:idPkg=\"http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging\">" +
            "<idPkg:Story src=\"Stories/Story_u2.xml\"/><idPkg:Story src=\"Stories/Story_u1.xml\"/></Document>"),
        ("META-INF/metadata.xml", XmpPacket("Brochure Layout", "application/x-indesign", "brochure", "client")),
        ("Stories/Story_u1.xml", "<idPkg:Story xmlns:idPkg=\"http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging\"><Story Self=\"u1\">" +
            "<ParagraphStyleRange><CharacterStyleRange><Properties><Leading type=\"unit\">12</Leading></Properties><Content>Second story</Content><Br/>" +
            "<Content>more text</Content></CharacterStyleRange></ParagraphStyleRange>" +
            "<Change ChangeType=\"DeletedText\"><Content>gone words</Content></Change></Story></idPkg:Story>"),
        ("Stories/Story_u2.xml", "<idPkg:Story xmlns:idPkg=\"http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging\"><Story Self=\"u2\">" +
            "<ParagraphStyleRange><CharacterStyleRange><Content>Headline story</Content></CharacterStyleRange></ParagraphStyleRange></Story></idPkg:Story>"));

    /// <summary>A zip whose central directory claims many tiny entries (a crafted directory); built by hand.</summary>
    public static byte[] CraftedDirectory(int entries)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        long cdStart = ms.Position;
        for (int i = 0; i < entries; i++)
        {
            w.Write(0x02014B50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u);
            w.Write((byte)'a');
        }
        long cdSize = ms.Position - cdStart;
        w.Write(0x06054B50u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)Math.Min(entries, 0xFFFF)); w.Write((ushort)Math.Min(entries, 0xFFFF));
        w.Write((uint)cdSize); w.Write((uint)cdStart); w.Write((ushort)0);
        return ms.ToArray();
    }
}
