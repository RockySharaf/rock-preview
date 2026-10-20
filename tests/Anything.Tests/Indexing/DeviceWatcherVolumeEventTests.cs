using System.Runtime.InteropServices;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// The device watcher's handling of eject and volume lock/dismount notifications, sent to its window the way Windows
/// sends them (a registration on a temp folder; no real drive is locked or ejected).
/// </summary>
public sealed unsafe class DeviceWatcherVolumeEventTests : IDisposable
{
    const uint WM_DEVICECHANGE = 0x0219;
    const int DBT_DEVICEQUERYREMOVE = 0x8001, DBT_DEVICEQUERYREMOVEFAILED = 0x8002, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_CUSTOMEVENT = 0x8006;
    const uint DBT_DEVTYP_HANDLE = 6;

    // From the Windows SDK's ioevent.h.
    static readonly Guid VolumeLock = new("50708874-c9af-11d1-8fef-00a0c9a06d32"),
        VolumeLockFailed = new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32"),
        VolumeUnlock = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32"),
        VolumeDismount = new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32"),
        VolumeDismountFailed = new("e3c5b178-105d-11d2-8ffd-00a0c9a06d32"),
        VolumeMount = new("b5804878-1a96-11d2-8ffd-00a0c9a06d32"),
        VolumeLabelChange = new("7373654a-812a-11d0-bec7-08002be2092f");

    readonly string _dir = Directory.CreateTempSubdirectory("anything-watch-").FullName;
    readonly DeviceWatcher _watcher = new();

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    sealed class Calls
    {
        public int Released, Failed;
    }

    (DeviceWatcher.Watch Watch, Calls Calls) Watch()
    {
        var calls = new Calls();
        var watch = _watcher.WatchRemoval(_dir, () => calls.Released++, () => calls.Failed++);
        Assert.NotNull(watch);
        var w = (DeviceWatcher.Watch)watch;
        Assert.NotEqual(0, w.Notify);
        return (w, calls);
    }

    /// <summary>Sends WM_DEVICECHANGE with a DEV_BROADCAST_HANDLE for <paramref name="notify"/>, synchronously.</summary>
    void Send(nint notify, int ev, Guid guid = default, uint? size = null)
    {
        var b = new DeviceWatcher.DEV_BROADCAST_HANDLE
        {
            dbch_size = size ?? (uint)sizeof(DeviceWatcher.DEV_BROADCAST_HANDLE),
            dbch_devicetype = DBT_DEVTYP_HANDLE,
            dbch_hdevnotify = notify,
            dbch_eventguid = guid,
            dbch_nameoffset = -1,
        };
        SendMessageW(_watcher.Window, WM_DEVICECHANGE, ev, (nint)(&b));
    }

    [Fact]
    public void Handle_broadcast_has_the_dbt_h_layout()
    {
        Assert.Equal(8, IntPtr.Size); // the app ships for 64-bit Windows only
        Assert.Equal(16, (int)Marshal.OffsetOf<DeviceWatcher.DEV_BROADCAST_HANDLE>(nameof(DeviceWatcher.DEV_BROADCAST_HANDLE.dbch_handle)));
        Assert.Equal(24, (int)Marshal.OffsetOf<DeviceWatcher.DEV_BROADCAST_HANDLE>(nameof(DeviceWatcher.DEV_BROADCAST_HANDLE.dbch_hdevnotify)));
        Assert.Equal(32, (int)Marshal.OffsetOf<DeviceWatcher.DEV_BROADCAST_HANDLE>(nameof(DeviceWatcher.DEV_BROADCAST_HANDLE.dbch_eventguid)));
        Assert.Equal(48, (int)Marshal.OffsetOf<DeviceWatcher.DEV_BROADCAST_HANDLE>(nameof(DeviceWatcher.DEV_BROADCAST_HANDLE.dbch_nameoffset)));
        Assert.Equal(56, sizeof(DeviceWatcher.DEV_BROADCAST_HANDLE));
    }

    [Fact]
    public void Lock_releases_once_and_unlock_hands_the_drive_back()
    {
        var (watch, calls) = Watch();
        nint notify = watch.Notify;

        Send(notify, DBT_CUSTOMEVENT, VolumeLock);
        Assert.Equal(1, calls.Released);
        Assert.Null(watch.Handle); // its own handle is closed too
        // A dismount and then an eject of the same drive: already let go of.
        Send(notify, DBT_CUSTOMEVENT, VolumeDismount);
        Send(notify, DBT_DEVICEQUERYREMOVE);
        Assert.Equal(1, calls.Released);
        Assert.Equal(0, calls.Failed);

        Send(notify, DBT_CUSTOMEVENT, VolumeUnlock);
        Assert.Equal(1, calls.Failed);
        Assert.Equal(0, watch.Notify); // unregistered: the manager replaces the root, with a new watch
        Send(notify, DBT_CUSTOMEVENT, VolumeLock);
        Assert.Equal(1, calls.Released);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("dismount")]
    [InlineData("eject")]
    public void Failed_lock_dismount_or_eject_hands_the_drive_back(string what)
    {
        var (watch, calls) = Watch();
        nint notify = watch.Notify;
        switch (what)
        {
            case "lock": Send(notify, DBT_CUSTOMEVENT, VolumeLock); Send(notify, DBT_CUSTOMEVENT, VolumeLockFailed); break;
            case "dismount": Send(notify, DBT_CUSTOMEVENT, VolumeDismount); Send(notify, DBT_CUSTOMEVENT, VolumeDismountFailed); break;
            default: Send(notify, DBT_DEVICEQUERYREMOVE); Send(notify, DBT_DEVICEQUERYREMOVEFAILED); break;
        }
        Assert.Equal(1, calls.Released);
        Assert.Equal(1, calls.Failed);
        Assert.Equal(0, watch.Notify);
    }

    [Fact]
    public void Failure_without_a_release_changes_nothing()
    {
        // Another program's eject (or lock) was refused before this drive's watch was asked: it never let go.
        var (watch, calls) = Watch();
        nint notify = watch.Notify;
        Send(notify, DBT_DEVICEQUERYREMOVEFAILED);
        Send(notify, DBT_CUSTOMEVENT, VolumeUnlock);
        Send(notify, DBT_CUSTOMEVENT, VolumeLockFailed);
        Send(notify, DBT_CUSTOMEVENT, VolumeLabelChange);
        Assert.Equal(0, calls.Released);
        Assert.Equal(0, calls.Failed);
        Assert.Equal(notify, watch.Notify);
        Assert.NotNull(watch.Handle);
    }

    [Fact]
    public void Custom_event_too_short_to_hold_its_guid_is_ignored()
    {
        var (watch, calls) = Watch();
        Send(watch.Notify, DBT_CUSTOMEVENT, VolumeLock, size: 32);
        Assert.Equal(0, calls.Released);
        Assert.NotNull(watch.Handle);
    }

    [Fact]
    public void Mount_and_removal_raise_volumes_changed()
    {
        int changed = 0;
        _watcher.VolumesChanged += () => changed++;
        var (watch, calls) = Watch();
        nint notify = watch.Notify;
        Send(notify, DBT_CUSTOMEVENT, VolumeMount);
        Assert.Equal(1, changed);
        Assert.Equal(0, calls.Released);

        Send(notify, DBT_CUSTOMEVENT, VolumeLock);
        Send(notify, DBT_DEVICEREMOVECOMPLETE);
        Assert.Equal(2, changed);
        Assert.Equal(1, calls.Released);
        Assert.Equal(0, watch.Notify);
    }

    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
