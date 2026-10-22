namespace Anything.Core.Content;

/// <summary>
/// Document keys: FNV-1a-64 chained over a volume key and the upper-case path components below it (known-answer tests pin
/// it, because a future shared index needs the same keys on every PC). Every UTF-16 code unit is upper-cased invariantly
/// and hashed low byte, then high byte; the volume key is hashed the same way, so its case doesn't matter either.
/// </summary>
internal static class ContentKeys
{
    const ulong Offset64 = 14695981039346656037UL, Prime64 = 1099511628211UL;
    const uint Offset32 = 2166136261U, Prime32 = 16777619U;

    /// <summary>The state after the volume key ("vol:{GUID}", "unc:\\SERVER\SHARE", "path:X:").</summary>
    public static ulong Start(string volumeKey) => Hash(Offset64, volumeKey);

    /// <summary>The state after '\' plus one more component (upper-invariant UTF-16, low byte then high byte).</summary>
    public static ulong Continue(ulong state, ReadOnlySpan<char> component) => Hash(Step(state, '\\'), component);

    /// <summary>The key of a volume-relative path ("Users\Me\a.pdf"); '\' or '/' separate components, empty ones are skipped.</summary>
    public static ulong Of(string volumeKey, ReadOnlySpan<char> relativePath)
    {
        ulong state = Start(volumeKey);
        while (!relativePath.IsEmpty)
        {
            int cut = relativePath.IndexOfAny('\\', '/');
            var part = cut < 0 ? relativePath : relativePath[..cut];
            if (!part.IsEmpty) state = Continue(state, part);
            relativePath = cut < 0 ? default : relativePath[(cut + 1)..];
        }
        return state;
    }

    /// <summary>FNV-1a-32 of an upper-case leaf name, for move detection.</summary>
    public static uint NameHash(ReadOnlySpan<char> name)
    {
        uint h = Offset32;
        foreach (char ch in name)
        {
            char c = char.ToUpperInvariant(ch);
            h = (h ^ (byte)c) * Prime32;
            h = (h ^ (byte)(c >> 8)) * Prime32;
        }
        return h;
    }

    static ulong Hash(ulong h, ReadOnlySpan<char> s)
    {
        foreach (char ch in s) h = Step(h, char.ToUpperInvariant(ch));
        return h;
    }

    static ulong Step(ulong h, char c)
    {
        h = (h ^ (byte)c) * Prime64;
        return (h ^ (byte)(c >> 8)) * Prime64;
    }
}
