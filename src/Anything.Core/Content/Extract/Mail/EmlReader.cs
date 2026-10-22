using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Saved emails (MIME): subject (Subject, as Windows keeps it: subject: finds it), From (Sender: from: and author:), To and
/// Cc (Recipients: to:), the text body and attachment names (as
/// body lines; attachment contents are never decoded). Headers are unfolded and RFC 2047 encoded words decoded, adjacent
/// ones joined by their bytes so a character split between two words survives; RFC 2231 parameters (filename*=UTF-8''…,
/// continuations) are decoded. Charset names go through an alias table with a fallback (UTF-8 when valid, else ANSI).
/// The MIME tree is walked iteratively, at most 8 deep and 256 parts: text/plain is preferred within an alternative,
/// else text/html with its tags stripped. Base64 and quoted-printable are decoded leniently. At most 32 MB are read.
/// </summary>
internal sealed class EmlReader : IContentReader
{
    /// <summary>Most bytes read from one message.</summary>
    public const long MaxBytes = 32L << 20;
    const int MaxDepth = 8, MaxParts = 256, MaxHeaders = 1000, MaxHeaderChars = 64 << 10;
    const int MaxPartBytes = 8 << 20;

    public ContentKind Kind => ContentKind.Mail;
    /// <summary>2: the subject moved from Title to Subject (read again, ExtractorChanges).</summary>
    public byte Version => 2;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "eml";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
        => new Walk(source, sink, budget).Run();

    /// <summary>A multipart part being read.</summary>
    sealed class Frame(string boundary, bool alternative)
    {
        public readonly byte[] Boundary = Encoding.ASCII.GetBytes("--" + boundary);
        public readonly bool Alternative = alternative;
        public bool HasPlain;
        public string? HeldHtml;
    }

    /// <summary>The boundary line a part's content ended at.</summary>
    readonly record struct Hit(Frame? Frame, bool Closing, bool End);

    sealed class Walk(IByteSource source, ExtractSink sink, ExtractBudget budget)
    {
        readonly Lines _lines = new(source, budget);
        readonly List<Frame> _stack = [];
        readonly List<string> _attachments = [];
        bool _partial, _wroteText;
        int _parts;

        public DocStatus Run()
        {
            var headers = ReadHeaders();
            if (headers.Count == 0) throw new InvalidDataException("No headers.");
            Fields(headers);
            while (!sink.Full)
            {
                var hit = Part(headers);
                bool more = false;
                while (!sink.Full)
                {
                    if (hit.End) break;
                    int at = _stack.IndexOf(hit.Frame!);
                    while (_stack.Count - 1 > at) Close(Pop()); // inner parts that never closed
                    if (hit.Closing)
                    {
                        Close(Pop());
                        if (_stack.Count == 0) break;
                        hit = SkipToBoundary(); // the epilogue
                        continue;
                    }
                    more = true;
                    break;
                }
                if (!more) break;
                if (++_parts >= MaxParts) { _partial = true; break; } // later parts are left unread: what was read is Truncated
                headers = ReadHeaders();
            }
            while (_stack.Count > 0) Close(Pop());
            foreach (var name in _attachments)
            {
                sink.LineBreak();
                sink.Text(name);
            }
            if (_lines.Cut) _partial = true;
            return _partial ? DocStatus.Truncated : DocStatus.Ok;
        }

        Frame Pop()
        {
            var f = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return f;
        }

        void Close(Frame frame)
        {
            if (frame.Alternative && !frame.HasPlain && frame.HeldHtml is { } html) Emit(html, isHtml: true);
        }

