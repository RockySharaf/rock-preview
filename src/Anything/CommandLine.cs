namespace Anything;

/// <summary>Parsed command line (Everything-compatible subset).</summary>
internal sealed class CommandLine
{
    public string? Search { get; set; }
    /// <summary>-path / -browse: open this folder in the Explorer view.</summary>
    public string? Path { get; set; }
    public bool Startup { get; set; }
    public bool NewWindow { get; set; }
    public bool Admin { get; set; }
    public bool Exit { get; set; }
    public bool Demo { get; set; }
    /// <summary>
    /// -explorer: started in place of File Explorer (Win+E, a folder or drive): a new tab in the window used last (or a
    /// new window, see Settings › General), at Path or Home.
    /// </summary>
    public bool Explorer { get; set; }
    /// <summary>Internal: this process was relaunched (elevation restart) and must wait for the old instance to exit.</summary>
    public bool Restarted { get; set; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var c = new CommandLine();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string key = a.TrimStart('-', '/').ToLowerInvariant();
            if (a.Length == 0 || (a[0] != '-' && a[0] != '/'))
            {
                // A bare argument is treated as search text (like "Everything.exe foo").
                c.Search = c.Search is null ? a : c.Search + " " + a;
                continue;
            }
            switch (key)
            {
                case "s":
                case "search":
                    if (i + 1 < args.Count) c.Search = args[++i];
                    break;
                case "path":
                case "browse":
                    if (i + 1 < args.Count) c.Path = RepairQuotedPath(args[++i]);
                    break;
                case "explorer": c.Explorer = true; break;
                case "startup": c.Startup = true; break;
                case "newwindow":
                case "new-window": c.NewWindow = true; break;
                case "admin":
                case "administrator": c.Admin = true; break;
                case "exit":
                case "quit": c.Exit = true; break;
                case "demo": c.Demo = true; break;
                case "restarted": c.Restarted = true; break;
            }
        }
        return c;
    }

    /// <summary>Arguments to forward (or to pass when relaunching), excluding process-local flags.</summary>
    public List<string> ToArgs(bool includeAdmin = false)
    {
        var l = new List<string>();
        if (Demo) l.Add("--demo");
        if (Search is not null) { l.Add("-search"); l.Add(Search); }
        if (Path is not null) { l.Add("-path"); l.Add(Path); }
        if (Startup) l.Add("-startup");
        if (NewWindow) l.Add("-newwindow");
        if (Explorer) l.Add("-explorer");
        if (Exit) l.Add("-exit");
        if (includeAdmin && Admin) l.Add("-admin");
        return l;
    }

    /// <summary>
    /// Windows passes a drive as "%1" = "C:\", and the backslash before the closing quote escapes it, so the
    /// argument arrives as C:" — turn it back into C:\.
    /// </summary>
    static string RepairQuotedPath(string path) => path.EndsWith('"') ? path[..^1].TrimEnd() + "\\" : path;

    public static string Quote(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteOne));

    static string QuoteOne(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
