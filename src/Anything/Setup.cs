using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Anything.Core;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// Rock Preview installs itself: the single-file build is also its installer. Started as "RockPreview-Setup.exe"
/// (or with -install) it shows a setup window and copies itself to Program Files (everyone) or
/// %LOCALAPPDATA%\Programs (just me), adds Start menu / desktop shortcuts (optionally "Run as administrator"), the
/// start-with-Windows entry and an entry in Settings &gt; Apps, whose Uninstall runs "RockPreview.exe -uninstall".
/// Switches: -install, -uninstall, -quiet, -allusers, -dir &lt;folder&gt;, -desktop, -runasadmin, -startwithwindows,
/// -replaceexplorer, -indexservice, -updatecheck / -noupdatecheck, -content names|documents|pictures (search inside
/// files), -asuser &lt;SID&gt; (whose choice it is), -launch, -update [-launch] (the app's Update now: the installed copy
/// updated in place, every choice kept, <see cref="RunUpdate"/>), and for -uninstall: -removedata, -confirmed with -dir (already
/// asked about that copy, in Settings › About), -portable with those (Settings › About's Remove from this PC: a copy that
/// isn't installed, whose program stays) and -ownpartdone with -parent &lt;pid&gt; (the user's own part is done: the
/// elevated half of an uninstall for everyone, which that process waits for).
/// </summary>
internal static partial class Setup
{
    public const string AppName = "Rock Preview";
    /// <summary>The settings and index folders: named Rock Preview all along, so updating keeps them.</summary>
    public const string DataFolderName = "Rock Preview";
    /// <summary>
    /// The app's name from 1.5 to 1.7.4: its shortcuts and the folder it installed into by itself, which are replaced by
    /// ones named AppName.
    /// </summary>
    public const string OldShortcutName = "Ultimate File Explorer";
    public const string ExeName = "RockPreview.exe";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RockPreview";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Rock Preview";
    /// <summary>Task Manager's on/off state for each start-with-Windows entry.</summary>
    const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    /// <summary>Start-with-Windows names: today's, and early versions' "RockPreview" (from Program Files, --minimized).</summary>
    static readonly string[] RunValues = [RunValue, "RockPreview"];

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
        public Anything.Core.Content.ContentMode? Content; // search inside files; null = leave the user's choice as it is
        public string? AsUser; // the SID of the user who chose in the setup window: an elevated copy writes their choices only for them
        public bool Launch;
        public bool Quiet;
        public bool RemoveData;
        public bool Confirmed; // uninstall: the user already said yes (Settings › About), so don't ask again
        public bool Portable;  // uninstall, with Confirmed: this copy isn't installed; what it set up goes, its program stays
        public bool OwnPartDone; // uninstall for everyone: the user's own registry part was done before elevating
        public int ParentPid;    // with OwnPartDone: the copy without rights that waits for this one
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
            if (Content is { } content) { l.Add("-content"); l.Add(Anything.Core.Content.ContentSettings.ModeName(content)); }
            if (!string.IsNullOrEmpty(AsUser)) { l.Add("-asuser"); l.Add(AsUser!); }
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

    /// <summary>The folder versions 1.5 to 1.7.6 installed into by themselves, named after the app's name then.</summary>
    static string OldDefaultFolder(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), OldShortcutName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", OldShortcutName);

    /// <summary>
    /// Where an update or a setup puts an existing installation: its own folder, unless that is the Ultimate File
    /// Explorer folder an older version chose by itself for just this user, which moves to the Rock Preview one. One for
    /// everyone stays: the PC's other accounts may open their folders or start Windows with the program there.
    /// </summary>
    public static string FolderFor(Installed inst) =>
        !inst.AllUsers && SamePath(inst.Folder, OldDefaultFolder(allUsers: false)) ? DefaultFolder(allUsers: false) : inst.Folder;

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
        if (HasSwitch(args, "install") || HasSwitch(args, "uninstall") || HasSwitch(args, "update")) return true;
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
                case "content" when i + 1 < args.Count: o.Content = ParseContentMode(args[++i]); break;
                case "asuser" when i + 1 < args.Count: o.AsUser = args[++i]; break;
                case "launch": o.Launch = true; break;
                case "quiet": o.Quiet = true; break;
                case "removedata": o.RemoveData = true; break;
                case "confirmed": o.Confirmed = true; break;
                case "portable": o.Portable = true; break;
                case "ownpartdone": o.OwnPartDone = true; break;
                case "parent" when i + 1 < args.Count: _ = int.TryParse(args[++i], out o.ParentPid); break;
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
            if (HasSwitch(args, "update")) return RunUpdate(o);
            if (o.Quiet)
            {
                if (o.AllUsers && !Program.IsElevated())
                {
                    // Whoever's password elevates, the choices (search inside files) belong to the user who ran it.
                    o.AsUser ??= CurrentUserSid();
                    return Program.RelaunchElevated(o.ToArgs()) ? 0 : 5;
                }
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
        var older = OlderCopies(o); // before this install's Settings › Apps entry takes the place of theirs

        status("Closing Rock Preview...");
        CloseRunning(exe, o.Quiet, anyFolder: true, again: "install again");

        status("Copying Rock Preview...");
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
        if (o.Content is { } m) SetContentMode(m, o.AsUser);
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
        // as administrator). An older one is stopped, replaced and started again (IndexService.NeedsUpdate), so it is
        // always this release: its fixes, and its name and description in Windows' Services list. Declined, the app
        // offers it again when it starts.
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
        if (older.Count > 0)
        {
            status("Removing older copies...");
            RetireOlderCopies(o, exe, older);
        }
        status("Rock Preview is installed.");
    }

