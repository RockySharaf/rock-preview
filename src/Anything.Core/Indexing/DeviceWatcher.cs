using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>
/// Hears Windows' device notifications on a hidden window of its own: drives arriving and leaving (so a USB stick
/// shows up in searches as soon as it is plugged in), and "this drive is about to be ejected" for the drives being
/// indexed, so their handles are closed first and Safely Remove Hardware works. Locking or dismounting a volume
/// (format, chkdsk /f, BitLocker, a card reader's Eject) needs the same, without the device going away.
/// </summary>
internal sealed unsafe class DeviceWatcher : IDisposable
{
    const int WM_DEVICECHANGE = 0x0219, WM_CLOSE = 0x0010, WM_APP_REGISTER = 0x8000 + 41;
    const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEQUERYREMOVE = 0x8001, DBT_DEVICEQUERYREMOVEFAILED = 0x8002,
        DBT_DEVICEREMOVEPENDING = 0x8003, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_CUSTOMEVENT = 0x8006;
    const int DBT_DEVTYP_VOLUME = 2, DBT_DEVTYP_HANDLE = 6;

    // Volume events (ioevent.h), sent as DBT_CUSTOMEVENT to handle registrations. A lock or dismount fails if any
    // handle stays open; a dismount isn't necessarily preceded by a lock.
    static readonly Guid GUID_IO_VOLUME_LOCK = new("50708874-c9af-11d1-8fef-00a0c9a06d32"),
        GUID_IO_VOLUME_LOCK_FAILED = new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32"),
        GUID_IO_VOLUME_UNLOCK = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32"),
        GUID_IO_VOLUME_DISMOUNT = new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32"),
        GUID_IO_VOLUME_DISMOUNT_FAILED = new("e3c5b178-105d-11d2-8ffd-00a0c9a06d32"),
        GUID_IO_VOLUME_MOUNT = new("b5804878-1a96-11d2-8ffd-00a0c9a06d32");

    /// <summary>Bytes a DEV_BROADCAST_HANDLE must have for its event GUID to be there (48 on x64).</summary>
    static readonly int EventGuidEnd = (int)Marshal.OffsetOf<DEV_BROADCAST_HANDLE>(nameof(DEV_BROADCAST_HANDLE.dbch_eventguid)) + sizeof(Guid);

    /// <summary>A drive was plugged in or removed (or a network drive mapped). Raised on the watcher's thread.</summary>
    public event Action? VolumesChanged;

    static int s_classes;

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly Dictionary<nint, Watch> _watches = [];
    readonly object _gate = new();
    WndProc? _proc; // kept alive while the window exists
    nint _hwnd;
    bool _disposed;

    delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    public DeviceWatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Rock Preview device notifications" };
        _thread.Start();
        _ready.Wait(5000);
    }

    /// <summary>The hidden window that receives the notifications (0 if it couldn't be created).</summary>
    internal nint Window => _hwnd;

    /// <summary>A registration for one drive: closes its own handle when the drive is ejected.</summary>
    internal sealed class Watch(DeviceWatcher owner, string root, Action release, Action ejectFailed) : IDisposable
    {
        public readonly string Root = root;
        public readonly Action Release = release;
        public readonly Action EjectFailed = ejectFailed;
        public SafeFileHandle? Handle;
        public nint Notify;
        /// <summary>Set once Release has run (on the watcher's thread only), so a lock followed by an eject releases once.</summary>
        public bool Released;

        public void Close()
        {
            Handle?.Dispose();
            Handle = null;
        }

        public void Dispose() => owner.Unwatch(this);
    }

    /// <summary>
    /// Calls <paramref name="release"/> (on the watcher's thread, before Windows checks for open handles) when the drive
    /// of <paramref name="root"/> is about to be ejected, or its volume locked or dismounted, and
    /// <paramref name="ejectFailed"/> if that is then cancelled (or, for a lock, over). Null when the drive can't be watched.
    /// </summary>
    public IDisposable? WatchRemoval(string root, Action release, Action ejectFailed)
    {
        if (_hwnd == 0) return null;
        var h = Native.CreateFile(Native.LongPath(root), 0, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) { h.Dispose(); return null; }
        var watch = new Watch(this, root, release, ejectFailed) { Handle = h };
        var gc = GCHandle.Alloc(watch);
        try
        {
            // Registered on the window's thread, which is the one that will receive the notifications.
            if (SendMessageW(_hwnd, WM_APP_REGISTER, GCHandle.ToIntPtr(gc), 0) == 0) { watch.Close(); return null; }
        }
        finally { gc.Free(); }
        return watch;
    }

    void Unwatch(Watch watch)
    {
        lock (_gate)
        {
            if (watch.Notify != 0)
            {
                _watches.Remove(watch.Notify);
                UnregisterDeviceNotification(watch.Notify);
                watch.Notify = 0;
            }
        }
        watch.Close();
    }

    void Run()
    {
        _proc = Proc;
        // A class of its own per watcher, unregistered when its window is gone: a thread id is reused once the thread
        // ends, and a later watcher whose class name was taken would get no window (so no notifications at all).
        string cls = "RockPreviewDeviceWatcher" + Interlocked.Increment(ref s_classes);
        fixed (char* name = cls)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = name,
            };
            if (RegisterClassExW(&wc) == 0) { _ready.Set(); return; }
            try
            {
                // A hidden top-level window: message-only windows don't get the drive arrival/removal broadcasts.
                _hwnd = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, name, name, 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
                _ready.Set();
                if (_hwnd == 0) return;
                MSG msg;
                while (GetMessageW(&msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
            finally { UnregisterClassW(name, wc.hInstance); }
        }
    }

    nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_APP_REGISTER) return Register((Watch)GCHandle.FromIntPtr(wParam).Target!) ? 1 : 0;
            if (msg == WM_DEVICECHANGE && lParam != 0) return OnDeviceChange((int)wParam, (DEV_BROADCAST_HDR*)lParam);
            if (msg == WM_CLOSE) { DestroyWindow(hwnd); PostQuitMessage(0); return 0; }
        }
        catch (Exception) { /* never let an exception cross into Windows */ }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    bool Register(Watch watch)
    {
        var filter = new DEV_BROADCAST_HANDLE
        {
            dbch_size = (uint)sizeof(DEV_BROADCAST_HANDLE),
            dbch_devicetype = DBT_DEVTYP_HANDLE,
            dbch_handle = watch.Handle!.DangerousGetHandle(),
        };
        nint notify = RegisterDeviceNotificationW(_hwnd, &filter, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (notify == 0) return false;
        lock (_gate)
        {
            watch.Notify = notify;
            _watches[notify] = watch;
        }
        return true;
    }

    nint OnDeviceChange(int ev, DEV_BROADCAST_HDR* hdr)
    {
        if (hdr->dbch_devicetype == DBT_DEVTYP_VOLUME)
        {
            if (ev is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) VolumesChanged?.Invoke();
            return 1;
        }
        if (hdr->dbch_devicetype != DBT_DEVTYP_HANDLE) return 1;
        var h = (DEV_BROADCAST_HANDLE*)hdr;
        Watch? watch;
        lock (_gate) _watches.TryGetValue(h->dbch_hdevnotify, out watch);
        if (watch == null) return 1;
        switch (ev)
        {
            case DBT_DEVICEQUERYREMOVE:
                ReleaseDrive(watch);
                break;
            case DBT_DEVICEQUERYREMOVEFAILED:
                ReleaseFailed(watch);
                break;
            case DBT_DEVICEREMOVEPENDING or DBT_DEVICEREMOVECOMPLETE:
                Unwatch(watch);
                VolumesChanged?.Invoke();
                break;
            case DBT_CUSTOMEVENT when hdr->dbch_size >= EventGuidEnd:
            {
                var guid = h->dbch_eventguid;
                if (guid == GUID_IO_VOLUME_LOCK || guid == GUID_IO_VOLUME_DISMOUNT) ReleaseDrive(watch);
                else if (guid == GUID_IO_VOLUME_LOCK_FAILED || guid == GUID_IO_VOLUME_UNLOCK || guid == GUID_IO_VOLUME_DISMOUNT_FAILED) ReleaseFailed(watch);
                else if (guid == GUID_IO_VOLUME_MOUNT) VolumesChanged?.Invoke();
                break;
            }
        }
        return 1; // TRUE: allow the removal
    }

    /// <summary>
    /// Closes every handle on the drive now: Windows refuses the eject, lock or dismount if any stays open. Once per
    /// watch (a lock followed by an eject releases once); the registration stays, so the outcome still arrives.
    /// </summary>
    static void ReleaseDrive(Watch watch)
    {
        if (!watch.Released)
        {
            watch.Released = true;
            try { watch.Release(); } catch (Exception) { }
        }
        watch.Close();
    }

    /// <summary>
    /// The eject, lock or dismount didn't happen (or is over): the drive can be read again, so the manager gets it
    /// back and replaces the root. Nothing to do if this watch never let go (another program's eject was refused).
    /// </summary>
    void ReleaseFailed(Watch watch)
    {
        if (!watch.Released) return;
        Unwatch(watch);
        try { watch.EjectFailed(); } catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Watch[] watches;
        lock (_gate) watches = [.. _watches.Values];
        foreach (var w in watches) Unwatch(w);
        if (_hwnd != 0) PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        _thread.Join(2000);
        _ready.Dispose();
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_HDR
    {
        public uint dbch_size, dbch_devicetype, dbch_reserved;
    }

    /// <summary>As in dbt.h (natural alignment): on x64 the handles are at 16 and 24, the GUID at 32, 56 bytes in all.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEV_BROADCAST_HANDLE
    {
        public uint dbch_size, dbch_devicetype, dbch_reserved;
        public nint dbch_handle, dbch_hdevnotify;
        public Guid dbch_eventguid;
        public int dbch_nameoffset;
        public byte dbch_data;
    }

    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] static extern int UnregisterClassW(char* cls, nint instance);
    [DllImport("user32.dll")] static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern int TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint RegisterDeviceNotificationW(nint recipient, void* filter, uint flags);
    [DllImport("user32.dll")] static extern int UnregisterDeviceNotification(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
}
