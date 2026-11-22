using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// The F1 help window's size at each scaling. The text widths are the help's longest line in its font (Courier New
/// 9.5pt), measured at 125% on a real window and computed for the other scalings; the box's chrome and the frame are
/// the 125% measurements scaled.
/// </summary>
public sealed class TextWindowLayoutTests
{
    // Scale %, the longest help line in pixels.
    public static TheoryData<int, int> Scalings => new()
    {
        { 100, 960 }, { 125, 1200 }, { 150, 1320 }, { 175, 1680 }, { 200, 1920 },
    };

    static int S(int v, int scale) => v * scale / 100;
    static int Chrome(int scale) => S(17, scale) + 3 + S(4, scale); // vertical scroll bar, edit margins, caret
    static (int W, int H) Frame(int scale) => (S(14, scale), S(38, scale));
    static (int W, int H) WorkingArea(int screenW, int screenH, int scale) => (screenW, screenH - S(48, scale)); // taskbar at the bottom

    [Theory]
    [MemberData(nameof(Scalings))]
    public void OnAWideScreenEveryLineShowsWhole(int scale, int text)
    {
        var (fw, fh) = Frame(scale);
        var (aw, ah) = WorkingArea(3840, 2160, scale);
        var (w, h) = TextWindowLayout.ClientSize(text, Chrome(scale), S(600, scale), fw, fh, aw, ah);
        Assert.True(w - Chrome(scale) >= text, $"{scale}%: {w - Chrome(scale)}px of room for a {text}px line");
        Assert.Equal(S(600, scale), h);
    }

    [Theory]
    [MemberData(nameof(Scalings))]
    public void OnAFullHdScreenItFitsTheTextWhenItCanAndNeverOutgrowsTheScreen(int scale, int text)
    {
        var (fw, fh) = Frame(scale);
        var (aw, ah) = WorkingArea(1920, 1080, scale);
        var (w, h) = TextWindowLayout.ClientSize(text, Chrome(scale), S(600, scale), fw, fh, aw, ah);
        Assert.True(w + fw <= aw && h + fh <= ah, $"{scale}%: window {w + fw}x{h + fh} on {aw}x{ah}");
        bool roomForText = text + Chrome(scale) + fw <= aw;
        if (roomForText) Assert.True(w - Chrome(scale) >= text, $"{scale}%: the line is cut although the screen has room");
        else Assert.Equal(aw - fw, w); // as wide as the screen allows; the rest scrolls
    }

    [Fact]
    public void At125PercentItMatchesTheMeasuredWindow()
    {
        // Measured on this PC (1920x1080 at 125%, working area 1920x1020): scroll bar 21, margins 2 + 1, caret 5,
        // frame 18 x 47. The window that came out was 1229 x 750 inside, with 1205px of room for a 1200px line.
        var (w, h) = TextWindowLayout.ClientSize(1200, 21 + 3 + 5, 750, 18, 47, 1920, 1020);
        Assert.Equal((1229, 750), (w, h));
    }

    [Fact]
    public void ASmallScreenGetsAWindowItsSizeThatScrolls()
    {
        var (w, h) = TextWindowLayout.ClientSize(1920, 38, 1200, 28, 76, 1366, 728);
        Assert.Equal((1366 - 28, 728 - 76), (w, h));
    }

    [Fact]
    public void NeverZeroOrLess()
    {
        Assert.Equal((1, 1), TextWindowLayout.ClientSize(500, 20, 400, 30, 60, 10, 10));
    }

    [Fact]
    public void ALineThatFitsExactlyNeedsNoHorizontalScrollBar()
    {
        Assert.False(TextWindowLayout.NeedsHorizontalScroll(1200, 37, 1237));
    }

    [Fact]
    public void OnePixelShortNeedsTheHorizontalScrollBar()
    {
        Assert.True(TextWindowLayout.NeedsHorizontalScroll(1200, 37, 1236));
    }

    [Fact]
    public void AMuchWiderWindowNeedsNoHorizontalScrollBar()
    {
        Assert.False(TextWindowLayout.NeedsHorizontalScroll(960, 24, 3000));
    }
}
