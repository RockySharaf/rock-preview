using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// PNG chunks up to IDAT, then text chunks at the end (CRC-checked): IHDR, pHYs, iCCP, tEXt/zTXt/iTXt, eXIf. The walk
/// seeks from chunk to chunk; a lying length ends it. Text chunks written after the image data are found in the last 64
/// KB only where a chunk's own CRC-32 matches (so "tEXt" inside compressed pixels is never taken for a chunk). At most 512
/// KB is read.
/// </summary>
internal static class Png
{
    const int MaxChunks = 1000, MaxText = 1 << 20;
    const long MaxRead = 512 << 10, Tail = 64 << 10;

    public static void Read(IByteSource s, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        long start = s.BytesRead;
        long pos = 8, imageData = -1;
        Span<byte> h = stackalloc byte[8];
        for (int n = 0; n < MaxChunks && pos + 12 <= s.Length; n++)
        {
            budget.Check();
            if (!s.TryRead(pos, h)) break;
            long length = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.ASCII.GetString(h[4..8]);
            if (length > s.Length - pos - 12) { parts.Partial = true; break; } // a lying length
            long data = pos + 8;
            if (type is "IDAT" or "IEND") { imageData = pos; break; }
            if (s.BytesRead - start > MaxRead) break;
            parts.Unit(() => Chunk(s, type, data, length, sink, budget, parts));
            pos = data + length + 4;
        }
        // Text chunks after the image data (some writers put the metadata last).
        if (imageData > 0 && s.Length > imageData + 12 && s.BytesRead - start <= MaxRead)
        {
            long from = Math.Max(imageData, s.Length - Tail);
            var tail = s.ReadBytes(from, s.Length - from, Tail);
            if (tail != null) parts.Unit(() => TailChunks(tail, sink, budget, parts));
        }
    }

