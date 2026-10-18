using System.ComponentModel;

namespace Anything;

/// <summary>
/// Windows 11 File Explorer's search box: a rounded field hosting a borderless TextBox, with the magnifier on the
/// right (a clear button once there is text) and an accent underline while focused.
/// </summary>
internal sealed class SearchField : Control
{
    readonly TextBox _box;
    bool _hover, _glyphHover, _glyphDown;

    /// <summary>The clear (×) button was clicked.</summary>
    public event Action? ClearClicked;

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
        ApplyColors();
    }

    /// <summary>The field's height for the current DPI (32 logical, like Explorer).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PreferredFieldHeight => LogicalToDeviceUnits(32);

    public void ApplyColors()
    {
        _box.BackColor = _box.Focused ? ExplorerTheme.FieldFillFocused : ExplorerTheme.FieldFill;
        _box.ForeColor = ExplorerTheme.Text;
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
        int left = LogicalToDeviceUnits(11);
        int right = GlyphRect.Width + LogicalToDeviceUnits(4);
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
