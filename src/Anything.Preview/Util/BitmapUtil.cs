using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using WpfPixelFormat = System.Windows.Media.PixelFormat;

namespace Anything.Preview.Util;

internal static class BitmapUtil
{
    /// <summary>Scales <paramref name="src"/> to fit inside <paramref name="box"/> keeping aspect (never upscales unless asked).</summary>
    public static Size FitSize(Size src, Size box, bool allowUpscale = false)
    {
        if (src.Width <= 0 || src.Height <= 0) return new Size(1, 1);
        if (box.Width <= 0 || box.Height <= 0) return src;
        double s = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);
        if (!allowUpscale && s > 1) s = 1;
        return new Size(Math.Max(1, (int)Math.Round(src.Width * s)), Math.Max(1, (int)Math.Round(src.Height * s)));
    }

    /// <summary>The size we decode to for a given pane: generous enough for some zoom, bounded for memory.</summary>
    public static Size DecodeBox(Size target)
    {
        int w = Math.Max(target.Width, 256), h = Math.Max(target.Height, 256);
        // 2x the pane so zooming in a little stays sharp, but never more than ~4K on a side.
        return new Size(Math.Min(w * 2, 4096), Math.Min(h * 2, 4096));
    }

    public static bool FormatHasAlpha(WpfPixelFormat f) =>
        f == WpfPixelFormats.Bgra32 || f == WpfPixelFormats.Pbgra32 || f == WpfPixelFormats.Rgba64 ||
        f == WpfPixelFormats.Prgba64 || f == WpfPixelFormats.Rgba128Float || f == WpfPixelFormats.Prgba128Float;

    public static bool PaletteHasAlpha(BitmapSource src)
    {
        var pal = src.Palette;
        if (pal == null) return false;
        foreach (var c in pal.Colors) if (c.A < 255) return true;
        return false;
    }

    /// <summary>Downscales a WIC source (Fant) so it fits <paramref name="box"/>; returns the source when already small.</summary>
    public static BitmapSource ScaleToFit(BitmapSource src, Size box)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var fit = FitSize(new Size(w, h), box);
        if (fit.Width >= w && fit.Height >= h) return src;
        double sx = (double)fit.Width / w, sy = (double)fit.Height / h;
        var tb = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(sx, sy));
        return tb;
    }

    /// <summary>Copies a WIC bitmap into a GDI+ 32bpp premultiplied bitmap (fast to blit).</summary>
    public static Bitmap ToGdi(BitmapSource src)
    {
        BitmapSource s = src.Format == WpfPixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, WpfPixelFormats.Pbgra32, null, 0);
        int w = s.PixelWidth, h = s.PixelHeight;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            s.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Stride * h, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>Decodes an in-memory image (any WIC format, GDI+ fallback) scaled to fit <paramref name="box"/>.</summary>
    public static Bitmap? Decode(byte[] data, Size box, out Size sourceSize, out bool hasAlpha)
    {
        sourceSize = Size.Empty;
        hasAlpha = false;
        if (data.Length < 8) return null;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (dec.Frames.Count == 0) return null;
            BitmapSource frame = dec.Frames[0];
            sourceSize = new Size(frame.PixelWidth, frame.PixelHeight);
            hasAlpha = FormatHasAlpha(frame.Format) || PaletteHasAlpha(frame);
            int orientation = WicImage.ReadOrientation(dec.Frames[0]);
            var scaled = ScaleToFit(frame, OrientBox(box, orientation));
            var bmp = ToGdi(scaled);
            ApplyOrientation(bmp, orientation);
            return bmp;
        }
        catch
        {
            // fall through to GDI+
        }
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var img = System.Drawing.Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            sourceSize = img.Size;
            hasAlpha = System.Drawing.Image.IsAlphaPixelFormat(img.PixelFormat);
            var fit = FitSize(img.Size, box);
            return Resize(img, fit);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap? Decode(byte[] data, Size box) => Decode(data, box, out _, out _);

    /// <summary>For orientations that swap axes, swap the decode box as well.</summary>
    public static Size OrientBox(Size box, int orientation) =>
        orientation is >= 5 and <= 8 ? new Size(box.Height, box.Width) : box;

    public static void ApplyOrientation(Bitmap bmp, int orientation)
    {
        RotateFlipType? t = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => null,
        };
        if (t != null) bmp.RotateFlip(t.Value);
    }

    /// <summary>High quality resize into a new 32bpp premultiplied bitmap.</summary>
    public static Bitmap Resize(System.Drawing.Image src, Size size)
    {
        var dst = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    /// <summary>Returns a downscaled copy if larger than the box, else the original (ownership transfers).</summary>
    public static Bitmap FitOrSelf(Bitmap src, Size box)
    {
        var fit = FitSize(src.Size, box);
        if (fit.Width >= src.Width && fit.Height >= src.Height) return src;
        var r = Resize(src, fit);
        src.Dispose();
        return r;
    }

    /// <summary>Wraps a raw DIB (BITMAPINFOHEADER + palette + bits, no file header) as a .bmp byte array.</summary>
    public static byte[]? DibToBmp(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) return null;
        uint hdrSize = Bytes.U32LE(dib, 0);
        if (hdrSize < 12 || hdrSize > 124 || hdrSize > dib.Length) return null;
        int bitCount, clrUsed = 0;
        uint compression = 0;
        if (hdrSize == 12)
        {
            bitCount = Bytes.U16LE(dib, 10);
        }
        else
        {
            bitCount = Bytes.U16LE(dib, 14);
            compression = Bytes.U32LE(dib, 16);
            clrUsed = (int)Bytes.U32LE(dib, 32);
        }
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32)) return null;
        int paletteEntries = bitCount <= 8 ? (clrUsed > 0 ? clrUsed : 1 << bitCount) : clrUsed;
        int entrySize = hdrSize == 12 ? 3 : 4;
        int masks = (compression == 3 && hdrSize == 40) ? 12 : 0; // BI_BITFIELDS
        int offBits = 14 + (int)hdrSize + masks + paletteEntries * entrySize;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), offBits);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    /// <summary>Paints a checkerboard (for transparent images).</summary>
    public static void FillChecker(Graphics g, Rectangle r, int cell = 8)
    {
        using var light = new SolidBrush(Color.FromArgb(255, 255, 255));
        using var dark = new SolidBrush(Color.FromArgb(222, 222, 222));
        g.FillRectangle(light, r);
        var clip = g.Clip;
        g.SetClip(r, CombineMode.Intersect);
        for (int y = r.Top; y < r.Bottom; y += cell)
        {
            for (int x = r.Left + (((y - r.Top) / cell) % 2) * cell; x < r.Right; x += cell * 2)
                g.FillRectangle(dark, x, y, cell, cell);
        }
        g.Clip = clip;
    }
}
