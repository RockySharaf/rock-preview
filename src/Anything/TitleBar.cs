namespace Anything;

/// <summary>
/// Minimize / maximize / close for a window without a title bar (Windows 11 File Explorer style: the top row of the
/// window holds them). The form does the hit testing (see MainForm.TitleBar.cs): the buttons report themselves to
/// Windows as its caption buttons, so hovering maximize still shows the snap layouts.
/// </summary>
internal sealed class CaptionButtons : Control
{
    public const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
    const char GMinimize = '', GMaximize = '', GRestore = '', GClose = '';
    static readonly Color CloseHot = Color.FromArgb(0xC4, 0x2B, 0x1C), ClosePressed = Color.FromArgb(0xC8, 0x3C, 0x31);

    int _hover, _pressed;
    bool _active = true, _maximized;

    public CaptionButtons()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        BackColor = ExplorerTheme.Chrome;
    }

    /// <summary>Glyph size in pixels (10 at 100%).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int GlyphSize { get; set; } = 10;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Hover { get => _hover; set { if (_hover != value) { _hover = value; Invalidate(); } } }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Pressed { get => _pressed; set { if (_pressed != value) { _pressed = value; Invalidate(); } } }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get => _active; set { if (_active != value) { _active = value; Invalidate(); } } }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Maximized { get => _maximized; set { if (_maximized != value) { _maximized = value; Invalidate(); } } }

    /// <summary>Which button is at <paramref name="client"/> (HTMINBUTTON, HTMAXBUTTON, HTCLOSE), or 0.</summary>
    public int HitTest(Point client)
    {
        if (!ClientRectangle.Contains(client)) return 0;
        int w = Math.Max(1, Width / 3);
        return (client.X / w) switch { 0 => HTMINBUTTON, 1 => HTMAXBUTTON, _ => HTCLOSE };
    }

    public static bool IsButton(int ht) => ht is HTMINBUTTON or HTMAXBUTTON or HTCLOSE;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int w = Width / 3;
        int[] codes = [HTMINBUTTON, HTMAXBUTTON, HTCLOSE];
        for (int i = 0; i < 3; i++)
        {
            var r = new Rectangle(i * w, 0, i == 2 ? Width - 2 * w : w, Height);
            int ht = codes[i];
            bool hot = _hover == ht, down = _pressed == ht;
            Color fore = _active ? ExplorerTheme.Text : ExplorerTheme.SecondaryText;
            if (ht == HTCLOSE && (hot || down))
            {
                using var b = new SolidBrush(down ? ClosePressed : CloseHot);
                g.FillRectangle(b, r);
                fore = Color.White;
            }
            else if (hot || down)
            {
                using var b = new SolidBrush(down ? ExplorerTheme.Pressed : ExplorerTheme.Hover);
                g.FillRectangle(b, r);
            }
            char glyph = ht switch { HTMINBUTTON => GMinimize, HTMAXBUTTON => _maximized ? GRestore : GMaximize, _ => GClose };
            ExplorerTheme.DrawGlyph(g, glyph, r, GlyphSize, fore);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // The form answers for this area, so Windows treats these as the window's own caption buttons.
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }
}

/// <summary>A panel whose empty space belongs to the window's title area: dragging it moves the window.</summary>
/// <summary>
/// The title row's start: Rock Preview's mark and name, small, so anyone looking at the window can tell it from File
/// Explorer's. Like the strip's empty space it moves the window (the form hit-tests it as the caption).
/// </summary>
internal sealed class BrandBadge : Control
{
    const string Name_ = "Rock Preview";

    public BrandBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AccessibleName = Name_;
        AccessibleRole = AccessibleRole.StaticText;
    }

    int MarkHeight => LogicalToDeviceUnits(15);

    /// <summary>Left gap, the mark, a gap, the name, and a gap before the first tab.</summary>
    public int PreferredWidth()
    {
        int markW = (int)Math.Round(MarkHeight * Anything.Preview.Brand.Aspect);
        return LogicalToDeviceUnits(12) + markW + LogicalToDeviceUnits(7) + TextRenderer.MeasureText(Name_, Font).Width + LogicalToDeviceUnits(6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        // The row's tabs sit on its bottom line; the badge is centred in the space above it.
        int rowH = Height - 1, x = LogicalToDeviceUnits(12);
        var color = ExplorerTheme.Dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x67, 0xC0);
        if (Anything.Preview.Brand.Tinted(color, MarkHeight) is { } mark)
        {
            e.Graphics.DrawImage(mark, x, (rowH - mark.Height) / 2);
            x += mark.Width + LogicalToDeviceUnits(7);
        }
        TextRenderer.DrawText(e.Graphics, Name_, Font, new Rectangle(x, 0, Width - x, rowH), ExplorerTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 /* WM_NCHITTEST */) { m.Result = -1 /* HTTRANSPARENT: the form decides */; return; }
        base.WndProc(ref m);
    }
}

internal sealed class DragPanel : Panel
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 /* WM_NCHITTEST */) { m.Result = -1 /* HTTRANSPARENT: the form decides */; return; }
        base.WndProc(ref m);
    }
}
