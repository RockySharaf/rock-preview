using System.Runtime.InteropServices;
using System.Security.Principal;
using Anything.Core;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// Optional (Settings › General, Setup): Ultimate File Explorer instead of File Explorer. Only this user's registry classes
/// change, and whatever was there before is kept under <see cref="BackupKey"/> and put back when this is turned off,
/// so File Explorer (or another file manager) comes back exactly as it was. What opens Rock Preview:
/// <list type="bullet">
/// <item>opening a folder or drive (the default verb of Directory and Drive: desktop, Start menu, other apps);</item>
/// <item>Win+E and explorer.exe without arguments (the "File Explorer" object's opennewwindow verb);</item>
/// <item>shortcuts to File Explorer (that object's open verb);</item>
/// <item>This PC: the desktop icon and shortcuts to it;</item>
/// <item>File Explorer's pinned taskbar button, whose shortcut becomes Rock Preview's (the taskbar starts File Explorer
/// itself, bypassing the registry); the original shortcut is kept in the backup.</item>
/// </list>
/// File Explorer windows opened by apps ("Show in folder", explorer.exe with a folder) stay File Explorer, which is
/// also the way back to it.
/// </summary>
internal static class ExplorerReplacement
{
    const string Verb = "RockPreview";
    const string Classes = @"Software\Classes\";
    const string BackupKey = @"Software\Rock Preview\Explorer replacement";
    const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// Shell classes whose default action ("open this") becomes Rock Preview's verb: folders and drives (the path
    /// is "%1"), and This PC. Created marks a class key that only exists in this user's registry because of us.
    /// </summary>
    sealed record FolderClass(string Key, string PathArgument, bool Created);

    static readonly FolderClass[] FolderClasses =
    [
        new("Directory", "%1", Created: false),
        new("Drive", "%1", Created: false),
        new(@"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}", ThisPc, Created: true),
    ];

    /// <summary>A shell object's verb whose command runs Rock Preview: the key, its name in the backup, the arguments.</summary>
    sealed record Command(string Name, string Clsid, string VerbName, string Arguments)
    {
        public string Root => Classes + $@"CLSID\{Clsid}";
        public string VerbKey => Root + $@"\shell\{VerbName}";
        public string CommandKey => VerbKey + @"\command";
    }

    static readonly Command[] Commands =
    [
        new("Win+E", "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", "opennewwindow", "-explorer"),
        new("Taskbar", "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", "open", "-explorer"),
    ];

