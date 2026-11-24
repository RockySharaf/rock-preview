using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Anything.Core.Indexing;
using Microsoft.Win32.SafeHandles;

namespace Anything;

/// <summary>
/// Instant indexing in the Microsoft Store copy: a small helper, the same program started elevated with "-indexhelper
/// -parent PID -run N", reads NTFS drives for Rock Preview through the Rock Preview service's own pipe
/// (<see cref="VolumeService"/>), while that Rock Preview runs and no longer. The window itself always stays a normal app,
/// so files dragged in from other programs keep working (Windows refuses drops into an elevated window). Off unless turned
/// on (Settings › Indexing); turned on, Windows asks for permission each time Rock Preview starts it. The GitHub copy has
/// the Rock Preview service instead and never starts it. No window, tray icon, mutex, settings, index or log of its own:
/// it stops when Rock Preview ends, or when Rock Preview sets the stop event of its start (<see cref="StopEventName"/>).
/// </summary>
internal static class IndexHelper
{
    const string Switch = "indexhelper", ParentSwitch = "parent", RunSwitch = "run";
    /// <summary>The app's Id in the package's manifest (build/Store/AppxManifest.xml): with the family name, its name in shell:AppsFolder.</summary>
    const string AppId = "RockPreview";
    const int ERROR_ACCESS_DENIED = 5, ERROR_NOT_SUPPORTED = 50, ERROR_INVALID_PARAMETER = 87, ERROR_CANCELLED = 1223;

    /// <summary>"-indexhelper" anywhere in the command line: this process is the helper, not Rock Preview.</summary>
    public static bool IsHelperLaunch(IReadOnlyList<string> args) => args.Any(a => Is(a, Switch));

    static bool Is(string arg, string name) =>
        arg.Length > 1 && arg[0] is '-' or '/' && arg.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The event Rock Preview (process <paramref name="parentPid"/>) makes for its start number <paramref name="run"/> of
    /// the helper, and sets to stop it. One per start, never reused: a helper that comes up after its start was given up
    /// (turned off while Windows asked, or not answering in time) finds its own event set or gone, never a newer start's.
    /// </summary>
    public static string StopEventName(int parentPid, int run) =>
        @"Local\RockPreview.IndexHelper.Stop." + Number(parentPid) + "." + Number(run);

    static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether this account can start the helper as itself: an administrator's, whose Rock Preview runs with the limited
    /// token Windows gives it. A standard account would need an administrator's password, and the helper would then run as
    /// that other account, which can't see this Rock Preview's stop event; it isn't offered there.
    /// </summary>
    public static bool UserCanElevate => s_userCanElevate.Value;

