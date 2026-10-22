using System.Runtime.InteropServices;
using Anything.Core.Indexing;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// Win32 calls of the indexer: background mode, idle time, power and notification state, free space, thread CPU time,
/// volume names and the boot id. Every call is wrapped so a missing export or a failing call answers "unknown", never throws.
/// </summary>
internal static partial class ContentNative
{
    const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000, THREAD_MODE_BACKGROUND_END = 0x00020000;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);

    /// <summary>Lowers this thread's CPU, I/O and memory priority (background mode); false when Windows refused.</summary>
    public static bool EnterBackgroundMode()
    {
        try { return SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Back to normal priorities (before holding anything a foreground path may wait on); true when the thread was in background mode.</summary>
    public static bool LeaveBackgroundMode()
    {
        try { return SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetTickCount();

    /// <summary>Time since the last keyboard or mouse input in this session; zero when unknown (counts as active).</summary>
    public static TimeSpan IdleTime()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - info.Time));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return TimeSpan.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>On battery (AC line offline) and Battery Saver on; unknown answers count as mains power, saver off.</summary>
    public static (bool OnBattery, bool BatterySaver) Power()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return (false, false);
            return (s.ACLineStatus == 0, (s.SystemStatusFlag & 1) != 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return (false, false); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>A full-screen app, a D3D game or presentation mode is showing (QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE).</summary>
    public static bool FullScreen()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    /// <summary>Free bytes for this user and the size of the drive holding the folder (or its nearest existing parent); -1 when unknown.</summary>
    public static (long Free, long Total) DiskSpace(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return (-1, -1);
        try
        {
            string? probe = folder;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe)) return (-1, -1);
            if (!GetDiskFreeSpaceEx(probe.EndsWith('\\') ? probe : probe + "\\", out ulong free, out ulong total, out _)) return (-1, -1);
            return ((long)Math.Min(free, long.MaxValue), (long)Math.Min(total, long.MaxValue));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (-1, -1);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>CPU time this thread has used (kernel + user), for calibration's wall − CPU split; zero when unknown.</summary>
    public static TimeSpan ThreadCpuTime()
    {
        try
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)) return TimeSpan.Zero;
            return TimeSpan.FromTicks(kernel + user);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return TimeSpan.Zero; }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, [Out] char[] volumePath, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, int length);

    /// <summary>The mount point holding a local path ("C:\", "C:\Mount\Data\"); null when Windows can't tell.</summary>
    public static string? VolumePathOf(string path)
    {
        try
        {
            var buffer = new char[1024];
            if (!GetVolumePathName(path, buffer, buffer.Length)) return null;
            int n = Array.IndexOf(buffer, '\0');
            return n <= 0 ? null : new string(buffer, 0, n);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return null; }
    }

    /// <summary>"\\?\Volume{GUID}\" of a mount point ("C:\"); null when it isn't one.</summary>
    public static string? VolumeNameOf(string mountPoint)
    {
        try
        {
            var buffer = new char[64];
            string mp = mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\";
            if (!GetVolumeNameForVolumeMountPoint(mp, buffer, buffer.Length)) return null;
            int n = Array.IndexOf(buffer, '\0');
            return n <= 0 ? null : new string(buffer, 0, n);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return null; }
    }

    /// <summary>The UNC path a mapped drive letter ("Z:") points at ("\\server\share\dir"), also while it is disconnected; null when it isn't mapped.</summary>
    public static string? MappedTarget(string letter)
    {
        try
        {
            var buffer = new char[1024];
            int length = buffer.Length;
            // ERROR_CONNECTION_UNAVAIL (1201): a remembered connection that is disconnected now; the share is still named.
            int error = DrivesNative.WNetGetConnectionW(letter, buffer, ref length);
            if (error != 0 && error != 1201) return null;
            int n = Array.IndexOf(buffer, '\0');
            return n <= 0 ? null : new string(buffer, 0, n);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The boot id Windows keeps (incremented at every boot), or 0 when the value isn't there; the crash marker uses it to
    /// ignore slots left by a power cut or a blue screen.
    /// </summary>
    public static uint BootId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue("BootId") is int id ? unchecked((uint)id) : 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return 0; }
    }

    /// <summary>When this PC booted (FILETIME UTC), from the tick count; drifts by the clock's adjustments, so compare with a tolerance.</summary>
    public static long BootTimeUtc() => DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64).ToFileTimeUtc();

    /// <summary>When this process started (FILETIME UTC).</summary>
    public static long ProcessStartUtc()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.StartTime.ToUniversalTime().ToFileTimeUtc();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTime.UtcNow.ToFileTimeUtc();
        }
    }
}
