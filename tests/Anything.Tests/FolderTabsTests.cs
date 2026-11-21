using Anything.Core;

namespace Anything.Tests;

/// <summary>Opening folders in tabs from the folder view and the navigation pane, and the pane's extra roots.</summary>
public sealed class FolderTabsTests
{
    const uint Folder = 0x20000000, Stream = 0x00400000, FileSystem = 0x40000000, Link = 0x00010000;

    [Theory]
    [InlineData(Folder | FileSystem, true)]   // a folder on disk
    [InlineData(Folder, true)]                // This PC, a library, a phone
    [InlineData(Folder | Stream | FileSystem, false)] // a zip file
    [InlineData(FileSystem | Stream, false)]  // a file
    [InlineData(Link | FileSystem | Stream, false)] // a shortcut
    public void OnlyFoldersOpenInATab(uint attributes, bool expected) => Assert.Equal(expected, FolderTabs.CanOpenInTab(attributes));

    [Theory]
    [InlineData(1, Link | FileSystem, true)]
    [InlineData(2, Link | FileSystem, false)]
    [InlineData(0, Link, false)]
    [InlineData(1, Folder | FileSystem, false)]
    public void OnlyOneShortcutOpensItsTarget(int count, uint attributes, bool expected)
        => Assert.Equal(expected, FolderTabs.IsSingleShortcut(count, attributes));

    [Theory]
    [InlineData("Open in new &tab", "Open in new tab")]
    [InlineData("&Open in new tab", "Open in new tab")]
    [InlineData("Open in new tab\tCtrl+T", "Open in new tab")]
    [InlineData("Copy &&paste", "Copy &paste")]
    [InlineData("Properties...", "Properties")]
    [InlineData("  Open with\u2026 ", "Open with")]
    [InlineData(null, "")]
    public void MenuTextReadsAsShown(string? text, string expected) => Assert.Equal(expected, FolderTabs.MenuText(text));

    [Fact]
    public void TheShellsOwnItemIsRecognisedWhateverItsAccessKey()
    {
        Assert.True(FolderTabs.SameMenuText("Open in new &tab", "Open in new tab"));
        Assert.True(FolderTabs.SameMenuText("OPEN IN NEW TAB", "open in new tab"));
        Assert.False(FolderTabs.SameMenuText("Open in new window", "Open in new tab"));
        Assert.False(FolderTabs.SameMenuText("", ""));
        Assert.False(FolderTabs.SameMenuText(null, null));
    }

    // NSTCECLICKTYPE: LBUTTON 1, MBUTTON 2, RBUTTON 3, DBLCLICK 4. NSTCEHITTEST: NOWHERE 1, ICON 2, LABEL 4, INDENT 8,
    // BUTTON 0x10, RIGHT 0x20, STATEICON 0x40.
    [Theory]
    [InlineData(0x2u, 0x4u, MiddleClick.OpenInNewTab)]  // on the name
    [InlineData(0x2u, 0x2u, MiddleClick.OpenInNewTab)]  // on the icon
    [InlineData(0x2u, 0x20u, MiddleClick.OpenInNewTab)] // to the right of the name (the row is selected across)
    [InlineData(0x6u, 0x4u, MiddleClick.Ignore)]        // the second click of a double-click
    [InlineData(0x2u, 0x1u, MiddleClick.None)]          // empty space
    [InlineData(0x2u, 0x10u, MiddleClick.None)]         // the expand chevron
    [InlineData(0x2u, 0x8u, MiddleClick.None)]          // the indent
    [InlineData(0x1u, 0x4u, MiddleClick.None)]          // a left click
    [InlineData(0x3u, 0x4u, MiddleClick.None)]          // a right click
    public void AMiddleClickOnARowOpensATab(uint click, uint hit, MiddleClick expected)
        => Assert.Equal(expected, FolderTabs.ForPaneClick(click, hit));

