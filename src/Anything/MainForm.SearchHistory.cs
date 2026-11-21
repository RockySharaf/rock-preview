namespace Anything;

/// <summary>
/// Earlier searches, as in File Explorer but out of the way of searching as you type: a search is remembered when it is
/// committed (Enter in the search box, or a result opened), and the arrow at the end of the box or Alt+Down lists them.
/// One list for every window, kept in the settings file (Settings › General › Remember searches).
/// </summary>
internal sealed partial class MainForm
{
    SearchHistoryMenu? _historyMenu;
    long _historyClosedAt; // Environment.TickCount64 when a click outside last closed the list

    void WireSearchHistory()
    {
        _searchField.HistoryClicked += () => ShowSearchHistory(keyboard: false);
        UpdateSearchHistoryButton();
    }

    /// <summary>The arrow shows while there are searches to list.</summary>
    internal void UpdateSearchHistoryButton() =>
        _searchField.SetHistoryButton(_app.Settings.RememberSearches && _app.Settings.SearchHistory.Count > 0);

    /// <summary>Remembers what is in the search box (nothing while it is empty, or when Remember searches is off).</summary>
    void RememberSearch()
    {
        var s = _app.Settings;
        if (!s.RememberSearches || !s.SearchHistory.Add(_search.Text)) return;
        s.Save();
        SearchHistoryChanged(_app);
    }

    /// <summary>The list changed (here, or in Settings): every window's arrow shows or hides.</summary>
    internal static void SearchHistoryChanged(AnythingApp app)
    {
        foreach (var w in app.Windows) w.UpdateSearchHistoryButton();
    }

    void ShowSearchHistory(bool keyboard)
    {
        var s = _app.Settings;
        if (!s.RememberSearches || s.SearchHistory.Count == 0) return;
        if (_historyMenu is { Visible: true }) { _historyMenu.Close(); return; }
        // The press on the arrow that closed the list (a click outside it) shouldn't open it again at once.
        if (!keyboard && Environment.TickCount64 - _historyClosedAt < 250) return;
        if (_historyMenu == null)
        {
            var menu = new SearchHistoryMenu();
            menu.Run += RunEarlierSearch;
            menu.Remove += search =>
            {
                if (!_app.Settings.SearchHistory.Remove(search)) return;
                _app.Settings.Save();
                SearchHistoryChanged(_app);
            };
            menu.ClearAll += () =>
            {
                if (!_app.Settings.SearchHistory.Clear()) return;
                _app.Settings.Save();
                SearchHistoryChanged(_app);
            };
            menu.Closed += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked) _historyClosedAt = Environment.TickCount64;
                _searchField.SetHistoryOpen(false);
            };
            Disposed += (_, _) => menu.Dispose();
            _historyMenu = menu;
        }
        _searchField.SetHistoryOpen(true);
        _historyMenu.ShowUnder(_searchField, [.. s.SearchHistory.Items], keyboard);
    }

    /// <summary>Runs an earlier search in the search box, and moves it to the top of the list.</summary>
    void RunEarlierSearch(string search)
    {
        _search.Text = search; // searches as typing does
        FocusSearch(false);
        _search.SelectionStart = _search.TextLength;
        RememberSearch();
    }
}
