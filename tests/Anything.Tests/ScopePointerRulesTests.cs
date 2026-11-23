using Anything.Core;
using static Anything.Core.SearchScope;
using Box = Anything.Core.ScopePointerRules.Box;

namespace Anything.Tests;

/// <summary>
/// <see cref="ScopePointerRules"/>: the pointer at the scope chip for those who haven't used it. What it says for each state of the
/// chip, on which starts it shows, and where it goes: under the search box, never over it, never off the screen.
/// </summary>
public sealed class ScopePointerRulesTests
{
    [Theory]
    [InlineData(ScopePointerRules.EscTarget.SearchBox, true)]
    [InlineData(ScopePointerRules.EscTarget.OtherTextBox, false)]
    [InlineData(ScopePointerRules.EscTarget.NativeEdit, false)]
    [InlineData(ScopePointerRules.EscTarget.Other, true)]
    public void EscOnlyClosesThePointerUnlessAnotherEditBoxHasTheKeyboard(ScopePointerRules.EscTarget target, bool onlyThePointer)
        => Assert.Equal(onlyThePointer, ScopePointerRules.EscOnlyClosesPointer(target));

    [Fact]
    public void InAFolderItSaysWhereAndHowToSearchEverything()
    {
        var chip = Describe(everywhere: false, selected: null, folder: "Downloads", zap: null);
        Assert.Equal("Now only in Downloads. Click it to search everything.", ScopePointerRules.Line(chip));
    }

    [Fact]
    public void OnEverythingWithAFolderItSaysHowToGoBack()
    {
        var chip = Describe(everywhere: true, selected: null, folder: "Downloads", zap: null);
        Assert.Equal("Now everywhere. Click it to search only in Downloads.", ScopePointerRules.Line(chip));
        var selected = Describe(everywhere: true, selected: "3 folders", folder: "Projects", zap: null);
        Assert.Equal("Now everywhere. Click it to search only in the selected 3 folders.", ScopePointerRules.Line(selected));
    }

    [Fact]
    public void WithNoFolderItSaysWhatAFolderWouldDo()
    {
        var chip = Describe(everywhere: false, selected: null, folder: null, zap: null);
        Assert.Equal("Everything on this PC. In a folder it shows the folder's name and searches only there.", ScopePointerRules.Line(chip));
    }

    [Fact]
    public void WithFoldersSelectedItSaysTheSelectedFolders()
    {
        var chip = Describe(everywhere: false, selected: "3 folders", folder: "Projects", zap: null);
        Assert.Equal("Now only in the selected 3 folders. Click it to search everything.", ScopePointerRules.Line(chip));
        var one = Describe(everywhere: false, selected: "images", folder: "Pictures", zap: null);
        Assert.Equal("Now only in the selected images. Click it to search everything.", ScopePointerRules.Line(one));
    }

    [Fact]
    public void EveryFileInAFolderSaysThatFolder()
    {
        var chip = Describe(everywhere: false, selected: null, folder: "Downloads", zap: "Downloads");
        Assert.Equal("Now only in Downloads. Click it to search everything.", ScopePointerRules.Line(chip));
    }

    [Theory]
    [InlineData(0, false, false, true, 0, true)]
    [InlineData(1, false, false, true, 0, true)]
    [InlineData(2, false, false, true, 0, false)]  // two starts, or Got it: done
    [InlineData(5, false, false, true, 0, false)]
    [InlineData(0, true, false, true, 0, false)]   // the demo
    [InlineData(0, false, true, true, 0, false)]   // once per start
    [InlineData(0, false, false, false, 0, false)] // the chip can't be seen
    [InlineData(0, false, false, true, 1, false)]  // the chip was used before: they know it
    [InlineData(1, false, false, true, 3, false)]
    public void ItShowsOnTwoStartsOnlyToThoseWhoNeverUsedTheChip(int starts, bool demo, bool shownThisStart, bool chipVisible, int chipUses, bool wanted) =>
        Assert.Equal(wanted, ScopePointerRules.Wanted(starts, demo, shownThisStart, chipVisible, chipUses));

