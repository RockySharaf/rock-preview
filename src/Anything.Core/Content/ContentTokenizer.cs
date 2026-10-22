using System.Globalization;
using System.Text;
using CoreFold = Anything.Core.Search.Fold;

namespace Anything.Core.Content;

/// <summary>How a token came out of the text.</summary>
internal enum TokenKind : byte { Word, Part, Compound, Bigram, Field }

/// <summary>Receives tokens in text order (a code's compound right after its parts); folded is valid only during the call.</summary>
internal interface ITokenSink { void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind); }

/// <summary>One search word analysed by the index's rules.</summary>
/// <param name="Text">The word as typed, NFC.</param>
/// <param name="Tokens">Folded tokens in order (tokens under MinLength kept for verification).</param>
/// <param name="Compound">The joined form when the word is a code (INV-2024-001 → inv2024001), else null.</param>
/// <param name="NeedsSequence">Several tokens: they must stand next to each other (checked in stored text).</param>
internal sealed record QueryWord(string Text, IReadOnlyList<string> Tokens, string? Compound, bool LastIsPrefix, bool NeedsSequence)
{
    /// <summary>The word folded as stored, separators kept (a bigram word's run is matched against this).</summary>
    public string Folded { get; init; } = "";
    /// <summary>How each token was made (parallel to Tokens): Word, Part (of a code) or Bigram (a bigram, or one character of a bigram script, always looked up as a prefix).</summary>
    public IReadOnlyList<TokenKind> Kinds { get; init; } = [];
}

/// <summary>
/// The index's word rules, shared by indexing, queries, verification and highlighting (version 1).
/// <list type="bullet">
/// <item>A word character is a letter or digit (a Rune; agrees with TextUtil.IsWordChar on the BMP). Marks continue the
/// word and stay in the token, except the combining-diacritic blocks, Hebrew points and Arabic harakat, which are dropped.
/// The invisible format characters soft hyphen, ZWNJ, ZWJ, word joiner and U+FEFF continue a word too and are always
/// left out (under every match flag), so "Donau&#173;dampf" is one word; U+200B (zero-width space) separates words.</item>
/// <item>Fold = Fold.LowerStrip, plus full-width ASCII → ASCII (also deciding joiners). Other planes pass through.</item>
/// <item>Runs split at every change between a bigram script (Han, kana, Hangul, Thai, Lao, Khmer, Myanmar) and other
/// letters or digits. A bigram run gives overlapping bigrams of its characters (a character = a letter and its marks)
/// plus its last character alone.</item>
/// <item>Other runs are words of 2-64 folded characters. Runs joined by exactly one of - _ . / \ ' ’ @ : are a code:
/// each part, plus the compound with the joiners removed when there are 2-8 parts and it fits in 64 characters.</item>
/// </list>
/// </summary>
internal static class ContentTokenizer
{
    public const int Version = 1, MinLength = 2, MaxLength = 64, MaxCompoundParts = 8, MaxDistinctPerDoc = 100_000;

    // Character classes. Dropped: a mark left out of tokens (kept when matching diacritics). Ignored: an invisible format
    // character, inside a unit like Dropped but left out under every match flag.
    internal const byte Other = 0, Plain = 1, Bigram = 2, Mark = 3, Dropped = 4, Joiner = 5, Ignored = 6;
    // Boundaries before a unit: start of text, none (same run), one joiner between plain runs, anything else.
    internal const byte BStart = 0, BNone = 1, BJoiner = 2, BSpace = 3;

    static readonly byte[] Class = new byte[65536];
    static readonly char[] Folded = new char[65536];
    static readonly string[] FieldPrefixes = new string[32];

