using Anything.Core;

namespace Anything.Tests;

/// <summary>The command bar's View, Sort, Extract all and New folder, and the status bar's selection size.</summary>
public sealed class CommandBarRulesTests
{
    [Theory]
    [InlineData(CommandBarRules.FvmIcon, 256, 0)]
    [InlineData(CommandBarRules.FvmIcon, 96, 1)]
    [InlineData(CommandBarRules.FvmIcon, 48, 2)]
    [InlineData(CommandBarRules.FvmSmallIcon, 16, 3)]
    [InlineData(CommandBarRules.FvmList, 16, 4)]
    [InlineData(CommandBarRules.FvmDetails, 16, 5)]
    [InlineData(CommandBarRules.FvmTile, 48, 6)]
    [InlineData(CommandBarRules.FvmContent, 32, 7)]
    public void EachLayoutIsMarkedForTheModeItSets(int mode, int size, int index)
    {
        Assert.Equal(index, CommandBarRules.LayoutIndex(mode, size));
        Assert.Equal((mode, size), CommandBarRules.Layouts[index]);
    }

    [Fact]
    public void ZoomedIconsCountAsTheLayoutTheyHaveReached()
    {
        Assert.Equal(2, CommandBarRules.LayoutIndex(CommandBarRules.FvmIcon, 72));   // Medium
        Assert.Equal(1, CommandBarRules.LayoutIndex(CommandBarRules.FvmIcon, 200));  // Large
        Assert.Equal(0, CommandBarRules.LayoutIndex(CommandBarRules.FvmIcon, 512));  // Extra large
        Assert.Equal(1, CommandBarRules.LayoutIndex(CommandBarRules.FvmThumbnail, 96));
    }

    [Fact]
    public void AnUnknownModeMarksNoLayout() => Assert.Equal(-1, CommandBarRules.LayoutIndex(7 /* FVM_THUMBSTRIP */, 96));

    [Fact]
    public void CtrlShiftDigitsPickTheLayoutsInMenuOrder()
    {
        Assert.Equal(0, CommandBarRules.LayoutForDigit(1));
        Assert.Equal(5, CommandBarRules.LayoutForDigit(6));
        Assert.Equal(7, CommandBarRules.LayoutForDigit(8));
        Assert.Equal(-1, CommandBarRules.LayoutForDigit(9));
        Assert.Equal(-1, CommandBarRules.LayoutForDigit(0));
        Assert.Equal("Ctrl+Shift+6", CommandBarRules.LayoutShortcut(5));
    }

    [Fact]
    public void ANewSortColumnStartsInItsOwnDirection()
    {
        Assert.True(CommandBarRules.SortDescending(sameColumn: false, currentDescending: false, columnDefaultDescending: true));   // Date modified
        Assert.False(CommandBarRules.SortDescending(sameColumn: false, currentDescending: true, columnDefaultDescending: false));  // Name after a descending sort
    }

    [Fact]
    public void ChoosingTheSortedColumnAgainTurnsItRound()
    {
        Assert.False(CommandBarRules.SortDescending(sameColumn: true, currentDescending: true, columnDefaultDescending: true));
        Assert.True(CommandBarRules.SortDescending(sameColumn: true, currentDescending: false, columnDefaultDescending: false));
    }

    [Theory]
    [InlineData("Photos.zip", false, true)]
    [InlineData("PHOTOS.ZIP", false, true)]
    [InlineData("backup.7z", true, true)]
    [InlineData("backup.7z", false, false)] // a Windows without archive folders extracts only zips
    [InlineData("src.tar.gz", true, true)]
    [InlineData("game.rar", true, true)]
    [InlineData("setup.cab", true, false)]
    [InlineData("disk.iso", true, false)]
    [InlineData("notes.txt", true, false)]
    [InlineData("zip", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void ExtractAllIsForArchivesWindowsOpens(string? name, bool archiveFolders, bool expected)
        => Assert.Equal(expected, CommandBarRules.CanExtract(name, archiveFolders));

    [Fact]
    public void TheArchiveALocationIsInsideIsFound()
    {
        Assert.Equal(@"C:\x\Photos.zip", CommandBarRules.ArchiveContaining(@"C:\x\Photos.zip", false));
        Assert.Equal(@"C:\x\Photos.zip", CommandBarRules.ArchiveContaining(@"C:\x\Photos.zip\2024\May", false));
        Assert.Equal(@"\\server\share\a.zip", CommandBarRules.ArchiveContaining(@"\\server\share\a.zip\docs", false));
        Assert.Equal(@"D:\b.7z", CommandBarRules.ArchiveContaining(@"D:\b.7z\inner", true));
        Assert.Null(CommandBarRules.ArchiveContaining(@"D:\b.7z\inner", false));
    }

    [Fact]
    public void AnOrdinaryLocationIsInsideNoArchive()
    {
        Assert.Null(CommandBarRules.ArchiveContaining(@"C:\Users\me\Downloads", true));
        Assert.Null(CommandBarRules.ArchiveContaining(@"C:\", true));
        Assert.Null(CommandBarRules.ArchiveContaining("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true));
        Assert.Null(CommandBarRules.ArchiveContaining(@"\\zip.zip\share", true)); // a server's name is not an archive
        Assert.Null(CommandBarRules.ArchiveContaining(null, true));
    }

    [Fact]
    public void NewFoldersGoInFoldersAndLibraries()
    {
        Assert.True(CommandBarRules.CanMakeFolderIn(@"C:\Work", @"C:\Work"));
        Assert.True(CommandBarRules.CanMakeFolderIn(null, @"::{031E4825-7B94-4dc3-B131-E946B44C8DD5}\Documents.library-ms"));
        Assert.False(CommandBarRules.CanMakeFolderIn(null, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")); // This PC
        Assert.False(CommandBarRules.CanMakeFolderIn(null, null));
    }

    [Fact]
    public void FilesAddUpToTheirTotalSize()
    {
        var total = new CommandBarRules.SelectionTotal();
        total.Add(false, 1000);
        total.Add(false, 2500);
        total.Add(false, 0);
        Assert.Equal(3500, total.Bytes);
    }

    [Fact]
    public void AFolderInTheSelectionMeansNoSize()
    {
        var total = new CommandBarRules.SelectionTotal();
        total.Add(false, 1000);
        total.Add(true, null);
        total.Add(false, 1000);
        Assert.True(total.HasFolder);
        Assert.Null(total.Bytes);
    }

    [Fact]
    public void ItemsWithoutASizeShowNoTotal()
    {
        var total = new CommandBarRules.SelectionTotal();
        total.Add(false, null);
        Assert.Null(total.Bytes);
        total.Add(false, 10);
        Assert.Equal(10, total.Bytes);
    }
}