        /// <summary>
        /// Subject, From, To and Cc, decoding only what the fields can keep (a field holds 4 KB): the first Subject and
        /// From, recipients until they fill a field (at most 100), and at most 32 K characters of each header value.
        /// </summary>
        void Fields(List<(string Name, string Value)> headers)
        {
            var recipients = new List<string>();
            int recipientChars = 0;
            bool subject = false, from = false;
            foreach (var (name, value) in headers)
            {
                budget.Check();
                if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                {
                    if (subject) continue;
                    subject = true;
                    sink.Field(DocField.Subject, DecodeHeader(Head(value), budget));
                }
                else if (name.Equals("From", StringComparison.OrdinalIgnoreCase))
                {
                    if (from) continue;
                    from = true;
                    sink.Field(DocField.Sender, DecodeHeader(Head(value), budget));
                }
                else if ((name.Equals("To", StringComparison.OrdinalIgnoreCase) || name.Equals("Cc", StringComparison.OrdinalIgnoreCase))
                    && recipients.Count < 100 && recipientChars <= ExtractSink.MaxFieldChars)
                {
                    string recipient = DecodeHeader(Head(value), budget);
                    recipients.Add(recipient);
                    recipientChars += recipient.Length + 2;
                }
            }
            if (recipients.Count > 0) sink.Field(DocField.Recipients, string.Join("; ", recipients));
        }

        /// <summary>Most of a header value decoded: eight times what a field keeps, ample for any real header.</summary>
        static string Head(string value) => value.Length > ExtractSink.MaxFieldChars * 8 ? value[..(ExtractSink.MaxFieldChars * 8)] : value;

        /// <summary>Reads one part (its headers already read) up to the boundary that ends it; nested messages are followed in a loop.</summary>
        Hit Part(List<(string Name, string Value)> headers)
        {
            for (int nested = 0; ; nested++)
            {
                budget.Check();
                var (type, parameters) = Parameters(Header(headers, "Content-Type") ?? "text/plain");
                type = type.ToLowerInvariant();
                var (disposition, dispositionParameters) = Parameters(Header(headers, "Content-Disposition") ?? "");
                if (type.StartsWith("multipart/", StringComparison.Ordinal) && parameters.TryGetValue("boundary", out var boundary)
                    && boundary.Length is > 0 and <= 200)
                {
                    if (_stack.Count >= MaxDepth) { _partial = true; return SkipToBoundary(); }
                    _stack.Add(new Frame(boundary, type == "multipart/alternative"));
                    return SkipToBoundary(); // the preamble
                }
                if (type == "message/rfc822" && nested < MaxDepth && !disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                {
                    headers = ReadHeaders();
                    if (headers.Count == 0) return SkipToBoundary();
                    continue;
                }
                string? fileName = Name(dispositionParameters, "filename") ?? Name(parameters, "name");
                // A named part is an attachment unless it says it is shown inline (a text part named and inline is body text).
                bool attachment = disposition.Equals("attachment", StringComparison.OrdinalIgnoreCase)
                    || (fileName != null && !disposition.Equals("inline", StringComparison.OrdinalIgnoreCase));
                if (fileName != null && _attachments.Count < MaxParts) _attachments.Add(fileName);
                if (attachment || type is not ("text/plain" or "text/html")) return SkipToBoundary();
                string encoding = (Header(headers, "Content-Transfer-Encoding") ?? "7bit").Trim().ToLowerInvariant();
                var body = new BodyDecoder(encoding);
                var hit = ReadBody(body);
                if (body.Cut) _partial = true;
                parameters.TryGetValue("charset", out var charset);
                // One part's fault is that part's: the rest of the message is still read.
                try { Deliver(Decode(body.Bytes, charset, type == "text/html"), type == "text/html"); }
                catch (Exception e) when (PictureParts.IsFormatFault(e)) { _partial = true; }
                return hit;
            }
        }

        static string? Name(Dictionary<string, string> parameters, string key)
            => parameters.TryGetValue(key, out var v) && v.Trim() is { Length: > 0 } name ? Path.GetFileName(name.Replace('/', '\\')) is { Length: > 0 } leaf ? leaf : name : null;

        /// <summary>A text part's content: plain text wins within an alternative, else the first HTML waits for the alternative's end.</summary>
        void Deliver(string text, bool isHtml)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Frame? alternative = null;
            for (int i = _stack.Count - 1; i >= 0; i--)
                if (_stack[i].Alternative) { alternative = _stack[i]; break; }
            if (alternative == null) { Emit(text, isHtml); return; }
            if (alternative.HasPlain) return;
            if (!isHtml)
            {
                alternative.HasPlain = true;
                alternative.HeldHtml = null;
                Emit(text, false);
            }
            else alternative.HeldHtml ??= text;
        }

