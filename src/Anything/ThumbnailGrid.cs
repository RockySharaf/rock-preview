using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;

namespace Anything;

/// <summary>
/// Owner-drawn virtual grid of thumbnails for the current results. It owns no selection: the main window's
/// virtual ListView stays the single source of truth, so every list command (open, copy, delete, shell menu…)
/// works unchanged in thumbnail mode. Thumbnails come from the thumbnail service (<see cref="ThumbHost"/>): the grid
/// tells it what the view shows whenever that changes (the scroll position, the size, the list, the thumbnail size),
/// with the cells around it in the order they are wanted, and draws what has arrived. Painting never asks for anything
/// and never cancels anything.
/// </summary>
internal sealed class ThumbnailGrid : Control
{
    const int Pad = 8;

    // Scrolled in pixels, as File Explorer scrolls: smoothly, not by rows. The bar counts in units of _unit pixels,
    // more than one only when all the rows are too tall for its int range (millions of results at a large size).
    readonly GridScrollBar _scroll;
    long _offset;
    int _unit = 1;
    bool _syncing;       // the bar is being set from _offset, not moved by the user
    double _wheelPixels; // a touchpad's small wheel steps, added up until they make a whole pixel
    int _zoomDelta;      // Ctrl+wheel steps, added up until they make a whole notch
    // Where the view is, kept when the layout changes (zoom, resize, font, shown again): the item at the top, how far
    // into its row, whether at the end, and the layout they were measured in.
    int _topItem, _layoutColumns, _layoutCellHeight;
    double _topFraction;
    bool _atEnd;
    int _ensurePending = -1; // an item to bring into view once the grid is shown and laid out
    int _count, _thumbSize = 160, _anchor = -1;
    string _emptyText = "";

