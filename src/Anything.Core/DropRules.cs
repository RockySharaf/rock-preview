namespace Anything.Core;

/// <summary>Rules for dropping files on a folder that need no window, so they can be tested.</summary>
public static class DropRules
{
    /// <summary>
    /// Whether a drop of <paramref name="dragged"/> on <paramref name="folder"/> is refused (the folder isn't
    /// highlighted and the pointer shows "no"): it would only copy the files next to themselves
    /// (<see cref="OnlyIntoItsOwnFolder"/>), or put a folder inside itself (<see cref="IntoItself"/>).
    /// </summary>
    public static bool Refused(IReadOnlyList<string> dragged, string folder)
        => OnlyIntoItsOwnFolder(dragged, folder) || IntoItself(dragged, folder);

    /// <summary>
    /// Whether dropping <paramref name="dragged"/> on <paramref name="folder"/> would only put the files where they
    /// already are: every item is that folder itself or sits directly inside it. Rock Preview copies on a plain drop, so
    /// such a drop would make "name - Copy" duplicates, where File Explorer (which moves within a drive) does nothing.
    /// False when nothing was dragged as files (Outlook's mails) or when one item is anywhere else.
    /// </summary>
    public static bool OnlyIntoItsOwnFolder(IReadOnlyList<string> dragged, string folder)
    {
        if (dragged.Count == 0 || string.IsNullOrWhiteSpace(folder)) return false;
        string target = Trim(folder);
        foreach (string item in dragged)
        {
            string path = Trim(item);
            if (path.Length == 0) return false;
            if (Same(path, target)) continue;
            int slash = path.LastIndexOf('\\');
            if (slash <= 0 || !Same(path[..slash], target)) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether one of <paramref name="dragged"/> is <paramref name="folder"/> itself or a folder it is inside: Windows
    /// can't copy or move a folder into itself, and says so only after the drop, with an error. A folder dropped on a
    /// folder further up (its parent's parent) is fine.
    /// </summary>
    public static bool IntoItself(IReadOnlyList<string> dragged, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string target = Trim(folder);
        foreach (string item in dragged)
        {
            string path = Trim(item);
            if (path.Length == 0) continue;
            if (Same(path, target)) return true;
            if (target.Length > path.Length && target[path.Length] == '\\' && Same(target[..path.Length], path)) return true;
        }
        return false;
    }

    /// <summary>Case and trailing backslashes don't matter ("C:\" is "c:", "\\server\share\" is "\\server\share").</summary>
    static string Trim(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
