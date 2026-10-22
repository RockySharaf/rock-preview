using System.Runtime.CompilerServices;
using Anything.Core;
using Anything.Core.Content;
using Anything.Preview;

namespace Anything;

/// <summary>
/// Search inside files in the window: the Search › Match inside files toggle (per tab, like the other match toggles), the
/// Matching text column and its tooltip, the split of the results (names first) and the status bar's parts, the hint when
/// a query needs a choice that is off, the offer to turn it on, and the preview opened at the match.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>A tab's search-inside-files state, kept beside the tab (its own class belongs to the tabs).</summary>
    sealed class TabContent
    {
        /// <summary>Search › Match inside files for this tab.</summary>
        public bool MatchContent;
        /// <summary>The content side of the query the results came from; null when it never looks inside files.</summary>
        public ContentHighlighter? Highlighter;
        /// <summary>SearchResults.NameMatches of the results shown (-1: nothing looked inside files).</summary>
        public int NameMatches = -1;
        /// <summary>The results depend on words inside files (searched again when new words come in).</summary>
        public bool UsesContent;
        /// <summary>
        /// The status bar's hint for these results: the choice a query needs, or "Looking inside files…" while lookups
        /// run; null for none. Kept apart from the window's search error, which only real errors set.
        /// </summary>
        public string? Hint;
    }

    /// <summary>
    /// The query has content functions of its own (content:, title:, dpi:…): they look inside files whatever the tab's
    /// Match inside files says (that toggle is for plain words), so their matching lines are shown too.
    /// </summary>
    static bool HasExplicitContent(TabContent tc) => tc.Highlighter is { NeedsMode: > ContentMode.NamesOnly };

    /// <summary>The active tab's plain words look inside files: its Match inside files on, Match path and Regex off.</summary>
    bool PlainWordsLookInside(TabContent tc) => ContentPresentation.PlainWordsLookInside(tc.MatchContent, _matchPath, _regex);

    readonly ConditionalWeakTable<FolderTab, TabContent> _tabContent = new();
    TabContent? _lastTabContent;          // the active tab's, last time it was asked: a new tab starts from it
    FolderTab? _contentSeenTab;           // to notice a tab switch (the strip repaints when one happens)
    ToolStripMenuItem? _miMatchContent;
    FlowLayoutPanel? _offerBar;
    Label? _offerText;
    LinkLabel? _offerTurnOn;
    System.Windows.Forms.Timer? _snippetTimer;
    NoLabelTips? _noLabelTips;
    int _previewSeq;
    string? _previewMatchKey;

    /// <summary>
    /// Keeps comctl32's own tip off the list: with item tooltips on (for the Matching text), a row whose tooltip is empty
    /// would show its cut-off name in a system tooltip. Subclasses the list's window (again when its handle is made again)
    /// and, once WinForms has answered LVN_GETINFOTIP, clears the text when the row has no tooltip of its own.
    /// </summary>
    sealed class NoLabelTips : NativeWindow
    {
        const int ReflectedNotify = 0x2000 + 0x004E, GetInfoTipW = -158; // WM_REFLECT + WM_NOTIFY, LVN_GETINFOTIPW
        readonly ListView _list;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NmLvGetInfoTip
        {
            public nint HwndFrom;
            public nuint IdFrom;
            public int Code;
            public uint Flags;
            public nint Text;
            public int TextMax;
            public int Item;
            public int SubItem;
            public nint LParam;
        }

        public NoLabelTips(ListView list)
        {
            _list = list;
            if (list.IsHandleCreated) AssignHandle(list.Handle);
            list.HandleCreated += (_, _) => AssignHandle(list.Handle);
            list.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != ReflectedNotify || m.LParam == 0) return;
            var tip = System.Runtime.InteropServices.Marshal.PtrToStructure<NmLvGetInfoTip>(m.LParam);
            if (tip.Code != GetInfoTipW || tip.Text == 0 || tip.TextMax <= 0) return;
            int count = _list.VirtualMode ? _list.VirtualListSize : _list.Items.Count;
            try
            {
                if ((uint)tip.Item < (uint)count && _list.Items[tip.Item].ToolTipText.Length > 0) return;
            }
            catch (Exception) { } // a row that can't be had now has no tooltip either
            System.Runtime.InteropServices.Marshal.WriteInt16(tip.Text, 0);
        }
    }

    /// <summary>The tab's state; a tab seen for the first time takes the toggle of the tab used before it (or Settings' default).</summary>
    TabContent ContentTab(FolderTab t)
    {
        if (!_tabContent.TryGetValue(t, out var c))
        {
            c = new TabContent { MatchContent = _lastTabContent?.MatchContent ?? _app.Settings.MatchContent };
            _tabContent.Add(t, c);
        }
        if (t == _tab) _lastTabContent = c;
        return c;
    }

    TabContent ContentTab() => ContentTab(_tab);

    /// <summary>The search's SearchOptions.MatchContent: the tab's toggle, while search inside files is on.</summary>
    bool ContentMatchOn() => _app.ContentOn && ContentTab().MatchContent;

    // ---- menu, bars and events ---------------------------------------------------------------------

    /// <summary>Search › Match inside files (after Enable Regex), and what the window needs for search inside files.</summary>
    void AddContentMenuItems(ToolStripMenuItem search)
    {
        // Rows' tooltips carry the whole matching line. Set once, while the window is built: changing it later recreates
        // the list's window, which loses the selection, the focused row and the scroll position. A row without a line (every
        // row with search inside files off) must show no tooltip at all, as the list never did: comctl32 would show its
        // cut-off name in one, so that tip is cleared.
        _list.ShowItemToolTips = true;
        _noLabelTips ??= new NoLabelTips(_list);
        _miMatchContent = Item("Match &Inside Files", () =>
        {
            var tc = ContentTab();
            Toggle(() => tc.MatchContent = !tc.MatchContent);
            ApplyContentColumn();
        });
        int regex = search.DropDownItems.IndexOf(_miRegex);
        search.DropDownItems.Insert(regex >= 0 ? regex + 1 : search.DropDownItems.Count, _miMatchContent);
        search.DropDownOpening += (_, _) => SyncContentMenu();
        // Tabs have no "selected" event of their own; the strip repaints whenever the active tab changes.
        _tabStrip.Invalidated += (_, _) =>
        {
            if (ReferenceEquals(_tab, _contentSeenTab)) return;
            _contentSeenTab = _tab;
            if (_loaded && IsHandleCreated) BeginInvoke(ApplyContentColumn);
        };
        Load += (_, _) => UpdateOfferBar();
        FormClosed += (_, _) => _app.Snippets?.ClearQueue(this); // its rows' lookups go with it
        SyncContentMenu();
    }

    void SyncContentMenu()
    {
        if (_miMatchContent is null) return;
        _miMatchContent.Checked = ContentTab().MatchContent;
        _miMatchContent.Enabled = _app.ContentOn;
        _miMatchContent.ToolTipText = !_app.ContentOn ? "Turn on search inside files in Settings › Indexing."
            : _matchPath || _regex ? "With Match Path or Regex on, search words match names only (content: looks inside files)."
            : "Plain search words also find words inside files.";
    }

    /// <summary>The applied choice changed (Settings closed): column, menu, offer and results follow.</summary>
    internal void OnContentModeChanged()
    {
        if (!_loaded) return;
        SyncContentMenu();
        ApplyContentColumn();
        UpdateOfferBar();
        OnIndexChanged(); // what plain words match changed: search again (other tabs when they show)
    }

    /// <summary>Progress or phase of the text index changed (at most once a second): the status bar.</summary>
    internal void OnContentStatusChanged()
    {
        if (_loaded) UpdateStatus();
    }

    /// <summary>New words are searchable (or a lookup finished): tabs whose results used them search again.</summary>
    internal void OnContentChanged()
    {
        if (!_loaded) return;
        foreach (var t in _tabs)
            if (t != _tab && _tabContent.TryGetValue(t, out var c) && c.UsesContent) t.Stale = true;
        if (!ContentTab().UsesContent) return;
        if (!Visible) { _staleWhileHidden = true; return; }
        if (_changeTimer.Enabled) return;
        _changeTimer.Interval = (int)Math.Clamp(_lastSearchMs * 2, 300, 3000);
        _changeTimer.Start();
    }

    /// <summary>Matching text came in: the rows still waiting for it are rebuilt and repainted (debounced).</summary>
    internal void OnSnippetsResolved()
    {
        if (!_loaded) return;
        if (_snippetTimer is null)
        {
            _snippetTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _snippetTimer.Tick += (_, _) =>
            {
                _snippetTimer.Stop();
                foreach (var r in _rows.Values)
                    if (!r.SnippetAsked) r.Item = null; // screen readers get the new text too
                _list.Invalidate();
            };
            Disposed += (_, _) => _snippetTimer.Dispose();
        }
        if (!_snippetTimer.Enabled) _snippetTimer.Start();
    }

    /// <summary>Results arrived: what they say about words inside files (the split, pending lookups, the hint).</summary>
    void OnContentResults(SearchResults results)
    {
        var tc = ContentTab();
        ContentHighlighter? highlighter = null;
        try { if (_resultQuery.Length > 0) highlighter = ContentHighlighter.Create(_resultQuery, _resultOptions); }
        catch (Exception) { } // a query the parser rejects says so in the results already
        tc.Highlighter = highlighter;
        tc.NameMatches = results.NameMatches;
        tc.UsesContent = highlighter is not null || results.NameMatches >= 0 || results.ContentPending;
        // A query that needs a choice that is off matches nothing there; say why, as an error would be (above the toggles).
        string? hint = null;
        if (highlighter is not null && highlighter.NeedsMode > _app.ContentMode) hint = ContentPresentation.ModeHint(highlighter.NeedsMode);
        tc.Hint = hint is { Length: > 0 } ? hint : results.ContentPending ? ContentPresentation.LookingInside : null;
        _app.Snippets?.ClearQueue(this); // this window's rows from before; other windows' stay queued
        ApplyContentColumn();
    }

    /// <summary>
    /// The status bar's parts for search inside files, after the toggles. In the folder view (<paramref name="folderView"/>)
    /// no results are on screen: what the last ones said (the split, how much of them is read) is left out.
    /// </summary>
    void ContentStatusParts(List<string> parts, bool folderView)
    {
        var tc = ContentTab();
        var status = _app.ContentOn ? _app.Content.Status : null;
        parts.AddRange(folderView
            ? ContentPresentation.StatusParts(_app.ContentMode, PlainWordsLookInside(tc), -1, 0, status, resultsUseContent: false)
            : ContentPresentation.StatusParts(_app.ContentMode, PlainWordsLookInside(tc), tc.NameMatches, _ids.Length, status, tc.UsesContent,
                HasExplicitContent(tc)));
    }

    /// <summary>
    /// The status bar's right side with the tab's hint (the choice a query needs, "Looking inside files…") over the name
    /// index's progress and the toggles, as an error would be, but after the disk listing's own line (<paramref name="listingLine"/>:
    /// its reading progress, or why it can't be read); a real search error keeps its place.
    /// </summary>
    string ContentStatusRight(string right, bool listingLine)
        => ContentPresentation.StatusRight(right, string.IsNullOrEmpty(_searchError) ? ContentTab().Hint : null, listingLine);

    /// <summary>
    /// What the empty list says instead of "No items match your search." when the results' words inside files explain it
    /// (the choice the query needs, "Looking inside files…"); null otherwise.
    /// </summary>
    string? ContentEmptyText()
    {
        var tc = ContentTab();
        return ContentPresentation.EmptyListText(tc.Highlighter?.NeedsMode ?? ContentMode.NamesOnly, _app.ContentMode,
            tc.Hint == ContentPresentation.LookingInside);
    }

    // ---- the offer ---------------------------------------------------------------------------------

    /// <summary>The offer to search inside files, under the new-version bar, while it is due (built the first time).</summary>
    internal void UpdateOfferBar()
    {
        bool show = _app.ShowContentOffer;
        if (!show)
        {
            if (_offerBar is { Visible: true }) _offerBar.Visible = false;
            return;
        }
        if (_offerBar is null) BuildOfferBar();
        _offerText!.Text = _app.ContentOfferText;
        _offerTurnOn!.Visible = _app.KeptContent is not null;
        if (!_offerBar!.Visible) _offerBar.Visible = true;
    }

    void BuildOfferBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6, 3, 6, 3),
            BackColor = SystemColors.Info,
            ForeColor = SystemColors.InfoText,
            Visible = false,
        };
        _offerText = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 6, 2) };
        _offerTurnOn = new LinkLabel { AutoSize = true, Text = "Turn it back on", Margin = new Padding(0, 2, 12, 2) };
        _offerTurnOn.LinkClicked += (_, _) => _app.TurnKeptContentBackOn();
        var choose = new LinkLabel { AutoSize = true, Text = "Choose…", Margin = new Padding(0, 2, 12, 2) };
        choose.LinkClicked += (_, _) =>
        {
            _app.ContentOfferAnswered();
            _app.ShowContentSettings(this);
        };
        var later = new LinkLabel { AutoSize = true, Text = "Not now", Margin = new Padding(0, 2, 0, 2) };
        later.LinkClicked += (_, _) => _app.ContentOfferAnswered();
        bar.Controls.AddRange([_offerText, _offerTurnOn, choose, later]);
        // The sentence wraps in a narrow window rather than run past its edge (it can name folders left out).
        var text = _offerText;
        bar.SizeChanged += (_, _) =>
        {
            var max = new Size(Math.Max(1, bar.ClientSize.Width - bar.Padding.Horizontal - text.Margin.Horizontal), 0);
            if (text.MaximumSize != max) text.MaximumSize = max;
        };
        _offerBar = bar;
        // Docked bars stack in the order of the window's controls: just below the new-version bar.
        Controls.Add(bar);
        Controls.SetChildIndex(bar, Controls.GetChildIndex(_updateBar));
    }

    // ---- the Matching text column ---------------------------------------------------------------

    /// <summary>
    /// Whether the Matching text column is in the list: allowed in the header menu, search inside files on and the tab's
    /// Match inside files on, or the query's own content functions (not per result, so it doesn't come and go while
    /// typing plain words). Rows' tooltips go with it.
    /// </summary>
    bool ContentColumnShown()
    {
        var tc = ContentTab();
        return ContentPresentation.ShowsMatchingText(_cols.TryGetValue("content", out var cs) && cs.Visible, _app.ContentMode, PlainWordsLookInside(tc),
            HasExplicitContent(tc));
    }

    /// <summary>
    /// Whether the column header menu lists a column: Matching text only while search inside files is on (names only, it
    /// could never show, and ticking it would only move its place for later).
    /// </summary>
    bool HeaderMenuShows(ColumnDef c) => c.Key != "content" || _app.ContentOn;

    /// <summary>The Matching text column is in the list now (asked for every row painted: no allocation).</summary>
    bool ContentColumnInList
    {
        get
        {
            for (int i = 0; i < _list.Columns.Count; i++)
                if (_list.Columns[i].Tag is ColumnDef { Key: "content" }) return true;
            return false;
        }
    }

    /// <summary>Rebuilds the columns when the Matching text column should come or go.</summary>
    void ApplyContentColumn()
    {
        if (!_loaded || IsDisposed) return;
        if (ContentColumnShown() != ContentColumnInList) BuildColumns();
    }

    /// <summary>Asks for a row's Matching text (once per query); it comes in later, from the index, off the UI thread.</summary>
    void EnsureSnippet(Row row)
    {
        if (row.SnippetAsked || row.FullPath.Length == 0) return;
        var highlighter = _app.ContentOn ? ContentTab().Highlighter : null;
        if (highlighter is null || _app.Snippets is not { } snippets)
        {
            row.SnippetAsked = true;
            return;
        }
        if (!snippets.TryGet(ContentPathOf(_resultIndex, row.FullPath), highlighter, this, out var result, out bool stale)) return;
        row.Snippet = result.Snippet;
        row.SnippetReason = result.Snippet is null && result.Reason is { } why ? ContentPresentation.Reason(why) : null;
        row.SnippetAsked = !stale; // a line from before new words came in shows until it is looked up again
    }

    /// <summary>
    /// The path the text index knows a result by: a listing's file in a folder the index names otherwise (a SUBST letter,
    /// a link) is looked up under that name.
    /// </summary>
    static string ContentPathOf(FileIndex? index, string path) => index is null ? path : ContentServices.IndexedPath(index, path);

    /// <summary>The row's tooltip: the whole matching line and where it is ("Page 4 of 12").</summary>
    void SetContentToolTip(ListViewItem item, Row row)
    {
        string tip = "";
        if (ContentColumnInList)
        {
            EnsureSnippet(row);
            tip = row.Snippet is { } s ? ContentPresentation.SnippetToolTip(s) : row.SnippetReason ?? "";
        }
        if (item.ToolTipText != tip) item.ToolTipText = tip;
    }

    /// <summary>A Matching text cell: where it is in secondary colour, then the line with the words in bold.</summary>
    void DrawContentCell(Graphics g, Row row, Rectangle rect, Color fore, bool selected)
    {
        EnsureSnippet(row);
        var secondary = selected ? fore : ExplorerTheme.SecondaryText;
        var flags = BaseTextFlags | TextFormatFlags.EndEllipsis;
        if (row.Snippet is not { } snippet)
        {
            if (row.SnippetReason is { } reason) TextRenderer.DrawText(g, reason, _list.Font, rect, secondary, flags);
            return;
        }
        string prefix = ContentPresentation.SnippetPrefix(snippet);
        var (text, bold) = ContentPresentation.SnippetBody(snippet);
        text = OneLine(text);
        if (prefix.Length > 0)
        {
            int w = TextRenderer.MeasureText(g, prefix, _list.Font, new Size(int.MaxValue, rect.Height), BaseTextFlags).Width;
            if (w >= rect.Width)
            {
                TextRenderer.DrawText(g, prefix, _list.Font, rect, secondary, flags);
                return;
            }
            TextRenderer.DrawText(g, prefix, _list.Font, new Rectangle(rect.Left, rect.Top, w + 1, rect.Height), secondary, BaseTextFlags);
            rect = Rectangle.FromLTRB(rect.Left + w, rect.Top, rect.Right, rect.Bottom);
        }
        if (bold.Count == 0 || _boldFont is null)
        {
            TextRenderer.DrawText(g, text, _list.Font, rect, fore, flags);
            return;
        }
        // The snippet keeps some context before its first match, which a narrow cell would push past its edge: at most
        // three words before it, fewer while the match still doesn't fit (the tooltip has the whole line).
        var (line, lineBold) = (text, bold);
        (text, bold) = ContentPresentation.StartNearMatch(line, lineBold);
        for (int words = 2; words >= 0 && !ReachesFirstMatch(g, text, bold, rect); words--)
            (text, bold) = ContentPresentation.StartNearMatch(line, lineBold, keepWords: words);
        DrawHighlighted(g, text, bold, rect, _list.Font, _boldFont, fore);
    }

    /// <summary>Whether the end of the first bold word fits in the cell.</summary>
    bool ReachesFirstMatch(Graphics g, string text, IReadOnlyList<(int Start, int Length)> bold, Rectangle rect)
    {
        int reach = int.MaxValue;
        foreach (var (start, length) in bold)
            if (length > 0 && start >= 0 && start + length < reach) reach = start + length;
        reach = Math.Clamp(reach, 0, text.Length);
        return reach == 0 || TextRenderer.MeasureText(g, text[..reach], _boldFont!, new Size(int.MaxValue, rect.Height), BaseTextFlags).Width <= rect.Width;
    }

    /// <summary>Tabs, line and page breaks drawn as spaces (one for one, so the bold ranges still fit).</summary>
    static string OneLine(string text) => text.IndexOfAny(['\t', '\n', '\r', '\f']) < 0 ? text
        : text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('\f', ' ');

    /// <summary>Row <paramref name="index"/> is the first of the results matched only inside files: a line above it.</summary>
    bool StartsContentMatches(int index)
    {
        var tc = ContentTab();
        return tc.NameMatches > 0 && tc.NameMatches < _ids.Length && index == tc.NameMatches;
    }

    /// <summary>
    /// Export's Matching text: each file's line from the index (null when the results didn't look inside files). It reads
    /// the index, so the export calls it off the UI thread.
    /// </summary>
    Func<string, string>? ContentExportText()
    {
        var highlighter = _app.ContentOn ? ContentTab().Highlighter : null;
        if (highlighter is null) return null;
        var content = _app.Content;
        var index = _resultIndex;
        return path =>
        {
            try { return content.GetSnippet(ContentPathOf(index, path), highlighter) is { } s ? ContentPresentation.SnippetPrefix(s) + OneLine(ContentPresentation.SnippetBody(s).Text) : ""; }
            catch (Exception) { return ""; }
        };
    }

    /// <summary>A bookmark with its own Match inside files sets the tab's toggle (older bookmarks leave it alone).</summary>
    void ApplyBookmarkContent(Bookmark b)
    {
        if (b.MatchContent is bool on) ContentTab().MatchContent = on;
    }

    // ---- the preview at the match -------------------------------------------------------------

    /// <summary>Empties the preview (nothing selected, the pane hidden, the window closing); a match still being looked up is dropped.</summary>
    void ClearPreview()
    {
        _previewSeq++;
        _previewMatchKey = null;
        _preview.Clear();
    }

    /// <summary>
    /// Shows the selected file. When the query looks inside files, the file's matching line is fetched off the UI thread
    /// first, so the pane opens at the page, line or sheet with the words highlighted; a file without one gets the plain
    /// preview (the rich one: Office's own, say). The same file with a new query only changes the match.
    /// </summary>
    void ShowPreview(string path)
    {
        var highlighter = _app.ContentOn ? ContentTab().Highlighter : null;
        string key = highlighter?.Key ?? "";
        bool samePath = string.Equals(path, _preview.CurrentPath, StringComparison.OrdinalIgnoreCase);
        if (samePath && key == _previewMatchKey) return;
        int seq = ++_previewSeq;
        _previewMatchKey = key;
        if (highlighter is null)
        {
            if (samePath) _preview.SetMatch(null);
            else _ = _preview.ShowAsync(path, null);
            return;
        }
        var content = _app.Content;
        string indexed = ContentPathOf(_resultIndex, path);
        var lookup = Task.Run(() =>
        {
            try { return content.GetSnippet(indexed, highlighter); }
            catch (Exception) { return null; }
        });
        _ = ShowPreviewWhenMatched(path, highlighter, lookup, seq, samePath);
    }

    async Task ShowPreviewWhenMatched(string path, ContentHighlighter highlighter, Task<ContentSnippet?> lookup, int seq, bool samePath)
    {
        bool shown = samePath;
        // A slow index (a cold disk) mustn't hold the preview back: the file shows, and the match follows.
        if (await Task.WhenAny(lookup, Task.Delay(400)) != lookup)
        {
            if (seq != _previewSeq || IsDisposed) return;
            if (!shown) { shown = true; _ = _preview.ShowAsync(path, null); }
        }
        var snippet = await lookup;
        if (seq != _previewSeq || IsDisposed) return;
        var match = snippet is null ? null : PreviewMatchFor(path, highlighter, snippet);
        if (shown) _preview.SetMatch(match);
        else _ = _preview.ShowAsync(path, match);
    }

    PreviewMatch PreviewMatchFor(string path, ContentHighlighter highlighter, ContentSnippet snippet)
    {
        var content = _app.Content;
        var (banner, bold) = ContentPresentation.Banner(snippet);
        bool pdfPage = snippet.Page > 0 && snippet.Kind is ContentKind.Pdf or ContentKind.Design;
        return new PreviewMatch(highlighter.Key, text => highlighter.Find(text), snippet.Page, snippet.Line, snippet.Sheet, banner, banner is null ? null : bold)
        {
            PageBoxes = !pdfPage ? null : async (pageIndex, ct) =>
            {
                var boxes = await content.LocateOnPdfPageAsync(path, pageIndex, highlighter, ct).ConfigureAwait(false);
                return (IReadOnlyList<RectangleF>)[.. boxes.Select(b => new RectangleF(b.X, b.Y, b.Width, b.Height))];
            },
            TextWindow = snippet.Line <= 0 ? null : (line, ct) => content.LoadTextWindowAsync(path, line, 1 << 20, ct),
        };
    }
}
