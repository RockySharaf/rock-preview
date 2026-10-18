using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>EMF/WMF via GDI+ (vector: rendered directly at the target size).</summary>
internal static class MetafileRenderer
{
    public static PreviewResult? Render(string path, Size target)
    {
        var data = FileUtil.ReadAllCapped(path, 256L * 1024 * 1024);
        if (data == null) return null;
        var bmp = RenderBytes(data, BitmapUtil.DecodeBox(target), out var srcSize, out string fmt);
        if (bmp == null) return null;
        var res = new PreviewResult(path, PreviewKind.Image, "Metafile")
        {
            FormatName = fmt, Image = bmp, HasAlpha = true, SourcePixelSize = srcSize,
        };
        res.AddMeta("Frame size", $"{srcSize.Width} × {srcSize.Height}");
        return res;
    }

    public static Bitmap? RenderBytes(byte[] data, Size box, out Size srcSize, out string fmt)
    {
        srcSize = Size.Empty;
        fmt = "Metafile";
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var mf = new Metafile(ms);
            var hdr = mf.GetMetafileHeader();
            fmt = hdr.IsWmf() || hdr.IsWmfPlaceable() ? "Windows Metafile (WMF)" : "Enhanced Metafile (EMF)";
            srcSize = mf.Size;
            if (srcSize.Width <= 0 || srcSize.Height <= 0) return null;
            // Vector: scale up small metafiles to the box as well.
            var fit = BitmapUtil.FitSize(srcSize, box, allowUpscale: true);
            var bmp = new Bitmap(fit.Width, fit.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(mf, new Rectangle(0, 0, fit.Width, fit.Height));
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Truevision TGA: uncompressed + RLE; 8 (gray/mapped), 15/16, 24, 32 bpp.</summary>
internal static class TgaDecoder
{
    public static Bitmap? Decode(byte[] d, out bool hasAlpha)
    {
        hasAlpha = false;
        if (d.Length < 18) return null;
        int idLen = d[0], cmapType = d[1], type = d[2];
        int cmapFirst = Bytes.U16LE(d, 3), cmapLen = Bytes.U16LE(d, 5), cmapBits = d[7];
        int w = Bytes.U16LE(d, 12), h = Bytes.U16LE(d, 14), bpp = d[16], desc = d[17];
        if (w <= 0 || h <= 0 || w > 32768 || h > 32768) return null;
        if (type is not (1 or 2 or 3 or 9 or 10 or 11)) return null;
        bool rle = type >= 9;
        bool mapped = type is 1 or 9, gray = type is 3 or 11;
        if (bpp is not (8 or 15 or 16 or 24 or 32)) return null;
        if (mapped && (cmapType != 1 || bpp != 8)) return null;
        int pos = 18 + idLen;
        uint[]? palette = null;
        if (cmapType == 1)
        {
            int entryBytes = (cmapBits + 7) / 8;
            if (mapped)
            {
                palette = new uint[cmapFirst + cmapLen];
                for (int i = 0; i < cmapLen; i++)
                {
                    if (pos + entryBytes > d.Length) return null;
                    palette[cmapFirst + i] = ReadPixel(d, pos, cmapBits, false);
                    pos += entryBytes;
                }
            }
            else pos += cmapLen * entryBytes;
        }
        int alphaBits = desc & 0x0F;
        bool topDown = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0;
        int pxBytes = (bpp + 7) / 8;
        var pixels = new uint[w * h];
        int n = w * h, i0 = 0;
        try
        {
            while (i0 < n)
            {
                if (rle)
                {
                    int hdr = d[pos++];
                    int count = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0)
                    {
                        uint px = Convert(d, pos, bpp, gray, palette);
                        pos += pxBytes;
                        for (int k = 0; k < count && i0 < n; k++) pixels[i0++] = px;
                    }
                    else
                    {
                        for (int k = 0; k < count && i0 < n; k++)
                        {
                            pixels[i0++] = Convert(d, pos, bpp, gray, palette);
                            pos += pxBytes;
                        }
                    }
                }
                else
                {
                    if (pos + pxBytes > d.Length) break;
                    pixels[i0++] = Convert(d, pos, bpp, gray, palette);
                    pos += pxBytes;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // truncated file: show what we have
        }
        // 32-bit TGAs often carry alpha=0 everywhere when the alpha channel is unused.
        bool useAlpha = bpp == 32 && alphaBits > 0 || bpp == 32 && alphaBits == 0 && AnyNonZeroAlpha(pixels);
        if (bpp == 32 && useAlpha && !AnyNonZeroAlpha(pixels)) useAlpha = false;
        hasAlpha = useAlpha;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            int sy = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int sx = rightToLeft ? w - 1 - x : x;
                uint p = pixels[sy * w + sx];
                if (!useAlpha) p |= 0xFF000000;
                row[x] = unchecked((int)p);
            }
            Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w);
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    private static bool AnyNonZeroAlpha(uint[] px)
    {
        foreach (var p in px) if ((p >> 24) != 0) return true;
        return false;
    }

    private static uint Convert(byte[] d, int pos, int bpp, bool gray, uint[]? palette)
    {
        if (bpp == 8)
        {
            byte v = d[pos];
            if (palette != null) return v < palette.Length ? palette[v] : 0xFF000000;
            return 0xFF000000u | (uint)(v << 16 | v << 8 | v);
        }
        return ReadPixel(d, pos, bpp, gray);
    }

    private static uint ReadPixel(byte[] d, int pos, int bits, bool gray)
    {
        switch (bits)
        {
            case 15:
            case 16:
            {
                int v = d[pos] | d[pos + 1] << 8;
                int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                return 0xFF000000u | (uint)((r * 255 / 31) << 16 | (g * 255 / 31) << 8 | (b * 255 / 31));
            }
            case 24:
                return 0xFF000000u | (uint)(d[pos + 2] << 16 | d[pos + 1] << 8 | d[pos]);
            case 32:
                return (uint)(d[pos + 3] << 24 | d[pos + 2] << 16 | d[pos + 1] << 8 | d[pos]);
            default:
                return 0xFF000000;
        }
    }
}

/// <summary>
/// Photoshop PSD/PSB: decodes the merged composite (raw or PackBits RLE; 8/16-bit; RGB, Grayscale, CMYK, Indexed,
/// Bitmap, Duotone) subsampled to the target size while reading only the rows it needs. Falls back to the embedded
/// JPEG thumbnail resource (1036/1033).
/// </summary>
internal static class PsdDecoder
{
    public sealed class Info
    {
        public int Width, Height, Channels, Depth, ColorMode;
        public bool IsPsb;
        public string ColorModeName => ColorMode switch
        {
            0 => "Bitmap", 1 => "Grayscale", 2 => "Indexed", 3 => "RGB", 4 => "CMYK", 7 => "Multichannel",
            8 => "Duotone", 9 => "Lab", _ => "Mode " + ColorMode,
        };
    }

    public static Bitmap? Decode(string path, Size box, CancellationToken ct, out Info? info, out bool fromThumbnail)
    {
        info = null;
        fromThumbnail = false;
        using var fs = FileUtil.OpenShared(path, 1 << 16);
        var br = new BinaryReader(fs);
        byte[] hdr = br.ReadBytes(26);
        if (hdr.Length < 26 || !Bytes.StartsWithAscii(hdr, "8BPS")) return null;
        var inf = new Info
        {
            IsPsb = Bytes.U16BE(hdr, 4) == 2,
            Channels = Bytes.U16BE(hdr, 12),
            Height = (int)Bytes.U32BE(hdr, 14),
            Width = (int)Bytes.U32BE(hdr, 18),
            Depth = Bytes.U16BE(hdr, 22),
            ColorMode = Bytes.U16BE(hdr, 24),
        };
        info = inf;
        // Color mode data (palette for indexed).
        uint cmLen = ReadU32BE(br);
        byte[] colorData = cmLen > 0 && cmLen < 1 << 20 ? br.ReadBytes((int)cmLen) : Skip(fs, cmLen);
        // Image resources: look for the thumbnail while skipping.
        uint resLen = ReadU32BE(br);
        long resEnd = fs.Position + resLen;
        byte[]? thumbJpeg = null;
        while (fs.Position + 12 <= resEnd)
        {
            byte[] sig = br.ReadBytes(4);
            if (!Bytes.StartsWithAscii(sig, "8BIM") && !Bytes.StartsWithAscii(sig, "MeSa")) break;
            int id = ReadU16BE(br);
            int nameLen = br.ReadByte();
            int skipName = (nameLen + 1) % 2 == 0 ? nameLen : nameLen + 1; // pascal string padded to even (incl. length byte)
            fs.Seek(skipName, SeekOrigin.Current);
            uint size = ReadU32BE(br);
            long dataStart = fs.Position;
            if ((id == 1036 || id == 1033) && size > 28 && size < 16 << 20)
            {
                byte[] t = br.ReadBytes((int)size);
                if (Bytes.U32BE(t, 0) == 1) thumbJpeg = t[28..]; // kJpegRGB
            }
            fs.Position = dataStart + size + (size & 1);
        }
        fs.Position = resEnd;
        // Layer & mask section.
        long lmLen = inf.IsPsb ? (long)ReadU64BE(br) : ReadU32BE(br);
        fs.Seek(lmLen, SeekOrigin.Current);
        ct.ThrowIfCancellationRequested();

        Bitmap? bmp = null;
        try
        {
            bmp = DecodeComposite(fs, br, inf, colorData, box, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            bmp = null;
        }
        if (bmp == null && thumbJpeg != null)
        {
            bmp = BitmapUtil.Decode(thumbJpeg, box);
            fromThumbnail = bmp != null;
        }
        return bmp;
    }

    private static Bitmap? DecodeComposite(FileStream fs, BinaryReader br, Info inf, byte[] colorData, Size box, CancellationToken ct)
    {
        int w = inf.Width, h = inf.Height, depth = inf.Depth;
        if (w <= 0 || h <= 0 || depth is not (1 or 8 or 16)) return null;
        if (inf.ColorMode is not (0 or 1 or 2 or 3 or 4 or 8)) return null;
        int compression = ReadU16BE(br);
        if (compression is not (0 or 1)) return null;
        int channels = inf.Channels;
        int needChannels = inf.ColorMode switch { 3 => 3, 4 => 4, _ => 1 };
        if (channels < needChannels) return null;

        // Subsample: keep at most ~2x the box, final high-quality resize does the rest.
        var fit = BitmapUtil.FitSize(new Size(w, h), new Size(box.Width * 2, box.Height * 2));
        int step = Math.Max(1, (int)Math.Floor(Math.Min((double)w / fit.Width, (double)h / fit.Height)));
        int ow = (w + step - 1) / step, oh = (h + step - 1) / step;

        int rowBytes = depth == 1 ? (w + 7) / 8 : w * depth / 8;
        long dataStart;
        long[]? rowOffsets = null;
        int[]? rowLens = null;
        if (compression == 1)
        {
            int rows = channels * h;
            rowLens = new int[rows];
            byte[] counts = br.ReadBytes(rows * (inf.IsPsb ? 4 : 2));
            for (int i = 0; i < rows; i++)
                rowLens[i] = inf.IsPsb ? (int)Bytes.U32BE(counts, i * 4) : Bytes.U16BE(counts, i * 2);
            dataStart = fs.Position;
            rowOffsets = new long[rows];
            long off = dataStart;
            for (int i = 0; i < rows; i++) { rowOffsets[i] = off; off += rowLens[i]; }
        }
        else
        {
            dataStart = fs.Position;
        }

        var planes = new byte[needChannels][];
        for (int c = 0; c < needChannels; c++) planes[c] = new byte[ow * oh];
        var raw = new byte[rowBytes];
        var packed = new byte[Math.Max(16, rowBytes * 2)];
        for (int c = 0; c < needChannels; c++)
        {
            for (int oy = 0; oy < oh; oy++)
            {
                if ((oy & 63) == 0) ct.ThrowIfCancellationRequested();
                int y = oy * step;
                int ri = c * h + y;
                if (compression == 0)
                {
                    fs.Position = dataStart + (long)ri * rowBytes;
                    ReadExactly(fs, raw, rowBytes);
                }
                else
                {
                    int len = rowLens![ri];
                    if (packed.Length < len) packed = new byte[len];
                    fs.Position = rowOffsets![ri];
                    ReadExactly(fs, packed, len);
                    UnpackBits(packed, len, raw);
                }
                var plane = planes[c];
                int o = oy * ow;
                for (int ox = 0; ox < ow; ox++)
                {
                    int x = ox * step;
                    plane[o + ox] = depth switch
                    {
                        8 => raw[x],
                        16 => raw[x * 2],
                        _ => (raw[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255, // bitmap: 1 = black
                    };
                }
            }
        }

        var bmp = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var line = new int[ow];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int i = y * ow + x;
                int r, g, b;
                switch (inf.ColorMode)
                {
                    case 3: r = planes[0][i]; g = planes[1][i]; b = planes[2][i]; break;
                    case 4:
                    {
                        // PSD stores CMYK inverted (255 = no ink).
                        int cc = planes[0][i], mm = planes[1][i], yy = planes[2][i], kk = planes[3][i];
                        r = cc * kk / 255; g = mm * kk / 255; b = yy * kk / 255;
                        break;
                    }
                    case 2:
                    {
                        int idx = planes[0][i];
                        if (colorData.Length >= 768) { r = colorData[idx]; g = colorData[256 + idx]; b = colorData[512 + idx]; }
                        else r = g = b = idx;
                        break;
                    }
                    default: r = g = b = planes[0][i]; break;
                }
                line[x] = unchecked((int)(0xFF000000u | (uint)(r << 16 | g << 8 | b)));
            }
            Marshal.Copy(line, 0, bd.Scan0 + y * bd.Stride, ow);
        }
        bmp.UnlockBits(bd);
        return BitmapUtil.FitOrSelf(bmp, box);
    }

    private static void UnpackBits(byte[] src, int len, byte[] dst)
    {
        int si = 0, di = 0;
        while (si < len && di < dst.Length)
        {
            sbyte n = (sbyte)src[si++];
            if (n >= 0)
            {
                int cnt = n + 1;
                for (int k = 0; k < cnt && si < len && di < dst.Length; k++) dst[di++] = src[si++];
            }
            else if (n != -128)
            {
                int cnt = 1 - n;
                if (si >= len) break;
                byte v = src[si++];
                for (int k = 0; k < cnt && di < dst.Length; k++) dst[di++] = v;
            }
        }
    }

    private static void ReadExactly(Stream s, byte[] buf, int n)
    {
        int read = 0;
        while (read < n)
        {
            int r = s.Read(buf, read, n - read);
            if (r <= 0) { Array.Clear(buf, read, n - read); return; }
            read += r;
        }
    }

    private static byte[] Skip(Stream s, long n) { s.Seek(n, SeekOrigin.Current); return Array.Empty<byte>(); }
    private static int ReadU16BE(BinaryReader br) { var b = br.ReadBytes(2); return b.Length < 2 ? 0 : b[0] << 8 | b[1]; }
    private static uint ReadU32BE(BinaryReader br) { var b = br.ReadBytes(4); return b.Length < 4 ? 0 : Bytes.U32BE(b, 0); }
    private static ulong ReadU64BE(BinaryReader br) { var b = br.ReadBytes(8); return b.Length < 8 ? 0 : (ulong)Bytes.U32BE(b, 0) << 32 | Bytes.U32BE(b, 4); }
}
