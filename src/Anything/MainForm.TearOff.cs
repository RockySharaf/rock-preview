using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything;

/// <summary>
/// Tabs dragged out of a window and into another, as in Chrome. A tab dragged out of the strip (a little past it,
/// <see cref="TabTearOff.DetachMargin"/>) opens as a window of its own under the pointer, which follows it until the
/// button goes up; a window's only tab takes its window along. Over another Rock Preview window's strip the tab joins
/// that window's tabs instead, still held, to be dragged on along that strip or out of it again. The tab moves as it is,
/// its Explorer view and all: its folder, Back and Forward, selection and scroll position, its search and results.
/// Nothing loads again. Right-click a tab for Move tab to new window to do the same without dragging.
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>The tab was dragged out of this window's strip: into a window of its own under the pointer (this one, when it is the only tab).</summary>
    void TearOff(FolderTab t, Point pointer, PointF hold)
    {
        int index = _tabs.IndexOf(t);
        if (index < 0) return;
        if (StripUnder(pointer) is { } target) { MoveInto(t, target, pointer, hold); return; } // straight onto another window's tabs
        MainForm window = this;
        if (_tabs.Count == 1)
        {
            // Windows' own way when a maximized window is dragged off: back to its size, under the pointer.
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        }
        else
        {
            // Roughly where it goes, as large as this window: the drag then puts the tab exactly under the pointer.
            var size = (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds).Size;
            var client = PointToScreen(Point.Empty);
            var clientInWindow = new Size(client.X - Left, client.Y - Top - (IsZoomed(Handle) ? FrameThickness() : 0));
            var first = _tabStrip.TabBoundsOnScreen(0);
            var tab = _tabStrip.TabBoundsOnScreen(index);
            var held = TabTearOff.HeldPoint(new Rectangle(first.X - client.X, first.Y - client.Y, tab.Width, tab.Height), hold) + clientInWindow;
            window = MoveToNewWindow(t, new Rectangle(TabTearOff.FollowPointer(Point.Empty, held, pointer), size));
            _app.ShowWindow(window);
        }
        var drag = new TearOffDrag(t, window, hold);
        TabStrip.Drag = drag;
        drag.Move(pointer);
        if (window != this && TabStrip.Drag == drag) window._tabStrip.Capture = true; // the new window's strip has the pointer from here
    }

    /// <summary>A tab menu's Move tab to new window: the tab as it is, in a new window where new windows open.</summary>
    void MoveTabToNewWindow(FolderTab t)
    {
        if (_tabs.Count < 2 || !_tabs.Contains(t)) return;
        _app.ShowWindow(MoveToNewWindow(t, null));
    }

    /// <summary>Moves a tab, as it is, out of this window into a new one, at <paramref name="bounds"/> (null: where a new window opens). Not shown yet.</summary>
    MainForm MoveToNewWindow(FolderTab t, Rectangle? bounds)
    {
        ReleaseTab(t);
        var w = _app.CreateWindow(this, t);
        if (bounds is { } b) w.PlaceAt(b);
        return w;
    }

    /// <summary>Before the window shows: where it opens, not maximized.</summary>
    void PlaceAt(Rectangle bounds)
    {
        _initialBounds = bounds;
        _initialMaximized = false;
        StartPosition = FormStartPosition.Manual;
    }

    /// <summary>
    /// Lets go of a tab that moves to another window, as closing it would (the next tab shows), except that its view,
    /// results and listing go with it: the view waits, hidden, for its new window. A window left with no tab closes
    /// (<see cref="CloseEmptied"/>) once the tab is in its new window.
    /// </summary>
    void ReleaseTab(FolderTab t)
    {
        int i = _tabs.IndexOf(t);
        if (i < 0) return;
        if (t == _tab)
        {
            if (_tabs.Count > 1) SelectTab(_tabs[i + 1 < _tabs.Count ? i + 1 : i - 1]);
            else LeaveTab(t);
        }
        _tabs.RemoveAt(i);
        _split.Panel1.Controls.Remove(t.Explorer);
        _tabStrip.SelectedIndex = _tabs.IndexOf(_tab);
        _tabStrip.TabsChanged();
    }

    /// <summary>
    /// The held tab joins <paramref name="target"/>'s tabs where the pointer is, and is dragged on there; this window
    /// closes if that was its last tab.
    /// </summary>
    void MoveInto(FolderTab t, MainForm target, Point pointer, PointF hold)
    {
        int slot = target._tabStrip.SlotForNewTab(pointer);
        ReleaseTab(t);
        target.AdoptTab(t, this, slot);
        target.Activate();
        target._tabStrip.ContinueDrag(target._tabs.IndexOf(t), hold); // the pointer goes with it
        if (_tabs.Count == 0) CloseEmptied();
        target.FocusTabContent();
    }

    /// <summary>Takes in a tab from another window at <paramref name="index"/>, and shows it.</summary>
    void AdoptTab(FolderTab t, MainForm from, int index)
    {
        TakeContentState(t, from);
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), t);
        AttachTab(t);
        SelectTab(t);
        _tabStrip.TabsChanged();
    }

    /// <summary>The window's last tab went to another window: it closes, keeping nothing of its own (SaveStateTo).</summary>
    void CloseEmptied()
    {
        ForceClose = true;
        Close();
    }

    Rectangle StripBounds() => _tabStrip.RectangleToScreen(_tabStrip.ClientRectangle);

    /// <summary>
    /// The other Rock Preview window whose tab strip is under the pointer, on top there, which a tab dragged out of this
    /// window joins. Null anywhere else, or where another program's window covers the strip.
    /// </summary>
    MainForm? StripUnder(Point pointer)
    {
        var under = _app.Windows.Where(w => w != this && w._loaded && w.IsHandleCreated && w.Visible
            && w.WindowState != FormWindowState.Minimized && w.StripBounds().Contains(pointer)).ToList();
        if (under.Count == 0) return null; // the z-order is only looked at when it matters
        var layers = new List<TabTearOff.Layer>();
        var forms = new List<MainForm?>();
        IntPtr dragged = Handle;
        EnumWindows((hwnd, _) =>
        {
            var form = under.FirstOrDefault(w => w.Handle == hwnd);
            bool ignored = hwnd == dragged || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)
                || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
            layers.Add(new TabTearOff.Layer(ignored ? Rectangle.Empty : VisibleBounds(hwnd), form?.StripBounds(), ignored));
            forms.Add(form);
            return true;
        }, IntPtr.Zero);
        int i = TabTearOff.StripUnder(layers, pointer);
        return i >= 0 ? forms[i] : null;
    }

    /// <summary>A tab out of every strip, in a window of its own that follows the pointer, until the button goes up.</summary>
    sealed class TearOffDrag(FolderTab tab, MainForm window, PointF hold) : ITabDrag
    {
        public void Move(Point pointer)
        {
            int index = window.IsDisposed ? -1 : window._tabs.IndexOf(tab);
            if (index < 0) { End(); return; }
            if (window.StripUnder(pointer) is { } target)
            {
                // Over another window's strip: the tab joins its tabs there, still held, and its own window goes.
                End();
                window.MoveInto(tab, target, pointer, hold);
                return;
            }
            var held = TabTearOff.HeldPoint(window._tabStrip.TabBoundsOnScreen(index), hold);
            var to = TabTearOff.FollowPointer(window.Location, held, pointer);
            if (to != window.Location) window.Location = to;
        }

        public void Drop(Point pointer)
        {
            End();
            if (window.IsDisposed || !window._tabs.Contains(tab)) return;
            window.Activate();
            window.FocusTabContent();
        }

        public void CaptureLost(TabStrip strip)
        {
            // Passed on to the tab's window's strip (from the strip it was dragged out of), the drag goes on. Lost to
            // anything else (another program, a dialog), it ends where the window is.
            if (strip == window._tabStrip) End();
        }

        void End()
        {
            if (TabStrip.Drag == this) TabStrip.Drag = null;
        }
    }

    // ---- windows under the pointer ----------------------------------------------------------------

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct WinRect { public int Left, Top, Right, Bottom; }

    /// <summary>What shows of a window on screen (without the invisible resize borders), or its whole rectangle.</summary>
    static Rectangle VisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out WinRect r, Marshal.SizeOf<WinRect>()) != 0 && !GetWindowRect(hwnd, out r))
            return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Kept from view by Windows (on another desktop, or an app's window that isn't showing).</summary>
    static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out WinRect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
