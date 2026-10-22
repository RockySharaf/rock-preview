using System.Text;
using System.Xml;

namespace Anything.Core.Content.Extract;

/// <summary>
/// InDesign markup (IDML): story text and the document's XMP. Stories are read in designmap.xml order (else by name):
/// Content elements are text, Br ends a line, table cells and stories end lines; deleted tracked changes are skipped.
/// META-INF/metadata.xml is the document's XMP (title, keywords, fonts, plates, pages).
/// </summary>
internal sealed class IdmlReader : IContentReader
{
    public ContentKind Kind => ContentKind.Design;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "idml";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        var refused = OfficeZip.TryOpen(source, out var opened);
        if (refused != null || opened == null) return refused ?? DocStatus.Damaged;
        using var zip = opened;
        bool partial = false;
        if (zip.Entry("META-INF/metadata.xml") is { } metadata)
        {
            var bytes = ReadEntry(metadata, budget, ref partial);
            if (bytes != null) Xmp.Read(new ReadOnlyMemory<byte>(bytes), sink);
        }
        var stories = new List<string>();
        if (zip.Entry("designmap.xml") is { } map)
            Walk(map, budget, ref partial, r =>
            {
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Story" && r.GetAttribute("src") is { Length: > 0 } src && stories.Count < 10_000)
                        stories.Add(OfficeZip.Resolve("designmap.xml", src));
            });
        if (stories.Count == 0)
        {
            foreach (var e in zip.Entries)
                if (e.FullName.StartsWith("Stories/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    stories.Add(e.FullName);
            stories.Sort(StringComparer.OrdinalIgnoreCase);
        }
        if (stories.Count == 0 && sink.FieldCount == 0) throw new InvalidDataException("No stories.");
        var chunk = new char[4096];
        bool first = true;
        foreach (var name in stories)
        {
            if (sink.Full) break;
            if (zip.Entry(name) is not { } story) continue;
            if (!first) sink.LineBreak();
            first = false;
            Walk(story, budget, ref partial, r => Story(r, sink, budget, chunk));
        }
        return partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    static void Story(XmlReader r, ExtractSink sink, ExtractBudget budget, char[] chunk)
    {
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
                    case "Properties":
                        r.Skip();
                        more = !r.EOF;
                        continue;
                    case "Change" when r.GetAttribute("ChangeType") == "DeletedText":
                        r.Skip();
                        more = !r.EOF;
                        continue;
                    case "Content":
                        OfficeXml.ReadText(r, chunk, s => sink.Text(s));
                        break;
                    case "Br":
                        sink.LineBreak();
                        break;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "Cell") sink.LineBreak();
            more = r.Read();
        }
    }

    delegate void Walker(XmlReader r);

    static void Walk(System.IO.Compression.ZipArchiveEntry entry, ExtractBudget budget, ref bool partial, Walker walk)
    {
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
            IoErrors.ThrowIfWrapped(e); // a file I/O error ZipArchive wrapped: not the story's fault
            partial = true;
        }
        finally { stream.Dispose(); }
    }

    /// <summary>
    /// An XML entry's bytes (the XMP packet), checked by <see cref="XmlShape"/> as they inflate: a packet nested too deep
    /// or with a giant tag stops at once and is skipped (partial), before XmlReader could spend its memory on it.
    /// </summary>
    static byte[]? ReadEntry(System.IO.Compression.ZipArchiveEntry entry, ExtractBudget budget, ref bool partial)
    {
        var stream = OfficeZip.Open(entry, budget);
        if (stream == null) { partial = true; return null; }
        try
        {
            using var ms = new MemoryStream((int)Math.Clamp(entry.Length, 0, Math.Min(budget.Limits.MaxStreamBytes, 1 << 20)));
            using var checkedXml = new XmlShapeStream(stream);
            checkedXml.CopyTo(ms);
            return ms.ToArray();
        }
        catch (ContentLimitException e) when (e.Limit == ContentLimitException.Stream) { partial = true; return null; }
        catch (Exception e) when (e is InvalidDataException || (e is IOException && e is not ContentIoException))
        {
            IoErrors.ThrowIfWrapped(e);
            partial = true;
            return null;
        }
        finally { stream.Dispose(); }
    }
}
