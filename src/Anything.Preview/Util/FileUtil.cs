using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Anything.Preview.Util;

internal static class FileUtil
{
    /// <summary>Opens a file for reading without blocking anyone else (no locks left behind).</summary>
    public static FileStream OpenShared(string path, int bufferSize = 64 * 1024, bool sequential = false) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = bufferSize,
            Options = sequential ? FileOptions.SequentialScan : FileOptions.None,
        });

    /// <summary>Reads up to <paramref name="max"/> bytes from the start of the file.</summary>
    public static byte[] ReadHead(string path, int max)
    {
        using var fs = OpenShared(path, 4096);
        return ReadUpTo(fs, max);
    }

    public static byte[] ReadUpTo(Stream s, int max)
    {
        long remaining = s.CanSeek ? Math.Max(0, s.Length - s.Position) : max;
        int n = (int)Math.Min(max, remaining);
        var buf = new byte[n];
        int read = 0;
        while (read < n)
        {
            int r = s.Read(buf, read, n - read);
            if (r <= 0) break;
            read += r;
        }
        if (read < n) Array.Resize(ref buf, read);
        return buf;
    }

    public static byte[] ReadRange(string path, long offset, int count)
    {
        using var fs = OpenShared(path, 4096);
        if (offset >= fs.Length) return Array.Empty<byte>();
        fs.Position = offset;
        return ReadUpTo(fs, count);
    }

    /// <summary>Reads the whole file if it is not larger than <paramref name="maxBytes"/>; otherwise null.</summary>
    public static byte[]? ReadAllCapped(string path, long maxBytes)
    {
        using var fs = OpenShared(path, 1 << 16, sequential: true);
        if (fs.Length > maxBytes) return null;
        return ReadUpTo(fs, (int)fs.Length);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[u] +
               $" ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
    }

    public static string FormatShortSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string HexDump(byte[] data, int max = 4096)
    {
        int n = Math.Min(max, data.Length);
        var sb = new StringBuilder(n * 4 + 64);
        for (int off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < n) sb.Append(data[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16 && off + i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append('|').Append('\n');
        }
        return sb.ToString();
    }
}

internal static class Bytes
{
    public static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int start = 0)
    {
        if (start < 0) start = 0;
        if (start >= data.Length) return -1;
        int i = data[start..].IndexOf(pattern);
        return i < 0 ? -1 : i + start;
    }

    public static int LastIndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) => data.LastIndexOf(pattern);

    public static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) => data.StartsWith(prefix);

    public static bool StartsWithAscii(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++) if (data[i] != (byte)ascii[i]) return false;
        return true;
    }

    public static ushort U16LE(ReadOnlySpan<byte> d, int o) => (ushort)(d[o] | d[o + 1] << 8);
    public static uint U32LE(ReadOnlySpan<byte> d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    public static ushort U16BE(ReadOnlySpan<byte> d, int o) => (ushort)(d[o] << 8 | d[o + 1]);
    public static uint U32BE(ReadOnlySpan<byte> d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
}
