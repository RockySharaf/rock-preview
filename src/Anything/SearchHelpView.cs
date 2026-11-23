using System.Drawing.Drawing2D;
using Anything.Core.Search;

namespace Anything;

/// <summary>
/// The help (F1) drawn in the Settings window's look: the window's background, per part a big heading, then a rounded
/// card per section under its heading, in each card what a row names at the left (code chips for keys and what to type,
/// a bold name for how to use the program) and the meaning at the right in the program's font, wrapping, with examples
/// as chips under it. Laid out by <see cref="SearchHelpLayout"/> (tested in Core) at the control's width and scaling;
/// every font is made in pixels from <see cref="Control.DeviceDpi"/> and made again when the window moves to a screen
/// with another scaling, so it is right at any scaling. It scrolls with the wheel, the scroll bar and the keys, and
/// <see cref="ShowPart"/> scrolls a part to the top; Ctrl+C copies the whole help, a right-click copies one entry, and a
/// double-click on a name or an example of the search syntax tries it in the search box (<see cref="TryText"/>).
/// </summary>
internal sealed class SearchHelpView : ScrollableControl
{
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    // The Settings window's colours (its background is Explorer's title-bar colour; the cards are SettingsForm's).
    static Color Back => ExplorerTheme.TitleBar;
    static Color CodeFill => ExplorerTheme.Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEC, 0xEC, 0xEC);
    static Color ExampleBorder => ExplorerTheme.Dark ? Color.FromArgb(0x50, 0x50, 0x50) : Color.FromArgb(0xD4, 0xD4, 0xD4);

    static string? s_mono;

    /// <summary>Cascadia Mono (Windows 11), else Consolas (every Windows since Vista), else the system's fixed face.</summary>
    internal static string MonoFamily => s_mono ??= new[] { "Cascadia Mono", "Consolas" }.FirstOrDefault(Has) ?? FontFamily.GenericMonospace.Name;

    static bool Has(string family)
    {
        try { using var f = new FontFamily(family); return true; }
        catch (ArgumentException) { return false; }
    }

    Font[] _fonts;
    int _fontDpi;
    SearchHelpLayout _layout;
    IReadOnlyList<HelpPart> _parts = HelpContent.Build(null);
    HelpBox? _hover;
    readonly ToolTip _tip = new() { ShowAlways = true };
    string _tipText = "";
    int _wheelRest; // part of a wheel turn not scrolled yet (precision touchpads send small steps)
    List<(string Name, AccessibleRole Role, HelpRect Bounds)> _items = [];
    int _currentPart;
    int? _pendingPart; // asked for before the help was laid out at its real width

    /// <summary>A double-click on a name or an example: what to put in the search box and how. Null: nothing is tried.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, HelpInsert>? TryText { get; set; }

    /// <summary>What the help shows: its parts in order (<see cref="HelpContent.Build"/>).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<HelpPart> Content
    {
        get => _parts;
        set
        {
            if (ReferenceEquals(value, _parts)) return;
            _parts = value;
            _layoutFonts = null; // laid out again even at the same width
            Relayout();
        }
    }

    /// <summary>The part whose heading is at the top or above it; <see cref="CurrentPartChanged"/> says when it changes.</summary>
    public int CurrentPart => _currentPart;

    public event EventHandler? CurrentPartChanged;

    public SearchHelpView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
        TabStop = true;
        AutoScroll = true;
        BackColor = Back;
        AccessibleName = "Help";
        AccessibleRole = AccessibleRole.Document;
        _fonts = MakeFonts(DeviceDpi);
        _fontDpi = DeviceDpi;
        _layout = Arrange(_parts, _fonts, DeviceDpi, Math.Max(1, ClientSize.Width));
    }

    // ---- fonts and layout -----------------------------------------------------------------------

    /// <summary>The faces in pixels at a scaling, indexed by <see cref="HelpFont"/>: Settings' sizes (14px text).</summary>
    static Font[] MakeFonts(int dpi)
    {
        float S(int v) => (float)Math.Round(v * dpi / 96.0);
        var fonts = new Font[6];
        fonts[(int)HelpFont.Title] = new Font(ExplorerTheme.UiFamily, S(22), FontStyle.Bold, GraphicsUnit.Pixel);
        fonts[(int)HelpFont.Heading] = new Font(ExplorerTheme.UiFamily, S(15), FontStyle.Bold, GraphicsUnit.Pixel);
        fonts[(int)HelpFont.Small] = new Font(ExplorerTheme.UiFamily, S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        fonts[(int)HelpFont.Body] = new Font(ExplorerTheme.UiFamily, S(14), FontStyle.Regular, GraphicsUnit.Pixel);
        fonts[(int)HelpFont.Code] = new Font(MonoFamily, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        // A row's name in How to use: the text's face in bold, so it reads as a name and not as something to type.
        fonts[(int)HelpFont.Name] = new Font(ExplorerTheme.UiFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel);
        return fonts;
    }

    /// <summary>Measures as the painting draws: the same flags and the same width, so the wrap is the same.</summary>
    static SearchHelpLayout.Measure Measurer(Font[] fonts) => (text, font, maxWidth) =>
    {
        var f = fonts[(int)font];
        var size = maxWidth <= 0
            ? TextRenderer.MeasureText(text, f, Size.Empty, Line)
            : TextRenderer.MeasureText(text, f, new Size(maxWidth, int.MaxValue), Wrap);
        return (size.Width, size.Height);
    };

    static SearchHelpLayout Arrange(IReadOnlyList<HelpPart> parts, Font[] fonts, int dpi, int width) =>
        SearchHelpLayout.Arrange(parts, width, HelpMetrics.Scaled(dpi), Measurer(fonts));

    /// <summary>
    /// The help's whole height at a width and scaling, for the window to size itself to before it exists (when the
    /// view's own fonts are still the system scaling's).
    /// </summary>
    internal static int ContentHeightAt(IReadOnlyList<HelpPart> parts, int dpi, int width)
    {
        var fonts = MakeFonts(dpi);
        try { return Arrange(parts, fonts, dpi, Math.Max(1, width)).Height; }
        finally { foreach (var f in fonts) f.Dispose(); }
    }

    int ScrollY => -AutoScrollPosition.Y;
    int MaxScroll => Math.Max(0, _layout.Height - ClientSize.Height);
    int LineStep => _fonts[(int)HelpFont.Body].Height;

    void ScrollTo(int y)
    {
        y = Math.Clamp(y, 0, MaxScroll);
        if (y == ScrollY) return;
        AutoScrollPosition = new Point(0, y);
        Invalidate();
        UpdateHover(PointToClient(MousePosition));
        UpdateCurrentPart();
    }

    /// <summary>Scrolls part <paramref name="part"/> (<see cref="HelpContent.HowTo"/>...) to the top; the first part is the very top.</summary>
    public void ShowPart(int part)
    {
        if (!IsHandleCreated)
        {
            _pendingPart = part; // scrolled once the help is laid out in its window (Relayout)
            return;
        }
        _pendingPart = null;
        Relayout(); // at the present width first, so the heading is where it will be drawn
        if (part < 0 || part >= _layout.PartTops.Count) return;
        ScrollTo(part == 0 ? 0 : _layout.PartTops[part]);
        UpdateCurrentPart();
    }

    // A heading scrolled to sits at the very top; a few pixels under it still count, so rounding never shows the part before.
    int PartSlack => Math.Max(1, LineStep / 2);

    void UpdateCurrentPart()
    {
        int part = _layout.PartAt(ScrollY, PartSlack);
        // Scrolled to the very end, a part whose heading shows but can't come up to the top is the one being read.
        if (MaxScroll > 0 && ScrollY >= MaxScroll) part = Math.Max(part, _layout.PartAt(ScrollY + ClientSize.Height / 2, 0));
        if (part == _currentPart) return;
        _currentPart = part;
        CurrentPartChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lays the help out again for the present width and scaling, keeping the entry at the top in view.</summary>
    void Relayout()
    {
        if (_fontDpi != DeviceDpi)
        {
            foreach (var f in _fonts) f.Dispose();
            _fonts = MakeFonts(DeviceDpi);
            _fontDpi = DeviceDpi;
        }
        int width = Math.Max(1, ClientSize.Width);
        if (_layout.Width == width && _layout.Boxes.Count > 0 && ReferenceEquals(_layoutFonts, _fonts)) { ApplyPendingPart(); return; }
        // The reader's place: a part's heading jumped to stays at the top; else the row at the top and how far into
        // it, not the pixel offset (wrapping changes heights).
        int scroll = ScrollY;
        int atPart = scroll > 0 ? _layout.PartTops.ToList().IndexOf(scroll) : -1;
        var anchor = _layout.Rows.FirstOrDefault(r => r.Bounds.Bottom > scroll);
        double into = anchor is null || anchor.Bounds.Height == 0 ? 0 : (scroll - anchor.Bounds.Y) / (double)anchor.Bounds.Height;
        bool sameContent = _layout.Sections.Count == _parts.Sum(p => p.Sections.Count);
        _layout = Arrange(_parts, _fonts, _fontDpi, width);
        _layoutFonts = _fonts;
        _hover = null;
        BuildItems();
        AutoScrollMinSize = new Size(0, _layout.Height);
        if (atPart >= 0 && atPart < _layout.PartTops.Count) ScrollTo(_layout.PartTops[atPart]);
        else if (anchor is not null && scroll > 0 && sameContent
                 && _layout.Rows.FirstOrDefault(r => r.Section == anchor.Section && r.Entry == anchor.Entry) is { } now)
            ScrollTo(now.Bounds.Y + (int)Math.Round(Math.Clamp(into, 0, 1) * now.Bounds.Height));
        Invalidate();
        UpdateCurrentPart();
        ApplyPendingPart();
    }

    void ApplyPendingPart()
    {
        if (_pendingPart is { } part && IsHandleCreated) ShowPart(part);
    }

    Font[]? _layoutFonts;

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Relayout();
    }

    // ---- painting --------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var hover = TryText is not null && _hover is { } h && InsertOf(_layout, h) != HelpInsert.None ? h : null;
        PaintHelp(e.Graphics, _layout, _fonts, _fontDpi, ScrollY, e.ClipRectangle, hover);
    }

    /// <summary>
    /// Draws the help scrolled down by <paramref name="scrollY"/> into <paramref name="g"/>: the window and the
    /// pictures for checking the look draw it with this same code.
    /// </summary>
    static void PaintHelp(Graphics g, SearchHelpLayout layout, Font[] fonts, int dpi, int scrollY, Rectangle clip, HelpBox? hover)
    {
        int S(int v) => (int)Math.Round(v * dpi / 96.0);
        using (var back = new SolidBrush(Back)) g.FillRectangle(back, clip);
        foreach (var b in layout.Boxes)
        {
            var r = new Rectangle(b.Bounds.X, b.Bounds.Y - scrollY, b.Bounds.Width, b.Bounds.Height);
            if (!r.IntersectsWith(clip)) continue;
            switch (b.Kind)
            {
                case HelpBoxKind.Title:
                    TextRenderer.DrawText(g, b.Text, fonts[(int)HelpFont.Title], r, ExplorerTheme.Text, Wrap);
                    break;
                case HelpBoxKind.Heading:
                    TextRenderer.DrawText(g, b.Text, fonts[(int)HelpFont.Heading], r, ExplorerTheme.Text, Wrap);
                    break;
                case HelpBoxKind.Intro or HelpBoxKind.Subtitle or HelpBoxKind.Note:
                    TextRenderer.DrawText(g, b.Text, fonts[(int)HelpFont.Small], r, ExplorerTheme.SecondaryText, Wrap);
                    break;
                case HelpBoxKind.Meaning:
                    TextRenderer.DrawText(g, b.Text, fonts[(int)HelpFont.Body], r, ExplorerTheme.Text, Wrap);
                    break;
                case HelpBoxKind.Name:
                    TextRenderer.DrawText(g, b.Text, fonts[(int)HelpFont.Name], r, ExplorerTheme.Text, Wrap);
                    break;
                case HelpBoxKind.Card:
                    DrawCard(g, r, S(6));
                    break;
                case HelpBoxKind.CodeChip or HelpBoxKind.ExampleChip:
                    DrawChip(g, b, r, fonts[(int)HelpFont.Code], S(4), S(6), ReferenceEquals(b, hover));
                    break;
            }
        }
    }

    static void DrawCard(Graphics g, Rectangle r, int radius)
    {
        // As SettingCard draws its cards: the fill and a 1px border on the half pixel, anti-aliased.
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ExplorerTheme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius);
        using var fill = new SolidBrush(SettingsForm.CardFill);
        using var border = new Pen(SettingsForm.CardBorder);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        g.SmoothingMode = old;
    }

    static void DrawChip(Graphics g, HelpBox b, Rectangle r, Font font, int radius, int padX, bool hover)
    {
        var old = g.SmoothingMode;
        if (b.Kind == HelpBoxKind.CodeChip) ExplorerTheme.FillRounded(g, r, radius, CodeFill);
        else
        {
            // An example is outlined, so what to type stands out from what it looks like in use.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = ExplorerTheme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius);
            using var pen = new Pen(ExampleBorder);
            g.DrawPath(pen, path);
        }
        if (hover)
        {
            // Something to double-click: an accent edge while the pointer is on it.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = ExplorerTheme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius);
            using var pen = new Pen(ExplorerTheme.Accent);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = old;
        var text = Rectangle.FromLTRB(r.X + padX, r.Y, Math.Max(r.X + padX, r.Right - padX), r.Bottom);
        TextRenderer.DrawText(g, b.Text, font, text, ExplorerTheme.Text,
            Line | TextFormatFlags.VerticalCenter | (b.Clipped ? TextFormatFlags.EndEllipsis : 0));
    }

    // ---- scrolling and keys ------------------------------------------------------------------------

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space
        || base.IsInputKey(keyData);

    /// <summary>The whole help as plain text, every part: what Copy all and Ctrl+C put on the clipboard.</summary>
    internal string AllText() => HelpContent.PlainText(_parts);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(LineStep, ClientSize.Height - LineStep);
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollTo(ScrollY - LineStep); break;
            case Keys.Down: ScrollTo(ScrollY + LineStep); break;
            case Keys.PageUp: ScrollTo(ScrollY - page); break;
            case Keys.PageDown: ScrollTo(ScrollY + page); break;
            case Keys.Space: ScrollTo(ScrollY + (e.Shift ? -page : page)); break;
            case Keys.Home: ScrollTo(0); break;
            case Keys.End: ScrollTo(MaxScroll); break;
            case Keys.C when e.Control:
            case Keys.Insert when e.Control:
                FileOps.SetClipboardText(AllText());
                break;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                ShowMenuForKeyboard();
                break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Windows' own wheel setting: so many lines a notch, or a page when it says "one screen at a time".
        int lines = SystemInformation.MouseWheelScrollLines;
        int perNotch = lines < 0 ? Math.Max(LineStep, ClientSize.Height - LineStep) : Math.Max(1, lines) * LineStep;
        _wheelRest += e.Delta * perNotch;
        int pixels = _wheelRest / 120;
        _wheelRest -= pixels * 120;
        ScrollTo(ScrollY - pixels);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
        UpdateCurrentPart();
    }

    // ---- the mouse: hover, double-click, right-click -------------------------------------------------

    static HelpInsert InsertOf(SearchHelpLayout layout, HelpBox chip)
    {
        if (chip.Kind == HelpBoxKind.ExampleChip) return HelpInsert.Replace;
        if (chip.Section < 0 || chip.Entry < 0) return HelpInsert.None;
        return layout.Sections[chip.Section].Entries[chip.Entry].Insert;
    }

    void UpdateHover(Point client)
    {
        HelpBox? chip = ClientRectangle.Contains(client) ? _layout.ChipAt(client.X, client.Y + ScrollY) : null;
        if (ReferenceEquals(chip, _hover)) return;
        Invalidate(OnScreen(_hover));
        _hover = chip;
        Invalidate(OnScreen(_hover));
        string tip = TryText is null || chip is null ? "" : InsertOf(_layout, chip) switch
        {
            HelpInsert.Replace => "Double-click to search for this",
            HelpInsert.AtCaret => "Double-click to put this in the search box",
            _ => "",
        };
        if (tip != _tipText) _tip.SetToolTip(this, _tipText = tip);
    }

    Rectangle OnScreen(HelpBox? b) => b is null ? Rectangle.Empty
        : new Rectangle(b.Bounds.X - 2, b.Bounds.Y - ScrollY - 2, b.Bounds.Width + 4, b.Bounds.Height + 4);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateHover(new Point(-1, -1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || TryText is null) return;
        if (_layout.ChipAt(e.X, e.Y + ScrollY) is { } chip && InsertOf(_layout, chip) is var how && how != HelpInsert.None)
            TryText(chip.Text, how);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        int y = e.Y + ScrollY;
        ShowMenu(e.Location, _layout.RowAt(e.X, y), _layout.ChipAt(e.X, y));
    }

    void ShowMenuForKeyboard()
    {
        // The first entry fully in view (or the first one showing at all).
        var row = _layout.Rows.FirstOrDefault(r => r.Bounds.Y >= ScrollY) ?? _layout.Rows.FirstOrDefault(r => r.Bounds.Bottom > ScrollY);
        var at = row is null ? new Point(LogicalToDeviceUnits(24), LogicalToDeviceUnits(24))
            : new Point(row.Bounds.X + LogicalToDeviceUnits(18), row.Bounds.Y - ScrollY + LogicalToDeviceUnits(8));
        ShowMenu(at, row, null);
    }

    void ShowMenu(Point client, HelpRow? row, HelpBox? chip)
    {
        var menu = new ContextMenuStrip();
        ExplorerTheme.StyleDropDown(menu);
        if (row is not null)
        {
            var entry = _layout.Sections[row.Section].Entries[row.Entry];
            menu.Items.Add(new ToolStripMenuItem("&Copy", null, (_, _) => FileOps.SetClipboardText(entry.PlainText())));
        }
        // Only the search syntax has names and examples to try; keys and how-to rows have nothing for the search box.
        if (chip is not null && TryText is { } tryText && InsertOf(_layout, chip) is var how && how != HelpInsert.None)
        {
            string text = chip.Text;
            string shown = text.Replace("&", "&&"); // an & in the text is not a menu shortcut
            string label = how == HelpInsert.Replace ? $"&Search for {shown}" : $"&Put {shown} in the search box";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => tryText(text, how)));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Copy &all", null, (_, _) => FileOps.SetClipboardText(AllText())) { ShortcutKeyDisplayString = "Ctrl+C" });
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Show(this, client);
    }

    // ---- screen readers ----------------------------------------------------------------------------

    /// <summary>The help as items a screen reader walks through: each part's title and intro, each heading, entry and note.</summary>
    void BuildItems()
    {
        var items = new List<(string, AccessibleRole, HelpRect)>();
        foreach (var b in _layout.Boxes)
        {
            switch (b.Kind)
            {
                case HelpBoxKind.Title or HelpBoxKind.Intro or HelpBoxKind.Note:
                    items.Add((b.Text, AccessibleRole.StaticText, b.Bounds));
                    break;
                case HelpBoxKind.Heading:
                    items.Add((SearchHelp.PlainHeading(_layout.Sections[b.Section]), AccessibleRole.StaticText, b.Bounds));
                    break;
                case HelpBoxKind.Card:
                    foreach (var r in _layout.Rows.Where(r => r.Section == b.Section))
                        items.Add((_layout.Sections[r.Section].Entries[r.Entry].PlainText(), AccessibleRole.ListItem, r.Bounds));
                    break;
            }
        }
        _items = items;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new HelpAccessible(this);

    sealed class HelpAccessible(SearchHelpView view) : ControlAccessibleObject(view)
    {
        public override AccessibleRole Role => AccessibleRole.Document;
        public override string? Name => view.AccessibleName;
        public override string? Value { get => view.AllText(); set { } }
        public override string? Description => string.Join(", ", view._parts.Select(p => p.Title));
        public override AccessibleStates State => base.State | AccessibleStates.ReadOnly;
        public override int GetChildCount() => view._items.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < view._items.Count ? new ItemAccessible(view, this, index) : null;
    }

    sealed class ItemAccessible(SearchHelpView view, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => view._items[index].Name;
        public override AccessibleRole Role => view._items[index].Role;
        public override AccessibleObject? Parent => parent;
        public override AccessibleStates State
        {
            get
            {
                var b = view._items[index].Bounds;
                bool shown = b.Bottom > view.ScrollY && b.Y < view.ScrollY + view.ClientSize.Height;
                return AccessibleStates.ReadOnly | (shown ? 0 : AccessibleStates.Offscreen);
            }
        }
        public override Rectangle Bounds
        {
            get
            {
                var b = view._items[index].Bounds;
                return view.RectangleToScreen(new Rectangle(b.X, b.Y - view.ScrollY, b.Width, b.Height));
            }
        }
        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Down when index + 1 < view._items.Count => parent.GetChild(index + 1),
            AccessibleNavigation.Previous or AccessibleNavigation.Up when index > 0 => parent.GetChild(index - 1),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            foreach (var f in _fonts) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
