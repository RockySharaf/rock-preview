namespace Anything.Core;

/// <summary>
/// "Open the folder with this item selected" from the command line: File Explorer's own "/select,&lt;path&gt;" and Rock
/// Preview's "-select &lt;path&gt;", kept apart from the windows so it can be tested.
/// </summary>
public static class ItemSelection
{
    /// <summary>Where to go: the folder to open, and the item to select in it (null = nothing to select).</summary>
    public readonly record struct Target(string Folder, string? Select);

    /// <summary>
    /// Reads a select switch at <paramref name="i"/>: "/select,C:\x", "/select," or "-select" followed by the path as
    /// the next argument (never another switch). After Explorer's comma the path is the rest of the command line, as
    /// Explorer reads it, so an unquoted path with spaces, which arrives in pieces, is joined again up to the next
    /// switch. True when the argument was one (<paramref name="i"/> then points at the last argument used, and
    /// <paramref name="path"/> is null when no path came with it); false leaves <paramref name="i"/> as it was.
    /// </summary>
    public static bool TryRead(IReadOnlyList<string> args, ref int i, out string? path)
    {
        path = null;
        string a = args[i];
        if (a.Length == 0 || (a[0] != '-' && a[0] != '/')) return false;
        string body = a.TrimStart('-', '/');
        if (!body.StartsWith("select", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = body[6..];
        if (rest.Length > 0 && rest[0] != ',') return false; // "-selected" is something else
        // Only the first comma ends the switch: a comma inside the path belongs to the path.
        bool comma = rest.Length > 0;
        rest = comma ? rest[1..] : "";
        if (rest.Trim().Length == 0)
        {
            // "/select, "C:\a b"" or "-select C:\x": the path is the next argument, unless that is another switch.
            if (i + 1 >= args.Count || IsSwitch(args[i + 1])) return true;
            rest = args[++i];
        }
        // "/select,C:\Program Files\x.exe" unquoted: Explorer takes the rest of the line, so the pieces are joined again.
        if (comma && !rest.TrimEnd().EndsWith('"'))
            while (i + 1 < args.Count && args[i + 1].Length > 0 && !IsSwitch(args[i + 1])) rest += " " + args[++i];
        path = Clean(rest);
        if (path.Length == 0) path = null;
        return true;
    }

    static bool IsSwitch(string arg) => arg.Length > 0 && (arg[0] == '-' || arg[0] == '/');

    /// <summary>
    /// The path as typed, without quotes. Windows passes "C:\dir\" as C:\dir" (the backslash before the closing quote
    /// escapes it): that comes back as C:\dir\.
    /// </summary>
    static string Clean(string path)
    {
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') return path[1..^1].Trim();
        if (path.EndsWith('"')) return path[..^1].TrimEnd() + "\\";
        return path.TrimStart('"').Trim();
    }

    /// <summary>
    /// Where <paramref name="path"/> is shown: its folder with it selected (a folder too is selected in the folder above
    /// it, as File Explorer does). A drive or share root opens itself. An item that isn't there opens the nearest folder
    /// above it that is, with nothing selected. Null when nothing on the way is there (or it isn't a full path), so the
    /// caller can say it can't be opened. <paramref name="exists"/> asks the disk and is called off the UI thread.
    /// </summary>
    public static Target? Resolve(string path, Func<string, bool> exists)
    {
        string p = Normalize(path);
        if (p.Length == 0) return null;
        string root = Path.GetPathRoot(p) ?? "";
        if (root.Length == 0) return null;
        if (p.Length <= root.Length) return exists(p) ? new Target(p, null) : null;
        string? parent = Path.GetDirectoryName(p);
        if (parent is { Length: > 0 } && exists(p)) return new Target(parent, p);
        for (string? d = parent; d is { Length: > 0 }; d = Path.GetDirectoryName(d))
            if (exists(d)) return new Target(d, null);
        return null;
    }

    /// <summary>"C:" is "C:\"; a trailing backslash goes, except on a root ("C:\dir\" is "C:\dir", the item in "C:\").</summary>
    static string Normalize(string path)
    {
        string p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.Length == 0) return "";
        if (p.Length == 2 && p[1] == ':') return p + "\\";
        string root = Path.GetPathRoot(p) ?? "";
        string trimmed = p.TrimEnd('\\');
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