    [Fact]
    public void ACheckThatAnswersInTimeGivesItsAnswer()
    {
        Assert.True(FolderTabs.Within(() => true, TimeSpan.FromSeconds(10)));
        Assert.False(FolderTabs.Within(() => false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ACheckThatThrowsAnswersFalse()
        => Assert.False(FolderTabs.Within(() => throw new IOException("gone"), TimeSpan.FromSeconds(10)));

    [Fact]
    public void ACheckThatDoesNotAnswerInTimeGivesNoAnswer()
    {
        using var release = new ManualResetEventSlim();
        try { Assert.Null(FolderTabs.Within(() => release.Wait(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(50))); }
        finally { release.Set(); }
    }

    // ---- extra roots ------------------------------------------------------------------------------

    const string Dropbox = "{E31EA727-12ED-4702-820C-4B6445F28E1A}";
    const string Phone = "{B239341B-BCBD-481C-AA57-3BD0E6248AA0}";
    const string GoogleDrive = "{3935FFFA-E6EE-4CAD-AF73-2B4E8A1E5D1C}";
    const string Gallery = "{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    const string Linux = "{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}";
    const string OneDrive = "{018D5C66-4533-4307-9B53-224DE2ED1FE6}";

    static FolderTabs.NameSpaceRoot Root(string key, object? userPinned = null, object? machinePinned = null, object? userSort = null, object? machineSort = null)
        => new(key, userPinned, machinePinned, userSort, machineSort);

    [Fact]
    public void PinnedRootsThePaneDoesNotShowAreAddedInExplorersOrder()
    {
        var roots = FolderTabs.ExtraRoots(
        [
            Root(GoogleDrive, userPinned: 1, userSort: 0x42),
            Root(Phone, userPinned: 1, userSort: 0x41),
            Root(Dropbox, userPinned: 1),
        ], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(Phone), new Guid(GoogleDrive), new Guid(Dropbox)], roots);
    }

    [Fact]
    public void RootsOfTheSameOrderKeepTheOrderTheyAreListedIn()
    {
        var roots = FolderTabs.ExtraRoots([Root(Dropbox, userPinned: 1, userSort: 66), Root(Phone, userPinned: 1, userSort: 66)], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(Dropbox), new Guid(Phone)], roots);
    }

    [Fact]
    public void RootsThePaneAddsItselfAreNotAddedAgain()
    {
        var roots = FolderTabs.ExtraRoots(
        [
            Root(Gallery, machinePinned: 1, machineSort: 65),
            Root(Linux, machinePinned: 1, machineSort: 119),
            Root(OneDrive.ToLowerInvariant(), userPinned: 1, userSort: 66),
            Root(Phone, userPinned: 1, userSort: 66),
        ], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(Phone)], roots);
    }

    [Fact]
    public void RootsThatAreNotPinnedAreLeftOut()
    {
        var roots = FolderTabs.ExtraRoots(
        [
            Root("{4336a54d-038b-4685-ab02-99bb52d3fb8b}", machineSort: 76), // registered, never pinned
            Root(Dropbox, userPinned: 0, userSort: 66),
            Root(GoogleDrive, userPinned: "1"),
        ], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(GoogleDrive)], roots);
    }

    [Fact]
    public void TheUsersValueWinsOverTheMachines()
    {
        Assert.Empty(FolderTabs.ExtraRoots([Root(Dropbox, userPinned: 0, machinePinned: 1)], FolderTabs.PaneRoots));
        Assert.Single(FolderTabs.ExtraRoots([Root(Dropbox, machinePinned: 1)], FolderTabs.PaneRoots));
        var order = FolderTabs.ExtraRoots([Root(Dropbox, machinePinned: 1, userSort: 1, machineSort: 99), Root(Phone, userPinned: 1, userSort: 50)], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(Dropbox), new Guid(Phone)], order);
    }

    [Fact]
    public void ARootListedTwiceIsAddedOnceAndKeysThatAreNotClassesAreSkipped()
    {
        var roots = FolderTabs.ExtraRoots(
        [
            Root("DelegateFolders", userPinned: 1),
            Root(Dropbox, userPinned: 1, userSort: 66),
            Root(Dropbox.ToLowerInvariant(), userPinned: 0),
        ], FolderTabs.PaneRoots);
        Assert.Equal([new Guid(Dropbox)], roots);
    }

    [Theory]
    [InlineData(null, true)]                        // a phone: no folder on disk
    [InlineData(@"C:\Users\Me\Dropbox", true)]
    [InlineData(@"C:\Users\Me\OneDrive", false)]    // OneDrive's folder, already a root
    [InlineData(@"c:\users\me\onedrive\", false)]
    [InlineData(@"\\server\share\Box", false)]      // a network share may not answer
    public void ExtraRootFolders(string? folder, bool expected)
        => Assert.Equal(expected, FolderTabs.IsExtraRootFolder(folder, [@"C:\Users\Me\OneDrive", null]));
}
