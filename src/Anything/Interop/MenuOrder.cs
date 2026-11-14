using System.Runtime.InteropServices;

namespace Anything.Interop;

/// <summary>
/// Moves a shell context menu's "Open with" up beside "Open", where it is wanted most: in the results' menu and in a
/// folder's own (File Explorer's view). The item keeps its command id and its submenu, so choosing it works as before.
/// </summary>
internal static class MenuOrder
{
    const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_DATA = 0x20, MIIM_STRING = 0x40, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
    const uint MF_BYPOSITION = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool InsertMenuItem(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")] static extern bool RemoveMenu(IntPtr hMenu, uint position, uint flags);

    /// <summary>Whether a menu item's text is "Open with" (with or without its access key, "..." or a submenu).</summary>
    public static bool IsOpenWith(string? text)
    {
        if (text is null) return false;
        string t = text.Replace("&", "").Trim();
        int tab = t.IndexOf('\t');
        if (tab >= 0) t = t[..tab];
        return t.TrimEnd('.', '…', ' ').Equals("Open with", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves "Open with" to <paramref name="position"/> (counted before the move). <paramref name="isOpenWith"/> may
    /// also recognise it by command id (its "openas" verb) where the text is another language's. False when there's none.
    /// </summary>
    public static bool MoveOpenWith(IntPtr menu, int position, Func<uint, bool>? isOpenWith = null)
    {
        int count = GetMenuItemCount(menu);
        if (count <= 0) return false;
        for (int i = 0; i < count; i++)
        {
            var info = Read(menu, i, out string? text);
            if (info is not { } item) continue;
            bool match = IsOpenWith(text) || (item.hSubMenu == IntPtr.Zero && isOpenWith?.Invoke(item.wID) == true);
            if (!match) continue;
            if (i == position) return true;
            if (!RemoveMenu(menu, (uint)i, MF_BYPOSITION)) return false; // the submenu stays alive: it moves with the item
            int at = i < position ? position - 1 : position;
            var insert = item;
            IntPtr buffer = text is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(text);
            try
            {
                insert.cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>();
                insert.dwTypeData = buffer;
                insert.cch = (uint)(text?.Length ?? 0);
                insert.fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_DATA | MIIM_BITMAP | (text is null ? 0 : MIIM_STRING);
                return InsertMenuItem(menu, (uint)at, true, ref insert);
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        return false;
    }

    /// <summary>The item at a position, with its text (null for a separator or an item drawn by its owner).</summary>
    static MENUITEMINFO? Read(IntPtr menu, int position, out string? text)
    {
        text = null;
        var info = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_DATA | MIIM_BITMAP | MIIM_STRING,
        };
        if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) return null;
        if (info.cch > 0)
        {
            uint length = info.cch + 1;
            IntPtr buffer = Marshal.AllocHGlobal((int)length * 2);
            try
            {
                info.dwTypeData = buffer;
                info.cch = length;
                if (GetMenuItemInfo(menu, (uint)position, true, ref info)) text = Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        info.dwTypeData = IntPtr.Zero;
        info.cch = 0;
        return info;
    }
}
