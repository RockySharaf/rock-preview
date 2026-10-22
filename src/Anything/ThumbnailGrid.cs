using Anything.Preview;

namespace Anything;

/// <summary>
/// Owner-drawn virtual grid of thumbnails for the current results. It owns no selection: the main window's
/// virtual ListView stays the single source of truth, so every list command (open, copy, delete, shell menu…)
/// works unchanged in thumbnail mode. Thumbnails load lazily for visible cells through
/// <see cref="ThumbnailProvider"/>; requests for cells scrolled out of view are cancelled.
/// </summary>
internal sealed class ThumbnailGrid : Control
{
    const int Pad = 8;
    const long MaxCachedBytes = 40L * 1024 * 1024; // decoded thumbnails kept for nearby rows
    long _cachedBytes;

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
    readonly Dictionary<string, Bitmap> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _order = new();
    readonly HashSet<string> _none = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
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

    Point _downAt;
    int _downIndex = -1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, string?> PathAt { get; set; } = _ => null;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, string> NameAt { get; set; } = _ => "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, bool> IsSelected { get; set; } = _ => false;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int> FocusedIndex { get; set; } = () => -1;

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

    public ThumbnailGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        _scroll = new GridScrollBar(this) { Dock = DockStyle.Right };
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => { if (!_syncing) SetOffset((long)_scroll.Value * _unit, fromBar: true); };
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
            Relayout(() => _thumbSize = value);
            ClearThumbs();
            Invalidate();
        }
    }

    /// <summary>Sets the number of results; <paramref name="reset"/> scrolls to the top (new query).</summary>
    public void SetCount(int count, bool reset)
    {
        _count = count;
        CancelPending();
        if (reset) { _anchor = -1; _offset = 0; _wheelPixels = 0; _ensurePending = -1; }
        long before = _offset;
        UpdateScroll();
        // More or fewer results with the view where it was: the item it keeps at the top stays the same one, but
        // whether that's the end is measured again (more results: it no longer is).
        if (reset || _offset != before) Remember();
        else _atEnd = _offset > 0 && _offset >= MaxOffset;
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
        _offset = offset;
        if (!fromBar) SyncBar();
        Remember();
        if (moved) Invalidate();
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
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Relayout();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // An item to bring into view: only once the parent has laid the grid out at its new size (it isn't yet while
        // this runs; a size that changed meanwhile comes as a resize, which handles it first).
        if (Visible && _ensurePending >= 0 && IsHandleCreated)
            BeginInvoke(() => { if (Visible && _ensurePending >= 0) EnsureVisible(_ensurePending); });
    }

    Rectangle CellRect(int index)
    {
        int cols = Columns, row = index / cols, col = index % cols;
        var c = Cell;
        int spare = (GridWidth - cols * c.Width) / 2; // center the grid horizontally
        long y = (long)row * c.Height - _offset;
        return new Rectangle(spare + col * c.Width, (int)Math.Clamp(y, int.MinValue / 4, int.MaxValue / 4), c.Width, c.Height);
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
        var g = e.Graphics;
        g.Clear(BackColor);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        if (_count == 0)
        {
            if (_emptyText.Length == 0) return;
            int pad = LogicalToDeviceUnits(16);
            var r = new Rectangle(pad, LogicalToDeviceUnits(24), Math.Max(0, ClientSize.Width - 2 * pad), Math.Max(0, ClientSize.Height / 2));
            TextRenderer.DrawText(g, _emptyText, Font, r, ExplorerTheme.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        // The rows the view shows, partly or whole.
        int cols = Columns, cellHeight = Cell.Height;
        int first = (int)Math.Min(_count, _offset / cellHeight * cols);
        int last = (int)Math.Min(_count - 1L, ((_offset + ClientSize.Height) / cellHeight + 1) * cols - 1);
        int focused = FocusedIndex();
        var visiblePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selBack = Color.FromArgb(90, SystemColors.Highlight);
        using var selBrush = new SolidBrush(selBack);
        using var frame = new Pen(Color.FromArgb(70, ForeColor));
        using var focusPen = new Pen(SystemColors.Highlight, 1.5f);

        for (int i = first; i <= last; i++)
        {
            var cell = CellRect(i);
            if (!cell.IntersectsWith(e.ClipRectangle)) continue;
            var inner = Rectangle.Inflate(cell, -3, -3);
            if (IsSelected(i)) g.FillRectangle(selBrush, inner);
            if (i == focused && Focused) g.DrawRectangle(focusPen, inner);

            var box = new Rectangle(cell.X + Pad, cell.Y + Pad, _thumbSize, _thumbSize);
            string? path = PathAt(i);
            if (path is not null)
            {
                visiblePaths.Add(path);
                if (_thumbs.TryGetValue(path, out var bmp)) DrawFitted(g, bmp, box);
                else
                {
                    g.DrawRectangle(frame, Rectangle.Inflate(box, -box.Width / 6, -box.Height / 6));
                    if (!_none.Contains(path)) Request(path);
                }
            }
            var textRect = new Rectangle(cell.X + 4, box.Bottom + 4, cell.Width - 8, TextHeight);
            TextRenderer.DrawText(g, NameAt(i), Font, textRect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }

        // Cancel loads for cells that scrolled away.
        foreach (var (p, cts) in _pending.ToArray())
            if (!visiblePaths.Contains(p)) { cts.Cancel(); _pending.Remove(p); }
    }

    static void DrawFitted(Graphics g, Bitmap bmp, Rectangle box)
    {
        float scale = Math.Min(1f, Math.Min((float)box.Width / bmp.Width, (float)box.Height / bmp.Height));
        int w = Math.Max(1, (int)(bmp.Width * scale)), h = Math.Max(1, (int)(bmp.Height * scale));
        g.DrawImage(bmp, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    // ---- thumbnail loading -------------------------------------------------------------------

    void Request(string path)
    {
        if (_pending.ContainsKey(path)) return;
        var cts = new CancellationTokenSource();
        _pending[path] = cts;
        int size = _thumbSize;
        _ = LoadAsync(path, size, cts);
    }

    async Task LoadAsync(string path, int size, CancellationTokenSource cts)
    {
        Bitmap? bmp = null;
        try { bmp = await ThumbnailProvider.GetThumbnailAsync(path, size, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { /* treated as "no thumbnail" below */ }
        if (IsDisposed || !IsHandleCreated) { bmp?.Dispose(); return; }
        try
        {
            BeginInvoke(() =>
            {
                if (_pending.TryGetValue(path, out var current) && current == cts) _pending.Remove(path);
                if (cts.IsCancellationRequested || size != _thumbSize) { bmp?.Dispose(); return; }
                if (bmp is null) _none.Add(path);
                else Store(path, bmp);
                Invalidate();
            });
        }
        catch (InvalidOperationException) { bmp?.Dispose(); }
    }

    static long Bytes(Bitmap b) => (long)b.Width * b.Height * 4;

    void Store(string path, Bitmap bmp)
    {
        if (_thumbs.Remove(path, out var old)) { _cachedBytes -= Bytes(old); old.Dispose(); }
        _thumbs[path] = bmp;
        _cachedBytes += Bytes(bmp);
        _order.Enqueue(path);
        while (_cachedBytes > MaxCachedBytes && _thumbs.Count > 1 && _order.TryDequeue(out var evict))
            if (_thumbs.Remove(evict, out var b)) { _cachedBytes -= Bytes(b); b.Dispose(); }
    }

    void CancelPending()
    {
        foreach (var cts in _pending.Values) cts.Cancel();
        _pending.Clear();
    }

    public void ClearThumbs()
    {
        CancelPending();
        foreach (var b in _thumbs.Values) b.Dispose();
        _thumbs.Clear();
        _order.Clear();
        _cachedBytes = 0;
        _none.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearThumbs();
        base.Dispose(disposing);
    }

    // ---- input -----------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitTest(e.Location);
        _downIndex = i;
        _downAt = e.Location;
        if (i < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            if ((ModifierKeys & Keys.Shift) == 0) _anchor = i;
            ItemClicked?.Invoke(i, ModifierKeys);
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
        if (e.Button != MouseButtons.Left || _downIndex < 0) return;
        var d = SystemInformation.DragSize;
        if (Math.Abs(e.X - _downAt.X) > d.Width || Math.Abs(e.Y - _downAt.Y) > d.Height)
        {
            _downIndex = -1;
            ItemDrag?.Invoke();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right && HitTest(e.Location) >= 0) ItemContextMenu?.Invoke(PointToScreen(e.Location));
        _downIndex = -1;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
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
        if (Interop.ShellContextMenu.HandleMenuMessage(ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