    static ContentTokenizer()
    {
        for (int c = 0; c < 65536; c++)
        {
            char ch = (char)c;
            if (char.IsSurrogate(ch)) { Folded[c] = ch; Class[c] = Other; continue; }
            Folded[c] = FullWidth(CoreFold.LowerStrip[c]);
            if (ch is '­' or '‌' or '‍' or '⁠' or '﻿') { Class[c] = Ignored; continue; }
            char k = FullWidth(ch);
            if (k is '-' or '_' or '.' or '/' or '\\' or '\'' or '’' or '@' or ':') { Class[c] = Joiner; continue; }
            Class[c] = CharUnicodeInfo.GetUnicodeCategory(k) switch
            {
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                    => IsBigramScript(c) ? Bigram : Plain,
                UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => IsDroppedMark(c) ? Dropped : Mark,
                UnicodeCategory.SpacingCombiningMark => Mark,
                _ => Other,
            };
        }
        for (int f = 0; f < FieldPrefixes.Length; f++) FieldPrefixes[f] = "\u0001" + (char)(0x40 + f);
    }

    static char FullWidth(char c) => c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c;

    /// <summary>Scripts written without spaces between words, indexed as bigrams.</summary>
    static bool IsBigramScript(int cp) => cp switch
    {
        >= 0x0E00 and <= 0x0EFF => true,                       // Thai, Lao
        >= 0x1000 and <= 0x109F => true,                       // Myanmar
        >= 0x1100 and <= 0x11FF => true,                       // Hangul Jamo
        >= 0x1780 and <= 0x17FF or >= 0x19E0 and <= 0x19FF => true, // Khmer
        >= 0x2E80 and <= 0x2FDF => true,                       // CJK radicals, Kangxi
        >= 0x3005 and <= 0x3006 or >= 0x3038 and <= 0x303C => true,
        >= 0x3040 and <= 0x30FF => true,                       // Hiragana, Katakana
        >= 0x3130 and <= 0x318F => true,                       // Hangul compatibility Jamo
        >= 0x31F0 and <= 0x31FF => true,                       // Katakana phonetic extensions
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF => true, // CJK ideographs
        >= 0xA960 and <= 0xA97F or >= 0xA9E0 and <= 0xA9FF or >= 0xAA60 and <= 0xAA7F => true,
        >= 0xAC00 and <= 0xD7FF => true,                       // Hangul syllables, Jamo extended-B
        >= 0xF900 and <= 0xFAFF => true,                       // CJK compatibility ideographs
        >= 0xFF66 and <= 0xFF9F or >= 0xFFA0 and <= 0xFFDC => true, // half-width Katakana, Hangul
        >= 0x1B000 and <= 0x1B16F => true,                     // Kana supplement and extensions
        >= 0x20000 and <= 0x3FFFF => true,                     // CJK extensions B and later
        _ => false,
    };

    /// <summary>Combining-diacritic blocks, Hebrew points and Arabic harakat: dropped from tokens (they don't split words).</summary>
    static bool IsDroppedMark(int cp) => cp switch
    {
        >= 0x0300 and <= 0x036F or >= 0x1AB0 and <= 0x1AFF or >= 0x1DC0 and <= 0x1DFF => true,
        >= 0x20D0 and <= 0x20FF or >= 0xFE20 and <= 0xFE2F => true,
        >= 0x0591 and <= 0x05C7 => true,
        >= 0x0610 and <= 0x061A or >= 0x064B and <= 0x065F or 0x0670 or >= 0x06D6 and <= 0x06ED => true,
        _ => false,
    };

    /// <summary>Class of the code point at i and its length (a lone surrogate is Other).</summary>
    internal static byte ClassAt(ReadOnlySpan<char> s, int i, out int length)
    {
        char c = s[i];
        length = 1;
        if (!char.IsSurrogate(c)) return Class[c];
        if (!char.IsHighSurrogate(c) || i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return Other;
        length = 2;
        int cp = char.ConvertToUtf32(c, s[i + 1]);
        return CharUnicodeInfo.GetUnicodeCategory(cp) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                => IsBigramScript(cp) ? Bigram : Plain,
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => Mark,
            _ => Other,
        };
    }

