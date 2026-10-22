using System.Text;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Core.Content;

/// <summary>
/// A query's content side, parsed once: finds its words in a line with the index's own rules (column, tooltip, preview).
/// It holds the content: words and the plain words that look inside files (positive ones only, as name highlighting does),
/// the fields' words (title:, keywords: … found only in that field, see the snippet), and the picture conditions in words.
/// Immutable and safe from any thread.
/// </summary>
public sealed class ContentHighlighter
{
    /// <summary>One way to find words: a word or phrase with its match rules, or a wildcard over the index's words.</summary>
    sealed class Finder(QueryWord? word, GlobPattern? glob, bool matchCase, bool matchDiacritics, bool wholeWord)
    {
        public readonly QueryWord? Word = word;
        public readonly GlobPattern? Glob = glob;
        public readonly bool MatchCase = matchCase, MatchDiacritics = matchDiacritics, WholeWord = wholeWord;
    }

    readonly List<Finder> _body = [];
    readonly List<(DocField Field, DocField? Also, Finder Finder)> _fields = [];
    readonly List<(ContentTerm Term, bool Negated)> _pictures = [];

    ContentHighlighter(ContentPlan plan)
    {
        var keys = new List<string>(plan.Terms.Count);
        foreach (var (node, term) in plan.Terms)
        {
            bool negated = plan.IsNegated(node);
            keys.Add((negated ? "!" : "") + term.StableKey);
            if (term.Kind == ContentTermKind.Picture)
            {
                _pictures.Add((term, negated));
                continue;
            }
            if (negated) continue;
            Finder? finder = term.Kind switch
            {
                ContentTermKind.Words or ContentTermKind.Field when term.Word != null
                    => new Finder(term.Word, null, term.MatchCase, term.MatchDiacritics, term.WholeWord),
                ContentTermKind.Glob => new Finder(null, term.Glob, false, false, false),
                _ => null,
            };
            if (finder == null) continue;
            if (term.Field is { } field) _fields.Add((field, term.AlsoField, finder));
            else _body.Add(finder);
        }
        Key = string.Join('\u001F', keys);
        NeedsMode = plan.NeedsMode;
        ExpandsPlainWords = plan.HasPlainExpansion;
        PictureConditions = Describe(_pictures);
    }

    /// <summary>Null when the query never looks inside files (options.MatchContent decides plain words), or when it is invalid.</summary>
    public static ContentHighlighter? Create(string query, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        options ??= new SearchOptions();
        ContentPlan plan;
        try { plan = ContentPlan.Build(QueryParser.Parse(query, options), options, ContentMode.DocumentsAndPictures, DateTime.Now); }
        catch (QueryException) { return null; } // the search shows the error
        return plan.IsEmpty ? null : new ContentHighlighter(plan);
    }

    /// <summary>Stable key of the content terms and their flags (snippet caches).</summary>
    public string Key { get; }
    /// <summary>
    /// The least mode the query needs (Documents for content:/fields, DocumentsAndPictures for picture functions); plain
    /// words need none (they still match names), so a names-only search never asks for a mode.
    /// </summary>
    public ContentMode NeedsMode { get; }
    /// <summary>The query's plain words also look inside files.</summary>
    public bool ExpandsPlainWords { get; }
    /// <summary>Picture conditions in words for banners ("dpi &lt; 300 · CMYK"); null when none.</summary>
    public string? PictureConditions { get; }

    /// <summary>Some words are looked for in the body (content: or plain words).</summary>
    internal bool HasBodyWords => _body.Count > 0;
    /// <summary>The query compares pictures' details.</summary>
    internal bool HasPictureConditions => _pictures.Count > 0;
    /// <summary>Picture properties the positive conditions compare (the snippet shows them).</summary>
    internal IEnumerable<PictureProp> PictureProps => _pictures.Where(p => !p.Negated && p.Term.Prop != null).Select(p => p.Term.Prop!.Value);

    /// <summary>Ranges of matching words in the text, merged.</summary>
    public IReadOnlyList<(int Start, int Length)> Find(ReadOnlySpan<char> text) => FindIn(text, null);

    /// <summary>First match at or after from; -1 when none.</summary>
    public int FirstMatch(ReadOnlySpan<char> text, int from, out int length) => FirstIn(text, from, null, out length);