    /// <summary>The exe that folders open with, or null when Rock Preview isn't replacing File Explorer.</summary>
    public static string? RegisteredExe()
    {
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(Classes + @"Directory\shell");
            if (!IsOurVerb(shell?.GetValue(null) as string)) return null;
            using var cmd = Registry.CurrentUser.OpenSubKey(Classes + $@"Directory\shell\{Verb}\command");
            return ExeOf(cmd?.GetValue(null) as string);
        }
        catch (Exception) { return null; }
    }

    public static bool IsEnabled() => RegisteredExe() is not null;

    /// <summary>
    /// Whether this user's folders, drives, This PC or Win+E still run a program in <paramref name="folder"/> (as the
    /// default action or not). The uninstall must not delete that program while they do: every folder would then fail
    /// with "Application not found".
    /// </summary>
    public static bool References(string folder)
    {
        using var classes = Registry.CurrentUser.OpenSubKey(Classes.TrimEnd('\\'));
        return classes is not null && References(classes, folder);
    }

    /// <summary>
    /// The other Windows accounts whose folders, drives, This PC or Win+E run the program in <paramref name="folder"/>, or
    /// whose settings couldn't be checked (counted too, so the program isn't deleted under them), with their names (null
    /// when Windows can't name the account now: a work account away from its network, say) and profile folders. A
    /// signed-in account's registry classes are loaded under HKEY_USERS; a signed-out one's are read from its UsrClass.dat.
    /// Both need administrator rights. Accounts in <paramref name="except"/> (SIDs) are skipped, and so is a signed-out
    /// local account that was deleted from this PC (UninstallRules.IsRemovedLocalAccount): it never signs in again.
    /// </summary>
    public static List<OtherAccount> OtherAccountsUsing(string folder, IEnumerable<string?> except)
    {
        var skip = new HashSet<string>(except.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var found = new List<OtherAccount>();
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (string hive in Registry.Users.GetSubKeyNames())
        {
            if (!hive.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            string sid = hive[..^"_Classes".Length];
            loaded.Add(sid);
            if (skip.Contains(sid)) continue;
            try
            {
                using var classes = Registry.Users.OpenSubKey(hive);
                if (classes is not null && References(classes, folder)) found.Add(new(sid, Identify(sid).Name, ProfileOf(profiles, sid), AccountUse.UsesIt));
            }
            catch (Exception) { found.Add(new(sid, Identify(sid).Name, ProfileOf(profiles, sid), AccountUse.CouldNotCheck)); }
        }
        // Accounts that aren't signed in: their classes hive is a file in their profile, read privately and let go again.
        string? machine = null;
        foreach (string key in profiles?.GetSubKeyNames() ?? [])
        {
            // Only people's accounts (local, domain, Microsoft or work accounts), not the system's service accounts, and
            // only keys that are an account (not a profile repair's "<SID>.bak").
            if (!(key.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || key.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
                || !IsSid(key) || skip.Contains(key) || loaded.Contains(key)) continue;
            if (ProfileOf(profiles, key) is not { } home) continue;
            string file = Path.Combine(home, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
            if (!File.Exists(file)) continue; // never signed in, or no classes of its own: nothing can point here
            AccountUse? use = null;
            int rc = RegLoadAppKey(file, out var handle, KEY_READ, 0, 0);
            if (rc != 0) { handle.Dispose(); use = AccountUse.CouldNotCheck; }
            else
            {
                try
                {
                    using var classes = RegistryKey.FromHandle(handle);
                    if (References(classes, folder)) use = AccountUse.UsesIt;
                }
                catch (Exception) { use = AccountUse.CouldNotCheck; }
            }
            if (use is not { } why) continue;
            // Named only now: looking up an account away from its domain can take a while.
            var (name, notMapped) = Identify(key);
            if (UninstallRules.IsRemovedLocalAccount(key, notMapped, machine ??= MachineAccountDomain())) continue;
            found.Add(new(key, name, home, why));
        }
        return found;
    }

    /// <summary>An account's profile folder from the ProfileList, or null when it lists none.</summary>
    static string? ProfileOf(RegistryKey? profiles, string sid)
    {
        try
        {
            using var profile = profiles?.OpenSubKey(sid);
            return profile?.GetValue("ProfileImagePath") is string { Length: > 0 } raw ? Environment.ExpandEnvironmentVariables(raw) : null;
        }
        catch (Exception) { return null; }
    }

    static bool IsSid(string text)
    {
        try { return string.Equals(new System.Security.Principal.SecurityIdentifier(text).Value, text, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether this user's registry still has this app's folder verbs or Win+E commands although folders don't open
    /// with it (another file manager took the default action after it): turning the option off cleans them.
    /// </summary>
    public static bool HasLeftovers()
    {
        try
        {
            foreach (var cls in FolderClasses)
            {
                using var verb = Registry.CurrentUser.OpenSubKey(Classes + cls.Key + $@"\shell\{Verb}");
                if (verb is not null) return true;
            }
            foreach (var c in Commands)
            {
                using var key = Registry.CurrentUser.OpenSubKey(c.CommandKey);
                if (IsOurCommand(key?.GetValue(null) as string)) return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    const int KEY_READ = 0x20019;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKey(string file, out Microsoft.Win32.SafeHandles.SafeRegistryHandle key, int access, int options, int reserved);

    static bool References(RegistryKey classes, string folder)
    {
        foreach (var cls in FolderClasses)
            if (CommandAt(classes, cls.Key + $@"\shell\{Verb}\command") is { } command && Setup.RunsProgramIn(command, folder)) return true;
        foreach (var c in Commands)
            if (CommandAt(classes, $@"CLSID\{c.Clsid}\shell\{c.VerbName}\command") is { } command && IsOurCommand(command) && Setup.RunsProgramIn(command, folder)) return true;
        return false;
    }

    static string? CommandAt(RegistryKey classes, string path)
    {
        using var key = classes.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// The account's name, or null when Windows can't name it; NotMapped when Windows said there is no such account, not
    /// when it couldn't find out (a domain controller that can't be reached may answer either way).
    /// </summary>
    static (string? Name, bool NotMapped) Identify(string sid)
    {
        try { return (new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value, false); }
        catch (IdentityNotMappedException) { return (null, true); }
        catch (Exception) { return (null, false); }
    }

    /// <summary>This PC's own account domain (its local accounts' SIDs are it and one more number), or null when unknown.</summary>
    static string? MachineAccountDomain()
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        if (LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_VIEW_LOCAL_INFORMATION, out IntPtr policy) != 0) return null;
        try
        {
            if (LsaQueryInformationPolicy(policy, PolicyAccountDomainInformation, out IntPtr info) != 0) return null;
            try
            {
                IntPtr sid = Marshal.PtrToStructure<POLICY_ACCOUNT_DOMAIN_INFO>(info).DomainSid;
                return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid).Value;
            }
            finally { LsaFreeMemory(info); }
        }
        catch (Exception) { return null; }
        finally { LsaClose(policy); }
    }

    const int POLICY_VIEW_LOCAL_INFORMATION = 0x1, PolicyAccountDomainInformation = 5;
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory, ObjectName; public int Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    struct POLICY_ACCOUNT_DOMAIN_INFO { public ushort NameLength, NameMaximumLength; public IntPtr NameBuffer, DomainSid; }
    [DllImport("advapi32.dll")] static extern int LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, int access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern int LsaQueryInformationPolicy(IntPtr policy, int infoClass, out IntPtr buffer);
    [DllImport("advapi32.dll")] static extern int LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")] static extern int LsaClose(IntPtr policy);

    /// <summary>Makes <paramref name="exe"/> replace File Explorer. Safe to call again to move it to another exe.</summary>
    public static void Enable(string exe)
    {
        // The first time, remember what was there; a later call (another copy of Rock Preview) keeps that backup.
        bool firstTime;
        using (var existing = Registry.CurrentUser.OpenSubKey(BackupKey)) firstTime = existing is null;
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);

        foreach (var cls in FolderClasses)
        {
            using var shell = Registry.CurrentUser.CreateSubKey(Classes + cls.Key + @"\shell");
            if (firstTime && shell.GetValue(null) is string previous && !IsOurVerb(previous)) backup.SetValue(cls.Key + " default", previous);
            using (var verb = shell.CreateSubKey(Verb))
            {
                verb.SetValue(null, "Open in Ultimate File Explorer");
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = verb.CreateSubKey("command");
                // -path last: a drive's "C:\" arrives as C:" (the backslash escapes the quote); CommandLine repairs it.
                cmd.SetValue(null, $"\"{exe}\" -explorer -path \"{cls.PathArgument}\"");
            }
            shell.SetValue(null, Verb); // the default action for a double-click or "open this"
        }

        foreach (var c in Commands)
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(c.CommandKey))
            {
                if (firstTime && existing is not null && !IsOurCommand(existing.GetValue(null) as string))
                {
                    backup.SetValue(c.Name + " existed", 1, RegistryValueKind.DWord);
                    if (existing.GetValue(null) is string command) backup.SetValue(c.Name + " command", command);
                    if (existing.GetValue("DelegateExecute") is string d) backup.SetValue(c.Name + " DelegateExecute", d);
                }
            }
            using var key = Registry.CurrentUser.CreateSubKey(c.CommandKey);
            key.SetValue(null, $"\"{exe}\" {c.Arguments}");
            key.SetValue("DelegateExecute", ""); // empty overrides the shell's own handler, so the command runs
        }
        SwapTaskbarPin(exe, backup);
        Notify();
    }

    // ---- the taskbar's File Explorer button ------------------------------------------------------

    static string TaskbarPins => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    const string FileExplorerObject = "::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}";
    const string PinDescription = "Ultimate File Explorer, in place of File Explorer";

    static bool IsOurShortcut(string lnk) => Shortcuts.Read(lnk) is { } t && t.Arguments.Contains("-explorer", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pinned File Explorer button's shortcut. Its file name is translated, so it's found by its target.</summary>
    static string? FindFileExplorerPin()
    {
        try
        {
            foreach (string lnk in Directory.EnumerateFiles(TaskbarPins, "*.lnk"))
            {
                if (Shortcuts.Read(lnk) is not { } t) continue;
                if (string.Equals(t.ParsingName, FileExplorerObject, StringComparison.OrdinalIgnoreCase)
                    || (t.Path.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase) && t.Arguments.Length == 0))
                    return lnk;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Turns File Explorer's taskbar button into Rock Preview's, in the same place (the taskbar picks the change up
    /// by itself). The original shortcut's bytes go into the backup; a later call only points it at another exe.
    /// </summary>
    static void SwapTaskbarPin(string exe, RegistryKey backup)
    {
        try
        {
            if (backup.GetValue("Taskbar pin name") is string name && File.Exists(Path.Combine(TaskbarPins, name))
                && IsOurShortcut(Path.Combine(TaskbarPins, name)))
            {
                Shortcuts.Create(Path.Combine(TaskbarPins, name), exe, "-explorer", PinDescription);
                return;
            }
            if (FindFileExplorerPin() is not { } pin) return; // File Explorer isn't pinned: nothing to take over
            backup.SetValue("Taskbar pin", File.ReadAllBytes(pin), RegistryValueKind.Binary);
            backup.SetValue("Taskbar pin name", Path.GetFileName(pin));
            Shortcuts.Create(pin, exe, "-explorer", PinDescription);
        }
        catch (Exception) { }
    }

    /// <summary>Puts File Explorer's original taskbar shortcut back, unless the button was unpinned or changed since.</summary>
    static void RestoreTaskbarPin(RegistryKey? backup)
    {
        try
        {
            if (backup?.GetValue("Taskbar pin") is not byte[] original || backup.GetValue("Taskbar pin name") is not string name) return;
            string pin = Path.Combine(TaskbarPins, name);
            if (File.Exists(pin) && IsOurShortcut(pin)) File.WriteAllBytes(pin, original);
        }
        catch (Exception) { }
    }

    /// <summary>Puts back what was there before Rock Preview took over (File Explorer, unless another app had it).</summary>
    public static void Disable()
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey);
        foreach (var cls in FolderClasses)
        {
            using (var shell = Registry.CurrentUser.OpenSubKey(Classes + cls.Key + @"\shell", writable: true))
            {
                if (shell is null) continue;
                if (IsOurVerb(shell.GetValue(null) as string))
                {
                    if (backup?.GetValue(cls.Key + " default") is string previous) shell.SetValue(null, previous);
                    else shell.DeleteValue("", throwOnMissingValue: false);
                }
                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            }
            if (cls.Created)
            {
                DeleteIfEmpty(Classes + cls.Key + @"\shell");
                DeleteIfEmpty(Classes + cls.Key);
            }
        }

        foreach (var c in Commands)
        {
            bool existed = backup?.GetValue(c.Name + " existed") is int e && e != 0;
            using (var key = Registry.CurrentUser.OpenSubKey(c.CommandKey, writable: true))
            {
                if (key is null || !IsOurCommand(key.GetValue(null) as string)) continue; // not ours (any more): leave it
                if (existed)
                {
                    if (backup?.GetValue(c.Name + " command") is string command) key.SetValue(null, command); else key.DeleteValue("", false);
                    if (backup?.GetValue(c.Name + " DelegateExecute") is string d) key.SetValue("DelegateExecute", d); else key.DeleteValue("DelegateExecute", false);
                    continue;
                }
            }
            // We created it: remove the verb, then its parents only if nothing else lives there.
            Registry.CurrentUser.DeleteSubKeyTree(c.VerbKey, throwOnMissingSubKey: false);
            DeleteIfEmpty(c.Root + @"\shell");
            DeleteIfEmpty(c.Root);
        }
        RestoreTaskbarPin(backup);
        Registry.CurrentUser.DeleteSubKeyTree(BackupKey, throwOnMissingSubKey: false);
        Notify();
    }

    /// <summary>
    /// At startup: if Rock Preview replaces File Explorer through a copy that no longer exists (moved or deleted),
    /// point it at this one, so opening a folder never fails.
    /// </summary>
    public static void RepairIfMissing(string? exe)
    {
        try
        {
            if (exe is null || RegisteredExe() is not { } registered) return;
            if (!File.Exists(registered)) Enable(exe);
            else UpdatePinAppId();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Rock Preview's taskbar button written by an earlier version, under another AppUserModelID: rewritten (same
    /// target) so its windows group under it again.
    /// </summary>
    static void UpdatePinAppId()
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey);
        if (backup?.GetValue("Taskbar pin name") is not string name) return;
        string pin = Path.Combine(TaskbarPins, name);
        if (!File.Exists(pin) || !IsOurShortcut(pin) || Shortcuts.ReadAppId(pin) == Shortcuts.AppId) return;
        if (Shortcuts.Read(pin) is { } link && File.Exists(link.Path)) Shortcuts.Create(pin, link.Path, "-explorer", PinDescription);
    }

    static bool IsOurVerb(string? verb) => string.Equals(verb, Verb, StringComparison.OrdinalIgnoreCase);
    static bool IsOurCommand(string? command) => command?.Contains(" -explorer", StringComparison.OrdinalIgnoreCase) == true;

    static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
