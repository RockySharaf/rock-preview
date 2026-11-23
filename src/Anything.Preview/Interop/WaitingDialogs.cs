using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Anything.Core;
using Microsoft.Win32;

namespace Anything.Preview.Interop;

/// <summary>
/// Finds a message box that a hidden copy of a preview handler's program is waiting on (an Office program started for
/// a preview, showing a message nobody can see, answers every new preview "busy") and brings it to the front so the
/// user can answer it. It only looks and brings forward: it never closes or answers anything, and never touches a
/// copy of the program the user has open. A given message is brought to the front once, and only when the user is
/// not typing or clicking; otherwise it is only flashed, so keys meant for the file list (arrowing through several
/// such files) do not land in it; a message only flashed for that reason is looked at again for a few seconds and
/// brought forward once the user is quiet (a message box of a hidden program has no taskbar button, so its flash cannot
/// be seen); each new look replaces that retry, and one that asks for none stops it. A window that is not responding is left alone. The decisions themselves are in
/// <see cref="PreviewHandlerRules"/>.
/// </summary>
internal static class WaitingDialogs
{
    private const int MaxWindows = 20000; // EnumWindows is stopped after this many, whatever the desktop holds

    // Threads: BringToFront runs on the UI thread (a preview load), and the retry timer ticks on the thread that made it
    // (the first UI thread that asked for a retry), so in practice both run on one thread and never interleave. The
    // remembered message (_last*) is still read and written only under Gate, in case BringToFront is called from
    // another thread. The retry fields (_retry*) are touched only on _retryThread, the timer's own thread; a call from
    // any other thread leaves them alone.
    private static readonly object Gate = new();
    private static IntPtr _lastHwnd;      // the message last brought to the front, zero for none
    private static uint _lastPid;         // its process
    private static DateTime _lastTimeUtc; // when it was brought to the front

    private static System.Windows.Forms.Timer? _retryTimer; // made on first need; holds no window of the app's own
    private static int _retryThread;      // managed id of the thread that made it, 0 before that
    private static string? _retryName;    // process name of the program whose message is watched, null for none
    private static int _retryTicks;       // looks again since the last call that asked for them
    private static bool _inTick;          // a tick is running (guards against a nested tick)

    /// <summary>The handler's LocalServer32 command line (HKCR\CLSID\{id}\LocalServer32), or null when it has none.</summary>
    public static string? LocalServerCommand(Guid clsid)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey("CLSID\\" + clsid.ToString("B") + "\\LocalServer32");
            return k?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Looks for the message the handler's program is waiting on and brings it to the front, or only flashes it in the
    /// taskbar when Windows refuses the foreground, the user is typing or clicking, or it was brought to the front
    /// before. A message whose window is not responding is left alone and reported NotFound. Programs that many
    /// things run in are not searched. When it was only flashed because the user was typing or clicking, it is looked
    /// at again every <see cref="PreviewHandlerRules.RetryIntervalMs"/> for a while and brought forward once the user
    /// is quiet (only when called on a thread with a message loop). Every call replaces what the retry is doing: a
    /// call that does not ask for a retry stops a running one, so the retry never acts for an older call (it could
    /// otherwise pull an earlier program's message over the one just brought forward).
    /// </summary>
    public static PreviewHandlerRules.WaitingMessage BringToFront(Guid clsid)
    {
        bool retryStarted = false;
        try
        {
            string? exe = PreviewHandlerRules.ExecutablePath(LocalServerCommand(clsid));
            if (exe == null || PreviewHandlerRules.IsSharedHost(exe)) return PreviewHandlerRules.WaitingMessage.NotFound;
            string name = Path.GetFileNameWithoutExtension(exe);
            if (string.IsNullOrEmpty(name)) return PreviewHandlerRules.WaitingMessage.NotFound;

            IntPtr hwnd = LookFor(name, out uint pid, out bool hung);
            if (hwnd == IntPtr.Zero) return PreviewHandlerRules.WaitingMessage.NotFound;

            bool retry;
            lock (Gate)
            {
                bool before = PreviewHandlerRules.SameWaitingWindow(hwnd, pid, StartTimeUtc(pid), _lastHwnd, _lastPid, _lastTimeUtc);
                uint idle = InputIdleMs();
                bool down = AnyInputDown();
                var action = PreviewHandlerRules.WaitingWindowAction(hung, before, idle, down);
                if (action == PreviewHandlerRules.WaitingAction.Leave) return PreviewHandlerRules.WaitingMessage.NotFound;
                if (action == PreviewHandlerRules.WaitingAction.BringForward && TryBringForward(hwnd, pid))
                    return PreviewHandlerRules.WaitingMessage.BroughtToFront;
                retry = PreviewHandlerRules.RetryAfterFlash(hung, before, idle, down);
            }
            // Only flashed: not remembered as brought forward, so a later quiet moment can still bring it forward.
            Flash(hwnd);
            if (retry)
            {
                StartRetry(name);
                retryStarted = true;
            }
            return PreviewHandlerRules.WaitingMessage.FlashedOnly;
        }
        catch
        {
            return PreviewHandlerRules.WaitingMessage.NotFound;
        }
        finally
        {
            // Any other outcome ends an older call's retry; only on the timer's own thread, like StartRetry.
            if (!retryStarted && _retryThread == Environment.CurrentManagedThreadId) StopRetry();
        }
    }

