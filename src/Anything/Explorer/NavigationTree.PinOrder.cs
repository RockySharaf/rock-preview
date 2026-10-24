using System.Runtime.InteropServices;

namespace Anything.Explorer;

/// <summary>
/// Moving a pinned Quick access folder up or down by dragging it, as in File Explorer. The tree reorders only for the
/// host Explorer gives it (ExplorerFrame's ordering callback is private), so the pane does it itself: a drag that starts
/// on a pinned folder shows Explorer's insert line between the pinned folders, and the drop pins them again in the new
/// order (<see cref="QuickAccessPins"/>), which File Explorer then shows too. Dragged out of the pinned folders, it
/// becomes the tree's own drag again (to copy the folder somewhere, say); a click stays a click.
/// </summary>
internal sealed partial class NavigationTree
{
    const int WM_KEYDOWN = 0x100, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_CAPTURECHANGED = 0x215;
    const int TVM_GETITEMRECT = 0x1104, TVM_HITTEST = 0x1111, TVM_SETINSERTMARK = 0x111A, TVM_SETINSERTMARKCOLOR = 0x1125;
    const int TVHT_ONITEMICON = 0x2, TVHT_ONITEMLABEL = 0x4, TVHT_ONITEMINDENT = 0x8, TVHT_ONITEMRIGHT = 0x20, TVHT_ONITEMSTATEICON = 0x40;

    /// <summary>A pinned folder being dragged: the pinned rows in order, which one, and the gap it would drop into.</summary>
    sealed class PinDrag(List<IntPtr> rows, List<string> paths, int from, Message down)
    {
        public readonly List<IntPtr> Rows = rows;
        public readonly List<string> Paths = paths;
        public readonly int From = from;
        public readonly Message Down = down;
        public int Gap = -1;
    }

    PinDrag? _pinDrag;
    bool _passingDown;

