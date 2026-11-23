namespace Anything.Core;

/// <summary>
/// What the address bar does with text that isn't a place, as File Explorer's does: "cmd", "powershell", "wt",
/// "notepad notes.txt" or "code ." run in the folder that is open; a web address opens in the browser.
/// </summary>
public static class AddressCommand
{
    /// <summary>A program to start: its name or path, its arguments, and the folder it starts in.</summary>
    public readonly record struct Run(string File, string Arguments, string? WorkingFolder);

    /// <summary>
    /// The program and its arguments in what was typed (the first word, or a quoted path, is the program); null for
    /// nothing at all. <paramref name="folder"/>, the open folder's path (null for Home, This PC...), is where it
    /// starts. Windows Terminal is told the folder too, since it starts in its profile's own otherwise.
    /// </summary>
    public static Run? Parse(string text, string? folder)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        string file, args;
        if (t[0] == '"')
        {
            int end = t.IndexOf('"', 1);
            if (end < 0) { file = t[1..]; args = ""; }
            else { file = t[1..end]; args = t[(end + 1)..].Trim(); }
        }
        else
        {
            int space = t.IndexOf(' ');
            file = space < 0 ? t : t[..space];
            args = space < 0 ? "" : t[(space + 1)..].Trim();
        }
        if (file.Length == 0) return null;
        string? where = string.IsNullOrEmpty(folder) ? null : folder;
        string name = Path.GetFileNameWithoutExtension(file);
        if (where != null && args.Length == 0 && name.Equals("wt", StringComparison.OrdinalIgnoreCase))
            args = "-d \"" + where.TrimEnd('\\') + (where.EndsWith(":\\") || where.Length == 2 ? "\\\\" : "") + "\"";
        return new Run(file, args, where);
    }

    /// <summary>
    /// The parts of a path of display names, as File Explorer takes one typed into its address bar ("This PC\Downloads",
    /// "Documents": the names folders show, from the desktop down); null for what can't be one: a path from a drive or a
    /// network share, a "shell:" or "::{...}" name, a web address, an empty part.
    /// </summary>
    public static IReadOnlyList<string>? DisplayNameParts(string text)
    {
        string t = text.Trim().Trim('"').Trim().TrimEnd('\\');
        if (t.Length == 0 || Path.IsPathRooted(t) || t.Contains("::") || t.Contains('/') || IsWebAddress(t)
            || t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || t.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;
        var parts = t.Split('\\');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim();
            if (parts[i] is "" or "." or "..") return null;
        }
        return parts;
    }

    /// <summary>
    /// Whether a path of display names is worth looking for (which lists folders, and may wait on a network drive): only
    /// when its first part is "This PC" or one of <paramref name="placeNames"/> (the desktop's, This PC's, Home's and the
    /// user's own folders' names). Anything else ("notepda", "tools\x.exe", "ms-settings:display") is answered at once.
    /// </summary>
    public static bool WorthLookingFor(IReadOnlyList<string> parts, IEnumerable<string> placeNames)
        => parts.Count > 0 && (parts[0].Equals("This PC", StringComparison.OrdinalIgnoreCase)
                               || placeNames.Any(n => parts[0].Equals(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whose answer still counts, for a lookup that finishes later: each new one, and anything else typed or opened
    /// meanwhile (<see cref="Drop"/>), makes the earlier ones late.
    /// </summary>
    public sealed class Lookups
    {
        int _now;

        /// <summary>A new lookup; its number, to ask <see cref="IsCurrent"/> with when it answers.</summary>
        public int Begin() => ++_now;

        /// <summary>Something else happened (another address, another folder): the lookup going on is late.</summary>
        public void Drop() => _now++;

        public bool IsCurrent(int lookup) => lookup == _now;
    }

    /// <summary>
    /// Whether a program named <paramref name="file"/> ("cmd", "notepad.exe", "code") is in one of <paramref name="folders"/>
    /// (Windows' own and those of PATH), as itself or with one of the extensions of <paramref name="pathExt"/> (PATHEXT,
    /// ".COM;.EXE;.BAT"...). Lets a typed program start at once, without first looking for a folder of that name.
    /// </summary>
    public static bool FindsProgram(string file, IEnumerable<string> folders, string? pathExt)
    {
        if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false; // a bare name only
        var exts = (pathExt ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Length > 1 && e[0] == '.').ToList();
        var names = new List<string>(exts.Count + 1);
        if (Path.HasExtension(file)) names.Add(file);
        foreach (var e in exts) names.Add(file + e);
        foreach (var dir in folders)
        {
            string d = dir.Trim().Trim('"');
            if (d.Length == 0 || d.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(d)) continue;
            foreach (var n in names) if (File.Exists(Path.Combine(d, n))) return true;
        }
        return false;
    }

    /// <summary>A web address (http, https, ftp, mailto): opened in its app rather than looked for as a folder.</summary>
    public static bool IsWebAddress(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ')) return true;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "ftp" or "mailto";
    }
}