    /// <summary>Ranges of matching words in a body (field null) or in one field's value (the body's words and that field's), merged.</summary>
    internal IReadOnlyList<(int Start, int Length)> FindIn(ReadOnlySpan<char> text, DocField? field)
    {
        var ranges = new List<(int Start, int Length)>();
        if (text.IsEmpty) return ranges;
        foreach (var f in Finders(field)) Collect(f, text, ranges);
        return Merge(ranges);
    }

    /// <summary>The first match at or after from in a body (field null) or a field's value; -1 when none.</summary>
    internal int FirstIn(ReadOnlySpan<char> text, int from, DocField? field, out int length)
    {
        int best = -1;
        length = 0;
        if (text.IsEmpty || from >= text.Length) return -1;
        foreach (var f in Finders(field))
        {
            var (start, len) = First(f, text, Math.Max(0, from));
            if (start >= 0 && (best < 0 || start < best || (start == best && len > length))) { best = start; length = len; }
        }
        return best;
    }

    IEnumerable<Finder> Finders(DocField? field)
    {
        foreach (var f in _body) yield return f;
        if (field is not { } wanted) yield break;
        foreach (var (f, also, finder) in _fields)
            if (f == wanted || also == wanted) yield return finder;
    }

    static (int Start, int Length) First(Finder f, ReadOnlySpan<char> text, int from)
    {
        if (f.Word is { } word) return ContentTokenizer.FindSequence(text, word, f.MatchCase, f.MatchDiacritics, f.WholeWord, from);
        var sink = new GlobSink(f.Glob!, from, firstOnly: true);
        ContentTokenizer.Tokenize(text, sink);
        return sink.Ranges.Count > 0 ? sink.Ranges[0] : (-1, 0);
    }

    static void Collect(Finder f, ReadOnlySpan<char> text, List<(int Start, int Length)> into)
    {
        if (f.Word is { } word)
        {
            for (int pos = 0; pos < text.Length;)
            {
                var (start, len) = ContentTokenizer.FindSequence(text, word, f.MatchCase, f.MatchDiacritics, f.WholeWord, pos);
                if (start < 0) break;
                into.Add((start, len));
                pos = start + Math.Max(len, 1);
            }
            return;
        }
        var sink = new GlobSink(f.Glob!, 0, firstOnly: false);
        ContentTokenizer.Tokenize(text, sink);
        into.AddRange(sink.Ranges);
    }

