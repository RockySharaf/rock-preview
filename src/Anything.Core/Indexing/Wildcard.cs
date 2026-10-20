namespace Anything.Core.Indexing;

/// <summary>
/// One exclude pattern, matched case-insensitively against a whole name or path: * is any run of characters, backslashes
/// included; ? is exactly one character; everything else is literal. There are no escapes: FileSystemName reads '\' as
/// one, so it never matches path rules such as C:\Users\*\Desktop.
/// </summary>
internal readonly struct Wildcard
{
    enum Shape : byte { Exact, Prefix, Suffix, Any, General }

    readonly string _literal; // the pattern; for Prefix and Suffix, without its star
    readonly Shape _shape;

    public Wildcard(string pattern)
    {
        Pattern = _literal = pattern;
        var p = pattern.AsSpan();
        int wild = p.IndexOfAny('*', '?');
        // Plain names, "*.tmp" and "C:\Program*" are most rules: those need only one compare.
        if (wild < 0) _shape = Shape.Exact;
        else if (p.TrimStart('*').IsEmpty) _shape = Shape.Any;
        else if (wild == p.Length - 1 && p[wild] == '*') { _shape = Shape.Prefix; _literal = pattern[..^1]; }
        else if (wild == 0 && p[0] == '*' && p[1..].IndexOfAny('*', '?') < 0) { _shape = Shape.Suffix; _literal = pattern[1..]; }
        else _shape = Shape.General;
    }

    public string Pattern { get; }

    public bool IsMatch(ReadOnlySpan<char> text) => _shape switch
    {
        Shape.Exact => text.Equals(_literal, StringComparison.OrdinalIgnoreCase),
        Shape.Prefix => text.StartsWith(_literal, StringComparison.OrdinalIgnoreCase),
        Shape.Suffix => text.EndsWith(_literal, StringComparison.OrdinalIgnoreCase),
        Shape.Any => true,
        _ => IsMatch(Pattern, text),
    };

    /// <summary>Greedy match: on a mismatch, the last * takes one more character and matching resumes after it.</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; resume = t; }
            else if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], text[t]))) { p++; t++; }
            else if (star < 0) return false;
            else { p = star + 1; t = ++resume; }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static bool SameChar(char a, char b)
    {
        if (a == b) return true;
        // ASCII: only letters differ by case, and only in the 0x20 bit.
        if ((a | b) < 0x80) return (a | 0x20) == (b | 0x20) && (uint)((a | 0x20) - 'a') <= 'z' - 'a';
        return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
