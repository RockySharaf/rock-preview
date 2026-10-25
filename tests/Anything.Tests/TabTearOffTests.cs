using System.Drawing;
using Anything.Core;

namespace Anything.Tests;

/// <summary>Dragging tabs out of a window and into another: when a tab leaves its strip, where it goes, which strip takes it.</summary>
public sealed class TabTearOffTests
{
    static readonly Rectangle Strip = new(100, 50, 600, 41); // a window's strip on screen
    const int Margin = TabTearOff.DetachMargin;

    [Theory]
    [InlineData(400, 70)]                // on a tab
    [InlineData(400, 50 + 41 + Margin - 1)] // down into the address row, not far enough yet
    [InlineData(400, 50 - Margin)]       // just above the window
    [InlineData(100 - Margin, 70)]       // just past the left end
    [InlineData(700 + Margin - 1, 70)]   // just past the right end
    public void ATabDraggedAlongOrJustPastTheStripStaysInIt(int x, int y)
        => Assert.False(TabTearOff.LeavesStrip(Strip, new Point(x, y), Margin));

    [Theory]
    [InlineData(400, 50 + 41 + Margin)]  // down, past the margin
    [InlineData(400, 50 - Margin - 1)]   // up
    [InlineData(100 - Margin - 1, 70)]   // out of the left end
    [InlineData(700 + Margin, 70)]       // out of the right end
    [InlineData(1500, 900)]              // far away
    public void ATabDraggedPastTheMarginLeavesTheStrip(int x, int y)
        => Assert.True(TabTearOff.LeavesStrip(Strip, new Point(x, y), Margin));

    [Theory]
    [InlineData(8, 0)]
    [InlineData(127, 0)]
    [InlineData(128, 1)]
    [InlineData(367, 2)]
    [InlineData(368, 3)]
    [InlineData(1000, 3)] // past the last tab: the last slot
    [InlineData(-50, 0)]  // before the first: the first
    public void SlotsAreTabWidthsFromTheFirstTab(int x, int slot)
        => Assert.Equal(slot, TabTearOff.SlotAt(x, left: 8, tabWidth: 120, slots: 4));

    [Fact]
    public void ATabComingInCanTakeTheSlotAfterTheLastTab()
    {
        // Three tabs 120 wide, a fourth coming in: the strip makes room for four, so the end of it is the fourth slot.
        Assert.Equal(3, TabTearOff.SlotAt(8 + 3 * 120 + 5, left: 8, tabWidth: 120, slots: 3 + 1));
        Assert.Equal(0, TabTearOff.SlotAt(0, left: 8, tabWidth: 0, slots: 0)); // no tabs, no width: still a slot
    }

    [Fact]
    public void ATabIsHeldAtTheSamePlaceWhenDrawnWider()
    {
        var narrow = new Rectangle(200, 58, 80, 32);
        var hold = TabTearOff.HoldAt(narrow, new Point(220, 66)); // a quarter across, a quarter down
        Assert.Equal(0.25f, hold.X, 3);
        Assert.Equal(0.25f, hold.Y, 3);
        var wide = new Rectangle(8, 8, 240, 32); // the same tab, alone in a new window
        Assert.Equal(new Point(68, 16), TabTearOff.HeldPoint(wide, hold));
    }

    [Fact]
    public void AHoldOutsideTheTabIsKeptOnIt()
    {
        var tab = new Rectangle(0, 0, 100, 30);
        var hold = TabTearOff.HoldAt(tab, new Point(150, -10));
        Assert.Equal(new PointF(1, 0), hold);
        Assert.Equal(new PointF(0, 0), TabTearOff.HoldAt(Rectangle.Empty, new Point(5, 5)));
    }

    [Fact]
    public void TheWindowMovesSoThatTheHeldPointIsUnderThePointer()
    {
        var window = new Point(300, 200);
        var held = new Point(360, 216);
        var to = TabTearOff.FollowPointer(window, held, new Point(500, 400));
        Assert.Equal(new Point(440, 384), to);
        Assert.Equal(window, TabTearOff.FollowPointer(window, held, held)); // already there: it stays
    }

    static TabTearOff.Layer Ours(Rectangle bounds, Rectangle strip) => new(bounds, strip, Ignored: false);
    static TabTearOff.Layer Other(Rectangle bounds) => new(bounds, null, Ignored: false);
    static TabTearOff.Layer Passed(Rectangle bounds) => new(bounds, null, Ignored: true);

    static readonly Rectangle Window = new(100, 50, 600, 400);

    [Fact]
    public void TheStripOfTheWindowOnTopTakesTheTab()
    {
        var layers = new[] { Other(new Rectangle(900, 0, 300, 300)), Ours(Window, Strip) };
        Assert.Equal(1, TabTearOff.StripUnder(layers, new Point(300, 70)));
    }

    [Fact]
    public void TheDraggedWindowAndHiddenOnesArePassedOver()
    {
        // The window being dragged is on top, right under the pointer; a click-through overlay too.
        var layers = new[] { Passed(new Rectangle(250, 40, 600, 400)), Passed(new Rectangle(0, 0, 2000, 2000)), Ours(Window, Strip) };
        Assert.Equal(2, TabTearOff.StripUnder(layers, new Point(300, 70)));
    }

    [Fact]
    public void AnotherProgramCoveringTheStripKeepsTheTabOut()
    {
        var layers = new[] { Other(new Rectangle(250, 0, 300, 300)), Ours(Window, Strip) };
        Assert.Equal(-1, TabTearOff.StripUnder(layers, new Point(300, 70)));
        Assert.Equal(1, TabTearOff.StripUnder(layers, new Point(650, 70))); // the part of the strip it leaves showing
    }

    [Fact]
    public void ElsewhereOnTheWindowNoStripTakesTheTab()
    {
        // A window a little higher, on top of the other: just under its strip, the pointer is over the other one's strip.
        var layers = new[] { Ours(new Rectangle(100, 20, 600, 400), new Rectangle(100, 20, 600, 41)), Ours(Window, Strip) };
        Assert.Equal(-1, TabTearOff.StripUnder(layers, new Point(300, 80))); // the top window is in the way
        Assert.Equal(0, TabTearOff.StripUnder(layers, new Point(300, 40)));
        Assert.Equal(-1, TabTearOff.StripUnder([Ours(Window, Strip)], new Point(300, 200))); // its files, not its strip
        Assert.Equal(-1, TabTearOff.StripUnder([], new Point(300, 70)));
    }
}
