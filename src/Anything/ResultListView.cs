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
        base.OnHandleDestroyed(e);
        if (_rowList != IntPtr.Zero) { ImageList_Destroy(_rowList); _rowList = IntPtr.Zero; }
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
    [DllImport("comctl32.dll")] static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int initial, int grow);
    [DllImport("comctl32.dll")] static extern bool ImageList_Destroy(IntPtr himl);

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

    protected override void WndProc(ref Message m)
    {
        if (ShellContextMenu.HandleMenuMessage(ref m)) return;
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