        void Emit(string text, bool isHtml)
        {
            if (_wroteText) sink.LineBreak();
            _wroteText = true;
            if (isHtml) HtmlText.Extract(text, sink, budget);
            else sink.Text(text);
        }

        /// <summary>Headers up to the empty line, unfolded; values as Latin-1 strings of the raw bytes (decoded later).</summary>
        List<(string Name, string Value)> ReadHeaders()
        {
            var list = new List<(string, string)>();
            var current = new StringBuilder();
            string? name = null;
            while (_lines.Next(out var line))
            {
                if (line.IsEmpty) break;
                if (line[0] is (byte)' ' or (byte)'\t')
                {
                    if (name != null && current.Length < MaxHeaderChars) current.Append(Encoding.Latin1.GetString(line));
                    continue;
                }
                if (name != null && list.Count < MaxHeaders) list.Add((name, current.ToString()));
                name = null;
                current.Clear();
                int colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    // Not a header: the headers ended without an empty line (or this isn't a message part at all).
                    if (list.Count == 0) continue;
                    break;
                }
                name = Encoding.ASCII.GetString(line[..colon]).Trim();
                var value = line[(colon + 1)..];
                current.Append(Encoding.Latin1.GetString(value[..Math.Min(value.Length, MaxHeaderChars)]));
            }
            if (name != null && list.Count < MaxHeaders) list.Add((name, current.ToString()));
            return list;
        }

        static string? Header(List<(string Name, string Value)> headers, string name)
        {
            foreach (var (n, v) in headers) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        /// <summary>Skips lines until a boundary of an open multipart, or the end.</summary>
        Hit SkipToBoundary() => ReadBody(null);

        Hit ReadBody(BodyDecoder? into)
        {
            while (_lines.Next(out var line))
            {
                if (_stack.Count > 0 && line.Length >= 2 && line[0] == (byte)'-' && line[1] == (byte)'-')
                    for (int i = _stack.Count - 1; i >= 0; i--)
                    {
                        var b = _stack[i].Boundary;
                        if (!line.StartsWith(b)) continue;
                        var rest = line[b.Length..];
                        bool closing = rest.StartsWith("--"u8);
                        if (closing) rest = rest[2..];
                        if (rest.Trim((byte)' ').Trim((byte)'\t').IsEmpty) return new Hit(_stack[i], closing, false);
                    }
                into?.Line(line, _lines.Continued);
            }
            return new Hit(null, false, true);
        }

        string Decode(byte[] bytes, string? charset, bool html)
        {
            var encoding = TextDecoding.ForCharset(charset) ?? (html ? TextDecoding.HtmlCharset(bytes) : null);
            if (encoding == null) encoding = Utf8.IsValid(bytes) ? new UTF8Encoding(false) : TextDecoding.Ansi();
            return TextDecoding.Decode(bytes, encoding);
        }
    }

    /// <summary>A part's content decoded line by line: base64 (lenient: other characters ignored), quoted-printable or as is.</summary>
    sealed class BodyDecoder(string transferEncoding)
    {
        readonly MemoryStream _bytes = new();
        readonly bool _base64 = transferEncoding == "base64", _quoted = transferEncoding == "quoted-printable";
        readonly char[] _quad = new char[4];
        int _quadLength;

        public bool Cut { get; private set; }
        public byte[] Bytes => _bytes.ToArray();

        public void Line(ReadOnlySpan<byte> line, bool continued)
        {
            if (Cut) return;
            if (_bytes.Length > MaxPartBytes) { Cut = true; return; }
            if (_base64) { Base64(line); return; }
            if (_quoted) { QuotedPrintable(line, continued); return; }
            _bytes.Write(line);
            if (!continued) _bytes.Write("\r\n"u8);
        }

        void Base64(ReadOnlySpan<byte> line)
        {
            foreach (byte b in line)
            {
                if (b == '=') { Flush(); continue; }
                if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/')) continue;
                _quad[_quadLength++] = (char)b;
                if (_quadLength == 4) Flush();
            }
        }

