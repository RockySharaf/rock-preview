using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Anything.Interop;

/// <summary>
/// Stands in for Windows' AutoPlay for USB drives while Rock Preview offers to open them itself (Settings › Defaults):
/// registered in the Running Object Table as an IQueryCancelAutoPlay, which Windows asks before it shows its own
/// AutoPlay. It says no for removable drives and fixed disks (USB disks show as fixed); CDs and DVDs keep Windows'.
/// </summary>
internal sealed class AutoPlayBlocker : IDisposable
{
    static readonly Guid CLSID_QueryCancelAutoPlay = new("331F1768-05A9-4ddd-B86E-DAE34DDC998A");
    const int ROTFLAGS_REGISTRATIONKEEPSALIVE = 1, S_OK = 0, S_FALSE = 1;
    const uint DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3;

    int _cookie;
    IRunningObjectTable? _rot;
    Canceller? _canceller;

    [ComImport, Guid("DDEFE873-6997-4e68-BE26-39B633ADBE12"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IQueryCancelAutoPlay
    {
        [PreserveSig]
        int AllowAutoPlay([MarshalAs(UnmanagedType.LPWStr)] string path, uint contentType, [MarshalAs(UnmanagedType.LPWStr)] string label, uint serialNumber);
    }

    [ComVisible(true)]
    sealed class Canceller : IQueryCancelAutoPlay
    {
        public int AllowAutoPlay(string path, uint contentType, string label, uint serialNumber)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return S_OK;
                string root = Path.GetPathRoot(path) ?? path;
                return GetDriveType(root) is DRIVE_REMOVABLE or DRIVE_FIXED ? S_FALSE : S_OK;
            }
            catch (Exception) { return S_OK; }
        }
    }

    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] static extern int CreateClassMoniker(ref Guid clsid, out IMoniker moniker);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveType(string root);

    /// <summary>Whether Windows' AutoPlay is held back now.</summary>
    public bool Active => _cookie != 0;

    /// <summary>Holds Windows' AutoPlay back for USB drives, or lets it be again.</summary>
    public void Set(bool on)
    {
        if (on == Active) return;
        if (!on) { Revoke(); return; }
        try
        {
            if (GetRunningObjectTable(0, out var rot) != S_OK || rot == null) return;
            var clsid = CLSID_QueryCancelAutoPlay;
            if (CreateClassMoniker(ref clsid, out var moniker) != S_OK || moniker == null) return;
            _canceller = new Canceller();
            _cookie = rot.Register(ROTFLAGS_REGISTRATIONKEEPSALIVE, _canceller, moniker);
            _rot = rot;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            _cookie = 0;
            _rot = null;
        }
    }

    void Revoke()
    {
        try { if (_cookie != 0) _rot?.Revoke(_cookie); }
        catch (COMException) { }
        _cookie = 0;
        _rot = null;
        _canceller = null;
    }

    public void Dispose() => Revoke();
}
