using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Extract;

/// <summary>Kinds of PDF token.</summary>
internal enum PdfToken : byte { Eof, Integer, Real, Name, String, HexString, Keyword, ArrayOpen, ArrayClose, DictOpen, DictClose, BraceOpen, BraceClose }

/// <summary>
/// PDF tokens: numbers, names, strings, hex strings, arrays, dictionaries, keywords (no recursion; a string's nested
/// parentheses are only counted). Reads bytes in memory (content streams, object streams) or a file through a fill
/// callback that brings in more bytes as needed, starting with small reads, never at or past its limit.
/// </summary>
internal sealed class PdfLexer
{
    /// <summary>The longest string token kept; the rest of a longer string is skipped.</summary>
    public const int MaxStringBytes = 4 << 20;
    /// <summary>The longest name or keyword kept; the rest is skipped.</summary>
    const int MaxWordBytes = 256;

    /// <summary>Reads up to count bytes at a file offset into buffer[index..]; returns the bytes read (0 at the end).</summary>
    public delegate int Fill(long offset, byte[] buffer, int index, int count);

    readonly Fill? _fill;
    readonly long _limit;
    byte[] _buf;
    int _pos, _len, _mark = -1, _keepFrom = -1, _chunk;
    long _base;
    byte[] _scratch = new byte[64];
    int _stringLength;

    /// <summary>A lexer over bytes in memory (positions are indexes into them).</summary>
    public PdfLexer(byte[] data, int length)
    {
        _buf = data;
        _len = Math.Clamp(length, 0, data.Length);
        _limit = _len;
    }

    /// <summary>A lexer over a file through a fill callback, starting at an offset and never reading at or past limit.</summary>
    public PdfLexer(Fill fill, long start, long limit, int firstChunk = 512)
    {
        _fill = fill;
        _limit = limit;
        _chunk = Math.Max(64, firstChunk);
        _buf = new byte[_chunk];
        _base = Math.Max(0, start);
    }

    /// <summary>The kind of the current token.</summary>
    public PdfToken Kind { get; private set; }
    /// <summary>The current integer (Integer tokens; Real tokens truncated).</summary>
    public long Integer { get; private set; }
    /// <summary>The current number (Integer and Real tokens).</summary>
    public double Number { get; private set; }
    /// <summary>The decoded name (Name tokens, without the slash) or the keyword (Keyword tokens).</summary>
    public string Text { get; private set; } = "";
    /// <summary>The decoded bytes of a String or HexString token (valid until the next token).</summary>
    public ReadOnlySpan<byte> StringBytes => _scratch.AsSpan(0, _stringLength);
    /// <summary>Position of the current token's first byte.</summary>
    public long TokenStart { get; private set; }
    /// <summary>The bytes are in memory (no fill callback).</summary>
    public bool InMemory => _fill == null;
    /// <summary>The end of the bytes this lexer may read.</summary>
    public long Limit => _limit;

    /// <summary>Position of the next unread byte (a file offset, or an index for bytes in memory).</summary>
    public long Position
    {
        get => _base + _pos;
        set
        {
            if (value >= _base && value <= _base + _len) { _pos = (int)(value - _base); return; }
            if (_fill == null) { _pos = (int)Math.Clamp(value, 0, _len); return; }
            _base = Math.Clamp(value, 0, _limit);
            _pos = _len = 0;
            _mark = -1;
        }
    }

    /// <summary>Keeps the bytes from the current position in the buffer, so <see cref="Position"/> can come back to it.</summary>
    public void Mark() => _mark = _pos;
    /// <summary>Releases the mark.</summary>
    public void Unmark() => _mark = -1;

    /// <summary>The current token is this keyword.</summary>
    public bool IsKeyword(string keyword) => Kind == PdfToken.Keyword && Text == keyword;

