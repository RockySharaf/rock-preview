using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anything.Core.Indexing;

/// <summary>One drive letter or mounted volume, as Windows reports it now.</summary>
/// <param name="Root">"C:\" for a drive letter, "C:\Mount\Data\" for a volume mounted in a folder. Always ends with a backslash.</param>
/// <param name="FileSystem">"NTFS", "ReFS", "exFAT", "FAT32", "CDFS", "UDF"... Empty when the drive is not ready.</param>
/// <param name="IsReady">False for an empty card reader, a BitLocker-locked drive or an unreachable network drive.</param>
/// <param name="NetworkPath">For a mapped network drive, the share it maps ("\\server\share").</param>
/// <param name="SubstTarget">For a SUBST drive, the folder it shows ("C:\Projects"); such a drive repeats files of another drive.</param>
public sealed record DriveEntry(string Root, DriveKind Kind, string FileSystem, string Label, bool IsReady, string? NetworkPath, string? SubstTarget)
{
    /// <summary>True for "X:\", false for a volume mounted in a folder.</summary>
    public bool IsDriveLetter => Root.Length == 3 && Root[1] == ':';
}

/// <summary>
/// Lists drives and mounted volumes the way Everything finds them: every drive letter (GetLogicalDrives, so SUBST,
/// mapped network and locked drives are seen too) plus volumes mounted in folders (FindFirstVolume). Never shows
/// "Insert a disk" prompts, and never waits long on an unreachable network drive.
/// </summary>
public static class Drives
{
    /// <summary>How long to wait for a network drive to answer before reporting it not ready.</summary>
    public static int NetworkTimeoutMs { get; set; } = 2000;

    // A probe of an unreachable server can block for half a minute; it keeps running in the background and later
    // listings reuse its answer instead of starting another one.
    static readonly ConcurrentDictionary<string, Task<VolumeInfo?>> NetworkProbes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drive letters set in the current session (bit 0 = A:), cheap enough to poll.</summary>
    public static uint LetterMask() => DrivesNative.GetLogicalDrives();

