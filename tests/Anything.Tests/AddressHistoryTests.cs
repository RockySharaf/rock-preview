using Anything.Core;

namespace Anything.Tests;

/// <summary>The addresses typed in the address bar: newest first, 25 at most, with File Explorer's own after them.</summary>
public sealed class AddressHistoryTests
{
    [Fact]
    public void TheNewestComesFirst()
    {
        var h = new AddressHistory();
        h.Add(@"C:\one");
        h.Add(@"C:\two");
        Assert.Equal([@"C:\two", @"C:\one"], h.Items);
    }

    [Fact]
    public void TypedAgainItMovesToTheTopInsteadOfShowingTwice()
    {
        var h = new AddressHistory();
        h.Add(@"C:\one");
        h.Add(@"C:\two");
        h.Add(@"c:\ONE\");
        Assert.Equal([@"c:\ONE\", @"C:\two"], h.Items);
    }

    [Fact]
    public void OnlyTwentyFiveAreKept()
    {
        var h = new AddressHistory();
        for (int i = 0; i < 30; i++) h.Add($@"C:\f{i}");
        Assert.Equal(AddressHistory.MaxEntries, h.Items.Count);
        Assert.Equal(@"C:\f29", h.Items[0]);
        Assert.Equal(@"C:\f5", h.Items[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a|b")]
    [InlineData("line\r\nbreak")]
    public void WhatTheSettingsCantHoldIsntKept(string address)
    {
        var h = new AddressHistory();
        Assert.False(h.Add(address));
        Assert.Empty(h.Items);
    }

    [Fact]
    public void AddressesAreTrimmed()
    {
        var h = new AddressHistory();
        h.Add(@"  C:\x  ");
        Assert.Equal([@"C:\x"], h.Items);
    }

    [Fact]
    public void LoadingDropsBlanksRepeatsAndTheOverflow()
    {
        var h = new AddressHistory();
        h.Load([@"C:\a", "", @"c:\A\", @"C:\b", .. Enumerable.Range(0, 40).Select(i => $@"D:\{i}")]);
        Assert.Equal(AddressHistory.MaxEntries, h.Items.Count);
        Assert.Equal([@"C:\a", @"C:\b"], h.Items.Take(2));
    }

    [Fact]
    public void ClearEmptiesTheList()
    {
        var h = new AddressHistory();
        h.Add(@"C:\a");
        h.Clear();
        Assert.Empty(h.Items);
    }

    [Fact]
    public void ItemsIsACopyThatDoesntChangeLater()
    {
        var h = new AddressHistory();
        h.Add(@"C:\a");
        var before = h.Items;
        h.Add(@"C:\b");
        Assert.Equal([@"C:\a"], before);
    }

    [Fact]
    public void TheDriveRootKeepsItsBackslashWhenCompared()
    {
        Assert.True(AddressHistory.Same(@"C:\", @"c:\"));
        Assert.False(AddressHistory.Same(@"C:\", @"C:"));
        Assert.True(AddressHistory.Same(@"C:\Users\", @"C:\users"));
    }

    [Fact]
    public void ExplorersOwnComeAfterRockPreviewsWithoutRepeats()
        => Assert.Equal([@"C:\mine", @"C:\both", @"C:\explorer"],
            AddressHistory.Combine([@"C:\mine", @"C:\both"], [@"c:\BOTH\", @"C:\explorer", " "]));

    [Fact]
    public void ExplorersTypedPathsAreReadInOrderSkippingWhatIsntAString()
    {
        var values = new Dictionary<string, object?>
        {
            ["url1"] = @"C:\newest",
            ["url2"] = 5,              // not a string
            ["url3"] = @"C:\older",
            ["url4"] = "  ",
            ["url5"] = @"c:\NEWEST",   // a repeat
            ["url25"] = @"C:\oldest",
            ["url26"] = @"C:\past the end",
        };
        Assert.Equal([@"C:\newest", @"C:\older", @"C:\oldest"],
            AddressHistory.FromExplorerTypedPaths(name => values.TryGetValue(name, out var v) ? v : null));
    }
}