    /// <summary>A PDF white-space byte.</summary>
    public static bool IsWhite(int b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    /// <summary>A PDF delimiter byte.</summary>
    public static bool IsDelimiter(int b) => b is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    /// <summary>A byte that ends a regular token.</summary>
    public static bool IsBreak(int b) => IsWhite(b) || IsDelimiter(b);

    /// <summary>The value of a hex digit, or -1.</summary>
    public static int HexValue(int b) => b switch
    {
        >= '0' and <= '9' => b - '0',
        >= 'a' and <= 'f' => b - 'a' + 10,
        >= 'A' and <= 'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>The next byte without consuming it, or -1 at the end.</summary>
    public int Peek() => _pos < _len || More() ? _buf[_pos] : -1;

    /// <summary>The next byte, consumed, or -1 at the end.</summary>
    public int ReadByte() => _pos < _len || More() ? _buf[_pos++] : -1;

    /// <summary>Skips white space and comments.</summary>
    public void SkipWhite()
    {
        while (true)
        {
            int b = Peek();
            if (b < 0) return;
            if (IsWhite(b)) { _pos++; continue; }
            if (b == '%')
            {
                while ((b = Peek()) >= 0 && b != '\n' && b != '\r') _pos++;
                continue;
            }
            return;
        }
    }

    /// <summary>Reads the next token; false at the end of the bytes.</summary>
    public bool Next()
    {
        SkipWhite();
        TokenStart = Position;
        int b = ReadByte();
        switch (b)
        {
            case -1: Kind = PdfToken.Eof; return false;
            case '[': Kind = PdfToken.ArrayOpen; return true;
            case ']': Kind = PdfToken.ArrayClose; return true;
            case '{': Kind = PdfToken.BraceOpen; return true;
            case '}': Kind = PdfToken.BraceClose; return true;
            case '(': ReadLiteral(); Kind = PdfToken.String; return true;
            case '/': ReadName(); Kind = PdfToken.Name; return true;
            case '<':
                if (Peek() == '<') { _pos++; Kind = PdfToken.DictOpen; return true; }
                ReadHex();
                Kind = PdfToken.HexString;
                return true;
            case '>':
                if (Peek() == '>') _pos++;
                Kind = PdfToken.DictClose;
                return true;
            case ')':
                // A stray close parenthesis: a keyword the callers ignore.
                Text = ")";
                Kind = PdfToken.Keyword;
                return true;
            default:
                _pos--;
                ReadWord();
                return true;
        }
    }

    bool More()
    {
        if (_fill == null) return false;
        long at = _base + _len;
        if (at >= _limit) return false;
        // Drop the bytes before what must stay (the mark, or the word being read).
        int keep = _pos;
        if (_mark >= 0) keep = Math.Min(keep, _mark);
        if (_keepFrom >= 0) keep = Math.Min(keep, _keepFrom);
        if (keep > 0)
        {
            Buffer.BlockCopy(_buf, keep, _buf, 0, _len - keep);
            _len -= keep;
            _pos -= keep;
            if (_mark >= 0) _mark -= keep;
            if (_keepFrom >= 0) _keepFrom -= keep;
            _base += keep;
        }
        int want = (int)Math.Min(_chunk, _limit - at);
        if (_len + want > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + want));
        int n = _fill(at, _buf, _len, want);
        if (n <= 0) return false;
        _len += n;
        if (_chunk < 64 << 10) _chunk *= 2;
        return true;
    }

    void ReadWord()
    {
        _keepFrom = _pos;
        bool tooLong = false;
        while (true)
        {
            if (_pos >= _len && !More()) break;
            if (IsBreak(_buf[_pos])) break;
            _pos++;
            if (_pos - _keepFrom >= MaxWordBytes) { tooLong = true; break; }
        }
        int start = _keepFrom;
        _keepFrom = -1;
        var word = _buf.AsSpan(start, _pos - start);
        if (word.IsEmpty)
        {
            // Not reachable for well-formed callers; consume one byte so the lexer always advances.
            _pos++;
            Text = "";
            Kind = PdfToken.Keyword;
            return;
        }
        if (TryNumber(word, out long integer, out double number, out bool isInteger))
        {
            Kind = isInteger ? PdfToken.Integer : PdfToken.Real;
            Integer = integer;
            Number = number;
        }
        else
        {
            Kind = PdfToken.Keyword;
            Text = Shared(word);
        }
        if (tooLong)
            while (Peek() is var c && c >= 0 && !IsBreak(c)) _pos++;
    }

    /// <summary>Parses a number leniently: signs, digits and one point; trailing junk is ignored ("1.2.3" is 1.2, "--5" is 5).</summary>
    internal static bool TryNumber(ReadOnlySpan<byte> s, out long integer, out double number, out bool isInteger)
    {
        integer = 0; number = 0; isInteger = true;
        int i = 0;
        bool negative = false;
        while (i < s.Length && s[i] is (byte)'+' or (byte)'-') { if (s[i] == '-') negative = !negative; i++; }
        if (i == s.Length || !(s[i] is >= (byte)'0' and <= (byte)'9' or (byte)'.')) return false;
        int digits = 0;
        long whole = 0;
        double value = 0;
        bool big = false;
        while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9')
        {
            int d = s[i] - '0';
            if (!big && whole > (long.MaxValue - d) / 10) big = true;
            if (!big) whole = whole * 10 + d;
            value = value * 10 + d;
            digits++;
            i++;
        }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            double scale = 0.1;
            while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9')
            {
                value += (s[i] - '0') * scale;
                scale *= 0.1;
                digits++;
                i++;
            }
            isInteger = false;
        }
        if (digits == 0) return false;
        if (big) isInteger = false;
        if (!double.IsFinite(value)) value = double.MaxValue;
        number = negative ? -value : value;
        integer = isInteger ? (negative ? -whole : whole) : (long)Math.Clamp(number, long.MinValue, long.MaxValue);
        return true;
    }

