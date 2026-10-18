using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// Rock Preview installs itself: the single-file build is also its installer. Started as "RockPreview-Setup.exe"
/// (or with -install) it shows a setup window and copies itself to Program Files (everyone) or
/// %LOCALAPPDATA%\Programs (just me), adds Start menu / desktop shortcuts (optionally "Run as administrator"), the
/// start-with-Windows entry and an entry in Settings &gt; Apps, whose Uninstall runs "RockPreview.exe -uninstall".
/// Switches: -install, -uninstall, -quiet, -allusers, -dir &lt;folder&gt;, -desktop, -runasadmin, -startwithwindows,
/// -launch, -removedata (uninstall).
/// </summary>
internal static class Setup
{
    public const string AppName = "Rock Preview";
    public const string ExeName = "RockPreview.exe";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RockPreview";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Rock Preview";

    /// <summary>What to install and how.</summary>
    internal sealed class Options
    {
        public bool AllUsers;
        public string? Folder;
        public bool DesktopShortcut;
        public bool RunAsAdmin;
        public bool StartWithWindows;
        public bool Launch;
        public bool Quiet;
        public bool RemoveData;
        public bool Auto;   // start installing at once (the elevated copy started for an install for everyone)

        public string TargetFolder => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder(AllUsers) : Folder!;
        public string TargetExe => Path.Combine(TargetFolder, ExeName);

        public List<string> ToArgs()
        {
            var l = new List<string> { "-install" };
            if (AllUsers) l.Add("-allusers");
            if (!string.IsNullOrWhiteSpace(Folder)) { l.Add("-dir"); l.Add(Folder!); }
            if (DesktopShortcut) l.Add("-desktop");
            if (RunAsAdmin) l.Add("-runasadmin");
            if (StartWithWindows) l.Add("-startwithwindows");
            if (Launch) l.Add("-launch");
            if (Quiet) l.Add("-quiet");
            if (Auto) l.Add("-auto");
            return l;
        }
    }

    /// <summary>An existing installation (from its Settings &gt; Apps entry).</summary>
    internal sealed record Installed(bool AllUsers, string Folder, string Version);

