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
        // The Rock Preview service and its one-time install (IndexService): no windows.
        if (args.Length > 0 && args[0].TrimStart('-', '/').Equals("service", StringComparison.OrdinalIgnoreCase)) return IndexService.RunAsService();
        if (args.Any(a => a.TrimStart('-', '/').Equals("installservice", StringComparison.OrdinalIgnoreCase))) return InstallServiceCommand(args);
        if (args.Any(a => a.TrimStart('-', '/').Equals("uninstallservice", StringComparison.OrdinalIgnoreCase))) return UninstallServiceCommand();

        // The single-file build is also the installer: "RockPreview-Setup.exe", -install, -uninstall.
        if (Setup.IsSetupLaunch(args)) return Setup.Run(args);
        Shortcuts.UseAppIdForThisProcess(); // one taskbar button for all windows, matching Rock Preview's shortcuts

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
            MessageBox.Show($"An unexpected error occurred{(fatal ? " and Ultimate File Explorer must close" : "")}.\n\n{ex.Message}\n\nDetails were written to %LOCALAPPDATA%\\Rock Preview\\error.log.",
                "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>
    /// "-installservice -for SID": installs the Rock Preview service for that user (run elevated, from the app or Setup,
    /// after Windows asked the user once). Returns 0 on success.
    /// </summary>
    static int InstallServiceCommand(string[] args)
    {
        if (!IsElevated()) return 5; // ERROR_ACCESS_DENIED
        int i = Array.FindIndex(args, a => a.TrimStart('-', '/').Equals("for", StringComparison.OrdinalIgnoreCase));
        try
        {
            var user = i >= 0 && i + 1 < args.Length ? new System.Security.Principal.SecurityIdentifier(args[i + 1]) : WindowsIdentity.GetCurrent().User!;
            IndexService.Install(Environment.ProcessPath!, user);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ultimate File Explorer couldn't install its service.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int UninstallServiceCommand()
    {
        if (!IsElevated()) return 5;
        try { IndexService.Uninstall(); return 0; }
        catch (Exception ex)
        {
            MessageBox.Show($"Ultimate File Explorer couldn't remove its service.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Runs this executable elevated and waits for it: its exit code, or -1 when the UAC prompt was declined.</summary>
    public static int RunElevatedAndWait(IEnumerable<string> args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, CommandLine.Quote(args))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!,
            });
            if (p == null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception) { return -1; }
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

    /// <summary>The process ID of the running copy (whatever its folder or file name), or null when none runs.</summary>
    public static int? RunningInstanceId()
    {
        IntPtr hwnd = FindInstance("", 0);
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
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
