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
        AttachSystemImageList();
    }

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
