using System.Text;
using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>
/// Resolves content terms to document sets on one segment at a time (segments are immutable, so a segment's set is
/// cached per term): prefix ranges, exact words, codes' compounds, globs over the dictionary, fields, picture records and
/// row values. Words that must stand next to each other, or match case or accents, come back as candidates that
/// <see cref="Confirm"/> checks in the stored text. Everything here runs before FileIndex.Lock.
/// </summary>
internal static class ContentResolve
{
    /// <summary>
    /// One segment's answer: documents that match outright (ordinal 0), and candidates the stored text must confirm.
    /// Unanswered: the segment can't be asked yet (made by other word rules); its documents are missing, not absent.
    /// </summary>
    internal readonly record struct Found(DocSet Matches, int[]? Candidates, bool Unanswered = false);

    /// <summary>ww: candidates joined from a code's parts confirmed per segment at most; past that, the exact word only.</summary>
    internal const int MaxJoinCandidates = 256;

    /// <summary>Matching terms whose postings are read together before the rest of a range is visited (bounded memory).</summary>
    internal const int TermBatch = 4096;

    // Field-scoped terms start with byte 0x01; every plain word starts above it (text never produces 0x01).
    static readonly byte[] FirstPlainTerm = [0x02];
    static readonly byte[] PastEveryTerm = [0xFF];

    /// <summary>The term's documents in one segment (as a one-segment set), and candidates to confirm.</summary>
    public static Found Segment(ContentTerm term, IContentSegment seg, CancellationToken ct)
    {
        // A segment made by other word rules can't be asked with these words (Analyze has no version yet): it is rebuilt
        // from its stored text through merges meanwhile, so its documents are reported missing (ContentPending), never
        // cached as having none.
        if (term.Kind is ContentTermKind.Words or ContentTermKind.Field or ContentTermKind.Glob && seg.TokenizerVersion != ContentTokenizer.Version)
            return new(DocSet.Empty, null, Unanswered: true);
        return term.Kind switch
        {
            ContentTermKind.Words or ContentTermKind.Field => Words(term, seg, ct),
            ContentTermKind.Glob => new(Glob(term, seg, ct), null),
            ContentTermKind.Picture => new(Pictures(term, seg, ct), null),
            _ => new(Rows(term, seg, ct), null),
        };
    }

    /// <summary>Whether a candidate's stored text has the term's words next to each other under its case, accent and whole-word rules.</summary>
    public static bool Confirm(ContentTerm term, IContentSegment seg, int doc)
    {
        if (term.Word is not { } word || seg.ReadStored(doc) is not { } stored) return false;
        bool Has(string text) => text.Length > 0
            && ContentTokenizer.FindSequence(text, word, term.MatchCase, term.MatchDiacritics, term.WholeWord).Start >= 0;
        if (term.Field is { } field)
        {
            foreach (var (f, value) in stored.Fields)
                if ((f == field || f == term.AlsoField) && Has(value)) return true;
            return false;
        }
        // Field values are plain words too (a picture's keywords find it), so a body word may stand in a field.
        if (Has(stored.Body)) return true;
        foreach (var (_, value) in stored.Fields)
            if (Has(value)) return true;
        return false;
    }

    /// <summary>The term prefixes a term is looked up under: none for content:, the field's (and camera:'s second field) for fields.</summary>
    static string[] Scopes(ContentTerm term) => term.Field is not { } f
        ? [""]
        : term.AlsoField is { } also ? [ContentTokenizer.FieldPrefix(f), ContentTokenizer.FieldPrefix(also)] : [ContentTokenizer.FieldPrefix(f)];

