using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything.Interop;

/// <summary>
/// Keeps the process alive for the shell's work in it. A copy, move or delete started in the hosted Explorer view (paste,
/// drag and drop, its progress window) or a Properties window runs on the shell's own threads inside Rock Preview, and
/// the shell takes a reference on the process for as long as that work goes (CTF_PROCESS_REF) when the process has
/// registered one, as File Explorer does. This registers it (SetProcessReference from shcore.dll, and SHSetInstanceExplorer, which sets the same slot, for older
/// shell code), counts what the shell holds (<see cref="ShellWork"/>), and at exit waits for that work to finish instead
/// of ending the process under it, which cut a copy short mid-file.
/// </summary>
internal static unsafe class ShellProcessReference
{
    static readonly ShellWork s_work = new();
    static nint s_object; // the IUnknown the shell is given: a pointer to its table of three functions
    static bool s_registered;

    /// <summary>The name of the event that tells Setup this process (its ID added) is waiting for the shell's work to finish before it ends.</summary>
    public const string FinishingEventPrefix = @"Local\Rock Preview.FinishingFileOperations.";

    /// <summary>Gives the shell the process's reference. Once, on the UI thread, before any shell view exists.</summary>
    public static void Register()
    {
        if (s_object != 0) return;
        // Never freed: the shell may call it until the process ends.
        var table = (nint*)NativeMemory.Alloc((nuint)(3 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        var obj = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        *obj = (nint)table;
        s_object = (nint)obj;
        // Neither function takes a reference of its own: the one this keeps is the app's, never counted.
        try { SetProcessReference(s_object); s_registered = true; } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        try { SHSetInstanceExplorer(s_object); s_registered = true; } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>
    /// Whether the shell is working in the process now (a copy from the Explorer view going on): automatic updates wait
    /// for it. Looks at the process's windows: call it on the UI thread.
    /// </summary>
    public static bool Busy => s_registered && s_work.Busy(ShellWindowShown());

    /// <summary>
    /// At exit, after the app's windows have gone: takes the reference back from the shell, so nothing new holds the
    /// process, then waits (pumping messages, as the shell's threads may call back into this thread) while its work
    /// goes on. Returns at once when there is none.
    /// </summary>
    public static void WaitForShellWork()
    {
        if (!s_registered) return;
        try { SetProcessReference(0); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        try { SHSetInstanceExplorer(0); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        if (!Busy) return;
        // Setup (an update, or an uninstall) waits for this process to end: this tells it why it is taking a while.
        using var finishing = new EventWaitHandle(false, EventResetMode.ManualReset, FinishingEventPrefix + Environment.ProcessId);
        using var tick = new ManualResetEvent(false);
        while (Busy)
        {
            tick.WaitOne(250); // on this STA thread the wait also lets COM calls in
            try { Application.DoEvents(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Whether a window of this process that isn't one of the app's own is on screen: the shell's progress or Properties
    /// window. A helper window some component keeps "visible" but with no size, hidden by Windows (cloaked), on no
    /// monitor, or a tooltip, doesn't count: with a reference never let go it would keep an invisible process alive.
    /// The shell's progress window and dialogs are exempt from those checks (see below).
    /// </summary>
    static bool ShellWindowShown()
    {
        bool shown = false;
        uint self = (uint)Environment.ProcessId;
        var cls = new char[64];
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != self || Control.FromHandle(hwnd) is not null) return true;
            int n = GetClassName(hwnd, cls, cls.Length);
            string name = n > 0 ? new string(cls, 0, n) : "";
            if (name.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase)) return true;
            // The shell's copy progress window and its dialogs (Properties, "Replace or skip") always count: Windows
            // cloaks them while they sit on another virtual desktop, and one can be left off-screen after a monitor is
            // unplugged, yet the copy behind them is still going on and must not be cut short.
            bool shellWindow = name.Equals("OperationStatusWindow", StringComparison.OrdinalIgnoreCase) || name == "#32770";
            if (!shellWindow)
            {
                if (!GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
                if (MonitorFromWindow(hwnd, 0 /* MONITOR_DEFAULTTONULL */) == IntPtr.Zero) return true;
                try { if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0) return true; }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            }
            shown = true;
            return false;
        }, IntPtr.Zero);
        return shown;
    }

    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    const int S_OK = 0, E_NOINTERFACE = unchecked((int)0x80004002), E_POINTER = unchecked((int)0x80004003);

    [UnmanagedCallersOnly]
    static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (result == null) return E_POINTER;
        if (iid != null && *iid == IID_IUnknown)
        {
            *result = self;
            s_work.AddRef();
            return S_OK;
        }
        *result = 0;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    static uint AddRef(nint self) => (uint)s_work.AddRef() + 1; // and the app's own

    [UnmanagedCallersOnly]
    static uint Release(nint self) => (uint)s_work.Release() + 1;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // Declared in shlwapi.h, but shlwapi.dll doesn't export it by name: shcore.dll does (and shell32 imports it from there).
    [DllImport("shcore.dll")] static extern void SetProcessReference(nint punk);
    [DllImport("shell32.dll")] static extern void SHSetInstanceExplorer(nint punk);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, char[] name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out uint value, int size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
}
