using System.Diagnostics;
using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>Helpers shared by the drive / plan / root tests.</summary>
internal static class DriveIndexingTestSupport
{
    public static bool WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    /// <summary>Full paths of the live entries, sorted.</summary>
    public static string[] Paths(FileIndex index)
    {
        index.Lock.EnterReadLock();
        try
        {
            return Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { index.Lock.ExitReadLock(); }
    }

    /// <summary>Live entries at or below <paramref name="folder"/> (its root entry renders with a trailing backslash).</summary>
    public static string[] PathsUnder(FileIndex index, string folder)
    {
        string root = folder.TrimEnd('\\');
        return Paths(index).Where(p => p.Equals(root + "\\", StringComparison.OrdinalIgnoreCase)
                                       || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>The paths a scanned folder should give: the root (with its trailing backslash) plus the relative ones.</summary>
    public static string[] Expected(string root, params string[] relative) =>
        new[] { "" }.Concat(relative)
            .Select(r => r.Length == 0 ? root.TrimEnd('\\') + "\\" : Path.Combine(root, r))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// A drive that is really a temp folder (like a volume mounted in a folder), so no real drive is touched: it is
    /// never a drive letter, so the plan scans it instead of reading a file table.
    /// </summary>
    public static DriveEntry FakeDrive(string folder, DriveKind kind = DriveKind.Fixed, bool ready = true) =>
        new(folder.TrimEnd('\\') + "\\", kind, ready ? "NTFS" : "", "Test", ready, null, null);

    /// <summary>An index manager that sees only <paramref name="drives"/> and never listens for device notifications.</summary>
    public static IndexManager Manager(Func<IReadOnlyList<DriveEntry>> drives) => new()
    {
        DriveSource = drives,
        LetterSource = () => 0,
        WatchDevices = false,
    };

    public static void WaitUntilSettled(IndexManager mgr, int timeoutMs = 15_000)
    {
        Assert.True(WaitFor(() => mgr.State == IndexState.Ready, timeoutMs), $"state {mgr.State}: {mgr.StatusText} {mgr.LastError}");
        Assert.True(mgr.WaitForBackgroundWork(timeoutMs), "background work did not finish");
        Assert.True(WaitFor(() => mgr.State == IndexState.Ready, timeoutMs), $"state {mgr.State}: {mgr.StatusText} {mgr.LastError}");
    }

    /// <summary>A drive letter that is not in use on this PC now (from the end of the alphabet), or null.</summary>
    public static char? UnusedDriveLetter()
    {
        uint mask = Drives.LetterMask();
        for (char c = 'W'; c >= 'G'; c--)
            if ((mask & (1u << (c - 'A'))) == 0) return c;
        return null;
    }

    public static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
