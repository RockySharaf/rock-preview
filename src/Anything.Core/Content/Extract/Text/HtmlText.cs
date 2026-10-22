using System.Globalization;
using System.Net;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// HTML to text: a tag-stripping state machine (no recursion, no DOM), entities decoded, script and style skipped,
/// &lt;title&gt; kept as Title and the description, keywords and author meta tags as fields. Block elements end lines,
/// table cells are separated by tabs, and whitespace runs collapse to one space outside &lt;pre&gt;.
/// </summary>
internal static class HtmlText
{
    static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "ul", "ol", "dl", "dt", "dd", "tr", "table", "thead", "tbody", "tfoot", "caption", "h1", "h2", "h3", "h4",
        "h5", "h6", "blockquote", "pre", "hr", "section", "article", "header", "footer", "nav", "aside", "main", "address", "figure",
        "figcaption", "form", "fieldset", "legend", "details", "summary", "option", "body", "html", "center", "title",
    };

    /// <summary>Elements whose content is never text.</summary>
    static readonly string[] Raw = ["script", "style", "template", "textarea"];

    /// <summary>Writes the text of an HTML document into the sink.</summary>
    public static void Extract(string html, ExtractSink sink, ExtractBudget budget)
    {
        var w = new Writer(sink);
        int pre = 0, i = 0, n = html.Length, checkAt = 0;
        while (i < n && !sink.Full)
        {
            if (i >= checkAt) { budget.Check(); checkAt = i + (64 << 10); }
            char c = html[i];
            if (c == '<' && i + 1 < n)
            {
                char next = html[i + 1];
                if (next == '!')
                {
                    if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0) { i = Skip(html, i + 4, "-->"); continue; }
                    i = Skip(html, i + 2, ">");
                    continue;
                }
                if (next == '?') { i = Skip(html, i + 2, ">"); continue; }
                bool closing = next == '/';
                int nameStart = closing ? i + 2 : i + 1;
                if (nameStart < n && char.IsAsciiLetter(html[nameStart]))
                {
                    int nameEnd = nameStart;
                    while (nameEnd < n && nameEnd - nameStart < 32 && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':')) nameEnd++;
                    string name = html[nameStart..nameEnd];
                    int tagEnd = TagEnd(html, nameEnd);
                    var attributes = html.AsSpan(nameEnd, Math.Max(0, tagEnd - nameEnd - 1));
                    i = tagEnd;
                    if (!closing)
                    {
                        if (Array.FindIndex(Raw, r => r.Equals(name, StringComparison.OrdinalIgnoreCase)) >= 0) { i = SkipClose(html, i, name); continue; }
                        if (name.Equals("title", StringComparison.OrdinalIgnoreCase))
                        {
                            int close = FindClose(html, i, "title");
                            sink.Field(DocField.Title, Collapse(Decode(html.AsSpan(i, Math.Min(close - i, 2 * ExtractSink.MaxFieldChars)))));
                            i = SkipClose(html, i, "title");
                            continue;
                        }
                        if (name.Equals("meta", StringComparison.OrdinalIgnoreCase)) { Meta(attributes, sink); continue; }
                        if (name.Equals("pre", StringComparison.OrdinalIgnoreCase)) pre++;
                    }
                    else if (name.Equals("pre", StringComparison.OrdinalIgnoreCase) && pre > 0) pre--;
                    if (name.Equals("td", StringComparison.OrdinalIgnoreCase) || name.Equals("th", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!closing) w.Tab();
                    }
                    else if (Blocks.Contains(name)) w.Line();
                    continue;
                }
            }
            if (c == '&')
            {
                int end = html.IndexOf(';', i + 1, Math.Min(32, n - i - 1));
                if (end > i + 1)
                {
                    var entity = html.AsSpan(i, end - i + 1);
                    if (TryEntity(entity, out var decoded)) { w.Text(decoded, pre > 0); i = end + 1; continue; }
                }
            }
            int run = i;
            while (run < n && html[run] is not ('<' or '&')) run++;
            if (run == i) run++;
            w.Text(html.AsSpan(i, run - i), pre > 0);
            i = run;
        }
        w.Flush();
    }

    /// <summary>The index after the tag's closing '&gt;', quotes respected; the end of the text when it never closes.</summary>
    static int TagEnd(string s, int from)
    {
        char quote = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '>') return i + 1;
        }
        return s.Length;
    }

    static int Skip(string s, int from, string until)
    {
        int at = from <= s.Length ? s.IndexOf(until, from, StringComparison.Ordinal) : -1;
        return at < 0 ? s.Length : at + until.Length;
    }

    /// <summary>Where "&lt;/name" starts (case ignored), or the end of the text.</summary>
    static int FindClose(string s, int from, string name)
    {
        for (int at = s.IndexOf("</", from, StringComparison.Ordinal); at >= 0; at = s.IndexOf("</", at + 2, StringComparison.Ordinal))
            if (string.Compare(s, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0) return at;
        return s.Length;
    }

    static int SkipClose(string s, int from, string name)
    {
        int at = FindClose(s, from, name);
        return at >= s.Length ? s.Length : TagEnd(s, at + 2 + name.Length);
    }

    /// <summary>Decodes one entity ("&amp;amp;", "&amp;#233;", "&amp;#x1F600;"); invalid code points become U+FFFD, unknown names stay text.</summary>
    internal static bool TryEntity(ReadOnlySpan<char> entity, out string value)
    {
        value = "";
        if (entity.Length < 3 || entity[0] != '&' || entity[^1] != ';') return false;
        var body = entity[1..^1];
        if (body[0] == '#')
        {
            bool hex = body.Length > 1 && body[1] is 'x' or 'X';
            var digits = body[(hex ? 2 : 1)..];
            if (digits.IsEmpty || digits.Length > 8
                || !int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                return false;
            value = code switch
            {
                0 => "",
                >= 0xD800 and <= 0xDFFF or > 0x10FFFF or < 0 => "�",
                // Windows-1252 bytes written as numbers (&#150; for a dash), as browsers read them.
                >= 0x80 and <= 0x9F => Windows1252(code),
                _ => char.ConvertFromUtf32(code),
            };
            return true;
        }
        foreach (char c in body) if (!char.IsAsciiLetterOrDigit(c)) return false;
        string decoded = WebUtility.HtmlDecode(entity.ToString());
        if (decoded.Length == entity.Length && decoded[0] == '&') return false;
        value = decoded;
        return true;
    }

    static string Windows1252(int code)
    {
        try
        {
            string s = Encoding.GetEncoding(1252).GetString([(byte)code]);
            return s.Length == 1 && s[0] != '?' ? s : "�";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return "�"; }
    }

    /// <summary>Text with its entities decoded; linear in its length (an entity's ';' is looked for in the next 32 characters only).</summary>
    internal static string Decode(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('&') < 0) return text.ToString();
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                int end = text.Slice(i + 1, Math.Min(32, text.Length - i - 1)).IndexOf(';');
                if (end > 0 && TryEntity(text.Slice(i, end + 2), out var v)) { sb.Append(v); i += end + 1; continue; }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>&lt;meta name="description|keywords|author" content="…"&gt; → Comment, Keywords, Author.</summary>
    static void Meta(ReadOnlySpan<char> attributes, ExtractSink sink)
    {
        string? name = null, content = null;
        foreach (var (key, value) in Attributes(attributes))
        {
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) || key.Equals("property", StringComparison.OrdinalIgnoreCase)) name ??= value;
            else if (key.Equals("content", StringComparison.OrdinalIgnoreCase)) content ??= value;
        }
        if (name == null || string.IsNullOrWhiteSpace(content)) return;
        DocField? field = name.Trim().ToLowerInvariant() switch
        {
            "description" or "og:description" => DocField.Comment,
            "author" => DocField.Author,
            "keywords" => DocField.Keywords,
            _ => null,
        };
        if (field is not { } f) return;
        content = Collapse(Decode(content));
        if (f == DocField.Keywords) sink.Keywords(content);
        else sink.Field(f, content);
    }

    /// <summary>
    /// Most characters of an attribute value kept: enough for the keywords' 64K after entities and white space, so a
    /// multi-megabyte value is never copied whole.
    /// </summary>
    const int MaxAttributeChars = ExtractSink.MaxKeywordListChars;

    /// <summary>The attributes of a tag (name=value, quoted or not); at most 64, values cut to <see cref="MaxAttributeChars"/>.</summary>
    static List<(string Key, string Value)> Attributes(ReadOnlySpan<char> s)
    {
        var list = new List<(string, string)>();
        int i = 0;
        while (i < s.Length && list.Count < 64)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '/')) i++;
            int keyStart = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('=' or '/' or '>')) i++;
            if (i == keyStart) { i++; continue; }
            string key = s[keyStart..i].ToString();
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            string value = "";
            if (i < s.Length && s[i] == '=')
            {
                i++;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i < s.Length && s[i] is '"' or '\'')
                {
                    char q = s[i++];
                    int end = s[i..].IndexOf(q);
                    end = end < 0 ? s.Length : i + end;
                    value = s[i..Math.Min(end, i + MaxAttributeChars)].ToString();
                    i = Math.Min(s.Length, end + 1);
                }
                else
                {
                    int start = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                    value = s[start..Math.Min(i, start + MaxAttributeChars)].ToString();
                }
            }
            list.Add((key, value));
        }
        return list;
    }

    /// <summary>Feeds the sink: collapses whitespace outside &lt;pre&gt;, and gives at most one line end between blocks.</summary>
    sealed class Writer(ExtractSink sink)
    {
        bool _pendingSpace, _atLineStart = true;

        public void Text(ReadOnlySpan<char> text, bool preformatted)
        {
            if (preformatted)
            {
                if (_pendingSpace && !_atLineStart) sink.Text(" ");
                _pendingSpace = false;
                sink.Text(text);
                if (!text.IsEmpty) _atLineStart = text[^1] is '\n' or '\r';
                return;
            }
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { _pendingSpace = true; i++; continue; }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                if (_pendingSpace && !_atLineStart) sink.Text(" ");
                _pendingSpace = false;
                sink.Text(text[start..i]);
                _atLineStart = false;
            }
        }

        public void Tab()
        {
            if (_atLineStart) return;
            sink.Text("\t");
            _pendingSpace = false;
        }

        public void Line()
        {
            _pendingSpace = false;
            if (_atLineStart) return;
            sink.LineBreak();
            _atLineStart = true;
        }

        public void Flush() { }
    }
}