    /// <summary>Code point at i (a lone surrogate as itself).</summary>
    static int CodePointAt(ReadOnlySpan<char> s, int i)
        => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

    /// <summary>A class that continues the unit before it (marks, dropped marks, invisible format characters).</summary>
    static bool Continues(byte c) => c is Mark or Dropped or Ignored;

    /// <summary>A character left out of the folded unit: always for Ignored, for Dropped unless diacritics must match.</summary>
    static bool LeftOut(char c, bool matchDiacritics) => Class[c] == Ignored || (!matchDiacritics && Class[c] == Dropped);

    /// <summary>Whether the text has a word: a letter, digit or kept mark the index makes units of (body text, not just line and page ends).</summary>
    internal static bool HasWords(ReadOnlySpan<char> text) => !text.IsEmpty && NextUnitStart(text, 0) >= 0;

    /// <summary>The unit (a letter or digit and its marks, or marks after a separator) starting at i: its end and class.</summary>
    internal static int UnitEnd(ReadOnlySpan<char> s, int i, out byte cls)
    {
        byte c0 = ClassAt(s, i, out int len);
        cls = c0 is Plain or Bigram ? c0 : IsBigramScript(CodePointAt(s, i)) ? Bigram : Plain;
        int j = i + len;
        while (j < s.Length)
        {
            byte c = ClassAt(s, j, out int l);
            if (!Continues(c)) break;
            j += l;
        }
        return j;
    }

    /// <summary>Whether the code point before i belongs to a unit (a letter, digit, or a mark attached to one).</summary>
    internal static bool InUnitBefore(ReadOnlySpan<char> s, int i)
    {
        bool sawKept = false;
        for (int steps = 0; i > 0 && steps < 32; steps++)
        {
            int at = i - 1;
            if (char.IsLowSurrogate(s[at]) && at > 0 && char.IsHighSurrogate(s[at - 1])) at--;
            byte c = ClassAt(s, at, out _);
            if (c is Plain or Bigram) return true;
            if (!Continues(c)) return sawKept;
            sawKept |= c == Mark;
            i = at;
        }
        return i > 0 || sawKept;
    }

    /// <summary>
    /// Class of the unit that ends at end (end must follow a unit character). A unit of marks after a separator starts at
    /// its first kept mark (dropped marks and format characters never start one), so only kept marks decide its class.
    /// </summary>
    static byte ClassOfUnitEndingAt(ReadOnlySpan<char> s, int end)
    {
        byte orphan = Plain;
        for (int steps = 0; end > 0 && steps < 32; steps++)
        {
            int at = end - 1;
            if (char.IsLowSurrogate(s[at]) && at > 0 && char.IsHighSurrogate(s[at - 1])) at--;
            byte c = ClassAt(s, at, out _);
            if (c is Plain or Bigram) return c;
            if (!Continues(c)) return orphan;
            if (c == Mark) orphan = IsBigramScript(CodePointAt(s, at)) ? Bigram : Plain;
            end = at;
        }
        return orphan;
    }

    /// <summary>The next unit start at or after i; -1 when none.</summary>
    internal static int NextUnitStart(ReadOnlySpan<char> s, int i)
    {
        bool inUnit = InUnitBefore(s, i);
        while (i < s.Length)
        {
            byte c = ClassAt(s, i, out int len);
            if (c is Plain or Bigram || (c == Mark && !inUnit)) return i;
            if (!Continues(c)) inUnit = false;
            i += len;
        }
        return -1;
    }

    /// <summary>The boundary between a unit ending at prevEnd and the next starting at start.</summary>
    static byte Gap(ReadOnlySpan<char> s, int prevEnd, byte prevClass, int start, byte cls)
    {
        if (start == prevEnd) return prevClass == cls ? BNone : BSpace;
        if (start == prevEnd + 1 && prevClass == Plain && cls == Plain && Class[s[prevEnd]] == Joiner) return BJoiner;
        return BSpace;
    }