        void Flush()
        {
            if (_quadLength < 2) { _quadLength = 0; return; }
            Span<byte> out3 = stackalloc byte[3];
            int v = 0;
            for (int i = 0; i < 4; i++) v = (v << 6) | (i < _quadLength ? Sextet(_quad[i]) : 0);
            out3[0] = (byte)(v >> 16); out3[1] = (byte)(v >> 8); out3[2] = (byte)v;
            _bytes.Write(out3[..(_quadLength - 1)]);
            _quadLength = 0;
        }

        static int Sextet(char c) => c switch
        {
            >= 'A' and <= 'Z' => c - 'A',
            >= 'a' and <= 'z' => c - 'a' + 26,
            >= '0' and <= '9' => c - '0' + 52,
            '+' => 62,
            _ => 63,
        };

        void QuotedPrintable(ReadOnlySpan<byte> line, bool continued)
        {
            // Trailing whitespace is padding (RFC 2045); a final '=' is a soft line break.
            int end = line.Length;
            while (end > 0 && line[end - 1] is (byte)' ' or (byte)'\t') end--;
            bool soft = end > 0 && line[end - 1] == '=';
            if (soft) end--;
            for (int i = 0; i < end; i++)
            {
                byte b = line[i];
                if (b == '=' && i + 2 < end && Hex(line[i + 1]) is int hi && Hex(line[i + 2]) is int lo)
                {
                    _bytes.WriteByte((byte)(hi * 16 + lo));
                    i += 2;
                    continue;
                }
                _bytes.WriteByte(b);
            }
            if (!soft && !continued) _bytes.Write("\r\n"u8);
        }

        static int? Hex(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            _ => null,
        };
    }

    /// <summary>The message's lines (CR LF, LF or CR ends), at most 64 KB each (longer ones come in pieces), within the read limit.</summary>
    sealed class Lines(IByteSource source, ExtractBudget budget)
    {
        const int MaxLine = 64 << 10;
        readonly byte[] _buffer = new byte[128 << 10];
        readonly byte[] _line = new byte[MaxLine];
        readonly long _limit = Math.Min(source.Length, MaxBytes);
        int _pos, _len;
        long _offset, _nextCheck;

        /// <summary>The last line was cut at 64 KB; its rest is the next line.</summary>
        public bool Continued { get; private set; }

        /// <summary>The message is longer than the read limit.</summary>
        public bool Cut => source.Length > _limit && _offset + _pos >= _limit;

        public bool Next(out ReadOnlySpan<byte> line)
        {
            int n = 0;
            Continued = false;
            while (true)
            {
                if (_pos >= _len && !Fill())
                {
                    line = _line.AsSpan(0, n);
                    return n > 0;
                }
                byte b = _buffer[_pos++];
                if (b == '\n') break;
                if (b == '\r')
                {
                    if ((_pos < _len || Fill()) && _buffer[_pos] == '\n') _pos++;
                    break;
                }
                _line[n++] = b;
                if (n == MaxLine) { Continued = true; break; }
            }
            line = _line.AsSpan(0, n);
            return true;
        }

        bool Fill()
        {
            _offset += _len;
            _pos = _len = 0;
            if (_offset >= _limit) return false;
            if (_offset >= _nextCheck) { budget.Check(); _nextCheck = _offset + (1 << 20); }
            int want = (int)Math.Min(_buffer.Length, _limit - _offset);
            _len = source.ReadFully(_offset, _buffer.AsSpan(0, want));
            return _len > 0;
        }
    }

    // ------------------------------------------------------------------ Header decoding

