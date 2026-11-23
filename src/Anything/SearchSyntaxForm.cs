using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Search;

namespace Anything;

/// <summary>
/// Help (F1): how to use Rock Preview, its keys and the search syntax, in one window with a strip at the top that jumps
/// to each part. One for the whole program and modeless, so a search can be typed while reading: F1 again brings it to
/// the front at the top (Search options' Search syntax, at that part), and moves it to the window that asked when that
/// is another one. It opens under the search box, lined up with its right edge, inside that window, and never off the
/// screen. Its size and place are kept for this session only. Esc closes it; Ctrl+1, 2 and 3 jump to the parts.
/// </summary>
internal sealed class SearchSyntaxForm : Form
{
    const int WantedWidth = 720, MinWidth = 420, MinHeight = 240, Gap = 8; // logical pixels (client area)

    static SearchSyntaxForm? s_open;
    // Kept for this session, not in the settings file: the client size in logical pixels, and where it last was for
    // which window (a reopened help goes back there while that window is on the same screen).
    static Size? s_logicalSize;
    static (WeakReference<Form> Owner, Rectangle Bounds, string Screen)? s_place;

    readonly SearchHelpView _view = new() { Dock = DockStyle.Fill, TabIndex = 1 };
    readonly HelpStrip _strip = new(HelpContent.StripLabels) { Dock = DockStyle.Top, TabIndex = 0 };
    Form? _ownerForm;
    Func<Rectangle?> _searchBox = () => null;
    bool _hiddenWithOwner;
    string? _hotkey;

    SearchSyntaxForm()
    {
        Text = "Help - Rock Preview";
        AutoScaleMode = AutoScaleMode.None; // everything is in pixels from DeviceDpi, as in Settings
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = ExplorerTheme.TitleBar;
        ForeColor = ExplorerTheme.Text;
        KeyPreview = true;
        Controls.Add(_view);
        Controls.Add(_strip);
        _view.BringToFront(); // the strip docks first, at the top; the help fills the rest under it
        _strip.Chosen += (part, keyboard) =>
        {
            _view.ShowPart(part);
            if (!keyboard) _view.Focus(); // clicked: the scrolling keys go on working in the help
        };
        _view.CurrentPartChanged += (_, _) => _strip.Current = _view.CurrentPart;
    }

    /// <summary>Opens the help at its top for <paramref name="owner"/>, or brings the open one there (F1).</summary>
    /// <param name="hotkey">The shortcut that opens Rock Preview now, for the help to name; null when none works.</param>
    public static void ShowHelp(MainForm owner, string? hotkey) => Show(owner, owner.SearchBoxScreenBounds, owner.TryInSearchBox, hotkey, HelpContent.HowTo);

    /// <summary>Opens the help at its Search syntax part (Search options' Search syntax).</summary>
    public static void ShowSyntax(MainForm owner, string? hotkey) => Show(owner, owner.SearchBoxScreenBounds, owner.TryInSearchBox, hotkey, HelpContent.Syntax);

    /// <param name="searchBox">The owner's search box on the screen, or null when it has none showing.</param>
    /// <param name="tryText">Puts a double-clicked name or example in the owner's search box; null: nothing is tried.</param>
    /// <param name="part">The part to show at the top (<see cref="HelpContent.HowTo"/>...).</param>
    internal static SearchSyntaxForm Show(Form owner, Func<Rectangle?> searchBox, Action<string, HelpInsert>? tryText,
                                          string? hotkey = null, int part = HelpContent.HowTo)
    {
        if (s_open is { IsDisposed: false } open)
        {
            open.SetHotkey(hotkey);
            if (!ReferenceEquals(open._ownerForm, owner))
            {
                open.Attach(owner, searchBox, tryText);
                open.MoveTo(open.PlaceFor(owner, open.LogicalClientSize()), owner.DeviceDpi);
            }
            if (!open.Visible) open.Show(owner);
            open.Activate();
            open._view.ShowPart(part);
            return open;
        }
        var help = new SearchSyntaxForm();
        help.SetHotkey(hotkey);
        help.Attach(owner, searchBox, tryText);
        // Placed before the window exists, so Windows makes it on that screen at that screen's scaling.
        help.Bounds = help.Remembered(owner) ?? help.PlaceFor(owner, s_logicalSize ?? new Size(WantedWidth, 0));
        help.MinimumSize = help.MinimumFor(owner.DeviceDpi, help.Size);
        s_open = help;
        help.Show(owner);
        help._view.ShowPart(part);
        return help;
    }

