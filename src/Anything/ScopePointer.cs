using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything;

/// <summary>
/// A small callout for those who haven't used the scope chip yet, its arrow pointing at it at the start of the search
/// box: "This is where Rock Preview searches", what the chip means now, and Got it. Shown on two starts at most
/// (<see cref="ScopePointerRules"/>); Got it or a click on the chip ends it for good. It never takes the focus, so typing
/// in the search box goes on, and it follows nothing: Got it, Esc, a click on the chip, a menu, the window losing the
/// focus, moving, resizing or closing, the chip moving, or 20 seconds close it.
/// </summary>
internal sealed class ScopePointer : Form, IMessageFilter
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_NCLBUTTONDOWN = 0xA1, WM_NCRBUTTONDOWN = 0xA4;
    const int VK_ESCAPE = 0x1B, VK_APPS = 0x5D;

    // Logical sizes (96 DPI), scaled to the owner's.
    const int LogicalWidth = 300, Pad = 16, ArrowHeight = 8, ArrowHalf = 9, Radius = 8;

    readonly MainForm _owner;
    readonly SearchField _field;
    readonly string _line;
    readonly double _scale;
    readonly Font _title, _body;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    readonly long _shownAt = Environment.TickCount64;
    Rectangle _chipAt, _titleRect, _lineRect, _buttonRect, _bodyRect;
    bool _below = true, _buttonHot, _buttonDown, _filtering, _closing;
    int _arrowX;

    /// <summary>It closes by itself after this long.</summary>
    const int LifetimeMs = 20_000;

    /// <summary>Closed by its Got it button: the user has read it, so it never shows again.</summary>
    public bool GotIt { get; private set; }

    ScopePointer(MainForm owner, SearchField field, string line)
    {
        _owner = owner;
        _field = field;
        _line = line;
        _scale = owner.DeviceDpi / 96.0;
        _title = new Font(ExplorerTheme.UiFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel);
        _body = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        Text = ScopePointerRules.Title;
        AccessibleName = $"{ScopePointerRules.Title}. {line}";
        AccessibleRole = AccessibleRole.Alert;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ExplorerTheme.MenuBack;
        ForeColor = ExplorerTheme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Measure();
        _watch.Tick += (_, _) => Guard(Watch);
    }

    int S(int v) => (int)Math.Round(v * _scale);

    /// <summary>
    /// Shows the pointer under (or over) the search box of <paramref name="owner"/>; null when it can't be placed so, or
    /// anything fails (it is only a hint).
    /// </summary>
    public static ScopePointer? TryShow(MainForm owner, SearchField field, string line)
    {
        ScopePointer? p = null;
        try
        {
            var chip = field.ScopeChipOnScreen;
            if (chip.IsEmpty) return null;
            p = new ScopePointer(owner, field, line);
            var area = Screen.FromRectangle(chip).WorkingArea;
            var placed = ScopePointerRules.Place(Box(chip), Box(field.RectangleToScreen(field.ClientRectangle)), Box(area),
                p.Width, p.Height, p.S(4), p.S(Radius + ArrowHalf + 4));
            if (placed is not { } at) { p.Dispose(); return null; }
            p._chipAt = chip;
            p.Arrange(at);
            p.Show(owner);
            p.Begin();
            return p;
        }
        catch (Exception)
        {
            try { p?.Dispose(); } catch (Exception) { }
            return null;
        }
    }

    static ScopePointerRules.Box Box(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The callout's size for its text: the arrow's room, the title, the line as it wraps, the button.</summary>
    void Measure()
    {
        int width = S(LogicalWidth), pad = S(Pad), inner = width - 2 * pad;
        int titleH = TextRenderer.MeasureText("Ag", _title, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
        int lineH = TextRenderer.MeasureText(_line, _body, new Size(inner, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
        int y = S(14);
        _titleRect = new Rectangle(pad, y, inner, titleH);
        y += titleH + S(4);
        _lineRect = new Rectangle(pad, y, inner, lineH);
        y += lineH + S(12);
        _buttonRect = new Rectangle(width - pad - S(80), y, S(80), S(30));
        y += _buttonRect.Height + S(14);
        _bodyRect = new Rectangle(0, 0, width, y);
        Size = new Size(width, y + S(ArrowHeight));
    }

    /// <summary>Puts the arrow on the side towards the chip and gives the window its shape.</summary>
    void Arrange(ScopePointerRules.Placement at)
    {
        _below = at.Below;
        _arrowX = at.ArrowX;
        int arrow = S(ArrowHeight);
        if (_below)
        {
            // The arrow on top: everything moves down by its height.
            _bodyRect.Offset(0, arrow);
            _titleRect.Offset(0, arrow);
            _lineRect.Offset(0, arrow);
            _buttonRect.Offset(0, arrow);
        }
        Bounds = new Rectangle(at.Bounds.X, at.Bounds.Y, at.Bounds.Width, at.Bounds.Height);
        using var shape = Outline(0);
        Region = new Region(shape);
    }

    /// <summary>The rounded body with its arrow, <paramref name="inset"/> inside the window's edge.</summary>
    GraphicsPath Outline(float inset)
    {
        var b = new RectangleF(_bodyRect.X + inset, _bodyRect.Y + inset, _bodyRect.Width - 2 * inset - (inset > 0 ? 1 : 0), _bodyRect.Height - 2 * inset - (inset > 0 ? 1 : 0));
        float r = S(Radius), half = S(ArrowHalf), h = S(ArrowHeight), x = _arrowX;
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddArc(b.X, b.Y, 2 * r, 2 * r, 180, 90);
        if (_below)
        {
            path.AddLine(x - half, b.Y, x, b.Y - h + inset);
            path.AddLine(x, b.Y - h + inset, x + half, b.Y);
        }
        path.AddArc(b.Right - 2 * r, b.Y, 2 * r, 2 * r, 270, 90);
        path.AddArc(b.Right - 2 * r, b.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
        if (!_below)
        {
            path.AddLine(x + half, b.Bottom, x, b.Bottom + h - inset);
            path.AddLine(x, b.Bottom + h - inset, x - half, b.Bottom);
        }
        path.AddArc(b.X, b.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    void Begin()
    {
        Application.AddMessageFilter(this);
        _filtering = true;
        _owner.Deactivate += OnOwnerChanged;
        _owner.Move += OnOwnerChanged;
        _owner.Resize += OnOwnerChanged;
        _owner.VisibleChanged += OnOwnerChanged;
        _owner.FormClosing += OnOwnerClosing;
        _watch.Start();
        AccessibilityNotifyClients(AccessibleEvents.SystemAlert, -1);
    }

    void OnOwnerChanged(object? sender, EventArgs e) => Dismiss();
    void OnOwnerClosing(object? sender, FormClosingEventArgs e) => Dismiss();

    /// <summary>Closes it, once, whatever state it is in.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Guard(() => { if (!IsDisposed) Close(); });
        Guard(Detach);
    }

    void Detach()
    {
        _watch.Stop();
        if (_filtering) { Application.RemoveMessageFilter(this); _filtering = false; }
        _owner.Deactivate -= OnOwnerChanged;
        _owner.Move -= OnOwnerChanged;
        _owner.Resize -= OnOwnerChanged;
        _owner.VisibleChanged -= OnOwnerChanged;
        _owner.FormClosing -= OnOwnerClosing;
    }

    /// <summary>Its time is up, or the chip moved or went: it points at nothing now.</summary>
    void Watch()
    {
        if (Environment.TickCount64 - _shownAt >= LifetimeMs || _owner.IsDisposed || _field.ScopeChipOnScreen != _chipAt) Dismiss();
    }

    /// <summary>Never lets a hint throw into the message loop.</summary>
    static void Guard(Action a)
    {
        try { a(); }
        catch (Exception) { }
    }

    /// <summary>
    /// Keys and clicks for the whole app while it shows: Esc closes it and does nothing else, except in an edit box that is
    /// not the search box (a rename, the address bar), where that Esc goes on to cancel it too. A menu on its way closes it.
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        try
        {
            if (_closing) return false;
            switch (m.Msg)
            {
                case WM_KEYDOWN when (int)(long)m.WParam == VK_ESCAPE:
                    Dismiss();
                    return EscIsOnlyForMe(m.HWnd);
                case WM_KEYDOWN when (int)(long)m.WParam == VK_APPS:
                case WM_SYSKEYDOWN:
                    Dismiss(); // a menu by the keyboard (Alt, F10, the menu key)
                    break;
                case WM_RBUTTONDOWN or WM_NCRBUTTONDOWN when m.HWnd != Handle:
                    Dismiss(); // a shortcut menu
                    break;
                case WM_LBUTTONDOWN or WM_NCLBUTTONDOWN when m.HWnd != Handle:
                    // The chip (which switches what it points at) or a bar or menu, whose buttons open menus.
                    if (m.HWnd == _field.ScopeChipHandle || FromHandle(m.HWnd) is ToolStrip) Dismiss();
                    break;
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>
    /// True (the Esc is swallowed) unless the key went to an edit box that is not the search box: a rename box, the
    /// address bar or the folder view's own rename edit (a plain Windows "Edit"), which the Esc cancels.
    /// </summary>
    bool EscIsOnlyForMe(IntPtr target) => ScopePointerRules.EscOnlyClosesPointer(EscTargetOf(target));

    ScopePointerRules.EscTarget EscTargetOf(IntPtr target) =>
        FromHandle(target) is TextBox box
            ? box.Parent == _field ? ScopePointerRules.EscTarget.SearchBox : ScopePointerRules.EscTarget.OtherTextBox
            : IsNativeEdit(target) ? ScopePointerRules.EscTarget.NativeEdit : ScopePointerRules.EscTarget.Other;

    static bool IsNativeEdit(IntPtr hwnd)
    {
        var name = new char[16];
        int n = GetClassName(hwnd, name, name.Length);
        return n > 0 && new string(name, 0, n).Equals("Edit", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, char[] name, int max);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE; // a click on it leaves the focus in the search box
            return;
        }
        try { base.WndProc(ref m); }
        catch (Exception) when (m.Msg != 0x0002 /* WM_DESTROY */) { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Guard(() => ExplorerTheme.ApplyPopupFrame(this));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Dismiss(); // on another screen now: not where the chip is
    }

    static Color ButtonFace(bool hot, bool down)
    {
        var a = ExplorerTheme.Accent;
        if (!hot && !down) return a;
        // Windows 11's accent button: a little lighter under the pointer (darker in dark mode), more when pressed.
        float k = down ? 0.2f : 0.1f;
        return ExplorerTheme.Dark
            ? Color.FromArgb((int)(a.R * (1 - k)), (int)(a.G * (1 - k)), (int)(a.B * (1 - k)))
            : Color.FromArgb(a.R + (int)((255 - a.R) * k), a.G + (int)((255 - a.G) * k), a.B + (int)((255 - a.B) * k));
    }

    /// <summary>Text on the accent: white, or black on a light accent (dark mode's).</summary>
    static Color OnAccent(Color face) => face.R * 0.299 + face.G * 0.587 + face.B * 0.114 > 150 ? Color.Black : Color.White;

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            var g = e.Graphics;
            g.Clear(ExplorerTheme.MenuBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var edge = Outline(0.5f))
            using (var pen = new Pen(ExplorerTheme.MenuBorder, 1f))
                g.DrawPath(pen, edge);
            var face = ButtonFace(_buttonHot, _buttonDown);
            ExplorerTheme.FillRounded(g, _buttonRect, S(4), face);
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, ScopePointerRules.Title, _title, _titleRect, ExplorerTheme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _line, _body, _lineRect, ExplorerTheme.SecondaryText, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, "Got it", _body, _buttonRect, OnAccent(face),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        catch (Exception) { }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hot = _buttonRect.Contains(e.Location);
        if (hot == _buttonHot) return;
        _buttonHot = hot;
        Invalidate(_buttonRect);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _buttonHot = _buttonDown = false;
        Invalidate(_buttonRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_buttonRect.Contains(e.Location)) return;
        _buttonDown = true;
        Invalidate(_buttonRect);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool click = _buttonDown && _buttonRect.Contains(e.Location);
        _buttonDown = false;
        Invalidate(_buttonRect);
        if (!click) return;
        GotIt = true;
        Dismiss();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Guard(Detach);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Guard(Detach);
            _watch.Dispose();
            _title.Dispose();
            _body.Dispose();
        }
        base.Dispose(disposing);
    }
}
