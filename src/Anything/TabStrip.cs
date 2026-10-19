using System.Drawing.Drawing2D;
using Anything.Interop;

namespace Anything;

/// <summary>What the tab strip shows for one tab.</summary>
internal interface ITabStripItem
{
    string Title { get; }
    string? ToolTip { get; }
    /// <summary>The location's own icon in the system's small image list, or -1 to draw <see cref="Glyph"/> instead.</summary>
    int IconIndex { get; }
    char Glyph { get; }
}

/// <summary>
/// Windows 11 File Explorer's tabs, in the strip at the top of the window: each open folder or search as a tab (its
/// icon, its name and a close button), then the + button. The selected tab takes the address row's colour and joins
/// the row under it; the others sit on the strip, separated by thin lines. Sizes were measured from Explorer: tabs 8px
/// below the top with 8px rounded corners, up to 240px wide, the close button 22px from the tab's edge. The strip's
/// empty space still moves the window: those points are left to the form (HTTRANSPARENT), which reports them as caption.
/// </summary>
internal sealed class TabStrip : Control
{
    const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1, HTCLIENT = 1;
    const char GAdd = '', GClose = '';

    readonly ToolTip _tip = new() { ShowAlways = true };
    IReadOnlyList<ITabStripItem> _tabs = [];
    int _selected, _hover = -1, _pressed = -1;
    bool _hoverClose, _pressedClose, _hoverAdd, _pressedAdd, _active = true;
    int _dragIndex = -1;
    bool _dragging;
    Point _dragStart;
    string? _tipText;
    Font? _font;
    int _fontDpi;

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // clicking a tab leaves the focus where it was, like Explorer
        TabStop = false;
        BackColor = ExplorerTheme.TitleBar;
        AccessibleName = "Tabs";
        AccessibleRole = AccessibleRole.PageTabList;
    }

    /// <summary>The user picked a tab.</summary>
    public event Action<int>? TabClicked;
    /// <summary>The tab's close button, or a middle click on the tab.</summary>
    public event Action<int>? CloseClicked;
    public event Action? NewTabClicked;
    /// <summary>A tab was dragged to another place: (from, to).</summary>
    public event Action<int, int>? TabMoved;
    /// <summary>Right click on a tab: (tab, screen point).</summary>
    public event Action<int, Point>? TabMenuRequested;

    /// <summary>The tabs, read as they are now each time the strip paints (the owner keeps the list).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<ITabStripItem> Tabs { get => _tabs; set { _tabs = value; Invalidate(); } }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    /// <summary>Whether the window is active (tab text is dimmed while it isn't, like the caption buttons').</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get => _active; set { if (_active != value) { _active = value; Invalidate(); } } }

    /// <summary>Repaints after a tab's name or icon, or the list, changed.</summary>
    public void TabsChanged()
    {
        Invalidate();
        UpdateToolTip();
    }

    int S(int v) => LogicalToDeviceUnits(v);

    // ---- layout ---------------------------------------------------------------------------------

    /// <summary>The bottom line's row: the strip is 40 high plus this 1px line, which the selected tab cuts through.</summary>
    int LineY => Height - 1;
    int TabTop => S(8);

    /// <summary>Tabs share the width equally, up to 240 each, keeping room for the + button and some space to drag by.</summary>
    int TabWidth()
    {
        int n = Math.Max(1, _tabs.Count);
        int room = Width - S(8) - S(42) - S(40);
        return Math.Clamp(room / n, S(40), S(240));
    }

    Rectangle TabRect(int i)
    {
        int w = TabWidth();
        return new Rectangle(S(8) + i * w, TabTop, w, LineY - TabTop);
    }

    Rectangle AddRect() => new(S(8) + _tabs.Count * TabWidth() + S(4), S(10), S(38), S(30));

    /// <summary>Tabs this narrow (many tabs in a small window) show their icon alone; the selected one, its close button.</summary>
    bool IconOnly(Rectangle tab) => tab.Width < S(56);

    /// <summary>The close button: 28 by 24, its centre 22 from the tab's right edge. Narrow tabs keep it only when selected.</summary>
    Rectangle CloseRect(int i)
    {
        var r = TabRect(i);
        if (r.Width < S(72) && i != _selected) return Rectangle.Empty;
        int cy = r.Top + r.Height / 2;
        int x = IconOnly(r) ? r.Left + (r.Width - S(28)) / 2 : r.Right - S(8) - S(28);
        return new Rectangle(x, cy - S(12), S(28), S(24));
    }

    int TabAt(Point p)
    {
        for (int i = 0; i < _tabs.Count; i++) if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    // ---- painting -------------------------------------------------------------------------------

    /// <summary>Explorer's strip edge: black at 10% (dark) / 5.9% (light) over the strip, which the selected tab's outline continues.</summary>
    internal static Color LineColor => ExplorerTheme.Dark ? Color.FromArgb(0x1D, 0x1D, 0x1D) : Color.FromArgb(0xE5, 0xE5, 0xE5);
    static Color HoverFill => ExplorerTheme.Dark ? Color.FromArgb(0x28, 0x28, 0x28) : Color.FromArgb(0xF9, 0xF9, 0xF9);
    static Color SeparatorColor => ExplorerTheme.Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xD6, 0xD6, 0xD6);

    Font TabFont()
    {
        if (_font is null || _fontDpi != DeviceDpi)
        {
            _font?.Dispose();
            _font = new Font(ExplorerTheme.UiFamily, S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = DeviceDpi;
        }
        return _font;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var line = new Pen(LineColor)) g.DrawLine(line, 0, LineY, Width, LineY);

        for (int i = 0; i < _tabs.Count; i++)
        {
            var r = TabRect(i);
            bool selected = i == _selected;
            if (selected) PaintSelectedShape(g, r);
            else if (i == _hover || i == _pressed)
            {
                using var path = TopRounded(r, S(8));
                using var fill = new SolidBrush(HoverFill);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(fill, path);
                g.SmoothingMode = SmoothingMode.Default;
            }
            else if (i + 1 < _tabs.Count && i + 1 != _selected && i + 1 != _hover)
            {
                // A thin line between two plain tabs, as in Explorer.
                using var sep = new Pen(SeparatorColor);
                int cy = r.Top + r.Height / 2;
                g.DrawLine(sep, r.Right - 1, cy - S(8), r.Right - 1, cy + S(8));
            }
            PaintTabContent(g, i, r);
        }

        var add = AddRect();
        if (_hoverAdd || _pressedAdd) ExplorerTheme.FillRounded(g, add, S(4), _pressedAdd ? ExplorerTheme.Pressed : ExplorerTheme.Hover);
        ExplorerTheme.DrawGlyph(g, GAdd, add, S(12), _active ? ExplorerTheme.Text : ExplorerTheme.SecondaryText);
    }

    /// <summary>The selected tab: rounded at the top, flaring into the bottom line with small outward curves, outlined like Explorer's.</summary>
    void PaintSelectedShape(Graphics g, Rectangle r)
    {
        // Half-pixel edges keep the 1px outline crisp.
        float l = r.Left + 0.5f, t = r.Top + 0.5f, rt = r.Right - 0.5f, rad = S(8), ear = S(4);
        GraphicsPath Shape(float bottom)
        {
            var p = new GraphicsPath();
            p.AddArc(l - 2 * ear, bottom - 2 * ear, 2 * ear, 2 * ear, 90, -90);
            p.AddArc(l, t, 2 * rad, 2 * rad, 180, 90);
            p.AddArc(rt - 2 * rad, t, 2 * rad, 2 * rad, 270, 90);
            p.AddArc(rt, bottom - 2 * ear, 2 * ear, 2 * ear, 180, -90);
            return p;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = Shape(Height))
        using (var brush = new SolidBrush(ExplorerTheme.Chrome))
        {
            fill.CloseFigure();
            g.FillPath(brush, fill);
        }
        using (var outline = Shape(LineY + 0.5f))
        using (var pen = new Pen(LineColor))
            g.DrawPath(pen, outline);
        g.SmoothingMode = SmoothingMode.Default;
    }

    static GraphicsPath TopRounded(Rectangle r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddLine(r.Left, r.Bottom, r.Left, r.Top + radius);
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddLine(r.Right, r.Top + radius, r.Right, r.Bottom);
        p.CloseFigure();
        return p;
    }

    void PaintTabContent(Graphics g, int i, Rectangle r)
    {
        var tab = _tabs[i];
        Color fore = _active ? ExplorerTheme.Text : ExplorerTheme.SecondaryText;
        var close = CloseRect(i);
        bool iconOnly = IconOnly(r);
        if (!iconOnly || close.IsEmpty)
        {
            int icon = S(16);
            var box = new Rectangle(iconOnly ? r.Left + (r.Width - icon) / 2 : r.Left + S(11), r.Top + (r.Height - icon) / 2, icon, icon);
            if (tab.IconIndex >= 0 && ShellInfo.SmallImageList != IntPtr.Zero)
            {
                var size = ShellInfo.SmallIconSize;
                IntPtr hdc = g.GetHdc();
                try { Native.ImageList_Draw(ShellInfo.SmallImageList, tab.IconIndex, hdc, box.Left + (icon - size.Width) / 2, box.Top + (icon - size.Height) / 2, Native.ILD_TRANSPARENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            else ExplorerTheme.DrawGlyph(g, tab.Glyph, box, S(14), fore);
        }
        if (!iconOnly)
        {
            int textLeft = r.Left + S(37);
            int textRight = close.IsEmpty ? r.Right - S(8) : close.Left;
            if (textRight > textLeft)
                TextRenderer.DrawText(g, tab.Title, TabFont(), new Rectangle(textLeft, r.Top, textRight - textLeft, r.Height), fore,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        if (close.IsEmpty) return;
        bool hot = _hover == i && _hoverClose, down = _pressed == i && _pressedClose;
        if (hot || down) ExplorerTheme.FillRounded(g, close, S(4), down ? ExplorerTheme.Pressed : ExplorerTheme.Hover);
        ExplorerTheme.DrawGlyph(g, GClose, close, S(12), fore);
    }

    // ---- mouse ------------------------------------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            // Tabs and + are ours; the empty strip moves the window (the form answers HTCAPTION for it).
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            m.Result = TabAt(p) >= 0 || AddRect().Contains(p) ? HTCLIENT : HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex >= 0 && e.Button == MouseButtons.Left)
        {
            if (_dragging || Math.Abs(e.X - _dragStart.X) >= SystemInformation.DragSize.Width)
            {
                _dragging = true; // the tab follows the pointer
                int to = Math.Clamp((e.X - S(8)) / Math.Max(1, TabWidth()), 0, _tabs.Count - 1);
                if (to != _dragIndex)
                {
                    int from = _dragIndex;
                    _dragIndex = _pressed = _hover = to;
                    TabMoved?.Invoke(from, to);
                    Invalidate();
                }
            }
            return;
        }
        SetHover(e.Location);
    }

    void SetHover(Point p)
    {
        int hover = TabAt(p);
        bool close = hover >= 0 && CloseRect(hover).Contains(p);
        bool add = AddRect().Contains(p);
        if (hover == _hover && close == _hoverClose && add == _hoverAdd) return;
        bool tabChanged = hover != _hover;
        (_hover, _hoverClose, _hoverAdd) = (hover, close, add);
        Invalidate();
        if (tabChanged || add) UpdateToolTip();
    }

    void UpdateToolTip()
    {
        string? text = _hoverAdd ? "New tab (Ctrl+T)" : _hover >= 0 && _hover < _tabs.Count ? _tabs[_hover].ToolTip ?? _tabs[_hover].Title : null;
        if (text == _tipText) return;
        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        (_hover, _hoverClose, _hoverAdd) = (-1, false, false);
        _tipText = null;
        _tip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        SetHover(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (_hoverAdd) { _pressedAdd = true; Invalidate(); return; }
            if (_hover < 0) return;
            _pressed = _hover;
            _pressedClose = _hoverClose;
            if (!_pressedClose)
            {
                if (_hover != _selected) TabClicked?.Invoke(_hover);
                _dragIndex = _hover;
                _dragStart = e.Location;
            }
            Invalidate();
        }
        else if (e.Button == MouseButtons.Middle && _hover >= 0) _pressed = _hover;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int pressed = _pressed;
        bool pressedClose = _pressedClose, pressedAdd = _pressedAdd;
        _pressed = _dragIndex = -1;
        _pressedClose = _pressedAdd = _dragging = false;
        SetHover(e.Location);
        Invalidate();
        if (e.Button == MouseButtons.Left)
        {
            if (pressedAdd && _hoverAdd) NewTabClicked?.Invoke();
            else if (pressedClose && pressed >= 0 && pressed == _hover && _hoverClose) CloseClicked?.Invoke(pressed);
        }
        else if (e.Button == MouseButtons.Middle && pressed >= 0 && pressed == _hover) CloseClicked?.Invoke(pressed);
        else if (e.Button == MouseButtons.Right && _hover >= 0) TabMenuRequested?.Invoke(_hover, PointToScreen(e.Location));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _font?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- accessibility ----------------------------------------------------------------------------

    protected override AccessibleObject CreateAccessibilityInstance() => new StripAccessible(this);

    sealed class StripAccessible(TabStrip strip) : ControlAccessibleObject(strip)
    {
        public override int GetChildCount() => strip._tabs.Count;
        public override AccessibleObject? GetChild(int index) => (uint)index < (uint)strip._tabs.Count ? new TabAccessible(strip, index) : null;
    }

    sealed class TabAccessible(TabStrip strip, int index) : AccessibleObject
    {
        public override string? Name => index < strip._tabs.Count ? strip._tabs[index].Title : null;
        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleStates State => AccessibleStates.Selectable | (index == strip._selected ? AccessibleStates.Selected : 0);
        public override Rectangle Bounds => strip.RectangleToScreen(strip.TabRect(index));
        public override AccessibleObject? Parent => strip.AccessibilityObject;
        public override string DefaultAction => "Switch";
        public override void DoDefaultAction() { if (index != strip._selected) strip.TabClicked?.Invoke(index); }
    }
}
