using System.Text;
using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>What a content term asks for.</summary>
internal enum ContentTermKind : byte { Words, Glob, HasText, Field, Picture, Pages, Status }

/// <summary>Picture properties a Picture term compares.</summary>
internal enum PictureProp : byte { Width, Height, Dimensions, Orientation, BitDepth, Dpi, DpiX, DpiY, ColorMode, ExifColorSpace, DateTaken, Gps, Rating }

/// <summary>
/// How a word finds its documents in one segment: a direct term (a word, a code's compound, or a field's word) whose
/// postings are matches, plus candidates that the stored text must confirm (FindSequence): the documents of every group.
/// </summary>
/// <param name="Direct">The folded term looked up directly; null = none.</param>
/// <param name="DirectPrefix">Every term starting with Direct (else exactly Direct).</param>
/// <param name="DirectNeedsVerify">Direct's documents must be confirmed too (case:, diacritics:, a whole-word bigram).</param>
/// <param name="Groups">ANDed into candidates (minus the direct matches), confirmed in the text; empty = no candidates.</param>
/// <param name="Joins">
/// ww: one word: also candidates where the text joins it from a code's parts (ww:inv2024 is a whole word of INV-2024-001,
/// whose words are inv, 2024, 001 and inv2024001): documents with a part that begins the word and, after it, a word starting
/// with the rest, or the code's compound starting with the word and its next part. At most
/// <see cref="ContentResolve.MaxJoinCandidates"/> per segment are confirmed; past that, the exact word only (§7).
/// </param>
internal sealed record WordPlan(string? Direct, bool DirectPrefix, bool DirectNeedsVerify, IReadOnlyList<WordGroup> Groups, bool Joins = false)
{
    /// <summary>Some documents need the stored text to confirm them.</summary>
    public bool Verifies => DirectNeedsVerify || Groups.Count > 0 || Joins;
}

/// <summary>
/// One word, code or bigram run of a query, as candidates: documents with all of its parts (each a prefix or an exact
/// term), or, for a code, with a word starting with its compound (the text may join what the query separates: "e-mail"
/// is found in "email", "INV-2024-001" in "inv2024001").
/// </summary>
internal sealed record WordGroup(IReadOnlyList<(string Token, bool Prefix)> Parts, string? Compound = null);

/// <summary>One dimensions: condition: both axes compared with the operator, or a range of each (0 = any).</summary>
internal readonly record struct DimCond(CompareOp Op, int Width, int Height, int ToWidth = 0, int ToHeight = 0, bool IsRange = false)
{
    /// <summary>Whether a picture shown at width × height meets it.</summary>
    public bool Eval(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (IsRange)
            return (Width == 0 || width >= Width) && (Height == 0 || height >= Height)
                && (ToWidth == 0 || width <= ToWidth) && (ToHeight == 0 || height <= ToHeight);
        return Axis(width, Width) && Axis(height, Height);
    }

    bool Axis(int v, int want) => want == 0 || Op switch
    {
        CompareOp.Less => v < want,
        CompareOp.LessOrEqual => v <= want,
        CompareOp.Greater => v > want,
        CompareOp.GreaterOrEqual => v >= want,
        CompareOp.NotEqual => v != want,
        _ => v == want,
    };
}

/// <summary>One content predicate in canonical form; Key is the cache key (per segment id in DocSetCache).</summary>
internal sealed record ContentTerm(ContentTermKind Kind, QueryWord? Word, GlobPattern? Glob, DocField? Field, PictureProp? Prop,
    NumCond[]? Conds, ColorMode[]? Modes, DocStatus[]? Statuses, bool MatchCase, bool MatchDiacritics, bool WholeWord, bool Plain)
{
    /// <summary>The term as written, canonical ("content:report", "dpi:<300", "cmyk:"): part of the key and the banner text.</summary>
    public string Source { get; init; } = "";
    /// <summary>Words and Field terms: how a segment's documents are found.</summary>
    public WordPlan? Lookup { get; init; }
    /// <summary>A second field searched too (camera: is the make or the model).</summary>
    public DocField? AlsoField { get; init; }
    /// <summary>Glob terms: the folded literal text before the first wildcard (the dictionary range scanned).</summary>
    public string GlobPrefix { get; init; } = "";
    /// <summary>dimensions: conditions (OR).</summary>
    public DimCond[]? Dims { get; init; }
    /// <summary>orientation: (1 landscape, 2 portrait, 3 square) or colorspace: (EXIF 1 sRGB, 2 uncalibrated) values (OR).</summary>
    public byte[]? Values { get; init; }
    /// <summary>colormode:!=… : the modes listed are excluded (among pictures whose mode is known).</summary>
    public bool Except { get; init; }
    /// <summary>Added to the key when the resolved set depends on more than the text (datetaken: carries the periods its value resolved to now).</summary>
    public string KeySalt { get; init; } = "";

    /// <summary>
    /// Stable text of the term and its flags (the cache key; equal keys resolve to equal sets on a segment). Built on each
    /// call: callers keep it. A plain word's set depends only on its lookup (it is never confirmed in the stored text), so
    /// its key is the lookup: the keystrokes inv, inv- and inv-2 (or INV), report and report.p share one cached set and
    /// one running lookup.
    /// </summary>
    public string Key => Plain && Lookup is { Verifies: false } plan && Field == null && AlsoField == null
        ? "wP" + (WholeWord ? 'W' : '-') + '|' + plan.Direct + (plan.DirectPrefix ? '*' : '.')
        : BuildKey(withSalt: true);

    /// <summary>The key without what changes from search to search (the snippet caches' part of ContentHighlighter.Key: it keeps the typed word, which highlighting depends on).</summary>
    internal string StableKey => BuildKey(withSalt: false);

    string BuildKey(bool withSalt)
    {
        var sb = new StringBuilder(32);
        sb.Append(Kind switch
        {
            ContentTermKind.Words => 'w', ContentTermKind.Glob => 'g', ContentTermKind.HasText => 't', ContentTermKind.Field => 'f',
            ContentTermKind.Picture => 'p', ContentTermKind.Pages => 'n', _ => 's',
        });
        sb.Append(Plain ? 'P' : 'E').Append(MatchCase ? 'C' : '-').Append(MatchDiacritics ? 'D' : '-').Append(WholeWord ? 'W' : '-');
        if (Field is { } f) sb.Append((char)('A' + (int)f));
        if (AlsoField is { } a) sb.Append((char)('A' + (int)a));
        sb.Append('|').Append(Source);
        if (Word != null) sb.Append('|').Append(Word.Text).Append(Word.LastIsPrefix ? '*' : '.');
        if (withSalt && KeySalt.Length > 0) sb.Append('|').Append(KeySalt);
        return sb.ToString();
    }
}
