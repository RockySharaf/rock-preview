using Anything.Core;

namespace Anything.Tests;

/// <summary>Dropping files on a folder in the address bar: refused only when it would put every file where it already is.</summary>
public sealed class DropRulesTests
{
    [Fact]
    public void FilesDroppedOnTheirOwnFolderAreRefused()
        => Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\a.txt", @"C:\Work\b.txt"], @"C:\Work"));

    [Fact]
    public void FilesDroppedOnAParentFolderAreAllowed() // how people move a file up a level
        => Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\Sub\a.txt"], @"C:\Work"));

    [Fact]
    public void AFolderDroppedOnItselfIsRefused()
        => Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\Sub"], @"C:\Work\Sub"));

    [Fact]
    public void AFolderDroppedIntoItsOwnSubfolderIsRefused() // Windows would only say so after the drop, with an error
    {
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Work"], @"C:\Work\Sub"));
        Assert.True(DropRules.IntoItself([@"C:\Work"], @"C:\Work\Sub"));
        Assert.True(DropRules.IntoItself([@"C:\Work"], @"C:\Work\Sub\Deeper"));
        Assert.True(DropRules.IntoItself([@"C:\Work"], @"C:\Work"));
        Assert.True(DropRules.IntoItself([@"c:\work\"], @"C:\WORK\Sub\"));
        Assert.True(DropRules.Refused([@"C:\Work"], @"C:\Work\Sub"));
    }

    [Fact]
    public void AFolderDroppedFurtherUpIsAllowed() // its parent's parent: how people move a folder up two levels
    {
        Assert.False(DropRules.IntoItself([@"C:\A\B\X"], @"C:\A"));
        Assert.False(DropRules.Refused([@"C:\A\B\X"], @"C:\A"));
        Assert.False(DropRules.Refused([@"C:\A\B\X", @"C:\A\B\y.txt"], @"C:\A"));
    }

    [Fact]
    public void ASiblingThatOnlyStartsLikeTheDraggedFolderIsNotInsideIt()
    {
        Assert.False(DropRules.IntoItself([@"C:\Work"], @"C:\Work2\Sub"));
        Assert.False(DropRules.IntoItself([@"C:\Work"], @"C:\Workshop"));
        Assert.False(DropRules.Refused([@"C:\Work"], @"C:\Work2"));
    }

    [Fact]
    public void OneDraggedFolderAboveTheTargetRefusesTheWholeDrop() // Windows would copy the rest, then show an error
        => Assert.True(DropRules.Refused([@"C:\Other\a.txt", @"C:\Work"], @"C:\Work\Sub"));

    [Fact]
    public void IntoItselfDrivesAndNetworkPaths()
    {
        Assert.True(DropRules.IntoItself([@"C:\"], @"C:\Work"));
        Assert.False(DropRules.IntoItself([@"C:\"], @"D:\Work"));
        Assert.True(DropRules.IntoItself([@"\\server\share\docs"], @"\\SERVER\share\docs\2026"));
        Assert.False(DropRules.IntoItself([@"\\server\share\docs"], @"\\server\share"));
        Assert.False(DropRules.IntoItself([], @"C:\Work"));
        Assert.False(DropRules.IntoItself([@"C:\Work"], ""));
    }

    [Fact]
    public void CaseAndTrailingBackslashesDontMatter()
    {
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"c:\work\A.TXT"], @"C:\WORK\"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\Sub\"], @"c:\work"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\Sub\"], @"C:\Work\Sub\\"));
    }

    [Fact]
    public void DriveRoots()
    {
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\a.txt", @"C:\Folder"], @"C:\"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"C:\a.txt"], "C:"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"D:\"], @"D:\")); // a drive onto itself
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\a.txt"], @"D:\"));
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Folder\a.txt"], @"C:\"));
    }

    [Fact]
    public void NetworkPaths()
    {
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"\\server\share\a.txt"], @"\\server\share"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"\\SERVER\Share\a.txt"], @"\\server\share\"));
        Assert.True(DropRules.OnlyIntoItsOwnFolder([@"\\server\share\docs\a.txt"], @"\\server\share\docs"));
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"\\server\share\docs\a.txt"], @"\\server\share"));
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"\\server\share\a.txt"], @"\\other\share"));
    }

    [Fact]
    public void OneFileFromElsewhereAllowsTheDrop()
        => Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\a.txt", @"C:\Other\b.txt", @"C:\Work\c.txt"], @"C:\Work"));

    [Fact]
    public void ADropWithNoFilesIsAllowed() // Outlook's mails and other data with no file paths
        => Assert.False(DropRules.OnlyIntoItsOwnFolder([], @"C:\Work"));

    [Fact]
    public void ANameThatOnlyStartsLikeTheFolderIsElsewhere()
    {
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Work2\a.txt"], @"C:\Work"));
        Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Workshop"], @"C:\Work"));
    }

    [Fact]
    public void NoFolderMeansNothingToRefuse()
        => Assert.False(DropRules.OnlyIntoItsOwnFolder([@"C:\Work\a.txt"], ""));

    [Fact]
    public void RefusedCoversBothRules()
    {
        Assert.True(DropRules.Refused([@"C:\Work\a.txt", @"C:\Work\b.txt"], @"C:\Work"));     // onto their own folder
        Assert.True(DropRules.Refused([@"C:\Work"], @"C:\Work\Sub"));                          // a folder into itself
        Assert.False(DropRules.Refused([@"C:\Work\Sub\a.txt"], @"C:\Work"));                   // up a level
        Assert.False(DropRules.Refused([], @"C:\Work"));                                       // Outlook's mails
        Assert.False(DropRules.Refused([@"C:\Work\a.txt"], ""));
    }
}
