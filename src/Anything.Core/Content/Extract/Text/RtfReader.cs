using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// RTF: a streaming tokenizer with an explicit group stack (at most 256 deep; deeper braces are counted, not stacked),
/// at most 32 MB read. Font code pages come from \fonttbl (\fcharsetN, \cpgN), so \'hh runs are decoded in the current
/// font's code page, double-byte ones (Shift-JIS, GBK, Big5, Korean) as pairs; \uN with \ucN fallbacks; \upr keeps its
/// Unicode \ud branch. Destinations that hold no text (font, colour and style tables, pictures, objects, field
/// instructions, \* destinations) are skipped, \binN skips N bytes, and \info gives the title, subject, author, keywords
/// and comment fields. \par and \line end lines, \page ends a page.
/// </summary>
internal sealed class RtfReader : IContentReader
{
    /// <summary>Most bytes read from one file.</summary>
    public const long MaxBytes = 32L << 20;

    public ContentKind Kind => ContentKind.Text;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "rtf";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        var parser = new Parser(source, sink, budget);
        if (!parser.Start()) throw new InvalidDataException("Not RTF.");
        parser.Run();
        return parser.Cut ? DocStatus.Truncated : DocStatus.Ok;
    }

    /// <summary>Where a group's text goes.</summary>
    enum Dest : byte { Text, Skip, FontTable, Info, Field, Upr }

    struct State
    {
        public Dest Dest;
        public DocField Field;
        public bool OwnsField, Starred;
        public Dest UprOuter;
        public DocField UprOuterField;
        public int Uc, Font;
    }

    /// <summary>Decodes RTF text into the sink.</summary>
    internal sealed class Parser(IByteSource source, ExtractSink sink, ExtractBudget budget)
    {
        const int MaxDepth = 256, MaxWord = 32;
        readonly byte[] _buffer = new byte[64 << 10];
        int _pos, _len;
        long _offset, _nextCheck;
        readonly long _limit = Math.Min(source.Length, MaxBytes);

        readonly State[] _stack = new State[MaxDepth];
        int _depth, _overflow;
        State _s;

        readonly Dictionary<int, int> _fontCodePages = [];
        int _ansiCodePage = 1252, _defaultFont = -1, _fontEntry = -1;
        readonly Dictionary<int, Encoding> _encodings = [];

        readonly List<byte> _hex = [];
        int _hexCodePage;
        int _ucSkip;
        readonly StringBuilder _text = new();
        readonly StringBuilder _field = new();
        readonly char[] _word = new char[MaxWord];

        /// <summary>Stopped before the end: the byte limit (32 MB) was reached.</summary>
        public bool Cut { get; private set; }

        public bool Start()
        {
            // "{\rtf", allowing leading whitespace (and a UTF-8 BOM some writers put first).
            int c;
            while ((c = Peek()) is ' ' or '\r' or '\n' or '\t' or 0xEF or 0xBB or 0xBF) Next();
            // "{\rtf" must come first; only the brace is consumed, so "\rtf1" is read as the (ignored) control word it is.
            if (Peek() < 0 || _len - _pos < 5 || !_buffer.AsSpan(_pos, 5).SequenceEqual("{\\rtf"u8)) return false;
            Next();
            _s = new State { Dest = Dest.Text, Uc = 1, Font = -1 };
            _depth = 1;
            _stack[0] = _s;
            return true;
        }

        public void Run()
        {
            while (!sink.Full)
            {
                int c = Next();
                if (c < 0)
                {
                    Cut = _limit < source.Length;
                    break;
                }
                if (_offset + _pos >= _nextCheck) { budget.Check(); _nextCheck = _offset + _pos + (64 << 10); }
                switch (c)
                {
                    case '{':
                        FlushHex();
                        Push();
                        break;
                    case '}':
                        FlushHex();
                        if (!Pop()) { FlushText(); return; }
                        break;
                    case '\\':
                        Control();
                        break;
                    case '\r' or '\n':
                        break;
                    default:
                        Literal((byte)c);
                        break;
                }
            }
            FlushHex();
            FlushText();
        }

        void Push()
        {
            _ucSkip = 0;
            if (_depth >= MaxDepth) { _overflow++; return; }
            _stack[_depth - 1] = _s;
            var parent = _s;
            _s.Starred = false;
            _s.OwnsField = false;
            if (parent.Dest == Dest.Upr) _s.Dest = Dest.Skip; // the ANSI branch, unless it turns out to be \*\ud
            _depth++;
        }

        /// <summary>Ends a group; false when the document's outermost group ends.</summary>
        bool Pop()
        {
            _ucSkip = 0;
            if (_overflow > 0) { _overflow--; return true; }
            if (_s.OwnsField) EmitField(_s.Field);
            if (_s.Dest == Dest.Text) FlushText();
            _depth--;
            if (_depth <= 0) return false;
            _s = _stack[_depth - 1];
            return true;
        }

        void Control()
        {
            int c = Next();
            if (c < 0) return;
            if (!IsLetter(c))
            {
                Symbol(c);
                return;
            }
            int n = 0;
            _word[n++] = (char)c;
            while (IsLetter(Peek()) && n < MaxWord) _word[n++] = (char)Next();
            while (IsLetter(Peek())) Next(); // an overlong word: the rest is dropped
            bool hasParam = false, negative = false;
            long param = 0;
            if (Peek() == '-') { Next(); negative = true; }
            int digits = 0;
            while (Peek() is >= '0' and <= '9')
            {
                int d = Next() - '0';
                if (digits++ < 10) param = param * 10 + d;
                hasParam = true;
            }
            if (Peek() == ' ') Next();
            int value = (int)Math.Clamp(negative ? -param : param, int.MinValue, int.MaxValue);
            Word(_word.AsSpan(0, n), hasParam, value);
        }

        void Word(ReadOnlySpan<char> w, bool hasParam, int p)
        {
            if (w is "bin")
            {
                FlushHex();
                if (p > 0) SkipBytes(p);
                return;
            }
            bool starred = _s.Starred;
            _s.Starred = false;
            if (starred)
            {
                // An ignorable destination: only \ud inside \upr is kept (it holds the Unicode version of the text).
                if (w is "ud" && _depth >= 2 && _stack[_depth - 2].Dest == Dest.Upr)
                {
                    var upr = _stack[_depth - 2];
                    _s.Dest = upr.UprOuter;
                    _s.Field = upr.UprOuterField;
                }
                else _s.Dest = Dest.Skip;
                return;
            }
            if (_s.Dest == Dest.Skip) return;
            if (_ucSkip > 0) { _ucSkip--; return; }

            if (_s.Dest == Dest.FontTable)
            {
                switch (w)
                {
                    case "f": _fontEntry = p; break;
                    case "fcharset": if (_fontEntry >= 0) _fontCodePages.TryAdd(_fontEntry, CodePageOfCharset(p, _ansiCodePage)); break;
                    case "cpg": if (_fontEntry >= 0 && p > 0) _fontCodePages[_fontEntry] = p; break;
                }
                return;
            }

            switch (w)
            {
                // Document character set.
                case "ansi": _ansiCodePage = 1252; return;
                case "mac": _ansiCodePage = 10000; return;
                case "pc": _ansiCodePage = 437; return;
                case "pca": _ansiCodePage = 850; return;
                case "ansicpg": if (p > 0) _ansiCodePage = p; return;
                case "deff": _defaultFont = p; return;
                case "uc": _s.Uc = Math.Clamp(p, 0, 16); return;
                case "fonttbl": FlushHex(); FlushText(); _s.Dest = Dest.FontTable; _fontEntry = -1; return;
                case "info": FlushHex(); FlushText(); _s.Dest = Dest.Info; return;
                case "upr":
                    _s.UprOuter = _s.Dest;
                    _s.UprOuterField = _s.Field;
                    _s.Dest = Dest.Upr;
                    return;
            }
            if (IsSkippedDestination(w))
            {
                FlushHex();
                if (_s.Dest == Dest.Text) FlushText();
                _s.Dest = Dest.Skip;
                return;
            }
            if (_s.Dest == Dest.Info)
            {
                DocField? field = w switch
                {
                    "title" => DocField.Title, "subject" => DocField.Subject, "author" => DocField.Author,
                    "keywords" => DocField.Keywords, "doccomm" => DocField.Comment, _ => null,
                };
                if (field is { } f)
                {
                    _s.Dest = Dest.Field;
                    _s.Field = f;
                    _s.OwnsField = true;
                    _field.Clear();
                }
                else if (w is "nofpages" && hasParam && p > 0 && sink.Pages == 0) sink.Pages = p;
                return;
            }
            if (_s.Dest is not (Dest.Text or Dest.Field)) return;
            switch (w)
            {
                case "f":
                    FlushHex();
                    _s.Font = p;
                    return;
                case "u":
                    FlushHex();
                    Char((char)(ushort)(p < 0 ? p + 65536 : p));
                    _ucSkip = _s.Uc;
                    return;
                case "par" or "line" or "sect" or "row" or "lbr":
                    FlushHex();
                    NewLine();
                    return;
                case "page":
                    FlushHex();
                    if (_s.Dest == Dest.Text) { FlushText(); sink.PageBreak(); }
                    return;
                case "tab" or "cell" or "nestcell":
                    FlushHex();
                    Char('\t');
                    return;
                case "emdash": FlushHex(); Char('—'); return;
                case "endash": FlushHex(); Char('–'); return;
                case "bullet": FlushHex(); Char('•'); return;
                case "lquote": FlushHex(); Char('‘'); return;
                case "rquote": FlushHex(); Char('’'); return;
                case "ldblquote": FlushHex(); Char('“'); return;
                case "rdblquote": FlushHex(); Char('”'); return;
                case "emspace" or "enspace" or "qmspace": FlushHex(); Char(' '); return;
            }
        }

        static bool IsSkippedDestination(ReadOnlySpan<char> w) => w is "colortbl" or "stylesheet" or "pict" or "object" or "themedata"
            or "datastore" or "fldinst" or "xmlnstbl" or "listtable" or "listoverridetable" or "rsidtbl" or "revtbl" or "filetbl"
            or "latentstyles" or "pgdsctbl" or "bkmkstart" or "bkmkend" or "nonshppict" or "listtext" or "pntext" or "pntxta"
            or "pntxtb" or "atnid" or "atnauthor" or "atrfstart" or "atrfend" or "sp" or "sn" or "sv" or "xe" or "tc" or "txe"
            or "generator" or "mmathPict" or "blipuid" or "objdata" or "objclass" or "fontemb" or "fontfile" or "falt" or "panose"
            or "passwordhash" or "protusertbl" or "wgrffmtfilter" or "colorschememapping" or "defchp" or "defpap" or "mhtmltag"
            or "htmltag" or "picprop" or "shpinst" or "userprops" or "docvar" or "background" or "template";

        void Symbol(int c)
        {
            switch (c)
            {
                case '\'':
                {
                    int h = Next(), l = Next();
                    if (Hex(h) is int hi && Hex(l) is int lo) Byte((byte)(hi * 16 + lo));
                    return;
                }
                case '*':
                    _s.Starred = true;
                    return;
                case '\\' or '{' or '}':
                    LiteralChar((char)c);
                    return;
                case '~':
                    LiteralChar(' ');
                    return;
                case '_':
                    LiteralChar('-');
                    return;
                case '\r' or '\n':
                    if (_s.Dest is Dest.Text or Dest.Field && _ucSkip == 0) { FlushHex(); NewLine(); }
                    return;
                case '-' or ':' or '|':
                    return;
                default:
                    return;
            }
        }

        /// <summary>A plain character of the file (not escaped).</summary>
        void Literal(byte b)
        {
            if (_s.Dest is not (Dest.Text or Dest.Field)) return;
            if (_ucSkip > 0) { _ucSkip--; return; }
            int cp = CodePage();
            // A double-byte character whose lead came as \'hh and whose trail byte is a plain character.
            if (b < 0x80 && _hex.Count > 0 && cp == _hexCodePage && EndsWithLead(cp)) { _hex.Add(b); return; }
            if (b >= 0x80) { AddHex(b, cp); return; }
            FlushHex();
            Char(b == '\t' ? '\t' : (char)b);
        }

        void LiteralChar(char c)
        {
            if (_s.Dest is not (Dest.Text or Dest.Field)) return;
            if (_ucSkip > 0) { _ucSkip--; return; }
            FlushHex();
            Char(c);
        }

        void Byte(byte b)
        {
            if (_s.Dest is not (Dest.Text or Dest.Field)) return;
            if (_ucSkip > 0) { _ucSkip--; return; }
            AddHex(b, CodePage());
        }

        void AddHex(byte b, int cp)
        {
            if (_hex.Count > 0 && cp != _hexCodePage) FlushHex();
            _hexCodePage = cp;
            _hex.Add(b);
            if (_hex.Count >= 4096 && !EndsWithLead(cp)) FlushHex();
        }

        bool EndsWithLead(int cp)
        {
            int i = 0;
            while (i < _hex.Count) i += IsLead(cp, _hex[i]) ? 2 : 1;
            return i == _hex.Count + 1;
        }

        void FlushHex()
        {
            if (_hex.Count == 0) return;
            var bytes = _hex.ToArray();
            _hex.Clear();
            string s = EncodingFor(_hexCodePage).GetString(bytes);
            foreach (char c in s) Char(c);
        }

        int CodePage()
        {
            int font = _s.Font >= 0 ? _s.Font : _defaultFont;
            return font >= 0 && _fontCodePages.TryGetValue(font, out int cp) ? cp : _ansiCodePage;
        }

        Encoding EncodingFor(int cp)
        {
            if (_encodings.TryGetValue(cp, out var e)) return e;
            e = TextDecoding.ForCharset("windows-" + cp) ?? (cp == _ansiCodePage ? null : TextDecoding.ForCharset("windows-" + _ansiCodePage))
                ?? Encoding.Latin1;
            _encodings[cp] = e;
            return e;
        }

        void Char(char c)
        {
            if (_s.Dest == Dest.Field)
            {
                if (_field.Length < ExtractSink.MaxFieldChars * 2) _field.Append(c);
                return;
            }
            if (_s.Dest != Dest.Text) return;
            _text.Append(c);
            if (_text.Length >= 4096) FlushText();
        }

        void NewLine()
        {
            if (_s.Dest == Dest.Field) { Char(' '); return; }
            if (_s.Dest != Dest.Text) return;
            FlushText();
            sink.LineBreak();
        }

        void FlushText()
        {
            if (_text.Length == 0) return;
            foreach (var chunk in _text.GetChunks()) sink.Text(chunk.Span);
            _text.Clear();
        }

        void EmitField(DocField field)
        {
            FlushHex();
            string value = _field.ToString().Trim();
            _field.Clear();
            if (value.Length == 0) return;
            if (field == DocField.Keywords) sink.Keywords(value);
            else sink.Field(field, value);
        }

        static bool IsLetter(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

        static int? Hex(int c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => null,
        };

        int Next()
        {
            if (_pos >= _len && !Fill()) return -1;
            return _buffer[_pos++];
        }

        int Peek()
        {
            if (_pos >= _len && !Fill()) return -1;
            return _buffer[_pos];
        }

        bool Fill()
        {
            _offset += _len;
            _pos = _len = 0;
            if (_offset >= _limit) return false;
            int want = (int)Math.Min(_buffer.Length, _limit - _offset);
            _len = source.ReadFully(_offset, _buffer.AsSpan(0, want));
            return _len > 0;
        }

        void SkipBytes(long n)
        {
            long inBuffer = _len - _pos;
            if (n <= inBuffer) { _pos += (int)n; return; }
            n -= inBuffer;
            _offset += _len + n;
            _pos = _len = 0;
        }
    }

    /// <summary>The code page of an RTF \fcharset.</summary>
    internal static int CodePageOfCharset(int charset, int ansi) => charset switch
    {
        0 => 1252, 1 => ansi, 2 => 1252, 77 => 10000, 78 => 10001, 79 => 10003, 80 => 10008, 81 => 10002, 83 => 10005, 84 => 10004,
        85 => 10006, 86 => 10081, 87 => 10021, 88 => 10029, 89 => 10007, 128 => 932, 129 => 949, 130 => 1361, 134 => 936, 136 => 950,
        161 => 1253, 162 => 1254, 163 => 1258, 177 => 1255, 178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250, 254 => 437,
        255 => 850, _ => ansi,
    };

    /// <summary>A lead byte of a double-byte code page (a character continues in the next byte).</summary>
    internal static bool IsLead(int codePage, byte b) => codePage switch
    {
        932 => b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC,
        936 or 949 or 950 => b is >= 0x81 and <= 0xFE,
        1361 => b is >= 0x84 and <= 0xD3 or >= 0xD8 and <= 0xDE or >= 0xE0 and <= 0xF9,
        _ => false,
    };
}