    /// <summary>The boundary before the unit at i, looking back (BStart at the start of the text).</summary>
    static byte BoundaryBefore(ReadOnlySpan<char> s, int i, byte cls)
    {
        if (i == 0) return BStart;
        if (InUnitBefore(s, i)) return ClassOfUnitEndingAt(s, i) == cls ? BNone : BSpace;
        if (cls == Plain && Class[s[i - 1]] == Joiner && i >= 2 && InUnitBefore(s, i - 1) && ClassOfUnitEndingAt(s, i - 1) == Plain)
            return BJoiner;
        return BSpace;
    }

    /// <summary>Units of a text in order, with the boundary before each.</summary>
    internal ref struct Units(ReadOnlySpan<char> text)
    {
        readonly ReadOnlySpan<char> _s = text;
        int _pos, _prevEnd;
        byte _prevClass;
        bool _hasPrev, _inUnit;

        public int Start, End;
        public byte UnitClass, Before;

        public bool MoveNext()
        {
            int i = _pos;
            while (i < _s.Length)
            {
                byte c = ClassAt(_s, i, out int len);
                if (c is Plain or Bigram || (c == Mark && !_inUnit)) break;
                if (!Continues(c)) _inUnit = false;
                i += len;
            }
            _pos = i;
            if (i >= _s.Length) return false;
            End = UnitEnd(_s, i, out UnitClass);
            Start = i;
            Before = _hasPrev ? Gap(_s, _prevEnd, _prevClass, i, UnitClass) : BStart;
            _prevEnd = _pos = End;
            _prevClass = UnitClass;
            _hasPrev = _inUnit = true;
            return true;
        }
    }

    /// <summary>Folds the unit [start, end) into dest with the index's rules (dropped marks and format characters left out); the folded length, or -1 when it doesn't fit.</summary>
    static int FoldUnit(ReadOnlySpan<char> s, int start, int end, Span<char> dest)
    {
        int n = 0;
        for (int j = start; j < end; j++)
        {
            char c = s[j];
            if (LeftOut(c, matchDiacritics: false)) continue;
            if (n == dest.Length) return -1;
            dest[n++] = Folded[c];
        }
        return n;
    }

    /// <summary>Tokenizes body text with the rules of a tokenizer version (the previous version stays callable for one release).</summary>
    public static void Tokenize(ReadOnlySpan<char> text, ITokenSink sink, int version = Version)
    {
        if (version != Version) throw new ArgumentOutOfRangeException(nameof(version), $"Tokenizer version {version} is not known.");
        Span<char> buffers = stackalloc char[MaxLength * 7];
        var e = new Emitter(sink, buffers);
        var units = new Units(text);
        while (units.MoveNext()) e.Unit(text, units.Start, units.End, units.UnitClass, units.Before);
        e.End();
    }

    /// <summary>Tokenizes a field value: plain tokens plus field-scoped ones (FieldPrefix + token).</summary>
    public static void TokenizeField(DocField field, ReadOnlySpan<char> value, ITokenSink sink, int version = Version)
        => Tokenize(value, new FieldSink(sink, FieldPrefix(field)), version);

    /// <summary>Prefix of a field's scoped terms: "\u0001" + (char)(0x40 + field); text never produces it.</summary>
    public static string FieldPrefix(DocField field)
        => (uint)field < (uint)FieldPrefixes.Length ? FieldPrefixes[(int)field] : "\u0001" + (char)(0x40 + (int)field);

