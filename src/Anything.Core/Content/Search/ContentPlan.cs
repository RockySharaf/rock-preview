using System.Globalization;
using System.Text;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>
/// The content side of one parsed query (query tree only for plain words; content functions of the filter too, never its
/// plain words), by node reference. Plain words are expanded (also matched inside files) only when they can be answered
/// from the dictionary alone, never confirmed in the stored text (A9): a word of 3 or more letters or digits as a prefix,
/// a code (INV-2024, report.pdf) through its compound with the joiners removed and incomplete trailing parts of one
/// character ignored, or two characters of a script written without spaces (東京) through their bigram. Longer runs of
/// such a script (東京都) are several bigrams that only the stored text could prove adjacent, so they stay name-only, like
/// phrases (content:東京都 finds them inside files). Phrases, wildcards, regex, path terms, extensions typed alone (.pdf)
/// and words under a NOT are never expanded; under case: or diacritics: one word is, confirmed in the stored text with
/// those rules. Expanded words still match names, so they leave <see cref="NeedsMode"/> alone.
/// </summary>
internal sealed class ContentPlan
{
    readonly Dictionary<QueryNode, ContentTerm> _terms = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<QueryNode> _negated = new(ReferenceEqualityComparer.Instance);

    ContentPlan() { }

    /// <summary>
    /// Builds the plan; throws QueryException for bad content values (regex, too short, wildcard across words). The
    /// filter's content functions are planned too (a "CMYK pictures" filter works), never its plain words.
    /// </summary>
    public static ContentPlan Build(QueryNode query, SearchOptions options, ContentMode mode, DateTime now, QueryNode? filter = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        options ??= new SearchOptions();
        var plan = new ContentPlan();
        bool expand = options.MatchContent && mode != ContentMode.NamesOnly;
        plan.Walk(query, negated: false, underNot: false, expand, now);
        if (filter != null) plan.Walk(filter, negated: false, underNot: false, expand: false, now);
        return plan;
    }

    /// <summary>No content terms.</summary>
    public bool IsEmpty => _terms.Count == 0;
    /// <summary>Content terms by node (ReferenceEqualityComparer): content functions and expanded plain TextNodes.</summary>
    public IReadOnlyDictionary<QueryNode, ContentTerm> Terms => _terms;
    /// <summary>Some plain words also match inside files.</summary>
    public bool HasPlainExpansion { get; private set; }
    /// <summary>
    /// The least mode the query needs: Documents for content: and field functions, DocumentsAndPictures for picture
    /// functions; NamesOnly when it has none (plain words also match names, so they need no mode).
    /// </summary>
    internal ContentMode NeedsMode { get; private set; }

    /// <summary>The term's node is negated (an odd number of NOTs above it): not highlighted.</summary>
    internal bool IsNegated(QueryNode node) => _negated.Contains(node);

    void Add(QueryNode node, ContentTerm term, bool negated, ContentMode needs)
    {
        _terms[node] = term;
        if (negated) _negated.Add(node);
        if (needs > NeedsMode) NeedsMode = needs;
    }

    void Walk(QueryNode node, bool negated, bool underNot, bool expand, DateTime now)
    {
        switch (node)
        {
            case AndNode a:
                foreach (var c in a.Children) Walk(c, negated, underNot, expand, now);
                break;
            case OrNode o:
                foreach (var c in o.Children) Walk(c, negated, underNot, expand, now);
                break;
            case NotNode n:
                Walk(n.Child, !negated, underNot: true, expand, now);
                break;
            case TextNode t when expand && !underNot:
                if (PlainTerm(t) is { } plain)
                {
                    Add(t, plain, negated, ContentMode.NamesOnly); // still a name match without the index: no mode hint
                    HasPlainExpansion = true;
                }
                break;
            case FunctionNode f when ContentFunctions.IsContentName(f.Name):
                Add(f, FunctionTerm(f, now), negated, ContentFunctions.NeedsMode(f.Name));
                break;
        }
    }

