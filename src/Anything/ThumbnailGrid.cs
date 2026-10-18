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

    readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, SmallChange = 1 };
    readonly Dictionary<string, Bitmap> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _order = new();
    readonly HashSet<string> _none = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    int _count, _thumbSize = 160, _anchor = -1;
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
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ThumbSize
    {
        get => _thumbSize;
        set
        {
            value = Math.Clamp(value, 48, 512);
            if (value == _thumbSize) return;
            _thumbSize = value;
            ClearThumbs();
            UpdateScroll();
            Invalidate();
        }
    }

    /// <summary>Sets the number of results; <paramref name="reset"/> scrolls to the top (new query).</summary>
    public void SetCount(int count, bool reset)
    {
        _count = count;
        CancelPending();
        if (reset) { _anchor = -1; _scroll.Value = 0; }
        UpdateScroll();
        Invalidate();
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _count) return;
        int cols = Columns, row = index / cols, top = _scroll.Value, vis = VisibleRows;
        if (row < top) SetTop(row);
        else if (row >= top + vis) SetTop(row - vis + 1);
    }

    // ---- geometry -------------------------------------------------------------------------------

    int TextHeight => Font.Height * 2 + 4;
    Size Cell => new(_thumbSize + Pad * 2, _thumbSize + TextHeight + Pad * 2);
    int GridWidth => Math.Max(1, ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0));
    int Columns => Math.Max(1, GridWidth / Cell.Width);
    int Rows => (_count + Columns - 1) / Columns;
    int VisibleRows => Math.Max(1, ClientSize.Height / Cell.Height);

    void SetTop(int row) => _scroll.Value = Math.Clamp(row, _scroll.Minimum, Math.Max(_scroll.Minimum, _scroll.Maximum - _scroll.LargeChange + 1));

    void UpdateScroll()
    {
        int vis = VisibleRows, rows = Rows;
        _scroll.LargeChange = vis;
        _scroll.Maximum = Math.Max(0, rows - 1);
        _scroll.Enabled = rows > vis;
        if (_scroll.Value > Math.Max(0, rows - vis)) _scroll.Value = Math.Max(0, rows - vis);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateScroll();
    }

    Rectangle CellRect(int index)
    {
        int cols = Columns, row = index / cols - _scroll.Value, col = index % cols;
        var c = Cell;
        int spare = (GridWidth - cols * c.Width) / 2; // center the grid horizontally
        return new Rectangle(spare + col * c.Width, row * c.Height, c.Width, c.Height);
    }

    int HitTest(Point p)
    {
        var c = Cell;
        int cols = Columns, spare = (GridWidth - cols * c.Width) / 2;
        if (p.X < spare || p.X >= spare + cols * c.Width || p.Y < 0) return -1;
        int index = (_scroll.Value + p.Y / c.Height) * cols + (p.X - spare) / c.Width;
        return index < _count ? index : -1;
    }

    // ---- painting ------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        if (_count == 0) return;

        int cols = Columns, first = _scroll.Value * cols, last = Math.Min(_count - 1, (_scroll.Value + VisibleRows + 1) * cols - 1);
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = e.Delta / 120;
        if (notches == 0) return;
        if ((ModifierKeys & Keys.Control) != 0) { ZoomRequested?.Invoke(notches); return; }
        SetTop(_scroll.Value - notches * Math.Max(1, SystemInformation.MouseWheelScrollLines / 3));
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
