using System.Text;
using System.Xml;
using System.Xml.Linq;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// The preview's Office renderers (OfficePreview.LoadPart) load parts into XDocument trees through OfficeXml.LoadTree: a
/// part nested deeper than the guard allows is refused before any tree walk (XElement.Value recurses once per level and
/// would overflow the stack), while real parts load as XDocument.Load loaded them (whitespace between elements left out,
/// and no cap on the part's size).
/// </summary>
public class PreviewXmlGuardTests
{
    static XDocument Load(string xml) => OfficeXml.LoadTree(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void A_part_nested_fifty_thousand_deep_is_refused_before_it_is_walked()
    {
        var sb = new StringBuilder("<w:document xmlns:w=\"urn:w\"><w:p><w:r><w:t>");
        for (int i = 0; i < 50_000; i++) sb.Append("<w:t>");
        sb.Append('x');
        for (int i = 0; i < 50_000; i++) sb.Append("</w:t>");
        sb.Append("</w:t></w:r></w:p></w:document>");
        Assert.Throws<InvalidDataException>(() => Load(sb.ToString()));
    }

    [Fact]
    public void A_part_at_the_depth_limit_still_loads()
    {
        int depth = OfficeXml.MaxDepth - 2;
        var xml = string.Concat(Enumerable.Repeat("<a>", depth)) + "deep" + string.Concat(Enumerable.Repeat("</a>", depth));
        Assert.Equal("deep", Load(xml).Root!.Value);
    }

    [Fact]
    public void Whitespace_between_elements_is_left_out_as_XDocument_Load_left_it()
    {
        const string xml = "<r xmlns:w=\"urn:w\">\n  <w:p>\n    <w:t>One</w:t>\n    <w:t xml:space=\"preserve\"> two </w:t>\n  </w:p>\n</r>";
        var guarded = Load(xml);
        var plain = XDocument.Parse(xml, LoadOptions.None);
        Assert.Equal(plain.Root!.Value, guarded.Root!.Value);
        Assert.Equal("One two ", guarded.Root.Value);
        Assert.Equal(plain.ToString(), guarded.ToString());
    }

    [Fact]
    public void A_part_past_the_indexers_64M_characters_still_loads_for_the_preview()
    {
        // Shared strings of a large data export: one big string past 64M characters (the indexer's cap) in a valid part.
        const int chars = (64 << 20) + 1024;
        var head = Encoding.UTF8.GetBytes("<sst xmlns=\"urn:s\"><si><t>");
        var tail = Encoding.UTF8.GetBytes("</t></si><si><t>last</t></si></sst>");
        var bytes = new byte[head.Length + chars + tail.Length];
        head.CopyTo(bytes, 0);
        bytes.AsSpan(head.Length, chars).Fill((byte)'x');
        tail.CopyTo(bytes, head.Length + chars);

        var doc = OfficeXml.LoadTree(new MemoryStream(bytes));
        XNamespace s = "urn:s";
        var items = doc.Root!.Elements(s + "si").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(chars, items[0].Value.Length);
        Assert.Equal("last", items[1].Value);
        // The indexer's reader keeps its cap.
        Assert.Throws<XmlException>(() =>
        {
            using var r = OfficeXml.Create(new MemoryStream(bytes));
            while (r.Read()) { }
        });
    }
}