    [Theory]
    [InlineData(false, false, false, 0, ScopePointerRules.Moment.Show)]
    [InlineData(false, false, false, 9, ScopePointerRules.Moment.Show)]   // free at last: it shows
    [InlineData(true, false, false, 0, ScopePointerRules.Moment.Wait)]    // a menu or drop-down is open
    [InlineData(false, true, false, 0, ScopePointerRules.Moment.Wait)]    // a dialog is up
    [InlineData(false, false, true, 0, ScopePointerRules.Moment.Wait)]    // a drag
    [InlineData(true, true, true, 8, ScopePointerRules.Moment.Wait)]      // the ninth moment waits
    [InlineData(true, false, false, 9, ScopePointerRules.Moment.GiveUp)]  // the tenth gives up for this start
    [InlineData(false, false, true, 12, ScopePointerRules.Moment.GiveUp)]
    public void ItNeverShowsUnderAMenuADialogOrADrag(bool menu, bool dialog, bool capture, int tries, ScopePointerRules.Moment expected) =>
        Assert.Equal(expected, ScopePointerRules.WhenDue(menu, dialog, capture, tries));

    [Fact]
    public void ItTriesTenTimesInAll()
    {
        int tries = 0;
        while (ScopePointerRules.WhenDue(menuOpen: true, dialogOpen: false, captured: false, tries) == ScopePointerRules.Moment.Wait) tries++;
        Assert.Equal(ScopePointerRules.Tries, tries + 1);
    }

    // A maximized window on a 1920 x 1080 screen at 125%: the search box at the top right, the taskbar at the bottom.
    static readonly Box Area = new(0, 0, 1920, 1020);
    static readonly Box Field = new(1525, 60, 375, 40);
    static readonly Box Chip = new(1531, 66, 110, 28);
    const int Width = 375, Height = 172, Gap = 5, Inset = 26;

    [Fact]
    public void ItGoesUnderTheSearchBoxPointingAtTheChip()
    {
        var p = ScopePointerRules.Place(Chip, Field, Area, Width, Height, Gap, Inset)!.Value;
        Assert.True(p.Below);
        Assert.Equal(Field.Bottom + Gap, p.Bounds.Y);
        Assert.True(p.Bounds.Inside(Area));
        Assert.False(p.Bounds.Intersects(Field));
        Assert.Equal(Chip.X + Chip.Width / 2, p.Bounds.X + p.ArrowX); // the arrow's tip at the chip's middle
    }

    [Fact]
    public void WithNoRoomUnderTheBoxItGoesOverIt()
    {
        var field = new Box(1525, 950, 375, 40);
        var chip = new Box(1531, 956, 110, 28);
        var p = ScopePointerRules.Place(chip, field, Area, Width, Height, Gap, Inset)!.Value;
        Assert.False(p.Below);
        Assert.Equal(field.Y - Gap - Height, p.Bounds.Y);
        Assert.True(p.Bounds.Inside(Area));
        Assert.False(p.Bounds.Intersects(field));
    }

    [Fact]
    public void WithNoRoomEitherWayItDoesntShow()
    {
        var area = new Box(0, 0, 1920, 200);
        Assert.Null(ScopePointerRules.Place(new Box(1531, 86, 110, 28), new Box(1525, 80, 375, 40), area, Width, Height, Gap, Inset));
    }

    [Fact]
    public void OnAScreenToTheRightItStaysOnThatScreen()
    {
        // A window at the left of a second screen: the callout starts at the screen's edge, its arrow still on the chip.
        var area = new Box(1920, 0, 1920, 1020);
        var field = new Box(1924, 60, 375, 40);
        var chip = new Box(1930, 66, 60, 28);
        var p = ScopePointerRules.Place(chip, field, area, Width, Height, Gap, Inset)!.Value;
        Assert.Equal(1934, p.Bounds.X);
        Assert.True(p.Bounds.Inside(area));
        Assert.Equal(Inset, p.ArrowX);
        Assert.Equal(chip.X + chip.Width / 2, p.Bounds.X + p.ArrowX);
    }
}
