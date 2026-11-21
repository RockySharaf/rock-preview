namespace Anything.Core;

/// <summary>Rules around the file operations started from the results that need no window, so they can be tested.</summary>
public static class FileOpRules
{
    /// <summary>
    /// Whether a rename, delete or move of <paramref name="targets"/> takes in <paramref name="shown"/> (the file the
    /// preview pane has open): it is one of them, or inside a folder that is. The preview is let go first then, or the
    /// shell reports the file as in use by Rock Preview. Case and a trailing backslash don't matter.
    /// </summary>
    public static bool Touches(string? shown, IEnumerable<string> targets)
    {
        if (string.IsNullOrEmpty(shown)) return false;
        string file = shown.TrimEnd('\\');
        foreach (var t in targets)
        {
            if (string.IsNullOrEmpty(t)) continue;
            string target = t.TrimEnd('\\');
            if (target.Length == 0) continue;
            if (file.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Length > target.Length && file[target.Length] == '\\'
                && file.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a path the shell couldn't turn into an item (<paramref name="error"/>, the HRESULT) has simply gone: it
    /// was "not found" and really isn't there. A delete leaves such a path out quietly; any other failure (a share that
    /// is down, a path that still <paramref name="exists"/>) left the item where it was, and the user is told.
    /// </summary>
    public static bool IsGone(int error, bool exists)
        => !exists && error is unchecked((int)0x80070002) or unchecked((int)0x80070003); // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
}
