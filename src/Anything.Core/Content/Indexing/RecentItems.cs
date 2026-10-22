using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// Targets of the newest Recent Items shortcuts (%APPDATA%\Microsoft\Windows\Recent\*.lnk), for the queue's first level
/// ("most opened"). Only each shortcut's LinkInfo is read (LocalBasePath, or the network share and path suffix), at most
/// 4 KB per file; nothing is resolved or opened through the shell.
/// </summary>
internal static class RecentItems
{
    /// <summary>The most bytes read from one shortcut.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Full paths of the targets of the newest <paramref name="max"/> shortcuts in the folder (Recent Items when null), newest first.</summary>
    public static List<string> Targets(string? folder = null, int max = 500)
    {
        var result = new List<string>();
        try
        {
            folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent");
            if (!Directory.Exists(folder)) return result;
            var files = new DirectoryInfo(folder).EnumerateFiles("*.lnk")
                .Select(f => (f.FullName, Time: SafeTime(f)))
                .OrderByDescending(f => f.Time)
                .Take(max)
                .ToList();
            var buffer = new byte[MaxBytes];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, _) in files)
            {
                int n;
                try
                {
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    n = RandomAccess.Read(handle, buffer, 0);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (Target(buffer.AsSpan(0, n)) is { } target && seen.Add(target)) result.Add(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return result;
    }

    static DateTime SafeTime(FileInfo f)
    {
        try { return f.LastWriteTimeUtc; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    static ReadOnlySpan<byte> LinkClsid => [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>
    /// The target path of a Shell Link (MS-SHLLINK) from its first bytes: LinkInfo's LocalBasePath (Unicode when present)
    /// plus CommonPathSuffix, or the network share's NetName plus the suffix; null when the shortcut has neither, or when
    /// any offset or length points outside the bytes.
    /// </summary>
    internal static string? Target(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x4C || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x4C || !data.Slice(4, 16).SequenceEqual(LinkClsid)) return null;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        const uint HasIdList = 0x1, HasLinkInfo = 0x2, ForceNoLinkInfo = 0x100;
        int pos = 0x4C;
        if ((flags & HasIdList) != 0)
        {
            if (pos + 2 > data.Length) return null;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        }
        if ((flags & HasLinkInfo) == 0 || (flags & ForceNoLinkInfo) != 0 || pos + 28 > data.Length) return null;
        var info = data[pos..];
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(info);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        uint infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (size < 28 || size > info.Length || headerSize < 28 || headerSize > size) return null;
        info = info[..(int)size];
        uint localOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        uint networkOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[20..]);
        uint suffixOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
        bool unicode = headerSize >= 0x24 && info.Length >= 0x24;
        string suffix = (unicode ? WideAt(info, BinaryPrimitives.ReadUInt32LittleEndian(info[32..])) : null) ?? AnsiAt(info, suffixOffset) ?? "";
        const uint VolumeIdAndLocalBasePath = 0x1, CommonNetworkRelativeLinkAndPathSuffix = 0x2;
        if ((infoFlags & VolumeIdAndLocalBasePath) != 0)
        {
            string? local = (unicode ? WideAt(info, BinaryPrimitives.ReadUInt32LittleEndian(info[28..])) : null) ?? AnsiAt(info, localOffset);
            if (!string.IsNullOrEmpty(local)) return Combine(local, suffix);
        }
        if ((infoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0 && networkOffset >= 28 && networkOffset + 20 <= (uint)info.Length)
        {
            var net = info[(int)networkOffset..];
            uint netSize = BinaryPrimitives.ReadUInt32LittleEndian(net);
            if (netSize < 20 || netSize > net.Length) return null;
            net = net[..(int)netSize];
            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(net[8..]);
            string? name = (nameOffset > 0x14 && net.Length >= 0x1C ? WideAt(net, BinaryPrimitives.ReadUInt32LittleEndian(net[20..])) : null) ?? AnsiAt(net, nameOffset);
            if (!string.IsNullOrEmpty(name) && name.StartsWith(@"\\", StringComparison.Ordinal)) return Combine(name, suffix);
        }
        return null;
    }

    static string Combine(string basePath, string suffix)
    {
        if (suffix.Length == 0) return basePath;
        return basePath.EndsWith('\\') ? basePath + suffix : basePath + "\\" + suffix;
    }

    static string? AnsiAt(ReadOnlySpan<byte> block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return null;
        var s = block[(int)offset..];
        int end = s.IndexOf((byte)0);
        if (end < 0) return null;
        try { return Extract.TextDecoding.Ansi().GetString(s[..end]); }
        catch (Exception e) when (e is ArgumentException or DecoderFallbackException) { return null; }
    }

    static string? WideAt(ReadOnlySpan<byte> block, uint offset)
    {
        if (offset == 0 || offset >= (uint)block.Length) return null;
        var s = block[(int)offset..];
        for (int i = 0; i + 1 < s.Length; i += 2)
            if (s[i] == 0 && s[i + 1] == 0) return Encoding.Unicode.GetString(s[..i]);
        return null;
    }
}
