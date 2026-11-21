using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Anything.Tests")]
// The preview (Anything.Preview) splits text into lines with Content.TextLines and decodes text with TextDecoding, so the
// line numbers and the code page it shows are the ones the index uses.
[assembly: InternalsVisibleTo("Anything.Preview")]

namespace Anything.Core.Search;

/// <summary>
/// Precomputed one-to-one character fold tables (case and/or diacritics). Because every
/// mapping is char-to-char, positions in a folded string line up with the original, which
/// keeps highlighting simple.
/// </summary>
internal static class Fold
{
    /// <summary>Lower-case only (diacritics kept).</summary>
    public static readonly char[] Lower = new char[65536];
    /// <summary>Diacritics stripped only (case kept).</summary>
    public static readonly char[] Strip = new char[65536];
    /// <summary>Lower-case and diacritics stripped (the default search mode).</summary>
    public static readonly char[] LowerStrip = new char[65536];

    static Fold()
    {
        for (int c = 0; c < 65536; c++)
        {
            char ch = (char)c;
            Lower[c] = char.ToLowerInvariant(ch);
            Strip[c] = ch;
        }
        var sb = new StringBuilder(8);
        for (int c = 0xC0; c < 65536; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF) continue;
            char ch = (char)c;
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.OtherLetter))
                continue;
            string d;
            try { d = ch.ToString().Normalize(NormalizationForm.FormD); }
            catch (ArgumentException) { continue; }
            if (d.Length < 2) continue;
            bool allMarks = true;
            for (int i = 1; i < d.Length; i++)
                if (CharUnicodeInfo.GetUnicodeCategory(d[i]) != UnicodeCategory.NonSpacingMark) { allMarks = false; break; }
            if (allMarks && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark)
                Strip[c] = d[0];
        }
        for (int c = 0; c < 65536; c++)
            LowerStrip[c] = Strip[Lower[Strip[c]]];
    }

    /// <summary>The table for a match mode; null means identity (case- and diacritic-sensitive).</summary>
    public static char[]? Table(bool matchCase, bool matchDiacritics)
        => matchCase ? (matchDiacritics ? null : Strip) : (matchDiacritics ? Lower : LowerStrip);

    public static string Apply(string s, char[]? table)
    {
        if (table == null) return s;
        if (!ReferenceEquals(table, Lower))
        {
            try { s = s.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
        }
        return string.Create(s.Length, (s, table), static (span, st) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = st.table[st.s[i]];
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char F(char c, char[]? table) => table == null ? c : table[c];
}

/// <summary>A folded substring needle with an ASCII fast path (vectorized search) and a fold-table slow path.</summary>
internal sealed class Needle
{
    public string Text { get; }
    readonly char[]? _table;
    readonly bool _caseSensitive;
    readonly bool _ascii;
    readonly SearchValues<string>? _values;

    public Needle(string raw, bool matchCase, bool matchDiacritics)
    {
        _table = Fold.Table(matchCase, matchDiacritics);
        _caseSensitive = matchCase;
        Text = Fold.Apply(raw, _table);
        _ascii = Ascii.IsValid(Text);
        if (!_caseSensitive && _ascii && Text.Length > 0)
            _values = SearchValues.Create([Text], StringComparison.OrdinalIgnoreCase);
    }

    public int Length => Text.Length;

    /// <summary>Index of the first occurrence at or after <paramref name="start"/>, or -1.</summary>
    public int IndexOf(ReadOnlySpan<char> hay, int start = 0)
    {
        if (Text.Length == 0) return start <= hay.Length ? start : -1;
        if (hay.Length - start < Text.Length) return -1;
        var h = hay[start..];
        int r;
        if (Ascii.IsValid(h))
        {
            // An ASCII haystack folds to ASCII, so a non-ASCII needle can never match it.
            if (!_ascii) return -1;
            r = _caseSensitive ? h.IndexOf(Text.AsSpan(), StringComparison.Ordinal) : h.IndexOfAny(_values!);
        }
        else if (_table == null) r = h.IndexOf(Text.AsSpan(), StringComparison.Ordinal);
        else r = SlowIndexOf(h);
        return r < 0 ? -1 : r + start;
    }

    int SlowIndexOf(ReadOnlySpan<char> h)
    {
        string n = Text;
        char[] t = _table!;
        char first = n[0];
        int last = h.Length - n.Length;
        for (int i = 0; i <= last; i++)
        {
            if (t[h[i]] != first) continue;
            int j = 1;
            while (j < n.Length && t[h[i + j]] == n[j]) j++;
            if (j == n.Length) return i;
        }
        return -1;
    }

    public bool Contains(ReadOnlySpan<char> hay, bool wholeWord)
    {
        if (!wholeWord) return IndexOf(hay) >= 0;
        int pos = 0;
        while (true)
        {
            int i = IndexOf(hay, pos);
            if (i < 0) return false;
            if (TextUtil.IsWordBoundary(hay, i, Text.Length)) return true;
            pos = i + 1;
        }
    }

    public bool StartsWith(ReadOnlySpan<char> hay) => TextUtil.EqualsFold(hay, 0, Text, _table);
    public bool EndsWith(ReadOnlySpan<char> hay) => hay.Length >= Text.Length && TextUtil.EqualsFold(hay, hay.Length - Text.Length, Text, _table);
    public bool EqualsWhole(ReadOnlySpan<char> hay) => hay.Length == Text.Length && TextUtil.EqualsFold(hay, 0, Text, _table);
}

internal static class TextUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// True when the match at <paramref name="start"/> stands as whole words. A side needs a boundary only when the
    /// match's own edge character there is a letter or digit, so ".pdf" matches "invoice.pdf" and "projects\" matches
    /// "projects\app". Scripts written without spaces between words (Han, kana, Hangul, Thai, Lao, Khmer, Myanmar,
    /// Tibetan) need no boundary, or a Chinese name could only be found by a complete name part; Arabic, Hebrew and every
    /// other script need one exactly as Latin does. Highlighting uses this same test, so it marks what was matched.
    /// </summary>
    public static bool IsWordBoundary(ReadOnlySpan<char> s, int start, int length)
    {
        if (length <= 0) return true;
        int end = start + length;
        return (start == 0 || !Joined(s[start - 1], s[start])) && (end >= s.Length || !Joined(s[end - 1], s[end]));
    }

    // Two adjacent characters belong to one word when both are word characters and neither is from a script
    // written without spaces between words.
    static bool Joined(char a, char b)
        => IsWordChar(a) && IsWordChar(b) && !IsUnspacedLetter(a) && !IsUnspacedLetter(b);

    /// <summary>
    /// A character of a script written without spaces between words, by Unicode block (letters and the marks that act as
    /// letters there, such as the long-vowel mark ー, 々 and Thai ๆ). Supplementary ideographs are surrogates, which are
    /// not word characters at all, so they never need a boundary either.
    /// </summary>
    internal static bool IsUnspacedLetter(char c) => c >= 0x0E00 && c switch
    {
        <= '໿' => true,                     // Thai, Lao
        >= 'ༀ' and <= '࿿' => true,     // Tibetan
        >= 'က' and <= '႟' => true,     // Myanmar
        >= 'ᄀ' and <= 'ᇿ' => true,     // Hangul Jamo
        >= 'ក' and <= '៿' => true,     // Khmer
        >= '᧠' and <= '᧿' => true,     // Khmer symbols
        >= '⺀' and <= '⿟' => true,     // CJK radicals, Kangxi radicals
        >= '々' and <= '〇' => true,     // 々 〆 〇
        >= '〱' and <= '〵' => true,     // vertical kana repeat marks
        >= '〻' and <= '〼' => true,     // 〻 〼
        >= '぀' and <= 'ㄯ' => true,     // Hiragana, Katakana, Bopomofo
        >= '㄰' and <= '㆏' => true,     // Hangul compatibility jamo
        >= 'ㆠ' and <= 'ㆿ' => true,     // Bopomofo extended
        >= 'ㇰ' and <= 'ㇿ' => true,     // Katakana phonetic extensions
        >= '㐀' and <= '䶿' => true,     // CJK extension A
        >= '一' and <= '鿿' => true,     // CJK unified ideographs
        >= 'ꥠ' and <= '꥿' => true,     // Hangul Jamo extended A
        >= 'ꧠ' and <= '꧿' => true,     // Myanmar extended B
        >= 'ꩠ' and <= 'ꩿ' => true,     // Myanmar extended A
        >= '가' and <= '퟿' => true,     // Hangul syllables, Jamo extended B
        >= '豈' and <= '﫿' => true,     // CJK compatibility ideographs
        >= 'ｦ' and <= 'ￜ' => true,     // halfwidth Katakana (with ｰ) and Hangul
        _ => false,
    };

    /// <summary>True when <paramref name="hay"/> at <paramref name="offset"/> starts with the already-folded <paramref name="folded"/>.</summary>
    public static bool EqualsFold(ReadOnlySpan<char> hay, int offset, string folded, char[]? table)
    {
        if (offset < 0 || hay.Length - offset < folded.Length) return false;
        if (table == null) return hay.Slice(offset, folded.Length).SequenceEqual(folded);
        for (int i = 0; i < folded.Length; i++)
            if (table[hay[offset + i]] != folded[i]) return false;
        return true;
    }

    public static bool HasWildcards(string s) => s.AsSpan().IndexOfAny('*', '?') >= 0;
}

/// <summary>The search engine's whole-word rule, for the app's own code (demo mode matches with it).</summary>
public static class WholeWord
{
    /// <summary>True when the match of <paramref name="length"/> characters at <paramref name="start"/> stands as whole words.</summary>
    public static bool IsMatch(ReadOnlySpan<char> s, int start, int length) => TextUtil.IsWordBoundary(s, start, length);
}

/// <summary>Whole-string wildcard matcher (<c>*</c> = any run, <c>?</c> = one char) with fold support.</summary>
internal sealed class GlobPattern
{
    readonly string _pattern;          // folded pattern
    readonly char[]? _table;
    readonly bool _hasQuestion;
    readonly string[] _segments;       // split on '*'
    readonly Needle[] _middle;         // needles for segments 1..n-2

    public GlobPattern(string pattern, bool matchCase, bool matchDiacritics)
    {
        _table = Fold.Table(matchCase, matchDiacritics);
        _pattern = Fold.Apply(pattern, _table);
        _hasQuestion = _pattern.Contains('?');
        _segments = _pattern.Split('*');
        // Middle segments use table-aware needles built from the raw pattern (they fold themselves).
        _middle = _segments.Length > 2 ? BuildMiddle(pattern, matchCase, matchDiacritics) : [];
    }

    static Needle[] BuildMiddle(string rawPattern, bool matchCase, bool matchDiacritics)
    {
        var raw = rawPattern.Split('*');
        var list = new List<Needle>();
        for (int i = 1; i < raw.Length - 1; i++)
            if (raw[i].Length > 0) list.Add(new Needle(raw[i], matchCase, matchDiacritics));
        return list.ToArray();
    }

    /// <summary>Literal (non-wildcard) pieces of the pattern, folded.</summary>
    public IEnumerable<string> LiteralSegments => _pattern.Split('*', '?').Where(s => s.Length > 0);

    public bool IsMatch(ReadOnlySpan<char> s)
    {
        if (_hasQuestion) return GenericMatch(s);
        if (_segments.Length == 1) return s.Length == _pattern.Length && TextUtil.EqualsFold(s, 0, _pattern, _table);
        string first = _segments[0], last = _segments[^1];
        if (s.Length < first.Length + last.Length) return false;
        if (!TextUtil.EqualsFold(s, 0, first, _table)) return false;
        if (!TextUtil.EqualsFold(s, s.Length - last.Length, last, _table)) return false;
        int pos = first.Length, end = s.Length - last.Length;
        foreach (var n in _middle)
        {
            int i = n.IndexOf(s[..end], pos);
            if (i < 0) return false;
            pos = i + n.Length;
        }
        return true;
    }

    bool GenericMatch(ReadOnlySpan<char> s)
    {
        string p = _pattern;
        char[]? t = _table;
        int si = 0, pi = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || (p[pi] != '*' && p[pi] == Fold.F(s[si], t)))) { si++; pi++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}
