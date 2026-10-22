using System.Buffers.Binary;

namespace Anything.Core.Content.Extract;

/// <summary>GIF logical screen and BMP BITMAPINFOHEADER.</summary>
internal static class GifBmp
{
    /// <summary>GIF: the logical screen's size; indexed colour with the global colour table's bits per pixel.</summary>
    public static void ReadGif(IByteSource s, ExtractSink sink)
    {
        var b = s.ReadBytes(0, 13, 13);
        if (b == null) throw new InvalidDataException("Short GIF.");
        var p = sink.Picture;
        p.Width = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
        p.Height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
        int packed = b[10];
        p.BitDepth = (byte)((packed & 0x80) != 0 ? (packed & 7) + 1 : ((packed >> 4) & 7) + 1);
        p.ColorMode = ColorMode.Indexed;
    }

    /// <summary>BMP: size (height may be negative: top-down), bits per pixel, pixels per metre as DPI; indexed at 8 bits or fewer.</summary>
    public static void ReadBmp(IByteSource s, ExtractSink sink)
    {
        var b = s.ReadBytes(0, Math.Min(s.Length, 54), 54);
        if (b == null || b.Length < 26) throw new InvalidDataException("Short BMP.");
        var p = sink.Picture;
        uint header = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(14));
        int bpp;
        if (header == 12)
        {
            // BITMAPCOREHEADER (OS/2): 16-bit sizes.
            p.Width = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18));
            p.Height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24));
        }
        else if (header >= 40 && b.Length >= 46)
        {
            p.Width = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18)));
            int height = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22));
            p.Height = height == int.MinValue ? 0 : Math.Abs(height);
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28));
            double x = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(38)) * 0.0254, y = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(42)) * 0.0254;
            if (PictureValues.SaneDpi(x)) p.DpiX = (int)Math.Round(x);
            if (PictureValues.SaneDpi(y)) p.DpiY = (int)Math.Round(y);
        }
        else throw new InvalidDataException("Unknown BMP header.");
        if (bpp is > 0 and <= 64)
        {
            p.BitDepth = (byte)bpp;
            p.ColorMode = bpp <= 8 ? ColorMode.Indexed : ColorMode.Rgb;
        }
    }
}