    // ---------------- plain words ----------------

    /// <summary>
    /// The content side of a plain word, or null when it stays a name-only term. Under Match case or Match diacritics one
    /// word is still looked up folded, and its documents confirmed in the stored text with those rules (as case:content:
    /// is), within the plain words' budget; a code or a longer run stays name-only then (A9: never confirmed part by part).
    /// </summary>
    internal static ContentTerm? PlainTerm(TextNode t)
    {
        var f = t.Flags;
        if (f.Regex || t.HasWildcards || UsesPath(t.Text, f) || IsExtension(t.Text)) return null;
        string text = f.WholeWord ? t.Text : WithoutShortTrailingParts(t.Text);
        var word = ContentTokenizer.Analyze(text, lastIsPrefix: !f.WholeWord);
        var lookup = PlainLookup(word, f.WholeWord);
        if (lookup == null) return null;
        bool confirm = f.MatchCase || f.MatchDiacritics;
        if (confirm)
        {
            if (word.Tokens.Count != 1) return null;
            lookup = lookup with { DirectNeedsVerify = true };
        }
        return new ContentTerm(ContentTermKind.Words, word, null, null, null, null, null, null, f.MatchCase, f.MatchDiacritics, f.WholeWord, Plain: true)
        {
            Source = t.Text,
            Lookup = lookup,
        };
    }

    /// <summary>
    /// ".pdf", ".jpg", ".docx", ".NET": a dot and a short run of letters or digits, typed to narrow names to an extension
    /// (Everything's habit). Its word alone would find every document that mentions PDF or JPG: it matches names only.
    /// A longer one (".gitignore") is a name fragment like any other word.
    /// </summary>
    static bool IsExtension(string text)
    {
        if (text.Length is < 2 or > 6 || text[0] != '.') return false;
        for (int i = 1; i < text.Length; i++)
            if (!char.IsAsciiLetterOrDigit(text[i])) return false;
        return true;
    }

    /// <summary>Same rule as QueryCompiler: path: or Match Path, else a backslash makes it a path term (Auto).</summary>
    static bool UsesPath(string text, TermFlags f) => f.Path switch
    {
        PathMode.Path => true,
        PathMode.NoPath => false,
        _ => !f.Regex && text.Contains('\\'),
    };

    /// <summary>
    /// A plain word answered from the dictionary alone, nothing confirmed in the stored text (A9): one word of 3+ letters
    /// or digits, one bigram of a script written without spaces (two characters: 東京), or a code through its compound;
    /// null otherwise (never the AND of a code's parts or of a longer run's bigrams).
    /// </summary>
    static WordPlan? PlainLookup(QueryWord word, bool wholeWord)
    {
        var tokens = word.Tokens;
        if (tokens.Count == 0) return null;
        if (tokens.Count == 1)
        {
            string t = tokens[0];
            if (t.Length > ContentTokenizer.MaxLength) return null;
            if (word.Kinds[0] == TokenKind.Bigram)
                return UnitCount(t) >= 2 ? new WordPlan(t, DirectPrefix: true, DirectNeedsVerify: false, []) : null;
            return t.Length >= 3 ? new WordPlan(t, DirectPrefix: !wholeWord, DirectNeedsVerify: false, []) : null;
        }
        if (word.Compound is { } compound)
        {
            bool long3 = false;
            foreach (var t in tokens) long3 |= t.Length >= 3;
            return long3 ? new WordPlan(compound, DirectPrefix: !wholeWord, DirectNeedsVerify: false, []) : null;
        }
        return null;
    }

    /// <summary>Units (a letter or digit with its marks) in a folded token.</summary>
    static int UnitCount(string token)
    {
        var units = new ContentTokenizer.Units(token);
        int n = 0;
        while (units.MoveNext()) n++;
        return n;
    }