    static Found Words(ContentTerm term, IContentSegment seg, CancellationToken ct)
    {
        var plan = term.Lookup ?? throw new InvalidOperationException("A word term has no lookup.");
        int[] one = [seg.DocCount];
        string[] scopes = Scopes(term);
        DocSet matches = DocSet.Empty, candidates = DocSet.Empty;
        if (plan.Direct != null)
        {
            var b = new DocSetBuilder(one);
            foreach (var scope in scopes) AddTerm(seg, scope + plan.Direct, plan.DirectPrefix, b, ct);
            var direct = b.Build();
            if (plan.DirectNeedsVerify) candidates = direct;
            else matches = direct;
        }
        if (plan.Groups.Count > 0)
        {
            foreach (var scope in scopes)
            {
                DocSet? all = null;
                foreach (var group in plan.Groups)
                {
                    var s = Group(seg, scope, group, one, ct);
                    all = all == null ? s : all.And(s);
                    if (all.Count == 0) break;
                }
                if (all is { Count: > 0 }) candidates = candidates.Or(all.Except(matches));
            }
        }
        if (plan.Joins && plan.Direct is { Length: > 0 } whole)
            foreach (var scope in scopes) candidates = candidates.Or(Joins(seg, scope, whole, matches, one, ct));
        return new(matches, candidates.Count == 0 ? null : Docs(candidates));
    }

    /// <summary>
    /// ww: one word t that the text may join from a code's parts (ww:inv2024 is a whole word of "INV-2024-001", whose words
    /// are inv, 2024, 001 and inv2024001; FindSequence skips a joiner inside a word). For each split of t at a character
    /// boundary into a head p of 2+ characters (a part of the code) and a rest: documents with the word p and either a
    /// word starting with the rest (the next part ends the match: inv + 2024), or a word starting with t (the compound,
    /// from the code's first part) and the next part t[p..b] of 2+ characters as a word (INV-2024-001-A for ww:inv2024001),
    /// or nothing more when the rest is one character (v1 + 2 in v1.2.3, for ww:v12). Words that only start with t
    /// ("reporting" for ww:report) are never candidates. Empty past <see cref="MaxJoinCandidates"/> (common words on both
    /// sides, "for" + "mat"): then the exact word alone answers, as §7 says.
    /// </summary>
    static DocSet Joins(IContentSegment seg, string scope, string t, DocSet exclude, int[] one, CancellationToken ct)
    {
        var cuts = new List<int>();
        var units = new ContentTokenizer.Units(t);
        for (bool first = true; units.MoveNext(); first = false)
            if (!first) cuts.Add(units.Start);
        DocSet found = DocSet.Empty;
        DocSet? compound = null; // the words starting with t, looked up once
        foreach (int a in cuts)
        {
            if (a < ContentTokenizer.MinLength) continue;
            var head = Exact(seg, scope + t[..a], one, ct);
            if (head.Count == 0) continue;
            string rest = t[a..];
            var tails = rest.Length >= ContentTokenizer.MinLength ? Prefix(seg, scope + rest, one, ct) : DocSet.Empty;
            DocSet? next = rest.Length < ContentTokenizer.MinLength ? head : null;
            foreach (int b in cuts)
            {
                if (b - a < ContentTokenizer.MinLength) continue;
                var part = Exact(seg, scope + t[a..b], one, ct);
                if (part.Count > 0) next = next == null ? part : next.Or(part);
            }
            if (next != null)
            {
                compound ??= Prefix(seg, scope + t, one, ct);
                tails = tails.Or(next.And(compound));
            }
            found = found.Or(head.And(tails));
            if (found.Count - exclude.Count > MaxJoinCandidates) return DocSet.Empty;
        }
        var candidates = found.Except(exclude);
        return candidates.Count > MaxJoinCandidates ? DocSet.Empty : candidates;
    }

    static DocSet Exact(IContentSegment seg, string folded, int[] one, CancellationToken ct)
    {
        var b = new DocSetBuilder(one);
        AddTerm(seg, folded, prefix: false, b, ct);
        return b.Build();
    }

    static DocSet Prefix(IContentSegment seg, string folded, int[] one, CancellationToken ct)
    {
        var b = new DocSetBuilder(one);
        AddTerm(seg, folded, prefix: true, b, ct);
        return b.Build();
    }

