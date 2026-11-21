using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Anything.Core;

namespace Anything;

/// <summary>
/// The address box's suggestions: the folders on the disk, as before, and the addresses typed before (Rock Preview's,
/// then File Explorer's), through Windows' own AutoComplete with both lists joined (CLSID_ACLMulti), as File Explorer's
/// address bar mixes in its history. WinForms' AutoCompleteSource can offer one or the other, not both.
/// </summary>
internal static class AddressAutoComplete
{
    /// <summary>
    /// Suggestions for <paramref name="box"/> from now on, bound again whenever its window is made. <paramref name="history"/>
    /// is asked on AutoComplete's own thread, so it must be safe to call from any thread.
    /// </summary>
    public static void Attach(TextBox box, Func<IReadOnlyList<string>> history)
    {
        box.AutoCompleteMode = AutoCompleteMode.None; // this binds its own; two on one box would both subclass it
        box.AutoCompleteSource = AutoCompleteSource.None;
        // The AutoComplete object, held while the box's window lives (as WinForms holds its own).
        var bound = s_bound.GetValue(box, _ => new Holder());
        void Unbind() { Release(bound.Object); bound.Object = null; }
        box.HandleCreated += (_, _) => { Unbind(); bound.Object = Bind(box.Handle, history); };
        box.HandleDestroyed += (_, _) => Unbind();
        box.Disposed += (_, _) => Unbind();
        if (box.IsHandleCreated) bound.Object = Bind(box.Handle, history);
    }

    sealed class Holder { public object? Object; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Holder> s_bound = new();

