using Anything.Core;

namespace Anything.Tests;

/// <summary>Copy as path, and which clipboard files count as cut (shown dimmed in the results).</summary>
public sealed class ClipboardRulesTests
{
    [Fact]
    public void CopyAsPathQuotesEachPathOnItsOwnLine()
        => Assert.Equal("\"C:\\Work\\a b.txt\"\r\n\"D:\\Music\"", ClipboardRules.QuotedPaths([@"C:\Work\a b.txt", @"D:\Music"]));

    [Fact]
    public void CopyAsPathOfOneItemHasNoLineBreak()
        => Assert.Equal("\"C:\\Work\\a.txt\"", ClipboardRules.QuotedPaths([@"C:\Work\a.txt"]));

    [Fact]
    public void CopyAsPathLeavesOutEmptyPaths()
    {
        Assert.Equal("", ClipboardRules.QuotedPaths([]));
        Assert.Equal("\"C:\\a\"", ClipboardRules.QuotedPaths(["", @"C:\a", "  "]));
    }

    [Fact]
    public void FilesCutAreCut()
    {
        var cut = ClipboardRules.CutPaths([@"C:\Work\a.txt", @"C:\Work\Sub\"], ClipboardRules.DropEffectMove);
        Assert.Equal(2, cut.Count);
        Assert.Contains(@"c:\work\A.TXT", cut);
        Assert.Contains(@"C:\Work\Sub", cut);
    }

    [Fact]
    public void ADriveCutKeepsItsBackslash()
        => Assert.Contains(@"D:\", ClipboardRules.CutPaths([@"D:\"], ClipboardRules.DropEffectMove));

    [Fact]
    public void FilesCopiedOrWithNoEffectAreNotCut()
    {
        Assert.Empty(ClipboardRules.CutPaths([@"C:\a.txt"], 5)); // DROPEFFECT_COPY | LINK, Explorer's Copy
        Assert.Empty(ClipboardRules.CutPaths([@"C:\a.txt"], 1));
        Assert.Empty(ClipboardRules.CutPaths([@"C:\a.txt"], null));
        Assert.Empty(ClipboardRules.CutPaths(null, ClipboardRules.DropEffectMove));
    }

    [Fact]
    public void TheDropEffectIsTheFirstFourBytes()
    {
        Assert.Equal(2, ClipboardRules.ReadDropEffect(BitConverter.GetBytes(2)));
        Assert.Equal(5, ClipboardRules.ReadDropEffect([5, 0, 0, 0, 9, 9]));
        Assert.Null(ClipboardRules.ReadDropEffect([2, 0]));
    }
}