    /// <summary>The help names the shortcut set now: made again only when it changed.</summary>
    void SetHotkey(string? hotkey)
    {
        if (_contentMade && hotkey == _hotkey) return;
        _hotkey = hotkey;
        _contentMade = true;
        _view.Content = HelpContent.Build(hotkey);
    }

    bool _contentMade;

    /// <summary>
    /// Closes the help when it belongs to <paramref name="owner"/>. For a window closed into the background (Run in
    /// background hides it instead of closing it): the user closed it, so its help must not come back when it is shown
    /// again; only the hotkey's hiding brings the help back with its window.
    /// </summary>
    internal static void CloseFor(Form owner)
    {
        if (s_open is { IsDisposed: false } help && ReferenceEquals(help._ownerForm, owner)) help.Close();
    }

    /// <summary>Whether <paramref name="hwnd"/> is the help shown for <paramref name="owner"/> (the hotkey hides both).</summary>
    internal static bool IsHelpOf(Form owner, IntPtr hwnd) =>
        s_open is { IsDisposed: false, Visible: true, IsHandleCreated: true } help && help.Handle == hwnd && ReferenceEquals(help._ownerForm, owner);

    /// <summary>The open help, for the window probe.</summary>
    internal static SearchSyntaxForm? Current => s_open is { IsDisposed: false } f ? f : null;

    internal SearchHelpView View => _view;

    internal HelpStrip Strip => _strip;

    void Attach(Form owner, Func<Rectangle?> searchBox, Action<string, HelpInsert>? tryText)
    {
        if (_ownerForm is { } old)
        {
            old.VisibleChanged -= OwnerVisibleChanged;
            old.FormClosed -= OwnerClosed;
        }
        _ownerForm = owner;
        _searchBox = searchBox;
        _view.TryText = tryText;
        _hiddenWithOwner = false;
        Owner = owner;
        owner.VisibleChanged += OwnerVisibleChanged;
        owner.FormClosed += OwnerClosed;
    }