    /// <summary>Whether the box's suggestion list is open (IAutoCompleteDropDown); false when it can't be asked.</summary>
    public static bool SuggestionsShown(TextBox box)
    {
        if (!s_bound.TryGetValue(box, out var holder) || holder.Object is not IAutoCompleteDropDown dropDown) return false;
        try
        {
            if (dropDown.GetDropDownStatus(out uint flags, out IntPtr text) < 0) return false;
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
            return (flags & ACDD_VISIBLE) != 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True while <see cref="CloseSuggestions"/> hands the box its Escape: the box's own key handlers leave it alone.</summary>
    public static bool ClosingSuggestions { get; private set; }

    /// <summary>
    /// Closes the box's suggestion list if it is open, as Escape in the box does: AutoComplete's own handling of the key,
    /// which knows its list. The box's KeyDown sees <see cref="ClosingSuggestions"/> if the key reaches it.
    /// </summary>
    public static void CloseSuggestions(TextBox box)
    {
        if (!box.IsHandleCreated || !SuggestionsShown(box)) return;
        ClosingSuggestions = true;
        try { SendMessage(box.Handle, WM_KEYDOWN, VK_ESCAPE, 0x00010001); }
        finally { ClosingSuggestions = false; }
    }

    /// <summary>Binds the joined lists to the box; returns the AutoComplete object to hold, or null (then the folders alone).</summary>
    static object? Bind(IntPtr hwnd, Func<IReadOnlyList<string>> history)
    {
        object? auto = null, multi = null, folders = null;
        try
        {
            auto = Create(CLSID_AutoComplete);
            multi = Create(CLSID_ACLMulti);
            folders = Create(CLSID_ACListISF);
            if (auto is IAutoComplete2 complete && multi is IObjMgr lists)
            {
                if (folders is IACList2 options) options.SetOptions(ACLO_FILESYSDIRS); // what AutoCompleteSource.FileSystemDirectories offered
                if (folders != null) lists.Append(folders);
                lists.Append(new HistoryList(history));
                if (complete.Init(hwnd, multi, null, null) >= 0)
                {
                    complete.SetOptions(ACO_AUTOSUGGEST | ACO_AUTOAPPEND); // AutoCompleteMode.SuggestAppend
                    var keep = auto;
                    auto = null; // held by the caller, not released below
                    return keep;
                }
            }
        }
        catch (Exception) { } // never into the message loop: the folders alone below
        finally
        {
            Release(folders);
            Release(multi);
            Release(auto);
        }
        // Windows' combined lists aren't there: the folders alone, as before.
        try { SHAutoComplete(hwnd, SHACF_FILESYS_DIRS); } catch (Exception) { }
        return null;
    }

    static object? Create(Guid clsid)
    {
        var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type);
    }

    static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); }
        catch (Exception) { }
    }

    /// <summary>File Explorer's own typed paths (newest first), read and never written; none when they can't be read.</summary>
    public static List<string> ExplorerTypedPaths()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AddressHistory.ExplorerTypedPathsKey);
            return key is null ? [] : AddressHistory.FromExplorerTypedPaths(name => key.GetValue(name));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>The typed addresses as an IEnumString: a fresh copy each time AutoComplete starts over (Reset).</summary>
    sealed class HistoryList(Func<IReadOnlyList<string>> source) : IEnumString
    {
        readonly object _gate = new();
        IReadOnlyList<string> _items = [];
        int _next;
        bool _loaded;

        IReadOnlyList<string> Items()
        {
            if (!_loaded) Load();
            return _items;
        }

        void Load()
        {
            try { _items = source(); }
            catch (Exception) { _items = []; }
            _loaded = true;
        }

        public int Next(int celt, string[] rgelt, IntPtr pceltFetched)
        {
            int fetched = 0;
            lock (_gate)
            {
                var items = Items();
                while (fetched < celt && _next < items.Count) rgelt[fetched++] = items[_next++];
            }
            if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, fetched);
            return fetched == celt ? 0 /* S_OK */ : 1 /* S_FALSE */;
        }

        public int Skip(int celt)
        {
            lock (_gate)
            {
                var items = Items();
                _next = Math.Min(items.Count, _next + Math.Max(0, celt));
                return _next < items.Count ? 0 : 1;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _next = 0;
                Load();
            }
        }

        public void Clone(out IEnumString ppenum)
        {
            lock (_gate) ppenum = new HistoryList(source) { _items = _items, _next = _next, _loaded = _loaded };
        }
    }

    // ---- interop (ShlDisp.h, ShlObj_core.h, ShlGuid.h, Shlwapi.h) --------------------------------

    static readonly Guid CLSID_AutoComplete = new("00BB2763-6A77-11D0-A535-00C04FD7D062");
    static readonly Guid CLSID_ACLMulti = new("00BB2765-6A77-11D0-A535-00C04FD7D062");
    static readonly Guid CLSID_ACListISF = new("03C036F1-A186-11D0-824A-00AA005B4383");

    const uint ACO_AUTOSUGGEST = 0x1, ACO_AUTOAPPEND = 0x2;
    const uint ACLO_FILESYSDIRS = 32;
    const uint SHACF_FILESYS_DIRS = 0x20;

    /// <summary>IAutoComplete2: IAutoComplete's Init and Enable, then SetOptions and GetOptions.</summary>
    [ComImport, Guid("EAC04BC0-3791-11d2-BB95-0060977B464C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAutoComplete2
    {
        [PreserveSig] int Init(IntPtr hwndEdit, [MarshalAs(UnmanagedType.IUnknown)] object punkACL,
                               [MarshalAs(UnmanagedType.LPWStr)] string? pwszRegKeyPath, [MarshalAs(UnmanagedType.LPWStr)] string? pwszQuickComplete);
        [PreserveSig] int Enable([MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int SetOptions(uint dwFlag);
        [PreserveSig] int GetOptions(out uint pdwFlag);
    }

    /// <summary>IObjMgr: the lists CLSID_ACLMulti joins.</summary>
    [ComImport, Guid("00BB2761-6A77-11D0-A535-00C04FD7D062"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjMgr
    {
        [PreserveSig] int Append([MarshalAs(UnmanagedType.IUnknown)] object punk);
        [PreserveSig] int Remove([MarshalAs(UnmanagedType.IUnknown)] object punk);
    }

    /// <summary>IACList2: IACList's Expand, then SetOptions and GetOptions.</summary>
    [ComImport, Guid("470141a0-5186-11d2-bbb6-0060977b464c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IACList2
    {
        [PreserveSig] int Expand([MarshalAs(UnmanagedType.LPWStr)] string pszExpand);
        [PreserveSig] int SetOptions(uint dwFlag);
        [PreserveSig] int GetOptions(out uint pdwFlag);
    }

    /// <summary>IAutoCompleteDropDown (ShObjIdl.h): whether the suggestion list is open.</summary>
    [ComImport, Guid("3CD141F4-3C6A-11d2-BCAA-00C04FD929DB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAutoCompleteDropDown
    {
        [PreserveSig] int GetDropDownStatus(out uint pdwFlags, out IntPtr ppwszString); // the string: CoTaskMem, may be null
        [PreserveSig] int ResetEnumerator();
    }

    const uint ACDD_VISIBLE = 0x0001;
    const int WM_KEYDOWN = 0x0100;
    const nint VK_ESCAPE = 0x1B;

    [DllImport("shlwapi.dll")]
    static extern int SHAutoComplete(IntPtr hwndEdit, uint dwFlags);

    [DllImport("user32.dll")]
    static extern nint SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);
}
