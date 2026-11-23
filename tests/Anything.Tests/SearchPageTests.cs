using Anything.Core;

namespace Anything.Tests;

/// <summary>A search kept in Back and Forward, and the window's decisions around it (SearchPageRules).</summary>
public sealed class SearchPageTests
{
    // ---- what the tooltips call a page ------------------------------------------------------------

    [Fact]
    public void ASearchIsNamedByWhatWasTyped()
    {
        Assert.Equal("the search for \"ABC\"", new SearchPage { Text = "ABC" }.Label);
    }

    [Fact]
    public void EveryFileInIsNamedByItsFolder()
    {
        Assert.Equal("every file in Downloads", new SearchPage { ZapActive = true, ZapLabel = "Downloads" }.Label);
        Assert.Equal("\"cat\" in every file in Downloads", new SearchPage { ZapActive = true, ZapLabel = "Downloads", Text = "cat" }.Label);
        Assert.Equal("every file in Recent files", new SearchPage { ZapActive = true, ZapRecent = true, ZapLabel = "Recent files" }.Label);
    }

    [Fact]
    public void AVeryLongSearchIsCutShortInTheTooltip()
    {
        string label = new SearchPage { Text = new string('a', 500) }.Label;
        Assert.Equal("the search for \"" + new string('a', SearchPage.LabelTextLimit) + "...\"", label);
    }

    [Fact]
    public void ANewPageRemembersNoResult()
    {
        var page = new SearchPage();
        Assert.Equal(-1, page.ItemId);
        Assert.Null(page.ItemPath);
        Assert.Empty(page.SelScope);
        Assert.Empty(page.ZapRoots);
    }

    // ---- a folder opening ---------------------------------------------------------------------------

    [Fact]
    public void BackOntoAPageShowsItAgain()
    {
        var page = new SearchPage { Text = "ABC" };
        var arrival = SearchPageRules.OnArrival(fromHistory: true, page, searchBoxEmpty: true, closedWhileOpening: null);
        Assert.Same(page, arrival.Restore);
        Assert.Null(arrival.KeepForForward);
    }

    [Fact]
    public void AFolderOpenedAnyOtherWayShowsNoPage()
    {
        var page = new SearchPage { Text = "ABC" };
        Assert.Equal(default, SearchPageRules.OnArrival(fromHistory: false, page, true, null));
        Assert.Equal(default, SearchPageRules.OnArrival(fromHistory: true, null, true, null)); // a plain folder in the history
    }

    [Fact]
    public void SomethingTypedMeanwhileStays()
    {
        var page = new SearchPage { Text = "ABC" };
        Assert.Equal(default, SearchPageRules.OnArrival(true, page, searchBoxEmpty: false, null));
    }

    [Fact]
    public void BackPressedAgainWhileThePageWasOpeningKeepsItForForward()
    {
        var page = new SearchPage { Text = "ABC" };
        var arrival = SearchPageRules.OnArrival(true, page, true, closedWhileOpening: page);
        Assert.Null(arrival.Restore);
        Assert.Same(page, arrival.KeepForForward);

        // Another page (an equal one, even) that wasn't closed shows as usual: the very page decides.
        var other = page with { };
        Assert.Same(other, SearchPageRules.OnArrival(true, other, true, closedWhileOpening: page).Restore);
    }

    // ---- what Back does ---------------------------------------------------------------------------

    [Fact]
    public void BackFromResultsClosesThemFirst()
    {
        Assert.Equal(SearchPageRules.BackMove.LeaveResults, SearchPageRules.Back(resultsShowing: true, null, null));
        Assert.Equal(SearchPageRules.BackMove.LeaveResults, SearchPageRules.Back(true, new SearchPage(), null));
    }

    [Fact]
    public void BackFromAFolderStepsTheHistory()
    {
        Assert.Equal(SearchPageRules.BackMove.Step, SearchPageRules.Back(resultsShowing: false, pageOpening: null, null));
    }

    [Fact]
    public void BackTwiceQuicklyEndsOnTheFolderUnderTheSearchThenSteps()
    {
        var opening = new SearchPage { Text = "ABC" };
        // The second press, while the first is still opening the search page: the folder under it.
        Assert.Equal(SearchPageRules.BackMove.CloseOpeningPage, SearchPageRules.Back(false, opening, closedWhileOpening: null));
        // A third press while it is still opening: the history, as before.
        Assert.Equal(SearchPageRules.BackMove.Step, SearchPageRules.Back(false, opening, closedWhileOpening: opening));
    }

    // ---- tooltips -------------------------------------------------------------------------------------

