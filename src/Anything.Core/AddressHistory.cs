namespace Anything.Core;

/// <summary>
/// The addresses typed in the address bar that opened, newest first and at most <see cref="MaxEntries"/>, like File
/// Explorer's typed paths: the address bar's drop-down and its suggestions offer them. Safe to read from any thread
/// (Windows' suggestions ask for them on a thread of their own); changed on the UI thread.
/// </summary>
public sealed class AddressHistory
{
    /// <summary>As many as File Explorer keeps (url1..url25).</summary>
    public const int MaxEntries = 25;

    /// <summary>File Explorer's own typed paths under HKEY_CURRENT_USER: string values url1 (newest) to url25. Read, never written.</summary>
    public const string ExplorerTypedPathsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths";

    readonly object _gate = new();
    string[] _items = [];

    /// <summary>The addresses, newest first (a copy that doesn't change).</summary>
    public IReadOnlyList<string> Items { get { lock (_gate) return _items; } }

    /// <summary>Replaces the list with saved addresses (newest first): blanks, repeats and the ones past the limit go.</summary>
    public void Load(IEnumerable<string> saved)
    {
        var list = new List<string>();
        foreach (var raw in saved)
        {
            string a = raw.Trim();
            if (!Keepable(a) || list.Any(x => Same(x, a))) continue;
            list.Add(a);
            if (list.Count == MaxEntries) break;
        }
        lock (_gate) _items = [.. list];
    }

    /// <summary>
    /// An address that opened goes first; typed again, it moves to the top rather than showing twice. False when it
    /// isn't kept (blank, or with characters the settings file can't hold).
    /// </summary>
    public bool Add(string address)
    {
        string a = address.Trim();
        if (!Keepable(a)) return false;
        lock (_gate)
        {
            var list = new List<string>(_items.Length + 1) { a };
            list.AddRange(_items.Where(x => !Same(x, a)));
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            _items = [.. list];
        }
        return true;
    }

    /// <summary>Delete history: Rock Preview's own list is emptied (File Explorer's isn't touched).</summary>
    public void Clear()
    {
        lock (_gate) _items = [];
    }

    /// <summary>What the drop-down offers: Rock Preview's own addresses, then File Explorer's that aren't among them.</summary>
    public static List<string> Combine(IReadOnlyList<string> own, IEnumerable<string> explorer)
    {
        var all = new List<string>(own);
        foreach (var raw in explorer)
        {
            string e = raw.Trim();
            if (e.Length > 0 && !all.Any(x => Same(x, e))) all.Add(e);
        }
        return all;
    }

    /// <summary>
    /// File Explorer's typed paths, newest first, from <paramref name="value"/> (the value of that name in
    /// <see cref="ExplorerTypedPathsKey"/>, null when missing). Anything that isn't a string is skipped.
    /// </summary>
    public static List<string> FromExplorerTypedPaths(Func<string, object?> value)
    {
        var list = new List<string>();
        for (int i = 1; i <= MaxEntries; i++)
            if (value("url" + i) is string s && s.Trim() is { Length: > 0 } path && !list.Any(x => Same(x, path)))
                list.Add(path);
        return list;
    }

    /// <summary>The same address: case and a trailing backslash don't matter ("C:\Users\" = "c:\users").</summary>
    public static bool Same(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

    static string Key(string address)
    {
        string a = address.Trim();
        return a.Length > 3 ? a.TrimEnd('\\') : a; // "C:\" keeps its backslash
    }

    /// <summary>The settings file keeps the list on one line, separated by "|" (which no path can hold).</summary>
    static bool Keepable(string address) => address.Length > 0 && address.IndexOfAny(['|', '\r', '\n']) < 0;
}