    /// <summary>Gray text shown while there are no items ("No files in Downloads..."), as Explorer does; empty for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            if (value == _emptyText) return;
            _emptyText = value;
            if (_count == 0) Invalidate();
        }
    }

    EmptyTextLink? _emptyLink;
    bool _linkDown; // the left button went down on the link: its release there follows it

    /// <summary>A link on its own line under <see cref="EmptyText"/> ("Search everything instead"); null for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? EmptyLink
    {
        get => _emptyLink?.Text;
        set
        {
            _emptyLink ??= new EmptyTextLink(this, () => EmptyLinkClicked?.Invoke());
            if (!_emptyLink.SetText(value)) return;
            if (value is null) Cursor = Cursors.Default; // it may have gone from under the pointer
            if (_count == 0) Invalidate();
        }
    }

    /// <summary>The link under the gray text was clicked, or Enter pressed while it shows.</summary>
    public event Action? EmptyLinkClicked;

    bool EmptyLinkShowing => _emptyLink is { Text: not null } && _emptyText.Length > 0 && _count == 0;

    Rectangle EmptyTextArea()
    {
        int pad = LogicalToDeviceUnits(16);
        return new Rectangle(pad, LogicalToDeviceUnits(24), Math.Max(0, ClientSize.Width - 2 * pad), Math.Max(0, ClientSize.Height / 2));
    }

    /// <summary>The link's box, or empty while it doesn't show.</summary>
    Rectangle EmptyLinkBounds() => EmptyLinkShowing ? _emptyLink!.Layout(EmptyTextArea(), _emptyText, Font) : Rectangle.Empty;

    /// <summary>The pointer over the link or not: the hand and the underline follow.</summary>
    void TrackLinkHover(Point p)
    {
        var link = EmptyLinkBounds();
        bool hot = link.Contains(p);
        if (_emptyLink is null || !_emptyLink.SetHot(hot)) return;
        Cursor = hot ? Cursors.Hand : Cursors.Default;
        Invalidate(Rectangle.Inflate(link, 2, 2));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new GridAccessible(this);

    /// <summary>The grid's object, with the link as its child while the grid is empty and shows one.</summary>
    sealed class GridAccessible(ThumbnailGrid owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.EmptyLinkShowing ? 1 : base.GetChildCount();

        public override AccessibleObject? GetChild(int index) =>
            owner.EmptyLinkShowing ? index == 0 ? owner._emptyLink!.Accessible(this, owner.EmptyLinkBounds, owner._emptyText) : null : base.GetChild(index);
    }

    Point _downAt;
    int _downIndex = -1;
    Keys _downKeys;         // Ctrl and Shift as the button went down
    bool _clickOnRelease;   // pressed on a selected thumbnail: the click waits for the release (a drag takes them all)

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, string?> PathAt { get; set; } = _ => null;
    /// <summary>
    /// The item's length and last-write time as the index has them (no file is touched), so an edited file gets a new
    /// picture; <see cref="ThumbStamp.Unknown"/> when not known (the service then reads it from the file, on a worker).
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, ThumbStamp> StampAt { get; set; } = _ => ThumbStamp.Unknown;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, string> NameAt { get; set; } = _ => "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, bool> IsSelected { get; set; } = _ => false;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int> FocusedIndex { get; set; } = () => -1;
    /// <summary>How many results are selected (a slow click renames only the one selected item).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int> SelectedCount { get; set; } = () => 0;
    /// <summary>Whether a path is cut, waiting to be pasted: its thumbnail is drawn at half strength, as in Explorer.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, bool> IsCut { get; set; } = _ => false;

    /// <summary>Click on an item (index, modifier keys at the time of the click).</summary>
    public event Action<int, Keys>? ItemClicked;
    /// <summary>Keyboard navigation moved the focus (index, shift held, ctrl held).</summary>
    public event Action<int, bool, bool>? Navigate;
    public event Action<int>? ItemActivated;
    public event Action<Point>? ItemContextMenu;
    public event Action? ItemDrag;
    public event Action<int>? ZoomRequested;
    public event KeyEventHandler? ForwardKeyDown;
    public event KeyPressEventHandler? ForwardKeyPress;
    /// <summary>
    /// A click on the only selected thumbnail, in a grid that already had the focus, with no double-click after it: the
    /// item and where it was clicked (client). Whoever handles it checks the click was on the name.
    /// </summary>
    public event Action<int, Point>? RenameClick;
    /// <summary>The user scrolled the grid (wheel or scroll bar): a rename box over it ends.</summary>
    public event Action? Scrolling;
    /// <summary>
    /// The thumbnails on screen took more than a second to appear (once per stretch of waiting): the program counts these
    /// to know when keeping thumbnails on disk would help.
    /// </summary>
    public event Action? WaitedForThumbnails;

    public ThumbnailGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        _scroll = new GridScrollBar(this) { Dock = DockStyle.Right };
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            StopRenameClick(); // the thumbnail clicked moves from under the pointer
            Scrolling?.Invoke();
            SetOffset((long)_scroll.Value * _unit, fromBar: true);
        };
        _memory.Evicted += OnEvicted;
    }

    /// <summary>The grid's scroll bar: the wheel or a touchpad over it scrolls the grid as it does over the thumbnails.</summary>
    sealed class GridScrollBar(ThumbnailGrid grid) : VScrollBar
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            grid.ScrollByWheel(e.Delta);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ThumbSize
    {
        get => _thumbSize;
        set
        {
            value = Math.Clamp(value, 48, 512);
            if (value == _thumbSize) return;
            // The pictures of the old size stay, drawn scaled, until the new size's arrive.
            Relayout(() => _thumbSize = value);
            QueueViewport();
            Invalidate();
        }
    }

    /// <summary>Sets the number of results; <paramref name="reset"/> scrolls to the top (new query).</summary>
    public void SetCount(int count, bool reset)
    {
        _count = count;
        if (reset)
        {
            _anchor = -1; _offset = 0; _wheelPixels = 0; _ensurePending = -1; _hot = -1; StopRenameClick();
            NewList();
        }
        long before = _offset;
        UpdateScroll();
        // More or fewer results with the view where it was: the item it keeps at the top stays the same one, but
        // whether that's the end is measured again (more results: it no longer is).
        if (reset || _offset != before) Remember();
        else _atEnd = _offset > 0 && _offset >= MaxOffset;
        // The items may be others now (the index changed, another order): the cells are looked up again.
        QueueViewport();
        Invalidate();
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _count) return;
        // Hidden (switching to Thumbnails) or not laid out yet: measured with the real size once it is.
        if (!Visible || ClientSize.Height <= 0) { _ensurePending = index; return; }
        _ensurePending = -1;
        int h = Cell.Height;
        long top = (long)(index / Columns) * h;
        // A cell taller than the view is shown from its top, always (never its top one time and its bottom the next).
        if (h >= ClientSize.Height) { if (_offset != top) ScrollTo(top); return; }
        if (top < _offset) ScrollTo(top);
        else if (top + h > _offset + ClientSize.Height) ScrollTo(top + h - ClientSize.Height);
    }

    // ---- geometry -------------------------------------------------------------------------------

    int TextHeight => Font.Height * 2 + 4;
    Size Cell => new(_thumbSize + Pad * 2, _thumbSize + TextHeight + Pad * 2);
    // The bar is always docked at the right (also while the grid is hidden, when its Visible reads false).
    int GridWidth => Math.Max(1, ClientSize.Width - _scroll.Width);
    int Columns => Math.Max(1, GridWidth / Cell.Width);
    int Rows => (_count + Columns - 1) / Columns;
    int VisibleRows => Math.Max(1, ClientSize.Height / Cell.Height);
    long ContentHeight => (long)Rows * Cell.Height;
    long MaxOffset => Math.Max(0, ContentHeight - ClientSize.Height);

    void ScrollTo(long offset) => SetOffset(offset, fromBar: false);

    /// <summary>Scrolls to <paramref name="offset"/> pixels (clamped), moving the bar unless the bar moved it.</summary>
    void SetOffset(long offset, bool fromBar)
    {
        offset = Math.Clamp(offset, 0, MaxOffset);
        bool moved = offset != _offset;
        if (moved) _direction = offset > _offset ? ScrollDirection.Down : ScrollDirection.Up;
        _offset = offset;
        if (!fromBar) SyncBar();
        Remember();
        if (!moved) return;
        _lastActivity = Environment.TickCount64;
        QueueViewport();
        Invalidate();
        if (_hot >= 0) RefreshHot(); // the thumbnails moved under a pointer that didn't
    }

    void SyncBar()
    {
        _syncing = true;
        try { _scroll.Value = (int)Math.Clamp(_offset / _unit, 0L, Math.Max(0L, _scroll.Maximum - _scroll.LargeChange + 1L)); }
        finally { _syncing = false; }
    }

    void Remember()
    {
        int h = Cell.Height, cols = Columns;
        long row = _offset / h;
        _topItem = (int)Math.Min(int.MaxValue, row * cols);
        _topFraction = (double)(_offset - row * h) / h;
        _atEnd = _offset > 0 && _offset >= MaxOffset;
        _layoutColumns = cols;
        _layoutCellHeight = h;
    }

    /// <summary>
    /// Keeps the view where it was while the layout changes (zoom, resize, font, shown again): the same item at the top
    /// and as far into its row, or still at the end. Measured when the grid last scrolled, since by the time a resize is
    /// seen the old size is gone; when only the height changed, the offset just stays.
    /// </summary>
    void Relayout(Action? change = null)
    {
        int item = _topItem;
        double fraction = _topFraction;
        bool atEnd = _atEnd;
        change?.Invoke();
        UpdateScroll();
        int cols = Columns, h = Cell.Height;
        bool sameLayout = cols == _layoutColumns && h == _layoutCellHeight;
        ScrollTo(atEnd ? MaxOffset : sameLayout ? _offset : (long)(item / cols) * h + (long)(fraction * h));
        if (!atEnd) _topItem = item; // the same item next time too, not its row's first one
    }

    void UpdateScroll()
    {
        long content = ContentHeight;
        int page = Math.Max(1, ClientSize.Height);
        _unit = (int)Math.Max(1, (content + int.MaxValue / 2 - 1) / (int.MaxValue / 2));
        _syncing = true;
        try
        {
            // A scroll bar's furthest Value is Maximum - LargeChange + 1: with these, the last row's bottom at the bottom.
            _scroll.LargeChange = Math.Max(1, page / _unit);
            _scroll.SmallChange = Math.Max(1, Cell.Height / 3 / _unit);
            _scroll.Maximum = (int)Math.Max(0, (content - 1) / _unit);
            _scroll.Enabled = content > page;
        }
        finally { _syncing = false; }
        if (_offset > MaxOffset) _offset = MaxOffset;
        SyncBar();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
        if (_ensurePending >= 0) EnsureVisible(_ensurePending);
        QueueViewport(); // more or fewer cells on screen
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Relayout();
        QueueViewport();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // An item to bring into view: only once the parent has laid the grid out at its new size (it isn't yet while
        // this runs; a size that changed meanwhile comes as a resize, which handles it first).
        if (Visible && _ensurePending >= 0 && IsHandleCreated)
            BeginInvoke(() => { if (Visible && _ensurePending >= 0) EnsureVisible(_ensurePending); });
        if (Visible) QueueViewport();
        else ViewHidden();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Volatile.Write(ref _hwnd, Handle);
        QueueViewport();
        // A slice of arrivals posted to a window that was made again is lost with it: posted again here.
        Volatile.Write(ref _drainPosted, 0);
        if (!_arrivals.IsEmpty && Interlocked.Exchange(ref _drainPosted, 1) == 0) PostTakeArrivals();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Volatile.Write(ref _hwnd, 0);
        base.OnHandleDestroyed(e);
    }

    Rectangle CellRect(int index)
    {
        int cols = Columns, row = index / cols, col = index % cols;
        var c = Cell;
        int spare = (GridWidth - cols * c.Width) / 2; // center the grid horizontally
        long y = (long)row * c.Height - _offset;
        return new Rectangle(spare + col * c.Width, (int)Math.Clamp(y, int.MinValue / 4, int.MaxValue / 4), c.Width, c.Height);
    }

    /// <summary>The item at <paramref name="p"/> (client coordinates), or -1.</summary>
    public int IndexAt(Point p) => HitTest(p);

    /// <summary>Where item <paramref name="index"/>'s name is drawn under its thumbnail (client coordinates).</summary>
    public Rectangle CaptionRect(int index)
    {
        var cell = CellRect(index);
        return new Rectangle(cell.X + 4, cell.Y + Pad + _thumbSize + 4, cell.Width - 8, TextHeight);
    }

    int HitTest(Point p)
    {
        var c = Cell;
        int cols = Columns, spare = (GridWidth - cols * c.Width) / 2;
        if (p.X < spare || p.X >= spare + cols * c.Width || p.Y < 0) return -1;
        long index = (p.Y + _offset) / c.Height * cols + (p.X - spare) / c.Width;
        return index < _count ? (int)index : -1;
    }

    // ---- painting ------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        _painting = true;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { PaintCells(e); }
        finally
        {
            _painting = false;
            Counters.WorstPaintUs = Math.Max(Counters.WorstPaintUs, ThumbCounters.Since(start));
        }
    }

    void PaintCells(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_count == 0)
        {
            if (_emptyText.Length == 0) return;
            var r = EmptyTextArea();
            TextRenderer.DrawText(g, _emptyText, Font, r, ExplorerTheme.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if (EmptyLinkShowing) _emptyLink!.Paint(g, _emptyLink.Layout(r, _emptyText, Font), Font);
            return;
        }

        // The rows the view shows, partly or whole.
        int cols = Columns, cellHeight = Cell.Height;
        int first = (int)Math.Min(_count, _offset / cellHeight * cols);
        int last = (int)Math.Min(_count - 1L, ((_offset + ClientSize.Height) / cellHeight + 1) * cols - 1);
        int focused = FocusedIndex();
        var selBack = Color.FromArgb(90, SystemColors.Highlight);
        using var selBrush = new SolidBrush(selBack);
        using var hoverBrush = new SolidBrush(ExplorerTheme.Hover);
        using var frame = new Pen(Color.FromArgb(70, ForeColor));
        using var focusPen = new Pen(SystemColors.Highlight, 1.5f);
        var captions = new List<(string Name, Rectangle Rect)>(last - first + 1);

        for (int i = first; i <= last; i++)
        {
            var cell = CellRect(i);
            if (!cell.IntersectsWith(e.ClipRectangle)) continue;
            var inner = Rectangle.Inflate(cell, -3, -3);
            if (IsSelected(i)) g.FillRectangle(selBrush, inner);
            else if (i == _hot) g.FillRectangle(hoverBrush, inner); // under the pointer, as in Explorer
            if (i == focused && Focused) g.DrawRectangle(focusPen, inner);

            var box = new Rectangle(cell.X + Pad, cell.Y + Pad, _thumbSize, _thumbSize);
            string? path = PathAt(i);
            if (path is not null)
            {
                // Only what has arrived is drawn: nothing here asks for a picture or touches a file.
                if (!DrawThumb(g, i, box, path, IsCut(path)))
                    g.DrawRectangle(frame, Rectangle.Inflate(box, -box.Width / 6, -box.Height / 6));
            }
            captions.Add((NameAt(i), new Rectangle(cell.X + 4, box.Bottom + 4, cell.Width - 8, TextHeight)));
        }
        // The names last, all through one device context: each TextRenderer call on the Graphics would take its device
        // context and give it back, and that waits whenever a worker thread is in GDI+ (measured: a caption 0.1 ms alone,
        // several times that while the service decodes, which made a screen's paint 30 to 80 ms).
        if (captions.Count == 0) return;
        IntPtr hdc = g.GetHdc();
        try
        {
            using var dc = new SharedDc(hdc);
            var font = Font;
            StartCaptions(dc, font, captions[0].Rect.Width);
            foreach (var (name, rect) in captions)
            {
                // Windows' own wrapping, unless it would cut the extension: then the two lines laid out here. Worked out
                // once per name for this font and width: a repaint (the pointer, an arrival) measures nothing.
                if (!_captionLines.TryGetValue(name, out var lines))
                {
                    lines = CaptionLines(name, rect.Width, CaptionMeasurer);
                    if (_captionLines.Count >= MaxCaptionLayouts) _captionLines.Clear();
                    _captionLines[name] = lines;
                }
                if (lines != null) TextRenderer.DrawText(dc, lines, font, rect, ForeColor, CaptionLinesFlags);
                else TextRenderer.DrawText(dc, name, font, rect, ForeColor, CaptionFlags);
            }
        }
        finally
        {
            _captionDc = null;
            g.ReleaseHdc(hdc);
        }
    }

    // ---- caption layout -------------------------------------------------------------------------
    //
    // A layout needs ten or so widths (TextRenderer takes about 10 microseconds for each, which doubled a caption's cost
    // at the small size). So they are measured with GDI's own extent call (a microsecond or two) in a font made as
    // TextRenderer makes it (selected for each call: TextRenderer doesn't leave it selected), plus the margins TextRenderer
    // adds. Two samples check that this gives TextRenderer's widths exactly; if not, or for text outside Latin (where
    // Windows may take glyphs from another font), TextRenderer measures. Each name is laid out once for a font and width.

    readonly Dictionary<string, string?> _captionLines = new(StringComparer.Ordinal);
    const int MaxCaptionLayouts = 8192;
    Font? _captionFont;
    int _captionWidth = -1, _captionMargin;
    bool _captionGdi; // GDI's widths agreed with TextRenderer's for this font
    IntPtr _captionHfont, _captionHdc;
    IDeviceContext? _captionDc;
    CaptionMeasure? _captionMeasurer; // made once: no delegate per paint
    CaptionMeasure CaptionMeasurer => _captionMeasurer ??= MeasureCaption;

    /// <summary>Gets the captions' turn ready: the layouts kept while font and width stay; margin and agreement measured when either changed.</summary>
    void StartCaptions(IDeviceContext dc, Font font, int width)
    {
        _captionDc = dc;
        _captionHdc = dc.GetHdc();
        if (!ReferenceEquals(font, _captionFont))
        {
            FreeCaptionFont();
            _captionFont = font;
            _captionHfont = CaptionHfont(font);
            _captionWidth = -1; // the margin is measured again
        }
        if (width == _captionWidth) return;
        _captionLines.Clear();
        _captionWidth = width;
        _captionGdi = false;
        if (_captionHfont != IntPtr.Zero)
        {
            const string Short = "Report 2024.xlsx", Long = "Quarterly summary (final) - copy 2.pptx";
            int margin = TextRenderer.MeasureText(dc, Short, font, NoLimit, CaptionMeasureFlags).Width - GdiWidth(Short);
            _captionGdi = TextRenderer.MeasureText(dc, Long, font, NoLimit, CaptionMeasureFlags).Width - GdiWidth(Long) == margin;
            _captionMargin = margin;
        }
    }

    int MeasureCaption(ReadOnlySpan<char> text)
    {
        bool latin = _captionGdi && !text.IsEmpty;
        foreach (char c in text)
            if (c >= '\u0250' && c != '\u2026') { latin = false; break; }
        return latin ? GdiWidth(text) + _captionMargin : TextRenderer.MeasureText(_captionDc!, text, _captionFont, NoLimit, CaptionMeasureFlags).Width;
    }

    int GdiWidth(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0;
        IntPtr old = SelectObject(_captionHdc, _captionHfont);
        try { return GetTextExtentPoint32W(_captionHdc, ref MemoryMarshal.GetReference(MemoryMarshal.Cast<char, ushort>(text)), text.Length, out var size) ? size.Width : -1; }
        finally { SelectObject(_captionHdc, old); }
    }

    // The font TextRenderer selects for a hand-made device context: the family at the size in points at the system's DPI
    // (rounded up), normal quality, TrueType preferred. Zero when it can't be made (TextRenderer then measures everything).
    static IntPtr CaptionHfont(Font font)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        int dpi;
        try { dpi = GetDeviceCaps(screen, LogPixelsY); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        string face = font.FontFamily.Name;
        if (face.Length > 1 && face[0] == '@') face = face[1..];
        var lf = new LogFont
        {
            Height = -(int)Math.Ceiling(dpi * font.SizeInPoints / 72),
            Weight = font.Bold ? 700 : 400,
            Italic = (byte)(font.Italic ? 1 : 0),
            Underline = (byte)(font.Underline ? 1 : 0),
            StrikeOut = (byte)(font.Strikeout ? 1 : 0),
            CharacterSet = font.GdiCharSet,
            OutPrecision = 4, // OUT_TT_PRECIS
            FaceName = face,
        };
        return CreateFontIndirectW(ref lf);
    }

    void FreeCaptionFont()
    {
        if (_captionHfont != IntPtr.Zero) DeleteObject(_captionHfont);
        _captionHfont = IntPtr.Zero;
        _captionFont = null;
        _captionLines.Clear();
    }

    const int LogPixelsY = 90;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    // The text as UTF-16 units (ushort: pinned and passed as it is; a char would be copied as one ANSI byte).
    [DllImport("gdi32.dll")] static extern bool GetTextExtentPoint32W(IntPtr hdc, ref ushort text, int count, out Size size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontIndirectW(ref LogFont font);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // How a caption is drawn: Windows wraps it at spaces (else anywhere) and ends the second line with an ellipsis.
    const TextFormatFlags CaptionFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    // Two lines laid out by CaptionLines: drawn as given, each centred.
    const TextFormatFlags CaptionLinesFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    // One line measured as a caption line is drawn (same margins), so "fits" means what the drawing does.
    const TextFormatFlags CaptionMeasureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    static readonly Size NoLimit = new(int.MaxValue, int.MaxValue);
    /// <summary>The longest extension (after the last dot) kept whole on a caption's last line, as File Explorer keeps it.</summary>
    internal const int MaxCaptionExtension = 8;

    /// <summary>The width in pixels a caption line takes when drawn.</summary>
    internal delegate int CaptionMeasure(ReadOnlySpan<char> text);

    /// <summary>
    /// The two lines of a caption <paramref name="width"/> pixels wide (joined by a line break) when Windows' wrapping would
    /// cut the name's extension, else null (drawn as before: the name fits on one line, has no extension of at most
    /// <see cref="MaxCaptionExtension"/> characters, or the extension alone is wider than a line). The first line breaks
    /// where Windows breaks it (after the last space that fits, else after the last character that fits), but never after
    /// the last dot; the second line is the rest, and when it is too wide, as much of it as fits, an ellipsis and the
    /// extension ("report-2024-fi", an ellipsis, ".xlsx").
    /// </summary>
    internal static string? CaptionLines(string name, int width, CaptionMeasure measure)
    {
        int dot = name.LastIndexOf('.');
        int extLength = name.Length - dot - 1;
        // Cheap checks first: most names fit, and a name without a short extension is drawn as before unmeasured.
        if (dot <= 0 || extLength < 1 || extLength > MaxCaptionExtension || width <= 0) return null;
        var all = name.AsSpan();
        if (measure(all) <= width) return null;
        var ext = all[dot..];
        if (measure(ext) > width) return null;

        // The first line as Windows wraps it: the most characters that fit, then back to the last space among them.
        int fit = FitCount(all, width, measure);
        if (fit == 0 || fit >= all.Length) return null; // widths that don't add up: drawn as before
        int lineEnd = fit, next = fit;
        if (all[fit] != ' ')
        {
            int space = all[..fit].LastIndexOf(' ');
            if (space > 0) lineEnd = next = space + 1;
        }
        // Never inside the extension: the dot and what follows go to the second line together.
        if (lineEnd > dot) lineEnd = next = dot;
        while (next < dot && all[next] == ' ') next++; // the spaces at a break are not drawn
        var first = all[..lineEnd].TrimEnd(' ');
        var rest = all[next..];
        if (first.IsEmpty) return null;
        if (measure(rest) <= width) return string.Concat(first, "\n", rest);

        // Too long for two lines: as much of the rest as fits before an ellipsis and the extension.
        var stem = rest[..^ext.Length];
        Span<char> buffer = stackalloc char[Math.Min(stem.Length, 256) + 1 + ext.Length];
        int lo = 0, hi = Math.Min(stem.Length, 256);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(Shortened(buffer, stem[..mid], ext)) <= width) lo = mid;
            else hi = mid - 1;
        }
        lo = WholeCharacters(stem, lo);
        var second = Shortened(buffer, stem[..lo].TrimEnd(' '), ext);
        if (measure(second) > width) return null;
        return string.Concat(first, "\n", second);
    }

    // The stem's start, an ellipsis and the extension, written into buffer.
    static ReadOnlySpan<char> Shortened(Span<char> buffer, ReadOnlySpan<char> stem, ReadOnlySpan<char> ext)
    {
        stem.CopyTo(buffer);
        buffer[stem.Length] = '\u2026';
        ext.CopyTo(buffer[(stem.Length + 1)..]);
        return buffer[..(stem.Length + 1 + ext.Length)];
    }

    // How many characters from the start fit in width (a binary search: widths grow with the characters).
    static int FitCount(ReadOnlySpan<char> text, int width, CaptionMeasure measure)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(text[..mid]) <= width) lo = mid;
            else hi = mid - 1;
        }
        return WholeCharacters(text, lo);
    }

    // A cut at count moved back so it splits no character: not between the halves of a surrogate pair (an emoji), and not
    // between a letter and the accents or variation marks that follow it.
    static int WholeCharacters(ReadOnlySpan<char> text, int count)
    {
        while (count > 0 && count < text.Length && (char.IsLowSurrogate(text[count]) || IsCombiningMark(text[count])))
            count--;
        return count;
    }

    static bool IsCombiningMark(char c) => c >= '\u0300' && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
        is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark
        or System.Globalization.UnicodeCategory.EnclosingMark;

    /// <summary>A device context taken once from the paint's Graphics, lent to each caption (none of them gives it back).</summary>
    sealed class SharedDc(IntPtr hdc) : IDeviceContext
    {
        public IntPtr GetHdc() => hdc;
        public void ReleaseHdc() { }
        public void Dispose() { }
    }

    /// <summary>Draws what item <paramref name="index"/> has (ThumbPaint decides which picture and at what scale); false for nothing.</summary>
    bool DrawThumb(Graphics g, int index, Rectangle box, string path, bool faded)
    {
        if (!_cellKeys.TryGetValue(index, out var key)) return false;
        ThumbShown? exact = _memory.TryGet((key, _thumbSize), out var e) ? e : null;
        ThumbShown? other = null;
        if (exact is not { Quality: ThumbQuality.Final or ThumbQuality.None } && _latest.TryGetValue(key.Path, out var last)
            && (last.Key, last.Size) != (key, _thumbSize) && _memory.TryGet(last, out var o))
            other = o;
        var drawing = ThumbPaint.Choose(exact?.Quality, other?.Quality);
        var shown = drawing switch { ThumbDrawing.Exact or ThumbDrawing.StandIn => exact, ThumbDrawing.Other => other, _ => null };
        if (shown == null) return false;
        // A file with no picture and no icon either: the frame, as before.
        if (shown.Bitmap is not { } bmp) return false;
        float scale = ThumbPaint.Scale(drawing, bmp.Width, bmp.Height, _thumbSize, shown.Size);
        int w = Math.Max(1, (int)(bmp.Width * scale)), h = Math.Max(1, (int)(bmp.Height * scale));
        var dest = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        bool exactSize = w == bmp.Width && h == bmp.Height;
        // The service hands over premultiplied pictures at the display size: drawn pixel for pixel, they need no
        // filtering. A stand-in or a picture of another size is scaled, smoothly enough to pass for a moment.
        g.InterpolationMode = exactSize ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.PixelOffsetMode = exactSize ? System.Drawing.Drawing2D.PixelOffsetMode.Half : System.Drawing.Drawing2D.PixelOffsetMode.Default;
        try
        {
            if (!faded) g.DrawImage(bmp, dest);
            else
            {
                using var half = new System.Drawing.Imaging.ImageAttributes();
                half.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.5f }); // a cut item, until it is pasted
                g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, half);
            }
        }
        finally { g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default; }
        // A page-like picture (a white slide, a PDF page, a screenshot) gets a hairline on its own edge pixels, so it
        // doesn't melt into the white view. Only real pictures: an icon (None) has its own shape. A text or sheet page
        // draws a border on the same pixels, so this replaces it rather than adding a second one.
        if (shown.Quality != ThumbQuality.None && w > 2 && h > 2 && ThumbKinds.IsPageLike(path))
            g.DrawRectangle(OutlinePen(faded), dest.X, dest.Y, w - 1, h - 1);
        return true;
    }

    Pen? _outlinePen, _fadedOutlinePen;

    /// <summary>
    /// The page outline's pen, in the theme's divider colour, at half strength for a cut item as its picture is (each made
    /// again only when the theme changes).
    /// </summary>
    Pen OutlinePen(bool faded)
    {
        var color = ExplorerTheme.NavDivider;
        if (faded) color = Color.FromArgb(color.A / 2, color);
        ref Pen? pen = ref faded ? ref _fadedOutlinePen : ref _outlinePen;
        if (pen is null || pen.Color != color)
        {
            pen?.Dispose();
            pen = new Pen(color);
        }
        return pen;
    }

    // ---- thumbnails: what the view asks for, what arrives, the memory of both -------------------------
    //
    // The order (all on the UI thread): whenever the scroll position, the size, the list or the thumbnail size changes,
    // QueueViewport posts one SubmitViewport for this turn of the message loop. It resolves each cell of the plan
    // (ThumbOrder.Viewport: visible in reading order, two screens ahead in the scroll direction, one behind) to its path,
    // stamp and key, leaves out what memory already holds for good (Final or None), and hands the rest to the service. A
    // new list (a new search, another folder) drops everything not started. The service raises arrivals on its workers;
    // they wait in _arrivals and are taken a few milliseconds' worth per message (the rest in a message of its own), put
    // in memory, and only the cells on screen that show them are invalidated. When nothing waits and nothing was touched for 1.5 s, the idle step asks
    // for the rest of the list (200 at a time, 3,000 at most) to go into the store; without a store it doesn't run.

    /// <summary>No scrolling, key or click for this long (and nothing waiting): the idle step may run.</summary>
    const long IdleAfterMs = 1500;
    /// <summary>
    /// Arrivals are taken for this long at most in one message; the rest come in the next one, so many pictures arriving
    /// at once never hold the UI thread (a scroll, a click, a paint) for long.
    /// </summary>
    const double ArrivalSliceMs = 4;
    /// <summary>The message that takes the next slice of arrivals (posted, so it is a message of its own).</summary>
    const int WmTakeArrivals = 0x8000 + 0x2A1; // WM_APP + 0x2A1

    // Pictures kept at display size, least recently used first out: one memory for every window (ThumbHost.Memory).
    readonly ThumbMemory<(ThumbKey Key, int Size), ThumbShown> _memory = ThumbHost.Memory;
    readonly HashSet<ThumbKey> _visibleKeys = [];
    readonly Dictionary<ThumbKey, int> _idlePending = []; // idle work handed out and maybe not made yet: key to list index
    readonly Dictionary<string, (ThumbKey Key, int Size)> _latest = new(StringComparer.Ordinal); // a file's last arrival, any size
    readonly Dictionary<int, ThumbKey> _cellKeys = [];   // the plan's items: list index to key
    readonly HashSet<ThumbKey> _planKeys = [];
    readonly HashSet<ThumbKey> _idleKeys = [];
    readonly ConcurrentQueue<ThumbArrival> _arrivals = new();
    readonly IdlePlanner _idle = new();
    readonly ThumbFillWatch _fill = new();
    System.Windows.Forms.Timer? _idleTimer;
    ThumbClient? _client;
    int _listNumber = 1;
    int _idleStart;
    bool _idleInFlight;
    ScrollDirection _direction;
    long _lastActivity = Environment.TickCount64;
    bool _viewportQueued, _painting;
    int _drainPosted;
    // The window's handle, for the workers to post arrivals to (Handle itself may only be read on the UI thread); 0
    // while there is no window.
    nint _hwnd;
    volatile bool _closed;

    /// <summary>Counts of what the grid did with thumbnails (the off-screen checks read them).</summary>
    internal sealed class ThumbCounters
    {
        public int Viewports, Requests, IdleRequests, ArrivalBatches, Arrivals, CellsInvalidated, StaleDropped;
        /// <summary>Asks or cancels made while painting: must stay 0.</summary>
        public int AskedWhilePainting;
        /// <summary>The longest a viewport, a batch of arrivals and a paint took on the UI thread, in microseconds.</summary>
        public int WorstViewportUs, WorstArrivalsUs, WorstPaintUs;

        public static int Since(long start) => (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(start).Ticks / 10);
    }

    internal ThumbCounters Counters { get; } = new();

    ThumbClient Client => _client ??= ThumbHost.CreateClient(OnArrival);

    /// <summary>The list changed (a new search, another folder, a refresh): work not started for the old one is dropped.</summary>
    void NewList()
    {
        _listNumber++;
        _cellKeys.Clear();
        _planKeys.Clear();
        _visibleKeys.Clear();
        _idleKeys.Clear();
        _idlePending.Clear();
        _idle.Reset();
        _idleInFlight = false;
        _direction = ScrollDirection.None;
        _fill.Reset();
    }

    /// <summary>The view changed: the plan is made again once, at the end of this turn of the message loop.</summary>
    void QueueViewport()
    {
        if (_viewportQueued || _closed || !IsHandleCreated) return;
        _viewportQueued = true;
        try { BeginInvoke(SubmitViewport); }
        catch (InvalidOperationException) { _viewportQueued = false; }
    }

    void SubmitViewport()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { SubmitViewportNow(); }
        finally { Counters.WorstViewportUs = Math.Max(Counters.WorstViewportUs, ThumbCounters.Since(start)); }
    }

    void SubmitViewportNow()
    {
        _viewportQueued = false;
        if (_closed || !Visible || ClientSize.Height <= 0) return;
        if (_painting) Counters.AskedWhilePainting++;
        if (_count == 0)
        {
            // An empty list: whatever the last one still had waiting goes. The view is still shown, so the pictures
            // memory holds aren't let go after a while (a plan of one cell keeps it counted as shown): going back to
            // the folder before draws them at once.
            _cellKeys.Clear();
            _planKeys.Clear();
            _visibleKeys.Clear();
            ThumbHost.NotePlan(this, (long)_thumbSize * _thumbSize * 4);
            _client?.SubmitViewport(_listNumber, []);
            return;
        }
        int cols = Columns, h = Cell.Height;
        int firstRow = (int)Math.Min(int.MaxValue, _offset / h);
        int lastRow = (int)Math.Min(int.MaxValue, (_offset + ClientSize.Height - 1) / h);
        var plan = ThumbOrder.Viewport(_count, cols, firstRow, lastRow - firstRow + 1, _direction);
        _cellKeys.Clear();
        _planKeys.Clear();
        _visibleKeys.Clear();
        var requests = new List<ThumbRequest>(plan.Count);
        foreach (var item in plan)
        {
            string? path = PathAt(item.Index);
            if (path is null) continue;
            var stamp = StampAt(item.Index);
            var key = ThumbKey.Of(path, stamp, _thumbSize);
            _cellKeys[item.Index] = key;
            _planKeys.Add(key);
            if (item.Priority == ThumbPriority.Visible) _visibleKeys.Add(key);
            // Held for good already: not asked again (a stand-in still waits for its real picture).
            if (_memory.Peek((key, _thumbSize), out var held) && held.Quality != ThumbQuality.Low) continue;
            requests.Add(new ThumbRequest(path, stamp, _thumbSize, item.Priority, item.Index));
        }
        // Memory holds the plan with room to spare, and what is on screen is the last to go.
        ThumbHost.NotePlan(this, (long)plan.Count * _thumbSize * _thumbSize * 4);
        foreach (var key in _visibleKeys) _memory.TryGet((key, _thumbSize), out _);
        _idleStart = ThumbOrder.IdleStart(plan, _count);
        // Idle work not started is dropped by a new plan: what was handed out and not made may be handed out again; what
        // was made (or runs, or the new plan asks for) is not, so a list that keeps changing doesn't get the same chunk
        // again and again. Not made: still waiting in the queue now, or dropped by the service between its stages as the
        // new plan goes in (it says which, after).
        List<int>? giveBack = null;
        Dictionary<ThumbKey, int>? handedOut = null;
        if (_idleInFlight && _client is { } idleClient)
        {
            giveBack = _idlePending.Where(kv => !_planKeys.Contains(kv.Key) && idleClient.Queue.IsWaiting(kv.Key)).Select(kv => kv.Value).ToList();
            handedOut = new Dictionary<ThumbKey, int>(_idlePending);
            _idlePending.Clear();
            _idleInFlight = false;
        }
        Counters.Viewports++;
        Counters.Requests += requests.Count;
        Client.SubmitViewport(_listNumber, requests);
        var dropped = Client.TakeDroppedIdle();
        if (handedOut != null)
        {
            foreach (var key in dropped)
                if (handedOut.TryGetValue(key, out int i) && !_planKeys.Contains(key)) giveBack!.Add(i);
            _idle.Return(giveBack!);
        }
        StartIdleTimer();
        ObserveFill();
    }

    /// <summary>The grid is hidden (the details list, a folder view): its work not started is dropped.</summary>
    void ViewHidden()
    {
        _idleTimer?.Stop();
        _fill.Reset();
        ThumbHost.NotePlan(this, 0);
        if (_idleInFlight && _client is { } idleClient)
        {
            var giveBack = _idlePending.Where(kv => idleClient.Queue.IsWaiting(kv.Key)).Select(kv => kv.Value).ToList();
            idleClient.Clear();
            // And the idle work the service dropped between its stages as it cleared.
            foreach (var key in idleClient.TakeDroppedIdle())
                if (_idlePending.TryGetValue(key, out int i)) giveBack.Add(i);
            _idle.Return(giveBack);
        }
        else _client?.Clear();
        _idlePending.Clear();
        _idleInFlight = false;
    }

    /// <summary>
    /// Memory let a picture go to make room (another window, or other sizes, took it): a cell on screen that showed it
    /// asks for it again, rather than show the empty frame until the view next moves.
    /// </summary>
    void OnEvicted((ThumbKey Key, int Size) slot)
    {
        if (_closed || !Visible || slot.Size != _thumbSize || !_visibleKeys.Contains(slot.Key)) return;
        QueueViewport();
    }

    // Raised on the service's workers, several at once: queued, and taken on the UI thread a few milliseconds' worth per message.
    void OnArrival(ThumbArrival a)
    {
        _arrivals.Enqueue(a);
        if (_closed) { DisposeArrivals(); return; }
        if (Interlocked.Exchange(ref _drainPosted, 1) != 0) return;
        // Posted as a message of its own, never BeginInvoke'd: WinForms runs every callback queued while it runs its
        // callbacks in one message, so a steady stream of arrivals chained slices into one long message.
        nint hwnd = Volatile.Read(ref _hwnd);
        if (hwnd != 0 && PostMessage(hwnd, WmTakeArrivals, 0, 0)) return;
        // No window now: the flag goes down, and a window made later posts for what waits (OnHandleCreated).
        Volatile.Write(ref _drainPosted, 0);
        if (_closed) DisposeArrivals();
    }

    void DisposeArrivals()
    {
        while (_arrivals.TryDequeue(out var a)) a.Bitmap?.Dispose();
    }

    /// <summary>
    /// The next slice of arrivals, as a message of its own: posted, not BeginInvoke'd, because every callback queued
    /// while WinForms runs its callbacks would run in the same message.
    /// </summary>
    void PostTakeArrivals()
    {
        if (!_closed && IsHandleCreated && PostMessage(Handle, WmTakeArrivals, 0, 0)) return;
        Volatile.Write(ref _drainPosted, 0);
        if (_closed) DisposeArrivals();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    void TakeArrivals()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { TakeArrivalsNow(); }
        finally { Counters.WorstArrivalsUs = Math.Max(Counters.WorstArrivalsUs, ThumbCounters.Since(start)); }
    }

    void TakeArrivalsNow()
    {
        if (_closed)
        {
            Volatile.Write(ref _drainPosted, 0);
            DisposeArrivals();
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var arrived = new HashSet<ThumbKey>();
        int taken = 0;
        bool more = false;
        while (_arrivals.TryDequeue(out var a))
        {
            taken++;
            // A few milliseconds' worth at a time: the rest in a message of their own (workers don't post meanwhile:
            // _drainPosted stays set until the queue is empty).
            if (taken % 8 == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= ArrivalSliceMs) more = !_arrivals.IsEmpty;
            // Warming for the store only: the picture is kept there, not in this window's memory.
            if (_idleKeys.Contains(a.Key) && !_planKeys.Contains(a.Key))
            {
                a.Bitmap?.Dispose();
                Counters.StaleDropped++;
                continue;
            }
            var slot = (a.Key, a.DisplaySize);
            if (a.Quality == ThumbQuality.Low && _memory.Peek(slot, out var held) && held.Quality != ThumbQuality.Low)
            {
                a.Bitmap?.Dispose(); // the real one came first
                continue;
            }
            var shown = new ThumbShown(a.Bitmap, a.Quality, a.DisplaySize);
            _memory.Set(slot, shown, shown.Bytes);
            // The file's latest picture stands in for it at another size, or after it changed, until its own comes.
            if (a.Quality != ThumbQuality.Low || !_latest.TryGetValue(a.Key.Path, out var last) || !_memory.Contains(last))
                _latest[a.Key.Path] = slot;
            arrived.Add(a.Key);
            if (more) break;
        }
        if (more) PostTakeArrivals();
        else
        {
            Volatile.Write(ref _drainPosted, 0);
            // One that arrived as the queue emptied found the flag still set and posted nothing: taken next time.
            if (!_arrivals.IsEmpty && Interlocked.Exchange(ref _drainPosted, 1) == 0) PostTakeArrivals();
        }
        if (taken == 0) return;
        // A file's latest picture is only worth remembering while memory still holds it.
        if (_latest.Count > 4 * _memory.Count + 2000)
            foreach (var (path, slot) in _latest.ToList())
                if (!_memory.Contains(slot)) _latest.Remove(path);
        Counters.ArrivalBatches++;
        Counters.Arrivals += taken;
        if (arrived.Count > 0 && _count > 0 && Visible)
        {
            var (first, last) = VisibleRange();
            for (int i = first; i <= last; i++)
                if (_cellKeys.TryGetValue(i, out var key) && arrived.Contains(key))
                {
                    Invalidate(CellRect(i));
                    Counters.CellsInvalidated++;
                }
        }
        ObserveFill();
    }

    /// <summary>The items whose cells the view shows, partly or whole (first &gt; last when none).</summary>
    (int First, int Last) VisibleRange()
    {
        int cols = Columns, cellHeight = Cell.Height;
        int first = (int)Math.Min(_count, _offset / cellHeight * cols);
        int last = (int)Math.Min(_count - 1L, ((_offset + ClientSize.Height - 1) / cellHeight + 1) * cols - 1);
        return (first, last);
    }

    /// <summary>How many cells on screen have nothing to show yet.</summary>
    int Unanswered()
    {
        if (!Visible || _count == 0 || ClientSize.Height <= 0) return 0;
        var (first, last) = VisibleRange();
        int missing = 0;
        for (int i = first; i <= last; i++)
        {
            if (!_cellKeys.TryGetValue(i, out var key)) continue; // no file (gone): nothing will come
            if (_memory.Contains((key, _thumbSize))) continue;
            if (_latest.TryGetValue(key.Path, out var other) && _memory.Contains(other)) continue;
            missing++;
        }
        return missing;
    }

    /// <summary>Times the screen's empty cells; a stretch over a second is a wait, which the program counts.</summary>
    void ObserveFill()
    {
        if (_fill.Observe(Environment.TickCount64, Unanswered())) WaitedForThumbnails?.Invoke();
    }

    void StartIdleTimer()
    {
        if (_idleTimer == null)
        {
            _idleTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _idleTimer.Tick += (_, _) => IdleTick();
        }
        if (!_idleTimer.Enabled) _idleTimer.Start();
    }

    /// <summary>"Instant thumbnails" was turned on: the idle step may start now, without waiting for a scroll.</summary>
    public void StoreChoiceChanged()
    {
        if (!_closed && Visible) StartIdleTimer();
    }

    void IdleTick()
    {
        if (_closed || !Visible) { _idleTimer?.Stop(); return; }
        ObserveFill(); // a screen that never fills is a wait too
        if (_client is not { } client) return;
        if (!client.IsIdle) return;
        // Everything handed out was made.
        _idleInFlight = false;
        _idlePending.Clear();
        // Nothing more to do here: the list is used up or empty, or the option is off (no idle step: there is nowhere to
        // keep its pictures). The timer stops once the screen is full; a viewport, or the option turned on, starts it
        // again. With the option on at a location that can't be used now, it keeps ticking, so the step starts by itself
        // when the location comes back.
        if (_idle.Exhausted || _count == 0 || !ThumbHost.StoreChosen) { if (!_fill.Running) _idleTimer?.Stop(); return; }
        if (!ThumbHost.KeepsStore) return;
        if (Environment.TickCount64 - _lastActivity < IdleAfterMs) return;
        var chunk = _idle.Next(_count, _idleStart, i => !_cellKeys.ContainsKey(i));
        // Nothing left to hand out (_idle.Done: a short list all handed out; or a huge one whose walk found nothing near
        // the cap): the timer stops once the screen is full, rather than walk the same list twice a second. Next is asked
        // on every tick, not skipped while Done, because a scroll moves the plan and the items it no longer covers become
        // wanted. Whatever can make new candidates starts the timer again: a viewport (scroll, resize, a new list,
        // another size, idle work given back), the option turned on, Settings' Clear.
        if (_idle.Done) { if (!_fill.Running) _idleTimer?.Stop(); return; }
        var requests = new List<ThumbRequest>(chunk.Count);
        var keep = ThumbHost.Keep;
        foreach (int i in chunk)
        {
            string? path = PathAt(i);
            // Only kinds kept on disk (those the estimate counts): the idle step has nothing to make of the rest (a page of
            // a file's own text, a music file's cover, a folder), nor of the kinds the user chose not to keep.
            if (path is null || !ThumbKinds.KeepOnDisk(path) || !keep.Keeps(path)) continue;
            var stamp = StampAt(i);
            var key = ThumbKey.Of(path, stamp, _thumbSize);
            if (_memory.Peek((key, _thumbSize), out var held) && held.Quality != ThumbQuality.Low) continue;
            _idleKeys.Add(key);
            _idlePending[key] = i;
            requests.Add(new ThumbRequest(path, stamp, _thumbSize, ThumbPriority.Idle, i));
        }
        if (requests.Count == 0) return;
        Counters.IdleRequests += requests.Count;
        if (client.SubmitIdle(_listNumber, requests) > 0) _idleInFlight = true;
    }

    /// <summary>Forgets every picture held (Settings' Clear) and asks again for what the view shows.</summary>
    public void ClearThumbs()
    {
        ThumbHost.ClearMemory();
        _latest.Clear();
        _idleKeys.Clear();
        _idlePending.Clear();
        _idle.Reset();
        _fill.Reset();
        QueueViewport();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            _memory.Evicted -= OnEvicted;
            ThumbHost.NotePlan(this, 0);
            _idleTimer?.Dispose();
            _outlinePen?.Dispose();
            _fadedOutlinePen?.Dispose();
            FreeCaptionFont();
            _client?.Dispose();
            DisposeArrivals();
            // The pictures stay in the program's memory for the other windows.
            _latest.Clear();
            StopRenameClick();
        }
        base.Dispose(disposing);
    }

    // ---- input -----------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _lastActivity = Environment.TickCount64;
        bool hadFocus = Focused;
        Focus();
        _linkDown = e.Button == MouseButtons.Left && EmptyLinkBounds().Contains(e.Location);
        if (_linkDown) return;
        StopRenameClick();
        SetHot(-1);
        int i = HitTest(e.Location);
        _downIndex = i;
        _downAt = e.Location;
        _downKeys = ModifierKeys;
        _clickOnRelease = false;
        // The only selected thumbnail clicked again, in a grid that had the focus: released without a drag or a
        // double-click, it renames, as in Explorer. The second press of a double-click (Clicks 2) never does: it opens.
        _renameIndex = e.Button == MouseButtons.Left && e.Clicks == 1 && hadFocus && i >= 0 && _downKeys == Keys.None && IsSelected(i) && SelectedCount() == 1 ? i : -1;
        if (i < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            // As in File Explorer: a press on a selected thumbnail keeps the selection, so dragging it takes every
            // selected file; released without a drag it is the click (that one alone, or with Ctrl, that one out).
            // Anywhere else the press is the click.
            if (IsSelected(i) && (_downKeys & Keys.Shift) == 0) _clickOnRelease = true;
            else ClickItem(i, _downKeys);
        }
        else if (e.Button == MouseButtons.Right && !IsSelected(i))
        {
            _anchor = i;
            ItemClicked?.Invoke(i, Keys.None);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_emptyLink is not null) TrackLinkHover(e.Location);
        SetHot(e.Button == MouseButtons.None ? HitTest(e.Location) : -1); // no highlight while a button is held
        if (e.Button != MouseButtons.Left || _downIndex < 0) return;
        var d = SystemInformation.DragSize;
        if (Math.Abs(e.X - _downAt.X) > d.Width || Math.Abs(e.Y - _downAt.Y) > d.Height)
        {
            _downIndex = -1;
            _clickOnRelease = false;
            _renameIndex = -1;
            ItemDrag?.Invoke();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // The window closing for good: WinForms calls this while it destroys the grid's handle, if the pointer was on
        // it. Nothing is left to repaint (and the window's fonts may be gone), so the highlight is only forgotten.
        if (TopLevelControl is { } top && (top.Disposing || top.IsDisposed)) { _hot = -1; return; }
        if (_emptyLink is not null) TrackLinkHover(new Point(-1, -1));
        SetHot(-1);
    }

    // ---- Explorer's hover highlight ----------------------------------------------------------------

    int _hot = -1;

    /// <summary>Moves the highlight, repainting only the thumbnail it leaves and the one it goes to.</summary>
    void SetHot(int index)
    {
        if (index == _hot) return;
        if (_hot >= 0 && _hot < _count) Invalidate(CellRect(_hot));
        _hot = index;
        if (index >= 0) Invalidate(CellRect(index));
    }

    void RefreshHot()
    {
        var p = PointToClient(Cursor.Position);
        SetHot(MouseButtons == MouseButtons.None && ClientRectangle.Contains(p) ? HitTest(p) : -1);
    }

    // ---- a slow second click renames, as in Explorer ------------------------------------------------

    int _renameIndex = -1;
    Point _renameAt;
    System.Windows.Forms.Timer? _renameTimer;

    void StartRenameTimer(int index, Point at)
    {
        StopRenameClick();
        _renameAt = at;
        _renameTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        _renameTimer.Tick += (_, _) =>
        {
            StopRenameClick();
            if (Focused && index < _count && IsSelected(index) && SelectedCount() == 1) RenameClick?.Invoke(index, _renameAt);
        };
        _renameTimer.Start();
    }

    void StopRenameClick()
    {
        _renameTimer?.Dispose();
        _renameTimer = null;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_linkDown && e.Button == MouseButtons.Left)
        {
            _linkDown = false;
            if (EmptyLinkBounds().Contains(e.Location)) EmptyLinkClicked?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Right && HitTest(e.Location) >= 0)
        {
            SetHot(-1); // no highlight while the menu is open
            ItemContextMenu?.Invoke(PointToScreen(e.Location));
        }
        // The keys held as it was pressed: Ctrl let go a moment before the button is still a Ctrl+click.
        if (e.Button == MouseButtons.Left && _clickOnRelease && _downIndex >= 0) ClickItem(_downIndex, _downKeys);
        if (e.Button == MouseButtons.Left && _renameIndex >= 0 && HitTest(e.Location) == _renameIndex) StartRenameTimer(_renameIndex, _downAt);
        _renameIndex = -1;
        _clickOnRelease = false;
        _downIndex = -1;
    }

    void ClickItem(int i, Keys keys)
    {
        if ((keys & Keys.Shift) == 0) _anchor = i;
        ItemClicked?.Invoke(i, keys);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        StopRenameClick(); // a double-click opens
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) ItemActivated?.Invoke(i);
    }

    /// <summary>
    /// A mouse wheel sends 120 per notch; a touchpad sends many small steps (10, 20, 40...). Every step counts, so a
    /// touchpad scrolls smoothly: a notch moves a third of a row per line Windows scrolls (one row at its default of 3
    /// lines), or a screen when Windows is set to scroll a screen at a time. Ctrl zooms, a notch at a time.
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollByWheel(e.Delta);
    }

    void ScrollByWheel(int delta)
    {
        _lastActivity = Environment.TickCount64;
        StopRenameClick();
        Scrolling?.Invoke();
        // A change of direction starts afresh, so a leftover never moves the grid, or zooms, the old way.
        if ((ModifierKeys & Keys.Control) != 0)
        {
            _wheelPixels = 0;
            if (Math.Sign(_zoomDelta) != Math.Sign(delta)) _zoomDelta = 0;
            _zoomDelta += delta;
            int notches = _zoomDelta / 120;
            if (notches == 0) return;
            _zoomDelta -= notches * 120;
            ZoomRequested?.Invoke(notches);
            return;
        }
        _zoomDelta = 0;
        int lines = SystemInformation.MouseWheelScrollLines;
        double perNotch = lines < 0 ? ClientSize.Height : Cell.Height * Math.Max(1, lines) / 3.0;
        if (Math.Sign(_wheelPixels) != Math.Sign(delta)) _wheelPixels = 0;
        _wheelPixels += delta * perNotch / 120.0;
        long pixels = (long)_wheelPixels;
        if (pixels == 0) return;
        _wheelPixels -= pixels;
        ScrollTo(_offset - pixels);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _lastActivity = Environment.TickCount64;
        StopRenameClick();
        if (e.KeyData == Keys.Enter && EmptyLinkShowing)
        {
            e.Handled = e.SuppressKeyPress = true;
            EmptyLinkClicked?.Invoke();
            return;
        }
        int cols = Columns, cur = Math.Max(0, FocusedIndex());
        int target = e.KeyCode switch
        {
            Keys.Left => cur - 1,
            Keys.Right => cur + 1,
            Keys.Up => cur - cols,
            Keys.Down => cur + cols,
            Keys.PageUp => cur - cols * VisibleRows,
            Keys.PageDown => cur + cols * VisibleRows,
            Keys.Home => 0,
            Keys.End => _count - 1,
            _ => int.MinValue,
        };
        if (target != int.MinValue)
        {
            if (_count == 0) return;
            target = Math.Clamp(target, 0, _count - 1);
            if (!e.Shift) _anchor = target;
            Navigate?.Invoke(target, e.Shift, e.Control);
            EnsureVisible(target);
            Invalidate();
            e.Handled = true;
            return;
        }
        ForwardKeyDown?.Invoke(this, e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        ForwardKeyPress?.Invoke(this, e);
    }

    /// <summary>Anchor for shift-range selection (the last plainly clicked/navigated item).</summary>
    public int SelectionAnchor => _anchor;

    // The Explorer context menu is owned by this window when shown from the grid; route its owner-draw messages.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmTakeArrivals)
        {
            TakeArrivals();
            return;
        }
        if (Interop.ShellContextMenu.HandleMenuMessage(ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); StopRenameClick(); Invalidate(); }
}
