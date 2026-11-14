namespace Anything;

/// <summary>
/// A USB drive was plugged in: a small window in the corner of the screen, on top, without taking the focus, offers
/// to open it in one click (in place of Windows' own AutoPlay, see <see cref="Interop.AutoPlayBlocker"/>). It goes by
/// itself after a while, or when the drive does.
/// </summary>
internal sealed class UsbPromptForm : Form
{
    readonly Button _open = new() { Text = "Open", UseVisualStyleBackColor = true };
    readonly Button _notNow = new() { Text = "Not now", UseVisualStyleBackColor = true };
    readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 20_000 };
    readonly string _drive, _name;
    Font? _heading, _body;
    Rectangle _glyphRect, _headingRect, _bodyRect;

    const int LogicalWidth = 340, Pad = 18;
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, CS_DROPSHADOW = 0x20000;
    const char GlyphUsb = '\uE88E'; // Segoe Fluent Icons: USB

    /// <summary>Open was clicked.</summary>
    public event Action? OpenClicked;

    /// <summary>The drive this offers ("E:\").</summary>
    public string Drive => _drive;

    public UsbPromptForm(string drive, string name)
    {
        _drive = drive;
        _name = name;
        Text = "USB drive";
        AccessibleName = $"{name} was plugged in. Open it in Rock Preview?";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ExplorerTheme.MenuBack;
        ForeColor = ExplorerTheme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _open.Click += (_, _) => { OpenClicked?.Invoke(); Close(); };
        _notNow.Click += (_, _) => Close();
        Controls.Add(_open);
        Controls.Add(_notNow);
        _closeTimer.Tick += (_, _) => Close();
        Relayout();
    }

    /// <summary>Shows it in the bottom-right corner of the screen <paramref name="anchor"/> is on (or the main one).</summary>
    public void ShowNear(Form? anchor)
    {
        var area = (anchor is { IsDisposed: false } ? Screen.FromControl(anchor) : Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int margin = LogicalToDeviceUnits(16);
        Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
        Show();
        _closeTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyPopupFrame(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    void Relayout()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        _heading?.Dispose();
        _body?.Dispose();
        _heading = new Font(ExplorerTheme.UiFamily, S(15), FontStyle.Bold, GraphicsUnit.Pixel);
        _body = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        int width = S(LogicalWidth), pad = S(Pad);
        _glyphRect = new Rectangle(pad, S(18), S(28), S(28));
        int textLeft = _glyphRect.Right + S(12);
        _headingRect = new Rectangle(textLeft, S(16), width - textLeft - pad, S(22));
        _bodyRect = new Rectangle(textLeft, _headingRect.Bottom + S(2), width - textLeft - pad, S(20));
        int y = _bodyRect.Bottom + S(16);
        foreach (var b in new[] { _open, _notNow })
        {
            b.Font = _body;
            b.Size = new Size(S(96), S(32));
        }
        _notNow.Location = new Point(width - pad - _notNow.Width, y);
        _open.Location = new Point(_notNow.Left - S(8) - _open.Width, y);
        ClientSize = new Size(width, y + _open.Height + S(16));
        AcceptButton = _open;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        ExplorerTheme.DrawGlyph(g, GlyphUsb, _glyphRect, LogicalToDeviceUnits(24), ExplorerTheme.Blue);
        TextRenderer.DrawText(g, "USB drive plugged in", _heading, _headingRect, ExplorerTheme.Text, line);
        TextRenderer.DrawText(g, $"Open {_name} in Rock Preview?", _body, _bodyRect, ExplorerTheme.SecondaryText, line);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
            _heading?.Dispose();
            _body?.Dispose();
        }
        base.Dispose(disposing);
    }
}
