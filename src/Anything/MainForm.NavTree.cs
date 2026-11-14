using Anything.Explorer;

namespace Anything;

/// <summary>
/// The navigation pane: Windows' own folder tree, kept in sync with the view, in two parts. Quick access (Home,
/// Gallery, OneDrive and the pinned folders) stays at the top, like a frozen pane, under a header that folds it away;
/// This PC, Network and Linux scroll below it.
/// </summary>
internal sealed partial class MainForm
{
    readonly NavigationTree _pinTree = new() { Dock = DockStyle.Top, Part = NavigationTree.Parts.Pinned };
    readonly NavigationTree _tree = new() { Dock = DockStyle.Fill, Part = NavigationTree.Parts.Places };
    readonly NavSectionHeader _pinHeader = new() { Dock = DockStyle.Top, Text = "Quick access" };
    readonly NavDividerLine _pinDivider = new() { Dock = DockStyle.Top };

    /// <summary>At most this share of the pane is Quick access's: more of it scrolls within its part.</summary>
    const float PinnedShare = 0.5f;

    void BuildNavTree()
    {
        foreach (var t in new[] { _pinTree, _tree })
        {
            var tree = t;
            tree.ExpandToFolder = _app.Settings.ExpandTreeToFolder;
            tree.FolderSelected += location =>
            {
                _navClick = (tree, Environment.TickCount64);
                OpenFolder(location);
            };
        }
        // Docked from the back of the z-order: the header on top, Quick access, the line, then the places fill the rest.
        _navPanel.Controls.AddRange([_tree, _pinDivider, _pinTree, _pinHeader]);
        _pinDivider.SendToBack();
        _pinTree.SendToBack();
        _pinHeader.SendToBack();
        _tree.BringToFront();
        // One highlight in the pane: a folder shown in both parts (pinned, and under This PC) is highlighted in one.
        _pinTree.Highlighted += () => _tree.ClearHighlight();
        _tree.Highlighted += () => _pinTree.ClearHighlight();
        _pinTree.ContentHeightChanged += LayoutNavParts;
        _navPanel.Resize += (_, _) => LayoutNavParts();
        _pinHeader.Expanded = !_app.Settings.NavPinnedCollapsed;
        _pinHeader.Click += (_, _) =>
        {
            _app.Settings.NavPinnedCollapsed = !_app.Settings.NavPinnedCollapsed;
            foreach (var w in _app.Windows) w.LayoutNavParts();
        };
        LayoutNavParts();
    }

    /// <summary>Quick access as tall as its rows (up to half the pane), or folded under its header.</summary>
    void LayoutNavParts()
    {
        bool open = !_app.Settings.NavPinnedCollapsed;
        _pinHeader.Expanded = open;
        _pinTree.Visible = open;
        _pinDivider.Visible = open;
        if (!open) return;
        int room = _navPanel.ClientSize.Height - _pinHeader.Height - _pinDivider.Height;
        int want = _pinTree.ContentHeight;
        int height = Math.Max(0, Math.Min(want, (int)(room * PinnedShare)));
        if (want > 0 && _pinTree.Height != height) _pinTree.Height = height;
    }

    /// <summary>
    /// The part of the pane just clicked: the folder it opens keeps that highlight, though the folder may go by another
    /// name there (a pinned Desktop is Quick access's own item, not the Desktop folder under This PC).
    /// </summary>
    (NavigationTree Tree, long Ticks)? _navClick;

    partial void SyncNavTreeCore(string location)
    {
        if (_navClick is { } click && Environment.TickCount64 - click.Ticks < 5000)
        {
            _navClick = null;
            (click.Tree == _pinTree ? _tree : _pinTree).ClearHighlight();
            return;
        }
        _navClick = null;
        bool Same(string? name) => string.Equals(name, location, StringComparison.OrdinalIgnoreCase);
        // Already highlighted in one part (it was clicked there): the other part lets go of its own.
        if (Same(_pinTree.SelectedParsingName)) { _tree.ClearHighlight(); return; }
        if (Same(_tree.SelectedParsingName)) { _pinTree.ClearHighlight(); return; }
        _tree.SelectFolder(location);
        _pinTree.SelectFolder(location);
    }

    /// <summary>"Quick access" above its part of the pane, with a chevron: a click folds it away or brings it back.</summary>
    sealed class NavSectionHeader : Control
    {
        bool _expanded = true, _hover;

        public NavSectionHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            Height = 28;
        }

        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                AccessibleDescription = value ? "Click to fold Quick access away" : "Click to show Quick access";
                Invalidate();
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = LogicalToDeviceUnits(28); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = LogicalToDeviceUnits(28); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ExplorerTheme.Content);
            int pad = LogicalToDeviceUnits(6);
            var row = Rectangle.FromLTRB(pad, LogicalToDeviceUnits(2), Width - pad, Height - LogicalToDeviceUnits(2));
            if (_hover) ExplorerTheme.FillRounded(g, row, LogicalToDeviceUnits(4), ExplorerTheme.Hover);
            int glyph = LogicalToDeviceUnits(12);
            var chevron = new Rectangle(row.Left + LogicalToDeviceUnits(8), row.Top, glyph, row.Height);
            ExplorerTheme.DrawGlyph(g, _expanded ? ExplorerTheme.GChevronDown : ExplorerTheme.GChevronRight, chevron, LogicalToDeviceUnits(8), ExplorerTheme.SecondaryText);
            var text = Rectangle.FromLTRB(chevron.Right + LogicalToDeviceUnits(8), row.Top, row.Right, row.Bottom);
            TextRenderer.DrawText(g, Text, Font, text, ExplorerTheme.SecondaryText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>The line between Quick access and the places, as File Explorer's pane draws between its groups.</summary>
    sealed class NavDividerLine : Control
    {
        public NavDividerLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Height = 9;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = LogicalToDeviceUnits(9); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = LogicalToDeviceUnits(9); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ExplorerTheme.Content);
            int inset = LogicalToDeviceUnits(8), y = Height / 2;
            using var pen = new Pen(ExplorerTheme.NavDivider, Math.Max(1, LogicalToDeviceUnits(1)));
            e.Graphics.DrawLine(pen, inset, y, Width - inset, y);
        }
    }
}
