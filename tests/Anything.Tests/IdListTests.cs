using Anything.Core;

namespace Anything.Tests;

/// <summary>The shell's ID lists as bytes: what's one level up (Up), and the item Up selects there.</summary>
public sealed class IdListTests
{
    /// <summary>An ID list of items with these data bytes: each item is its 2-byte size, then its data; a 2-byte zero ends it.</summary>
    static byte[] List(params byte[][] items)
    {
        var bytes = new List<byte>();
        foreach (var data in items)
        {
            int size = data.Length + 2;
            bytes.Add((byte)size);
            bytes.Add((byte)(size >> 8));
            bytes.AddRange(data);
        }
        bytes.Add(0);
        bytes.Add(0);
        return [.. bytes];
    }

    static readonly byte[] Desktop = [0, 0];
    static readonly byte[] ThisPc = [0x1F, 0x50, 0xE0, 0x4F];
    static readonly byte[] DriveC = [0x2F, (byte)'C', (byte)':', (byte)'\\'];
    static readonly byte[] Users = [0x31, 1, 2, 3, 4, 5, 6, 7];

    [Fact]
    public void TheDesktopHasNothingAbove()
    {
        Assert.True(IdList.IsEmpty(Desktop));
        Assert.Null(IdList.Parent(Desktop));
        Assert.Null(IdList.LastItem(Desktop));
    }

    [Fact]
    public void UpFromAFolderInsideADrive()
    {
        var users = List(ThisPc, DriveC, Users);
        Assert.False(IdList.IsEmpty(users));
        Assert.Equal(List(ThisPc, DriveC), IdList.Parent(users));
        Assert.Equal(List(Users), IdList.LastItem(users));
    }

    [Fact]
    public void UpFromThisPcIsTheDesktop()
    {
        var thisPc = List(ThisPc);
        Assert.Equal(Desktop, IdList.Parent(thisPc));
        Assert.Equal(thisPc, IdList.LastItem(thisPc));
        Assert.True(IdList.IsEmpty(IdList.Parent(thisPc)!));
    }

    [Fact]
    public void ABigItemSizeReadsBothBytes()
    {
        var big = new byte[300];
        big[0] = 0x31;
        var list = List(ThisPc, big);
        Assert.Equal(List(ThisPc), IdList.Parent(list));
        Assert.Equal(List(big), IdList.LastItem(list));
    }

    [Fact]
    public void BytesAfterTheEndingZeroDontCount()
    {
        var list = List(ThisPc, DriveC);
        var padded = list.Concat(new byte[] { 9, 9, 9, 9 }).ToArray();
        Assert.Equal(List(ThisPc), IdList.Parent(padded));
        Assert.Equal(List(DriveC), IdList.LastItem(padded));
    }

    [Theory]
    [InlineData(new byte[0])]                    // nothing at all
    [InlineData(new byte[] { 0 })]               // half an ending
    [InlineData(new byte[] { 6, 0, 1, 2 })]      // an item longer than the bytes
    [InlineData(new byte[] { 4, 0, 1, 2 })]      // an item with no ending after it
    [InlineData(new byte[] { 1, 0, 0, 0 })]      // a size smaller than the size field itself
    public void ABrokenListIsTreatedAsTheTop(byte[] broken)
    {
        Assert.True(IdList.IsEmpty(broken));
        Assert.Null(IdList.Parent(broken));
        Assert.Null(IdList.LastItem(broken));
    }
}