    /// <summary>A group's documents: those with all of its parts, or with a word starting with its compound.</summary>
    static DocSet Group(IContentSegment seg, string scope, WordGroup group, int[] one, CancellationToken ct)
    {
        DocSet? all = null;
        foreach (var (token, prefix) in group.Parts)
        {
            var b = new DocSetBuilder(one);
            AddTerm(seg, scope + token, prefix, b, ct);
            var s = b.Build();
            all = all == null ? s : all.And(s);
            if (all.Count == 0) break;
        }
        all ??= DocSet.Empty;
        if (group.Compound is not { } compound) return all;
        var joined = new DocSetBuilder(one);
        AddTerm(seg, scope + compound, prefix: true, joined, ct);
        return all.Or(joined.Build());
    }

    /// <summary>The documents of a one-segment set, ascending.</summary>
    internal static int[] Docs(DocSet set)
    {
        var docs = new int[set.Count];
        int k = 0;
        set.ForEach((_, d) => docs[k++] = d, CancellationToken.None);
        return docs;
    }

    /// <summary>Adds the postings of one exact term, or of every term starting with it.</summary>
    static void AddTerm(IContentSegment seg, string folded, bool prefix, DocSetBuilder into, CancellationToken ct)
    {
        byte[] from = Encoding.UTF8.GetBytes(folded);
        if (!prefix)
        {
            if (seg.TryGetTerm(from, out var p)) seg.ReadPostings(p, 0, into, ct);
            return;
        }
        // Valid UTF-8 never holds 0xFF, so [term, term + 0xFF) is every term starting with it.
        byte[] to = new byte[from.Length + 1];
        from.CopyTo(to, 0);
        to[^1] = 0xFF;
        AddRange(seg, from, to, keep: null, into, ct);
    }

    /// <summary>Decides whether a dictionary term's postings are wanted.</summary>
    delegate bool TermFilter(ReadOnlySpan<byte> term);

    /// <summary>
    /// Adds the postings of every term in [from, to) that keep accepts (all when null). Postings are read in batches of
    /// <see cref="TermBatch"/> terms: the visit stops, the batch is read, and the visit resumes right after the batch's last
    /// term, so a wildcard matching every word of a large segment never holds a list of millions of terms.
    /// </summary>
    static void AddRange(IContentSegment seg, byte[] from, byte[] to, TermFilter? keep, DocSetBuilder into, CancellationToken ct)
    {
        var batch = new List<TermPostings>();
        byte[] start = from;
        while (true)
        {
            batch.Clear();
            byte[]? resume = null;
            seg.Terms(start, to, (ReadOnlySpan<byte> term, in TermPostings postings) =>
            {
                if (keep != null && !keep(term)) return true;
                batch.Add(postings);
                if (batch.Count < TermBatch) return true;
                resume = new byte[term.Length + 1]; // term + 0x00: the first byte string after it
                term.CopyTo(resume);
                return false;
            }, ct);
            foreach (var p in batch) seg.ReadPostings(p, 0, into, ct);
            if (resume == null) return;
            start = resume;
        }
    }

    /// <summary>Every dictionary word the glob matches, within the literal text before its first wildcard.</summary>
    static DocSet Glob(ContentTerm term, IContentSegment seg, CancellationToken ct)
    {
        var glob = term.Glob ?? throw new InvalidOperationException("A glob term has no pattern.");
        var b = new DocSetBuilder([seg.DocCount]);
        foreach (var scope in Scopes(term))
        {
            byte[] from, to;
            if (scope.Length == 0 && term.GlobPrefix.Length == 0)
            {
                from = FirstPlainTerm;
                to = PastEveryTerm;
            }
            else
            {
                from = Encoding.UTF8.GetBytes(scope + term.GlobPrefix);
                to = new byte[from.Length + 1];
                from.CopyTo(to, 0);
                to[^1] = 0xFF;
            }
            int skip = Encoding.UTF8.GetByteCount(scope);
            AddRange(seg, from, to, bytes =>
            {
                if (bytes.Length < skip) return false;
                var word = bytes[skip..];
                Span<char> chars = word.Length <= 256 ? stackalloc char[word.Length] : new char[word.Length];
                int n = Encoding.UTF8.GetChars(word, chars);
                return glob.IsMatch(chars[..n]);
            }, b, ct);
        }
        return b.Build();
    }

