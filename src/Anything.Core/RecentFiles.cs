using System.Text.RegularExpressions;

namespace Anything.Core;

/// <summary>
/// The files opened lately, as Home shows them in File Explorer: Windows' Recent folder (%AppData%\Microsoft\Windows\Recent)
/// holds a shortcut to each, newest first. Shown as thumbnails, they are found in the index by an exact filter.
/// </summary>
public static class RecentFiles
{
    /// <summary>How many of the newest are shown.</summary>
    public const int Limit = 80;

    /// <summary>
    /// The files the Recent folder's shortcuts point to, newest first, each once: files only (a folder opened lately is
    /// left out), and only those still there. <paramref name="resolve"/> reads a shortcut's target.
    /// </summary>
    public static List<string> Read(string recentFolder, Func<string, string?> resolve, Func<string, bool> fileExists, int limit = Limit)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> links;
        try
        {
            links = new DirectoryInfo(recentFolder).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return result; }
        foreach (var link in links)
        {
            if (result.Count >= limit) break;
            string? target;
            try { target = resolve(link.FullName); }
            catch (Exception) { continue; }
            if (string.IsNullOrEmpty(target) || !Path.IsPathFullyQualified(target) || !seen.Add(target)) continue;
            if (fileExists(target)) result.Add(target);
        }
        return result;
    }

    /// <summary>
    /// A search term for exactly this file: its folder (parent:) and its whole name (a regular expression, which never
    /// switches to the path and takes no whole-word rule).
    /// </summary>
    public static string ExactFile(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileName(path);
        return $"<parent:\"{folder}\" regex:\"^{Regex.Escape(name)}$\">";
    }

    /// <summary>A filter for all of these files (none: one that finds nothing).</summary>
    public static string Filter(IReadOnlyList<string> paths)
        => paths.Count == 0 ? "file: !file:" : "<" + string.Join(" | ", paths.Select(ExactFile)) + ">";
}
