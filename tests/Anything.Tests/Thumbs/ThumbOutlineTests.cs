using System.Drawing;
using System.Drawing.Imaging;
using Anything.Preview.Thumbnails;

namespace Anything.Tests.Thumbs;

/// <summary>
/// The grid outlines a page-like picture so it doesn't melt into the white view, but not one with transparent corners
/// (a logo, a sticker): the answer is read once per picture and kept with it.
/// </summary>
public sealed class ThumbOutlineTests
{
    static Bitmap Filled(PixelFormat format, Color fill)
    {
        var bmp = new Bitmap(8, 6, format);
        using var g = Graphics.FromImage(bmp);
        g.Clear(fill);
        return bmp;
    }

    [Fact]
    public void A_picture_with_a_transparent_corner_gets_no_outline()
    {
        using var bmp = Filled(PixelFormat.Format32bppPArgb, Color.White);
        bmp.SetPixel(7, 5, Color.Transparent);
        Assert.True(ThumbSources.CornersSeeThrough(bmp));
    }

    [Fact]
    public void Opaque_pictures_keep_their_outline()
    {
        using var argb = Filled(PixelFormat.Format32bppArgb, Color.White);
        using var rgb = Filled(PixelFormat.Format24bppRgb, Color.White);
        Assert.False(ThumbSources.CornersSeeThrough(argb));
        Assert.False(ThumbSources.CornersSeeThrough(rgb));
    }

    [Fact]
    public void The_answer_is_read_once_per_picture()
    {
        using var bmp = Filled(PixelFormat.Format32bppArgb, Color.White);
        Assert.False(ThumbSources.CornersSeeThrough(bmp));
        // A later change to the pixels isn't read again: painting asks for the remembered answer.
        bmp.SetPixel(0, 0, Color.Transparent);
        Assert.False(ThumbSources.CornersSeeThrough(bmp));
        // Another picture gets its own answer.
        using var other = Filled(PixelFormat.Format32bppArgb, Color.Transparent);
        Assert.True(ThumbSources.CornersSeeThrough(other));
    }
}