    [Fact]
    public void BackTooltipFromResultsNamesTheirFolder()
    {
        Assert.Equal("Projects", SearchPageRules.BackTarget(resultsShowing: true, "Projects", "the search for \"x\""));
        Assert.Equal("the search for \"ABC\"", SearchPageRules.BackTarget(false, "ABC", "the search for \"ABC\""));
        Assert.Equal("Downloads", SearchPageRules.BackTarget(false, "Projects", "Downloads"));
    }

    [Fact]
    public void ForwardTooltipNamesTheSearchBackClosed()
    {
        Assert.Equal("the search for \"ABC\"", SearchPageRules.ForwardTarget(new SearchPage { Text = "ABC" }, "Downloads"));
        Assert.Equal("Downloads", SearchPageRules.ForwardTarget(null, "Downloads"));
        Assert.Null(SearchPageRules.ForwardTarget(null, null));
    }

    // ---- which result is remembered ---------------------------------------------------------------

    [Fact]
    public void TheResultOpenedIsRemembered()
    {
        var focused = (7, @"D:\Work\ABC");
        Assert.Equal((7, @"D:\Work\ABC"), SearchPageRules.PickRemembered(focused, [], @"D:\Work\ABC\"));
    }

    [Fact]
    public void TheOpenedOneAmongTheSelectedBeatsTheFocusedOne()
    {
        var focused = (7, @"D:\Work\ABC");
        (int, string)[] selected = [(7, @"D:\Work\ABC"), (9, @"D:\Work\Other")];
        Assert.Equal((9, @"D:\Work\Other"), SearchPageRules.PickRemembered(focused, selected, @"d:\work\other"));
    }

    [Fact]
    public void ElseTheFocusedResultIsRemembered()
    {
        var focused = (7, @"D:\Work\ABC");
        // Up, Home, the navigation pane: what was opened isn't a result.
        Assert.Equal((7, @"D:\Work\ABC"), SearchPageRules.PickRemembered(focused, [(9, @"D:\Work\Other")], @"D:\"));
        Assert.Equal((7, @"D:\Work\ABC"), SearchPageRules.PickRemembered(focused, [], null));
        Assert.Equal((-1, null), SearchPageRules.PickRemembered(null, [], null));
        Assert.Equal((-1, null), SearchPageRules.PickRemembered(null, [], @"D:\Elsewhere"));
    }

    // ---- finding it in fresh results ----------------------------------------------------------------

    static readonly Dictionary<int, string> Paths = new()
    {
        [10] = @"D:\Work\notes.txt",
        [11] = @"D:\Work\ABC",
        [12] = @"D:\Old\ABC",
        [13] = @"D:\Work\abc.txt",
    };

    static int Find(int[] ids, SearchPage page, bool sameNumbering, Func<int, string>? fullPath = null)
        => SearchPageRules.FindResult(ids, page, sameNumbering, id => Path.GetFileName(Paths[id]), fullPath ?? (id => Paths[id]));

    [Fact]
    public void FoundByIdWhileTheIndexNumbersFilesTheSame()
    {
        var page = new SearchPage { ItemId = 11, ItemPath = @"D:\Work\ABC" };
        Assert.Equal(2, Find([10, 12, 11, 13], page, sameNumbering: true));
    }

    [Fact]
    public void GoneFromTheResultsSelectsNothingNotANamesake()
    {
        var page = new SearchPage { ItemId = 11, ItemPath = @"D:\Work\ABC" };
        Assert.Equal(-1, Find([10, 12, 13], page, sameNumbering: true)); // D:\Old\ABC has the same name
        Assert.Equal(-1, Find([10, 12, 13], page, sameNumbering: false));
        Assert.Equal(-1, Find([], page, sameNumbering: false));
    }

    [Fact]
    public void FoundByPathOnceTheIdsMeanSomethingElse()
    {
        // A compaction renumbered the files, or "every file in" read the folder again from the disk.
        var page = new SearchPage { ItemId = 99, ItemPath = @"d:\work\abc\" };
        Assert.Equal(2, Find([10, 12, 11, 13], page, sameNumbering: false));
    }

    [Fact]
    public void OnlySameNamedFilesHaveTheirPathBuilt()
    {
        var built = new List<int>();
        var page = new SearchPage { ItemPath = @"D:\Work\ABC" };
        int found = Find([10, 13, 12, 11], page, sameNumbering: false, id => { built.Add(id); return Paths[id]; });
        Assert.Equal(3, found);
        Assert.Equal([12, 11], built);
    }

    [Fact]
    public void NothingRememberedFindsNothing()
    {
        Assert.Equal(-1, Find([10, 11], new SearchPage(), sameNumbering: true));
        Assert.Equal(-1, Find([10, 11], new SearchPage(), sameNumbering: false));
    }

    [Fact]
    public void ADriveIsLookedForByIdOnly()
    {
        // "E:\" has no file name to narrow by, so every path would have to be built: only its id finds it.
        var paths = new Dictionary<int, string> { [1] = @"E:\" };
        var page = new SearchPage { ItemId = 1, ItemPath = @"E:\" };
        Assert.Equal(-1, SearchPageRules.FindResult([1], page, false, _ => "E:", id => paths[id]));
        Assert.Equal(0, SearchPageRules.FindResult([1], page, true, _ => "E:", id => paths[id]));
    }

    // ---- when to select it ----------------------------------------------------------------------------

    [Fact]
    public void FoundIsSelected()
    {
        Assert.Equal(SearchPageRules.SelectMove.Select, SearchPageRules.DecideSelect(sameSearch: true, found: 4, stillReading: false, userSelected: false));
        Assert.Equal(SearchPageRules.SelectMove.Select, SearchPageRules.DecideSelect(true, 0, stillReading: true, false));
    }

    [Fact]
    public void NotFoundWhileTheDiskIsStillReadWaits()
    {
        Assert.Equal(SearchPageRules.SelectMove.Wait, SearchPageRules.DecideSelect(true, -1, stillReading: true, false));
        Assert.Equal(SearchPageRules.SelectMove.GiveUp, SearchPageRules.DecideSelect(true, -1, stillReading: false, false));
    }

    [Fact]
    public void AnotherSearchOrTheUsersOwnSelectionWins()
    {
        Assert.Equal(SearchPageRules.SelectMove.GiveUp, SearchPageRules.DecideSelect(sameSearch: false, 4, false, false));
        Assert.Equal(SearchPageRules.SelectMove.GiveUp, SearchPageRules.DecideSelect(true, 4, true, userSelected: true));
        Assert.Equal(SearchPageRules.SelectMove.GiveUp, SearchPageRules.DecideSelect(true, -1, true, userSelected: true));
    }

    // ---- the page a navigation away keeps (ExplorerHost's LeavingPage) --------------------------------

    static bool Same(string a, string b) => a == b;

    [Fact]
    public void ALeavingPageGoesWithItsNavigation()
    {
        var abc = new SearchPage { Text = "ABC" };
        var leaving = default(LeavingPage<string>).Start(abc, "x");
        Assert.Same(abc, leaving.Page);
        Assert.Equal("x", leaving.Target);
        Assert.Null(default(LeavingPage<string>).Start(null, "x").Page);
    }

    [Fact]
    public void ItsOwnNavigationFailingDropsThePage()
    {
        var leaving = default(LeavingPage<string>).Start(new SearchPage { Text = "ABC" }, "x");
        Assert.Null(leaving.Fail("x", Same).Page);
        Assert.Null(leaving.Fail(null, Same).Page); // the shell didn't say which: as before, dropped
        Assert.Null(default(LeavingPage<string>).Start(new SearchPage(), null).Fail("other", Same).Page); // target unknown
    }

    [Fact]
    public void ALateFailureOfACancelledNavigationKeepsThePage()
    {
        // A slow folder was opening; the result folder opened from the search cancels it, and the shell reports the slow
        // one as failed: the search left still goes into Back when the result folder opens.
        var abc = new SearchPage { Text = "ABC" };
        var leaving = default(LeavingPage<string>).Start(abc, "abc").Fail("slow", Same);
        Assert.Same(abc, leaving.Page);
        Assert.Equal("abc", leaving.Target);
    }

    [Fact]
    public void ANewerNavigationWithoutAPageKeepsTheOneStillPending()
    {
        // A folder opened from the results is still opening when the navigation pane goes elsewhere: the folder on screen
        // is still the one searched, so its search still goes into Back, now with the newer navigation.
        var abc = new SearchPage { Text = "ABC" };
        var leaving = default(LeavingPage<string>).Start(abc, "x").Start(null, "y");
        Assert.Same(abc, leaving.Page);
        Assert.Equal("y", leaving.Target);
        Assert.Same(abc, leaving.Fail("x", Same).Page); // the cancelled first one reporting late
        Assert.Null(leaving.Fail("y", Same).Page);      // the newer one failing
        var other = new SearchPage { Text = "other" };
        Assert.Same(other, leaving.Start(other, "z").Page); // a page of its own replaces it
    }
}
