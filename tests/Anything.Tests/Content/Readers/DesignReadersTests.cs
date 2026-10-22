using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Readers.DesignFixtures;

namespace Anything.Tests.Content.Readers;

public class DesignReadersTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 8)]
    [InlineData(true, 32)]
    public void Psd_and_psb_give_picture_details_layer_names_and_type_layer_text(bool psb, int depth)
    {
        var bytes = Psd(psb, depth);
        var (doc, source) = ReaderRun.Counted(bytes, psb ? "psb" : "psd");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(ContentKind.Design, doc.Kind);
        Assert.Equal("Logo client Ä\nHeadline text\nSecond paragraph of copy", doc.Body);
        var p = doc.Picture!.Value;
        Assert.Equal((2480, 3508), (p.Width, p.Height));
        Assert.Equal(ColorMode.Cmyk, p.ColorMode);
        Assert.Equal(Math.Min(255, depth * 4), p.BitDepth);
        Assert.Equal(300, p.DpiX); // 0x03ED is pixels per inch whatever unit it is shown in
        Assert.Equal("Poster Design", doc.Field(DocField.Title));
        Assert.Equal("poster; print", doc.Field(DocField.Keywords));
        // Neither the thumbnail resource nor the merged image data are read.
        Assert.False(source.Touched(bytes.Length - 4000, bytes.Length));
    }

    [Fact]
    public void Psd_layer_counts_past_the_cap_and_broken_descriptors_are_bounded()
    {
        var many = ReaderRun.Bytes(Psd(layerCount: 1 << 15), "psd");
        Assert.True(many.Body.Split('\n').Length <= 10_000);
        Assert.Contains("L0", many.Body);

        var broken = TypeTool("text");
        broken[^13] = (byte)'?'; // an unknown item type after 'Txt ': the text was already found
        Assert.Equal("text", PsdReader.TypeTool(broken));
        Assert.Null(PsdReader.TypeTool(TypeTool("text")[..60]));
    }

    [Fact]
    public void Indesign_takes_the_documents_packet_not_a_placed_pictures()
    {
        var (doc, source) = ReaderRun.Counted(Indd(), "indd");
        Assert.Equal("Catalog 2024", doc.Field(DocField.Title));
        Assert.Equal("catalog; spring", doc.Field(DocField.Keywords));
        Assert.Equal(12, doc.Pages);
        Assert.True(source.BytesRead <= (9 << 20) + (2 << 20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eps_reads_dsc_comments_and_its_main_packet(bool dos)
    {
        var doc = ReaderRun.Bytes(Eps(dos), "eps");
        Assert.Equal("Spring Poster", doc.Field(DocField.Title));
        Assert.Equal("Adobe Illustrator(R) 24.0", doc.Field(DocField.CreatorTool));
        Assert.Equal("eps keyword", doc.Field(DocField.Keywords));
    }
}