    /// <summary>
    /// A code typed so far ends in a part of one letter or digit ("report.p", "IMG_1"), which isn't a word of the index
    /// yet: drops such trailing parts with their joiner (A9), so "report.p" looks up words starting with "report".
    /// </summary>
    internal static string WithoutShortTrailingParts(string text)
    {
        string s = Nfc(text);
        var runs = new List<(int Start, int End, byte Class, byte Before)>();
        var units = new ContentTokenizer.Units(s);
        while (units.MoveNext())
        {
            if (runs.Count > 0 && units.Before == ContentTokenizer.BNone && runs[^1].Class == units.UnitClass)
                runs[^1] = runs[^1] with { End = units.End };
            else runs.Add((units.Start, units.End, units.UnitClass, units.Before));
        }
        int cut = s.Length;
        while (runs.Count >= 2)
        {
            var last = runs[^1];
            if (last.Class != ContentTokenizer.Plain || last.Before != ContentTokenizer.BJoiner) break;
            if (ContentTokenizer.Fold(s.AsSpan(last.Start, last.End - last.Start)).Length >= ContentTokenizer.MinLength) break;
            cut = last.Start - 1; // the joiner before it
            runs.RemoveAt(runs.Count - 1);
        }
        return cut == s.Length ? s : s[..cut];
    }

    static string Nfc(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }

    // ---------------- functions ----------------

    static ContentTerm FunctionTerm(FunctionNode f, DateTime now)
    {
        string name = f.Name;
        if (name == ContentFunctions.Content)
            return string.IsNullOrWhiteSpace(f.Value)
                ? new ContentTerm(ContentTermKind.HasText, null, null, null, null, null, null, null, false, false, false, false) { Source = "content:" }
                : WordTerm(f, field: null);
        if (ContentFunctions.FieldOf(name) is { } field)
            return string.IsNullOrWhiteSpace(f.Value) ? AnyInField(f, field) : WordTerm(f, field);
        if (ContentFunctions.PictureOf(name) is { } prop) return PictureTerm(f, prop, now);
        if (name == ContentFunctions.PageCount)
        {
            var conds = ValueParsers.ParseConds(name, f.Op, f.Value, ValueParsers.ParseInt, allowDashRange: true);
            return new ContentTerm(ContentTermKind.Pages, null, null, null, null, conds, null, null, false, false, false, false) { Source = Canonical(f) };
        }
        if (name == ContentFunctions.Status)
            return new ContentTerm(ContentTermKind.Status, null, null, null, null, null, null, Statuses(f), false, false, false, false) { Source = Canonical(f) };
        throw new QueryException($"Unknown function '{name}:'");
    }

    static string Canonical(FunctionNode f) => f.Name + ":" + FunctionNode.OpText(f.Op) + f.Value;

    /// <summary>The second field a function searches: camera: the model too, author: an email's sender too (only mail has one).</summary>
    static DocField? Also(string name) => name switch
    {
        ContentFunctions.Camera => DocField.CameraModel,
        "author" => DocField.Sender,
        _ => null,
    };

    /// <summary>title: with no value: files that have a title (any word in the field).</summary>
    static ContentTerm AnyInField(FunctionNode f, DocField field)
        => new(ContentTermKind.Field, null, null, field, null, null, null, null, false, false, false, false)
        {
            Source = f.Name + ":",
            Lookup = new WordPlan("", DirectPrefix: true, DirectNeedsVerify: false, []),
            AlsoField = Also(f.Name),
        };

    /// <summary>content:word, content:"a phrase", content:INV-2024, content:inv*01, and the same in a field (title:…).</summary>
    static ContentTerm WordTerm(FunctionNode f, DocField? field)
    {
        string name = f.Name, value = f.Value;
        var flags = f.Flags;
        if (flags.Regex)
            throw new QueryException($"{name}: can't use regex: the text index holds words; use wildcards, e.g. {name}:inv*24");
        var kind = field == null ? ContentTermKind.Words : ContentTermKind.Field;
        if (flags.Wildcards && TextUtil.HasWildcards(value)) return GlobTerm(f, field);
        var word = ContentTokenizer.Analyze(value, lastIsPrefix: !flags.WholeWord);
        var lookup = ExplicitLookup(name, word, flags.WholeWord, flags.MatchCase || flags.MatchDiacritics);
        return new ContentTerm(kind, word, null, field, null, null, null, null, flags.MatchCase, flags.MatchDiacritics, flags.WholeWord, false)
        {
            Source = name + ":" + value,
            Lookup = lookup,
            AlsoField = Also(name),
        };
    }

