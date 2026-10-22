using System.Text;

namespace Anything.Core.Search;

/// <summary>
/// Parses Everything 1.4 style search text:
/// space = AND, <c>|</c> = OR (binds tighter than AND), <c>!</c> = NOT, <c>&lt; &gt;</c> = grouping,
/// <c>"..."</c> = literal text, <c>name:</c> modifiers / functions / macros.
/// The parser is forgiving: unbalanced quotes and brackets are closed at the end of input,
/// stray operators are ignored. It never throws.
/// </summary>
public static class QueryParser
{
    enum Kind { Modifier, Restriction, Function }

    // Modifiers change the flags of the following term or group.
    static readonly Dictionary<string, Func<TermFlags, TermFlags>> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["case"] = f => f with { MatchCase = true },
        ["nocase"] = f => f with { MatchCase = false },
        ["diacritics"] = f => f with { MatchDiacritics = true },
        ["nodiacritics"] = f => f with { MatchDiacritics = false },
        ["path"] = f => f with { Path = PathMode.Path },
        ["nopath"] = f => f with { Path = PathMode.NoPath },
        ["regex"] = f => f with { Regex = true },
        ["noregex"] = f => f with { Regex = false },
        ["ww"] = f => f with { WholeWord = true },
        ["wholeword"] = f => f with { WholeWord = true },
        ["wholewords"] = f => f with { WholeWord = true },
        ["noww"] = f => f with { WholeWord = false },
        ["nowholeword"] = f => f with { WholeWord = false },
        ["nowholewords"] = f => f with { WholeWord = false },
        ["wildcards"] = f => f with { Wildcards = true },
        ["nowildcards"] = f => f with { Wildcards = false },
    };

    /// <summary>Value-less restrictions: alone they filter, before a term/group they are ANDed with it.</summary>
    static readonly Dictionary<string, string> Restrictions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file"] = "file", ["files"] = "file",
        ["folder"] = "folder", ["folders"] = "folder",
        ["audio"] = "audio", ["zip"] = "zip", ["doc"] = "doc", ["exe"] = "exe", ["pic"] = "pic", ["video"] = "video",
        ["root"] = "root", ["empty"] = "empty", ["dupe"] = "dupe",
    };

    static readonly Dictionary<string, string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ext"] = "ext", ["extension"] = "ext",
        ["size"] = "size",
        ["dm"] = "dm", ["datemodified"] = "dm",
        ["dc"] = "dc", ["datecreated"] = "dc",
        ["da"] = "da", ["dateaccessed"] = "da",
        ["rc"] = "rc", ["recentchange"] = "rc",
        ["dr"] = "dr", ["daterun"] = "dr",
        ["runcount"] = "runcount",
        ["attrib"] = "attrib", ["attributes"] = "attrib",
        ["parent"] = "parent", ["infolder"] = "parent", ["nosubfolders"] = "parent",
        ["startwith"] = "startwith", ["endwith"] = "endwith",
        ["len"] = "len",
        ["depth"] = "depth", ["parents"] = "depth",
        ["child"] = "child",
        ["childcount"] = "childcount", ["childfilecount"] = "childfilecount", ["childfoldercount"] = "childfoldercount",
        ["type"] = "type",
    };

    static readonly (string Name, char Literal)[] LiteralMacros =
        [("quot:", '"'), ("apos:", '\''), ("amp:", '&'), ("lt:", '<'), ("gt:", '>')];

    /// <summary>Parses <paramref name="query"/> with defaults from <paramref name="options"/> (the filter is not included).</summary>
    public static QueryNode Parse(string? query, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(query)) return AllNode.Instance;
        var flags = TermFlags.From(options);
        if (options.Regex) return new TextNode(query.Trim(), flags with { Regex = true });
        return new Parser(query).Run(flags with { Regex = false });
    }

    /// <summary>
    /// How <see cref="SearchOptions.Filter"/> is read, whatever the window's toggles: ignoring case, with whole words off,
    /// no regex, wildcards on and Match path off, but with diacritics matched, so a scope of <c>C:\cafe</c> does not take
    /// <c>C:\café</c>.
    /// </summary>
    public static SearchOptions FilterOptions { get; } = new() { MatchDiacritics = true };

    /// <summary>
    /// Parses the query with <paramref name="options"/> and ANDs the active filter, parsed with <see cref="FilterOptions"/>:
    /// the app builds the filter (macros, folder scopes), and Match whole words or Match case would empty a scope like
    /// <c>"C:\x\"</c> or one typed in another case.
    /// </summary>
    public static QueryNode ParseWithFilter(string? query, SearchOptions options)
    {
        var node = Parse(query, options);
        if (string.IsNullOrWhiteSpace(options.Filter)) return node;
        var filter = Parse(options.Filter, FilterOptions);
        return QueryNode.And(node, filter);
    }

    static bool IsKnownName(string name) =>
        Modifiers.ContainsKey(name) || Restrictions.ContainsKey(name) || Functions.ContainsKey(name);

    sealed class Parser(string s)
    {
        int _pos;
        int _depth;

        public QueryNode Run(TermFlags flags) => ParseSequence(flags);

        bool AtEnd => _pos >= s.Length;

        void SkipWs()
        {
            while (_pos < s.Length && char.IsWhiteSpace(s[_pos])) _pos++;
        }

        bool AtTermEnd() =>
            AtEnd || char.IsWhiteSpace(s[_pos]) || s[_pos] == '|' || (s[_pos] == '>' && _depth > 0);

        // sequence := or (WS or)*      (AND by juxtaposition)
        QueryNode ParseSequence(TermFlags flags)
        {
            var items = new List<QueryNode?>();
            while (true)
            {
                SkipWs();
                if (AtEnd) break;
                char c = s[_pos];
                if (c == '>')
                {
                    if (_depth > 0) break;
                    _pos++; // stray '>' at top level
                    continue;
                }
                if (c == '|') { _pos++; continue; } // stray '|'
                items.Add(ParseOr(flags));
            }
            return QueryNode.And(items);
        }

        // or := unary ('|' unary)*
        QueryNode? ParseOr(TermFlags flags)
        {
            var alts = new List<QueryNode?> { ParseUnary(flags) };
            while (true)
            {
                int save = _pos;
                SkipWs();
                if (AtEnd || s[_pos] != '|') { _pos = save; break; }
                while (!AtEnd && (s[_pos] == '|' || char.IsWhiteSpace(s[_pos]))) _pos++;
                if (AtEnd || (s[_pos] == '>' && _depth > 0)) break;
                alts.Add(ParseUnary(flags));
            }
            return alts.Count == 1 ? alts[0] : QueryNode.Or(alts);
        }

        QueryNode? ParseUnary(TermFlags flags)
        {
            if (AtEnd) return null;
            char c = s[_pos];
            if (c == '!')
            {
                _pos++;
                SkipWs();
                if (AtEnd || s[_pos] == '|' || (s[_pos] == '>' && _depth > 0)) return null;
                var inner = ParseUnary(flags);
                return inner is null ? null : inner is AllNode ? null : new NotNode(inner);
            }
            if (c == '<')
            {
                _pos++;
                _depth++;
                var inner = ParseSequence(flags);
                if (!AtEnd && s[_pos] == '>') _pos++;
                _depth--;
                return inner is AllNode ? null : inner;
            }

            if (TryReadName(out string name))
            {
                if (Modifiers.TryGetValue(name, out var apply))
                {
                    var f = apply(flags);
                    if (AtTermEnd()) return null; // bare modifier: nothing to apply to
                    return ParseUnary(f);
                }
                if (Restrictions.TryGetValue(name, out string? canon))
                {
                    var restriction = new FunctionNode(canon, CompareOp.None, "", flags);
                    if (AtTermEnd()) return restriction;
                    return QueryNode.And(restriction, ParseUnary(flags));
                }
                if (Functions.TryGetValue(name, out canon))
                {
                    var op = ReadOperator();
                    string value = ReadWord(flags with { Regex = false }, allowMacros: false);
                    return new FunctionNode(canon, op, value, flags);
                }
            }

            string text = ReadWord(flags, allowMacros: true);
            return text.Length == 0 ? null : new TextNode(text, flags);
        }

        /// <summary>Reads "name:" at the current position when name is a known modifier/function.</summary>
        bool TryReadName(out string name)
        {
            name = "";
            int i = _pos;
            while (i < s.Length && char.IsAsciiLetterOrDigit(s[i])) i++;
            if (i == _pos || i >= s.Length || s[i] != ':') return false;
            string candidate = s[_pos..i];
            if (!IsKnownName(candidate)) return false;
            name = candidate;
            _pos = i + 1;
            return true;
        }

        CompareOp ReadOperator()
        {
            if (AtEnd) return CompareOp.None;
            string rest = s[_pos..];
            (string tok, CompareOp op)[] ops =
                [(">=", CompareOp.GreaterOrEqual), ("<=", CompareOp.LessOrEqual), ("!=", CompareOp.NotEqual),
                 ("==", CompareOp.Equal), (">", CompareOp.Greater), ("<", CompareOp.Less), ("=", CompareOp.Equal)];
            foreach (var (tok, op) in ops)
                if (rest.StartsWith(tok, StringComparison.Ordinal)) { _pos += tok.Length; return op; }
            return CompareOp.None;
        }

        /// <summary>
        /// Reads a term: runs until unquoted whitespace, '|', '&lt;' or '&gt;'. Quotes are removed and protect
        /// their content. Regex terms only stop at whitespace (trailing '&gt;' still closes open groups).
        /// </summary>
        string ReadWord(TermFlags flags, bool allowMacros)
        {
            var sb = new StringBuilder();
            bool inQuote = false;
            bool macroBoundary = true;
            while (_pos < s.Length)
            {
                char c = s[_pos];
                if (c == '"') { inQuote = !inQuote; _pos++; macroBoundary = false; continue; }
                if (!inQuote)
                {
                    if (char.IsWhiteSpace(c)) break;
                    if (!flags.Regex && (c == '|' || c == '<' || c == '>')) break;
                    if (allowMacros && macroBoundary && TryLiteralMacro(out char lit))
                    {
                        sb.Append(lit);
                        continue;
                    }
                }
                macroBoundary = false;
                sb.Append(c);
                _pos++;
            }
            if (flags.Regex && _depth > 0)
            {
                int stripped = 0;
                while (stripped < _depth && sb.Length > 0 && sb[^1] == '>' && s[_pos - 1] == '>')
                {
                    sb.Length--;
                    _pos--;
                    stripped++;
                }
            }
            return sb.ToString();
        }

        bool TryLiteralMacro(out char literal)
        {
            foreach (var (name, lit) in LiteralMacros)
            {
                if (string.Compare(s, _pos, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _pos += name.Length;
                    literal = lit;
                    return true;
                }
            }
            literal = '\0';
            return false;
        }
    }
}
