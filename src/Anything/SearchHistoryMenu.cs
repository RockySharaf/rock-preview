namespace Anything;

/// <summary>
/// The list of earlier searches under the search box (its arrow, or Alt+Down), newest first, as in File Explorer:
/// choosing one runs it, Delete or the × on the highlighted row forgets it, and the last row forgets them all.
/// </summary>
internal sealed class SearchHistoryMenu : ContextMenuStrip
{
    const int MaxShown = 80; // longer searches are cut short on their row (the whole text runs)

    readonly ToolStripMenuItem _clear = new("Clear search history");
    bool _keepOpen; // the × was clicked: forget that search and stay open

    /// <summary>A search was chosen.</summary>
    public event Action<string>? Run;

    /// <summary>A search is to be forgotten.</summary>
    public event Action<string>? Remove;

    /// <summary>Every search is to be forgotten.</summary>
    public event Action? ClearAll;

    public SearchHistoryMenu()
    {
        ShowImageMargin = false;
        ShowCheckMargin = false;
        ShowItemToolTips = true;
        ExplorerTheme.StyleDropDown(this); // the app's Windows 11 menu look (rounded corners, roomy rows)
        _clear.Click += (_, _) => ClearAll?.Invoke();
        Closing += (_, e) =>
        {
            if (_keepOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            _keepOpen = false;
        };
    }

    /// <summary>Opens under <paramref name="field"/>, as wide as it; from the keyboard, the newest search is highlighted.</summary>
    public void ShowUnder(Control field, IReadOnlyList<string> searches, bool keyboard)
    {
        SuspendLayout();
        var old = Items.Cast<ToolStripItem>().Where(i => i != _clear).ToList();
        Items.Clear();
        foreach (var item in old) item.Dispose();
        foreach (var search in searches)
        {
            var item = new HistoryItem(search, Shorten(search));
            item.Click += (_, _) => OnItemClick(item);
            Items.Add(item);
        }
        Items.Add(new ToolStripSeparator());
        Items.Add(_clear);
        ResumeLayout();
        MinimumSize = new Size(field.Width, 0);
        MaximumSize = new Size(Math.Max(field.Width, field.LogicalToDeviceUnits(560)), 0);
        Show(field, new Point(0, field.Height + field.LogicalToDeviceUnits(2)));
        if (keyboard && Items.Count > 0) Items[0].Select();
    }

    bool _widening;

    /// <summary>
    /// A menu sizes its rows to its widest text, not to its own width: opened as wide as the search box, the rows (their
    /// highlight and the ×) would stop part way. They are stretched to the menu's inner width after each layout.
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_widening) return; // a row's new width asks for a layout again
        base.OnLayout(e);
        _widening = true;
        try
        {
            var inner = DisplayRectangle;
            foreach (ToolStripItem item in Items)
            {
                int width = inner.Right - item.Bounds.Left;
                if (item.Available && width > item.Width) item.Width = width;
            }
        }
        finally { _widening = false; }
    }

    static string Shorten(string search) =>
        (search.Length > MaxShown ? search[..(MaxShown - 1)] + "…" : search).Replace("&", "&&");

    void OnItemClick(HistoryItem item)
    {
        if (item.RemovePressed)
        {
            item.RemovePressed = false;
            _keepOpen = true;
            // After the click is done with the row, not while it is still being handled.
            BeginInvoke(() => RemoveItem(item));
            return;
        }
        Run?.Invoke(item.Search);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => RemoveSelected(keyData) || base.ProcessCmdKey(ref msg, keyData);

    protected override bool ProcessDialogKey(Keys keyData) => RemoveSelected(keyData) || base.ProcessDialogKey(keyData);

    /// <summary>Delete forgets the highlighted search.</summary>
    bool RemoveSelected(Keys keyData)
    {
        if (keyData != Keys.Delete || Items.OfType<HistoryItem>().FirstOrDefault(i => i.Selected) is not { } item) return false;
        RemoveItem(item);
        return true;
    }

    void RemoveItem(HistoryItem item)
    {
        if (item.IsDisposed || item.Owner != this) return;
        int at = Items.IndexOf(item);
        Remove?.Invoke(item.Search);
        Items.Remove(item);
        item.Dispose();
        var rest = Items.OfType<HistoryItem>().ToList();
        if (rest.Count == 0) { Close(); return; }
        // The row that took its place stays highlighted, so Delete can go on down the list.
        rest[Math.Min(at, rest.Count - 1)].Select();
    }

    /// <summary>One earlier search: its text, and a × at the end while it is highlighted.</summary>
    sealed class HistoryItem : ToolStripMenuItem
    {
        bool _overRemove;

        public HistoryItem(string search, string shown) : base(shown)
        {
            Search = search;
            ToolTipText = search.Length > MaxShown ? search : null;
            // Room at the end of the row for the ×: menus keep a column for shortcut keys, and these are blank.
            ShortcutKeyDisplayString = "  ";
        }

        public string Search { get; }

        /// <summary>The press was on the ×: the click forgets the search instead of running it.</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool RemovePressed { get; set; }

        int Scale(int v) => v * (Owner?.DeviceDpi ?? 96) / 96;

        Rectangle RemoveRect
        {
            get
            {
                int w = Scale(28);
                return new Rectangle(Width - w - Scale(4), Scale(2), w, Math.Max(0, Height - Scale(4)));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!Selected) return;
            var r = RemoveRect;
            if (_overRemove) ExplorerTheme.FillRounded(e.Graphics, r, Scale(4), ExplorerTheme.Hover);
            ExplorerTheme.DrawGlyph(e.Graphics, ExplorerTheme.GClose, r, Scale(9), ExplorerTheme.SecondaryText);
        }

        protected override void OnMouseMove(MouseEventArgs mea)
        {
            base.OnMouseMove(mea);
            bool over = RemoveRect.Contains(mea.Location);
            if (over == _overRemove) return;
            _overRemove = over;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _overRemove = RemovePressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            RemovePressed = e.Button == MouseButtons.Left && RemoveRect.Contains(e.Location);
            base.OnMouseDown(e);
        }
    }
}