    public static string DefaultFolder(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string Version
    {
        get
        {
            var v = typeof(Setup).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>True when this process should run setup or uninstall instead of the app.</summary>
    public static bool IsSetupLaunch(IReadOnlyList<string> args)
    {
        if (HasSwitch(args, "install") || HasSwitch(args, "uninstall")) return true;
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("install", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasSwitch(IReadOnlyList<string> args, string name)
        => args.Any(a => a.Length > 1 && (a[0] == '-' || a[0] == '/') && a.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    static Options ParseOptions(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.Length < 2 || (a[0] != '-' && a[0] != '/')) continue;
            switch (a.TrimStart('-', '/').ToLowerInvariant())
            {
                case "allusers": o.AllUsers = true; break;
                case "dir" when i + 1 < args.Count: o.Folder = args[++i]; break;
                case "desktop": o.DesktopShortcut = true; break;
                case "runasadmin": o.RunAsAdmin = true; break;
                case "startwithwindows": o.StartWithWindows = true; break;
                case "launch": o.Launch = true; break;
                case "quiet": o.Quiet = true; break;
                case "removedata": o.RemoveData = true; break;
                case "auto": o.Auto = true; break;
            }
        }
        return o;
    }

    /// <summary>Runs setup or uninstall; returns the process exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        var o = ParseOptions(args);
        if (!o.Quiet) ApplicationConfiguration.Initialize();
        try
        {
            if (HasSwitch(args, "uninstall")) return RunUninstall(o);
            if (o.Quiet)
            {
                if (o.AllUsers && !Program.IsElevated()) return Program.RelaunchElevated(o.ToArgs()) ? 0 : 5;
                Install(o, _ => { });
                if (o.Launch) LaunchInstalled(o);
                return 0;
            }
            Application.Run(new SetupForm(o));
            return 0;
        }
        catch (Exception ex) when (o.Quiet)
        {
            Log(ex);
            return 1;
        }
    }

    // ---- install ------------------------------------------------------------------------------

    /// <summary>Copies the program and registers it. Throws with a user-readable message on failure.</summary>
    public static void Install(Options o, Action<string> status)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the setup program.");
        string folder = o.TargetFolder, exe = o.TargetExe;

        status("Closing Rock Preview...");
        CloseRunning(exe, o.Quiet);

        status("Copying Rock Preview...");
        Directory.CreateDirectory(folder);
        if (!SamePath(source, exe)) CopyWithRetry(source, exe);

        status("Adding shortcuts...");
        string startMenu = Path.Combine(Environment.GetFolderPath(o.AllUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), AppName + ".lnk");
        string desktop = Path.Combine(Environment.GetFolderPath(o.AllUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        CreateShortcut(startMenu, exe, o.RunAsAdmin);
        if (o.DesktopShortcut) CreateShortcut(desktop, exe, o.RunAsAdmin);
        else TryDelete(desktop);

        status("Registering...");
        using (var key = (o.AllUsers ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(UninstallKeyPath))
        {
            string quoted = "\"" + exe + "\"";
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", folder);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", quoted + " -uninstall");
            key.SetValue("QuietUninstallString", quoted + " -uninstall -quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("RunAsAdministrator", o.RunAsAdmin ? 1 : 0, RegistryValueKind.DWord);
        }
        // Start with Windows (the same per-user entry Rock Preview's Options page uses).
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (o.StartWithWindows) run.SetValue(RunValue, "\"" + exe + "\" -startup");
            else if (run.GetValue(RunValue) is string old && PointsInto(old, folder)) run.DeleteValue(RunValue, false);
        }
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        status("Rock Preview is installed.");
    }

    /// <summary>Opens the installed copy: elevated when chosen, otherwise with normal rights even from an elevated setup.</summary>
    public static void LaunchInstalled(Options o)
    {
        try
        {
            if (o.RunAsAdmin)
                Process.Start(new ProcessStartInfo(o.TargetExe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = o.TargetFolder })?.Dispose();
            else if (Program.IsElevated())
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + o.TargetExe + "\"") { UseShellExecute = false })?.Dispose(); // Explorer starts it unelevated
            else
                Process.Start(new ProcessStartInfo(o.TargetExe) { UseShellExecute = true, WorkingDirectory = o.TargetFolder })?.Dispose();
        }
        catch (Win32Exception) { /* UAC cancelled */ }
    }

    public static Installed? FindInstalled()
    {
        foreach (var (root, allUsers) in new[] { (Registry.CurrentUser, false), (Registry.LocalMachine, true) })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallKeyPath);
                if (key?.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, ExeName)))
                    return new Installed(allUsers, folder, key.GetValue("DisplayVersion") as string ?? "");
            }
            catch (Exception) { }
        }
        return null;
    }

    // ---- uninstall ----------------------------------------------------------------------------

    static int RunUninstall(Options o)
    {
        var inst = FindInstalled();
        string folder = inst?.Folder ?? Path.GetDirectoryName(Environment.ProcessPath)!;
        bool allUsers = inst?.AllUsers ?? false;
        if (allUsers && !Program.IsElevated())
        {
            // Program Files and the machine-wide entry need administrator rights.
            var args = new List<string> { "-uninstall" };
            if (o.Quiet) args.Add("-quiet");
            if (o.RemoveData) args.Add("-removedata");
            return Program.RelaunchElevated(args) ? 0 : 5;
        }

        bool removeData = o.RemoveData;
        if (!o.Quiet)
        {
            var check = new TaskDialogVerificationCheckBox("Also delete my Rock Preview settings and search index");
            var uninstall = new TaskDialogButton("Uninstall");
            var page = new TaskDialogPage
            {
                Caption = AppName + " Setup",
                Heading = "Uninstall Rock Preview?",
                Text = $"Rock Preview will be removed from this PC ({folder}). Your files are not affected.",
                Icon = TaskDialogIcon.Information,
                Buttons = { uninstall, TaskDialogButton.Cancel },
                Verification = check,
                DefaultButton = TaskDialogButton.Cancel,
            };
            if (TaskDialog.ShowDialog(page) != uninstall) return 2;
            removeData = check.Checked;
        }

        try
        {
            Uninstall(folder, allUsers, removeData, o.Quiet);
        }
        catch (Exception ex)
        {
            Log(ex);
            if (!o.Quiet) TaskDialog.ShowDialog(new TaskDialogPage { Caption = AppName + " Setup", Heading = "Rock Preview could not be completely removed", Text = ex.Message, Icon = TaskDialogIcon.Error });
            return 1;
        }
        if (!o.Quiet)
            TaskDialog.ShowDialog(new TaskDialogPage { Caption = AppName + " Setup", Heading = "Rock Preview was uninstalled.", Icon = TaskDialogIcon.ShieldSuccessGreenBar });
        return 0;
    }

    static void Uninstall(string folder, bool allUsers, bool removeData, bool quiet)
    {
        string exe = Path.Combine(folder, ExeName);
        CloseRunning(exe, quiet);

        foreach (var special in allUsers
                     ? new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }
                     : new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        {
            string lnk = Path.Combine(Environment.GetFolderPath(special), AppName + ".lnk");
            if (File.Exists(lnk) && ShortcutTarget(lnk) is { } target && SamePath(target, exe)) TryDelete(lnk);
        }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(RunValue) is string cmd && PointsInto(cmd, folder)) run.DeleteValue(RunValue, false);
        }
        catch (Exception) { }
        (allUsers ? Registry.LocalMachine : Registry.CurrentUser).DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        if (removeData)
        {
            // Only Rock Preview's own data folders ("Rock Preview", with the space): settings, index, logs.
            TryDeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
            TryDeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
        }

        // The program file: delete it now, or (when it is the uninstaller that is running) right after this process
        // ends. The folder is removed only if that leaves it empty.
        string? self = Environment.ProcessPath;
        if (File.Exists(exe) && !SamePath(self ?? "", exe)) TryDelete(exe);
        if (File.Exists(exe))
        {
            string script = $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\" & rmdir \"{folder}\"";
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), script)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
        else
        {
            try { Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Asks a Rock Preview started from <paramref name="exe"/> to save and exit (it accepts that even when elevated),
    /// then waits. Copies started from elsewhere (e.g. a portable copy) are left alone.
    /// </summary>
    static void CloseRunning(string exe, bool force)
    {
        int self = Environment.ProcessId;
        List<Process> Running() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
            .Where(p => p.Id != self && SamePath(ImagePath(p) ?? "", exe)).ToList();
        var running = Running();
        if (running.Count == 0) return;
        Program.ForwardToRunning(["-exit"]);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 60_000)
        {
            Thread.Sleep(250);
            running = Running();
            if (running.Count == 0) return;
        }
        if (!force) throw new InvalidOperationException("Rock Preview is still running. Close it and try again.");
        foreach (var p in running) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
    }

    static string? ImagePath(Process p)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string tmp = to + ".new";
                File.Copy(from, tmp, overwrite: true);
                File.Move(tmp, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 40) { Thread.Sleep(250); } // the old copy is still letting go of the file
        }
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    static bool PointsInto(string command, string folder)
        => command.Contains(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Rock Preview Setup");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "setup.log"), $"[{DateTime.Now:O}] {ex}\r\n\r\n");
        }
        catch { }
    }

    /// <summary>The hotkey Rock Preview uses for this user (Alt+X unless changed in Options), or null when it's turned off.</summary>
    public static string? ConfiguredHotkey()
    {
        try
        {
            string h = AppSettings.Load(AppSettings.DefaultPath(demo: false)).Hotkey;
            return h.Length > 0 ? h : null;
        }
        catch (Exception) { return AppSettings.DefaultHotkey; }
    }

    /// <summary>Whether this user has a Start with Windows entry for Rock Preview.</summary>
    public static bool StartsWithWindows()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return run?.GetValue(RunValue) is string;
        }
        catch (Exception) { return false; }
    }

    // ---- shortcuts (IShellLinkW) -----------------------------------------------------------------

    const uint SLDF_RUNAS_USER = 0x2000;

    static void CreateShortcut(string lnk, string exe, bool runAsAdmin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription(ConfiguredHotkey() is { } hotkey
                ? $"Search, browse and preview every file instantly. Press {hotkey} to open it from anywhere."
                : "Search, browse and preview every file instantly.");
            link.SetIconLocation(exe, 0);
            if (link is IShellLinkDataList data && data.GetFlags(out uint flags) >= 0)
                data.SetFlags(runAsAdmin ? flags | SLDF_RUNAS_USER : flags & ~SLDF_RUNAS_USER);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    static string? ShortcutTarget(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch (Exception) { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkDataList
    {
        [PreserveSig] int AddDataBlock(IntPtr pDataBlock);
        [PreserveSig] int CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        [PreserveSig] int RemoveDataBlock(uint dwSig);
        [PreserveSig] int GetFlags(out uint pdwFlags);
        [PreserveSig] int SetFlags(uint dwFlags);
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
