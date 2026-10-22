using Anything.Core;

namespace Anything.Tests;

/// <summary>The Explorer view's selection: what the 200 ms poll reads again, and which selections a search can look inside.</summary>
public sealed class ShellSelectionTests
{
    const int Big = ShellSelection.FullReadLimit + 1;

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(ShellSelection.FullReadLimit)]
    public void SmallSelectionsAreReadEveryTick(int selected) // so a renamed first item still shows in the preview
        => Assert.False(ShellSelection.CanKeepFirst(selected, 3, false, selected, 3));

    [Theory]
    [InlineData(Big)]
    [InlineData(100_000)]
    public void BigSelectionThatStaysTheSameIsNotReadAgain(int selected)
        => Assert.True(ShellSelection.CanKeepFirst(selected, 7, false, selected, 7));

    [Fact]
    public void BigSelectionIsReadWhenTheCountChanges()
    {
        Assert.False(ShellSelection.CanKeepFirst(100_000, 7, false, 99_999, 7));
        Assert.False(ShellSelection.CanKeepFirst(Big, 7, false, -1, 7)); // just navigated: nothing read yet
    }

    [Fact]
    public void BigSelectionIsReadWhenTheFocusMoves()
    {
        Assert.False(ShellSelection.CanKeepFirst(100_000, 8, false, 100_000, 7));
        Assert.False(ShellSelection.CanKeepFirst(100_000, -1, false, 100_000, 7));
    }

    [Fact]
    public void BigSelectionIsReadWhenTheViewReportsAChange() // same count and focus, another item (Ctrl+click off and on)
        => Assert.False(ShellSelection.CanKeepFirst(100_000, 7, true, 100_000, 7));

    [Fact]
    public void UnknownCountIsAlwaysRead() => Assert.False(ShellSelection.CanKeepFirst(-1, 7, false, -1, 7));

    /// <summary>A view's selected items by index, as the poll reads them one at a time.</summary>
    sealed class View(Dictionary<int, string?> selected)
    {
        public readonly Dictionary<int, string?> Selected = selected;
        public int Reads, FirstSelectedAsked;
        public int FirstSelected() { FirstSelectedAsked++; return Selected.Count == 0 ? -1 : Selected.Keys.Min(); }
        public bool Read(int index, out string? path) { Reads++; return Selected.TryGetValue(index, out path); }
    }

    [Fact]
    public void TheFocusedSelectedItemIsFirst() // DefView lists it first; the rest isn't asked for
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        Assert.Equal(7, ShellSelection.IndexOfFirst(@"C:\pics\b.jpg", 7, view.FirstSelected, view.Read));
        Assert.Equal(0, view.FirstSelectedAsked);
    }

    [Theory]
    [InlineData(5)]  // Ctrl+click took the focused item out of the selection
    [InlineData(-1)] // focus unknown
    public void WithoutAFocusedSelectedItemTheFirstSelectedOneIsFirst(int focused)
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        Assert.Equal(3, ShellSelection.IndexOfFirst(@"C:\pics\a.jpg", focused, view.FirstSelected, view.Read));
    }

    [Fact]
    public void TheFirstPathIsComparedIgnoringCase()
    {
        var view = new View(new() { [7] = @"C:\pics\b.jpg" });
        Assert.Equal(7, ShellSelection.IndexOfFirst(@"c:\PICS\B.JPG", 7, view.FirstSelected, view.Read));
    }

    [Fact]
    public void AFirstItemElsewhereIsNotFound() // the array listed another item first: keep the path, as before
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        Assert.Equal(-1, ShellSelection.IndexOfFirst(@"C:\pics\a.jpg", 7, view.FirstSelected, view.Read));
        Assert.Equal(-1, ShellSelection.IndexOfFirst(@"C:\pics\a.jpg", 7, () => -1, (int i, out string? p) => { p = null; return false; }));
    }

    [Fact]
    public void AVirtualFirstItemIsFoundToo() // no file-system path, inside a zip or on a phone
    {
        var view = new View(new() { [2] = null });
        Assert.Equal(2, ShellSelection.IndexOfFirst(null, 2, view.FirstSelected, view.Read));
    }

    [Fact]
    public void AKeptFirstItemShowsItsNewName() // F2 on the selection, or another program renamed it
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        view.Selected[7] = @"C:\pics\x (1).jpg";
        Assert.True(ShellSelection.TryRefreshFirst(7, @"C:\pics\b.jpg", 7, view.FirstSelected, view.Read, out var first));
        Assert.Equal(@"C:\pics\x (1).jpg", first);
        Assert.Equal(1, view.Reads);
    }

    [Fact]
    public void AKeptFirstItemNoLongerSelectedIsReadInFull()
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        view.Selected.Remove(7);
        Assert.False(ShellSelection.TryRefreshFirst(7, @"C:\pics\b.jpg", 7, view.FirstSelected, view.Read, out var first));
        Assert.Null(first);
    }

    [Fact]
    public void AKeptFirstItemThatMovedIsReadInFull() // no focused item selected, and another selected item now comes first
    {
        var view = new View(new() { [3] = @"C:\pics\a.jpg", [7] = @"C:\pics\b.jpg" });
        Assert.True(ShellSelection.TryRefreshFirst(3, @"C:\pics\a.jpg", 5, view.FirstSelected, view.Read, out var first));
        Assert.Equal(@"C:\pics\a.jpg", first);
        view.Selected[1] = @"C:\pics\new.jpg";
        Assert.False(ShellSelection.TryRefreshFirst(3, @"C:\pics\a.jpg", 5, view.FirstSelected, view.Read, out _));
    }

    [Fact]
    public void AKeptFirstItemOfUnknownIndexKeepsItsPath() // nothing is read
    {
        var view = new View(new() { [7] = @"C:\pics\b.jpg" });
        Assert.True(ShellSelection.TryRefreshFirst(-1, @"C:\pics\old.jpg", 7, view.FirstSelected, view.Read, out var first));
        Assert.Equal(@"C:\pics\old.jpg", first);
        Assert.Equal(0, view.Reads + view.FirstSelectedAsked);
    }

    const uint Folder = ShellSelection.SfgaoFolder, FileSystem = ShellSelection.SfgaoFileSystem, Stream = ShellSelection.SfgaoStream;

    [Fact]
    public void FoldersOnDiskAreFolders() => Assert.True(ShellSelection.AreFolders(Folder | FileSystem, Folder | FileSystem));

    [Fact]
    public void OtherAttributesDontMatter()
        => Assert.True(ShellSelection.AreFolders(Folder | FileSystem | 0x1 /* SFGAO_CANCOPY */, Folder | FileSystem | 0x4));

    [Fact]
    public void AZipIsNotAFolder() // the shell calls it one
        => Assert.False(ShellSelection.AreFolders(Folder | FileSystem | Stream, Folder | FileSystem | Stream));

    [Fact]
    public void OneZipAmongFoldersIsNotAllFolders() // AND drops the zip's stream bit; only OR keeps it
        => Assert.False(ShellSelection.AreFolders(Folder | FileSystem, Folder | FileSystem | Stream));

    [Fact]
    public void AFileAmongFoldersIsNotAllFolders() => Assert.False(ShellSelection.AreFolders(FileSystem, Folder | FileSystem | Stream));

    [Fact]
    public void VirtualFoldersAreNotFoldersOnDisk() // a phone, Control Panel, a folder inside a zip
        => Assert.False(ShellSelection.AreFolders(Folder, Folder | FileSystem));
}
