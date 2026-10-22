using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// InDesign and EPS: the document's XMP packet (and DSC comments for EPS). The packet is looked for in the last 1 MB
/// first, then the first 8 MB (EPS follows its %ADO_ContainsXMP: MainFirst / MainLast hint); packets of placed pictures
/// (a dc:format that isn't the container's) are passed over, so a stock photo's keywords don't become the layout's. EPS
/// also gives %%Title and %%Creator; a DOS EPS binary header points at its PostScript section.
/// </summary>
internal sealed class XmpScanReader : IContentReader
{
    const long Head = 8 << 20, Tail = 1 << 20, Window = 1 << 20, MaxPacket = 16 << 20;
    const int MaxPackets = 16;

    public ContentKind Kind => ContentKind.Design;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "indd" or "eps";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        bool eps = extension is "eps";
        long start = 0, length = source.Length;
        bool? mainFirst = null;
        if (eps)
        {
            // DOS EPS: C5 D0 D3 C6, then the PostScript section's offset and length.
            var dos = source.ReadBytes(0, Math.Min(12, source.Length), 12);
            if (dos is { Length: 12 } && dos[0] == 0xC5 && dos[1] == 0xD0 && dos[2] == 0xD3 && dos[3] == 0xC6)
            {
                start = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(4));
                length = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(8));
                if (start > source.Length || length > source.Length - start) throw new InvalidDataException("Bad DOS EPS header.");
            }
            mainFirst = Comments(new ByteSourceSlice(source, start, length), sink);
        }
        var section = new ByteSourceSlice(source, start, length);
        string[] formats = eps ? ["application/postscript", "image/eps", "application/eps", "image/x-eps"] : ["application/x-indesign"];
        long tailFrom = Math.Max(0, section.Length - Tail), headTo = Math.Min(section.Length, Head);
        var order = mainFirst == true ? new[] { (0L, headTo), (tailFrom, section.Length) } : [(tailFrom, section.Length), (0L, headTo)];
        byte[]? fallback = null;
        int seen = 0;
        foreach (var (from, to) in order)
        {
            foreach (var packet in Packets(section, from, to, budget))
            {
                if (++seen > MaxPackets) break;
                if (!XmlShape.Allows(packet)) continue; // nested too deep or a tag past its caps: XmlReader would spend hundreds of MB
                string? format = Xmp.Format(new ReadOnlyMemory<byte>(packet));
                if (format == null || formats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    Xmp.Read(new ReadOnlyMemory<byte>(packet), sink);
                    return DocStatus.Ok;
                }
                fallback ??= packet;
            }
            if (seen > MaxPackets) break;
        }
        if (fallback != null) Xmp.Read(new ReadOnlyMemory<byte>(fallback), sink);
        return DocStatus.Ok;
    }

    /// <summary>The DSC header comments up to %%EndComments: Title, Creator; returns the %ADO_ContainsXMP hint (true = MainFirst).</summary>
    static bool? Comments(IByteSource ps, ExtractSink sink)
    {
        var head = ps.ReadBytes(0, Math.Min(ps.Length, 64 << 10), 64 << 10);
        if (head == null) return null;
        var text = Encoding.Latin1.GetString(head);
        if (!text.StartsWith("%!PS", StringComparison.Ordinal)) return null;
        bool? hint = null;
        foreach (var raw in text.Split(['\r', '\n']))
        {
            if (raw.StartsWith("%%EndComments", StringComparison.Ordinal)) break;
            if (Value(raw, "%%Title:") is { } title) sink.Field(DocField.Title, DscText(title));
            else if (Value(raw, "%%Creator:") is { } creator) sink.Field(DocField.CreatorTool, DscText(creator));
            else if (Value(raw, "%ADO_ContainsXMP:") is { } xmp)
                hint = xmp.Contains("MainFirst", StringComparison.OrdinalIgnoreCase) ? true
                    : xmp.Contains("MainLast", StringComparison.OrdinalIgnoreCase) ? false : hint;
        }
        return hint;
    }

    static string? Value(string line, string key) => line.StartsWith(key, StringComparison.Ordinal) ? line[key.Length..].Trim() : null;

    /// <summary>A DSC value: a PostScript string "(…)" unwrapped, otherwise as written; the PC's code page for 8-bit text.</summary>
    static string DscText(string value)
    {
        if (value.Length >= 2 && value[0] == '(' && value[^1] == ')') value = value[1..^1];
        var bytes = Encoding.Latin1.GetBytes(value);
        return PictureValues.Text(bytes);
    }

    /// <summary>The packets that start in [from, to), in order; each read whole (its end tag may lie past the window), at most 16 MB.</summary>
    static IEnumerable<byte[]> Packets(IByteSource s, long from, long to, ExtractBudget budget)
    {
        long pos = from;
        while (pos < to)
        {
            budget.Check();
            long take = Math.Min(Window + 64, to - pos);
            var window = s.ReadBytes(pos, take, Window + 64);
            if (window == null) yield break;
            var (at, length) = Xmp.FindPacket(window);
            bool last = pos + take >= to;
            if (at < 0 && last) yield break;
            if (at < 0 || (at >= Window && !last))
            {
                pos += Window; // the next window overlaps this one by 64 bytes, so a tag cut at the end is found there
                continue;
            }
            long packetAt = pos + at;
            byte[]? packet;
            if (at + length < window.Length) packet = window.AsSpan(at, length).ToArray();
            else packet = WholePacket(s, packetAt, budget);
            if (packet == null) yield break;
            yield return packet;
            pos = packetAt + Math.Max(1, packet.Length);
        }
    }

    /// <summary>A packet whose end tag wasn't in the window: reads on until it is found (or 16 MB).</summary>
    static byte[]? WholePacket(IByteSource s, long at, ExtractBudget budget)
    {
        long size = Math.Min(s.Length - at, Window * 2);
        while (true)
        {
            budget.Check();
            var bytes = s.ReadBytes(at, size, MaxPacket);
            if (bytes == null) return null;
            var (start, length) = Xmp.FindPacket(bytes);
            if (start == 0 && length < bytes.Length) return bytes.AsSpan(0, length).ToArray();
            if (size >= MaxPacket || at + size >= s.Length) return bytes;
            size = Math.Min(Math.Min(s.Length - at, size * 2), MaxPacket);
        }
    }
}