    /// <summary>
    /// An explicit word (content: keeps full verification). One word is a prefix: its postings are the matches. With ww:
    /// (or Match whole word) the exact word is (§7: an exact term lookup), plus the few documents where the text joins it
    /// from a code's parts, confirmed (ww:inv2024 matches "INV-2024-001", see <see cref="WordPlan.Joins"/>); words that
    /// merely start with it ("reporting" for ww:report) are never candidates. Several words, codes or bigram runs are
    /// candidates confirmed in the stored text: every group's documents (<see cref="Groups"/>); a code alone also matches
    /// outright through its compound's prefix, which finds "INV-2024" and "INV2024" while its parts find "INV 2024".
    /// </summary>
    static WordPlan ExplicitLookup(string name, QueryWord word, bool wholeWord, bool flagsVerify)
    {
        var tokens = word.Tokens;
        if (tokens.Count == 1)
        {
            string t = tokens[0];
            bool bigram = word.Kinds[0] == TokenKind.Bigram;
            if (t.Length > ContentTokenizer.MaxLength) throw TooLong(name);
            if (bigram || t.Length >= ContentTokenizer.MinLength)
                return !wholeWord || bigram
                    ? new WordPlan(t, DirectPrefix: true, DirectNeedsVerify: flagsVerify || (bigram && wholeWord), [])
                    : new WordPlan(t, DirectPrefix: false, DirectNeedsVerify: flagsVerify, [], Joins: true);
        }
        else if (tokens.Count > 1)
        {
            var groups = Groups(word.Text, out bool tooLong);
            if (word.Compound is { Length: >= ContentTokenizer.MinLength } compound)
            {
                // One code: the compound's documents match outright; the group is its parts alone (minus those).
                var parts = groups.Count == 1 ? groups[0].Parts : [];
                return new WordPlan(compound, DirectPrefix: word.LastIsPrefix, DirectNeedsVerify: flagsVerify,
                    parts.Count > 0 ? [new WordGroup(parts)] : []);
            }
            if (groups.Count > 0) return new WordPlan(null, false, flagsVerify, groups);
            if (tooLong) throw TooLong(name);
        }
        throw new QueryException($"{name}: needs a word of at least 2 letters or digits");
    }

