using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// JPEG marker walk up to SOS: SOF, APP0 JFIF, APP1 Exif and XMP (extended XMP too), APP13 IRB, APP14 Adobe, APP2 ICC,
/// COM. At most 64 segments; only the headers are read during the walk, then the payloads of the segments used, in order
/// of trust: frame header, Photoshop resolution, EXIF, JFIF density, IPTC, XMP. At most 512 KB is read. JFIF units 0
/// is an aspect ratio, not a resolution.
/// </summary>
internal static class Jpeg
{
    const int MaxSegments = 64;
    const long MaxRead = 512 << 10;
    static readonly byte[] ExifId = "Exif\0\0"u8.ToArray();
    static readonly byte[] XmpId = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    static readonly byte[] ExtendedXmpId = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    static readonly byte[] PhotoshopId = Encoding.ASCII.GetBytes("Photoshop 3.0\0");
    static readonly byte[] IccId = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    readonly record struct Segment(int Marker, long Payload, int Length);

    public static void Read(IByteSource s, ExtractSink sink, ExtractBudget budget, PictureParts parts)
    {
        long start = s.BytesRead;
        var segments = new List<Segment>();
        long pos = 2;
        Span<byte> h = stackalloc byte[4];
        int fill = 0;
        for (int n = 0; n < MaxSegments; n++)
        {
            budget.Check();
            if (!s.TryRead(pos, h)) break;
            if (h[0] != 0xFF) break;
            int marker = h[1];
            if (marker == 0xFF)
            {
                // Fill bytes before a marker: skipped, not counted as segments (but not without end).
                if (++fill > 256) break;
                pos++;
                n--;
                continue;
            }
            fill = 0;
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (marker is 0xD9 or 0xDA) break; // end of image, start of scan: the headers are over
            int length = BinaryPrimitives.ReadUInt16BigEndian(h[2..]);
            if (length < 2 || pos + 2 + length > s.Length) break;
            segments.Add(new Segment(marker, pos + 4, length - 2));
            pos += 2 + length;
        }
        var p = sink.Picture;
        bool Over() => s.BytesRead - start > MaxRead;

        // Frame header: size, depth, colour model. Four components are CMYK whatever the APP14 Adobe transform says
        // (0 = CMYK, 2 = YCCK, which is CMYK stored as YCbCr plus K); three are RGB (YCbCr or, with transform 0, RGB).
        foreach (var seg in segments)
        {
            if (seg.Marker is < 0xC0 or > 0xCF or 0xC4 or 0xC8 or 0xCC || seg.Length < 6) continue;
            var f = s.ReadBytes(seg.Payload, 6, 6);
            if (f == null) break;
            int precision = f[0], height = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(1)), width = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(3)), components = f[5];
            p.Width = width;
            p.Height = height; // 0 = given later by a DNL marker: unknown
            p.BitDepth = (byte)Math.Clamp(precision * components, 0, 255);
            p.ColorMode = components switch
            {
                1 => ColorMode.Gray,
                3 => ColorMode.Rgb,
                4 => ColorMode.Cmyk,
                _ => ColorMode.Unknown,
            };
            break;
        }

        // Photoshop resources first: their resolution outranks EXIF and JFIF.
        foreach (var seg in segments)
            if (seg.Marker == 0xED && Starts(s, seg, PhotoshopId) && !Over())
                parts.Unit(() => Photoshop8Bim.Read(new ByteSourceSlice(s, seg.Payload + PhotoshopId.Length, seg.Length - PhotoshopId.Length), sink, budget, parts));
        foreach (var seg in segments)
            if (seg.Marker == 0xE1 && Starts(s, seg, ExifId) && !Over())
            {
                parts.Unit(() => Tiff.Read(new ByteSourceSlice(s, seg.Payload + ExifId.Length, seg.Length - ExifId.Length), sink, budget, TiffMode.Exif, parts));
                break;
            }
        foreach (var seg in segments)
            if (seg.Marker == 0xE0 && seg.Length >= 12 && Starts(s, seg, "JFIF\0"u8))
            {
                parts.Unit(() => Jfif(s, seg, p));
                break;
            }
        // XMP: the main packet, then extended XMP chunks reassembled by their GUID.
        // Chunks are kept as they come and joined at the end, so a declared total is never allocated on trust.
        Dictionary<string, (long Total, List<(int Offset, byte[] Data)> Chunks)>? extended = null;
        foreach (var seg in segments)
        {
            if (seg.Marker != 0xE1 || Over()) continue;
            if (Starts(s, seg, XmpId))
            {
                if (s.ReadBytes(seg.Payload + XmpId.Length, seg.Length - XmpId.Length, 1 << 16) is { } packet && !XmlShape.ReadXmp(packet, sink)) parts.Partial = true;
            }
            else if (Starts(s, seg, ExtendedXmpId) && seg.Length > ExtendedXmpId.Length + 40)
            {
                var chunk = s.ReadBytes(seg.Payload + ExtendedXmpId.Length, seg.Length - ExtendedXmpId.Length, 1 << 16);
                if (chunk == null) continue;
                string guid = Encoding.ASCII.GetString(chunk, 0, 32);
                long total = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(32)), offset = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(36));
                if (total is <= 0 or > 4 << 20 || offset >= total) continue;
                extended ??= [];
                if (!extended.TryGetValue(guid, out var whole))
                {
                    if (extended.Count >= 4) continue;
                    extended[guid] = whole = (total, []);
                }
                if (whole.Total != total) continue;
                int n = (int)Math.Min(chunk.Length - 40, total - offset);
                whole.Chunks.Add(((int)offset, chunk.AsSpan(40, n).ToArray()));
            }
        }
        if (extended != null)
            foreach (var (_, chunks) in extended.Values)
            {
                int size = chunks.Max(c => c.Offset + c.Data.Length);
                var data = new byte[size];
                foreach (var (offset, bytes) in chunks) bytes.CopyTo(data, offset);
                if (!XmlShape.ReadXmp(data, sink)) parts.Partial = true;
            }

        // ICC profile chunks, in order.
        var icc = new SortedDictionary<int, byte[]>();
        foreach (var seg in segments)
            if (seg.Marker == 0xE2 && seg.Length > IccId.Length + 2 && Starts(s, seg, IccId) && !Over())
                if (s.ReadBytes(seg.Payload + IccId.Length, seg.Length - IccId.Length, 1 << 16) is { } c && c.Length > 2) icc.TryAdd(c[0], c[2..]);
        if (icc.Count > 0)
        {
            var profile = icc.Values.SelectMany(b => b).Take(1 << 20).ToArray();
            parts.Unit(() => { if (PictureValues.IccDescription(profile) is { Length: > 0 } name) sink.Field(DocField.ColorProfile, name); });
        }
        foreach (var seg in segments)
            if (seg.Marker == 0xFE && seg.Length > 0 && !Over() && s.ReadBytes(seg.Payload, seg.Length, ExtractSink.MaxFieldChars * 4) is { } comment
                && PictureValues.Text(comment) is { Length: > 0 } text)
            {
                sink.Field(DocField.Comment, text);
                break;
            }
    }

    static void Jfif(IByteSource s, Segment seg, PictureBuilder p)
    {
        var b = s.ReadBytes(seg.Payload, 12, 12);
        if (b == null) return;
        int units = b[7], x = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(8)), y = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(10));
        // Units 0 means the densities are only an aspect ratio (libjpeg writes 1:1): no resolution.
        double scale = units switch { 1 => 1, 2 => 2.54, _ => 0 };
        if (scale == 0) return;
        if (PictureValues.SaneDpi(x * scale)) p.DpiX = (int)Math.Round(x * scale);
        if (PictureValues.SaneDpi(y * scale)) p.DpiY = (int)Math.Round(y * scale);
    }

    static bool Starts(IByteSource s, Segment seg, ReadOnlySpan<byte> id)
    {
        if (seg.Length < id.Length) return false;
        Span<byte> b = stackalloc byte[64];
        var head = b[..id.Length];
        return s.TryRead(seg.Payload, head) && head.SequenceEqual(id);
    }
}
