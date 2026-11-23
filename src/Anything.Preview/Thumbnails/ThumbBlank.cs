using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace Anything.Preview.Thumbnails;

/// <summary>
/// Whether a picture is blank: one flat colour, nothing on it. Office documents made by libraries (python-docx and
/// python-pptx among them) carry their template's empty page as their saved thumbnail, and Windows' Office handler hands
/// back the same picture; shown as is, it is a white cell that looks like a missing thumbnail. Only document kinds are
/// judged this way: a real all-white photo is still that photo.
/// </summary>
public static class ThumbBlank
{
    // A channel further than this from the corner's counts as a mark (JPEG noise on a flat page stays well under it).
    private const int Tolerance = 24;

    /// <summary>
    /// True when fewer than about 0.1 % of the picture's pixels differ noticeably from its top-left corner's colour.
    /// One pass over the pixels, stopped as soon as enough marks are found: well under a millisecond for a thumbnail
    /// (about 0.1 ms for 256 x 256; a 32-bit picture is read in place, any other format is converted by GDI+ first).
    /// False for an empty picture. The caller keeps <paramref name="picture"/>.
    /// </summary>
    public static unsafe bool IsBlank(Bitmap picture)
    {
        int w = picture.Width, h = picture.Height;
        if (w <= 0 || h <= 0) return false;
        // 32-bit pictures (what the decoders here give) are read as they are; the rest are converted to 32-bit ARGB.
        var format = picture.PixelFormat is PixelFormat.Format32bppPArgb or PixelFormat.Format32bppArgb or PixelFormat.Format32bppRgb
            ? picture.PixelFormat : PixelFormat.Format32bppArgb;
        // A picture without alpha has an undefined fourth byte: it is not compared.
        uint mask = format == PixelFormat.Format32bppRgb ? 0x00FFFFFFu : 0xFFFFFFFFu;
        long allowed = (long)w * h / 1000; // more marks than this and it is not blank
        long marks = 0;
        var data = picture.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, format);
        try
        {
            byte* scan = (byte*)data.Scan0;
            uint corner = *(uint*)scan & mask;
            for (int y = 0; y < h; y++)
            {
                uint* row = (uint*)(scan + (long)y * data.Stride);
                for (int x = 0; x < w; x++)
                {
                    uint p = row[x] & mask;
                    if (p == corner || !Differs(p, corner)) continue;
                    if (++marks > allowed) return false;
                }
            }
        }
        finally { picture.UnlockBits(data); }
        return true;
    }

    private static bool Differs(uint a, uint b)
    {
        for (int shift = 0; shift < 32; shift += 8)
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > Tolerance) return true;
        return false;
    }
}