    /// <summary>
    /// The candidate groups of a query of several runs: a word between spaces (exact, the last one a prefix: it may go
    /// on, or end where a code's part ends), a code (its parts as prefixes, since a part may run on into the next one in
    /// the text, or its compound's prefix), a bigram run (its bigrams as prefixes: a character may end before its marks).
    /// Runs the index can't look up (one letter, more than 64) are only confirmed in the text.
    /// </summary>
    static List<WordGroup> Groups(string text, out bool tooLong)
    {
        tooLong = false;
        string s = Nfc(text);
        var runs = new List<(byte Class, byte Before, List<string> Units)>();
        var units = new ContentTokenizer.Units(s);
        while (units.MoveNext())
        {
            string u = ContentTokenizer.Fold(s.AsSpan(units.Start, units.End - units.Start));
            if (runs.Count > 0 && units.Before == ContentTokenizer.BNone && runs[^1].Class == units.UnitClass) runs[^1].Units.Add(u);
            else runs.Add((units.UnitClass, units.Before, [u]));
        }
        var groups = new List<WordGroup>();
        for (int r = 0; r < runs.Count;)
        {
            var (cls, _, us) = runs[r];
            if (cls == ContentTokenizer.Bigram)
            {
                var bigrams = new List<(string, bool)>();
                if (us.Count == 1) bigrams.Add((us[0], true));
                for (int i = 0; i + 1 < us.Count; i++) bigrams.Add((us[i] + us[i + 1], true));
                groups.Add(new WordGroup(bigrams));
                r++;
                continue;
            }
            // A plain run and the plain runs joined to it by single joiners: a word, or a code.
            int end = r + 1;
            while (end < runs.Count && runs[end].Class == ContentTokenizer.Plain && runs[end].Before == ContentTokenizer.BJoiner) end++;
            bool lastGroup = end == runs.Count;
            var parts = new List<(string, bool)>();
            var compound = new StringBuilder();
            for (int i = r; i < end; i++)
            {
                string part = string.Concat(runs[i].Units);
                compound.Append(part);
                if (part.Length > ContentTokenizer.MaxLength) { tooLong = true; continue; }
                if (part.Length < ContentTokenizer.MinLength) continue;
                parts.Add((part, end - r > 1 || lastGroup));
            }
            string? joined = end - r is >= 2 and <= ContentTokenizer.MaxCompoundParts
                && compound.Length is >= ContentTokenizer.MinLength and <= ContentTokenizer.MaxLength ? compound.ToString() : null;
            // A code none of whose parts the index holds ("a-b") could stand as "a b" in the text: it narrows nothing.
            if (parts.Count > 0) groups.Add(new WordGroup(parts, joined));
            r = end;
        }
        return groups;
    }

    static QueryException TooLong(string name) => new($"{name}: words of more than {ContentTokenizer.MaxLength} letters or digits aren't indexed");

    /// <summary>
    /// A wildcard within one word, matched against the dictionary's folded words (case and accents are ignored, as in the
    /// index; joiners are left out, as in its compounds, so inv-20* finds INV-2024-001). Wildcards alone (content:*)
    /// mean any word: files with words, or with that field.
    /// </summary>
    static ContentTerm GlobTerm(FunctionNode f, DocField? field)
    {
        string name = f.Name, value = f.Value;
        if (value.Any(char.IsWhiteSpace)) throw WithinOneWord(name);
        string folded = ContentTokenizer.Fold(value);
        var sb = new StringBuilder(folded.Length);
        bool literal = false;
        for (int i = 0; i < folded.Length;)
        {
            char c = folded[i];
            if (c is '*' or '?') { sb.Append(c); i++; continue; }
            byte cls = ContentTokenizer.ClassAt(folded, i, out int len);
            if (cls == ContentTokenizer.Joiner) { i += len; continue; } // joiners never stand in the index's words
            if (cls == ContentTokenizer.Other) throw WithinOneWord(name);
            sb.Append(folded, i, len);
            literal = true;
            i += len;
        }
        string pattern = sb.ToString();
        if (!literal && pattern.Contains('*'))
            return field is { } any
                ? AnyInField(f, any)
                : new ContentTerm(ContentTermKind.HasText, null, null, null, null, null, null, null, false, false, false, false) { Source = "content:" };
        if (!literal) throw new QueryException($"{name}: needs a word of at least 2 letters or digits");
        int wild = pattern.AsSpan().IndexOfAny('*', '?');
        return new ContentTerm(ContentTermKind.Glob, null, new GlobPattern(pattern, matchCase: true, matchDiacritics: true), field, null, null,
            null, null, false, false, false, false)
        {
            Source = name + ":" + value,
            GlobPrefix = wild < 0 ? pattern : pattern[..wild],
            AlsoField = Also(name),
        };
    }

    static QueryException WithinOneWord(string name) => new($"{name}: wildcards work within one word");

    // ---------------- pictures, pages, statuses ----------------

