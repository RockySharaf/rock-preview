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
/// -replaceexplorer, -indexservice, -updatecheck / -noupdatecheck, -launch, -removedata (uninstall).
/// </summary>
internal static class Setup
{
    public const string AppName = "Ultimate File Explorer";
    /// <summary>The settings and index folders keep the earlier name, so updating keeps them.</summary>
    public const string DataFolderName = "Rock Preview";
    /// <summary>Shortcuts named before the rename; replaced by ones named AppName.</summary>
    public const string OldShortcutName = "Rock Preview";
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
        public bool ReplaceExplorer;  // Rock Preview instead of File Explorer (off unless chosen)
        public bool IndexService;     // instant indexing through the Rock Preview service (Windows asks once)
        public bool? CheckForUpdates; // tell the user about new versions; null = leave their choice as it is
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
            if (ReplaceExplorer) l.Add("-replaceexplorer");
            if (IndexService) l.Add("-indexservice");
            if (CheckForUpdates is bool check) l.Add(check ? "-updatecheck" : "-noupdatecheck");
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
                case "replaceexplorer": o.ReplaceExplorer = true; break;
                case "indexservice": o.IndexService = true; break;
                case "updatecheck": o.CheckForUpdates = true; break;
                case "noupdatecheck": o.CheckForUpdates = false; break;
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

        status("Closing Ultimate File Explorer...");
        CloseRunning(exe, o.Quiet, anyFolder: true);

        status("Copying Ultimate File Explorer...");
        Directory.CreateDirectory(folder);
        if (!SamePath(source, exe)) CopyWithRetry(source, exe);

