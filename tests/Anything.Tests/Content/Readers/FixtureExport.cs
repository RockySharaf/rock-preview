namespace Anything.Tests.Content.Readers;

/// <summary>
/// Manual: writes the fixtures to RP_FIXTURE_OUT (skipped when unset), so Windows' own decoders (WIC, the shell property
/// system, Office, the IFilters) can be pointed at them as an independent check of the writers and readers.
/// </summary>
public class FixtureExport
{
    [Fact]
    public void Export_fixtures()
    {
        var folder = Environment.GetEnvironmentVariable("RP_FIXTURE_OUT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        void Save(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(folder, name), bytes);
        Save("camera.jpg", PictureFixtures.Jpeg());
        Save("cmyk.jpg", PictureFixtures.Jpeg(components: 4, adobeTransform: 2));
        Save("print-ii.tif", PictureFixtures.Tiff(false, photometric: 2, samples: 3));
        Save("print-mm.tif", PictureFixtures.Tiff(true, photometric: 2, samples: 3));
        Save("lake.png", PictureFixtures.Png());
        Save("screen.gif", PictureFixtures.Gif());
        Save("scan.bmp", PictureFixtures.Bmp());
        Save("web.webp", PictureFixtures.WebP());
        Save("phone.heic", PictureFixtures.Heic());
        Save("poster.psd", DesignFixtures.Psd());
        Save("report.docx", OfficeFixtures.Docx());
        Save("budget.xlsx", OfficeFixtures.Xlsx());
        Save("deck.pptx", OfficeFixtures.Pptx());
        Save("plan.odt", OfficeFixtures.Odt());
        Save("sales.ods", OfficeFixtures.Ods());
        Save("message.eml", MailReadersTests.GeneratedMessage());
        Save("message.msg", MailReadersTests.Msg());
    }
}
