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