    sealed class FieldSink(ITokenSink inner, string prefix) : ITokenSink
    {
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            inner.Token(folded, start, length, kind);
            Span<char> scoped = stackalloc char[prefix.Length + folded.Length];
            prefix.CopyTo(scoped);
            folded.CopyTo(scoped[prefix.Length..]);
            inner.Token(scoped, start, length, TokenKind.Field);
        }
    }

    /// <summary>Turns units into tokens: plain runs into words and codes, bigram runs into bigrams.</summary>
    ref struct Emitter
    {
        readonly ITokenSink _sink;
        readonly Span<char> _word, _first, _compound, _prev, _cur, _pair;
        byte _run;
        // Current plain run.
        int _wordLen, _wordStart, _wordEnd;
        bool _wordLong;
        // Code chain: the first part is held until a second one shows it is a code.
        int _parts, _firstLen, _firstStart, _firstEnd, _compoundLen, _chainStart, _chainEnd;
        bool _firstLong, _compoundLong;
        // Previous character of the current bigram run.
        int _prevLen, _prevStart, _prevEnd;

        public Emitter(ITokenSink sink, Span<char> buffers)
        {
            _sink = sink;
            _word = buffers[..MaxLength];
            _first = buffers.Slice(MaxLength, MaxLength);
            _compound = buffers.Slice(MaxLength * 2, MaxLength);
            _prev = buffers.Slice(MaxLength * 3, MaxLength);
            _cur = buffers.Slice(MaxLength * 4, MaxLength);
            _pair = buffers.Slice(MaxLength * 5, MaxLength * 2);
        }

        public void Unit(ReadOnlySpan<char> s, int start, int end, byte cls, byte before)
        {
            bool continues = before == BNone && _run == cls;
            if (!continues)
            {
                byte ended = _run;
                EndRun();
                if (!(cls == Plain && ended == Plain && before == BJoiner)) EndChain();
                _run = cls;
                if (cls == Plain) { _wordLen = 0; _wordLong = false; _wordStart = start; }
                else _prevLen = -2;
            }
            if (cls == Plain)
            {
                _wordEnd = end;
                if (_wordLong) return;
                int n = FoldUnit(s, start, end, _word[_wordLen..]);
                if (n < 0) _wordLong = true; else _wordLen += n;
            }
            else
            {
                int n = FoldUnit(s, start, end, _cur);
                if (_prevLen >= 0 && n >= 0 && _prevLen + n <= MaxLength)
                {
                    _prev[.._prevLen].CopyTo(_pair);
                    _cur[..n].CopyTo(_pair[_prevLen..]);
                    _sink.Token(_pair[..(_prevLen + n)], _prevStart, end - _prevStart, TokenKind.Bigram);
                }
                if (n >= 0) _cur[..n].CopyTo(_prev);
                _prevLen = n;
                _prevStart = start;
                _prevEnd = end;
            }
        }

        public void End()
        {
            EndRun();
            EndChain();
        }

        void EndRun()
        {
            if (_run == Plain) Part();
            else if (_run == Bigram && _prevLen > 0) _sink.Token(_prev[.._prevLen], _prevStart, _prevEnd - _prevStart, TokenKind.Bigram);
            _run = Other;
        }

        void Part()
        {
            if (_parts == 0)
            {
                _word[.._wordLen].CopyTo(_first);
                _firstLen = _wordLen;
                _firstLong = _wordLong;
                _firstStart = _wordStart;
                _firstEnd = _wordEnd;
                _chainStart = _wordStart;
                _compoundLen = 0;
                _compoundLong = false;
            }
            else
            {
                if (_parts == 1) Emit(_first, _firstLen, _firstLong, _firstStart, _firstEnd, TokenKind.Part);
                Emit(_word, _wordLen, _wordLong, _wordStart, _wordEnd, TokenKind.Part);
            }
            _parts++;
            _chainEnd = _wordEnd;
            if (_compoundLong || _wordLong || _compoundLen + _wordLen > MaxLength) _compoundLong = true;
            else
            {
                _word[.._wordLen].CopyTo(_compound[_compoundLen..]);
                _compoundLen += _wordLen;
            }
        }

        void EndChain()
        {
            if (_parts == 1) Emit(_first, _firstLen, _firstLong, _firstStart, _firstEnd, TokenKind.Word);
            else if (_parts is >= 2 and <= MaxCompoundParts && !_compoundLong && _compoundLen >= MinLength)
                _sink.Token(_compound[.._compoundLen], _chainStart, _chainEnd - _chainStart, TokenKind.Compound);
            _parts = 0;
        }

        readonly void Emit(Span<char> buffer, int length, bool tooLong, int start, int end, TokenKind kind)
        {
            if (!tooLong && length is >= MinLength and <= MaxLength) _sink.Token(buffer[..length], start, end - start, kind);
        }
    }

    /// <summary>One search word as the index sees it.</summary>
    public static QueryWord Analyze(string word, bool lastIsPrefix)
    {
        string text = Nfc(word ?? "");
        var runs = new List<(byte Class, byte Before, List<string> Units)>();
        Span<char> unit = stackalloc char[MaxLength];
        var units = new Units(text);
        while (units.MoveNext())
        {
            int n = FoldUnit(text, units.Start, units.End, unit);
            string u = n >= 0 ? new string(unit[..n]) : Fold(text.AsSpan(units.Start, units.End - units.Start));
            if (runs.Count > 0 && units.Before == BNone && runs[^1].Class == units.UnitClass) runs[^1].Units.Add(u);
            else runs.Add((units.UnitClass, units.Before, [u]));
        }
        var tokens = new List<string>();
        var kinds = new List<TokenKind>();
        bool code = runs.Count >= 2;
        for (int r = 0; r < runs.Count; r++)
        {
            var (cls, before, us) = runs[r];
            if (cls == Plain)
            {
                tokens.Add(string.Concat(us));
                kinds.Add(runs.Count >= 2 && (before == BJoiner || (r + 1 < runs.Count && runs[r + 1].Before == BJoiner)) ? TokenKind.Part : TokenKind.Word);
            }
            else if (us.Count == 1) { tokens.Add(us[0]); kinds.Add(TokenKind.Bigram); }
            else
                for (int i = 0; i + 1 < us.Count; i++) { tokens.Add(us[i] + us[i + 1]); kinds.Add(TokenKind.Bigram); }
            if (cls != Plain || (r > 0 && before != BJoiner)) code = false;
        }
        string? compound = null;
        if (code && runs.Count <= MaxCompoundParts)
        {
            compound = string.Concat(tokens);
            if (compound.Length > MaxLength) compound = null;
        }
        return new QueryWord(text, tokens, compound, lastIsPrefix, tokens.Count > 1) { Folded = Fold(text), Kinds = kinds };
    }

    /// <summary>One word folded exactly as stored (Fold.LowerStrip, dropped marks and format characters left out, full-width ASCII folded); separators kept.</summary>
    public static string Fold(ReadOnlySpan<char> word)
    {
        string s = Nfc(word.ToString());
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!LeftOut(c, matchDiacritics: false)) sb.Append(Folded[c]);
        return sb.ToString();
    }

    static string Nfc(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }

    /// <summary>
    /// Where the word's tokens stand next to each other (only non-word characters between, joiners optional on either
    /// side), under the case/diacritics/whole-word rules; (-1, 0) when none. A plain word starts at a word or code-part
    /// start; a bigram-script word may start anywhere in its run (a substring match). Unless the last token is a prefix,
    /// a plain match ends at a word end. With a prefix (and not whole word), the last unit may stop before the text
    /// unit's marks: สวัสด finds สวัสดี, कित finds किताब, and with diacritics on abx finds abx́.
    /// </summary>
    public static (int Start, int Length) FindSequence(ReadOnlySpan<char> text, QueryWord word, bool matchCase, bool matchDiacritics, bool wholeWord, int from = 0)
    {
        var pattern = Pattern.Build(word.Text, matchCase, matchDiacritics);
        if (pattern.Units.Count == 0 || from >= text.Length) return (-1, 0);
        var table = CoreFold.Table(matchCase, matchDiacritics);
        bool prefix = word.LastIsPrefix && !wholeWord;
        bool firstIsLast = pattern.Units.Count == 1;
        for (int i = NextUnitStart(text, Math.Max(0, from)); i >= 0; i = NextUnitStart(text, i))
        {
            int end = UnitEnd(text, i, out byte cls);
            int at = i;
            i = end;
            if (cls != pattern.Classes[0] || !UnitMatches(text, at, end, pattern.Units[0], table, matchDiacritics, prefix && firstIsLast)) continue;
            byte before = BoundaryBefore(text, at, cls);
            if (before == BNone && (cls == Plain || wholeWord)) continue;
            if (MatchRest(text, end, cls, pattern, table, matchDiacritics, wholeWord, prefix, out int matchEnd))
                return (at, matchEnd - at);
        }
        return (-1, 0);
    }

    static bool MatchRest(ReadOnlySpan<char> s, int end, byte cls, Pattern p, char[]? table, bool matchDiacritics, bool wholeWord,
        bool lastIsPrefix, out int matchEnd)
    {
        matchEnd = end;
        for (int k = 1; k < p.Units.Count; k++)
        {
            int next = NextUnitStart(s, end);
            if (next < 0) return false;
            int nextEnd = UnitEnd(s, next, out byte nextClass);
            if (nextClass != p.Classes[k] || !Compatible(p.Before[k], Gap(s, end, cls, next, nextClass))) return false;
            if (!UnitMatches(s, next, nextEnd, p.Units[k], table, matchDiacritics, lastIsPrefix && k == p.Units.Count - 1)) return false;
            end = nextEnd;
            cls = nextClass;
        }
        if (wholeWord || (!lastIsPrefix && cls == Plain))
        {
            int next = NextUnitStart(s, end);
            if (next == end)
            {
                UnitEnd(s, next, out byte nextClass);
                if (Gap(s, end, cls, next, nextClass) == BNone) return false;
            }
        }
        matchEnd = end;
        return true;
    }

    /// <summary>Whether a query boundary accepts a text boundary: inside a word a joiner may be skipped; a joiner matches anything; a space needs some separator.</summary>
    static bool Compatible(byte query, byte text) => query switch
    {
        BNone => text is BNone or BJoiner,
        BJoiner => true,
        _ => text is BJoiner or BSpace,
    };

    /// <summary>
    /// Whether the text unit [start, end), folded under the flags, equals the pattern unit; with prefix, whether it starts
    /// with it (the rest of the text unit, marks the query hasn't reached yet, is ignored).
    /// </summary>
    static bool UnitMatches(ReadOnlySpan<char> s, int start, int end, string unit, char[]? table, bool matchDiacritics, bool prefix)
    {
        int k = 0;
        for (int j = start; j < end; j++)
        {
            char c = s[j];
            if (LeftOut(c, matchDiacritics)) continue;
            if (k == unit.Length) return prefix;
            if (unit[k] != FullWidth(table == null ? c : table[c])) return false;
            k++;
        }
        return k == unit.Length;
    }

    /// <summary>A query word as units folded under the match flags, with their classes and the boundary before each.</summary>
    sealed class Pattern
    {
        public readonly List<string> Units = [];
        public readonly List<byte> Classes = [];
        public readonly List<byte> Before = [];

        public static Pattern Build(string word, bool matchCase, bool matchDiacritics)
        {
            var p = new Pattern();
            string text = Nfc(word);
            var table = CoreFold.Table(matchCase, matchDiacritics);
            var sb = new StringBuilder(8);
            var units = new Units(text);
            while (units.MoveNext())
            {
                sb.Clear();
                for (int j = units.Start; j < units.End; j++)
                {
                    char c = text[j];
                    if (LeftOut(c, matchDiacritics)) continue;
                    sb.Append(FullWidth(table == null ? c : table[c]));
                }
                p.Units.Add(sb.ToString());
                p.Classes.Add(units.UnitClass);
                p.Before.Add(units.Before);
            }
            return p;
        }
    }
}
