using System.Text;
using Anything.Core.Content.Store;

namespace Anything.Core.Content.Search;

/// <summary>The matching line around the first hit (≤ maxChars, the hit near its start) with its page/line/sheet/field prefix.</summary>
internal static class SnippetFormatter
{
    /// <summary>Context kept before the hit: the cell shows the start of the snippet, so the hit must sit near it.</summary>
    internal const int LeadChars = 40;

    enum Places : byte { None, Pages, Slides, Sheets, Lines }

    static readonly HashSet<string> Presentations = new(["pptx", "pptm", "ppsx", "ppsm", "potx", "potm", "odp", "otp"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Workbooks = new(["xlsx", "xlsm", "xltx", "xltm", "ods", "ots"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> PagedFiles = new(["pdf", "ai"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> LinedFiles = new(["txt", "text", "md", "markdown"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The snippet of a stored document, or null when nothing in it matches. The caller (IContentIndex.GetSnippet) passes
    /// the file's extension (no dot, any case) from its path and the row's page count, which ends up in ContentSnippet.Pages.
    /// The extension picks the prefix within a kind: "Slide 3 ·" for presentations (pptx family, odp, otp), "Sheet "2024" ·"
    /// with Sheet set for workbooks (xlsx family, ods, ots), none for other Office documents, "p. 4 ·" for PDF and AI; Line
    /// (and "Line 120 ·") only for txt, text, md, markdown and a Text-kind extension ContentTypes doesn't know (a user
    /// extra), never for rtf, htm, html, csv or tsv. The prefix itself is <see cref="ContentPresentation.SnippetPrefix"/>, never part of Text.
    /// The body is searched first, then the fields in order, then picture details matched by numbers.
    /// </summary>
    public static ContentSnippet? Format(StoredDoc doc, ContentKind kind, ReadOnlySpan<char> extension, int pages, PictureInfo? picture,
        ContentHighlighter highlighter, int maxChars = 300)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(highlighter);
        maxChars = Math.Max(maxChars, 20);
        string ext = extension.TrimStart('.').ToString();
        var places = PlacesOf(kind, ext);

        if (highlighter.HasBodyWords && doc.Body.Length > 0)
        {
            int at = highlighter.FirstIn(doc.Body, 0, null, out int length);
            if (at >= 0) return BodySnippet(doc.Body, at, length, kind, places, pages, highlighter, maxChars);
        }
        foreach (var (field, value) in doc.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            int at = highlighter.FirstIn(value, 0, field, out int length);
            if (at < 0) continue;
            string text = Window(value, 0, value.Length, at, length, maxChars);
            return new ContentSnippet(text, highlighter.FindIn(text, field), kind, 0, 0, null, field) { Pages = pages };
        }
        if (picture is { } info && highlighter.HasPictureConditions)
        {
            string line = highlighter.PictureLine(info);
            if (line.Length > 0) return new ContentSnippet(line, [], kind, 0, 0, null, null) { Pages = pages };
        }
        return null;
    }

    static Places PlacesOf(ContentKind kind, string ext)
    {
        if (PagedFiles.Contains(ext)) return Places.Pages;
        if (kind == ContentKind.Office) return Presentations.Contains(ext) ? Places.Slides : Workbooks.Contains(ext) ? Places.Sheets : Places.None;
        if (kind == ContentKind.Text && (LinedFiles.Contains(ext) || ContentTypes.KindOfExtension(ext) == ContentKind.None)) return Places.Lines;
        return Places.None;
    }

    static ContentSnippet BodySnippet(string body, int at, int length, ContentKind kind, Places places, int pages,
        ContentHighlighter highlighter, int maxChars)
    {
        var span = body.AsSpan();
        int pageStart = at == 0 ? 0 : span[..at].LastIndexOf('\f') + 1;
        int pageEnd = span[at..].IndexOf('\f') is var next and >= 0 ? at + next : body.Length;
        int page = 0, line = 0;
        string? sheet = null;
        int from = pageStart;
        switch (places)
        {
            case Places.Pages or Places.Slides:
                page = span[..at].Count('\f') + 1;
                break;
            case Places.Sheets:
            {
                page = span[..at].Count('\f') + 1;
                int nameEnd = span[pageStart..pageEnd].IndexOf('\n') is var nl and >= 0 ? pageStart + nl : pageEnd;
                sheet = body[pageStart..nameEnd];
                if (at > nameEnd) from = nameEnd + 1; // the sheet's name is in the prefix already
                break;
            }
            case Places.Lines:
                line = span[..at].Count('\n') + 1;
                break;
        }
        string text = Window(body, from, pageEnd, at, length, maxChars);
        return new ContentSnippet(text, highlighter.FindIn(text, null), kind, page, line, sheet, null) { Pages = pages };
    }

    /// <summary>
    /// Up to maxChars of [regionStart, regionEnd) holding the hit, starting a little before it, whitespace runs collapsed
    /// to one space, "…" where it was cut. A cut never falls inside a word of a script written with spaces (it moves to
    /// the word's edge, so a CSV row or a URL keeps its other cells), nor inside a character (a surrogate pair, a letter
    /// and its marks); in scripts written without spaces (Japanese, Chinese, Thai) any character boundary is a cut.
    /// </summary>
    internal static string Window(string s, int regionStart, int regionEnd, int at, int length, int maxChars)
    {
        int room = Math.Max(0, maxChars - length);
        int start = Math.Max(regionStart, at - Math.Min(LeadChars, room / 2));
        int end = Math.Min(regionEnd, Math.Max(at + length, start + maxChars));
        if (end - start < maxChars) start = Math.Max(regionStart, Math.Min(start, end - maxChars)); // the region ends soon: show more before
        start = Math.Min(start, at);
        if (start > regionStart)
            while (start < at && InsideWord(s, start)) start++;
        if (end < regionEnd)
            while (end > at + length && InsideWord(s, end)) end--;
        var sb = new StringBuilder(end - start + 2);
        if (start > regionStart) sb.Append('…');
        bool space = false;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == '\f')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        if (end < regionEnd) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>
    /// Whether cutting before s[i] would split a character (the second half of a surrogate pair, or a mark or invisible
    /// joiner continuing the character before) or a word: letters or digits of a script written with spaces on both sides.
    /// </summary>
    internal static bool InsideWord(string s, int i)
    {
        if (i <= 0 || i >= s.Length) return false;
        if (char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1])) return true;
        byte at = ContentTokenizer.ClassAt(s, i, out _);
        if (at is ContentTokenizer.Mark or ContentTokenizer.Dropped or ContentTokenizer.Ignored) return true;
        return at == ContentTokenizer.Plain && ClassBefore(s, i) == ContentTokenizer.Plain;
    }

    /// <summary>Class of the character that ends before i, its marks skipped (a few steps at most).</summary>
    static byte ClassBefore(string s, int i)
    {
        for (int steps = 0; i > 0 && steps < 32; steps++)
        {
            int k = i - 1;
            if (char.IsLowSurrogate(s[k]) && k > 0 && char.IsHighSurrogate(s[k - 1])) k--;
            byte c = ContentTokenizer.ClassAt(s, k, out _);
            if (c is not (ContentTokenizer.Mark or ContentTokenizer.Dropped or ContentTokenizer.Ignored)) return c;
            i = k;
        }
        return ContentTokenizer.Other;
    }
}
