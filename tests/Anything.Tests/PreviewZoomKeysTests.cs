using System.Windows.Forms;
using Anything.Preview.UI;
using ZoomKey = Anything.Preview.UI.ImageView.ZoomKey;

namespace Anything.Tests;

/// <summary>A picture in the preview has its own zoom keys, which the preview pane gives it before the window takes them.</summary>
public sealed class PreviewZoomKeysTests
{
    [Theory]
    [InlineData(Keys.Control | Keys.D0, nameof(ZoomKey.Fit))]
    [InlineData(Keys.Control | Keys.NumPad0, nameof(ZoomKey.Fit))]
    [InlineData(Keys.Control | Keys.D1, nameof(ZoomKey.Actual))]
    [InlineData(Keys.Control | Keys.NumPad1, nameof(ZoomKey.Actual))]
    [InlineData(Keys.Control | Keys.Oemplus, nameof(ZoomKey.In))]
    [InlineData(Keys.Control | Keys.Add, nameof(ZoomKey.In))]
    [InlineData(Keys.Control | Keys.OemMinus, nameof(ZoomKey.Out))]
    [InlineData(Keys.Control | Keys.Subtract, nameof(ZoomKey.Out))]
    public void Ctrl_with_0_1_plus_minus_zooms_the_picture(Keys key, string zoom) => Assert.Equal(zoom, ImageView.ZoomKeyOf(key).ToString());

    [Theory]
    [InlineData(Keys.D0)]
    [InlineData(Keys.Oemplus)]
    [InlineData(Keys.Control | Keys.Shift | Keys.D1)] // a thumbnail size, the window's
    [InlineData(Keys.Control | Keys.Alt | Keys.D0)]
    [InlineData(Keys.Control | Keys.D2)] // sort by path, the window's
    [InlineData(Keys.Control | Keys.C)]
    public void Other_keys_stay_the_windows(Keys key) => Assert.Equal(ZoomKey.None, ImageView.ZoomKeyOf(key));

    [Theory]
    [InlineData(Keys.Control | Keys.D0)]
    [InlineData(Keys.Control | Keys.D1)]
    [InlineData(Keys.Control | Keys.Oemplus)]
    [InlineData(Keys.Control | Keys.OemMinus)]
    public void The_pane_gives_the_zoom_keys_to_the_picture_only_while_it_shows_and_has_the_keyboard(Keys key)
    {
        Assert.True(Anything.Preview.PreviewPane.PictureTakesZoomKey(pictureShown: true, pictureFocused: true, key));
        Assert.False(Anything.Preview.PreviewPane.PictureTakesZoomKey(pictureShown: true, pictureFocused: false, key)); // the focus elsewhere
        Assert.False(Anything.Preview.PreviewPane.PictureTakesZoomKey(pictureShown: false, pictureFocused: true, key)); // text or a table shows
    }

    [Fact]
    public void The_pane_leaves_other_keys_to_the_window_even_with_the_picture_focused()
    {
        Assert.False(Anything.Preview.PreviewPane.PictureTakesZoomKey(true, true, Keys.Control | Keys.D2));
        Assert.False(Anything.Preview.PreviewPane.PictureTakesZoomKey(true, true, Keys.Control | Keys.Shift | Keys.D1));
    }

    [Fact]
    public void The_zoom_keys_are_handled_even_before_a_picture_shows()
    {
        using var view = new ImageView();
        Assert.True(view.HandleZoomKey(Keys.Control | Keys.Oemplus));
        Assert.False(view.HandleZoomKey(Keys.Control | Keys.D2));
    }
}
