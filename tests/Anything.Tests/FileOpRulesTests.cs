using Anything.Core;

namespace Anything.Tests;

/// <summary>When a rename or delete from the results has to let go of the previewed file first.</summary>
public sealed class FileOpRulesTests
{
    [Fact]
    public void ThePreviewedFileItselfIsTouched()
        => Assert.True(FileOpRules.Touches(@"C:\Music\song.mp3", [@"C:\Work\a.txt", @"c:\music\SONG.mp3"]));

    [Fact]
    public void AFileInsideATargetFolderIsTouched()
        => Assert.True(FileOpRules.Touches(@"C:\Music\Album\song.mp3", [@"C:\Music\"]));

    [Fact]
    public void AFolderWhoseNameOnlyStartsTheSameIsNot()
        => Assert.False(FileOpRules.Touches(@"C:\Music2\song.mp3", [@"C:\Music"]));

    [Fact]
    public void NothingPreviewedOrNoTargetsTouchesNothing()
    {
        Assert.False(FileOpRules.Touches(null, [@"C:\Music"]));
        Assert.False(FileOpRules.Touches(@"C:\Music\song.mp3", []));
        Assert.False(FileOpRules.Touches(@"C:\Music\song.mp3", ["", @"\"]));
    }

    [Fact]
    public void APathNotFoundThatIsNotThereHasGone()
    {
        Assert.True(FileOpRules.IsGone(unchecked((int)0x80070002), exists: false));
        Assert.True(FileOpRules.IsGone(unchecked((int)0x80070003), exists: false));
    }

    [Fact]
    public void APathStillThereOrAnotherFailureIsReported()
    {
        Assert.False(FileOpRules.IsGone(unchecked((int)0x80070002), exists: true)); // too long for the shell, say
        Assert.False(FileOpRules.IsGone(unchecked((int)0x80070035), exists: false)); // the network path wasn't found
    }
}
