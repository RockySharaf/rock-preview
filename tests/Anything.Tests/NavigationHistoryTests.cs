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

    // ---- search pages carried on entries ----------------------------------------------------------

    static SearchPage Search(string text) => new() { Text = text };
    static NavigationHistory<string>.Entry P(string place, SearchPage page, string? item = null) => new(place, place.ToUpperInvariant(), item, page);

    [Fact]
    public void AnEntryWithoutAPageHasNone()
    {
        Assert.Null(E("a").Page);
        Assert.Equal("A", E("a").Label);
    }

    [Fact]
    public void BackShowsTheSearchAgainAndForwardTheFolderOpenedFromIt()
    {
        // F with "ABC" showing over it, then the folder ABC opened from the results.
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc));

        var back = h.GoBack(E("abc"), _ => true);
        Assert.Equal("f", back!.Place);
        Assert.Same(abc, back.Page);
        Assert.Equal("abc", h.ForwardEntry!.Place);
        Assert.Null(h.ForwardEntry.Page);

        // Forward from the search shown again: the search goes into Back with its folder once more.
        var forward = h.GoForward(P("f", abc), _ => true);
        Assert.Equal("abc", forward!.Place);
        Assert.Equal("f", h.BackEntry!.Place);
        Assert.Same(abc, h.BackEntry.Page);

        // And Back from the folder again shows it again: it survives any number of trips.
        Assert.Same(abc, h.GoBack(E("abc"), _ => true)!.Page);
        Assert.Equal("prev", h.BackEntry!.Place);
    }

    [Fact]
    public void SeveralLevelsComeBackToTheSearch()
    {
        // prev -> F (search for ABC) -> ABC -> sub: Back, Back shows the search; the window's own next Back closes it.
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc));
        h.Visit(E("abc"));

        Assert.Equal("abc", h.GoBack(E("sub"), _ => true)!.Place);
        var search = h.GoBack(E("abc"), _ => true)!;
        Assert.Equal("f", search.Place);
        Assert.Same(abc, search.Page);
        Assert.Equal("prev", h.BackEntry!.Place);
        Assert.Equal(2, h.ForwardCount);

        // Forward, Forward walks the same way back down.
        Assert.Equal("abc", h.GoForward(P("f", abc), _ => true)!.Place);
        Assert.Equal("sub", h.GoForward(E("abc"), _ => true)!.Place);
        Assert.Equal("abc", h.GoBack(E("sub"), _ => true)!.Place);
        Assert.Same(abc, h.GoBack(E("abc"), _ => true)!.Page);
    }

    [Fact]
    public void TwoSearchesEachComeBack()
    {
        var h = new NavigationHistory<string>();
        var first = Search("first");
        var second = Search("second");
        h.Visit(P("a", first));
        h.Visit(P("b", second));

        Assert.Same(second, h.GoBack(E("c"), _ => true)!.Page);
        Assert.Same(first, h.GoBack(P("b", second), _ => true)!.Page);
    }

    [Fact]
    public void LeavingASearchOverTheSameFolderStillMakesAnEntry()
    {
        // "Open file location" on a result inside the folder searched: the same place opens again.
        Assert.True(SearchPageRules.RecordsLeaving(samePlace: true, leavingPage: true));
        Assert.False(SearchPageRules.RecordsLeaving(samePlace: true, leavingPage: false)); // as before: no entry
        Assert.True(SearchPageRules.RecordsLeaving(samePlace: false, leavingPage: false));
        Assert.True(SearchPageRules.RecordsLeaving(samePlace: false, leavingPage: true));

        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc, item: "x.txt"));   // F with its search, then F itself with x.txt selected
        var back = h.GoBack(E("f", "x.txt"), _ => true)!;
        Assert.Equal("f", back.Place);
        Assert.Same(abc, back.Page);
        Assert.Equal("prev", h.BackEntry!.Place); // Back doesn't skip F
    }

    [Fact]
    public void OpeningANewPlaceDropsForwardPages()
    {
        var h = new NavigationHistory<string>();
        h.Visit(P("a", Search("x")));
        h.GoBack(P("b", Search("y")), _ => true);
        Assert.NotNull(h.ForwardEntry!.Page);

        h.Visit(E("a"));
        Assert.False(h.CanGoForward);
        Assert.Null(h.ForwardEntry);
    }

    [Fact]
    public void AStepThatCantOpenKeepsThePagesWhereTheyWere()
    {
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        var here = Search("here");
        h.Visit(P("f", abc));

        Assert.Null(h.GoBack(P("g", here), _ => false));
        Assert.Same(abc, h.BackEntry!.Page);
        Assert.False(h.CanGoForward);

        Assert.NotNull(h.GoBack(P("g", here), _ => true));
        Assert.Null(h.GoForward(P("f", abc), _ => false));
        Assert.Same(here, h.ForwardEntry!.Page);
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void AStepUndoneAfterItStartedPutsThePagesBack()
    {
        // As ExplorerHost.UndoStep does when the folder fails to open after BrowseToObject returned: step the other way
        // with the target as "here".
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc));
        var target = h.GoBack(E("abc"), _ => true)!;

        h.GoForward(target, _ => true);
        Assert.Same(abc, h.BackEntry!.Page);
        Assert.Equal("f", h.BackEntry.Place);
        Assert.False(h.CanGoForward);
        Assert.Equal(2, h.BackCount);
    }

    [Fact]
    public void TheLimitKeepsTheNewestPages()
    {
        var h = new NavigationHistory<string>();
        for (int i = 0; i < NavigationHistory<string>.Limit + 5; i++) h.Visit(P("p" + i, Search("s" + i)));
        Assert.Equal(NavigationHistory<string>.Limit, h.BackCount);
        Assert.Equal("s" + (NavigationHistory<string>.Limit + 4), h.BackEntry!.Page!.Text);
        for (int i = 0; i < NavigationHistory<string>.Limit - 1; i++) h.GoBack(E("x"), _ => true);
        Assert.Equal("s5", h.BackEntry!.Page!.Text); // the oldest five pages went with their places
    }

    [Fact]
    public void TheTooltipNamesTheSearch()
    {
        Assert.Equal("the search for \"ABC\"", P("f", Search("ABC")).Label);
        Assert.Equal("F", E("f").Label);
        Assert.Equal("Downloads", new NavigationHistory<string>.Entry("d", "Downloads", null).Label);
    }

    [Fact]
    public void ABackWhileAnotherIsStillOpeningStartsFromTheOneOpening()
    {
        // Back from ABC to F (search over it) is still opening when Back is pressed again (the window's third press:
        // the second only closed the page). ExplorerHost.Step then takes the entry still opening as "here", not the
        // folder still on screen, which the first Back already put into Forward.
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc));
        var opening = h.GoBack(E("abc"), _ => true)!;

        Assert.Equal("prev", h.GoBack(opening, _ => true)!.Place);
        // Forward walks F (with its search) then ABC, each once.
        Assert.Equal("f", h.ForwardEntry!.Place);
        Assert.Same(abc, h.ForwardEntry.Page);
        Assert.Equal(2, h.ForwardCount);
        Assert.Same(abc, h.GoForward(E("prev"), _ => true)!.Page);
        Assert.Equal("abc", h.GoForward(P("f", abc), _ => true)!.Place);
        Assert.False(h.CanGoForward);
    }

    [Fact]
    public void ForwardWhileABackIsStillOpeningStartsFromTheOneOpening()
    {
        var h = new NavigationHistory<string>();
        var abc = Search("ABC");
        h.Visit(E("prev"));
        h.Visit(P("f", abc));
        var opening = h.GoBack(E("abc"), _ => true)!;

        Assert.Equal("abc", h.GoForward(opening, _ => true)!.Place);
        Assert.Equal("f", h.BackEntry!.Place);
        Assert.Same(abc, h.BackEntry.Page);
        Assert.Equal(2, h.BackCount);
        Assert.False(h.CanGoForward);
    }
}
