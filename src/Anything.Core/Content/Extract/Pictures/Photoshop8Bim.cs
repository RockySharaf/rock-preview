using System.Buffers.Binary;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Photoshop image resource blocks (8BIM): resolution (0x03ED, pixels per inch whatever unit it is shown in), IPTC
/// (0x0404), XMP (0x0424), EXIF (0x0422, bounded to its block) and the ICC profile's name (0x040F). The thumbnail and
/// other blocks are skipped by seeking. Each block is read on its own; a bad one is skipped. The caller may give an
/// allowance of bytes (a TIFF's 1 MB): a block that doesn't fit what is left of it is skipped and the picture is partial.
/// </summary>
internal static class Photoshop8Bim
{
    const int MaxBlocks = 1000;
    const long MaxBlockBytes = 16 << 20;

    /// <summary>Reads the blocks of an image resource section (the source holds just that section).</summary>
    public static void Read(IByteSource s, ExtractSink sink, ExtractBudget budget, PictureParts parts, long allowance = long.MaxValue)
    {
        long pos = 0, start = s.BytesRead;
        long Left() => allowance - (s.BytesRead - start);
        bool Fits(long bytes)
        {
            if (bytes <= Left()) return true;
            parts.Partial = true;
            return false;
        }
        Span<byte> head = stackalloc byte[7];
        for (int n = 0; n < MaxBlocks && pos + 12 <= s.Length && Left() > 0; n++)
        {
            budget.Check();
            if (!s.TryRead(pos, head)) break;
            if (!IsSignature(head[..4])) break;
            int id = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
            int nameLength = head[6];
            long sizeAt = pos + 6 + ((1 + nameLength + 1) & ~1); // Pascal name padded to an even length
            if (!s.TryU32(sizeAt, bigEndian: true, out uint size)) break;
            long data = sizeAt + 4;
            if (size > s.Length - data) break;
            long blockSize = size;
            pos = data + blockSize + (blockSize & 1);
            switch (id)
            {
                case 0x03ED when blockSize >= 16:
                    parts.Unit(() => Resolution(s, data, sink));
                    break;
                case 0x0404 when blockSize <= MaxBlockBytes && Fits(blockSize):
                    if (s.ReadBytes(data, blockSize, MaxBlockBytes) is { } iptc) parts.Unit(() => Iptc.Read(iptc, sink));
                    break;
                case 0x0424 when blockSize <= MaxBlockBytes && Fits(blockSize):
                    if (s.ReadBytes(data, blockSize, MaxBlockBytes) is { } xmp && !XmlShape.ReadXmp(xmp, sink)) parts.Partial = true;
                    break;
                case 0x0422:
                    long exifAllowance = Left();
                    parts.Unit(() => Tiff.Read(new ByteSourceSlice(s, data, blockSize), sink, budget, TiffMode.Exif, parts, exifAllowance));
                    break;
                case 0x040F when blockSize <= 4 << 20 && Fits(blockSize):
                    if (s.ReadBytes(data, blockSize, 4 << 20) is { } icc && PictureValues.IccDescription(icc) is { Length: > 0 } name)
                        sink.Field(DocField.ColorProfile, name);
                    break;
            }
        }
    }

    static bool IsSignature(ReadOnlySpan<byte> sig) => sig.SequenceEqual("8BIM"u8) || sig.SequenceEqual("MeSa"u8)
        || sig.SequenceEqual("PHUT"u8) || sig.SequenceEqual("AgHg"u8) || sig.SequenceEqual("DCSR"u8);

    /// <summary>ResolutionInfo: hRes and vRes are 16.16 fixed pixels per inch (the units only say how Photoshop shows them).</summary>
    static void Resolution(IByteSource s, long at, ExtractSink sink)
    {
        if (!s.TryU32(at, true, out uint h) || !s.TryU32(at + 8, true, out uint v)) return;
        double x = h / 65536.0, y = v / 65536.0;
        if (PictureValues.SaneDpi(x)) sink.Picture.DpiX = (int)Math.Round(x);
        if (PictureValues.SaneDpi(y)) sink.Picture.DpiY = (int)Math.Round(y);
    }
}
