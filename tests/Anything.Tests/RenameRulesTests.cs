using Anything.Core;

namespace Anything.Tests;

/// <summary>Renaming results in place: File Explorer's checks and selections.</summary>
public sealed class RenameRulesTests
{
    [Theory]
    [InlineData('\\')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    [InlineData('\n')]
    [InlineData('\t')]
    [InlineData('\x7f')] // what an edit box types for Ctrl+Backspace
    public void TheCharactersExplorerRefusesAreForbidden(char c) => Assert.True(RenameRules.IsForbidden(c));

    [Theory]
    [InlineData('a')]
    [InlineData('.')]
    [InlineData(' ')]
    [InlineData('-')]
    [InlineData('(')]
    [InlineData('é')]
    public void OrdinaryCharactersAreAllowed(char c) => Assert.False(RenameRules.IsForbidden(c));

    [Fact]
    public void PastedTextLosesOnlyTheForbiddenCharacters()
    {
        Assert.Equal("report final.txt", RenameRules.RemoveForbidden("re:port* final?.txt"));
        Assert.Equal("ab", RenameRules.RemoveForbidden("a\r\nb"));
        Assert.Equal("plain.txt", RenameRules.RemoveForbidden("plain.txt"));
    }

    [Theory]
    [InlineData("report.txt", ".txt")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("README", "")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("v1.2 notes", "")] // a space after the dot: not an extension (PathFindExtension)
    [InlineData("name.", ".")]
    public void TheExtensionIsFoundAsWindowsFindsIt(string name, string extension)
        => Assert.Equal(extension, RenameRules.Extension(name));

    [Theory]
    [InlineData("report.txt", false, 6)]
    [InlineData("archive.tar.gz", false, 11)]
    [InlineData("README", false, 6)]
    [InlineData(".gitignore", false, 10)]
    [InlineData("My.Folder", true, 9)]
    public void TheEditStartsWithTheBaseNameSelected(string name, bool isDirectory, int length)
        => Assert.Equal(length, RenameRules.BaseNameLength(name, isDirectory));

    [Fact]
    public void F2GoesFromTheBaseNameToAllToTheExtensionAndRound()
    {
        var s = RenameRules.NextSelection("report.txt", false, 0, 6);
        Assert.Equal((0, 10), s);
        s = RenameRules.NextSelection("report.txt", false, s.Start, s.Length);
        Assert.Equal((6, 4), s);
        s = RenameRules.NextSelection("report.txt", false, s.Start, s.Length);
        Assert.Equal((0, 6), s);
    }

    [Fact]
    public void F2OnAFolderGoesBetweenTheNameAndTheEnd()
    {
        Assert.Equal((6, 0), RenameRules.NextSelection("Photos", true, 0, 6));
        Assert.Equal((0, 6), RenameRules.NextSelection("Photos", true, 6, 0));
    }

    [Fact]
    public void F2AfterTheUserMovedTheCaretStartsAgainAtTheBaseName()
        => Assert.Equal((0, 6), RenameRules.NextSelection("report.txt", false, 3, 0));

    [Theory]
    [InlineData("  new name.txt ", "new name.txt")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("..", null)]
    public void TheTypedNameIsTrimmedAndAnEmptyOneLeavesTheNameAlone(string typed, string? expected)
        => Assert.Equal(expected, RenameRules.Normalize(typed));

    [Theory]
    [InlineData("a.txt", "b.txt", false, false)]
    [InlineData("a.txt", "a.TXT", false, false)] // only the case: Explorer doesn't ask
    [InlineData("a.txt", "a.md", false, true)]
    [InlineData("a.txt", "a", false, true)]
    [InlineData("README", "README.md", false, true)]
    [InlineData("Old.Folder", "New.Thing", true, false)] // folders have no extension to lose
    public void AChangedExtensionIsAskedAbout(string oldName, string newName, bool isDirectory, bool asked)
        => Assert.Equal(asked, RenameRules.ExtensionChanged(oldName, newName, isDirectory));

    [Fact]
    public void TabGoesToTheNextItemAndStopsAtTheEnds()
    {
        Assert.Equal(4, RenameRules.Step(3, 1, 10));
        Assert.Equal(2, RenameRules.Step(3, -1, 10));
        Assert.Equal(-1, RenameRules.Step(9, 1, 10));
        Assert.Equal(-1, RenameRules.Step(0, -1, 10));
        Assert.Equal(-1, RenameRules.Step(3, 0, 10));
    }

    [Theory]
    [InlineData("holiday photos.jpg", 18, 0, "holiday ", 8)] // the word before the caret
    [InlineData("holiday photos  ", 16, 0, "holiday ", 8)]   // with the spaces after it
    [InlineData("holiday photos", 7, 0, " photos", 0)]       // only what is before the caret
    [InlineData("holiday photos", 0, 0, "holiday photos", 0)]
    [InlineData("holiday photos", 2, 5, "ho photos", 2)]     // a selection is taken out instead
    public void CtrlBackspaceDeletesThePreviousWord(string text, int start, int length, string expected, int caret)
        => Assert.Equal((expected, caret), RenameRules.DeletePreviousWord(text, start, length));

    [Fact]
    public void TheDelCharacterIsTakenOutOfPastedText() => Assert.Equal("ab", RenameRules.RemoveForbidden("a\x7f" + "b"));
}
