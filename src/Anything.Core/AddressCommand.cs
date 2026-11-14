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

    /// <summary>A web address (http, https, ftp, mailto): opened in its app rather than looked for as a folder.</summary>
    public static bool IsWebAddress(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ')) return true;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "ftp" or "mailto";
    }
}
