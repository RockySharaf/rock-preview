using Anything.Core.Content;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Manual: pictures written by Windows' own WIC encoders (an independent writer; WicOracleProbe.ps1 beside this file
/// sets size, DPI, pixel format and metadata through BitmapMetadata, which uses the same System.Title and System.Comment
/// policies as Explorer's Details tab) are read with our readers and compared. Skipped unless RP_WIC_PICTURES names the
/// folder the script wrote: powershell -File WicOracleProbe.ps1 &lt;folder&gt;.
/// </summary>
public class WicOracleProbe
{
    [Fact]
    public void Pictures_written_by_wic_read_as_written()
    {
        var folder = Environment.GetEnvironmentVariable("RP_WIC_PICTURES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        ExtractedDoc Read(string name) => ReaderRun.Bytes(File.ReadAllBytes(Path.Combine(folder, name)), Path.GetExtension(name).TrimStart('.'));

        var jpg = Read("wic.jpg");
        var p = jpg.Picture!.Value;
        Assert.Equal((640, 480, 24, ColorMode.Rgb), (p.Width, p.Height, (int)p.BitDepth, p.ColorMode));
        Assert.Equal(300, p.DpiX);
        Assert.Equal(6, p.Orientation);
        Assert.Equal("WicMaker", jpg.Field(DocField.CameraMake));
        Assert.Equal("WicModel 9", jpg.Field(DocField.CameraModel));
        Assert.Equal("Wic jpeg title", jpg.Field(DocField.Title));
        Assert.Contains("harbour", jpg.Field(DocField.Keywords));
        Assert.Contains("evening", jpg.Field(DocField.Keywords));
        var local = new DateTime(2023, 7, 14, 15, 30, 12, DateTimeKind.Unspecified);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local).ToFileTimeUtc(), p.DateTakenUtc);
        Assert.True(p.HasGps);
        Assert.Equal(-33_858_333, p.LatitudeMicro);
        Assert.Equal(151_200_000, p.LongitudeMicro);

        var cmyk = Read("wic-cmyk.tif").Picture!.Value;
        Assert.Equal((300, 200, 32, ColorMode.Cmyk, 300), (cmyk.Width, cmyk.Height, (int)cmyk.BitDepth, cmyk.ColorMode, cmyk.DpiX));
        Assert.Equal("Wic tiff title", Read("wic-cmyk.tif").Field(DocField.Title));
        var gray = Read("wic-gray.tif").Picture!.Value;
        Assert.Equal((120, 90, 8, ColorMode.Gray, 150), (gray.Width, gray.Height, (int)gray.BitDepth, gray.ColorMode, gray.DpiX));

        var png = Read("wic.png");
        var pp = png.Picture!.Value;
        Assert.Equal((333, 222, 32, ColorMode.Rgb, 96), (pp.Width, pp.Height, (int)pp.BitDepth, pp.ColorMode, pp.DpiX));
        Assert.Equal("Wic png title", png.Field(DocField.Title));

        var gif = Read("wic.gif").Picture!.Value;
        Assert.Equal((160, 120, ColorMode.Indexed), (gif.Width, gif.Height, gif.ColorMode));
        var bmp = Read("wic.bmp").Picture!.Value;
        Assert.Equal((200, 100, 24, ColorMode.Rgb, 72), (bmp.Width, bmp.Height, (int)bmp.BitDepth, bmp.ColorMode, bmp.DpiX));

        // Title and Comments as Explorer writes them (ImageDescription, XPTitle, XPComment), and a camera's own
        // ImageDescription with a comment added in Explorer.
        var explorer = Read("explorer.jpg");
        Assert.Equal("Beach day", explorer.Field(DocField.Title));
        Assert.Equal("with Anna", explorer.Field(DocField.Comment));
        var camera = Read("camera.jpg");
        Assert.Equal("with Anna", camera.Field(DocField.Comment));
        Assert.Equal("OLYMPUS DIGITAL CAMERA", camera.Field(DocField.Title));
    }
}
