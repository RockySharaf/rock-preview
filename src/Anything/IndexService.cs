using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Anything.Core.Indexing;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// The Rock Preview service: instant NTFS indexing without running Rock Preview as administrator, the way
/// Everything does it. Installed once with the administrator's approval (Setup, the info bar or Options); after that
/// Rock Preview runs as a normal app and reads the drives' file lists through it (see <see cref="VolumeService"/>).
/// It runs from an administrators-only folder (a copy of this exe in Program Files), so a normal program can't
/// replace the code that runs as SYSTEM, and it answers only the users it was installed for.
/// </summary>
internal static class IndexService
{
    public const string ServiceName = "RockPreviewService";
    const string DisplayName = "Rock Preview Service";
    const string Description = "Lets Rock Preview index your NTFS drives instantly without running as administrator. " +
                               "Reads file names, sizes and dates only, for the users who installed it.";
    const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName + @"\Parameters";
    const string AllowedUsersValue = "AllowedUsers";
    const string VersionValue = "Version";

    /// <summary>
    /// Bumped only when the service itself changes (not with every Rock Preview release), so updating Rock Preview
    /// asks for administrator permission again only when the service really needs it. 2: frees memory after a scan.
    /// </summary>
    public const int ServiceVersion = 2;

    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rock Preview Service");
    public static string InstalledExe => Path.Combine(InstallFolder, "RockPreviewService.exe");

    // ---- status (any user) -----------------------------------------------------------------------