    static ContentTerm PictureTerm(FunctionNode f, PictureProp prop, DateTime now)
    {
        string name = f.Name, v = f.Value;
        string source = Canonical(f);
        ContentTerm Make(NumCond[]? conds = null, ColorMode[]? modes = null) =>
            new(ContentTermKind.Picture, null, null, null, prop, conds, modes, null, false, false, false, false) { Source = source };
        switch (prop)
        {
            case PictureProp.Width or PictureProp.Height or PictureProp.BitDepth or PictureProp.Dpi or PictureProp.DpiX
                or PictureProp.DpiY or PictureProp.Rating:
                return Make(ValueParsers.ParseConds(name, f.Op, v, ValueParsers.ParseInt, allowDashRange: true));
            case PictureProp.DateTaken:
            {
                // Relative dates (today, lastweek, pastday) move with the clock: the key carries the periods the value
                // resolved to, so a set is reused exactly while they are the same (always, for 2023), never across a change.
                var salt = new StringBuilder();
                var conds = ValueParsers.ParseConds(name, f.Op, v, s =>
                {
                    var r = ValueParsers.ParseDate(s, now);
                    if (r is { } range)
                        salt.Append(range.Unknown ? "?" : string.Create(CultureInfo.InvariantCulture, $"{range.Lo}..{range.Hi}")).Append(';');
                    else salt.Append("-;");
                    return r;
                }, allowDashRange: false);
                return Make(conds) with { KeySalt = conds == null ? "" : salt.ToString() };
            }
            case PictureProp.Dimensions:
                return Make() with { Dims = ParseDims(name, f.Op, v) };
            case PictureProp.Orientation:
                return Make() with { Values = ParseNames(name, v, OrientationNames, "landscape, portrait or square") };
            case PictureProp.ExifColorSpace:
                return Make() with { Values = ParseNames(name, v, ColorSpaceNames, "srgb or uncalibrated") };
            case PictureProp.ColorMode:
                if (name == "cmyk") return Make(modes: [ColorMode.Cmyk]);
                if (name == "rgb") return Make(modes: [ColorMode.Rgb]);
                return Make(modes: ParseModes(name, v)) with { Except = f.Op == CompareOp.NotEqual };
            default: // gps:
                return Make();
        }
    }