    /// <summary>
    /// Words of a text whose folded form the wildcard matches (field-scoped copies left out). Tokens come nearly in text
    /// order (a code's compound after its parts, starting where the first part does), so the first match is the one with
    /// the lowest start, the longest at equal starts.
    /// </summary>
    sealed class GlobSink(GlobPattern glob, int from, bool firstOnly) : ITokenSink
    {
        public readonly List<(int Start, int Length)> Ranges = [];

        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (kind == TokenKind.Field || start < from) return;
            if (firstOnly && Ranges.Count > 0 && (start > Ranges[0].Start || (start == Ranges[0].Start && length <= Ranges[0].Length))) return;
            if (!glob.IsMatch(folded)) return;
            if (!firstOnly || Ranges.Count == 0) Ranges.Add((start, length));
            else Ranges[0] = (start, length);
        }
    }

    static List<(int Start, int Length)> Merge(List<(int Start, int Length)> ranges)
    {
        if (ranges.Count <= 1) return ranges;
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var merged = new List<(int Start, int Length)>(ranges.Count);
        var (s, e) = (ranges[0].Start, ranges[0].Start + ranges[0].Length);
        for (int i = 1; i < ranges.Count; i++)
        {
            var r = ranges[i];
            if (r.Start <= e) e = Math.Max(e, r.Start + r.Length);
            else
            {
                merged.Add((s, e - s));
                (s, e) = (r.Start, r.Start + r.Length);
            }
        }
        merged.Add((s, e - s));
        return merged;
    }

    // ---------------- words for people ----------------
    // A snippet's place prefix ("p. 4 · ") and field names are ContentPresentation's (the app's one source of wording).

    /// <summary>The colour mode's name ("CMYK", "RGB", "Gray").</summary>
    internal static string ModeName(ColorMode mode) => mode switch
    {
        ColorMode.Rgb => "RGB",
        ColorMode.Cmyk => "CMYK",
        ColorMode.Gray => "Gray",
        ColorMode.Lab => "Lab",
        ColorMode.Indexed => "Indexed",
        ColorMode.Bitmap => "Bitmap",
        ColorMode.Multichannel => "Multichannel",
        ColorMode.Duotone => "Duotone",
        _ => "",
    };

    static string? Describe(List<(ContentTerm Term, bool Negated)> pictures)
    {
        if (pictures.Count == 0) return null;
        var parts = new List<string>(pictures.Count);
        foreach (var (term, negated) in pictures)
        {
            string text = negated && term.Prop == PictureProp.Gps ? "no location" : Describe(term);
            if (text.Length > 0) parts.Add(negated && term.Prop != PictureProp.Gps ? "not " + text : text);
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"dpi &lt; 300", "width ≥ 3000", "CMYK", "landscape", "taken 2023", "has a location".</summary>
    static string Describe(ContentTerm term)
    {
        string source = term.Source;
        int colon = source.IndexOf(':');
        string name = colon < 0 ? source : source[..colon];
        string rest = colon < 0 ? "" : source[(colon + 1)..];
        string op = "";
        foreach (var (token, shown) in OpWords)
            if (rest.StartsWith(token, StringComparison.Ordinal)) { op = shown; rest = rest[token.Length..]; break; }
        string value = rest.Replace("..", "–").Replace(";", " or ");
        switch (term.Prop)
        {
            case PictureProp.ColorMode:
                if (name is "cmyk" or "rgb") return name.ToUpperInvariant();
                if (term.Modes is not { } modes) return "known colour mode";
                return (term.Except ? "not " : "") + string.Join(" or ", modes.Select(ModeName));
            case PictureProp.Gps:
                return "has a location";
            case PictureProp.Orientation:
                return value.Length == 0 ? "known orientation" : value;
            case PictureProp.ExifColorSpace:
                return value.Length == 0 ? "known colour space" : value.Replace("srgb", "sRGB", StringComparison.OrdinalIgnoreCase);
            case PictureProp.DateTaken:
                return value.Length == 0 ? "date taken known" : "taken " + op.TrimStart() + value;
            case PictureProp.Dimensions:
                return value.Length == 0 ? "known size" : "size" + (op.Length > 0 ? op : " ") + value.Replace("x", " × ", StringComparison.OrdinalIgnoreCase).Trim();
        }
        string label = term.Prop switch
        {
            PictureProp.Width => "width",
            PictureProp.Height => "height",
            PictureProp.BitDepth => "bit depth",
            PictureProp.Dpi => "dpi",
            PictureProp.DpiX => "horizontal dpi",
            PictureProp.DpiY => "vertical dpi",
            PictureProp.Rating => "rating",
            _ => name,
        };
        if (value.Length == 0) return label + " known";
        return label + (op.Length > 0 ? op : " ") + value;
    }

    static readonly (string Token, string Shown)[] OpWords =
        [(">=", " ≥ "), ("<=", " ≤ "), ("!=", " ≠ "), ("==", " = "), (">", " > "), ("<", " < "), ("=", " = ")];

    /// <summary>The picture details a snippet shows for a match by numbers: "2480 × 3508 · 300 dpi · CMYK", plus what the conditions compare.</summary>
    internal string PictureLine(in PictureInfo info)
    {
        var parts = new List<string>(6);
        var (w, h) = info.Shown;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (w > 0 && h > 0) parts.Add(string.Create(culture, $"{w} × {h}"));
        int dpi = ContentResolve.Dpi(info);
        if (dpi > 0) parts.Add(string.Create(culture, $"{dpi} dpi"));
        if (info.ColorMode != ColorMode.Unknown) parts.Add(ModeName(info.ColorMode));
        var props = PictureProps.ToHashSet();
        if (props.Contains(PictureProp.BitDepth) && info.BitDepth > 0) parts.Add(string.Create(culture, $"{info.BitDepth}-bit"));
        if (props.Contains(PictureProp.DateTaken) && info.DateTakenUtc > 0)
        {
            try { parts.Add("taken " + DateTime.FromFileTimeUtc(info.DateTakenUtc).ToLocalTime().ToString("d", culture)); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (props.Contains(PictureProp.Rating) && info.Rating > 0) parts.Add(new string('★', Math.Min((int)info.Rating, 5)));
        if (props.Contains(PictureProp.ExifColorSpace) && info.ExifColorSpace != 0) parts.Add(info.ExifColorSpace == 1 ? "sRGB" : "uncalibrated");
        if (props.Contains(PictureProp.Gps) && info.HasGps) parts.Add("has a location");
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(p);
        }
        return sb.ToString();
    }
}