    public static bool IsInstalled()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>The installed service's version (1 for the first release, which didn't record one); 0 when not installed.</summary>
    public static int InstalledVersion()
    {
        if (!IsInstalled()) return 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ParametersKey);
            return key?.GetValue(VersionValue) is int v ? v : 1;
        }
        catch (Exception) { return 1; }
    }

    /// <summary>Installed but older than this copy of Rock Preview's service code.</summary>
    public static bool NeedsUpdate() => InstalledVersion() is > 0 and < ServiceVersion;

    // ---- install / uninstall (administrator) ---------------------------------------------------

    /// <summary>
    /// Installs or updates the service (copies this exe into Program Files, registers and starts it) and allows
    /// <paramref name="user"/> to use it. Needs administrator.
    /// </summary>
    public static void Install(string sourceExe, SecurityIdentifier user)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't open the service manager.");
        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (svc != IntPtr.Zero)
            {
                Stop(svc);
                CloseServiceHandle(svc);
            }

            Directory.CreateDirectory(InstallFolder);
            if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                CopyWithRetry(sourceExe, InstalledExe);

            string binPath = $"\"{InstalledExe}\" -service";
            svc = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                svc = CreateService(scm, ServiceName, DisplayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                    SERVICE_ERROR_NORMAL, binPath, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't create the service.");
            }
            else if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_NO_CHANGE, binPath, null, IntPtr.Zero, null, null, null, DisplayName))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't update the service.");
            try
            {
                var description = new SERVICE_DESCRIPTION { lpDescription = Description };
                ChangeServiceConfig2(svc, SERVICE_CONFIG_DESCRIPTION, ref description);
                SetRestartOnFailure(svc);
                AllowUser(user);
                using (var parameters = Registry.LocalMachine.CreateSubKey(ParametersKey))
                    parameters.SetValue(VersionValue, ServiceVersion, RegistryValueKind.DWord);
                if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ERROR_SERVICE_ALREADY_RUNNING)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't start the service.");
                WaitForState(svc, SERVICE_RUNNING, 10_000);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Stops and removes the service and its Program Files copy. Needs administrator.</summary>
    public static void Uninstall()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't open the service manager.");
        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (svc != IntPtr.Zero)
            {
                try
                {
                    Stop(svc);
                    if (!DeleteService(svc) && Marshal.GetLastWin32Error() != ERROR_SERVICE_MARKED_FOR_DELETE)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't remove the service.");
                }
                finally { CloseServiceHandle(svc); }
            }
        }
        finally { CloseServiceHandle(scm); }
        for (int i = 0; i < 20; i++)
        {
            try { if (Directory.Exists(InstallFolder)) Directory.Delete(InstallFolder, recursive: true); break; }
            catch (Exception) when (i < 19) { Thread.Sleep(250); }
            catch (Exception) { }
        }
    }

    /// <summary>Adds a user to the service's allowed list (kept with the service, which only administrators can change).</summary>
    static void AllowUser(SecurityIdentifier user)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ParametersKey);
        var users = (key.GetValue(AllowedUsersValue) as string[] ?? []).ToList();
        if (!users.Contains(user.Value, StringComparer.OrdinalIgnoreCase)) users.Add(user.Value);
        key.SetValue(AllowedUsersValue, users.ToArray(), RegistryValueKind.MultiString);
    }

    static List<SecurityIdentifier> AllowedUsers()
    {
        var list = new List<SecurityIdentifier>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ParametersKey);
            foreach (string s in key?.GetValue(AllowedUsersValue) as string[] ?? [])
            {
                try { list.Add(new SecurityIdentifier(s)); } catch (ArgumentException) { }
            }
        }
        catch (Exception) { }
        return list;
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 40) { Thread.Sleep(250); } // the stopping service may still hold the file
        }
    }

    static void Stop(IntPtr svc)
    {
        var status = new SERVICE_STATUS();
        if (QueryServiceStatus(svc, ref status) && status.dwCurrentState != SERVICE_STOPPED)
        {
            ControlService(svc, SERVICE_CONTROL_STOP, ref status);
            WaitForState(svc, SERVICE_STOPPED, 15_000);
        }
    }

    static void WaitForState(IntPtr svc, int state, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var status = new SERVICE_STATUS();
        while (sw.ElapsedMilliseconds < timeoutMs && QueryServiceStatus(svc, ref status) && status.dwCurrentState != state)
            Thread.Sleep(200);
    }

    static void SetRestartOnFailure(IntPtr svc)
    {
        // Restart after 5 s on the first two failures, then give up until the next boot; reset the count daily.
        var actions = new SC_ACTION[] { new() { Type = SC_ACTION_RESTART, Delay = 5000 }, new() { Type = SC_ACTION_RESTART, Delay = 5000 }, new() { Type = 0, Delay = 0 } };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<SC_ACTION>() * actions.Length);
        try
        {
            for (int i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], p + i * Marshal.SizeOf<SC_ACTION>(), false);
            var fa = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86400, cActions = actions.Length, lpsaActions = p };
            ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS, ref fa);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---- running as the service ------------------------------------------------------------------

    static readonly CancellationTokenSource s_stop = new();
    static IntPtr s_statusHandle;
    static readonly ServiceMainProc s_main = ServiceMain;
    static readonly HandlerEx s_handler = Handler;

    /// <summary>Entry point for "RockPreviewService.exe -service" (started by the service manager).</summary>
    public static int RunAsService()
    {
        var table = new[] { new SERVICE_TABLE_ENTRY { lpServiceName = ServiceName, lpServiceProc = s_main }, new SERVICE_TABLE_ENTRY() };
        return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        s_statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, s_handler, IntPtr.Zero);
        if (s_statusHandle == IntPtr.Zero) return;
        Report(SERVICE_RUNNING);
        try { VolumeService.Run(AllowedUsers(), s_stop.Token); }
        catch (Exception) { }
        Report(SERVICE_STOPPED);
    }

    static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
            case SERVICE_CONTROL_SHUTDOWN:
                Report(SERVICE_STOP_PENDING);
                s_stop.Cancel();
                return 0;
            case SERVICE_CONTROL_INTERROGATE:
                return 0;
            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    static void Report(int state)
    {
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0,
            dwWaitHint = state == SERVICE_STOP_PENDING ? 10_000 : 0,
        };
        SetServiceStatus(s_statusHandle, ref status);
    }

    // ---- Win32 -----------------------------------------------------------------------------------

    const int SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_ALL_ACCESS = 0xF003F;
    const int SERVICE_QUERY_STATUS = 0x0004, SERVICE_ALL_ACCESS = 0xF01FF;
    const int SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1;
    const int SERVICE_NO_CHANGE = unchecked((int)0xFFFFFFFF);
    const int SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5;
    const int SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4;
    const int SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    const int SC_ACTION_RESTART = 1;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_MARKED_FOR_DELETE = 1072, ERROR_CALL_NOT_IMPLEMENTED = 120;

    delegate void ServiceMainProc(int argc, IntPtr argv);
    delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public ServiceMainProc? lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_DESCRIPTION { public string lpDescription; }

    [StructLayout(LayoutKind.Sequential)]
    struct SC_ACTION { public int Type; public int Delay; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_FAILURE_ACTIONS
    {
        public int dwResetPeriod;
        public string? lpRebootMsg;
        public string? lpCommand;
        public int cActions;
        public IntPtr lpsaActions;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenService(IntPtr scm, string name, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateService(IntPtr scm, string name, string displayName, int access, int serviceType, int startType, int errorControl,
        string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ChangeServiceConfig(IntPtr svc, int serviceType, int startType, int errorControl, string binaryPath, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? account, string? password, string displayName);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ChangeServiceConfig2(IntPtr svc, int infoLevel, ref SERVICE_DESCRIPTION info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ChangeServiceConfig2(IntPtr svc, int infoLevel, ref SERVICE_FAILURE_ACTIONS info);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartService(IntPtr svc, int argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(IntPtr svc, int control, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DeleteService(IntPtr svc);
    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerEx handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);
}
