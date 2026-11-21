using Anything.Core;

namespace Anything.Tests;

/// <summary>Back and Forward: each place remembers its name and the item selected when it was left.</summary>
public sealed class NavigationHistoryTests
{
    static NavigationHistory<string>.Entry E(string place, string? item = null) => new(place, place.ToUpperInvariant(), item);

    [Fact]
    public void ANewHistoryGoesNowhere()
    {
        var h = new NavigationHistory<string>();
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
        Assert.Null(h.BackEntry);
        Assert.Null(h.ForwardEntry);
        Assert.Null(h.GoBack(E("here"), _ => true));
        Assert.Null(h.GoForward(E("here"), _ => true));
    }

    [Fact]
    public void BackReturnsWithTheItemThatWasSelected()
    {
        var h = new NavigationHistory<string>();
        h.Visit(E("downloads", "photos"));
        Assert.Equal("DOWNLOADS", h.BackEntry!.Name);

        NavigationHistory<string>.Entry? opened = null;
        var back = h.GoBack(E("photos", "cat.jpg"), e => { opened = e; return true; });

        Assert.Same(opened, back);
        Assert.Equal("downloads", back!.Place);
        Assert.Equal("photos", back.Item);
        Assert.False(h.CanGoBack);
        Assert.Equal("photos", h.ForwardEntry!.Place);
        Assert.Equal("cat.jpg", h.ForwardEntry.Item); // Forward selects what was selected when Back was pressed
    }

    [Fact]
    public void ForwardReturnsAndBackComesHomeAgain()
    {
        var h = new NavigationHistory<string>();
        h.Visit(E("a", "b"));
        h.GoBack(E("b", "file"), _ => true);
        var forward = h.GoForward(E("a", "b"), _ => true);

        Assert.Equal("b", forward!.Place);
        Assert.Equal("file", forward.Item);
        Assert.False(h.CanGoForward);
        Assert.Equal("a", h.BackEntry!.Place);
        Assert.Equal("b", h.BackEntry.Item);
    }

    [Fact]
    public void APlaceLeftWithNothingSelectedSelectsNothing()
    {
        var h = new NavigationHistory<string>();
        h.Visit(E("a"));
        Assert.Null(h.GoBack(E("b"), _ => true)!.Item);
    }

    [Fact]
    public void OpeningANewPlaceDropsForward()
    {
        var h = new NavigationHistory<string>();
        h.Visit(E("a"));
        h.Visit(E("b"));
        h.GoBack(E("c"), _ => true);
        Assert.True(h.CanGoForward);

        h.Visit(E("b"));
        Assert.False(h.CanGoForward);
        Assert.Equal(2, h.BackCount);
    }

    [Fact]
    public void APlaceThatCantOpenLeavesTheHistoryAsItWas()
    {
        var h = new NavigationHistory<string>();
        h.Visit(E("a", "x"));
        h.Visit(E("b", "y"));

        Assert.Null(h.GoBack(E("c", "z"), _ => false));
        Assert.Equal(2, h.BackCount);
        Assert.Equal("b", h.BackEntry!.Place);
        Assert.False(h.CanGoForward);

        h.GoBack(E("c", "z"), _ => true);
        Assert.Null(h.GoForward(E("b", "y"), _ => false));
        Assert.Equal(1, h.ForwardCount);
        Assert.Equal("c", h.ForwardEntry!.Place);
        Assert.Equal("a", h.BackEntry!.Place);
    }

    [Fact]
    public void TheOldestPlacesAreForgotten()
    {
        var h = new NavigationHistory<string>();
        for (int i = 0; i < NavigationHistory<string>.Limit + 5; i++) h.Visit(E("p" + i));
        Assert.Equal(NavigationHistory<string>.Limit, h.BackCount);
        Assert.Equal("p" + (NavigationHistory<string>.Limit + 4), h.BackEntry!.Place);
        for (int i = 0; i < NavigationHistory<string>.Limit - 1; i++) h.GoBack(E("x"), _ => true);
        Assert.Equal("p5", h.BackEntry!.Place); // p0 to p4 went
    }
}