    /// <param name="probeNetwork">
    /// Ask each network drive's server whether it answers (all at once, waiting at most <see cref="NetworkTimeoutMs"/> in
    /// total). Off: network drives are listed from the local mapping only, as ready, with no file system: what the index
    /// needs (it keeps network drives whether or not they answer), without touching the network.
    /// </param>
    public static IReadOnlyList<DriveEntry> List(bool includeMountedFolders = true, bool probeNetwork = true)
    {
        var list = new List<DriveEntry>();
        uint mask = DrivesNative.GetLogicalDrives();
        using (QuietErrors())
        {
            var roots = new List<string>();
            for (int i = 0; i < 26; i++)
                if ((mask & (1u << i)) != 0) roots.Add((char)('A' + i) + @":\");
            // Start every network probe first, so several unreachable servers cost one timeout, not one each.
            long deadline = Environment.TickCount64 + NetworkTimeoutMs;
            var probes = new Dictionary<string, Task<VolumeInfo?>>(StringComparer.OrdinalIgnoreCase);
            if (probeNetwork)
                foreach (var r in roots)
                    if (DrivesNative.GetDriveTypeW(r) == 4) probes[r] = StartProbe(r);
            foreach (var r in roots)
            {
                try { list.Add(Describe(r, probeNetwork, deadline, probes.GetValueOrDefault(r))); }
                catch (Exception) { /* a drive that vanished while we looked */ }
            }
            if (includeMountedFolders)
            {
                try { list.AddRange(MountedFolders()); }
                catch (Exception) { }
            }
            try { list.AddRange(HiddenMappings(mask, RememberedMappings())); }
            catch (Exception) { }
        }
        return list;
    }

    /// <summary>
    /// Network drives the user mapped that this program can't see by their letter: Windows keeps a program running as
    /// administrator apart from the drive letters of the user's normal session (unless EnableLinkedConnections is set),
    /// so a mapped Z: is missing there. The remembered ones are listed by their share ("\\server\share"), which works
    /// in either session; one whose letter is here is listed under the letter as usual.
    /// </summary>
    internal static List<DriveEntry> HiddenMappings(uint mask, IEnumerable<(string Letter, string RemotePath)> remembered)
    {
        var found = new List<DriveEntry>();
        foreach (var (letter, remote) in remembered)
        {
            if (letter.Length == 0 || !char.IsAsciiLetter(letter[0])) continue;
            int bit = char.ToUpperInvariant(letter[0]) - 'A';
            if ((mask & (1u << bit)) != 0) continue; // seen under its letter
            string share = remote.Trim().TrimEnd('\\');
            if (!share.StartsWith(@"\\", StringComparison.Ordinal) || share.Length < 5) continue;
            if (found.Exists(d => string.Equals(d.NetworkPath, share, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new DriveEntry(share + "\\", DriveKind.Network, "", $"{char.ToUpperInvariant(letter[0])}: from your normal session", true, share, null));
        }
        return found;
    }

    /// <summary>The user's remembered network drives (HKCU\Network: one key per letter, with its share in RemotePath).</summary>
    static IEnumerable<(string Letter, string RemotePath)> RememberedMappings()
    {
        using var network = Registry.CurrentUser.OpenSubKey("Network");
        if (network == null) return [];
        var list = new List<(string, string)>();
        foreach (var letter in network.GetSubKeyNames())
        {
            using var key = network.OpenSubKey(letter);
            if (key?.GetValue("RemotePath") is string remote) list.Add((letter, remote));
        }
        return list;
    }

    /// <summary>Describes one drive letter ("X:\") now.</summary>
    public static DriveEntry Describe(string root) => Describe(root, probeNetwork: true, Environment.TickCount64 + NetworkTimeoutMs, null);

    static DriveEntry Describe(string root, bool probeNetwork, long deadline, Task<VolumeInfo?>? probe)
    {
        root = root.Length == 2 ? root + "\\" : root;
        var kind = KindOf(DrivesNative.GetDriveTypeW(root));
        string letter = root[..2];
        string? subst = SubstTarget(letter);
        if (kind == DriveKind.Network)
        {
            string? share = NetworkShare(letter);
            if (!probeNetwork) return new DriveEntry(root, kind, "", "", true, share, subst);
            var answer = AwaitProbe(root, probe ?? StartProbe(root), deadline);
            return new DriveEntry(root, kind, answer?.FileSystem ?? "", answer?.Label ?? "", answer != null, share, subst);
        }
        var info = VolumeInfo.Read(root);
        return new DriveEntry(root, kind, info?.FileSystem ?? "", info?.Label ?? "", info != null, null, subst);
    }

    /// <summary>What kind of drive <paramref name="root"/> ("X:\") is, without touching the drive or the network.</summary>
    public static DriveKind KindOf(string root) => KindOf(DrivesNative.GetDriveTypeW(root.Length == 2 ? root + "\\" : root));

    static DriveKind KindOf(uint type) => type switch
    {
        2 => DriveKind.Removable,
        3 => DriveKind.Fixed,
        4 => DriveKind.Network,
        5 => DriveKind.Optical,
        6 => DriveKind.Ram,
        _ => DriveKind.Unknown,
    };

    /// <summary>The folder a SUBST drive shows, from its DOS device ("\??\C:\Projects"); null for real drives.</summary>
    static string? SubstTarget(string letter)
    {
        var buffer = new char[1024];
        uint n = DrivesNative.QueryDosDeviceW(letter, buffer, buffer.Length);
        if (n == 0) return null;
        string target = new string(buffer, 0, (int)n).Split('\0')[0];
        if (!target.StartsWith(@"\??\", StringComparison.Ordinal)) return null;
        target = target[4..];
        if (target.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) target = @"\\" + target[4..];
        return target.TrimEnd('\\') is { Length: > 0 } t ? (t.Length == 2 && t[1] == ':' ? t + "\\" : t) : null;
    }

    /// <summary>The share a mapped drive points at ("\\server\share"), also while it is disconnected.</summary>
    static string? NetworkShare(string letter)
    {
        var buffer = new char[1024];
        int size = buffer.Length;
        int err = DrivesNative.WNetGetConnectionW(letter, buffer, ref size);
        // ERROR_CONNECTION_UNAVAIL: a remembered connection that is disconnected now; the share name is still filled in.
        if (err != 0 && err != 1201) return null;
        int end = Array.IndexOf(buffer, '\0');
        return end > 0 ? new string(buffer, 0, end).TrimEnd('\\') : null;
    }

    /// <summary>
    /// The probe running for <paramref name="root"/>, or a new one. Only a probe still running is shared: one that
    /// finished after its listing gave up is stale (the server may have gone or come back since).
    /// </summary>
    static Task<VolumeInfo?> StartProbe(string root)
    {
        var probe = NetworkProbes.GetOrAdd(root, Probe);
        if (!probe.IsCompleted) return probe;
        NetworkProbes.TryRemove(new KeyValuePair<string, Task<VolumeInfo?>>(root, probe));
        return NetworkProbes.GetOrAdd(root, Probe);

        static Task<VolumeInfo?> Probe(string r) => Task.Run(() =>
        {
            using (QuietErrors()) return VolumeInfo.Read(r);
        });
    }

    static VolumeInfo? AwaitProbe(string root, Task<VolumeInfo?> probe, long deadline)
    {
        try
        {
            int wait = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (!probe.Wait(wait)) return null; // still waiting on the server: not ready for now
        }
        catch (Exception) { }
        // Finished: forget it, so the next listing asks again (the server may come back or go away).
        NetworkProbes.TryRemove(new KeyValuePair<string, Task<VolumeInfo?>>(root, probe));
        return probe.IsCompletedSuccessfully ? probe.Result : null;
    }

    /// <summary>Volumes mounted in a folder (not at a drive letter), e.g. a disk mounted at C:\Mount\Data.</summary>
    static IEnumerable<DriveEntry> MountedFolders()
    {
        var found = new List<DriveEntry>();
        var name = new char[64];
        nint h = DrivesNative.FindFirstVolumeW(name, name.Length);
        if (h == -1) return found;
        try
        {
            do
            {
                string volume = new string(name, 0, Math.Max(0, Array.IndexOf(name, '\0')));
                // One entry per volume, as Everything indexes it: none when it also has a drive letter (listed under
                // that), else its first folder path in a stable order (a volume can be mounted in several folders).
                var paths = MountPaths(volume);
                if (paths.Count == 0 || paths.Exists(p => p.Length <= 3)) continue;
                string path = paths.Min(StringComparer.OrdinalIgnoreCase)!;
                var kind = KindOf(DrivesNative.GetDriveTypeW(path));
                if (kind is DriveKind.Network or DriveKind.Unknown) continue;
                var info = VolumeInfo.Read(path);
                found.Add(new DriveEntry(path, kind, info?.FileSystem ?? "", info?.Label ?? "", info != null, null, null));
            }
            while (DrivesNative.FindNextVolumeW(h, name, name.Length));
        }
        finally { DrivesNative.FindVolumeClose(h); }
        return found;
    }

    static List<string> MountPaths(string volume)
    {
        var result = new List<string>();
        var buffer = new char[1024];
        if (!DrivesNative.GetVolumePathNamesForVolumeNameW(volume, buffer, buffer.Length, out int needed))
        {
            if (needed <= buffer.Length || needed > 1 << 20) return result;
            buffer = new char[needed];
            if (!DrivesNative.GetVolumePathNamesForVolumeNameW(volume, buffer, buffer.Length, out _)) return result;
        }
        foreach (var p in new string(buffer).Split('\0'))
        {
            if (p.Length == 0) break;
            result.Add(p.EndsWith('\\') ? p : p + "\\");
        }
        return result;
    }

    /// <summary>Keeps Windows from asking "Insert a disk into drive E:" while we look at empty drives.</summary>
    internal static ErrorModeScope QuietErrors() => new();

    internal readonly struct ErrorModeScope : IDisposable
    {
        const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOOPENFILEERRORBOX = 0x8000;
        readonly uint _old;
        readonly bool _set;

        public ErrorModeScope() => _set = DrivesNative.SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX, out _old);

        public void Dispose()
        {
            if (_set) DrivesNative.SetThreadErrorMode(_old, out _);
        }
    }

    sealed record VolumeInfo(string FileSystem, string Label)
    {
        public static VolumeInfo? Read(string root)
        {
            var label = new char[261];
            var fs = new char[261];
            if (!DrivesNative.GetVolumeInformationW(root, label, label.Length, out _, out _, out _, fs, fs.Length)) return null;
            return new VolumeInfo(Text(fs), Text(label));
        }

        static string Text(char[] s) => new(s, 0, Math.Max(0, Array.IndexOf(s, '\0')));
    }
}

internal static partial class DrivesNative
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetLogicalDrives();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveTypeW(string root);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDeviceW(string device, [Out] char[] target, int max);

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetGetConnectionW(string local, [Out] char[] remote, ref int length);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformationW(string root, [Out] char[] label, int labelSize, out uint serial,
        out uint maxComponent, out uint flags, [Out] char[] fileSystem, int fileSystemSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstVolumeW([Out] char[] name, int size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolumeW(nint handle, [Out] char[] name, int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeNameW(string volume, [Out] char[] names, int size, out int needed);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint mode, out uint oldMode);
}