    /// <summary>The tree's mouse and key messages while a pinned folder may be dragged; true when handled here.</summary>
    bool HandlePinDragMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN when _pinDrag == null && !_passingDown:
                return BeginPinDrag(m);
            case WM_MOUSEMOVE when _pinDrag != null:
                if (((long)m.WParam & 0x1 /* MK_LBUTTON */) == 0) { EndPinDrag(drop: false); return true; } // its release went elsewhere
                TrackPinDrag(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                return true;
            case WM_LBUTTONUP when _pinDrag != null:
                EndPinDrag(drop: true);
                return true;
            case WM_KEYDOWN when _pinDrag != null && (int)m.WParam == 0x1B: // Esc
                EndPinDrag(drop: false);
                return true;
            case WM_CAPTURECHANGED when _pinDrag != null && m.LParam != _hwndFocus:
                EndPinDrag(drop: false);
                return false;
        }
        return false;
    }

    /// <summary>
    /// A press on a pinned folder's row: Windows decides whether it is a click or a drag. A click goes on to the tree
    /// (with its button release); a drag of a pinned folder is reordered here, any other drag is the tree's.
    /// </summary>
    bool BeginPinDrag(Message m)
    {
        if (_hwndFocus == IntPtr.Zero) return false;
        var pt = new POINT { X = unchecked((short)(long)m.LParam), Y = unchecked((short)((long)m.LParam >> 16)) };
        var hit = new TVHITTESTINFO { pt = pt };
        IntPtr item = SendMessage(_hwndFocus, TVM_HITTEST, IntPtr.Zero, ref hit);
        const int onRow = TVHT_ONITEMICON | TVHT_ONITEMLABEL | TVHT_ONITEMINDENT | TVHT_ONITEMRIGHT | TVHT_ONITEMSTATEICON;
        if (item == IntPtr.Zero || (hit.flags & onRow) == 0) return false;
        IntPtr parent = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)3 /* TVGN_PARENT */, item);
        if (parent == IntPtr.Zero || !IsFlatRow(parent)) return false; // not one of Quick access's folders

        var screen = pt;
        ClientToScreen(_hwndFocus, ref screen);
        if (!DragDetect(_hwndFocus, screen))
        {
            // A click: the tree gets its press now and the release Windows took while it watched.
            PostMessage(_hwndFocus, WM_LBUTTONUP, IntPtr.Zero, m.LParam);
            return false;
        }

        // Which rows are the pinned folders: the first ones under Quick access, in Windows' order (frequent folders follow).
        var pins = QuickAccessPins.Read();
        // Let go while the shell answered (outside the tree, maybe): no drag. The button as it is now, the left one as set up.
        if ((GetAsyncKeyState(GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01) & 0x8000) == 0) return true;
        if (pins == null) { PassDown(m); return true; }
        var rows = new List<IntPtr>();
        var text = Marshal.AllocHGlobal(522);
        try
        {
            for (IntPtr child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)4 /* TVGN_CHILD */, parent);
                 child != IntPtr.Zero && rows.Count < pins.Count;
                 child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)1 /* TVGN_NEXT */, child))
            {
                if (ReadRow(child, text).Name != pins[rows.Count].Name) break; // the tree isn't showing Windows' order yet
                rows.Add(child);
            }
        }
        finally { Marshal.FreeHGlobal(text); }
        int from = rows.IndexOf(item);
        if (from < 0 || rows.Count != pins.Count || rows.Count < 2)
        {
            PassDown(m); // a frequent folder (or the pins are changing): the tree's own drag
            return true;
        }

        _pinDrag = new PinDrag(rows, pins.ConvertAll(p => p.Path), from, m);
        SendMessage(_hwndFocus, TVM_SETINSERTMARKCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(ExplorerTheme.Text));
        SetCapture(_hwndFocus);
        GetCursorPos(out var now);
        ScreenToClient(_hwndFocus, ref now);
        TrackPinDrag(new Point(now.X, now.Y));
        return true;
    }

    /// <summary>The insert line at the gap nearest the pointer; well outside the pinned folders, the tree's own drag takes over.</summary>
    void TrackPinDrag(Point p)
    {
        var drag = _pinDrag!;
        int rowH = Math.Max(1, RowHeight());
        var first = RowRect(drag.Rows[0]);
        var last = RowRect(drag.Rows[^1]);
        GetClientRect(_hwndFocus, out var client);
        if (p.X < client.Left || p.X >= client.Right || p.Y < first.Top - rowH || p.Y > last.Bottom + rowH)
        {
            var down = drag.Down;
            EndPinDrag(drop: false);
            PassDown(down); // the button is still down and the pointer far away: the tree starts dragging the folder
            return;
        }
        int gap = drag.Rows.Count;
        for (int i = 0; i < drag.Rows.Count; i++)
        {
            var r = RowRect(drag.Rows[i]);
            if (p.Y < (r.Top + r.Bottom) / 2) { gap = i; break; }
        }
        if (gap == drag.Gap) return;
        drag.Gap = gap;
        bool after = gap == drag.Rows.Count;
        SendMessage(_hwndFocus, TVM_SETINSERTMARK, (IntPtr)(after ? 1 : 0), drag.Rows[after ? gap - 1 : gap]);
    }

    void EndPinDrag(bool drop)
    {
        var drag = _pinDrag;
        if (drag == null) return;
        _pinDrag = null; // before ReleaseCapture, whose WM_CAPTURECHANGED comes back here
        SendMessage(_hwndFocus, TVM_SETINSERTMARK, IntPtr.Zero, IntPtr.Zero);
        if (GetCapture() == _hwndFocus) ReleaseCapture();
        if (!drop || drag.Gap < 0) return;
        int to = drag.Gap > drag.From ? drag.Gap - 1 : drag.Gap;
        if (to == drag.From) return;
        var paths = drag.Paths;
        int from = drag.From;
        // Pinning goes through the shell (and its extensions): off the window's thread, on one of its own.
        var thread = new Thread(() =>
        {
            bool moved = QuickAccessPins.Move(paths, from, to);
            try { if (moved && IsHandleCreated) BeginInvoke(QueuePinCheck); } catch (InvalidOperationException) { }
        }) { IsBackground = false, Name = "Quick access order" }; // closing the app waits for it: no pin is left unpinned
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The tree follows Windows' change notices; should it still show the old order a moment later, it is rebuilt.</summary>
    void QueuePinCheck()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (!IsHandleCreated || _hwndFocus == IntPtr.Zero) return;
            if (QuickAccessPins.Read() is not { } pins) return;
            var names = new List<string>();
            var text = Marshal.AllocHGlobal(522);
            try
            {
                for (IntPtr row = SendMessage(_hwndFocus, TVM_GETNEXTITEM, IntPtr.Zero /* TVGN_ROOT */, IntPtr.Zero); row != IntPtr.Zero;
                     row = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)1 /* TVGN_NEXT */, row))
                {
                    if (!ReadRow(row, text).Flat) continue;
                    for (IntPtr child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)4 /* TVGN_CHILD */, row);
                         child != IntPtr.Zero && names.Count < pins.Count;
                         child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)1 /* TVGN_NEXT */, child))
                        names.Add(ReadRow(child, text).Name);
                    break;
                }
            }
            finally { Marshal.FreeHGlobal(text); }
            if (!names.SequenceEqual(pins.Select(p => p.Name))) RefreshRoots();
        };
        timer.Start();
    }

    /// <summary>Gives the tree the press it was held back from (a drag that isn't a reorder).</summary>
    void PassDown(Message down)
    {
        if (_hook == null) return;
        _passingDown = true;
        try { _hook.Pass(ref down); }
        finally { _passingDown = false; }
    }

    bool IsFlatRow(IntPtr item)
    {
        var text = Marshal.AllocHGlobal(522);
        try { return ReadRow(item, text).Flat; }
        finally { Marshal.FreeHGlobal(text); }
    }

    RECT RowRect(IntPtr item)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.WriteIntPtr(buffer, item); // in: the item; out: its whole row
            SendMessage(_hwndFocus, TVM_GETITEMRECT, IntPtr.Zero, buffer);
            return Marshal.PtrToStructure<RECT>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TVHITTESTINFO { public POINT pt; public int flags; public IntPtr hItem; }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVHITTESTINFO lParam);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DragDetect(IntPtr hwnd, POINT pt);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetCapture();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}

