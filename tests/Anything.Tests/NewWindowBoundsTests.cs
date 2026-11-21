using System.Drawing;
using Anything.Core;

namespace Anything.Tests;

/// <summary>Where a new window opens: cascaded from the window it came from, or moved onto the monitor it was opened on.</summary>
public sealed class NewWindowBoundsTests
{
    static readonly Rectangle Left = new(0, 0, 1920, 1040);      // a monitor's working area (taskbar below)
    static readonly Rectangle Right = new(1920, 0, 2560, 1400);  // a larger one to its right
    const int Step = NewWindowBounds.CascadeStep;

    [Fact]
    public void AWindowFromAnotherSitsAStepDownAndRight()
        => Assert.Equal(new Rectangle(100 + Step, 80 + Step, 1000, 700), NewWindowBounds.Cascade(new Rectangle(100, 80, 1000, 700), Left, Step));

    [Fact]
    public void ACascadeThatWouldRunOffTheMonitorStartsAgainFromTheTopLeft()
    {
        var b = NewWindowBounds.Cascade(new Rectangle(900, 400, 1020, 640), Left, Step);
        Assert.Equal(new Rectangle(0, 0, 1020, 640), b); // at the bottom-right corner: it starts again, not on top of it
        Assert.True(Left.Contains(b));
    }

    [Fact]
    public void ACascadeStartsAgainOnlyOnTheSideThatRunsOff()
    {
        var b = NewWindowBounds.Cascade(new Rectangle(100, 400, 1000, 640), Left, Step);
        Assert.Equal(new Rectangle(100 + Step, 0, 1000, 640), b);
    }

    [Fact]
    public void AWindowFillingTheMonitorWithoutBeingMaximizedIsNotCoveredExactly()
    {
        var b = NewWindowBounds.Cascade(Left, Left, Step);
        Assert.NotEqual(Left, b);
        Assert.Equal(new Rectangle(Step, Step, 1920 - Step, 1040 - Step), b);
        Assert.True(Left.Contains(b));
    }

    [Fact]
    public void ACascadeFromAWindowOnTheSecondMonitorStaysThere()
    {
        var b = NewWindowBounds.Cascade(new Rectangle(2000, 100, 1200, 800), Right, Step);
        Assert.Equal(new Rectangle(2000 + Step, 100 + Step, 1200, 800), b);
    }

    [Fact]
    public void SavedBoundsOnAnotherMonitorMoveOntoTheLaunchMonitorKeepingTheirSize()
    {
        var b = NewWindowBounds.MoveOnto(new Rectangle(2100, 150, 1000, 700), Right, Left);
        Assert.Equal(new Rectangle(180, 150, 1000, 700), b); // as far from the corner as before
    }

    [Fact]
    public void BoundsMovedOntoASmallerMonitorArePushedInside()
    {
        var b = NewWindowBounds.MoveOnto(new Rectangle(3000, 900, 1200, 400), Right, Left);
        Assert.Equal(new Rectangle(1920 - 1200, 1040 - 400, 1200, 400), b);
        Assert.True(Left.Contains(b));
    }

    [Fact]
    public void AWindowLargerThanTheMonitorIsMadeToFit()
    {
        var b = NewWindowBounds.MoveOnto(new Rectangle(1920, 0, 2560, 1400), Right, Left);
        Assert.Equal(Left, b);
    }

    [Fact]
    public void BoundsAboveOrLeftOfTheAreaArePushedIn()
        => Assert.Equal(new Rectangle(0, 0, 800, 600), NewWindowBounds.Clamp(new Rectangle(-50, -30, 800, 600), Left));
}