    /// <summary>
    /// The waiting message of the program with this process name and its process, or zero. hung is whether its window
    /// is not responding: flashing or bringing such a window forward could wait on it and freeze this one, so it is
    /// left alone and the plain busy advice (close the program) is given.
    /// </summary>
    private static IntPtr LookFor(string name, out uint pid, out bool hung)
    {
        pid = 0;
        hung = false;
        var pids = new HashSet<uint>();
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); } catch { return IntPtr.Zero; }
        foreach (var p in procs)
        {
            try { pids.Add((uint)p.Id); } catch { }
            p.Dispose();
        }
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr hwnd = Find(pids, out pid);
        if (hwnd != IntPtr.Zero) hung = IsHungAppWindow(hwnd);
        return hwnd;
    }

    /// <summary>Brings the window to the front and remembers it as brought forward; false when Windows refuses. Called under Gate.</summary>
    private static bool TryBringForward(IntPtr hwnd, uint pid)
    {
        // Async: a plain ShowWindow on another process's window waits for that window's thread.
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        if (!SetForegroundWindow(hwnd)) return false;
        _lastHwnd = hwnd;
        _lastPid = pid;
        _lastTimeUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Watches the program's message from now on (replacing whatever was watched) and starts the count again. A thread
    /// without a message loop (System.Windows.Forms.Application.MessageLoop false) would never see a tick, so there no
    /// timer is started; neither on a thread other than the one that made the timer.
    /// </summary>
    private static void StartRetry(string name)
    {
        try
        {
            if (!System.Windows.Forms.Application.MessageLoop) return;
            int thread = Environment.CurrentManagedThreadId;
            if (_retryTimer == null)
            {
                if (_retryThread != 0 && _retryThread != thread) return;
                // A WinForms timer neither keeps the app running nor refers to any of its windows.
                var timer = new System.Windows.Forms.Timer { Interval = PreviewHandlerRules.RetryIntervalMs };
                timer.Tick += OnRetryTick;
                _retryTimer = timer;
                _retryThread = thread;
            }
            else if (_retryThread != thread)
            {
                return;
            }
            _retryName = name;
            _retryTicks = 0;
            _retryTimer.Stop(); // restarts the interval as well as the count
            _retryTimer.Start();
        }
        catch
        {
            if (_retryThread == Environment.CurrentManagedThreadId) StopRetry();
        }
    }

    /// <summary>One look again (see <see cref="PreviewHandlerRules.WaitingRetryStep"/>); never throws into the message loop.</summary>
    private static void OnRetryTick(object? sender, EventArgs e)
    {
        if (_inTick) return;
        _inTick = true;
        try
        {
            string? name = _retryName;
            if (name == null) { StopRetry(); return; }
            _retryTicks++;
            IntPtr hwnd = LookFor(name, out uint pid, out bool hung);
            lock (Gate)
            {
                var look = hwnd == IntPtr.Zero ? PreviewHandlerRules.WaitingLook.NoWindow
                    : hung ? PreviewHandlerRules.WaitingLook.Hung
                    : PreviewHandlerRules.SameWaitingWindow(hwnd, pid, StartTimeUtc(pid), _lastHwnd, _lastPid, _lastTimeUtc)
                        ? PreviewHandlerRules.WaitingLook.BroughtBefore
                        : PreviewHandlerRules.WaitingLook.Found;
                var step = PreviewHandlerRules.WaitingRetryStep(look, InputIdleMs(), AnyInputDown(), _retryTicks);
                if (step == PreviewHandlerRules.RetryStep.LookAgain) return;
                StopRetry();
                // Refused by Windows: it was flashed already, and is not tried again.
                if (step == PreviewHandlerRules.RetryStep.BringForwardAndStop) TryBringForward(hwnd, pid);
            }
        }
        catch
        {
            StopRetry();
        }
        finally
        {
            _inTick = false;
        }
    }

    /// <summary>Stops the looks again; the timer is kept for the next time. Never throws.</summary>
    private static void StopRetry()
    {
        _retryName = null;
        try { _retryTimer?.Stop(); } catch { }
    }

    /// <summary>Flashes the window's taskbar button until it comes to the front.</summary>
    private static void Flash(IntPtr hwnd)
    {
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fi);
    }

    /// <summary>Milliseconds since the user last pressed a key or moved or clicked the mouse; 0 when Windows cannot say.</summary>
    private static uint InputIdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? PreviewHandlerRules.InputIdleMs(unchecked((uint)Environment.TickCount), info.dwTime) : 0;
    }

    /// <summary>A key or mouse button is held down right now.</summary>
    private static bool AnyInputDown()
    {
        for (int vk = 0x01; vk <= 0xFE; vk++)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    /// <summary>When the process started, in UTC, or null when it cannot be asked (gone, or not ours to ask).</summary>
    private static DateTime? StartTimeUtc(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct Top(IntPtr Hwnd, uint Pid, IntPtr Owner, bool Visible, bool Enabled, bool Tool);

    /// <summary>The best-ranked waiting message among the processes' top-level windows and its process, or zero.</summary>
    private static IntPtr Find(HashSet<uint> pids, out uint bestPid)
    {
        var tops = new List<Top>();
        int seen = 0;
        EnumWindows((h, _) =>
        {
            if (++seen > MaxWindows) return false;
            GetWindowThreadProcessId(h, out uint pid);
            if (pids.Contains(pid))
            {
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                tops.Add(new Top(h, pid, GetWindow(h, GW_OWNER), IsWindowVisible(h), IsWindowEnabled(h), (ex & WS_EX_TOOLWINDOW) != 0));
            }
            return true;
        }, IntPtr.Zero);

        // Each process's main windows: visible, unowned, not tool windows, enabled or not (a disabled one is a program
        // showing its own box to the user). Counted per process so each candidate can ask whether its process shows
        // something other than itself (at most two are needed to answer that).
        var visibleMains = new Dictionary<uint, List<IntPtr>>();
        foreach (var t in tops)
        {
            if (!t.Visible || t.Owner != IntPtr.Zero || t.Tool) continue;
            Remember(visibleMains, t.Pid, t.Hwnd);
        }

        IntPtr best = IntPtr.Zero;
        bestPid = 0;
        int bestRank = 0;
        var cls = new StringBuilder(256);
        foreach (var t in tops)
        {
            if (!t.Visible || !t.Enabled) continue;
            cls.Clear();
            string? className = GetClassName(t.Hwnd, cls, cls.Capacity) > 0 ? cls.ToString() : null;
            var facts = new PreviewHandlerRules.WindowFacts(
                className, t.Visible, t.Enabled, t.Owner != IntPtr.Zero, HasOther(visibleMains, t.Pid, t.Hwnd));
            int rank = PreviewHandlerRules.WaitingDialogRank(facts);
            if (rank > bestRank) { best = t.Hwnd; bestPid = t.Pid; bestRank = rank; }
        }
        return best;
    }

    private static void Remember(Dictionary<uint, List<IntPtr>> map, uint pid, IntPtr hwnd)
    {
        if (!map.TryGetValue(pid, out var list)) map[pid] = list = new List<IntPtr>(2);
        if (list.Count < 2) list.Add(hwnd);
    }

    private static bool HasOther(Dictionary<uint, List<IntPtr>> map, uint pid, IntPtr self)
    {
        if (!map.TryGetValue(pid, out var list)) return false;
        foreach (var h in list) if (h != self) return true;
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int SW_RESTORE = 9;
    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
}
