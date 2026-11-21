using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything.Explorer;

/// <summary>
/// "Open in new tab" at the top of a folder's menu in the view, as in File Explorer: the folder opens in a tab behind
/// the one showing. The view builds and runs that menu itself, so the item is put in as the menu opens and its choice is
/// caught on its way to the menu (the view never hears of it). The shell's own "Open in new tab", which would open
/// File Explorer, is taken out so the menu doesn't have it twice.
/// </summary>
internal sealed partial class ExplorerHost
{
    /// <summary>"Open in new tab" was chosen on a folder in the view. Argument: its parsing name.</summary>
    public event Action<string>? FolderOpenInNewTab;

    void RaiseOpenInNewTab(string location)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { if (!IsDisposed) FolderOpenInNewTab?.Invoke(location); });
    }

    /// <summary>
    /// The parsing name a tab opens the selection at: one folder selected (a drive, a library, This PC too), not a file,
    /// a shortcut or a zip; otherwise null. From the attributes the view holds, so a folder on a slow share doesn't wait.
    /// </summary>
    string? SelectedTabLocation()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        try
        {
            if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return null;
            try
            {
                if (items.GetCount(out uint n) != ShellNative.S_OK || n != 1) return null;
                if (items.GetItemAt(0, out var item) != ShellNative.S_OK || item == null) return null;
                try
                {
                    if (item.GetAttributes(ShellSelection.SfgaoFolder | ShellSelection.SfgaoStream, out uint attrs) < 0 || !FolderTabs.CanOpenInTab(attrs)) return null;
                    return ShellNative.NameOf(item, SIGDN.DesktopAbsoluteParsing);
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>The words of the shell's own "Open in new tab" in this Windows' language (its verb's menu name), or null.</summary>
    static readonly Lazy<string?> ShellNewTabText = new(() =>
    {
        try
        {
            using var verb = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Folder\shell\" + FolderTabs.ShellNewTabVerb);
            if (verb?.GetValue("MUIVerb") is not string source || source.Length == 0) return null;
            if (!source.StartsWith('@')) return source;
            var text = new System.Text.StringBuilder(260);
            return SHLoadIndirectString(source, text, (uint)text.Capacity, IntPtr.Zero) == ShellNative.S_OK ? text.ToString() : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    });

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string pszSource, System.Text.StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

    sealed partial class ViewMenuHook
    {
        const int WM_MENUSELECT = 0x011F, WM_KEYDOWN = 0x0100, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, VK_RETURN = 0x0D;
        const int WH_MSGFILTER = -1, MSGF_MENU = 2;
        const uint MF_BYCOMMAND = 0x0, MF_BYPOSITION = 0x400, MF_POPUP = 0x10;
        const string NewTabText = "Open in new tab";

        /// <summary>The view the hooked window belongs to.</summary>
        public ExplorerHost? Host;

        IntPtr _menu, _filter;      // the menu with our item in it, while it is open; the hook watching its input
        uint _newTabId;
        string? _newTabLocation;
        bool _newTabHot, _newTabChosen;
        HookProc? _filterProc;      // kept alive while the hook is set

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// As the view's menu for one selected folder opens: "Open in new tab" goes at its top, and its input is watched
        /// until the menu closes. Any other menu (the background's, files, several items) is left alone.
        /// </summary>
        void AddOpenInNewTab(IntPtr menu)
        {
            EndOpenInNewTab(raise: false);
            if (Host == null || menu == IntPtr.Zero) return;
            try
            {
                if (Host.SelectedTabLocation() is not { } location) return;
                // A command id no item of the menu has, above the ranges the view gives its own (it never sees ours).
                uint id = 0xFFF0;
                while (id > 0xF000 && GetMenuState(menu, id, MF_BYCOMMAND) != uint.MaxValue) id--;
                if (id == 0xF000) return;
                RemoveShellNewTab(menu);
                if (!InsertMenu(menu, 0, MF_BYPOSITION, (UIntPtr)id, NewTabText)) return;
                _filterProc ??= OnMenuInput;
                _filter = SetWindowsHookEx(WH_MSGFILTER, _filterProc, IntPtr.Zero, GetCurrentThreadId());
                if (_filter == IntPtr.Zero) { DeleteMenu(menu, id, MF_BYCOMMAND); return; } // it couldn't be caught: not offered
                _menu = menu;
                _newTabId = id;
                _newTabLocation = location;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { EndOpenInNewTab(raise: false); }
        }

        /// <summary>Takes out the shell's own "Open in new tab", if it put one in: it would open File Explorer, not a tab here.</summary>
        static void RemoveShellNewTab(IntPtr menu)
        {
            if (ShellNewTabText.Value is not { } shell) return;
            var text = new System.Text.StringBuilder(260);
            for (int i = GetMenuItemCount(menu) - 1; i >= 0; i--)
            {
                text.Clear();
                if (GetMenuString(menu, (uint)i, text, text.Capacity, MF_BYPOSITION) <= 0) continue;
                if (!FolderTabs.SameMenuText(text.ToString(), shell)) continue;
                DeleteMenu(menu, (uint)i, MF_BYPOSITION);
                return;
            }
        }

        /// <summary>WM_MENUSELECT: whether our item is the one highlighted (for Enter).</summary>
        void TrackOpenInNewTab(IntPtr wParam, IntPtr lParam)
        {
            if (_menu == IntPtr.Zero) return;
            uint w = (uint)((ulong)wParam & 0xFFFF_FFFF), item = w & 0xFFFF, flags = w >> 16;
            _newTabHot = lParam == _menu && flags != 0xFFFF && (flags & MF_POPUP) == 0 && item == _newTabId;
        }

        /// <summary>
        /// The menu's input, before the menu acts on it: a click on our item (where the button comes up) or Enter while
        /// it is highlighted is taken here and the menu closed, so the view gets no command and runs nothing.
        /// </summary>
        IntPtr OnMenuInput(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code == MSGF_MENU && _menu != IntPtr.Zero && !_newTabChosen && lParam != IntPtr.Zero)
                {
                    var msg = Marshal.PtrToStructure<MSG>(lParam);
                    bool chosen = msg.message switch
                    {
                        WM_LBUTTONUP or WM_RBUTTONUP => MenuItemFromPoint(IntPtr.Zero, _menu, msg.pt) is int at and >= 0 && GetMenuItemID(_menu, at) == _newTabId,
                        WM_KEYDOWN => _newTabHot && (int)((ulong)msg.wParam & 0xFFFF) == VK_RETURN,
                        _ => false,
                    };
                    if (chosen)
                    {
                        _newTabChosen = true;
                        EndMenu();
                        return 1; // the menu never sees it
                    }
                }
            }
            catch (Exception) { }
            return CallNextHookEx(_filter, code, wParam, lParam);
        }

        /// <summary>
        /// The menu closed: the hook goes, and the folder opens in a new tab if our item was chosen. Also called before
        /// the view window is let go (released or swapped), so the hook never outlives this object.
        /// </summary>
        internal void EndOpenInNewTab(bool raise)
        {
            if (_filter != IntPtr.Zero) { UnhookWindowsHookEx(_filter); _filter = IntPtr.Zero; }
            string? location = _newTabChosen ? _newTabLocation : null;
            _menu = IntPtr.Zero;
            _newTabId = 0;
            _newTabLocation = null;
            _newTabHot = _newTabChosen = false;
            if (raise && location != null) Host?.RaiseOpenInNewTab(location);
        }

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool EndMenu();
        [DllImport("user32.dll")] static extern int MenuItemFromPoint(IntPtr hWnd, IntPtr hMenu, POINT ptScreen);
        [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
        [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")] static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);
    }
}
