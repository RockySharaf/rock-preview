using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Extract;

/// <summary>
/// IPTC IIM records: title (2:05), keywords (2:25), caption (2:120 → Comment), author (2:80), location (2:90 city, 2:92
/// sublocation, 2:95 state, 2:101 country). Text is UTF-8 when 1:90 says ESC % G, else UTF-8 when valid, else ANSI.
/// Every length is checked against the block.
/// </summary>
internal static class Iptc
{
    /// <summary>Reads IIM datasets into the sink.</summary>
    public static void Read(ReadOnlySpan<byte> data, ExtractSink sink)
    {
        bool utf8 = false;
        string? city = null, sublocation = null, state = null, country = null;
        int pos = 0, records = 0;
        while (pos + 5 <= data.Length && records++ < 10_000)
        {
            if (data[pos] != 0x1C) { pos++; continue; } // resynchronise on the tag marker
            int record = data[pos + 1], dataset = data[pos + 2];
            long length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
            int header = 5;
            if ((length & 0x8000) != 0)
            {
                // Extended dataset: the next (length & 0x7FFF) bytes hold the length.
                int n = (int)(length & 0x7FFF);
                if (n is < 1 or > 4 || pos + 5 + n > data.Length) break;
                length = 0;
                for (int i = 0; i < n; i++) length = (length << 8) | data[pos + 5 + i];
                header += n;
            }
            long start = pos + header;
            if (start + length > data.Length) length = data.Length - start;
            if (length < 0) break;
            var value = data.Slice((int)start, (int)length);
            pos = (int)(start + length);
            if (record == 1 && dataset == 90) { utf8 = value.SequenceEqual((ReadOnlySpan<byte>)[0x1B, 0x25, 0x47]); continue; }
            if (record != 2) continue;
            string Text(ReadOnlySpan<byte> v) => (utf8 || Utf8.IsValid(v) ? Encoding.UTF8.GetString(v) : TextDecoding.Ansi().GetString(v)).TrimEnd('\0').Trim();
            switch (dataset)
            {
                case 5: sink.Field(DocField.Title, Text(value)); break;
                case 25: sink.Field(DocField.Keywords, Text(value)); break;
                case 120: sink.Field(DocField.Comment, Text(value)); break;
                case 80: sink.Field(DocField.Author, Text(value)); break;
                case 90: city ??= Text(value); break;
                case 92: sublocation ??= Text(value); break;
                case 95: state ??= Text(value); break;
                case 101: country ??= Text(value); break;
            }
        }
        var parts = new[] { sublocation, city, state, country }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (parts.Length > 0) sink.Field(DocField.Location, string.Join(", ", parts));
    }
}