        status("Adding shortcuts...");
        string startMenu = Path.Combine(Environment.GetFolderPath(o.AllUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), AppName + ".lnk");
        string desktop = Path.Combine(Environment.GetFolderPath(o.AllUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        CreateShortcut(startMenu, exe, o.RunAsAdmin);
        if (o.DesktopShortcut) CreateShortcut(desktop, exe, o.RunAsAdmin);
        else TryDelete(desktop);
        foreach (var old in new[] { startMenu, desktop }.Select(l => Path.Combine(Path.GetDirectoryName(l)!, OldShortcutName + ".lnk")))
            if (File.Exists(old) && ShortcutTarget(old) is { } target && SamePath(target, exe)) TryDelete(old);

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
        // New-version notices: the choice made in the setup window (a quiet install without the switch leaves it alone).
        if (o.CheckForUpdates is bool check) SetCheckForUpdates(check);
        // Rock Preview instead of File Explorer: on when chosen (now pointing at this copy); unticked in the setup
        // window turns it off. A quiet install without the switch leaves the user's choice alone.
        try
        {
            if (o.ReplaceExplorer) ExplorerReplacement.Enable(exe);
            else if (!o.Quiet && ExplorerReplacement.IsEnabled()) ExplorerReplacement.Disable();
        }
        catch (Exception) { }
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        // Instant indexing: the Rock Preview service, installed once (Windows asks for permission unless setup already runs
        // as administrator). An existing service is kept unless its own code changed (IndexService.ServiceVersion).
        bool updateService = global::Anything.IndexService.NeedsUpdate();
        if ((o.IndexService && !global::Anything.IndexService.IsInstalled()) || updateService)
        {
            status(updateService ? "Updating the instant-indexing service..." : "Turning on instant indexing...");
            try
            {
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                if (Program.IsElevated()) global::Anything.IndexService.Install(exe, user);
                else Program.RunElevatedAndWait(["-installservice", "-for", user.Value]);
            }
            catch (Exception ex) { Log(ex); }
        }
        status("Ultimate File Explorer is installed.");
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
            var check = new TaskDialogVerificationCheckBox("Also delete my Ultimate File Explorer settings and search index");
            var uninstall = new TaskDialogButton("Uninstall");
            var page = new TaskDialogPage
            {
                Caption = AppName + " Setup",
                Heading = "Uninstall Ultimate File Explorer?",
                Text = $"Ultimate File Explorer will be removed from this PC ({folder}). Your files are not affected.",
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
            if (!o.Quiet) TaskDialog.ShowDialog(new TaskDialogPage { Caption = AppName + " Setup", Heading = "Ultimate File Explorer could not be completely removed", Text = ex.Message, Icon = TaskDialogIcon.Error });
            return 1;
        }
        if (!o.Quiet)
            TaskDialog.ShowDialog(new TaskDialogPage { Caption = AppName + " Setup", Heading = "Ultimate File Explorer was uninstalled.", Icon = TaskDialogIcon.ShieldSuccessGreenBar });
        return 0;
    }

    static void Uninstall(string folder, bool allUsers, bool removeData, bool quiet)
    {
        string exe = Path.Combine(folder, ExeName);
        CloseRunning(exe, quiet, anyFolder: false);

        foreach (var special in allUsers
                     ? new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory }
                     : new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        {
            foreach (string name in new[] { AppName, OldShortcutName })
            {
                string lnk = Path.Combine(Environment.GetFolderPath(special), name + ".lnk");
                if (File.Exists(lnk) && ShortcutTarget(lnk) is { } target && SamePath(target, exe)) TryDelete(lnk);
            }
        }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(RunValue) is string cmd && PointsInto(cmd, folder)) run.DeleteValue(RunValue, false);
        }
        catch (Exception) { }
        // Remove the instant-indexing service (Windows asks for permission unless this already runs as administrator).
        try
        {
            if (global::Anything.IndexService.IsInstalled())
            {
                if (Program.IsElevated()) global::Anything.IndexService.Uninstall();
                else Program.RunElevatedAndWait(["-uninstallservice"]);
            }
        }
        catch (Exception) { }
        // Hand folders and Win+E back to File Explorer if this copy had them.
        try
        {
            if (ExplorerReplacement.RegisteredExe() is { } registered && PointsInto(registered, folder)) ExplorerReplacement.Disable();
        }
        catch (Exception) { }
        (allUsers ? Registry.LocalMachine : Registry.CurrentUser).DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        if (removeData)
        {
            // Only its own data folders ("Rock Preview", with the space, the name before the rename): settings, index, logs.
            TryDeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName));
            TryDeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName));
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
    /// Asks the running copy to save and exit (it accepts that even when elevated), then waits; a quiet install ends a
    /// copy that doesn't. Installing closes the copy that runs from any folder (a portable or older copy): only one runs
    /// at a time, so one left open would answer instead of the new one. Uninstalling (<paramref name="anyFolder"/>
    /// false) closes only a copy started from <paramref name="exe"/>. A copy counts as closed when its process has ended,
    /// not when its window goes (it still saves the index then, and holds the single-instance mutex).
    /// </summary>
    static void CloseRunning(string exe, bool force, bool anyFolder)
    {
        int self = Environment.ProcessId;
        var seen = new Dictionary<int, Process>();
        List<Process> Running()
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
                if (p.Id != self && SamePath(ImagePath(p) ?? "", exe)) seen.TryAdd(p.Id, p);
            if (anyFolder && Program.RunningInstanceId() is int pid && pid != self && !seen.ContainsKey(pid))
            {
                try { seen.Add(pid, Process.GetProcessById(pid)); } catch (ArgumentException) { /* just exited */ }
            }
            return seen.Values.Where(IsAlive).ToList();
        }
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
        if (!force) throw new InvalidOperationException("Ultimate File Explorer is still running. Close it and try again.");
        foreach (var p in running) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
    }

    static bool IsAlive(Process p)
    {
        try { return !p.HasExited; }
        catch (Exception)
        {
            // No right to wait on it (e.g. it runs as administrator): it is alive while its process ID still exists.
            try { using var again = Process.GetProcessById(p.Id); return true; }
            catch (ArgumentException) { return false; }
        }
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

    /// <summary>Whether Rock Preview tells this user about new versions (on unless turned off).</summary>
    public static bool ChecksForUpdates()
    {
        try { return AppSettings.Load(AppSettings.DefaultPath(demo: false)).CheckForUpdates; }
        catch (Exception) { return true; }
    }

    static void SetCheckForUpdates(bool on)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath(demo: false));
            if (settings.CheckForUpdates == on) return;
            settings.CheckForUpdates = on;
            settings.Save();
        }
        catch (Exception ex) { Log(ex); }
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

    // ---- shortcuts --------------------------------------------------------------------------------

    static void CreateShortcut(string lnk, string exe, bool runAsAdmin) =>
        Shortcuts.Create(lnk, exe, "", ConfiguredHotkey() is { } hotkey
            ? $"Search, browse and preview every file instantly. Press {hotkey} to open it from anywhere."
            : "Search, browse and preview every file instantly.", runAsAdmin);

    static string? ShortcutTarget(string lnk) => Shortcuts.Read(lnk)?.Path;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
