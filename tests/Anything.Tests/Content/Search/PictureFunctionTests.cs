using Anything.Core;
using Anything.Core.Content;
using Anything.Tests.Search;

namespace Anything.Tests.Content.Search;

/// <summary>Every §5 picture function against picture records (META), through the real engine.</summary>
public class PictureFunctionTests
{
    const string Wide = @"C:\Pics\wide.jpg", Rotated = @"C:\Pics\rotated.jpg", Square = @"C:\Pics\square.png", Logo = @"C:\Pics\logo.psd",
        OneAxis = @"C:\Pics\scan.tif", Text = @"C:\Docs\a.txt";

    static PictureInfo Pic(int w, int h, int dpiX, int dpiY, byte bits, ColorMode mode, byte orientation = 1, byte space = 0, byte rating = 0,
        long taken = 0, bool gps = false)
        => new(w, h, dpiX, dpiY, bits, mode, orientation, space, rating, taken, gps, gps ? 48_856_600 : 0, gps ? 2_352_200 : 0, 0, 0, 0, 0);

    readonly ContentFixture _f;

    public PictureFunctionTests() => _f = Build();

    /// <summary>A fixture for one test: fakes here; the integration tests run the same tests on a real ContentStore.</summary>
    private protected virtual ContentFixture NewFixture() => new();

    ContentFixture Build()
    {
        var f = NewFixture();
        f.Doc(Wide, "", [(DocField.Keywords, "beach")], Pic(4000, 3000, 72, 72, 8, ColorMode.Rgb, space: 1, rating: 5,
            taken: TreeBuilder.Ft(2023, 7, 1), gps: true), DocStatus.NoText);
        // Stored 4000 × 3000 but turned a quarter (EXIF orientation 6): shown 3000 × 4000, portrait.
        f.Doc(Rotated, "", null, Pic(4000, 3000, 300, 300, 8, ColorMode.Cmyk, orientation: 6, space: 2, taken: TreeBuilder.Ft(2024, 5, 14, 9)),
            DocStatus.NoText);
        f.Doc(Square, "", null, Pic(1000, 1000, 150, 150, 16, ColorMode.Gray), DocStatus.NoText);
        f.NextSegment();
        f.Doc(Logo, "LOGO text layer", [(DocField.Keywords, "logo")], Pic(2480, 3508, 300, 300, 8, ColorMode.Cmyk), kind: ContentKind.Design);
        f.Doc(OneAxis, "", null, Pic(0, 0, 600, 0, 0, ColorMode.Unknown), DocStatus.NoText);
        f.Doc(Text, "logo design brief");
        f.Publish();
        return f;
    }

    void Is(string query, params string[] expected)
    {
        var r = _f.Run(query);
        Assert.Null(r.Error);
        var actual = r.Ids.Select(_f.Index.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(expected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
            $"[{query}]\n expected: {string.Join(" | ", expected)}\n actual:   {string.Join(" | ", actual)}");
    }

    [Fact] public void Width_and_height_use_the_shown_size()
    {
        Is("width:>=3000", Wide, Rotated);
        Is("width:4000", Wide);
        Is("wide:3000", Rotated);
        Is("height:>3500", Rotated, Logo);
        Is("high:1000", Square);
        Is("width:1000..2500", Square, Logo);
        Is("width:", Wide, Rotated, Square, Logo);
    }

    [Fact] public void Dimensions_compare_both_axes()
    {
        Is("dimensions:4000x3000", Wide);
        Is("dimensions:3000x4000", Rotated);
        Is("dimensions:>=2000x2000", Wide, Rotated, Logo);
        Is("dimension:1000x1000..2000x4000", Square);
        Is("dimensions:x1000", Square);
        Is("dimensions:2480x", Logo);
        Is("dimensions:1000x1000;2480x3508", Square, Logo);
        Is("dimensions:", Wide, Rotated, Square, Logo);
    }

    [Fact] public void Orientation_follows_the_rotation()
    {
        Is("orientation:landscape", Wide);
        Is("orientation:portrait", Rotated, Logo);
        Is("orientation:square", Square);
        Is("orientation:landscape;square", Wide, Square);
    }

    [Fact] public void Bit_depth()
    {
        Is("bitdepth:16", Square);
        Is("bpp:8", Wide, Rotated, Logo);
        Is("bit-depth:>8", Square);
        Is("bitsperpixel:<16", Wide, Rotated, Logo);
    }

    [Fact] public void Dpi_is_the_lower_axis_and_one_known_axis_stands_for_both()
    {
        Is("dpi:<300", Wide, Square);
        Is("dpi:300", Rotated, Logo);
        Is("dpi:72..150", Wide, Square);
        Is("dpi:600", OneAxis);
        Is("dpi:", Wide, Rotated, Square, Logo, OneAxis);
        Is("horizontalresolution:600", OneAxis);
        Is("verticalresolution:600");
        Is("vertical-resolution:>=300", Rotated, Logo);
    }

    [Fact] public void Colour_modes_and_restrictions()
    {
        Is("colormode:cmyk", Rotated, Logo);
        Is("colormode:rgb;gray", Wide, Square);
        Is("colormode:grey", Square);
        Is("colormode:!=cmyk", Wide, Square); // among pictures whose mode is known
        Is("colormode:", Wide, Rotated, Square, Logo);
        Is("cmyk:", Rotated, Logo);
        Is("rgb:", Wide);
        Is("cmyk: logo", Logo);
        Is("cmyk: width:>2000", Rotated, Logo);
        Is("dpi:<300 rgb:", Wide);
    }

    [Fact] public void Plain_words_and_cmyk_together()
    {
        var r = _f.Run("cmyk: logo", ContentFixture.Plain);
        Assert.Equal([Logo], r.Ids.Select(_f.Index.GetFullPath));
        Assert.Equal(1, r.NameMatches);
        var any = _f.Run("logo", ContentFixture.Plain);
        Assert.Equal([Logo, Text], any.Ids.Select(_f.Index.GetFullPath));
        Assert.Equal(1, any.NameMatches);
    }

    [Fact] public void Colour_space_is_exif_never_cmyk()
    {
        Is("colorspace:srgb", Wide);
        Is("colorspace:uncalibrated", Rotated);
        Is("color-space:srgb;uncalibrated", Wide, Rotated);
        Is("colorrepresentation:srgb", Wide);
    }

    [Fact] public void Date_taken_works_like_date_modified()
    {
        Is("datetaken:2023", Wide);
        Is("date-taken:yesterday", Rotated);
        Is("datetaken:2023..2024", Wide, Rotated);
        Is("datetaken:>2023", Rotated);
        Is("datetaken:", Wide, Rotated);
    }

    [Fact] public void Gps_and_rating()
    {
        Is("gps:", Wide);
        var noGps = _f.Run("!gps: file:").Ids.Select(_f.Index.GetFullPath).ToArray();
        Assert.Contains(Rotated, noGps);
        Assert.Contains(Text, noGps);
        Assert.DoesNotContain(Wide, noGps);
        Is("rating:>=4", Wide);
        Is("rating:0", Rotated, Square, Logo, OneAxis);
    }

    [Fact] public void Picture_functions_need_an_index_but_never_error()
    {
        _f.Index.Content = null;
        Is("dpi:<300");
        Is("cmyk:");
        Is("!cmyk: file:", Wide, Rotated, Square, Logo, OneAxis, Text);
    }
}
