namespace Anything;

/// <summary>The things the getting-started window teaches, ticked off as the user does them.</summary>
internal enum GettingStarted { Hide, Show, Everything, Thumbnails, Subfolders, Sort, Folders }

/// <summary>
/// Shown once, right after installing (the first time a window opens), and from Help › Get started: the hotkey drawn
/// as keys, then a short checklist (hide and bring back with the hotkey, searching everything from a folder,
/// Thumbnails, subfolders, sorting, choosing folders) ticked off as each happens. After an update that adds steps, it
/// shows once more with only those ("New in Rock Preview"), so nobody who updates misses them. It sits in the corner of
/// the screen, on top, without taking the focus, so the hotkey hides and brings back the window the user is looking at.
/// </summary>
internal sealed class GetStartedForm : Form
{
    /// <param name="Tour">The <see cref="TourVersion"/> the step came in: an update shows it to those who saw an older tour.</param>
    sealed record Step(GettingStarted Kind, string Title, string? Hint, int Tour = 0);

    /// <summary>The tour this version teaches. 1: clicking the folder's name in the search box to search everything.</summary>
    public const int TourVersion = 1;

    readonly AnythingApp _app;
    readonly string _hotkey;
    readonly string[] _keys;
    readonly bool _whatsNew;
    readonly Step[] _steps;
    readonly HashSet<GettingStarted> _done = [];
    readonly Rectangle[] _stepRects;
    readonly Button _gotIt = new() { Text = "Got it", UseVisualStyleBackColor = true };
    readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 10_000 };
    Font? _heading, _key, _body, _bold, _small;
    Icon? _icon;
    Rectangle _iconRect, _headingRect, _keysRect, _bodyRect, _tryRect;

    const int LogicalWidth = 420, Pad = 20;
    const char GlyphTodo = '', GlyphDone = ''; // RadioBtnOff, CompletedSolid
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, CS_DROPSHADOW = 0x20000;

    static Color Success => ExplorerTheme.Dark ? Color.FromArgb(0x6C, 0xCB, 0x5F) : Color.FromArgb(0x0F, 0x7B, 0x0F);
    static Color KeyFace => ExplorerTheme.Dark ? Color.FromArgb(0x3C, 0x3C, 0x3C) : Color.White;
    static Color KeyEdge => ExplorerTheme.Dark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : Color.FromArgb(0xCC, 0xCC, 0xCC);
    static Color KeyBorder => ExplorerTheme.Dark ? Color.FromArgb(0x50, 0x50, 0x50) : Color.FromArgb(0xD6, 0xD6, 0xD6);

    /// <param name="newSince">Only the steps newer than this tour (after an update), or null for the whole tour.</param>
    public GetStartedForm(AnythingApp app, string hotkey, int? newSince = null)
    {
        _app = app;
        _hotkey = hotkey;
        _keys = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Step[] all =
        [
            new(GettingStarted.Hide, $"Hide it: press {hotkey}", null),
            new(GettingStarted.Show, $"Bring it back: press {hotkey} again", null),
            new(GettingStarted.Everything, "In a folder, click its name in the search box", "To search everything; click Everything to go back", Tour: 1),
            new(GettingStarted.Thumbnails, "Open a folder, then click Thumbnails", "Every file in it and its subfolders, as pictures"),
            new(GettingStarted.Subfolders, "Click the arrow next to Thumbnails", "Include subfolders, or show this folder only"),
            new(GettingStarted.Sort, "Click Sort to put them in order", "For example by date, newest first"),
            new(GettingStarted.Folders, "Click the gear, then Folders", "Search more folders, or leave some out"),
        ];
        _steps = newSince is { } seen ? all.Where(s => s.Tour > seen).ToArray() : all;
        _whatsNew = newSince is not null && _steps.Length > 0;
        if (_steps.Length == 0) _steps = all;
        _stepRects = new Rectangle[_steps.Length];
        Text = "Get started";
        AccessibleName = _whatsNew
            ? $"New in Rock Preview: {string.Join(". ", _steps.Select(s => s.Hint is null ? s.Title : $"{s.Title}, {s.Hint}"))}."
            : $"Rock Preview is ready. Press {hotkey} in any app to open it, and again to hide it.";
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
        _app.StepTried += OnStepTried;
        Relayout();
    }

    bool AllDone => _done.Count == _steps.Length;

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

    void OnStepTried(GettingStarted step)
    {
        if (!_done.Add(step)) return;
        if (AllDone && !_closeTimer.Enabled)
        {
            _gotIt.Text = "Done";
            _closeTimer.Start();
        }
        Invalidate();
    }

    void Relayout()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        foreach (var f in new[] { _heading, _key, _body, _bold, _small }) f?.Dispose();
        _heading = new Font(ExplorerTheme.UiFamily, S(16), FontStyle.Bold, GraphicsUnit.Pixel);
        _key = new Font(ExplorerTheme.UiFamily, S(17), FontStyle.Bold, GraphicsUnit.Pixel);
        _body = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        _bold = new Font(ExplorerTheme.UiFamily, S(13), FontStyle.Bold, GraphicsUnit.Pixel);
        _small = new Font(ExplorerTheme.UiFamily, S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        _icon?.Dispose();
        _icon = new Icon(_app.AppIcon, S(20), S(20));

        int width = S(LogicalWidth), pad = S(Pad), inner = width - 2 * pad, y = S(18);
        _iconRect = new Rectangle(pad, y + S(2), S(20), S(20));
        _headingRect = new Rectangle(pad + S(30), y, inner - S(30), S(24));
        y += S(24) + S(16);
        if (!_whatsNew)
        {
            _keysRect = new Rectangle(pad, y, inner, S(46));
            y += S(46) + S(12);
        }
        _bodyRect = new Rectangle(pad, y, inner, S(36));
        y += S(36) + S(10);
        _tryRect = new Rectangle(pad, y, inner, S(20));
        y += S(24);
        for (int i = 0; i < _steps.Length; i++)
        {
            int h = _steps[i].Hint is null ? S(28) : S(44);
            _stepRects[i] = new Rectangle(pad, y, inner, h);
            y += h;
        }
        y += S(12);
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
        const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (_icon is not null) g.DrawIcon(_icon, _iconRect);
        string heading = AllDone ? "You've got it!" : _whatsNew ? "New in Rock Preview" : "Rock Preview is ready";
        TextRenderer.DrawText(g, heading, _heading, _headingRect, ExplorerTheme.Text, line);
        if (!_whatsNew) DrawKeys(g);
        string body = _whatsNew
            ? AllDone ? "Help › Get Started shows this again, with the rest of the tour." : "Rock Preview was updated. Here's how to use what's new."
            : AllDone ? $"{_hotkey} works in every app. You can change it in Settings › Keyboard."
            : $"Press {_hotkey} in any app to open Rock Preview, and press it again to hide it.";
        TextRenderer.DrawText(g, body, _body, _bodyRect, ExplorerTheme.SecondaryText, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, _steps.Length == 1 ? "Try this" : "Try these", _bold, _tryRect, ExplorerTheme.Text, line);
        for (int i = 0; i < _steps.Length; i++) DrawStep(g, _stepRects[i], _steps[i], _done.Contains(_steps[i].Kind));
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

    void DrawStep(Graphics g, Rectangle r, Step step, bool done)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int titleHeight = S(20);
        ExplorerTheme.DrawGlyph(g, done ? GlyphDone : GlyphTodo, new Rectangle(r.X, r.Y + S(2), S(18), titleHeight), S(16), done ? Success : ExplorerTheme.SecondaryText);
        var text = new Rectangle(r.X + S(28), r.Y + S(2), r.Width - S(28), titleHeight);
        TextRenderer.DrawText(g, step.Title, _body, text, done ? ExplorerTheme.SecondaryText : ExplorerTheme.Text, line | TextFormatFlags.VerticalCenter);
        if (step.Hint is { } hint)
            TextRenderer.DrawText(g, hint, _small, new Rectangle(text.X, text.Bottom, text.Width, S(18)), ExplorerTheme.SecondaryText, line | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.StepTried -= OnStepTried;
            _closeTimer.Dispose();
            foreach (var f in new[] { _heading, _key, _body, _bold, _small }) f?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
