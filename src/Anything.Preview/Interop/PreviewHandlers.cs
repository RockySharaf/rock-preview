using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Anything.Preview.Util;
using Microsoft.Win32;

namespace Anything.Preview.Interop;

/// <summary>Looks up registered shell preview handlers (HKCR\.ext\ShellEx\{8895b1c6…}, then the ProgID, then SystemFileAssociations).</summary>
public static class PreviewHandlerRegistry
{
    private const string Key = "ShellEx\\{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private static readonly ConcurrentDictionary<string, (Guid Clsid, string? Name)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryFind(string extension, out Guid clsid, out string? name)
    {
        var r = _cache.GetOrAdd(extension, Lookup);
        clsid = r.Clsid;
        name = r.Name;
        return clsid != Guid.Empty;
    }

    private static (Guid, string?) Lookup(string ext)
    {
        try
        {
            if (string.IsNullOrEmpty(ext)) return (Guid.Empty, null);
            Guid g = Read(ext + "\\" + Key);
            if (g == Guid.Empty)
            {
                using var k = Registry.ClassesRoot.OpenSubKey(ext);
                string? progId = k?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(progId)) g = Read(progId + "\\" + Key);
                if (g == Guid.Empty)
                {
                    string? perceived = k?.GetValue("PerceivedType") as string;
                    g = Read("SystemFileAssociations\\" + ext + "\\" + Key);
                    if (g == Guid.Empty && !string.IsNullOrEmpty(perceived)) g = Read("SystemFileAssociations\\" + perceived + "\\" + Key);
                }
            }
            if (g == Guid.Empty) return (Guid.Empty, null);
            string? name = null;
            using (var ck = Registry.ClassesRoot.OpenSubKey("CLSID\\" + g.ToString("B")))
                name = ck?.GetValue(null) as string ?? ck?.GetValue("DisplayName") as string;
            return (g, name);
        }
        catch
        {
            return (Guid.Empty, null);
        }
    }

    private static Guid Read(string sub)
    {
        using var k = Registry.ClassesRoot.OpenSubKey(sub);
        return k?.GetValue(null) is string s && Guid.TryParse(s, out var g) ? g : Guid.Empty;
    }
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref RECT rect);
    void SetRect(ref RECT rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    void Initialize([MarshalAs(UnmanagedType.IUnknown)] object psi, uint grfMode);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

/// <summary>A read-only COM IStream over a shared FileStream (so the file is never locked against delete/rename).</summary>
internal sealed class ReadOnlyComStream : IStream, IDisposable
{
    private readonly Stream _s;
    private readonly string _name;
    public ReadOnlyComStream(Stream s, string name) { _s = s; _name = name; }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int total = 0;
        while (total < cb)
        {
            int r = _s.Read(pv, total, cb - total);
            if (r <= 0) break;
            total += r;
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("read-only", unchecked((int)0x80030005)); // STG_E_ACCESSDENIED

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long pos = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void SetSize(long libNewSize) => throw new COMException("read-only", unchecked((int)0x80030005));

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buf = new byte[81920];
        long done = 0;
        while (done < cb)
        {
            int r = _s.Read(buf, 0, (int)Math.Min(buf.Length, cb - done));
            if (r <= 0) break;
            pstm.Write(buf, r, IntPtr.Zero);
            done += r;
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, done);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, done);
    }

    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("not supported", unchecked((int)0x80030001));
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("not supported", unchecked((int)0x80030001));

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _s.Length,
            grfMode = 0,
            pwcsName = (grfStatFlag & 1) != 0 ? null! : _name, // STATFLAG_NONAME
        };
    }

    public void Clone(out IStream ppstm) => throw new COMException("not supported", unchecked((int)0x80004001));

    public void Dispose() => _s.Dispose();
}

/// <summary>
/// WinForms host for a shell IPreviewHandler (Office, Outlook, Visio, PowerToys, Adobe…). Must live on the UI STA
/// thread. Tries out-of-process activation first (prevhost.exe surrogate, like Explorer), then in-process.
/// Initialization order: IInitializeWithFile → IInitializeWithStream → IInitializeWithItem.
/// </summary>
internal sealed class PreviewHandlerHost : Control
{
    private IPreviewHandler? _handler;
    private ReadOnlyComStream? _stream;
    private object? _shellItem;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    private const uint CLSCTX_INPROC_SERVER = 1, CLSCTX_LOCAL_SERVER = 4;
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    public PreviewHandlerHost()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
    }

    public string? LoadedPath { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Creates, initializes and shows the handler. Returns false (and leaves the host empty) on failure.</summary>
    public bool Load(string path, Guid clsid)
    {
        Unload();
        Error = null;
        if (!IsHandleCreated) CreateControl();
        object? obj = null;
        try
        {
            obj = Create(clsid);
            if (obj == null) { Error = "could not create handler"; return false; }
            bool ok = false;
            if (obj is IInitializeWithFile iwf)
            {
                try { iwf.Initialize(path, 0); ok = true; } catch (Exception ex) { Error = "IInitializeWithFile: " + ex.Message; }
            }
            if (!ok && obj is IInitializeWithStream iws)
            {
                try
                {
                    _stream = new ReadOnlyComStream(FileUtil.OpenShared(path, 1 << 16), Path.GetFileName(path));
                    iws.Initialize(_stream, 0);
                    ok = true;
                }
                catch (Exception ex) { Error = "IInitializeWithStream: " + ex.Message; _stream?.Dispose(); _stream = null; }
            }
            if (!ok && obj is IInitializeWithItem iwi)
            {
                try
                {
                    _shellItem = Shell.CreateShellItem(path);
                    if (_shellItem != null) { iwi.Initialize(_shellItem, 0); ok = true; }
                }
                catch (Exception ex) { Error = "IInitializeWithItem: " + ex.Message; }
            }
            if (!ok || obj is not IPreviewHandler h)
            {
                Error ??= "handler does not support any initialization interface";
                Release(obj);
                ReleaseExtras();
                return false;
            }
            _handler = h;
            var r = ClientRect();
            _handler.SetWindow(Handle, ref r);
            _handler.SetRect(ref r);
            _handler.DoPreview();
            LoadedPath = path;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            if (obj != null && !ReferenceEquals(obj, _handler)) Release(obj);
            Unload();
            return false;
        }
    }

    private static object? Create(Guid clsid)
    {
        var iid = IID_IUnknown;
        // Out-of-proc (prevhost surrogate) keeps a crashing handler from taking the app down.
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out var o) == 0 && o != null) return o;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out o) == 0 && o != null) return o;
        return null;
    }

    private RECT ClientRect() => new() { Left = 0, Top = 0, Right = Math.Max(1, ClientSize.Width), Bottom = Math.Max(1, ClientSize.Height) };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_handler != null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var r = ClientRect();
            try { _handler.SetRect(ref r); } catch { }
        }
    }

    public void Unload()
    {
        if (_handler != null)
        {
            try { _handler.Unload(); } catch { }
            Release(_handler);
            _handler = null;
        }
        ReleaseExtras();
        LoadedPath = null;
        Invalidate();
    }

    private void ReleaseExtras()
    {
        if (_stream != null) { try { _stream.Dispose(); } catch { } _stream = null; }
        if (_shellItem != null) { Release(_shellItem); _shellItem = null; }
    }

    private static void Release(object o)
    {
        try { if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Unload();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Unload();
        base.Dispose(disposing);
    }
}