    static readonly Lazy<bool> s_userCanElevate = new(() =>
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            return GetTokenInformation(me.Token, TokenElevationType, out int type, sizeof(int), out _) && type == TokenElevationTypeLimited;
        }
        catch (Exception) { return false; }
    });

    public enum StartResult { Started, Declined, Failed }

    /// <summary>
    /// Asks Windows to start the helper elevated for Rock Preview (process <paramref name="parentPid"/>, its start
    /// <paramref name="run"/>): ShellExecute's "runas" on the package's app in shell:AppsFolder, the way Windows Terminal's
    /// elevate-shim starts an elevated Terminal. Tried on a real PC on 2026-11-24: with only runFullTrust in the manifest,
    /// Windows asked and the elevated copy kept the package's identity. Not the app alias or the program file: this is the
    /// route that was tried. Waits while Windows asks, so it is called off the UI thread. Separate from
    /// Program.RelaunchElevated and RunElevatedAndWait, which refuse inside the package: this starts the helper only, never
    /// a window.
    /// </summary>
    public static StartResult Start(int parentPid, int run)
    {
        if (StorePackage.FamilyName is not { } family) return StartResult.Failed;
        try
        {
            using var started = Process.Start(new ProcessStartInfo(@"shell:AppsFolder\" + family + "!" + AppId,
                CommandLine.Quote(["-" + Switch, "-" + ParentSwitch, Number(parentPid), "-" + RunSwitch, Number(run)]))
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return StartResult.Started; // no process to hold on to when the shell activates the app: the pipe tells when it serves
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED) { return StartResult.Declined; }
        catch (Exception) { return StartResult.Failed; }
    }

    /// <summary>
    /// "-indexhelper -parent PID -run N": serves the pipe for the user who started it until that Rock Preview ends or sets
    /// the stop event of start N. Only inside the Store's package (the GitHub copy has the service) and only elevated
    /// (reading a drive's file table needs administrator rights); only for this package's RockPreview.exe; never beside a
    /// service Setup installed, whose pipe's name it is; and never on a pipe it didn't make (<see cref="VolumeService"/>
    /// ends at once when the name is taken). Returns 0 when it served or had nothing to do.
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        if (!StorePackage.IsInstalled) return ERROR_NOT_SUPPORTED;
        if (!Program.IsElevated()) return ERROR_ACCESS_DENIED;
        if (Argument(args, ParentSwitch) is not { } pid || Argument(args, RunSwitch) is not { } run) return ERROR_INVALID_PARAMETER;
        try
        {
            // Gone already, or not this package's Rock Preview: nothing to serve.
            using var parent = OpenRockPreview(pid);
            if (parent is null) return ERROR_INVALID_PARAMETER;
            // A service Setup installed reads the drives for Rock Preview, or will once it answers: the name is its own.
            if (IndexService.IsInstalled()) return 0;
            // Without its own start's event Turn off couldn't stop it: it doesn't serve at all then.
            if (!EventWaitHandle.TryOpenExisting(StopEventName(pid, run), out var stop)) return ERROR_INVALID_PARAMETER;
            using (stop)
            {
                WaitHandle[] ends = [parent, stop];
                if (WaitHandle.WaitAny(ends, 0) != WaitHandle.WaitTimeout) return 0; // closed or turned off meanwhile
                using var cts = VolumeService.CancelWhenAny(ends);
                // Under UAC the elevated token is the same user's: the pipe lets that user in, as the service does the
                // users it was installed for.
                using var me = WindowsIdentity.GetCurrent();
                VolumeService.Run([me.User!], cts.Token, asElevatedHelper: true);
            }
            return 0;
        }
        catch (Exception) { return 1; } // nothing is written anywhere: Rock Preview scans folders, as without it
    }

    /// <summary>The positive number after "-<paramref name="name"/>" (the last one given), else null.</summary>
    static int? Argument(IReadOnlyList<string> args, string name)
    {
        int i = -1;
        for (int k = 0; k < args.Count; k++) if (Is(args[k], name)) i = k;
        return i >= 0 && i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
    }

    /// <summary>
    /// Opens process <paramref name="pid"/> to wait for its end, when it is this package's RockPreview.exe (a process ID
    /// can be another program's by now); else null. Checked on the handle that is waited on, so it can't change meanwhile.
    /// </summary>
    static ProcessWait? OpenRockPreview(int pid)
    {
        var handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (!handle.IsInvalid && FamilyOf(handle) is { } family && family == StorePackage.FamilyName
            && string.Equals(Path.GetFileName(ImageOf(handle)), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            return new ProcessWait(handle);
        handle.Dispose();
        return null;
    }

    static string? FamilyOf(SafeWaitHandle process)
    {
        uint length = 0;
        if (GetPackageFamilyName(process, ref length, null) != ERROR_INSUFFICIENT_BUFFER) return null; // not in a package
        var name = new StringBuilder((int)length);
        return GetPackageFamilyName(process, ref length, name) == 0 ? name.ToString() : null;
    }

    static string? ImageOf(SafeWaitHandle process)
    {
        var name = new StringBuilder(1024);
        uint size = (uint)name.Capacity;
        return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
    }

    /// <summary>A process handle to wait on with the stop event.</summary>
    sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ERROR_INSUFFICIENT_BUFFER = 122, TokenElevationType = 18, TokenElevationTypeLimited = 3;

    // Windows' own libraries, from System32 only: this code runs elevated.
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeWaitHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int GetPackageFamilyName(SafeWaitHandle process, ref uint length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool QueryFullProcessImageName(SafeWaitHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