/// <summary>
/// Quick access's pinned folders through the shell, the way File Explorer keeps them: in pinned order, and moved by
/// unpinning the folders from the first place that changes and pinning them again in the new order (Windows always
/// pins at the end). Each step waits for Windows to have done it, so the order comes out right.
/// </summary>
internal static class QuickAccessPins
{
    const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    /// <summary>The pinned folders (name, parsing path) in Windows' order; null when the shell can't say.</summary>
    public static List<(string Name, string Path)>? Read()
    {
        var pins = new List<(string Name, string Path)>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
            dynamic? folder = shell.Namespace(QuickAccess);
            if (folder == null) return null;
            foreach (dynamic item in folder.Items())
            {
                if (!(bool)item.IsFolder) continue;
                object pinned = item.ExtendedProperty("System.Home.IsPinned");
                if (pinned is true) pins.Add(((string)item.Name, (string)item.Path));
            }
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { return null; }
        return pins;
    }

    /// <summary>Moves the pinned folder at <paramref name="from"/> to <paramref name="to"/>; false when nothing (or not all of it) happened.</summary>
    public static bool Move(List<string> paths, int from, int to)
    {
        var order = new List<string>(paths);
        string moving = order[from];
        order.RemoveAt(from);
        order.Insert(to, moving);
        int start = Math.Min(from, to);
        dynamic? shellApp = null;
        try
        {
            dynamic shell = shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
            var items = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.Skip(start))
            {
                dynamic? folder = shell.Namespace(path);
                if (folder == null) return false; // every folder must be there to pin again before any is unpinned
                items[path] = folder.Self;
            }
            var unpinned = new List<string>();
            foreach (string path in paths.Skip(start))
            {
                items[path].InvokeVerb("unpinfromhome");
                if (WaitUntil(pinned => !pinned.Contains(path))) unpinned.Add(path);
            }
            bool all = unpinned.Count == paths.Count - start;
            // Pinned again in the new order: those that went (one that wouldn't unpin keeps its place).
            foreach (string path in order.Skip(start).Where(p => unpinned.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                items[path].InvokeVerb("pintohome");
                if (!WaitUntil(pinned => pinned.Contains(path)))
                {
                    items[path].InvokeVerb("pintohome"); // once more: a folder must not end up unpinned
                    all &= WaitUntil(pinned => pinned.Contains(path));
                }
            }
            return all;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { return false; }
        finally { PinMissing(shellApp, paths); }
    }

    /// <summary>Whatever went wrong on the way (a folder gone offline, a slow shell), every folder that was pinned ends up pinned.</summary>
    static void PinMissing(dynamic? shell, List<string> paths)
    {
        if (shell == null) return;
        if (Read() is not { } now) return;
        var pinned = new HashSet<string>(now.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Where(p => !pinned.Contains(p)))
        {
            try
            {
                dynamic? folder = shell.Namespace(path);
                if (folder == null) continue;
                folder.Self.InvokeVerb("pintohome");
                WaitUntil(p => p.Contains(path));
            }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
        }
    }

    /// <summary>Polls the pinned paths until <paramref name="done"/> holds, for up to two seconds.</summary>
    static bool WaitUntil(Func<HashSet<string>, bool> done)
    {
        for (int i = 0; i < 40; i++)
        {
            // The shell not answering says nothing either way: only an answer counts.
            if (Read() is { } now && done(new HashSet<string>(now.Select(p => p.Path), StringComparer.OrdinalIgnoreCase))) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