    static readonly Dictionary<string, byte> OrientationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["landscape"] = 1, ["portrait"] = 2, ["square"] = 3,
    };

    static readonly Dictionary<string, byte> ColorSpaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["srgb"] = 1, ["uncalibrated"] = 2, ["1"] = 1, ["2"] = 2,
    };

    static readonly Dictionary<string, ColorMode> ModeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rgb"] = ColorMode.Rgb, ["cmyk"] = ColorMode.Cmyk, ["gray"] = ColorMode.Gray, ["grey"] = ColorMode.Gray,
        ["grayscale"] = ColorMode.Gray, ["greyscale"] = ColorMode.Gray, ["lab"] = ColorMode.Lab, ["indexed"] = ColorMode.Indexed,
        ["bitmap"] = ColorMode.Bitmap, ["multichannel"] = ColorMode.Multichannel, ["duotone"] = ColorMode.Duotone,
    };

    /// <summary>A ';' list of names; null when empty (any known value).</summary>
    static byte[]? ParseNames(string name, string value, Dictionary<string, byte> names, string choices)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = new List<byte>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(names.TryGetValue(part, out byte b) ? b : throw new QueryException($"{name}: invalid value '{part}' (use {choices})"));
        return list.Count == 0 ? null : [.. list];
    }

    static ColorMode[]? ParseModes(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = new List<ColorMode>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(ModeNames.TryGetValue(part, out var m)
                ? m
                : throw new QueryException($"{name}: invalid value '{part}' (use rgb, cmyk, gray, lab, indexed, bitmap, multichannel or duotone)"));
        return list.Count == 0 ? null : [.. list];
    }

    /// <summary>
    /// dimensions: WxH (both axes; either side may be empty: 1920x is any height), with an operator for both axes
    /// (&gt;=1920x1080), or a range WxH..WxH; ';' = OR. Null when empty (any known size).
    /// </summary>
    internal static DimCond[]? ParseDims(string name, CompareOp op, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = new List<DimCond>();
        foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = raw;
            var partOp = op;
            foreach (var (tok, o) in Ops)
                if (part.StartsWith(tok, StringComparison.Ordinal)) { partOp = o; part = part[tok.Length..]; break; }
            int dots = part.IndexOf("..", StringComparison.Ordinal);
            if (dots >= 0)
            {
                var (w1, h1) = Dim(name, part[..dots], raw);
                var (w2, h2) = Dim(name, part[(dots + 2)..], raw);
                list.Add(new DimCond(CompareOp.None, w1, h1, w2, h2, IsRange: true));
            }
            else
            {
                var (w, h) = Dim(name, part, raw);
                if (w == 0 && h == 0) throw new QueryException($"{name}: invalid value '{raw}' (use WxH, e.g. 1920x1080)");
                list.Add(new DimCond(partOp, w, h));
            }
        }
        return list.Count == 0 ? null : [.. list];
    }

    static readonly (string Token, CompareOp Op)[] Ops =
        [(">=", CompareOp.GreaterOrEqual), ("<=", CompareOp.LessOrEqual), ("!=", CompareOp.NotEqual), ("==", CompareOp.Equal),
         (">", CompareOp.Greater), ("<", CompareOp.Less), ("=", CompareOp.Equal)];

    static (int Width, int Height) Dim(string name, string s, string raw)
    {
        s = s.Trim();
        if (s.Length == 0) return (0, 0);
        int x = s.IndexOfAny(['x', 'X', '×', '*']);
        string a = x < 0 ? s : s[..x], b = x < 0 ? "" : s[(x + 1)..];
        if (x < 0) throw new QueryException($"{name}: invalid value '{raw}' (use WxH, e.g. 1920x1080)");
        return (Side(name, a, raw), Side(name, b, raw));
    }

    static int Side(string name, string s, string raw)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0
            ? v
            : throw new QueryException($"{name}: invalid value '{raw}' (use WxH, e.g. 1920x1080)");
    }

    static readonly Dictionary<string, DocStatus[]> StatusNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = [DocStatus.Ok], ["truncated"] = [DocStatus.Truncated], ["notext"] = [DocStatus.NoText],
        ["cloud"] = [DocStatus.CloudOnly], ["optedout"] = [DocStatus.OptedOut], ["efs"] = [DocStatus.EfsEncrypted],
        ["password"] = [DocStatus.PasswordProtected], ["toobig"] = [DocStatus.TooBig], ["damaged"] = [DocStatus.Damaged],
        ["unsupported"] = [DocStatus.Unsupported], ["denied"] = [DocStatus.AccessDenied], ["timeout"] = [DocStatus.TimedOut],
        ["crashed"] = [DocStatus.Crashed],
        ["failed"] = [DocStatus.PasswordProtected, DocStatus.TooBig, DocStatus.Damaged, DocStatus.Unsupported, DocStatus.AccessDenied,
            DocStatus.TimedOut, DocStatus.Crashed],
    };

    /// <summary>contentstatus: a ';' list (failed = any problem); empty = every document the index knows; != excludes the list.</summary>
    static DocStatus[] Statuses(FunctionNode f)
    {
        var all = Enum.GetValues<DocStatus>();
        if (string.IsNullOrWhiteSpace(f.Value)) return all;
        var set = new HashSet<DocStatus>();
        foreach (var part in f.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!StatusNames.TryGetValue(part, out var statuses))
                throw new QueryException($"{f.Name}: invalid value '{part}' (use ok, truncated, notext, cloud, optedout, efs, password, toobig, damaged, unsupported, denied, timeout, crashed or failed)");
            set.UnionWith(statuses);
        }
        return f.Op == CompareOp.NotEqual ? [.. all.Where(s => !set.Contains(s))] : [.. set.Order()];
    }
}
