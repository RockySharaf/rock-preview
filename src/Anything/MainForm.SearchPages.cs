using Anything.Core;
using Anything.Core.Indexing;

namespace Anything;

/// <summary>
/// Back returns to a search, as in File Explorer: a search is left by going somewhere (a folder opened from the results,
/// a result's location, the navigation pane, the address bar, the breadcrumb, Home, Up, Forward), and the folder it
/// showed over goes into Back with the search as a <see cref="SearchPage"/>. When Back or Forward comes to that entry,
/// the search shows again: its words, where it looked, the tab's filters and options, its order, "every file in", and the
/// result that was opened, selected once the fresh results are in. The decisions are in <see cref="SearchPageRules"/>;
/// this is the window's side of them. Escape, the search box's × and the scope chip still drop a search for good.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>A search shown again whose remembered result is still to be selected: in which tab, and whether results came in for it yet.</summary>
    readonly record struct PendingSelect(FolderTab Tab, SearchPage Page, bool First);
    PendingSelect? _restoreSelect;

    /// <summary>
    /// The search showing now, to keep with its folder in Back, or null when the folder view shows (nothing to keep).
    /// Taken before the search box empties. <paramref name="opened"/>: the folder or item being opened, remembered as the
    /// result to select again when it is one of the results selected.
    /// </summary>
    SearchPage? CaptureSearchPage(string? opened = null)
    {
        if (!_browsing || ExplorerShown) return null;
        var (id, path) = RememberedResult(opened);
        // Left again before the fresh results of a page shown again came in (Back twice, say): the result it was still
        // to select is the one remembered, not none.
        if (id < 0 && path == null && _restoreSelect is { } pending && pending.Tab == _tab && pending.Page.Text == _search.Text)
            return Capture() with
            {
                ItemId = pending.Page.ItemId,
                ItemGeneration = pending.Page.ItemGeneration,
                ItemFromMainIndex = pending.Page.ItemFromMainIndex,
                ItemPath = pending.Page.ItemPath,
            };
        return Capture();

        SearchPage Capture() => new()
        {
            Text = _search.Text,
            SearchEverywhere = _tab.SearchEverywhere,
            UseSelScope = _useSelScope,
            SelScope = _selScope.ToArray(),
            FilterIndex = _filterIndex,
            MatchCase = _matchCase,
            WholeWord = _wholeWord,
            MatchPath = _matchPath,
            Diacritics = _diacritics,
            Regex = _regex,
            MatchContent = ContentTab().MatchContent,
            Sort = _sort,
            Descending = _desc,
            ZapActive = _zapActive,
            ZapThumbs = _zapThumbs,
            ZapRoots = _zapRoots.ToArray(),
            ZapRecent = _tab.ZapRecent != null,
            ZapLabel = _zapLabel,
            SortBeforeZap = _sortBeforeZap,
            SortFromFolder = _sortFromFolder,
            ItemId = id,
            ItemGeneration = _resultGeneration,
            ItemFromMainIndex = ReferenceEquals(_resultIndex, _app.IndexManager.Index),
            ItemPath = path,
        };
    }

    /// <summary>
    /// The result to select again: the one opened when it is among those selected, else the focused one if it is
    /// selected. A focus nobody chose (where the keyboard would start, with nothing selected) isn't remembered, so
    /// coming back never selects a row the user didn't pick.
    /// </summary>
    (int Id, string? Path) RememberedResult(string? opened)
    {
        if (!ResultsCurrent || _resultIndex is not { } index) return (-1, null);
        try
        {
            (int, string)? focused = null;
            int fi = FocusedIndex();
            if (fi >= 0 && fi < _ids.Length && _list.IsItemSelected(fi) && index.IsAlive(_ids[fi])) focused = (_ids[fi], index.GetFullPath(_ids[fi]));
            return SearchPageRules.PickRemembered(focused, opened != null ? Selected(index) : [], opened);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return (-1, null); }

        // Paths are built only while looking for the one opened, which is usually the focused one already.
        IEnumerable<(int, string)> Selected(FileIndex ix)
        {
            foreach (int i in _list.GetSelectedIndices(1000))
                if (i < _ids.Length && ix.IsAlive(_ids[i])) yield return (_ids[i], ix.GetFullPath(_ids[i]));
        }
    }

    /// <summary>
    /// A folder has just opened in <paramref name="tab"/>: the search page to show again over it (the active tab's only),
    /// or null. Back pressed again while it was opening keeps the page for Forward instead, as Back on a search does.
    /// </summary>
    SearchPage? ArriveAtPage(FolderTab tab)
    {
        var arrival = SearchPageRules.OnArrival(tab.Explorer.LastNavigationWasHistory, tab.Explorer.LastHistoryPage,
            tab.SearchText.Length == 0, tab.PageClosedOpening);
        tab.PageClosedOpening = null;
        if (arrival.KeepForForward is { } kept)
        {
            tab.ForwardSearch = kept;
            tab.ForwardSearchAt = (tab.Explorer.CurrentParsingName, tab.Explorer.BackCount);
        }
        return tab == _tab ? arrival.Restore : null;
    }

    /// <summary>
    /// Shows a search page again over the open folder, searched afresh in the background as every search is. The tab's
    /// own filters and options come back as the search had them; the app-wide ones (Everyday files only, Include
    /// subfolders, list or thumbnails for search results) are left as they are, as other windows share them.
    /// </summary>
    void RestoreSearchPage(SearchPage page)
    {
        if (!_browsing) return;
        if (_zapActive) ExitZap(rerun: false); // first: leaving it puts back an order of its own
        _filterIndex = Math.Clamp(page.FilterIndex, 0, Filters.All.Length - 1);
        _matchCase = page.MatchCase;
        _wholeWord = page.WholeWord;
        _matchPath = page.MatchPath;
        _diacritics = page.Diacritics;
        _regex = page.Regex;
        ContentTab().MatchContent = page.MatchContent;
        (_sort, _desc) = (page.Sort, page.Descending);
        _tab.SearchEverywhere = page.SearchEverywhere;
        if (page.ZapActive)
        {
            // As the Thumbnails button sets it up, but with what the page had: Home's recent files are read again (the
            // list changes), and the orders "every file in" goes back to when it is left are its own.
            List<string>? recent = page.ZapRecent ? ReadRecentFiles() : null;
            _zapRoots = recent != null ? [] : page.ZapRoots.ToList();
            _tab.ZapRecent = recent;
            _zapLabel = page.ZapLabel;
            _sortBeforeZap = page.SortBeforeZap;
            _sortFromFolder = page.SortFromFolder;
            _zapActive = true;
            _zapThumbs = page.ZapThumbs;
            if (recent == null) ChooseZapSource(_zapRoots); // the index, or the disk (read in the background)
        }
        UpdateSortIndicator();
        SyncMenus();
        ClearResultsForPage();
        _suppress = true; // one search, below
        try
        {
            _search.Text = page.Text;
            _search.SelectionStart = _search.TextLength;
        }
        finally { _suppress = false; }
        // After the words: putting them in would otherwise take the folders selected in the view as where to look.
        _selScope = page.SelScope.ToList();
        _useSelScope = page.UseSelScope && _selScope.Count > 0;
        _restoreSelect = page.ItemId >= 0 || page.ItemPath != null ? new PendingSelect(_tab, page, First: true) : null;
        ApplyViewMode();
        UpdateLeftView();
        RunSearch(SearchReason.NewQuery);
        QueueStatusUpdate();
    }

    /// <summary>
    /// The list starts empty for the search shown again, so the results the tab had before (another search's) neither
    /// show meanwhile nor pass their selection on to the fresh ones.
    /// </summary>
    void ClearResultsForPage()
    {
        _list.CancelMarquee();
        _ids = [];
        _rows.Clear();
        _suppress = true;
        try
        {
            if (_list.SelectedIndices.Count > 0) _list.SetAllSelected(false);
            _list.VirtualListSize = 0;
        }
        catch (ArgumentException) { }
        finally { _suppress = false; }
        _grid.SetCount(0, reset: true);
    }

    /// <summary>
    /// Results came in (ApplyResults): after a search shown again, the result that was opened is selected and scrolled
    /// into view. Gone from the results: nothing is selected, and nothing is said.
    /// </summary>
    void SelectRestoredResult()
    {
        if (_restoreSelect is not { } pending) return;
        // Another tab's results (a stale one searched again after switching to it): the search shown again in its own
        // tab keeps waiting, to select its result when that tab shows and searches again. A tab closed or moved to
        // another window meanwhile is let go.
        if (pending.Tab != _tab)
        {
            if (!_tabs.Contains(pending.Tab)) _restoreSelect = null;
            return;
        }
        var page = pending.Page;
        bool sameSearch = _resultQuery == page.Text && _zapActive == page.ZapActive;
        int found = -1;
        if (sameSearch && ResultsCurrent && _resultIndex is { } index)
        {
            // By id while the app's index numbers its files as it did when the search was left; else by path.
            bool sameNumbering = page.ItemFromMainIndex && ReferenceEquals(index, _app.IndexManager.Index) && _resultGeneration == page.ItemGeneration;
            try { found = SearchPageRules.FindResult(_ids, page, sameNumbering, index.Name, index.GetFullPath); }
            catch (IndexOutOfRangeException) { found = -1; }
        }
        bool reading = _listing is { State: ListingState.Reading };
        bool userSelected = !pending.First && _list.SelectedIndices.Count > 0;
        switch (SearchPageRules.DecideSelect(sameSearch, found, reading, userSelected))
        {
            case SearchPageRules.SelectMove.Select:
                _restoreSelect = null;
                SelectResultAt(found);
                break;
            case SearchPageRules.SelectMove.Wait:
                _restoreSelect = pending with { First = false };
                break;
            default:
                _restoreSelect = null;
                break;
        }
    }

    /// <summary>Selects and focuses one result and scrolls it into view, in the list and the thumbnails alike.</summary>
    void SelectResultAt(int index)
    {
        if ((uint)index >= (uint)_ids.Length) return;
        try
        {
            _list.SetAllSelected(false);
            _list.SetSelected(index, true);
            _list.SetFocusedIndex(index);
            _list.EnsureVisible(index);
        }
        catch (ArgumentException) { return; }
        if (ThumbnailMode) _grid.EnsureVisible(index);
        _grid.Invalidate();
        QueueStatusUpdate();
        QueuePreview();
    }
}