    /// <summary>
    /// A header value as text: raw 8-bit bytes (in Latin-1 chars) as UTF-8 when valid, else the ANSI code page; then RFC
    /// 2047 encoded words decoded, adjacent ones joined by their bytes; folding whitespace collapsed. The budget, when
    /// given, is checked as the value is decoded.
    /// </summary>
    internal static string DecodeHeader(string value, ExtractBudget? budget = null)
    {
        bool high = false;
        foreach (char c in value) if (c > 0x7F) { high = true; break; }
        if (high)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            value = Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : TextDecoding.Ansi().GetString(bytes);
        }
        return DecodeWords(value, budget).Replace('\t', ' ').Trim();
    }

    /// <summary>
    /// Decodes RFC 2047 encoded words (=?charset?B|Q?text?=, at most 1,024 characters each); whitespace between adjacent
    /// words is dropped. Linear in the value's length: a word's end is looked for only within its own length limit, and
    /// each charset name is looked up once per value (an unknown one costs an exception each time). The budget, when
    /// given, is checked every 4 KB.
    /// </summary>
    internal static string DecodeWords(string s, ExtractBudget? budget = null)
    {
        if (!s.Contains("=?", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length);
        var pending = new List<byte>();
        var space = new StringBuilder(); // whitespace after an encoded word: dropped when another word follows
        var charsets = new Dictionary<string, Encoding?>(StringComparer.OrdinalIgnoreCase);
        string? pendingCharset = null;
        bool afterWord = false;
        void FlushPending()
        {
            if (pending.Count == 0) return;
            var raw = pending.ToArray();
            string name = pendingCharset ?? "";
            if (!charsets.TryGetValue(name, out var known)) charsets[name] = known = charsets.Count < 64 ? TextDecoding.ForCharset(name) : null;
            var encoding = known ?? (Utf8.IsValid(raw) ? Encoding.UTF8 : TextDecoding.Ansi());
            sb.Append(encoding.GetString(raw));
            pending.Clear();
        }
        int i = 0, nextCheck = 0;
        while (i < s.Length)
        {
            if (budget != null && i >= nextCheck) { budget.Check(); nextCheck = i + 4096; }
            if (TryWord(s, i, out int end, out string charset, out byte[] bytes))
            {
                space.Clear();
                // Adjacent words of one charset are joined by their bytes: a character may be split between them.
                if (pending.Count > 0 && !string.Equals(charset, pendingCharset, StringComparison.OrdinalIgnoreCase)) FlushPending();
                pendingCharset = charset;
                pending.AddRange(bytes);
                i = end;
                afterWord = true;
                continue;
            }
            char c = s[i++];
            if (afterWord && c is ' ' or '\t' or '\r' or '\n') { space.Append(c); continue; }
            FlushPending();
            sb.Append(space);
            space.Clear();
            afterWord = false;
            sb.Append(c);
        }
        FlushPending();
        sb.Append(space);
        return sb.ToString();
    }

    /// <summary>Longest encoded word read (RFC 2047 allows 75 characters; mailers write longer ones).</summary>
    const int MaxEncodedWord = 1024;

    static bool TryWord(string s, int at, out int end, out string charset, out byte[] bytes)
    {
        end = 0; charset = ""; bytes = [];
        if (at + 8 > s.Length || s[at] != '=' || s[at + 1] != '?') return false;
        // Both searches stay within the word's own length limit, so a value full of unclosed "=?" stays linear.
        int limit = Math.Min(s.Length, at + MaxEncodedWord + 2);
        int q1 = s.IndexOf('?', at + 2, limit - (at + 2));
        if (q1 < 0 || q1 + 3 >= limit || s[q1 + 2] != '?') return false;
        char mode = char.ToUpperInvariant(s[q1 + 1]);
        if (mode is not ('B' or 'Q')) return false;
        int close = s.IndexOf("?=", q1 + 3, limit - (q1 + 3), StringComparison.Ordinal);
        if (close < 0 || close - at > MaxEncodedWord) return false;
        charset = s[(at + 2)..q1];
        int star = charset.IndexOf('*'); // RFC 2231 language: charset*lang
        if (star >= 0) charset = charset[..star];
        var text = s.AsSpan(q1 + 3, close - q1 - 3);
        bytes = mode == 'B' ? LenientBase64(text) : QEncoded(text);
        end = close + 2;
        return true;
    }

    /// <summary>Base64 as the body decoder reads it (other characters ignored, '=' ends a group, a final partial group kept), without its buffers.</summary>
    static byte[] LenientBase64(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return [];
        var output = new byte[text.Length / 4 * 3 + 3];
        int n = 0, count = 0, value = 0;
        void Flush()
        {
            if (count >= 2)
            {
                value <<= 6 * (4 - count);
                output[n++] = (byte)(value >> 16);
                if (count > 2) output[n++] = (byte)(value >> 8);
                if (count > 3) output[n++] = (byte)value;
            }
            count = value = 0;
        }
        foreach (char c in text)
        {
            if (c == '=') { Flush(); continue; }
            int sextet = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= 'a' and <= 'z' => c - 'a' + 26,
                >= '0' and <= '9' => c - '0' + 52,
                '+' => 62,
                '/' => 63,
                _ => -1,
            };
            if (sextet < 0) continue;
            value = value << 6 | sextet;
            if (++count == 4) Flush();
        }
        Flush();
        return output.AsSpan(0, n).ToArray();
    }

    static byte[] QEncoded(ReadOnlySpan<char> text)
    {
        var list = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '_') { list.Add((byte)' '); continue; }
            if (c == '=' && i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                list.Add(Convert.ToByte(text.Slice(i + 1, 2).ToString(), 16));
                i += 2;
                continue;
            }
            list.Add(c < 0x100 ? (byte)c : (byte)'?');
        }
        return [.. list];
    }

    /// <summary>
    /// A structured header ("text/plain; charset=utf-8", "attachment; filename*=UTF-8''%E2%82%AC.pdf"): its value and
    /// parameters (names lower case), RFC 2231 continuations and charsets decoded.
    /// </summary>
    internal static (string Value, Dictionary<string, string> Parameters) Parameters(string header)
    {
        var parts = SplitOutsideQuotes(header);
        string value = parts.Count > 0 ? parts[0].Trim() : "";
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extended = new Dictionary<string, SortedDictionary<int, (string Text, bool Encoded)>>(StringComparer.OrdinalIgnoreCase);
        for (int p = 1; p < parts.Count && p < 64; p++)
        {
            string part = parts[p];
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string name = part[..eq].Trim().ToLowerInvariant();
            string raw = Unquote(part[(eq + 1)..].Trim());
            bool encoded = name.EndsWith('*');
            if (encoded) name = name[..^1];
            int star = name.IndexOf('*');
            if (star > 0 && int.TryParse(name.AsSpan(star + 1), out int index) && index is >= 0 and < 1000)
            {
                string baseName = name[..star];
                if (!extended.TryGetValue(baseName, out var segments)) extended[baseName] = segments = [];
                segments.TryAdd(index, (raw, encoded));
            }
            else if (encoded)
            {
                if (!extended.TryGetValue(name, out var segments)) extended[name] = segments = [];
                segments.TryAdd(0, (raw, true));
            }
            // Values are decoded (raw 8-bit, RFC 2047 words, which mailers put in file names), except the boundary,
            // which is matched byte for byte.
            else plain.TryAdd(name, name == "boundary" ? raw : DecodeHeader(raw));
        }
        foreach (var (name, segments) in extended)
        {
            var bytes = new List<byte>();
            string? charset = null;
            bool first = true;
            foreach (var (_, (text, isEncoded)) in segments)
            {
                string t = text;
                if (isEncoded && first)
                {
                    // charset'language'value
                    int a = t.IndexOf('\''), b = a >= 0 ? t.IndexOf('\'', a + 1) : -1;
                    if (b > a && a >= 0) { charset = t[..a]; t = t[(b + 1)..]; }
                }
                first = false;
                if (isEncoded) bytes.AddRange(PercentDecode(t));
                else bytes.AddRange(Encoding.Latin1.GetBytes(t));
            }
            var arr = bytes.ToArray();
            var encoding = TextDecoding.ForCharset(charset) ?? (Utf8.IsValid(arr) ? Encoding.UTF8 : TextDecoding.Ansi());
            plain[name] = encoding.GetString(arr); // the RFC 2231 form wins over a plain one
        }
        return (value, plain);
    }

    static List<string> SplitOutsideQuotes(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') quoted = !quoted;
            else if (c == '\\' && quoted && i + 1 < s.Length) { sb.Append(c).Append(s[++i]); continue; }
            if (c == ';' && !quoted) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"') return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            if (c == '"') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static IEnumerable<byte> PercentDecode(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
            {
                yield return Convert.ToByte(s.Substring(i + 1, 2), 16);
                i += 2;
                continue;
            }
            yield return s[i] < 0x100 ? (byte)s[i] : (byte)'?';
        }
    }
}
