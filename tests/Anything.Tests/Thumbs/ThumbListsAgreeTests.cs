using Anything.Core.Thumbs;
using Anything.Preview;

namespace Anything.Tests.Thumbs;

/// <summary>
/// The thumbnail rules (ThumbKinds) keep their own lists of kinds, which must agree with the preview's: a kind added to
/// one and not the other would silently get an icon instead of a page, or a page Windows should have drawn.
/// </summary>
public sealed class ThumbListsAgreeTests
{
    [Fact]
    public void Every_text_kind_of_the_preview_is_a_text_page_or_has_no_page_on_purpose()
    {
        foreach (var ext in TextPreview.KnownTextExtensions)
        {
            bool page = ThumbKinds.DrawsText(ext), none = ThumbKinds.NoTextPage(ext);
            Assert.True(page != none, ext); // exactly one of the two
            Assert.Equal(page ? ThumbRoute.OwnExtractor : ThumbRoute.Shell, ThumbKinds.RouteOf(ext));
        }
    }

    [Fact]
    public void Every_Office_kind_and_font_of_the_preview_is_known_to_the_thumbnail_rules()
    {
        // New Office and OpenDocument files, old Word and Excel files and fonts: Rock Preview draws them itself.
        var own = OfficePreview.WordExt.Concat(OfficePreview.ExcelExt).Concat(OfficePreview.PowerPointExt).Concat(OfficePreview.OdfExt)
            .Concat(LegacyOfficePreview.WordExt).Concat(LegacyOfficePreview.ExcelExt).Concat(FontPreview.Extensions);
        foreach (var ext in own)
        {
            Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(ext));
            Assert.False(ThumbKinds.NoTextPage(ext), ext);
        }
        // Old PowerPoint files: only Windows pictures them (the slide saved in the file), outlined as a page.
        foreach (var ext in LegacyOfficePreview.PowerPointExt)
        {
            Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(ext));
            Assert.True(ThumbKinds.IsPageLike(ext), ext);
        }
    }
}