    // Windows doesn't hide an owned window with its owner, so the hotkey hiding the window would leave the help
    // floating alone: it hides with its window and comes back with it.
    void OwnerVisibleChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || _ownerForm is null) return;
        if (!_ownerForm.Visible && Visible)
        {
            _hiddenWithOwner = true;
            Hide();
        }
        else if (_ownerForm.Visible && _hiddenWithOwner)
        {
            _hiddenWithOwner = false;
            Show();
        }
    }

    void OwnerClosed(object? sender, FormClosedEventArgs e)
    {
        if (!IsDisposed) Close();
    }

    // ---- size and place -------------------------------------------------------------------------------

    Size LogicalClientSize() => new(ClientSize.Width * 96 / DeviceDpi, ClientSize.Height * 96 / DeviceDpi);

    /// <summary>The window's outer size for a client size in logical pixels at <paramref name="dpi"/>.</summary>
    Size OuterSize(Size logicalClient, int dpi)
    {
        var cp = CreateParams;
        var r = new RECT { Right = logicalClient.Width * dpi / 96, Bottom = logicalClient.Height * dpi / 96 };
        try { AdjustWindowRectExForDpi(ref r, cp.Style, false, cp.ExStyle, (uint)dpi); }
        catch (EntryPointNotFoundException) { } // before Windows 10 1607: the client size alone, still on the screen
        return new Size(r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Where the help goes for <paramref name="owner"/>; a height of 0 asks for the whole help's height.</summary>
    Rectangle PlaceFor(Form owner, Size logicalClient)
    {
        int dpi = owner.DeviceDpi;
        int S(int v) => v * dpi / 96;
        var screen = Screen.FromControl(owner);
        var ownerBounds = owner.Bounds;
        // Without a search box showing, a stand-in at the window's top right, where the box would be.
        var box = _searchBox() ?? new Rectangle(ownerBounds.Right - S(16) - S(300), ownerBounds.Top + S(40), S(300), S(32));
        int clientWidth = S(Math.Max(MinWidth, logicalClient.Width));
        int clientHeight = logicalClient.Height > 0 ? S(logicalClient.Height) : HeightAt(_view.Content, clientWidth, dpi);
        var outer = OuterSize(new Size(clientWidth * 96 / dpi, clientHeight * 96 / dpi), dpi);
        var minimum = OuterSize(new Size(MinWidth, MinHeight), dpi);
        var r = HelpWindowPlacement.Place(Box(ownerBounds), Box(box), Box(screen.WorkingArea),
            (outer.Width, outer.Height), (minimum.Width, minimum.Height), S(Gap));
        return new Rectangle(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>The strip and the whole help's height at a client width (with the scroll bar it will then need), at a scaling.</summary>
    static int HeightAt(IReadOnlyList<HelpPart> parts, int clientWidth, int dpi) =>
        HelpStrip.HeightFor(dpi) + SearchHelpView.ContentHeightAt(parts, dpi, clientWidth - SystemInformation.GetVerticalScrollBarWidthForDpi(dpi));

    Rectangle? _moving;

    /// <summary>
    /// Moves it to bounds already worked out for the scaling of the screen they are on. Moved to a screen with another
    /// scaling, Windows suggests the old size scaled once more; that suggestion is not taken.
    /// </summary>
    void MoveTo(Rectangle bounds, int dpi)
    {
        _moving = bounds;
        // The minimum from the old screen's scaling would hold the new bounds back (wider on a screen with less
        // scaling), so it is lifted for the move and set for the new scaling after it.
        MinimumSize = Size.Empty;
        try { Bounds = bounds; }
        finally
        {
            _moving = null;
            MinimumSize = MinimumFor(dpi, bounds.Size);
        }
    }

    /// <summary>
    /// The minimum size at a scaling, but never more than <paramref name="placed"/>: on a screen too small for the
    /// minimum the help is placed smaller, and a larger minimum would push it off the screen again.
    /// </summary>
    Size MinimumFor(int dpi, Size placed)
    {
        var m = OuterSize(new Size(MinWidth, MinHeight), dpi);
        return new Size(Math.Min(m.Width, placed.Width), Math.Min(m.Height, placed.Height));
    }

    static HelpWindowPlacement.Box Box(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Where it last was for this window, if that window is still on the same screen.</summary>
    Rectangle? Remembered(Form owner)
    {
        if (s_place is not { } p || !p.Owner.TryGetTarget(out var was) || !ReferenceEquals(was, owner)) return null;
        var screen = Screen.FromControl(owner);
        if (screen.DeviceName != p.Screen || !screen.WorkingArea.IntersectsWith(p.Bounds)) return null;
        var area = Box(screen.WorkingArea);
        var r = HelpWindowPlacement.Clamp(Box(p.Bounds), area);
        return new Rectangle(r.X, r.Y, r.Width, r.Height);
    }

    void Remember()
    {
        if (!IsHandleCreated || WindowState != FormWindowState.Normal) return;
        s_logicalSize = LogicalClientSize();
        if (_ownerForm is { } owner) s_place = (new WeakReference<Form>(owner), Bounds, Screen.FromHandle(Handle).DeviceName);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Dragged to a screen with another scaling, Windows' suggested size is taken (the same size to the eye); moved
        // there by MoveTo, the size was already made for that screen.
        if (_moving is not null)
        {
            e.Cancel = true; // MoveTo sets the minimum for the new scaling itself
            base.OnDpiChanged(e);
            return;
        }
        // The old scaling's minimum must not hold back Windows' suggested size on the new screen (larger than it there
        // when the new screen has less scaling), so it is lifted first and set for the new scaling after.
        MinimumSize = Size.Empty;
        base.OnDpiChanged(e);
        MinimumSize = MinimumFor(e.DeviceDpiNew, Size);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _view.Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!_view.Focused && !_strip.Focused) _view.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = e.SuppressKeyPress = true; return; }
        // F1 in the help itself: it is already showing (and must not reach the window behind it).
        if (e.KeyCode == Keys.F1) { e.Handled = e.SuppressKeyPress = true; return; }
        // Ctrl+C and Ctrl+Insert copy the whole help wherever the focus is, the strip too.
        if (IsCopyAll(e.KeyData))
        {
            SearchHelpView.CopyText(_view.AllText());
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        // Ctrl+1, 2, 3 from anywhere in the window: the parts, as the strip's buttons.
        if (e.Modifiers == Keys.Control && PartForDigit(e.KeyCode) is int part and >= 0 && part < HelpContent.StripLabels.Count)
        {
            _view.ShowPart(part);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Whether a key copies the whole help: Ctrl+C or Ctrl+Insert, with or without Shift, not with Alt.</summary>
    internal static bool IsCopyAll(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.C or Keys.Insert
        && (keyData & Keys.Control) != 0 && (keyData & Keys.Alt) == 0;

    static int PartForDigit(Keys key) => key switch
    {
        >= Keys.D1 and <= Keys.D9 => key - Keys.D1,
        >= Keys.NumPad1 and <= Keys.NumPad9 => key - Keys.NumPad1,
        _ => -1,
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Remember();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_ownerForm is { } owner)
        {
            owner.VisibleChanged -= OwnerVisibleChanged;
            owner.FormClosed -= OwnerClosed;
        }
        if (ReferenceEquals(s_open, this)) s_open = null;
        base.OnFormClosed(e); // a modeless form is disposed by WinForms once closed
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AdjustWindowRectExForDpi(ref RECT rect, int style, bool menu, int exStyle, uint dpi);
}

/// <summary>
/// The strip at the top of the help, which does not scroll away: a button per part, drawn as Settings draws its list of
/// pages (the part being read has the pill and the accent mark). A click or Enter / Space scrolls that part to the top;
/// Tab reaches the strip as one stop, on the button of the part being read, and Left and Right move between its buttons. Screen readers hear a tool bar of
/// push buttons with the parts' names, the one being read as pressed.
/// </summary>
internal sealed class HelpStrip : Control
{
    readonly IReadOnlyList<string> _labels;
    Rectangle[] _buttons = [];
    int _current, _focus, _hot = -1;
    Font _font;
    int _fontDpi;

    /// <summary>A button was used: the part, and whether it was the keyboard (the focus then stays on the strip).</summary>
    public event Action<int, bool>? Chosen;

    public HelpStrip(IReadOnlyList<string> labels)
    {
        _labels = labels;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = ExplorerTheme.TitleBar;
        AccessibleName = "Parts of the help";
        AccessibleRole = AccessibleRole.ToolBar;
        _font = MakeFont(DeviceDpi);
        _fontDpi = DeviceDpi;
        Height = HeightFor(DeviceDpi);
    }

    static Font MakeFont(int dpi) => new(ExplorerTheme.UiFamily, (float)Math.Round(14 * dpi / 96.0), FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>The strip's height at a scaling, in device pixels.</summary>
    internal static int HeightFor(int dpi) => (int)Math.Round(48 * dpi / 96.0);

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    /// <summary>The part being read: its button is drawn as the current one.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Current
    {
        get => _current;
        set
        {
            if (value == _current) return;
            _current = value;
            if (!Focused) _focus = value; // the keyboard comes back to the part being read
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, value);
        }
    }

    /// <summary>The button the keyboard is on.</summary>
    internal int FocusIndex => _focus;

    /// <summary>Each button's place, from the left: lined up with the help's own margin under it.</summary>
    internal IReadOnlyList<Rectangle> Buttons
    {
        get
        {
            if (_buttons.Length != _labels.Count) Arrange();
            return _buttons;
        }
    }

    void Arrange()
    {
        int x = S(24) - S(12), h = S(32), top = (Height - S(1) - h) / 2;
        var list = new Rectangle[_labels.Count];
        for (int i = 0; i < _labels.Count; i++)
        {
            int w = TextRenderer.MeasureText(_labels[i], _font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 2 * S(12);
            list[i] = new Rectangle(x, top, w, h);
            x += w + S(4);
        }
        _buttons = list;
    }

    void RemakeFont()
    {
        if (_fontDpi == DeviceDpi) return;
        _font.Dispose();
        _font = MakeFont(DeviceDpi);
        _fontDpi = DeviceDpi;
        Height = HeightFor(DeviceDpi);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RemakeFont();
        _buttons = [];
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RemakeFont();
        _buttons = [];
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _buttons = [];
    }

    protected override void OnPaint(PaintEventArgs e) => PaintStrip(e.Graphics, Focused && ShowFocusCues);

    /// <summary>Draws the strip; also for checking its look in a picture, with or without the keyboard's mark.</summary>
    internal void PaintStrip(Graphics g, bool focusMark)
    {
        using (var back = new SolidBrush(ExplorerTheme.TitleBar)) g.FillRectangle(back, ClientRectangle);
        using (var line = new Pen(SettingsForm.CardBorder)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        var buttons = Buttons;
        for (int i = 0; i < buttons.Count; i++)
        {
            var r = buttons[i];
            bool current = i == _current;
            // As Settings' list of pages: the page shown has a pill and a short accent mark (here under the text).
            if (current) ExplorerTheme.FillRounded(g, r, S(4), ExplorerTheme.Dark ? Color.FromArgb(0x2D, 0x2D, 0x2D) : Color.FromArgb(0xEA, 0xEA, 0xEA));
            else if (i == _hot) ExplorerTheme.FillRounded(g, r, S(4), ExplorerTheme.MenuHover);
            if (current)
                ExplorerTheme.FillRounded(g, new Rectangle(r.X + r.Width / 2 - S(8), r.Bottom - S(3), S(16), S(3)), S(2), ExplorerTheme.Accent);
            TextRenderer.DrawText(g, _labels[i], _font, r, current || i == _hot ? ExplorerTheme.Text : ExplorerTheme.SecondaryText,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (focusMark && i == _focus)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = ExplorerTheme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(4));
                using var pen = new Pen(ExplorerTheme.Text, Math.Max(1, S(2)));
                g.DrawPath(pen, path);
                g.SmoothingMode = old;
            }
        }
    }

    int ButtonAt(Point p)
    {
        var buttons = Buttons;
        for (int i = 0; i < buttons.Count; i++) if (buttons[i].Contains(p)) return i;
        return -1;
    }

    /// <summary>Uses button <paramref name="index"/> as a click or a key would.</summary>
    internal void Choose(int index, bool keyboard)
    {
        if (index < 0 || index >= _labels.Count) return;
        _focus = index;
        Invalidate();
        Chosen?.Invoke(index, keyboard);
    }

    void MoveFocus(int index)
    {
        index = Math.Clamp(index, 0, _labels.Count - 1);
        if (index == _focus) return;
        _focus = index;
        Invalidate();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Focus, index);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Left: MoveFocus(_focus - 1); break;
            case Keys.Right: MoveFocus(_focus + 1); break;
            case Keys.Home: MoveFocus(0); break;
            case Keys.End: MoveFocus(_labels.Count - 1); break;
            case Keys.Enter:
            case Keys.Space: Choose(_focus, keyboard: true); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        _focus = _current; // the strip's focus starts on the part being read
        base.OnGotFocus(e);
        Invalidate();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Focus, _focus);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = ButtonAt(e.Location);
        if (hot == _hot) return;
        _hot = hot;
        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot < 0) return;
        _hot = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ButtonAt(e.Location) is var i and >= 0) Choose(i, keyboard: false);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new StripAccessible(this);

    sealed class StripAccessible(HelpStrip strip) : ControlAccessibleObject(strip)
    {
        public override AccessibleRole Role => AccessibleRole.ToolBar;
        public override int GetChildCount() => strip._labels.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < strip._labels.Count ? new ButtonAccessible(strip, this, index) : null;
        public override AccessibleObject? GetFocused() => strip.Focused ? GetChild(strip._focus) : null;
    }

    sealed class ButtonAccessible(HelpStrip strip, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => strip._labels[index];
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override AccessibleObject? Parent => parent;
        public override string? DefaultAction => "Press";
        public override void DoDefaultAction() => strip.Choose(index, keyboard: true);
        public override AccessibleStates State =>
            AccessibleStates.Focusable
            | (strip.Focused && strip._focus == index ? AccessibleStates.Focused : 0)
            | (strip._current == index ? AccessibleStates.Pressed : 0);
        public override Rectangle Bounds => strip.RectangleToScreen(strip.Buttons[index]);
        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Right when index + 1 < strip._labels.Count => parent.GetChild(index + 1),
            AccessibleNavigation.Previous or AccessibleNavigation.Left when index > 0 => parent.GetChild(index - 1),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
