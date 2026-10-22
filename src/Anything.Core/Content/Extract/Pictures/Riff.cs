using System.Buffers.Binary;

namespace Anything.Core.Content.Extract;

/// <summary>
/// WebP (RIFF): VP8, VP8L and VP8X canvas size, EXIF and XMP chunks (reached by seeking over the image data), and the
/// ICC profile's name. At most 64 chunks.
/// </summary>
internal static class Riff
{
    const int MaxChunks = 64;

    public static void Read(IByteSource s, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        long end = s.TryU32(4, false, out uint riffSize) ? Math.Min(s.Length, 8L + riffSize) : s.Length;
        long pos = 12;
        var p = sink.Picture;
        bool alpha = false, sized = false;
        Span<byte> h = stackalloc byte[8];
        for (int n = 0; n < MaxChunks && pos + 8 <= end; n++)
        {
            budget.Check();
            if (!s.TryRead(pos, h)) break;
            var fourcc = System.Text.Encoding.ASCII.GetString(h[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            long data = pos + 8;
            if (size > end - data) { parts.Partial = true; break; }
            switch (fourcc)
            {
                case "VP8X" when size >= 10:
                    if (s.ReadBytes(data, 10, 10) is { } x)
                    {
                        alpha = (x[0] & 0x10) != 0;
                        p.Width = 1 + (x[4] | x[5] << 8 | x[6] << 16);
                        p.Height = 1 + (x[7] | x[8] << 8 | x[9] << 16);
                        sized = true;
                    }
                    break;
                case "VP8 " when size >= 10 && !sized:
                    if (s.ReadBytes(data, 10, 10) is { } k && k[3] == 0x9D && k[4] == 0x01 && k[5] == 0x2A)
                    {
                        p.Width = BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(6)) & 0x3FFF;
                        p.Height = BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(8)) & 0x3FFF;
                        sized = true;
                    }
                    break;
                case "VP8L" when size >= 5 && !sized:
                    if (s.ReadBytes(data, 5, 5) is { } l && l[0] == 0x2F)
                    {
                        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(l.AsSpan(1));
                        p.Width = (int)(bits & 0x3FFF) + 1;
                        p.Height = (int)((bits >> 14) & 0x3FFF) + 1;
                        alpha |= ((bits >> 28) & 1) != 0;
                        sized = true;
                    }
                    break;
                case "EXIF":
                {
                    long at = data, length = size;
                    // Some writers keep JPEG's "Exif\0\0" in front of the TIFF header.
                    if (s.ReadBytes(data, Math.Min(6, size), 6) is { Length: 6 } id && id.AsSpan().SequenceEqual("Exif\0\0"u8)) { at += 6; length -= 6; }
                    parts.Unit(() => Tiff.Read(new ByteSourceSlice(s, at, length), sink, budget, TiffMode.Exif, parts));
                    break;
                }
                case "XMP " when size <= 4 << 20:
                    if (s.ReadBytes(data, size, 4 << 20) is { } xmp && !XmlShape.ReadXmp(xmp, sink)) parts.Partial = true;
                    break;
                case "ICCP" when size <= 1 << 20:
                    if (s.ReadBytes(data, size, 1 << 20) is { } icc)
                        parts.Unit(() => { if (PictureValues.IccDescription(icc) is { Length: > 0 } name) sink.Field(DocField.ColorProfile, name); });
                    break;
            }
            pos = data + size + (size & 1);
        }
        if (sized)
        {
            p.BitDepth = (byte)(alpha ? 32 : 24);
            p.ColorMode = ColorMode.Rgb;
        }
    }
}
