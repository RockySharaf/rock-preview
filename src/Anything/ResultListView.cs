using System.Runtime.InteropServices;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Virtual, owner-drawn, double-buffered details ListView using the shell's system image list.
/// Raises separate context-menu events for the column header and the item area, and forwards
/// menu messages to an open shell context menu.
/// </summary>
internal sealed class ResultListView : ListView
{
    const int LVS_SHAREIMAGELISTS = 0x0040;
    const int LVM_SETITEMSTATE = Native.LVM_FIRST + 43;
    const uint LVIF_STATE = 0x0008;
    const uint LVIS_SELECTED = 0x0002;
    const uint LVIS_FOCUSED = 0x0001;
    const int LVM_GETITEMSTATE = Native.LVM_FIRST + 44;

    [StructLayout(LayoutKind.Sequential)]
    struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessageItem(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

    /// <summary>Screen point of a right-click on the column header.</summary>
    public event Action<Point>? HeaderContextMenu;
    /// <summary>Screen point of a right-click in the item area; (-1,-1) when invoked from the keyboard.</summary>
    public event Action<Point>? ItemContextMenu;

    public ResultListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        OwnerDraw = true;
        DoubleBuffered = true;
        AllowColumnReorder = true;
        MultiSelect = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        BorderStyle = BorderStyle.None;
        LabelEdit = false;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= LVS_SHAREIMAGELISTS; // the system image list must never be destroyed by us
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_rowHeight > 0) ApplyRowHeight();
        else AttachSystemImageList();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _marquee = null;
        _rightDownInEmpty = false;
        StopMarqueeScroll();
        _layer?.Dispose();
        _layer = null;
        base.OnHandleDestroyed(e);
        if (_rowList != IntPtr.Zero) { ImageList_Destroy(_rowList); _rowList = IntPtr.Zero; }
        if (_headerImages != IntPtr.Zero) { ImageList_Destroy(_headerImages); _headerImages = IntPtr.Zero; }
    }

    IntPtr _rowList;
    int _rowHeight;

    /// <summary>The rows' height in pixels (0: the list's own). Icons are drawn from the system image list either way.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RowHeight
    {
        get => _rowHeight;
        set
        {
            if (value == _rowHeight) return;
            _rowHeight = value;
            if (IsHandleCreated) ApplyRowHeight();
        }
    }

    /// <summary>
    /// A report view's rows are as tall as its small image list's images and a pixel: an empty list that tall sets them.
    /// </summary>
    void ApplyRowHeight()
    {
        IntPtr old = _rowList;
        _rowList = _rowHeight > 1 ? ImageList_Create(1, _rowHeight - 1, ILC_COLOR32, 1, 0) : IntPtr.Zero;
        if (_rowList != IntPtr.Zero) Native.SendMessage(Handle, Native.LVM_SETIMAGELIST, (IntPtr)Native.LVSIL_SMALL, _rowList);
        else AttachSystemImageList();
        if (old != IntPtr.Zero) ImageList_Destroy(old);
    }

    /// <summary>Whether Windows shows keyboard focus now (after Tab or arrow keys, not after a click), like Explorer's focus border.</summary>
    public bool FocusCuesShown => ShowFocusCues;

    const uint ILC_COLOR32 = 0x20;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
              WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_CAPTURECHANGED = 0x215, MK_SHIFT = 0x4, MK_CONTROL = 0x8;
    const int LVM_SCROLL = Native.LVM_FIRST + 20, LVM_GETTOPINDEX = Native.LVM_FIRST + 39,
              LVM_GETSELECTIONMARK = Native.LVM_FIRST + 66, LVM_SETSELECTIONMARK = Native.LVM_FIRST + 67;
    const int WM_HSCROLL = 0x114, WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
    const int HDM_SETIMAGELIST = 0x1200 + 8;
    const uint HDI_IMAGE = 0x0020;
    const int HDF_IMAGE = 0x0800;
    static Point PointOf(IntPtr lParam) => new(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
    [DllImport("comctl32.dll")] static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int initial, int grow);
    [DllImport("comctl32.dll")] static extern bool ImageList_Destroy(IntPtr himl);
    [DllImport("comctl32.dll")] static extern int ImageList_Add(IntPtr himl, IntPtr image, IntPtr mask);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern int GetScrollPos(IntPtr hwnd, int bar);

    public void AttachSystemImageList()
    {
        if (IsHandleCreated && ShellInfo.SmallImageList != IntPtr.Zero)
            Native.SendMessage(Handle, Native.LVM_SETIMAGELIST, (IntPtr)Native.LVSIL_SMALL, ShellInfo.SmallImageList);
    }

    public IntPtr HeaderHandle => IsHandleCreated ? Native.SendMessage(Handle, Native.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;

    /// <summary>Shows the native sort arrow on <paramref name="column"/> (or none when -1).</summary>
    public void SetSortIndicator(int column, bool descending)
    {
        IntPtr header = HeaderHandle;
        if (header == IntPtr.Zero) return;
        for (int i = 0; i < Columns.Count; i++)
        {
            var item = new Native.HDITEM { mask = Native.HDI_FORMAT };
            Native.SendMessage(header, Native.HDM_GETITEMW, (IntPtr)i, ref item);
            item.fmt &= ~(Native.HDF_SORTDOWN | Native.HDF_SORTUP);
            if (i == column) item.fmt |= descending ? Native.HDF_SORTDOWN : Native.HDF_SORTUP;
            Native.SendMessage(header, Native.HDM_SETITEMW, (IntPtr)i, ref item);
        }
    }

    public void SetAllSelected(bool selected)
    {
        var item = new LVITEM { mask = LVIF_STATE, stateMask = LVIS_SELECTED, state = selected ? LVIS_SELECTED : 0 };
        SendMessageItem(Handle, LVM_SETITEMSTATE, (IntPtr)(-1), ref item);
    }

    public void SetSelected(int index, bool selected)
    {
        var item = new LVITEM { mask = LVIF_STATE, stateMask = LVIS_SELECTED, state = selected ? LVIS_SELECTED : 0 };
        SendMessageItem(Handle, LVM_SETITEMSTATE, (IntPtr)index, ref item);
    }

    /// <summary>
    /// WinForms' ListView accessibility provider materializes a managed AccessibleObject per item; with millions of
    /// virtual rows any UI Automation client (screen reader, automation tool) walking the tree freezes the UI thread
    /// and exhausts memory. A plain ControlAccessibleObject defers to the native SysListView32 proxy, which serves
    /// owner-data items on demand.
    /// </summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new ControlAccessibleObject(this);

    /// <summary>O(1) selection test (SelectedIndices.Contains walks every selected item in virtual mode).</summary>
    public bool IsItemSelected(int index)
        => IsHandleCreated && ((uint)Native.SendMessage(Handle, LVM_GETITEMSTATE, (IntPtr)index, (IntPtr)LVIS_SELECTED) & LVIS_SELECTED) != 0;

    /// <summary>Moves the focus rectangle to <paramref name="index"/> without touching the selection.</summary>
    public void SetFocusedIndex(int index)
    {
        var item = new LVITEM { mask = LVIF_STATE, stateMask = LVIS_FOCUSED, state = LVIS_FOCUSED };
        SendMessageItem(Handle, LVM_SETITEMSTATE, (IntPtr)index, ref item);
    }

    /// <summary>Selects exactly the inclusive range <paramref name="from"/>..<paramref name="to"/>.</summary>
    public void SelectRange(int from, int to)
    {
        SetAllSelected(false);
        if (from > to) (from, to) = (to, from);
        for (int i = Math.Max(0, from); i <= to && i < VirtualListSize; i++) SetSelected(i, true);
    }

    public int[] GetSelectedIndices(int max = int.MaxValue)
    {
        int n = SelectedIndices.Count;
        if (n == 0) return [];
        var result = new List<int>(Math.Min(n, max));
        foreach (int i in SelectedIndices)
        {
            result.Add(i);
            if (result.Count >= max) break;
        }
        return result.ToArray();
    }

    public int RowsPerPage => IsHandleCreated ? (int)Native.SendMessage(Handle, Native.LVM_GETCOUNTPERPAGE, IntPtr.Zero, IntPtr.Zero) : 0;

    string _emptyText = "";

    /// <summary>Gray text shown below the column headers while the list has no items, as Explorer does; empty for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            if (value == _emptyText) return;
            _emptyText = value;
            if (VirtualListSize == 0) Invalidate();
        }
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool GetUpdateRect(IntPtr hWnd, out RECT rect, bool erase);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Draws <see cref="EmptyText"/> over the part of the (empty) list just painted: the owner-drawn list draws nothing
    /// there itself, and drawing outside it would draw the text twice over.
    /// </summary>
    void DrawEmptyText(Rectangle painted)
    {
        int top = 0;
        if (HeaderHandle is var header && header != IntPtr.Zero && GetWindowRect(header, out var hr)) top = hr.Bottom - hr.Top;
        int pad = LogicalToDeviceUnits(16);
        var r = new Rectangle(pad, top + LogicalToDeviceUnits(20), Math.Max(0, ClientSize.Width - 2 * pad), LogicalToDeviceUnits(80));
        using var g = CreateGraphics();
        g.SetClip(painted);
        TextRenderer.DrawText(g, _emptyText, Font, r, ExplorerTheme.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    // ---- Explorer's empty space and selection rectangle -------------------------------------------

    int _rowMargin;

    /// <summary>
    /// The empty margin before each row's box (pixels), part of the first column as in Explorer: the leftmost column's
    /// title starts after it too.
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RowMargin
    {
        get => _rowMargin;
        set
        {
            if (value == _rowMargin) return;
            _rowMargin = value;
            IndentHeader();
        }
    }

    /// <summary>The empty gap above and below each row's box (pixels).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RowGapTop { get; set; }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RowGapBottom { get; set; }

    /// <summary>
    /// Whether <paramref name="p"/> (client) is outside every row's box, as in Explorer: the margin before the rows, the
    /// gaps between them, past the last column and below the last row. A press there selects with a rectangle.
    /// </summary>
    public bool IsEmptySpace(Point p)
    {
        if (!IsHandleCreated || p.Y < RowsTop) return false; // the column headers
        int index = IndexAtY(p.Y);
        if (index < 0 || index >= VirtualListSize) return true;
        int x = p.X + HorizontalScroll(), top = RowTop(index);
        return x < _rowMargin || x >= ColumnsWidth() || p.Y < top + RowGapTop || p.Y >= top + Pitch - RowGapBottom;
    }

    /// <summary>
    /// Whether a client point on a row is on what the row shows (its icon or a cell's text), set by whoever draws them.
    /// As in Explorer, a press anywhere else on a row that isn't selected draws a rectangle, as in empty space.
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, Point, bool>? IsOnRowContent { get; set; }

    /// <summary>
    /// Where a press draws a rectangle, as in Explorer: empty space (<paramref name="row"/> -1), or a row that isn't
    /// selected away from its icon and text (that row). A press on a selected row or on what a row shows is the list's.
    /// </summary>
    bool StartsMarquee(Point p, out int row)
    {
        row = -1;
        if (IsEmptySpace(p)) return true;
        if (!IsHandleCreated || p.Y < RowsTop || IsOnRowContent is not { } onContent) return false;
        int index = IndexAtY(p.Y);
        if (index < 0 || index >= VirtualListSize || IsItemSelected(index) || onContent(index, p)) return false;
        row = index;
        return true;
    }

    /// <summary>A rectangle being drawn: what it started from and what it covers now.</summary>
    sealed class Marquee
    {
        public Point Anchor;               // where it started, in the rows' own coordinates (it scrolls with them)
        public Point Pointer;              // the pointer now, client
        public int Keys;                   // MK_CONTROL and MK_SHIFT as they are now (they steer the rectangle)
        public int PressKeys;              // as the button went down: what a click (and the rectangle's start) does
        public int Row = -1;               // the row it was started on (-1: empty space)
        public bool Shown;                 // moved past the drag size: a rectangle, not a click
        public int First = -1, Last = -1;  // the rows it covers now
        public int ScrollRest;             // auto-scroll's pixels short of a whole row, kept for the next tick
    }

    Marquee? _marquee;
    MarqueeLayer? _layer;
    System.Windows.Forms.Timer? _marqueeScroll;

    /// <summary>Explorer's selection rectangle (measured from its own view): a #0078D7 border around #0066CC at a third.</summary>
    static readonly Color MarqueeBorder = Color.FromArgb(0x00, 0x78, 0xD7), MarqueeFill = Color.FromArgb(0x55, 0x00, 0x66, 0xCC);

    /// <summary>Explorer's auto-scroll (ExplorerFrame): every 83 ms by how far the pointer is past the edge, sideways in 100s.</summary>
    const int MarqueeScrollInterval = 83, MarqueeScrollSideways = 100;

    int Pitch => _rowHeight > 0 ? _rowHeight : VirtualListSize > 0 ? Math.Max(1, GetItemRect(TopRow).Height) : 1;
    int TopRow => (int)Native.SendMessage(Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
    int RowsTop => HeaderHandle is var header && header != IntPtr.Zero && GetWindowRect(header, out var hr) ? hr.Bottom - hr.Top : 0;
    int RowTop(int index) => RowsTop + (index - TopRow) * Pitch;
    int IndexAtY(int y) => (int)Math.Floor((double)(y - RowsTop) / Pitch) + TopRow;
    int HorizontalScroll() => GetScrollPos(Handle, 0 /* SB_HORZ */);
    int ColumnsWidth() { int w = 0; foreach (ColumnHeader c in Columns) w += c.Width; return w; }
    Point ToRows(Point client) => new(client.X + HorizontalScroll(), client.Y - RowsTop + TopRow * Pitch);
    Point ToClient(Point rows) => new(rows.X - HorizontalScroll(), rows.Y + RowsTop - TopRow * Pitch);
    Rectangle RowsArea => new(0, RowsTop, ClientSize.Width, Math.Max(0, ClientSize.Height - RowsTop));

    /// <summary>A press where a rectangle starts: nothing changes until the pointer moves past the drag size, as in Explorer.</summary>
    void BeginMarquee(Point p, int keys, int row)
    {
        _rightDownInEmpty = false;
        Focus();
        _marquee = new Marquee { Anchor = ToRows(p), Pointer = p, Keys = keys, PressKeys = keys, Row = row };
        Capture = true;
    }

    void TrackMarquee(Point p)
    {
        var marquee = _marquee!;
        marquee.Pointer = p;
        if (!marquee.Shown)
        {
            // ExplorerFrame starts it once the pointer is more than SM_CXDRAG or SM_CYDRAG away.
            var anchor = ToClient(marquee.Anchor);
            var drag = SystemInformation.DragSize;
            if (Math.Abs(p.X - anchor.X) <= drag.Width && Math.Abs(p.Y - anchor.Y) <= drag.Height) return;
            marquee.Shown = true;
            if (marquee.Row >= 0)
            {
                // From a row: that row is clicked first, then the rectangle adds to it (it covers it from the start).
                ClickRow(marquee.Row, marquee.PressKeys);
                marquee.First = marquee.Last = marquee.Row;
            }
            else if ((marquee.PressKeys & (MK_CONTROL | MK_SHIFT)) == 0)
            {
                SetAllSelected(false); // from empty space the selection goes, unless Ctrl or Shift keeps it
                SetSelectionMark(-1);
            }
        }

        // The rows whose boxes it touches: in the rows' coordinates, a row's box is [i*pitch + gap, (i+1)*pitch - gap).
        var now = ToRows(p);
        int x0 = Math.Min(now.X, marquee.Anchor.X), x1 = Math.Max(now.X, marquee.Anchor.X);
        int y0 = Math.Min(now.Y, marquee.Anchor.Y), y1 = Math.Max(now.Y, marquee.Anchor.Y);
        int pitch = Pitch, first = -1, last = -1;
        if (x1 >= _rowMargin && x0 < ColumnsWidth() && VirtualListSize > 0)
        {
            first = Math.Max(0, (int)Math.Floor((double)(y0 - pitch + RowGapBottom) / pitch) + 1);
            last = Math.Min(VirtualListSize - 1, (int)Math.Floor((double)(y1 - RowGapTop) / pitch));
            if (first > last) first = last = -1;
        }
        SelectCovered(marquee, first, last);
        ShowMarquee(marquee);

        bool scrolls = ScrollNeeded(marquee) != Point.Empty;
        if (scrolls && _marqueeScroll == null)
        {
            _marqueeScroll = new System.Windows.Forms.Timer { Interval = MarqueeScrollInterval };
            _marqueeScroll.Tick += (_, _) => ScrollMarquee();
            _marqueeScroll.Start();
        }
        else if (!scrolls) StopMarqueeScroll();
    }

    /// <summary>
    /// As ExplorerFrame does on every move: only the rows it now covers or no longer covers change, selected and
    /// deselected (with Ctrl down, both flipped).
    /// </summary>
    void SelectCovered(Marquee marquee, int first, int last)
    {
        int lo = marquee.First < 0 ? first : first < 0 ? marquee.First : Math.Min(first, marquee.First);
        int hi = marquee.Last < 0 ? last : last < 0 ? marquee.Last : Math.Max(last, marquee.Last);
        bool flip = (marquee.Keys & MK_CONTROL) != 0;
        for (int i = Math.Max(0, lo); lo >= 0 && i <= hi && i < VirtualListSize; i++)
        {
            bool covered = first >= 0 && i >= first && i <= last;
            bool was = marquee.First >= 0 && i >= marquee.First && i <= marquee.Last;
            if (covered == was) continue;
            SetSelected(i, flip ? !IsItemSelected(i) : covered);
        }
        if (last >= 0)
        {
            // The focus goes with the pointer's end of it, as in Explorer; Shift+click and Shift+arrows extend from
            // the end it started at.
            bool down = marquee.Anchor.Y <= ToRows(marquee.Pointer).Y;
            SetFocusedIndex(down ? last : first);
            SetSelectionMark(down ? first : last);
        }
        marquee.First = first;
        marquee.Last = last;
    }

    /// <summary>What a click on a row does (the list's own, done here for a press it didn't get): Ctrl flips, Shift extends.</summary>
    void ClickRow(int index, int keys)
    {
        if (index < 0 || index >= VirtualListSize) return;
        bool ctrl = (keys & MK_CONTROL) != 0;
        if ((keys & MK_SHIFT) != 0)
        {
            int mark = (int)Native.SendMessage(Handle, LVM_GETSELECTIONMARK, IntPtr.Zero, IntPtr.Zero);
            if (mark < 0 || mark >= VirtualListSize) mark = index;
            if (!ctrl) SetAllSelected(false);
            for (int i = Math.Min(mark, index); i <= Math.Max(mark, index); i++) SetSelected(i, true);
            SetFocusedIndex(index);
            SetSelectionMark(mark);
            return;
        }
        if (!ctrl) SetAllSelected(false);
        SetSelected(index, !ctrl || !IsItemSelected(index));
        SetFocusedIndex(index);
        SetSelectionMark(index);
    }

    void SetSelectionMark(int index) => Native.SendMessage(Handle, LVM_SETSELECTIONMARK, IntPtr.Zero, (IntPtr)index);

    /// <summary>The results are a different list now (a new search, a new order): a rectangle being drawn ends.</summary>
    public void CancelMarquee() => EndMarquee();

    /// <summary>
    /// The results are about to be refreshed while a rectangle is held: the rows it changed go back as they were, so
    /// the selection the refresh keeps is the one from before it; RowsReplaced then applies it again.
    /// </summary>
    public void RowsReplacing()
    {
        if (_marquee is not { Shown: true } marquee) return; // a press not yet a rectangle has changed nothing
        bool flip = (marquee.Keys & MK_CONTROL) != 0;
        for (int i = Math.Max(0, marquee.First); marquee.First >= 0 && i <= marquee.Last && i < VirtualListSize; i++)
            SetSelected(i, flip && !IsItemSelected(i));
    }

    /// <summary>
    /// The same results again, refreshed while a rectangle is held (files changed, a folder still loading): what it
    /// covers is selected again over the rows as they are now.
    /// </summary>
    public void RowsReplaced()
    {
        if (_marquee is not { } marquee) return;
        if (!marquee.Shown)
        {
            // Refreshed while pressed (results arrive as files change): the press is on the row in its place now, as a
            // press on the list itself would be; past the last row, it was pressed in empty space.
            if (marquee.Row >= VirtualListSize) marquee.Row = -1;
            return;
        }
        marquee.First = marquee.Last = -1;
        marquee.Row = -1;
        if ((marquee.PressKeys & (MK_CONTROL | MK_SHIFT)) == 0) SetAllSelected(false);
        TrackMarquee(marquee.Pointer);
    }

    /// <summary>
    /// How far past the rows' edges the pointer is, as ExplorerFrame measures it (from a pixel inside each edge): the
    /// rows scroll by that much each tick, so the further out, the faster.
    /// </summary>
    Point ScrollNeeded(Marquee marquee)
    {
        if (!marquee.Shown) return Point.Empty;
        var area = RowsArea;
        int x = marquee.Pointer.X - area.Left, y = marquee.Pointer.Y - area.Top;
        int dx = x < 1 ? x - 1 : x >= area.Width - 1 ? x - (area.Width - 1) + 1 : 0;
        int dy = y < 1 ? y - 1 : y >= area.Height - 1 ? y - (area.Height - 1) + 1 : 0;
        return new Point(dx, dy);
    }

    /// <summary>Past an edge while held: the rows scroll (a whole row at a time, the rest kept for the next tick) and it follows.</summary>
    void ScrollMarquee()
    {
        if (_marquee is not { } marquee) { StopMarqueeScroll(); return; }
        var need = ScrollNeeded(marquee);
        if (need == Point.Empty) { StopMarqueeScroll(); return; }
        int pitch = Pitch;
        marquee.ScrollRest += need.Y;
        int dy = marquee.ScrollRest / pitch * pitch;
        marquee.ScrollRest -= dy;
        // Sideways ExplorerFrame scrolls in steps of 100 pixels.
        int dx = need.X == 0 ? 0 : Math.Sign(need.X) * ((Math.Abs(need.X) + MarqueeScrollSideways - 1) / MarqueeScrollSideways * MarqueeScrollSideways);
        if (dx != 0 || dy != 0) Native.SendMessage(Handle, LVM_SCROLL, (IntPtr)dx, (IntPtr)dy);
        TrackMarquee(marquee.Pointer);
    }

    void StopMarqueeScroll()
    {
        _marqueeScroll?.Dispose();
        _marqueeScroll = null;
    }

    /// <summary>
    /// The button released: a press that never became a rectangle is a click, as in Explorer (on a row, that row is
    /// clicked; in empty space the selection goes, unless Ctrl or Shift was down). It goes by the keys held when it
    /// was pressed: Ctrl let go a moment before the button, with the pointer moving a pixel in between, made a
    /// Ctrl+click a plain click that dropped the rest of the selection.
    /// </summary>
    void ReleaseMarquee()
    {
        var marquee = _marquee;
        EndMarquee();
        if (marquee is null || marquee.Shown) return;
        if (marquee.Row >= 0) ClickRow(marquee.Row, marquee.PressKeys);
        else if ((marquee.PressKeys & (MK_CONTROL | MK_SHIFT)) == 0) { SetAllSelected(false); SetSelectionMark(-1); }
    }

    void EndMarquee()
    {
        var marquee = _marquee;
        if (marquee == null) return;
        _marquee = null; // before the capture goes: WM_CAPTURECHANGED comes back here
        StopMarqueeScroll();
        if (Capture) Capture = false;
        _layer?.Hide();
    }

    /// <summary>
    /// Shows it in its own layer above the rows, where they are now (its start scrolls with them), cut off at their
    /// edges; the rows themselves are never repainted for it.
    /// </summary>
    void ShowMarquee(Marquee marquee)
    {
        var a = ToClient(marquee.Anchor);
        var r = Rectangle.FromLTRB(Math.Min(a.X, marquee.Pointer.X), Math.Min(a.Y, marquee.Pointer.Y), Math.Max(a.X, marquee.Pointer.X) + 1, Math.Max(a.Y, marquee.Pointer.Y) + 1);
        (_layer ??= new MarqueeLayer(this)).Show(r, RowsArea, MarqueeBorder, MarqueeFill);
    }

    IntPtr _headerImages;
    int _headerImageWidth;

    /// <summary>
    /// The leftmost column's title starts after the rows' margin, as in Explorer: the header shows a blank image that
    /// wide before it (the dark header, drawn by the window, leaves the same room).
    /// </summary>
    public void IndentHeader()
    {
        IntPtr header = HeaderHandle;
        if (header == IntPtr.Zero || Columns.Count == 0) return;
        if (_rowMargin > 0 && (_headerImages == IntPtr.Zero || _headerImageWidth != _rowMargin))
        {
            if (_headerImages != IntPtr.Zero) ImageList_Destroy(_headerImages);
            // An image that draws nothing: every pixel black at an alpha of 1 in 255. (All zero, the list takes it as
            // having no alpha and draws a black dash; a masked one drew its mask colour.)
            _headerImages = ImageList_Create(_rowMargin, 1, ILC_COLOR32, 1, 0);
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = _rowMargin, biHeight = -1, biPlanes = 1, biBitCount = 32 };
            IntPtr hbm = CreateDIBSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hbm != IntPtr.Zero)
            {
                for (int i = 0; i < _rowMargin; i++) Marshal.WriteInt32(bits, i * 4, 0x01000000);
                ImageList_Add(_headerImages, hbm, IntPtr.Zero);
                DeleteObject(hbm);
            }
            _headerImageWidth = _rowMargin;
            Native.SendMessage(header, HDM_SETIMAGELIST, IntPtr.Zero, _headerImages);
        }
        int leftmost = Columns.Cast<ColumnHeader>().OrderBy(c => c.DisplayIndex).First().Index;
        for (int i = 0; i < Columns.Count; i++)
        {
            var item = new Native.HDITEM { mask = Native.HDI_FORMAT | HDI_IMAGE };
            Native.SendMessage(header, Native.HDM_GETITEMW, (IntPtr)i, ref item);
            bool indent = i == leftmost && _rowMargin > 0;
            int fmt = indent ? item.fmt | HDF_IMAGE : item.fmt & ~HDF_IMAGE;
            if (fmt == item.fmt && (!indent || item.iImage == 0)) continue;
            item.fmt = fmt;
            item.iImage = 0;
            Native.SendMessage(header, Native.HDM_SETITEMW, (IntPtr)i, ref item);
        }
    }

    bool _rightDownInEmpty;
    Point _rightDownAt;

    /// <summary>
    /// While a rectangle is held, keys don't reach the window (Esc there would close the search or the window) or move
    /// the list under it: Esc ends the rectangle, the rest wait for the release.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_marquee == null) return base.ProcessCmdKey(ref msg, keyData);
        if ((keyData & Keys.KeyCode) == Keys.Escape) EndMarquee();
        return true;
    }

    protected override void OnColumnReordered(ColumnReorderedEventArgs e)
    {
        base.OnColumnReordered(e);
        if (!e.Cancel && IsHandleCreated) BeginInvoke(IndentHeader); // the order changes after this event
    }

    protected override void WndProc(ref Message m)
    {
        if (ShellContextMenu.HandleMenuMessage(ref m)) return;
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN when _marquee == null && StartsMarquee(PointOf(m.LParam), out int pressedRow):
                BeginMarquee(PointOf(m.LParam), (int)(long)m.WParam & (MK_CONTROL | MK_SHIFT), pressedRow);
                return;
            case WM_LBUTTONDBLCLK when _marquee == null && IsEmptySpace(PointOf(m.LParam)):
                return; // nothing there to open (not the row beside the gap)
            case WM_RBUTTONDOWN when _marquee == null && IsEmptySpace(PointOf(m.LParam)):
                // As in Explorer: the selection goes (not with Ctrl) and the release shows the background menu.
                Focus();
                if (((long)m.WParam & MK_CONTROL) == 0) { SetAllSelected(false); SetSelectionMark(-1); }
                _rightDownInEmpty = true;
                _rightDownAt = PointOf(m.LParam);
                Capture = true; // its release comes back here, wherever it is (as the list's own press does)
                return;
            case WM_RBUTTONUP when _rightDownInEmpty:
                _rightDownInEmpty = false;
                Capture = false;
                ItemContextMenu?.Invoke(PointToScreen(_rightDownAt)); // where it was pressed, off the rows: the background menu
                return;
            case WM_CAPTURECHANGED when _rightDownInEmpty:
                _rightDownInEmpty = false; // the mouse went elsewhere first: no menu for that press
                break;
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL or WM_VSCROLL or WM_HSCROLL when _marquee is { Shown: true } held:
                // The rows scroll under it: it follows its start, and covers the rows under it now.
                base.WndProc(ref m);
                TrackMarquee(held.Pointer);
                return;
            case WM_MOUSEMOVE when _marquee is { } moved: // the list has the mouse until the release, wherever it is
                moved.Keys = (int)(long)m.WParam & (MK_CONTROL | MK_SHIFT); // Ctrl down or up changes what it does next
                TrackMarquee(PointOf(m.LParam));
                return;
            case WM_LBUTTONUP when _marquee != null:
                ReleaseMarquee();
                return;
            case WM_CAPTURECHANGED when _marquee != null:
                EndMarquee();
                break;
        }
        if (m.Msg == Native.WM_CONTEXTMENU)
        {
            long lp = m.LParam.ToInt64();
            Point pt = lp == -1 ? new Point(-1, -1) : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (m.WParam == HeaderHandle && HeaderHandle != IntPtr.Zero) HeaderContextMenu?.Invoke(pt);
            else ItemContextMenu?.Invoke(pt);
            return;
        }
        if (m.Msg == 0x000F /* WM_PAINT */ && _emptyText.Length > 0 && VirtualListSize == 0 && GetUpdateRect(Handle, out var dirty, false))
        {
            base.WndProc(ref m);
            DrawEmptyText(Rectangle.FromLTRB(dirty.Left, dirty.Top, dirty.Right, dirty.Bottom));
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }
}
