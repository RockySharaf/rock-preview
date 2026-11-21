using Anything.Core;

namespace Anything.Tests;

/// <summary>"/select,&lt;path&gt;" and "-select &lt;path&gt;" on the command line, and which folder opens with what selected.</summary>
public sealed class ItemSelectionTests
{
    static (bool Read, string? Path, int Index) Read(params string[] args)
    {
        int i = 0;
        bool read = ItemSelection.TryRead(args, ref i, out var path);
        return (read, path, i);
    }

    [Fact]
    public void ExplorersSyntaxGivesThePathAfterTheComma()
        => Assert.Equal((true, @"C:\dir\file.txt", 0), Read(@"/select,C:\dir\file.txt"));

    [Fact]
    public void RockPreviewsSyntaxTakesTheNextArgument()
        => Assert.Equal((true, @"C:\dir\file.txt", 1), Read("-select", @"C:\dir\file.txt"));

    [Fact]
    public void ASpaceAfterTheCommaTakesTheNextArgument()
        => Assert.Equal((true, @"C:\a b\c.txt", 1), Read("/select,", @"C:\a b\c.txt"));

    [Fact]
    public void QuotesThatReachTheArgumentAreTakenOff()
        => Assert.Equal((true, @"C:\a b\c.txt", 0), Read("/select,\"C:\\a b\\c.txt\""));

    [Fact]
    public void ACommaInsideThePathStaysInThePath()
        => Assert.Equal((true, @"C:\one, two\file.txt", 0), Read(@"/select,C:\one, two\file.txt"));

    [Fact]
    public void ATrailingBackslashEscapedIntoAQuoteComesBack()
        => Assert.Equal((true, @"C:\dir\", 0), Read("/select,C:\\dir\""));

    [Fact]
    public void CaseAndDashesDontMatter()
    {
        Assert.Equal((true, @"C:\x", 0), Read(@"/SELECT,C:\x"));
        Assert.Equal((true, @"C:\x", 1), Read("--select", @"C:\x"));
    }

    [Fact]
    public void NoPathIsReadButGivesNothing()
    {
        Assert.Equal((true, null, 0), Read("-select"));
        Assert.Equal((true, null, 0), Read("/select,"));
    }

    [Fact]
    public void AnUnquotedPathWithSpacesAfterTheCommaIsJoinedAgainAsExplorerReadsIt()
    {
        Assert.Equal((true, @"C:\Program Files\Notepad++\notepad++.exe", 1), Read(@"/select,C:\Program", @"Files\Notepad++\notepad++.exe"));
        Assert.Equal((true, @"C:\a b c\d.txt", 3), Read("/select,", @"C:\a", "b", @"c\d.txt"));
    }

    [Fact]
    public void JoiningStopsAtTheNextSwitch()
        => Assert.Equal((true, @"C:\a b\c.txt", 1), Read(@"/select,C:\a", @"b\c.txt", "-newwindow"));

    [Fact]
    public void RockPreviewsSyntaxTakesOnlyTheNextArgument()
        => Assert.Equal((true, @"C:\dir\file.txt", 1), Read("-select", @"C:\dir\file.txt", "invoice"));

    [Fact]
    public void AnotherSwitchIsNeverTakenAsThePath()
    {
        Assert.Equal((true, null, 0), Read("-select", "-newwindow"));
        Assert.Equal((true, null, 0), Read("/select,", "/newwindow"));
    }

    [Theory]
    [InlineData("-selected")]
    [InlineData("-search")]
    [InlineData("select,C:\\x")]
    [InlineData("")]
    public void OtherArgumentsArentASelect(string arg) => Assert.Equal((false, null, 0), Read(arg, "next"));

    // ---- which folder opens ----------------------------------------------------------------------

    static Func<string, bool> Existing(params string[] paths)
        => p => paths.Any(x => string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AFileOpensItsFolderWithItSelected()
        => Assert.Equal(new ItemSelection.Target(@"C:\dir", @"C:\dir\file.txt"),
            ItemSelection.Resolve(@"C:\dir\file.txt", Existing(@"C:\", @"C:\dir", @"C:\dir\file.txt")));

    [Fact]
    public void AFolderIsSelectedInTheFolderAboveIt()
        => Assert.Equal(new ItemSelection.Target(@"C:\dir", @"C:\dir\sub"),
            ItemSelection.Resolve(@"C:\dir\sub\", Existing(@"C:\", @"C:\dir", @"C:\dir\sub")));

    [Fact]
    public void ACommaInThePathIsPartOfTheName()
        => Assert.Equal(new ItemSelection.Target(@"C:\one, two", @"C:\one, two\a.txt"),
            ItemSelection.Resolve(@"C:\one, two\a.txt", Existing(@"C:\", @"C:\one, two", @"C:\one, two\a.txt")));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"C:\\")]
    public void ADriveRootOpensItselfWithNothingSelected(string path)
        => Assert.Equal(new ItemSelection.Target(@"C:\", null), ItemSelection.Resolve(path, Existing(@"C:\")));

    [Fact]
    public void AShareRootOpensItself()
    {
        var target = ItemSelection.Resolve(@"\\server\share\", Existing(@"\\server\share"));
        Assert.NotNull(target);
        Assert.Equal(@"\\server\share", target.Value.Folder.TrimEnd('\\'), ignoreCase: true);
        Assert.Null(target.Value.Select);
    }

    [Fact]
    public void AFileOnAShareOpensTheShare()
        => Assert.Equal(new ItemSelection.Target(@"\\server\share\docs", @"\\server\share\docs\a.txt"),
            ItemSelection.Resolve(@"\\server\share\docs\a.txt", Existing(@"\\server\share\docs", @"\\server\share\docs\a.txt")));

    [Fact]
    public void AMissingItemOpensTheNearestFolderThereWithNothingSelected()
        => Assert.Equal(new ItemSelection.Target(@"C:\dir", null),
            ItemSelection.Resolve(@"C:\dir\gone\deeper\file.txt", Existing(@"C:\", @"C:\dir")));

    [Fact]
    public void NothingThereAtAllGivesNothing()
        => Assert.Null(ItemSelection.Resolve(@"Q:\dir\file.txt", Existing(@"C:\")));

    [Theory]
    [InlineData("file.txt")]
    [InlineData("shell:Downloads")]
    [InlineData("")]
    public void APathThatIsntFullGivesNothing(string path) => Assert.Null(ItemSelection.Resolve(path, _ => true));

    [Fact]
    public void ForwardSlashesWork()
        => Assert.Equal(new ItemSelection.Target(@"C:\dir", @"C:\dir\file.txt"),
            ItemSelection.Resolve("C:/dir/file.txt", Existing(@"C:\", @"C:\dir", @"C:\dir\file.txt")));
}