    static void Chunk(IByteSource s, string type, long data, long length, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        switch (type)
        {
            case "IHDR" when length >= 13:
            {
                var b = s.ReadBytes(data, 13, 13);
                if (b != null) Header(b, sink.Picture);
                break;
            }
            case "pHYs" when length >= 9:
            {
                var b = s.ReadBytes(data, 9, 9);
                if (b != null && b[8] == 1)
                {
                    // Pixels per metre.
                    double x = BinaryPrimitives.ReadUInt32BigEndian(b) * 0.0254, y = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) * 0.0254;
                    if (PictureValues.SaneDpi(x)) sink.Picture.DpiX = (int)Math.Round(x);
                    if (PictureValues.SaneDpi(y)) sink.Picture.DpiY = (int)Math.Round(y);
                }
                break;
            }
            case "iCCP" when length >= 3:
            {
                var b = s.ReadBytes(data, Math.Min(length, 80), 80);
                int nul = b == null ? -1 : Array.IndexOf(b, (byte)0);
                if (nul > 0) sink.Field(DocField.ColorProfile, Encoding.Latin1.GetString(b!, 0, nul).Trim());
                break;
            }
            case "tEXt" or "zTXt" or "iTXt" when length <= MaxText:
                if (s.ReadBytes(data, length, MaxText) is { } text) Text(type, text, sink, budget);
                break;
            case "eXIf":
                Tiff.Read(new ByteSourceSlice(s, data, length), sink, budget, TiffMode.Exif, parts);
                break;
        }
    }

    static void Header(byte[] b, PictureBuilder p)
    {
        p.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), int.MaxValue);
        p.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)), int.MaxValue);
        int depth = b[8], colorType = b[9];
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels > 0) p.BitDepth = (byte)Math.Clamp(depth * channels, 0, 255);
        p.ColorMode = colorType switch { 0 or 4 => ColorMode.Gray, 2 or 6 => ColorMode.Rgb, 3 => ColorMode.Indexed, _ => ColorMode.Unknown };
    }

    /// <summary>tEXt (Latin-1), zTXt (Latin-1, deflated) and iTXt (UTF-8, maybe deflated) by keyword.</summary>
    static void Text(string type, byte[] chunk, ExtractSink sink, ExtractBudget budget)
    {
        int nul = Array.IndexOf(chunk, (byte)0);
        if (nul is <= 0 or > 79) return;
        string keyword = Encoding.Latin1.GetString(chunk, 0, nul);
        string? value;
        if (type == "tEXt") value = Encoding.Latin1.GetString(chunk, nul + 1, chunk.Length - nul - 1);
        else if (type == "zTXt")
        {
            if (nul + 2 > chunk.Length || chunk[nul + 1] != 0) return;
            var raw = Inflate(chunk.AsSpan(nul + 2), MaxText, budget);
            value = raw == null ? null : Encoding.Latin1.GetString(raw);
        }
        else
        {
            if (nul + 3 > chunk.Length) return;
            bool compressed = chunk[nul + 1] == 1;
            int language = Array.IndexOf(chunk, (byte)0, nul + 3);
            if (language < 0) return;
            int translated = Array.IndexOf(chunk, (byte)0, language + 1);
            if (translated < 0) return;
            var body = chunk.AsSpan(translated + 1);
            byte[]? raw = compressed ? Inflate(body, keyword == "XML:com.adobe.xmp" ? 16 << 20 : MaxText, budget) : body.ToArray();
            if (raw == null) return;
            if (keyword == "XML:com.adobe.xmp") { XmlShape.ReadXmp(raw, sink); return; }
            value = Encoding.UTF8.GetString(raw);
        }
        if (value == null) return;
        if (keyword == "XML:com.adobe.xmp") { XmlShape.ReadXmp(Encoding.UTF8.GetBytes(value), sink); return; }
        switch (keyword)
        {
            case "Title": sink.Field(DocField.Title, value); break;
            case "Author": sink.Field(DocField.Author, value); break;
            case "Description" or "Comment": sink.Field(DocField.Comment, value); break;
            case "Software": sink.Field(DocField.CreatorTool, value); break;
            case "Keywords": sink.Keywords(value); break;
        }
    }

    /// <summary>Text chunks found in a tail window, each confirmed by its CRC.</summary>
    static void TailChunks(byte[] tail, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        const uint Text1 = 0x74455874, Text2 = 0x7A545874, Text3 = 0x69545874, Exif = 0x65584966; // tEXt zTXt iTXt eXIf
        // Every wanted type has an 'X' (third letter, or second in eXIf): only the bytes around an 'X' are looked at.
        int from = 0, checks = 0;
        while (from < tail.Length)
        {
            int x = tail.AsSpan(from).IndexOf((byte)'X');
            if (x < 0) return;
            x += from;
            from = x + 1;
            if ((++checks & 1023) == 0) budget.Check();
            foreach (int i in (ReadOnlySpan<int>)[x - 2, x - 1])
            {
                if (i < 4 || i + 8 > tail.Length) continue;
                uint type = BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(i));
                bool text = type is Text1 or Text2 or Text3;
                if (!text && type != Exif) continue;
                long length = BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(i - 4));
                if (length > tail.Length - i - 8) continue;
                uint crc = BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(i + 4 + (int)length));
                if (Crc32.Compute(tail.AsSpan(i, 4 + (int)length)) != crc) continue;
                var data = tail.AsSpan(i + 4, (int)length).ToArray();
                string name = Encoding.ASCII.GetString(tail, i, 4);
                if (text) parts.Unit(() => Text(name, data, sink, budget));
                else parts.Unit(() =>
                {
                    using var exif = new MemoryByteSource(data, budget);
                    Tiff.Read(exif, sink, budget, TiffMode.Exif, parts);
                });
                from = Math.Max(from, i + 8 + (int)length); // past the chunk and its CRC
                break;
            }
        }
    }

    /// <summary>
    /// Inflates zlib data: the 2-byte header is skipped and the Adler-32 never checked, and output decoded before an error
    /// is kept; at most max bytes (charged as decoded). Null when nothing could be decoded.
    /// </summary>
    internal static byte[]? Inflate(ReadOnlySpan<byte> zlib, int max, ExtractBudget budget)
    {
        if (zlib.Length < 2) return null;
        using var input = new MemoryStream(zlib[2..].ToArray(), writable: false);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new MemoryStream();
        var buffer = new byte[16 << 10];
        try
        {
            int n;
            while (output.Length < max && (n = inflate.Read(buffer, 0, (int)Math.Min(buffer.Length, max - output.Length))) > 0)
            {
                budget.Decoded(n);
                output.Write(buffer, 0, n);
            }
        }
        catch (InvalidDataException) { }
        return output.Length > 0 ? output.ToArray() : null;
    }
}

/// <summary>CRC-32 (IEEE 802.3, as PNG uses) from our own table; the BCL has only CRC-32C.</summary>
internal static class Crc32
{
    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
