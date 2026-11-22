using Anything.Core;

namespace Anything.Tests;

/// <summary>The New folder command's rules: the message the shell's word of the new folder comes in, and when its rename starts.</summary>
public sealed class NewFolderRenameTests
{
    [Fact]
    public void EachCommandHasAMessageOfItsOwn() // word still queued for the command before is told apart
    {
        Assert.NotEqual(NewFolderRename.MessageFor(1), NewFolderRename.MessageFor(2));
        Assert.Equal(NewFolderRename.FirstMessage + 1, NewFolderRename.MessageFor(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)] // the count running over
    public void TheMessagesStayInTheirRange(int command) => Assert.True(NewFolderRename.IsMessage(NewFolderRename.MessageFor(command)));

    [Theory]
    [InlineData(0x8000)]      // WM_APP itself
    [InlineData(0x8000 + 0xFF)]
    [InlineData(0x8000 + 0x200)]
    [InlineData(0x0111)]      // WM_COMMAND
    public void OtherMessagesAreNotTheShellsWord(int msg) => Assert.False(NewFolderRename.IsMessage(msg));

    [Theory]
    [InlineData(null)]
    [InlineData(@"E:\work\Documents.library-ms")]
    [InlineData(@"C:\Users\me\AppData\Roaming\Microsoft\Windows\Libraries\PICTURES.LIBRARY-MS")]
    [InlineData(@"::{031E4825-7B94-4DC3-B131-E946B44C8DD5}\Documents.library-ms")]
    public void LibrariesAskForTheirSaveFolder(string? folder) => Assert.True(NewFolderRename.NeedsSaveFolder(folder));

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData(@"\\server\share\projects")]
    [InlineData(@"Z:\")]
    [InlineData(@"C:\work\library-ms")] // a folder named so is a folder
    public void FoldersMakeItWhereTheyAre(string folder) => Assert.False(NewFolderRename.NeedsSaveFolder(folder));

    [Fact]
    public void TheRenameStartsWhereTheCommandLeftTheUser()
        => Assert.True(NewFolderRename.MayStart(keysInView: true, @"C:\work", @"c:\WORK"));

    [Fact]
    public void KeysGoneElsewhereLeaveItAlone() // the search box, the address bar, another tab
        => Assert.False(NewFolderRename.MayStart(keysInView: false, @"C:\work", @"C:\work"));

    [Fact]
    public void AnotherFolderLeavesItAlone() // a same-named item there would be renamed
    {
        Assert.False(NewFolderRename.MayStart(keysInView: true, @"C:\work\sub", @"C:\work"));
        Assert.False(NewFolderRename.MayStart(keysInView: true, null, @"C:\work"));
    }
}