    // ---- older copies ---------------------------------------------------------------------------

    /// <summary>
    /// This user's older copies an install replaces: in the folders older versions installed into by themselves, under
    /// either name, and the one this user's Settings › Apps entry names. None when an administrator's account runs the
    /// install for someone else (its copies aren't theirs), and never a copy for everyone: the PC's other accounts may
    /// open their folders or start Windows with it, so it stays where it is (an update for everyone updates it there).
    /// </summary>
    static List<string> OlderCopies(Options o)
    {
        var found = new List<string>();
        // An install for everyone runs elevated: it knows whose it is only when told (-asuser).
        string? me = CurrentUserSid();
        bool mine = string.Equals(o.AsUser, me, StringComparison.OrdinalIgnoreCase) || (!o.AllUsers && string.IsNullOrEmpty(o.AsUser));
        if (!mine) return found;
        void Add(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder) && !SamePath(folder, o.TargetFolder) && !found.Any(f => SamePath(f, folder))) found.Add(folder);
        }
        Add(OldDefaultFolder(allUsers: false));
        Add(DefaultFolder(allUsers: false));
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            Add(key?.GetValue("InstallLocation") as string);
        }
        catch (Exception) { }
        return found;
    }

    /// <summary>
    /// Removes the older copies this install replaces (<see cref="OlderCopies"/>), leaving one that is newer than this
    /// one: folders open with this copy if they opened with it, its start with Windows starts this one, a taskbar pin
    /// opens this one, and its other shortcuts, Settings › Apps entry and program go. The settings and the index are
    /// shared by every copy, so they stay: the app checks the index when it starts and builds it again if it can't use it.
    /// </summary>
    static void RetireOlderCopies(Options o, string exe, List<string> older)
    {
        var current = System.Version.Parse(Version);
        foreach (string folder in older)
        {
            string old = Path.Combine(folder, ExeName);
            try
            {
                if (File.Exists(old))
                {
                    var v = FileVersionInfo.GetVersionInfo(old);
                    if (new System.Version(v.FileMajorPart, v.FileMinorPart, Math.Max(0, v.FileBuildPart)) > current) continue;
                    // Folders that open with it (a quiet install leaves that choice as it is) open with this copy instead;
                    // only when it is the one they open with: a verb it left behind another file manager doesn't count.
                    try { if (ExplorerReplacement.RegisteredExe() is { } registered && SamePath(registered, old)) ExplorerReplacement.Enable(exe); }
                    catch (Exception ex) { Log(ex); }
                    if (ExplorerReplacement.References(old)) continue; // its program stays while folders could still run it
                    DeleteShortcutsTo(old, Environment.SpecialFolder.Programs);
                    DeleteShortcutsTo(old, Environment.SpecialFolder.DesktopDirectory);
                    RepointTaskbarPins(old, exe, o.RunAsAdmin);
                    MoveStartupEntry(old, exe);
                    WaitUntilClosed(old, TimeSpan.FromSeconds(30)); // the copy that closed for this may still be saving
                }
                // Its Settings › Apps entry, when it still names that folder (this install's took its place otherwise).
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
                    if (key?.GetValue("InstallLocation") is string location && SamePath(location, folder))
                        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
                DeleteProgram(folder);
            }
            catch (Exception ex) { Log(ex); }
        }
    }

    /// <summary>Taskbar pins that open <paramref name="old"/> open <paramref name="exe"/> instead, staying where they are (with their arguments).</summary>
    static void RepointTaskbarPins(string old, string exe, bool runAsAdmin)
    {
        string pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        if (!Directory.Exists(pins)) return;
        foreach (string lnk in Directory.EnumerateFiles(pins, "*.lnk"))
        {
            try
            {
                if (Shortcuts.Read(lnk) is { } link && SamePath(link.Path, old))
                    Shortcuts.Create(lnk, exe, link.Arguments, ShortcutDescription(), runAsAdmin);
            }
            catch (Exception ex) { Log(ex); }
        }
    }

    /// <summary>Start with Windows for an older copy starts this one instead, under today's name, on or off as it was in Task Manager.</summary>
    static void MoveStartupEntry(string old, string exe)
    {
        var moved = RemoveStartupEntries(old).FirstOrDefault(e => UninstallRules.RunsProgram(e.Command, old));
        if (moved is null || StartsWithWindows()) return; // this install has its own entry already
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            run.SetValue(RunValue, "\"" + exe + "\" -startup");
            if (moved.Approved is { } state)
            {
                using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedRunKeyPath);
                approved.SetValue(RunValue, state, RegistryValueKind.Binary);
            }
        }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>An older copy's program (and an unfinished update's .new beside it), then its folder when nothing else is in it.</summary>
    static void DeleteProgram(string folder)
    {
        FileRetry.DeleteAside(Path.Combine(folder, ExeName));
        foreach (string file in new[] { ExeName, ExeName + ".new" }.Select(n => Path.Combine(folder, n)))
        {
            // The copy that closed for this install may take a moment to let go of its file.
            for (int i = 0; i < 20 && File.Exists(file); i++)
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (i == 19) Log(e); else Thread.Sleep(250); }
            }
        }
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- update in place (Update now) -------------------------------------------------------------

    /// <summary>
    /// -update -dir &lt;folder&gt; [-allusers] [-launch]: the app's Update now, run from the installer it downloaded (already
    /// elevated by the app for an installation for everyone). Updates the installation in that folder in place, with every
    /// choice kept as it is (shortcuts, run as administrator, start with Windows, File Explorer's place, the service,
    /// search inside files, new-version notices), then opens it again. Nothing asks (but Windows, when the instant-indexing
    /// service is updated too); should it fail, it says why and opens
    /// the copy that is installed once the one that closed for the update has gone, so the app is never left closed.
    /// </summary>
    static int RunUpdate(Options o)
    {
        // The installation the app named (an elevating administrator's own per-user copy is never the one updated);
        // without -dir, the one installed for this user, else for everyone.
        var inst = string.IsNullOrWhiteSpace(o.Folder) ? FindInstalled() : FindInstalled(o.Folder);
        if (inst is null || (o.AllUsers && !inst.AllUsers))
        {
            MessageBox.Show("Rock Preview isn't installed there any more, so there is nothing to update. Run its Setup to install it.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            // The copy that closed for this opens again, from the folder it named, when it is still there.
            if (o.Launch && !string.IsNullOrWhiteSpace(o.Folder) && File.Exists(o.TargetExe))
            {
                WaitUntilClosed(o.TargetExe, TimeSpan.FromSeconds(90));
                LaunchInstalled(new Options { AllUsers = o.AllUsers, Folder = o.Folder });
            }
            return 2;
        }
        // A copy in the Ultimate File Explorer folder an older version chose by itself moves to the Rock Preview one.
        var u = new Options
        {
            AllUsers = inst.AllUsers,
            Folder = FolderFor(inst),
            AsUser = o.AsUser, // for everyone: whose Update now it was (an administrator's account may have elevated it)
            DesktopShortcut = ShortcutExists(desktop: true, inst.AllUsers),
            RunAsAdmin = InstalledRunsAsAdmin(inst.AllUsers),
            StartWithWindows = StartsWithWindows(),
            ReplaceExplorer = ExplorerReplacement.IsEnabled(),
            Launch = o.Launch,
            Quiet = true, // a copy that doesn't close when asked is ended: the update was asked for
        };
        int code = 0;
        if (u.AllUsers && !Program.IsElevated())
        {
            // The app asks Windows before it closes; run without rights (by hand, or where Windows can't give them), it
            // stops here rather than ask again and again.
            code = 5;
            MessageBox.Show("Updating Rock Preview for everyone on this PC needs administrator rights, and Windows didn't give them. " +
                "Nothing changed: run its Setup as administrator to update it.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            try { Install(u, _ => { }); }
            catch (Exception ex)
            {
                Log(ex);
                code = 1;
                MessageBox.Show($"Rock Preview couldn't finish updating: {ex.Message}\n\n" +
                    "It opens again as it is now. Try Update now later, or get the new version from its page on GitHub.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (o.Launch)
        {
            // The copy that closed for the update may still be saving (it holds the single-instance lock until it ends):
            // one started before it has gone would hand over to it and close. When the move to the new folder didn't
            // happen, the copy that is still where it was opens.
            string was = Path.Combine(inst.Folder, ExeName);
            WaitUntilClosed(was, TimeSpan.FromSeconds(90));
            if (!SamePath(was, u.TargetExe)) WaitUntilClosed(u.TargetExe, TimeSpan.FromSeconds(30));
            if (!File.Exists(u.TargetExe) && File.Exists(was)) u.Folder = inst.Folder;
            LaunchInstalled(u);
        }
        return code;
    }

    /// <summary>Waits (never ends anything) until no copy of <paramref name="exe"/> runs in this session, or the time is up.</summary>
    static void WaitUntilClosed(string exe, TimeSpan limit)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            bool running = false;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                using (p)
                    if (p.Id != Environment.ProcessId && SessionOf(p) == session && SamePath(ImagePath(p) ?? "", exe) && IsAlive(p)) running = true;
            if (!running) return;
            Thread.Sleep(250);
        }
    }

    /// <summary>Whether the installed copy's shortcuts run it as administrator (its Settings › Apps entry says).</summary>
    static bool InstalledRunsAsAdmin(bool allUsers)
    {
        try
        {
            using var key = (allUsers ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(UninstallKeyPath);
            return key?.GetValue("RunAsAdministrator") is int on && on != 0;
        }
        catch (Exception) { return false; }
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

    /// <summary>The installed copy (just me first, then everyone); with <paramref name="folder"/>, only the one installed there.</summary>
    public static Installed? FindInstalled(string? folder = null)
    {
        foreach (var (root, allUsers) in new[] { (Registry.CurrentUser, false), (Registry.LocalMachine, true) })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallKeyPath);
                if (key?.GetValue("InstallLocation") is string location && File.Exists(Path.Combine(location, ExeName))
                    && (folder is null || SamePath(location, folder)))
                    return new Installed(allUsers, location, key.GetValue("DisplayVersion") as string ?? "");
            }
            catch (Exception) { }
        }
        return null;
    }

    // ---- uninstall ----------------------------------------------------------------------------

    static int RunUninstall(Options o)
    {
        string own = Path.GetDirectoryName(Environment.ProcessPath)!;
        // Settings › About names the copy it asked about (-dir): that one, or none (never the copy that happens to run).
        // Otherwise (Settings › Apps runs the entry's own exe) the installed copy this uninstaller is, before any other.
        bool named = o.Confirmed && !string.IsNullOrWhiteSpace(o.Folder);
        var inst = named ? FindInstalled(o.Folder) : FindInstalled(own) ?? FindInstalled();
        // Remove from this PC (-portable) is for the copy asking, which isn't installed: never another folder, and not a
        // copy installed there since, which only a whole uninstall removes.
        bool portable = o.Portable;
        if (portable && !(named && inst is null && SamePath(o.Folder!, own)))
            return Report(o, "Rock Preview was not removed from this PC", inst is not null
                ? $"It is installed in {o.Folder} now. Open Settings › About again to uninstall it."
                : "Only the copy that runs from that folder can remove itself this way.", 3);
        if (named && inst is null && !portable)
            return Report(o, "Rock Preview was not uninstalled", $"It isn't installed in {o.Folder} any more.", 3);
        string folder = inst?.Folder ?? own;
        bool allUsers = inst?.AllUsers ?? false;
        // The elevated half must have the rights it was started for (with UAC turned off, "runas" doesn't give them).
        if (o.OwnPartDone && allUsers && !Program.IsElevated())
            return Report(o, "Rock Preview was not uninstalled", "Windows didn't give administrator rights to remove it for everyone on this PC.", 5);

        // Asked here, in the user's own process, before anything changes; the elevated half of an uninstall for everyone
        // doesn't ask again.
        bool removeData = o.RemoveData;
        if (!o.Quiet && !o.Confirmed)
        {
            if (ConfirmUninstall(null, folder) is not bool delete) return 2;
            removeData = delete;
        }

        // Program Files and the machine-wide entry need administrator rights.
        if (allUsers && !Program.IsElevated()) return UninstallForEveryone(o, folder, removeData);

        Uninstalled done;
        try { done = Uninstall(o, folder, allUsers, removeData, keepProgram: portable); }
        catch (UninstallStopped stop)
        {
            Log(stop);
            return Report(o, portable ? "Rock Preview was not removed from this PC" : "Rock Preview was not uninstalled", stop.Message, 4);
        }
        catch (Exception ex) { Log(ex); return Report(o, "Rock Preview could not be completely removed", ex.Message, 1); }
        if (!o.Quiet)
            TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = AppName + " Setup",
                Heading = portable ? "Rock Preview was removed from this PC." : "Rock Preview was uninstalled.",
                Text = portable
                    ? string.Join(" ", new[]
                    {
                        done.Warning,
                        done.ServiceStays ? UninstallRules.ServiceStays : null,
                        $"{Path.GetFileName(Environment.ProcessPath) ?? ExeName} is still in {folder}: delete it whenever you like.",
                    }.OfType<string>())
                    : done.Warning,
                Icon = done.Warning is null ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Warning,
            });
        // Only now: this process holds the program file until "was uninstalled" is closed.
        if (done.DeleteLater) StartDeleteHelper(Path.Combine(folder, ExeName), folder);
        return 0;
    }

    /// <summary>
    /// An install for everyone, from a copy without administrator rights. This user's own part is done here, in their own
    /// registry: folders, drives, This PC and Win+E back to File Explorer, start with Windows, their shortcuts. The rest is
    /// done by a copy Windows elevates, which may run as another account (an administrator's password) and would clean
    /// that account's registry instead. If Windows doesn't give permission, or that copy stops before removing the
    /// program, this user's part is put back, so nothing points at a program that's gone and nothing half-changes.
    /// </summary>
    static int UninstallForEveryone(Options o, string folder, bool removeData)
    {
        string exe = Path.Combine(folder, ExeName);
        OwnPart own;
        try
        {
            CloseRunning(exe, o.Quiet, anyFolder: o.Confirmed || removeData);
            own = RemoveOwnPart(exe);
        }
        catch (UninstallStopped stop) { Log(stop); return Report(o, "Rock Preview was not uninstalled", stop.Message, 4); }
        catch (Exception ex) { Log(ex); return Report(o, "Rock Preview was not uninstalled", ex.Message, 1); }

        var args = new List<string> { "-uninstall", "-confirmed", "-ownpartdone", "-parent", Environment.ProcessId.ToString(), "-dir", folder };
        if (o.Quiet) args.Add("-quiet");
        if (removeData) args.Add("-removedata");
        // Whoever's password elevates, "delete my settings and search index" is about the user who ran it.
        if (CurrentUserSid() is { } sid) { args.Add("-asuser"); args.Add(sid); }
        int code = Program.RunElevatedAndWait(args);
        if (code == 0) return 0;
        if (File.Exists(exe)) own.PutBack(exe);
        // The elevated copy told the user why it stopped; a refused or cancelled permission prompt is said here.
        return code == -1
            ? Report(o, "Rock Preview was not uninstalled", "Windows didn't give permission to remove it for everyone on this PC.", 5)
            : code;
    }

    /// <summary>Tells the user why the uninstall stopped (not with -quiet) and returns <paramref name="code"/>.</summary>
    static int Report(Options o, string heading, string text, int code)
    {
        if (!o.Quiet) TaskDialog.ShowDialog(new TaskDialogPage { Caption = AppName + " Setup", Heading = heading, Text = text, Icon = TaskDialogIcon.Error });
        return code;
    }

    /// <summary>The uninstall stopped before removing anything, for a reason the user can act on.</summary>
    sealed class UninstallStopped(string message) : Exception(message);

    /// <summary>
    /// Done: whether the running uninstaller's own program file is still to be deleted, what the user should know, and
    /// whether Remove from this PC left the instant-indexing service for other accounts that use it too.
    /// </summary>
    sealed record Uninstalled(bool DeleteLater, string? Warning, bool ServiceStays = false);

    /// <summary>
    /// "Uninstall Rock Preview?" (with <paramref name="portable"/>, "Remove ... from this PC?" for the running
    /// copy, which isn't installed and whose program stays), with a box (unticked) to delete the user's settings and search
    /// index too: whether the box was ticked, or null when cancelled. Settings › About asks it, and so does the uninstall
    /// itself unless it was asked already (-confirmed).
    /// </summary>
    public static bool? ConfirmUninstall(IWin32Window? owner, string folder, bool portable = false)
    {
        var check = new TaskDialogVerificationCheckBox("Also delete my Rock Preview settings and search index");
        var uninstall = new TaskDialogButton(portable ? "Remove" : "Uninstall");
        var page = new TaskDialogPage
        {
            Caption = owner is null ? AppName + " Setup" : AppName,
            Heading = portable ? "Remove Rock Preview from this PC?" : "Uninstall Rock Preview?",
            Text = portable
                ? "Folders, drives, This PC and Win+E go back to File Explorer, start with Windows is turned off, and the " +
                  "instant-indexing service is removed unless other accounts on this PC use it too. The program stays in " +
                  $"{folder}: delete {Path.GetFileName(Environment.ProcessPath) ?? ExeName} afterwards. Your files are not affected."
                : $"Rock Preview will be removed from this PC ({folder}). Your files are not affected.",
            Icon = TaskDialogIcon.Information,
            Buttons = { uninstall, TaskDialogButton.Cancel },
            Verification = check,
            DefaultButton = TaskDialogButton.Cancel,
        };
        var answer = owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        return answer == uninstall ? check.Checked : null;
    }

    /// <summary>
    /// Settings › About's Uninstall, once <see cref="ConfirmUninstall"/> said yes to the copy in <paramref name="folder"/>:
    /// runs this program's uninstall of exactly that copy without asking again. It closes this copy (wherever it runs
    /// from), removes everything and says when it's done; Windows asks for permission only to remove the
    /// instant-indexing service or an install for everyone. With <paramref name="portable"/> (Remove from this PC, for
    /// this copy, which isn't installed) the program itself stays. False if it couldn't be started.
    /// </summary>
    public static bool StartUninstall(string folder, bool removeData, bool portable = false)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        // Not from inside the program's folder, which the uninstall removes.
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        foreach (string a in new[] { "-uninstall", "-confirmed", "-dir", folder }) psi.ArgumentList.Add(a);
        if (portable) psi.ArgumentList.Add("-portable");
        if (removeData) psi.ArgumentList.Add("-removedata");
        try { Process.Start(psi)?.Dispose(); return true; }
        catch (Win32Exception ex) { Log(ex); return false; }
    }

    /// <summary>
    /// Removes the copy in <paramref name="folder"/>. With <paramref name="keepProgram"/> (Remove from this PC, a copy that
    /// isn't installed) only what it set up in Windows goes, and the data if asked: its program, the shortcuts to it and
    /// any Settings › Apps entry (which is another copy's) stay, and so does the instant-indexing service unless it
    /// answers this user alone.
    /// </summary>
    static Uninstalled Uninstall(Options o, string folder, bool allUsers, bool removeData, bool keepProgram = false)
    {
        // Remove from this PC is for the running copy's own file, whatever it is called: its settings point at that file.
        string exe = keepProgram && Environment.ProcessPath is { } running ? running : Path.Combine(folder, ExeName);
        string? me = CurrentUserSid();
        // Other accounts first, since it only reads: when it says no, nothing has been closed or removed. Their folders
        // opening with this copy (signed in or not) would be left with none.
        if (allUsers && ExplorerReplacement.OtherAccountsUsing(exe, [o.AsUser, me]) is { Count: > 0 } others)
            throw new UninstallStopped(UninstallRules.OtherAccountsMessage(others));

        // With -confirmed, or when the user's data goes, the copy that asked closes too, wherever it runs from (a portable
        // copy would otherwise go on running, could turn folders back on for itself, and would save into the data just
        // deleted). The elevated half of an uninstall for everyone closes every copy but the one waiting for it.
        try { CloseRunning(exe, o.Quiet, anyFolder: o.Confirmed || removeData, exceptPid: o.ParentPid, again: keepProgram ? "try again" : "uninstall again"); }
        catch (InvalidOperationException ex) { throw new UninstallStopped(ex.Message); }
        // The copy without rights did its own account's part; this copy does its own account's, unless it is that same
        // account (another account when an administrator's password elevated it).
        if (!(o.OwnPartDone && string.Equals(o.AsUser, me, StringComparison.OrdinalIgnoreCase))) RemoveOwnPart(exe, keepProgram);

        if (allUsers)
            foreach (var special in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory })
                DeleteShortcutsTo(exe, special);
        // Remove the instant-indexing service (Windows asks for permission unless this already runs as administrator).
        // Remove from this PC removes it only when it answers this user alone: it is one service for the whole PC, and
        // another account's copy (an install of its own, which this user can't see) may depend on it. Taking just this
        // user off its list would need Windows' permission too, and a restart that interrupts the others; left on, it
        // changes nothing once this copy is gone.
        var warnings = new List<string>();
        bool serviceStays = false;
        try
        {
            var service = keepProgram ? ServiceForThisUser() : (ServiceFor?)null;
            serviceStays = service == ServiceFor.ThisUserAndOthers;
            if ((service is null or ServiceFor.OnlyThisUser) && global::Anything.IndexService.IsInstalled())
            {
                if (Program.IsElevated()) global::Anything.IndexService.Uninstall();
                else if (Program.RunElevatedAndWait(["-uninstallservice", "-quiet"]) == -1)
                    warnings.Add("Windows didn't give permission to remove the instant-indexing service (Rock Preview Service), so it is still installed.");
                if (warnings.Count == 0 && global::Anything.IndexService.IsInstalled())
                    warnings.Add("The instant-indexing service (Rock Preview Service) couldn't be removed, so it is still installed " +
                                 $"(the reason is in {Path.Combine(Path.GetTempPath(), "Rock Preview Setup", "setup.log")}).");
            }
        }
        catch (Exception ex) { Log(ex); warnings.Add($"The instant-indexing service couldn't be removed: {ex.Message}"); }
        if (warnings.Count > 0) warnings.Add("To remove it, run \"sc delete RockPreviewService\" in a command prompt opened as administrator.");
        if (!keepProgram) (allUsers ? Registry.LocalMachine : Registry.CurrentUser).DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        // Only its own data folders ("Rock Preview", with the space, the name before the rename): settings, index, logs; of
        // the user who asked, not of the administrator whose password elevated it.
        if (removeData) DeleteUserData(o.AsUser);

        // The program file: deleted now, or (when it is the uninstaller that is running) by a helper started once
        // "was uninstalled" is closed. The folder is removed only if that leaves it empty.
        bool deleteLater = false;
        if (!keepProgram)
        {
            bool self = SamePath(Environment.ProcessPath ?? "", exe);
            FileRetry.DeleteAside(exe); // an update's renamed old program, when it is free
            if (File.Exists(exe) && !self) // the copy closed just now can still hold its file for a moment
            {
                try { FileRetry.Delete(exe); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (File.Exists(exe) && !self) warnings.Add($"{exe} is in use, so it couldn't be deleted. Close it, then delete that folder.");
            deleteLater = self && File.Exists(exe);
            if (!File.Exists(exe))
            {
                try { Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return new Uninstalled(deleteLater, warnings.Count > 0 ? string.Join(" ", warnings) : null, serviceStays);
    }

    /// <summary>
    /// What a copy that isn't installed set up in Windows for this user, which deleting its file would leave behind. The
    /// instant-indexing service is its to remove only when it answers this user alone.
    /// </summary>
    internal sealed record Footprint(bool Takeover, bool StartsWithWindows, ServiceFor Service)
    {
        public bool Any => Takeover || StartsWithWindows || Service == ServiceFor.OnlyThisUser;
    }

    /// <summary>
    /// What the copy whose program file is <paramref name="exe"/> (the running one, whatever it is called) set up in
    /// Windows for this user: folders, drives, This PC or Win+E that run that file, a start-with-Windows entry that starts
    /// it, and whom the instant-indexing service answers (one for the whole PC, which other accounts' copies may use too).
    /// Reads the registry and asks the service manager: call it off the UI thread.
    /// </summary>
    public static Footprint FootprintOf(string exe)
    {
        bool takeover = false, startup = false;
        var service = ServiceFor.NotThisUser;
        try { takeover = ExplorerReplacement.References(exe); } catch (Exception) { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            startup = run is not null && RunValues.Any(name => run.GetValue(name) is string cmd && UninstallRules.RunsProgram(cmd, exe));
        }
        catch (Exception) { }
        try { service = ServiceForThisUser(); } catch (Exception) { }
        return new Footprint(takeover, startup, service);
    }

    /// <summary>
    /// Whom the instant-indexing service answers, seen from this user (NotThisUser when it isn't installed). A local
    /// account deleted from this PC doesn't count: its SID stays on the service's list, which nothing takes users off.
    /// </summary>
    static ServiceFor ServiceForThisUser() => global::Anything.IndexService.IsInstalled()
        ? UninstallRules.ServiceIsFor(global::Anything.IndexService.AllowedUserSids(), CurrentUserSid(), ExplorerReplacement.IsRemovedAccount)
        : ServiceFor.NotThisUser;

    /// <summary>
    /// What a user's own part of an uninstall changed, to put back exactly if the rest doesn't happen: whether this copy
    /// was the one folders opened with, the start-up entries with Task Manager's on/off state, and the shortcut files.
    /// </summary>
    sealed record OwnPart(bool Takeover, List<StartupEntry> Startup, List<(string Path, byte[] Bytes)> Shortcuts)
    {
        public void PutBack(string exe)
        {
            try { if (Takeover) ExplorerReplacement.Enable(exe); } catch (Exception ex) { Log(ex); }
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedRunKeyPath);
                foreach (var e in Startup)
                {
                    run.SetValue(e.Name, e.Command);
                    if (e.Approved is { } state) approved.SetValue(e.Name, state, RegistryValueKind.Binary);
                }
            }
            catch (Exception ex) { Log(ex); }
            foreach (var (path, bytes) in Shortcuts)
            {
                try { if (!File.Exists(path)) File.WriteAllBytes(path, bytes); } catch (Exception ex) { Log(ex); }
            }
        }
    }

    /// <summary>A start-with-Windows entry that was removed: its name, its command and Task Manager's on/off state (if any).</summary>
    internal sealed record StartupEntry(string Name, string Command, byte[]? Approved);

    /// <summary>
    /// The current account's own part of an uninstall, in its own registry. Folders, drives, This PC and Win+E go back to
    /// File Explorer first, right after the copy closed: while they still run this copy, opening a folder would start it
    /// again. If they still run it afterwards, the uninstall stops before removing anything else, so they never point
    /// at a program that's gone. Then start with Windows and this account's shortcuts (not with <paramref
    /// name="keepProgram"/>: the program they open stays). <paramref name="exe"/> is the copy's program file.
    /// </summary>
    static OwnPart RemoveOwnPart(string exe, bool keepProgram = false)
    {
        // Folders open with this copy (to put back if the rest doesn't happen), not just a leftover of its keys behind
        // another file manager, which put back would take folders from.
        bool takeover = ExplorerReplacement.RegisteredExe() is { } registered && SamePath(registered, exe);
        if (ExplorerReplacement.References(exe))
        {
            try { ExplorerReplacement.Disable(); } catch (Exception ex) { Log(ex); }
            if (ExplorerReplacement.References(exe))
                throw new UninstallStopped("Folders, drives or Win+E still open with Rock Preview, and Windows' settings for " +
                    "them couldn't be changed, so nothing was removed. Turn off Settings › General › Use instead of Windows File " +
                    $"Explorer, then {(keepProgram ? "try again" : "uninstall again")}.");
        }
        var startup = RemoveStartupEntries(exe);
        var shortcuts = new List<(string, byte[])>();
        if (!keepProgram)
            foreach (var special in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
                shortcuts.AddRange(DeleteShortcutsTo(exe, special));
        return new OwnPart(takeover, startup, shortcuts);
    }

    /// <summary>Deletes this program's shortcuts (either name) in that folder; returns the deleted files' contents.</summary>
    static List<(string Path, byte[] Bytes)> DeleteShortcutsTo(string exe, Environment.SpecialFolder special)
    {
        var deleted = new List<(string, byte[])>();
        foreach (string name in new[] { AppName, OldShortcutName })
        {
            string lnk = Path.Combine(Environment.GetFolderPath(special), name + ".lnk");
            if (!File.Exists(lnk) || ShortcutTarget(lnk) is not { } target || !SamePath(target, exe)) continue;
            try { var bytes = File.ReadAllBytes(lnk); File.Delete(lnk); deleted.Add((lnk, bytes)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    /// <summary>
    /// Deletes the running uninstaller's own program file once this process has ended: a helper tries every second for
    /// half a minute. Started after the last message is closed, so the time isn't used up while it's still on screen.
    /// </summary>
    static void StartDeleteHelper(string exe, string folder)
    {
        // An update's renamed old program ("RockPreview.exe.old") goes too, or the folder couldn't be removed.
        string script = $"/d /c for /l %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & del /f /q \"{exe}\" \"{exe}.old*\" >nul 2>&1 & " +
                        $"if not exist \"{exe}\" (rmdir \"{folder}\" >nul 2>&1 & exit /b))";
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), script)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
        catch (Win32Exception ex) { Log(ex); }
    }

    /// <summary>
    /// Start with Windows: the entry that starts <paramref name="exe"/>, and one an earlier copy left behind whose program
    /// is gone (an early version's "RockPreview" in Program Files), with Task Manager's on/off state for it. An entry that
    /// starts another copy that is still there is left alone, and so is one on a drive that isn't there now (a USB stick,
    /// a network drive). Returns what it removed.
    /// </summary>
    static List<StartupEntry> RemoveStartupEntries(string exe)
    {
        var removed = new List<StartupEntry>();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run == null) return removed;
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            foreach (string name in RunValues)
            {
                if (run.GetValue(name) is not string cmd) continue;
                bool gone = UninstallRules.ProgramOf(cmd) is { } program && Path.GetFileName(program).Equals(ExeName, StringComparison.OrdinalIgnoreCase)
                            && !File.Exists(program) && Path.GetPathRoot(program) is { Length: > 0 } root && Directory.Exists(root);
                if (!gone && !UninstallRules.RunsProgram(cmd, exe)) continue;
                var state = approved?.GetValue(name) as byte[];
                run.DeleteValue(name, false);
                approved?.DeleteValue(name, false);
                removed.Add(new StartupEntry(name, cmd, state));
            }
        }
        catch (Exception ex) { Log(ex); }
        return removed;
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Asks the running copy to save and exit (it accepts that even when elevated), then waits; a quiet install ends a
    /// copy that doesn't. Installing closes the copy that runs from any folder (a portable or older copy): only one runs
    /// at a time, so one left open would answer instead of the new one. Uninstalling (<paramref name="anyFolder"/>
    /// false) closes only a copy started from <paramref name="exe"/>. A copy counts as closed when its process has ended,
    /// not when its window goes (it still saves the index then, and holds the single-instance mutex). <paramref
    /// name="again"/> ends the message when a copy runs in another account ("install again", "uninstall again").
    /// </summary>
    static void CloseRunning(string exe, bool force, bool anyFolder, int exceptPid = 0, string again = "uninstall again")
    {
        int self = Environment.ProcessId;
        var seen = new Dictionary<int, Process>();
        List<Process> Running()
        {
            // exceptPid: the copy without rights that started this elevated half and waits for it.
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                if (p.Id != self && p.Id != exceptPid && SamePath(ImagePath(p) ?? "", exe)) seen.TryAdd(p.Id, p);
            if (anyFolder && Program.RunningInstanceId() is int pid && pid != self && pid != exceptPid && !seen.ContainsKey(pid))
            {
                try { seen.Add(pid, Process.GetProcessById(pid)); } catch (ArgumentException) { /* just exited */ }
            }
            return seen.Values.Where(IsAlive).ToList();
        }
        var running = Running();
        if (running.Count == 0) return;
        // A copy in another signed-in account's session can't be asked to close from here, and isn't killed: its
        // user would lose what it hadn't saved. Run from session 0 (SYSTEM, a deployment tool, a scheduled task), no
        // copy can be asked, and none is anyone else's: it waits, and a quiet one then ends them (UninstallRules).
        int session = Process.GetCurrentProcess().SessionId;
        void StopForOtherSessions(List<Process> list)
        {
            if (list.Any(p => UninstallRules.StopsForOtherSession(session, SessionOf(p))))
                throw new InvalidOperationException("Rock Preview is running in another account that is signed in on this PC. " +
                    $"Close it there, or sign that account out, then {again}.");
        }
        StopForOtherSessions(running);
        Program.ForwardToRunning(["-exit"]);
        // A demo (--demo) started from this program has a window of its own: asked too, or the wait would be for nothing.
        if (Program.RunningInstanceId(demo: true) is int demo && running.Any(p => p.Id == demo))
            Program.ForwardToRunning(["--demo", "-exit"], demo: true);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 60_000)
        {
            Thread.Sleep(250);
            running = Running();
            if (running.Count == 0) return;
        }
        StopForOtherSessions(running);
        if (!force) throw new InvalidOperationException("Rock Preview is still running. Close it and try again.");
        foreach (var p in running) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
    }

    static int? SessionOf(Process p)
    {
        try { return p.SessionId; }
        catch (Exception) { return null; }
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

    // The old copy, just closed, can still hold its file for a moment: access denied, or a sharing violation. Held
    // longer (it crashed as it closed and Windows is writing a report, or it runs in another session), it is renamed
    // out of the way and deleted once it is let go.
    static void CopyWithRetry(string from, string to)
    {
        FileRetry.Replace(from, to, moveAsideWhenHeld: true);
        FileRetry.DeleteAside(to);
    }

    /// <summary>Two paths name the same file or folder, compared as full paths (case and a trailing backslash don't matter).</summary>
    internal static bool SamePath(string a, string b)
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

    internal static void Log(Exception ex)
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

    static void CreateShortcut(string lnk, string exe, bool runAsAdmin) => Shortcuts.Create(lnk, exe, "", ShortcutDescription(), runAsAdmin);

    static string ShortcutDescription() => ConfiguredHotkey() is { } hotkey
        ? $"Search, browse and preview every file instantly. Press {hotkey} to open it from anywhere."
        : "Search, browse and preview every file instantly.";

    static string? ShortcutTarget(string lnk) => Shortcuts.Read(lnk)?.Path;

    /// <summary>Whether the desktop (or Start menu) shortcut exists, for just this user or everyone, by today's name or the old one.</summary>
    internal static bool ShortcutExists(bool desktop, bool allUsers)
    {
        var folder = desktop
            ? (allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory)
            : (allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);
        return File.Exists(Path.Combine(Environment.GetFolderPath(folder), AppName + ".lnk"))
            || File.Exists(Path.Combine(Environment.GetFolderPath(folder), OldShortcutName + ".lnk"));
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
