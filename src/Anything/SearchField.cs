using System.ComponentModel;

namespace Anything;

/// <summary>
/// Windows 11 File Explorer's search box: a rounded field hosting a borderless TextBox, with the magnifier on the
/// right (a clear button once there is text) and an accent underline while focused. At its start, where a search
/// looks: "Everything" in blue, or the folder.
/// </summary>
internal sealed class SearchField : Control
{
    readonly TextBox _box;
    readonly ScopeChip _chip = new();
    readonly ToolTip _tip = new();
    bool _hover, _glyphHover, _glyphDown;

    /// <summary>The clear (×) button was clicked.</summary>
    public event Action? ClearClicked;

    /// <summary>The scope chip was clicked.</summary>
    public event Action? ScopeClicked;

    public SearchField(TextBox box)
    {
        _box = box;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.IBeam;
        box.BorderStyle = BorderStyle.None;
        box.Margin = Padding.Empty;
        Controls.Add(box);
        box.TextChanged += (_, _) => Invalidate();
        box.GotFocus += (_, _) => ApplyColors();
        box.LostFocus += (_, _) => ApplyColors();
        box.MouseEnter += (_, _) => SetHover(true);
        box.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(MousePosition)));
        // The window decides afresh what a click does: what the chip last showed may be a moment old.
        _chip.Click += (_, _) => ScopeClicked?.Invoke();
        Controls.Add(_chip);
        ApplyColors();
    }

    /// <summary>Where a search looks: <paramref name="text"/> ("Everything" in blue, or a folder's name) and what clicking it does.</summary>
    public void SetScope(string text, bool everything, bool clickable, string tip)
    {
        if (_chip.Text == text && _chip.Everything == everything && _chip.Clickable == clickable && _chip.AccessibleName == tip) return;
        _chip.Text = text;
        _chip.Everything = everything;
        _chip.Clickable = clickable;
        _chip.Cursor = clickable ? Cursors.Hand : Cursors.Default;
        _chip.AccessibleName = tip;
        _tip.SetToolTip(_chip, tip);
        PerformLayout();
        _chip.Invalidate();
    }

    /// <summary>The field's height for the current DPI (32 logical, like Explorer).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PreferredFieldHeight => LogicalToDeviceUnits(32);

    public void ApplyColors()
    {
        _box.BackColor = _box.Focused ? ExplorerTheme.FieldFillFocused : ExplorerTheme.FieldFill;
        _box.ForeColor = ExplorerTheme.Text;
        _chip.BackColor = _box.BackColor;
        _chip.Invalidate();
        Invalidate();
    }

    Rectangle GlyphRect
    {
        get
        {
            int w = LogicalToDeviceUnits(32);
            return new Rectangle(Width - w - LogicalToDeviceUnits(2), LogicalToDeviceUnits(3), w, Height - LogicalToDeviceUnits(6));
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int right = GlyphRect.Width + LogicalToDeviceUnits(4);
        // The chip at the start, at most about half the field, so a long folder name leaves room to type.
        int inset = LogicalToDeviceUnits(5), chipH = Math.Max(1, Height - 2 * inset);
        int chipW = Math.Min(_chip.PreferredChipWidth(), Math.Max(LogicalToDeviceUnits(40), (Width - right) * 45 / 100));
        _chip.SetBounds(inset, (Height - chipH) / 2, chipW, chipH);
        int left = _chip.Right + LogicalToDeviceUnits(7);
        int h = _box.PreferredHeight;
        _box.SetBounds(left, Math.Max(0, (Height - h) / 2 + LogicalToDeviceUnits(1)), Math.Max(10, Width - left - right), h);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        PerformLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Parent?.BackColor ?? ExplorerTheme.Chrome)) g.FillRectangle(bg, ClientRectangle);
        float radius = LogicalToDeviceUnits(4);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        bool focused = _box.Focused;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = ExplorerTheme.RoundedRect(r, radius))
        {
            using var fill = new SolidBrush(focused ? ExplorerTheme.FieldFillFocused : ExplorerTheme.FieldFill);
            g.FillPath(fill, path);
            // No border, like Explorer's search box (its border brush is the background).
        }
        if (focused)
        {
            // Accent underline, clipped to the rounded shape like a Windows 11 text field.
            int t = Math.Max(2, LogicalToDeviceUnits(2));
            using var clip = ExplorerTheme.RoundedRect(new RectangleF(0, 0, Width, Height), radius);
            var old = g.Clip;
            g.SetClip(clip);
            using var accent = new SolidBrush(ExplorerTheme.Accent);
            g.FillRectangle(accent, 0, Height - t, Width, t);
            g.Clip = old;
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

        var gr = GlyphRect;
        bool hasText = _box.TextLength > 0;
        if (hasText && (_glyphHover || _glyphDown))
            ExplorerTheme.FillRounded(g, gr, LogicalToDeviceUnits(4), _glyphDown ? ExplorerTheme.Pressed : ExplorerTheme.Hover);
        ExplorerTheme.DrawGlyph(g, hasText ? ExplorerTheme.GClose : ExplorerTheme.GSearch, gr,
            LogicalToDeviceUnits(hasText ? 10 : 12), ExplorerTheme.SecondaryText);
    }

    void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(true);
        bool over = GlyphRect.Contains(e.Location) && _box.TextLength > 0;
        Cursor = over ? Cursors.Default : Cursors.IBeam;
        if (over != _glyphHover) { _glyphHover = over; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _glyphHover = _glyphDown = false;
        SetHover(_box.ClientRectangle.Contains(_box.PointToClient(MousePosition)));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (GlyphRect.Contains(e.Location) && _box.TextLength > 0) { _glyphDown = true; Invalidate(); return; }
        _box.Focus();
        _box.SelectionStart = _box.TextLength;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_glyphDown) return;
        _glyphDown = false;
        Invalidate();
        if (GlyphRect.Contains(e.Location)) ClearClicked?.Invoke();
    }
}

/// <summary>
/// The search box's scope: "Everything" in blue, or the folder a search looks inside (with a folder mark); a button
/// that switches between the two.
/// </summary>
internal sealed class ScopeChip : Control
{
    bool _hover, _down;

    public ScopeChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        // Every click switches: a second one soon after the first would otherwise be a double-click, and no Click.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Everything { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Clickable { get; set; }

    int Pad => LogicalToDeviceUnits(8);
    int GlyphSize => LogicalToDeviceUnits(12);

    /// <summary>The whole text, its padding and (for a folder) the folder mark.</summary>
    public int PreferredChipWidth() =>
        2 * Pad + (Everything ? 0 : GlyphSize + LogicalToDeviceUnits(5)) + TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var blue = ExplorerTheme.Blue;
        int lift = Clickable && (_hover || _down) ? 0x14 : 0;
        Color fill = Everything ? Color.FromArgb((ExplorerTheme.Dark ? 0x2E : 0x1C) + lift, blue)
            : Color.FromArgb((ExplorerTheme.Dark ? 0x18 : 0x0F) + lift, ExplorerTheme.Text);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        ExplorerTheme.FillRounded(g, new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(4), fill);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        int x = Pad;
        if (!Everything)
        {
            ExplorerTheme.DrawGlyph(g, ExplorerTheme.GFolder, new Rectangle(x, 0, GlyphSize, Height), GlyphSize, ExplorerTheme.SecondaryText);
            x += GlyphSize + LogicalToDeviceUnits(5);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, Width - x - Pad / 2), Height), Everything ? blue : ExplorerTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _down = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _down = false;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }
}
