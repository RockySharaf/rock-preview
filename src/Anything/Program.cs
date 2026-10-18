using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Anything.Interop;

namespace Anything;

static class Program
{
    public const string MutexName = @"Local\Rock Preview.Search.SingleInstance";
    public const string MessageWindowTitle = "Rock Preview.Search.MessageWindow";
    public const int CopyDataMagic = 0x414E5954; // "ANYT"

    [STAThread]
    static int Main(string[] args)
    {
        // The single-file build is also the installer: "RockPreview-Setup.exe", -install, -uninstall.
        if (Setup.IsSetupLaunch(args)) return Setup.Run(args);

        var cmd = CommandLine.Parse(args);
        string suffix = cmd.Demo ? ".Demo" : "";

        if (cmd.Admin && !IsElevated())
        {
            // Relaunch elevated; the new process waits for this instance (and any running one) to go away.
            var relaunch = cmd.ToArgs();
            relaunch.Add("-restarted");
            if (RelaunchElevated(relaunch))
            {
                IntPtr existing = FindInstance(suffix, 0);
                if (existing != IntPtr.Zero) Forward(existing, cmd.Demo ? ["--demo", "-exit"] : ["-exit"]);
                return 0;
            }
            // UAC cancelled: continue unelevated.
        }

        using var mutex = new Mutex(false, MutexName + suffix);
        // A restarted copy waits for the previous one to save and stop its indexer (can take a while mid-scan).
        bool owned = TryAcquire(mutex, cmd.Restarted ? 60000 : 0);
        if (!owned)
        {
            IntPtr hwnd = FindInstance(suffix, 3000);
            if (hwnd != IntPtr.Zero) Forward(hwnd, cmd.ToArgs());
            return 0;
        }
        try
        {
            if (cmd.Exit) return 0;

            ApplicationConfiguration.Initialize();
            AppSettings.MigrateFromAnything(cmd.Demo); // the app used to be called "Anything"
            // Theme must be chosen before any window exists; the full settings load happens in AnythingApp.Create.
            Application.SetColorMode(AppSettings.ColorModeFor(AppSettings.Load(AppSettings.DefaultPath(cmd.Demo)).Theme));
            ToolStripManager.Renderer = new ExplorerRenderer(); // command bar, status bar and every menu look like File Explorer's
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportError(e.Exception, fatal: false);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ReportError(ex, fatal: true); };
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var app = AnythingApp.Create(cmd);
            if (app is null) return 1;
            Application.Run(app);
            return 0;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    static int s_reporting;

    /// <summary>Logs to %LOCALAPPDATA%\Rock Preview\error.log and tells the user; UI-thread errors are survivable.</summary>
    static void ReportError(Exception ex, bool fatal)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rock Preview");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] {(fatal ? "FATAL " : "")}{ex}\r\n\r\n");
        }
        catch { }
        if (Interlocked.Exchange(ref s_reporting, 1) != 0) return;
        try
        {
            MessageBox.Show($"An unexpected error occurred{(fatal ? " and Rock Preview must close" : "")}.\n\n{ex.Message}\n\nDetails were written to %LOCALAPPDATA%\\Rock Preview\\error.log.",
                "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Volatile.Write(ref s_reporting, 0); }
    }

    static bool TryAcquire(Mutex m, int timeoutMs)
    {
        try { return m.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; }
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Starts this executable again with the current rights (an elevated instance stays elevated, no UAC prompt).</summary>
    public static bool Relaunch(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, CommandLine.Quote(args))
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    /// <summary>Starts this executable elevated. Returns false when the UAC prompt was cancelled or failed.</summary>
    public static bool RelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, CommandLine.Quote(args))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    static IntPtr FindInstance(string suffix, int waitMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            IntPtr h = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, MessageWindowTitle + suffix);
            if (h != IntPtr.Zero || sw.ElapsedMilliseconds >= waitMs) return h;
            Thread.Sleep(100);
        }
    }

    /// <summary>Sends a command line to the running Rock Preview, if there is one (setup uses it to close the app).</summary>
    public static bool ForwardToRunning(IReadOnlyList<string> args)
    {
        IntPtr hwnd = FindInstance("", 0);
        if (hwnd == IntPtr.Zero) return false;
        Forward(hwnd, args);
        return true;
    }

    /// <summary>Sends the command line to the running instance's message window via WM_COPYDATA.</summary>
    static void Forward(IntPtr hwnd, IReadOnlyList<string> args)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        Native.AllowSetForegroundWindow(pid);
        byte[] bytes = Encoding.Unicode.GetBytes(string.Join('\0', args));
        IntPtr mem = Marshal.AllocHGlobal(bytes.Length + 2);
        try
        {
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            Marshal.WriteInt16(mem, bytes.Length, 0);
            var cds = new Native.COPYDATASTRUCT { dwData = CopyDataMagic, cbData = bytes.Length, lpData = mem };
            Native.SendMessage(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }
}