    void ReadName()
    {
        Span<byte> name = stackalloc byte[MaxWordBytes];
        int n = 0;
        while (true)
        {
            int b = Peek();
            if (b < 0 || IsBreak(b)) break;
            _pos++;
            if (b == '#')
            {
                int h1 = HexValue(Peek());
                if (h1 >= 0)
                {
                    _pos++;
                    int h2 = HexValue(Peek());
                    if (h2 >= 0) { _pos++; b = h1 * 16 + h2; }
                    else b = h1;
                }
            }
            if (n < name.Length) name[n++] = (byte)b;
        }
        Text = Shared(name[..n]);
    }

    // Names and keywords met all the time, shared instead of allocated (read-only after the static constructor).
    static readonly Dictionary<string, string> s_shared = BuildShared();
    static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> s_lookup = s_shared.GetAlternateLookup<ReadOnlySpan<char>>();

    static Dictionary<string, string> BuildShared()
    {
        string[] words =
        [
            // Content stream operators.
            "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI", "EMC",
            "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re",
            "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr",
            "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"",
            // File structure and CMaps.
            "obj", "endobj", "stream", "endstream", "R", "xref", "trailer", "startxref", "true", "false", "null",
            "begincodespacerange", "endcodespacerange", "beginbfchar", "endbfchar", "beginbfrange", "endbfrange",
            "begincidrange", "endcidrange", "begincidchar", "endcidchar", "beginnotdefrange", "endnotdefrange", "usecmap",
            "def", "begin", "end", "dict", "dup", "pop", "currentdict", "findresource", "defineresource", "begincmap", "endcmap",
            // Common names.
            "Type", "Subtype", "Length", "Filter", "DecodeParms", "Font", "XObject", "Resources", "Contents", "Page", "Pages",
            "Kids", "Count", "Parent", "MediaBox", "CropBox", "Rotate", "Catalog", "Root", "Info", "Encrypt", "ID", "Prev",
            "Size", "Index", "W", "XRef", "XRefStm", "ObjStm", "N", "First", "FlateDecode", "Fl", "LZWDecode", "LZW",
            "ASCII85Decode", "A85", "ASCIIHexDecode", "AHx", "RunLengthDecode", "RL", "DCTDecode", "DCT", "JPXDecode",
            "CCITTFaxDecode", "CCF", "JBIG2Decode", "Crypt", "Predictor", "Columns", "Colors", "BitsPerComponent",
            "EarlyChange", "Image", "Form", "Matrix", "BBox", "BaseFont", "Encoding", "ToUnicode", "Widths", "FirstChar",
            "LastChar", "FontDescriptor", "DescendantFonts", "DW", "W2", "DW2", "Differences", "BaseEncoding", "WinAnsiEncoding",
            "MacRomanEncoding", "StandardEncoding", "PDFDocEncoding", "MacExpertEncoding", "Identity-H", "Identity-V",
            "Type0", "Type1", "Type3", "TrueType", "MMType1", "CIDFontType0", "CIDFontType2", "FontMatrix", "Flags",
            "MissingWidth", "Ascent", "Descent", "AvgWidth", "CIDSystemInfo", "Metadata", "Title", "Author", "Subject",
            "Keywords", "Creator", "Producer", "Filter", "V", "R", "O", "U", "P", "OE", "UE", "Perms", "CF", "StmF", "StrF",
            "CFM", "AESV2", "AESV3", "V2", "None", "Identity", "StdCF", "EncryptMetadata", "Standard", "ExtGState",
            "ColorSpace", "Pattern", "Shading", "ProcSet", "Properties", "Annots", "Group", "WMode", "CMapName", "PieceInfo",
        ];
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in words) map[w] = w;
        return map;
    }

    /// <summary>A name or keyword as a string (UTF-8 when valid, else Latin-1), shared when it is a common one.</summary>
    static string Shared(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "";
        if (bytes.Length <= 64 && Ascii.IsValid(bytes))
        {
            Span<char> chars = stackalloc char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return s_lookup.TryGetValue(chars, out var hit) ? hit : new string(chars);
        }
        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    void ReadLiteral()
    {
        int n = 0, depth = 1;
        while (true)
        {
            int b = ReadByte();
            if (b < 0) break;
            if (b == '\\')
            {
                b = ReadByte();
                switch (b)
                {
                    case -1: goto done;
                    case 'n': b = '\n'; break;
                    case 'r': b = '\r'; break;
                    case 't': b = '\t'; break;
                    case 'b': b = '\b'; break;
                    case 'f': b = '\f'; break;
                    case '\r':
                        if (Peek() == '\n') _pos++;
                        continue;
                    case '\n': continue;
                    case >= '0' and <= '7':
                        {
                            int v = b - '0';
                            for (int k = 0; k < 2; k++)
                            {
                                int c = Peek();
                                if (c is < '0' or > '7') break;
                                _pos++;
                                v = v * 8 + (c - '0');
                            }
                            b = v & 0xFF;
                            break;
                        }
                    default: break; // \( \) \\ and unknown escapes: the byte itself
                }
            }
            else if (b == '(') depth++;
            else if (b == ')' && --depth == 0) break;
            Put(ref n, (byte)b);
        }
    done:
        _stringLength = Math.Min(n, MaxStringBytes);
    }

    void ReadHex()
    {
        int n = 0, high = -1;
        while (true)
        {
            int b = ReadByte();
            if (b < 0 || b == '>') break;
            int v = HexValue(b);
            if (v < 0) continue;
            if (high < 0) high = v;
            else { Put(ref n, (byte)(high * 16 + v)); high = -1; }
        }
        if (high >= 0) Put(ref n, (byte)(high * 16));
        _stringLength = Math.Min(n, MaxStringBytes);
    }

    void Put(ref int n, byte b)
    {
        if (n >= MaxStringBytes) { n++; return; }
        if (n == _scratch.Length) Array.Resize(ref _scratch, Math.Min(MaxStringBytes, _scratch.Length * 2));
        _scratch[n++] = b;
    }

    /// <summary>The current string token's bytes as a new array.</summary>
    public byte[] StringArray() => StringBytes.ToArray();

    /// <summary>The current token for diagnostics (never a string's contents).</summary>
    public override string ToString() => Kind switch
    {
        PdfToken.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        PdfToken.Real => Number.ToString(CultureInfo.InvariantCulture),
        PdfToken.Name => "/" + Text,
        PdfToken.Keyword => Text,
        _ => Kind.ToString(),
    };
}
