namespace Anything;

/// <summary>
/// Shown once, right after installing (the first time Rock Preview opens a window): the hotkey drawn as keys and two
/// things to try with it, ticked off as they happen. It sits in the corner of the screen, on top, without taking the
/// focus, so the hotkey hides and brings back the window the user is looking at.
/// </summary>
internal sealed class HotkeyWelcomeForm : Form
{
    readonly AnythingApp _app;
    readonly string _hotkey;
    readonly string[] _keys;
    readonly Button _gotIt = new() { Text = "Got it", UseVisualStyleBackColor = true };
    readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 10_000 };
    bool _hidden, _opened;
    Font? _heading, _key, _body, _bold;
    Icon? _icon;
    Rectangle _iconRect, _headingRect, _keysRect, _bodyRect, _tryRect, _hideRect, _openRect;

    const int LogicalWidth = 360, Pad = 20;
    const char GlyphTodo = '', GlyphDone = ''; // RadioBtnOff, CompletedSolid
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, CS_DROPSHADOW = 0x20000;

    static Color Success => ExplorerTheme.Dark ? Color.FromArgb(0x6C, 0xCB, 0x5F) : Color.FromArgb(0x0F, 0x7B, 0x0F);
    static Color KeyFace => ExplorerTheme.Dark ? Color.FromArgb(0x3C, 0x3C, 0x3C) : Color.White;
    static Color KeyEdge => ExplorerTheme.Dark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : Color.FromArgb(0xCC, 0xCC, 0xCC);
    static Color KeyBorder => ExplorerTheme.Dark ? Color.FromArgb(0x50, 0x50, 0x50) : Color.FromArgb(0xD6, 0xD6, 0xD6);

    public HotkeyWelcomeForm(AnythingApp app, string hotkey)
    {
        _app = app;
        _hotkey = hotkey;
        _keys = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Text = "Rock Preview";
        AccessibleName = $"Rock Preview is ready. Press {hotkey} in any app to open it, and again to hide it.";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ExplorerTheme.MenuBack;
        ForeColor = ExplorerTheme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _gotIt.Click += (_, _) => Close();
        Controls.Add(_gotIt);
        _closeTimer.Tick += (_, _) => Close();
        _app.HotkeyUsed += OnHotkeyUsed;
        Relayout();
    }

    /// <summary>Shows it in the bottom-right corner of the screen <paramref name="anchor"/> is on.</summary>
    public void ShowNear(Form? anchor)
    {
        var area = (anchor is not null ? Screen.FromControl(anchor) : Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int margin = LogicalToDeviceUnits(16);
        Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
        Show();
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

    void OnHotkeyUsed(bool opened)
    {
        if (opened) _opened = true; else _hidden = true;
        if (_hidden && _opened && !_closeTimer.Enabled)
        {
            _gotIt.Text = "Done";
            _closeTimer.Start();
        }
        Invalidate();
    }

    void Relayout()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        foreach (var f in new[] { _heading, _key, _body, _bold }) f?.Dispose();
        _heading = new Font(ExplorerTheme.UiFamily, S(16), FontStyle.Bold, GraphicsUnit.Pixel);
        _key = new Font(ExplorerTheme.UiFamily, S(17), FontStyle.Bold, GraphicsUnit.Pixel);
        _body = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        _bold = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Bold, GraphicsUnit.Pixel);
        _icon?.Dispose();
        _icon = new Icon(_app.AppIcon, S(20), S(20));

        int width = S(LogicalWidth), pad = S(Pad), inner = width - 2 * pad, y = S(18);
        _iconRect = new Rectangle(pad, y + S(2), S(20), S(20));
        _headingRect = new Rectangle(pad + S(30), y, inner - S(30), S(24));
        y += S(24) + S(16);
        _keysRect = new Rectangle(pad, y, inner, S(46));
        y += S(46) + S(12);
        _bodyRect = new Rectangle(pad, y, inner, S(36));
        y += S(36) + S(10);
        _tryRect = new Rectangle(pad, y, inner, S(20));
        y += S(22);
        _hideRect = new Rectangle(pad, y, inner, S(26));
        y += S(26);
        _openRect = new Rectangle(pad, y, inner, S(26));
        y += S(26) + S(14);
        _gotIt.Font = _body;
        _gotIt.Size = new Size(S(96), S(32));
        _gotIt.Location = new Point(width - pad - _gotIt.Width, y);
        ClientSize = new Size(width, y + _gotIt.Height + S(18));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        bool done = _hidden && _opened;
        const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (_icon is not null) g.DrawIcon(_icon, _iconRect);
        TextRenderer.DrawText(g, done ? "You've got it!" : "Rock Preview is ready", _heading, _headingRect, ExplorerTheme.Text, line);
        DrawKeys(g);
        string body = done
            ? $"{_hotkey} works in every app while Rock Preview runs. You can change it in Tools > Options > Keyboard."
            : $"Press {_hotkey} in any app to open Rock Preview, and press it again to hide it.";
        TextRenderer.DrawText(g, body, _body, _bodyRect, ExplorerTheme.SecondaryText, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, "Try it now", _bold, _tryRect, ExplorerTheme.Text, line);
        DrawStep(g, _hideRect, _hidden, $"Hide Rock Preview: press {_hotkey}");
        DrawStep(g, _openRect, _opened, $"Bring it back: press {_hotkey} again");
    }

    /// <summary>The hotkey as keyboard keys: Alt + X.</summary>
    void DrawKeys(Graphics g)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        const TextFormatFlags centred = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        int x = _keysRect.X, h = _keysRect.Height, gap = S(10), edge = S(3);
        int plus = TextRenderer.MeasureText(g, "+", _key, Size.Empty, centred).Width;
        var smoothing = g.SmoothingMode;
        for (int i = 0; i < _keys.Length; i++)
        {
            if (i > 0)
            {
                TextRenderer.DrawText(g, "+", _key, new Rectangle(x, _keysRect.Y, plus, h - edge), ExplorerTheme.SecondaryText, centred);
                x += plus + gap;
            }
            int textWidth = TextRenderer.MeasureText(g, _keys[i], _key, Size.Empty, centred).Width;
            var cap = new Rectangle(x, _keysRect.Y, Math.Max(h, textWidth + S(30)), h);
            var face = new Rectangle(cap.X, cap.Y, cap.Width, cap.Height - edge);
            ExplorerTheme.FillRounded(g, cap, S(6), KeyEdge);   // the key's lower edge shows below its face
            ExplorerTheme.FillRounded(g, face, S(6), KeyFace);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = ExplorerTheme.RoundedRect(new RectangleF(cap.X + 0.5f, cap.Y + 0.5f, cap.Width - 1, cap.Height - 1), S(6)))
            using (var pen = new Pen(KeyBorder))
                g.DrawPath(pen, path);
            g.SmoothingMode = smoothing;
            TextRenderer.DrawText(g, _keys[i], _key, face, ExplorerTheme.Text, centred);
            x = cap.Right + gap;
        }
    }

    void DrawStep(Graphics g, Rectangle r, bool done, string text)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        ExplorerTheme.DrawGlyph(g, done ? GlyphDone : GlyphTodo, new Rectangle(r.X, r.Y, S(18), r.Height), S(16), done ? Success : ExplorerTheme.SecondaryText);
        TextRenderer.DrawText(g, text, _body, new Rectangle(r.X + S(28), r.Y, r.Width - S(28), r.Height),
            done ? ExplorerTheme.SecondaryText : ExplorerTheme.Text,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.HotkeyUsed -= OnHotkeyUsed;
            _closeTimer.Dispose();
            foreach (var f in new[] { _heading, _key, _body, _bold }) f?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