    /// <summary>Row values: content: with no value, pagecount:, contentstatus: (a scan of the segment's rows).</summary>
    static DocSet Rows(ContentTerm term, IContentSegment seg, CancellationToken ct)
    {
        var b = new DocSetBuilder([seg.DocCount]);
        for (int doc = 0; doc < seg.DocCount; doc++)
        {
            if ((doc & 4095) == 4095) ct.ThrowIfCancellationRequested();
            var row = seg.Row(doc);
            bool ok = term.Kind switch
            {
                ContentTermKind.HasText => row.Status is DocStatus.Ok or DocStatus.Truncated && (row.Flags & DocFlags.HasText) != 0,
                ContentTermKind.Pages => term.Conds == null ? row.Pages > 0 : ValueParsers.Eval(term.Conds, row.Pages, row.Pages > 0),
                ContentTermKind.Status => term.Statuses != null && Array.IndexOf(term.Statuses, row.Status) >= 0,
                _ => false,
            };
            if (ok) b.Add(0, doc);
        }
        return b.Build();
    }

    /// <summary>Picture conditions: a scan of the segment's picture records (META).</summary>
    static DocSet Pictures(ContentTerm term, IContentSegment seg, CancellationToken ct)
    {
        var b = new DocSetBuilder([seg.DocCount]);
        int n = seg.PictureCount;
        for (int i = 0; i < n; i++)
        {
            if ((i & 4095) == 4095) ct.ThrowIfCancellationRequested();
            var (doc, info) = seg.Picture(i);
            if (Matches(term, info)) b.Add(0, doc);
        }
        return b.Build();
    }

    /// <summary>Whether a picture's details meet a picture term (0 = unknown, which no comparison matches).</summary>
    internal static bool Matches(ContentTerm term, in PictureInfo info)
    {
        var (w, h) = info.Shown;
        bool sized = w > 0 && h > 0;
        switch (term.Prop)
        {
            case PictureProp.Width: return Num(term, w, w > 0);
            case PictureProp.Height: return Num(term, h, h > 0);
            case PictureProp.BitDepth: return Num(term, info.BitDepth, info.BitDepth > 0);
            case PictureProp.Dpi:
            {
                int dpi = Dpi(info);
                return Num(term, dpi, dpi > 0);
            }
            case PictureProp.DpiX: return Num(term, info.DpiX, info.DpiX > 0);
            case PictureProp.DpiY: return Num(term, info.DpiY, info.DpiY > 0);
            case PictureProp.Rating: return Num(term, info.Rating, true); // 0 = no stars given
            case PictureProp.DateTaken: return Num(term, info.DateTakenUtc, info.DateTakenUtc != 0);
            case PictureProp.Dimensions:
                if (term.Dims == null) return sized;
                foreach (var d in term.Dims) if (d.Eval(w, h)) return true;
                return false;
            case PictureProp.Orientation:
            {
                if (!sized) return false;
                byte o = w > h ? (byte)1 : h > w ? (byte)2 : (byte)3;
                return term.Values == null || Array.IndexOf(term.Values, o) >= 0;
            }
            case PictureProp.ColorMode:
                if (info.ColorMode == ColorMode.Unknown) return false;
                if (term.Modes == null) return true;
                return Array.IndexOf(term.Modes, info.ColorMode) >= 0 != term.Except;
            case PictureProp.ExifColorSpace:
                return info.ExifColorSpace != 0 && (term.Values == null || Array.IndexOf(term.Values, info.ExifColorSpace) >= 0);
            case PictureProp.Gps:
                return info.HasGps;
            default:
                return false;
        }
    }

    /// <summary>dpi: the lower of the two axes (the one a printer is limited by); one known axis stands for both.</summary>
    internal static int Dpi(in PictureInfo info)
        => info.DpiX > 0 && info.DpiY > 0 ? Math.Min(info.DpiX, info.DpiY) : Math.Max(info.DpiX, info.DpiY);

    static bool Num(ContentTerm term, long value, bool known) => term.Conds == null ? known : ValueParsers.Eval(term.Conds, value, known);
}
