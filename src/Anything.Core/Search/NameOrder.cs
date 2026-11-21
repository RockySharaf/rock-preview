namespace Anything.Core.Search;

/// <summary>
/// File Explorer's name order: runs of digits compare by their value (IMG_2, IMG_10, IMG_100), everything else by
/// character, ignoring case (upper-cased, invariant). Allocation-free, with no length limit on the digit runs, because
/// result lists of millions of names are sorted with it.
/// </summary>
internal static class NameOrder
{
    /// <summary>Compares two names, ignoring case. Zero only when they are equal ignoring case.</summary>
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;
        int zeros = 0; // first difference in leading zeros, kept for when nothing else differs
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                // Skip leading zeros, then the run with more significant digits is larger; equal lengths compare digit by digit.
                int za = i, zb = j;
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                int sa = i, sb = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                int la = i - sa, lb = j - sb;
                if (la != lb) return la < lb ? -1 : 1;
                for (int k = 0; k < la; k++)
                    if (a[sa + k] != b[sb + k]) return a[sa + k] < b[sb + k] ? -1 : 1;
                // Same value: fewer leading zeros first ("1" before "01"), unless something later decides.
                if (zeros == 0 && sa - za != sb - zb) zeros = sa - za < sb - zb ? -1 : 1;
                continue;
            }
            char ua = char.ToUpperInvariant(ca), ub = char.ToUpperInvariant(cb);
            if (ua != ub) return ua < ub ? -1 : 1;
            i++;
            j++;
        }
        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return zeros;
    }

    /// <summary>Name order, then ordinal, so names differing only in case still sort the same way every time.</summary>
    public static int CompareWithTieBreak(string a, string b)
    {
        int c = Compare(a, b);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// The start of <paramref name="s"/> as 12 units of 16 bits, packed into three keys whose order (p0, then p1, then
    /// p2) agrees with <see cref="Compare"/>: a character is its upper-case form; a digit run is '0' (it sorts against any
    /// other character as every digit does), then its count of significant digits, then those digits, so IMG_0001 and
    /// IMG_0002, or IMG_2 and IMG_10, are already apart here. Leading zeros are left out, so names differing only in them
    /// tie and are settled by the full comparison, as are names whose first 12 units agree.
    /// </summary>
    public static void Prefix(string s, out ulong p0, out ulong p1, out ulong p2)
    {
        Span<ushort> u = stackalloc ushort[12];
        u.Clear(); // units past the end of the name are 0, below every character
        int n = 0, i = 0;
        while (n < 12 && i < s.Length)
        {
            char c = s[i];
            if (!char.IsAsciiDigit(c))
            {
                u[n++] = char.ToUpperInvariant(c);
                i++;
                continue;
            }
            while (i < s.Length && s[i] == '0') i++;
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            int len = i - start;
            u[n++] = '0';
            if (n == 12) break;
            if (len >= ushort.MaxValue) { u[n] = ushort.MaxValue; break; } // never in a real name; the full comparison decides
            u[n++] = (ushort)len;
            for (int k = 0; k < len && n < 12; k++) u[n++] = s[start + k];
        }
        ulong k0 = 0, k1 = 0, k2 = 0;
        for (int k = 0; k < 4; k++)
        {
            k0 = (k0 << 16) | u[k];
            k1 = (k1 << 16) | u[k + 4];
            k2 = (k2 << 16) | u[k + 8];
        }
        p0 = k0; p1 = k1; p2 = k2;
    }
}

/// <summary>
/// <see cref="NameOrder"/> for the app's own code (demo mode sorts its names with it): File Explorer's name order, then
/// ordinal, the order the search engine sorts names in.
/// </summary>
public sealed class NameOrderComparer : IComparer<string>
{
    public static readonly NameOrderComparer Instance = new();
    NameOrderComparer() { }
    public int Compare(string? x, string? y) => x is null || y is null ? string.CompareOrdinal(x, y) : NameOrder.CompareWithTieBreak(x, y);
}
