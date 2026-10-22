using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>A PDF object: null, boolean, number, name, string, array, dictionary, reference or stream.</summary>
internal abstract class PdfObject { }

/// <summary>The null object (also what a missing or unparsable value becomes).</summary>
internal sealed class PdfNull : PdfObject
{
    /// <summary>The one null.</summary>
    public static readonly PdfNull Instance = new();
    PdfNull() { }
}

/// <summary>true or false.</summary>
internal sealed class PdfBool : PdfObject
{
    /// <summary>The two booleans.</summary>
    public static readonly PdfBool True = new(true), False = new(false);
    PdfBool(bool value) => Value = value;
    /// <summary>The value.</summary>
    public bool Value { get; }
}

/// <summary>An integer or real number.</summary>
internal sealed class PdfNumber : PdfObject
{
    static readonly PdfNumber[] s_small = Enumerable.Range(-16, 1041).Select(i => new PdfNumber(i, true)).ToArray();

    PdfNumber(double value, bool isInteger) { Value = value; IsInteger = isInteger; }
    /// <summary>The value.</summary>
    public double Value { get; }
    /// <summary>Written without a decimal point.</summary>
    public bool IsInteger { get; }

    /// <summary>A number, small integers shared.</summary>
    public static PdfNumber Of(double value, bool isInteger)
        => isInteger && value is >= -16 and <= 1024 ? s_small[(int)value + 16] : new PdfNumber(value, isInteger);
}

/// <summary>A name (without its slash).</summary>
internal sealed class PdfName(string value) : PdfObject
{
    /// <summary>The name, #xx escapes decoded.</summary>
    public string Value { get; } = value;
}

/// <summary>A string's bytes (literal or hex).</summary>
internal sealed class PdfString(byte[] bytes) : PdfObject
{
    /// <summary>The bytes (decrypted in place once, when the document is encrypted).</summary>
    public byte[] Bytes { get; set; } = bytes;
}

/// <summary>An array.</summary>
internal sealed class PdfArray : PdfObject
{
    /// <summary>The elements.</summary>
    public List<PdfObject> Items { get; } = [];
    /// <summary>Number of elements.</summary>
    public int Count => Items.Count;
    /// <summary>An element, or null past the end.</summary>
    public PdfObject this[int i] => i >= 0 && i < Items.Count ? Items[i] : PdfNull.Instance;
}

/// <summary>A dictionary; keys are names without their slash.</summary>
internal sealed class PdfDict : PdfObject
{
    /// <summary>The entries.</summary>
    public Dictionary<string, PdfObject> Items { get; } = new(StringComparer.Ordinal);
    /// <summary>The value of a key, unresolved; null when absent.</summary>
    public PdfObject? this[string key] => Items.TryGetValue(key, out var v) ? v : null;
    /// <summary>The value is a name equal to this one.</summary>
    public bool Is(string key, string name) => this[key] is PdfName n && n.Value == name;
    /// <summary>The value as a name, or null.</summary>
    public string? Name(string key) => this[key] is PdfName n ? n.Value : null;
}

/// <summary>An indirect reference "num gen R".</summary>
internal sealed class PdfRef(int number, int generation) : PdfObject
{
    /// <summary>The object number.</summary>
    public int Number { get; } = number;
    /// <summary>The generation.</summary>
    public int Generation { get; } = generation;
}

/// <summary>A stream: its dictionary and where its data starts in the file (the data itself is read only on demand).</summary>
internal sealed class PdfStream(PdfDict dict, long dataOffset, int number, int generation) : PdfObject
{
    /// <summary>The stream's dictionary.</summary>
    public PdfDict Dict { get; } = dict;
    /// <summary>Where the data starts in the file.</summary>
    public long DataOffset { get; } = dataOffset;
    /// <summary>The object it is (for decryption keys); 0 when unknown.</summary>
    public int Number { get; } = number;
    /// <summary>Its generation.</summary>
    public int Generation { get; } = generation;
}

/// <summary>A file that can't be read as a PDF at all (no usable cross-reference or catalog).</summary>
internal sealed class PdfFormatException(string message) : Exception(message) { }

/// <summary>
/// Builds objects from tokens without recursion: an explicit stack with a depth cap (deeper nesting is skipped and
/// becomes null), a cap on items per object, and "num gen R" references found by looking two tokens ahead.
/// </summary>
internal static class PdfParser
{
    /// <summary>Deepest nesting of arrays and dictionaries built (spec §4: PDF objects 32).</summary>
    public const int MaxDepth = 32;
    /// <summary>Most values built for one object; the rest are skipped (a 10,000-page flat /Kids or a big CJK /W fits).</summary>
    public const int MaxItems = 1 << 16;

    /// <summary>
    /// Parses the object that starts with the lexer's current token (already read). With refs, "num gen R" becomes a
    /// reference. Stops leniently at a keyword that can't be a value (endobj, stream, an operator), closing what is open.
    /// When it returns, the lexer's current token is the last one of the object (call Next for what follows), except when
    /// it stopped at a keyword, which is left as the current token (Stopped true).
    /// </summary>
    public static PdfObject Parse(PdfLexer lx, bool refs, ExtractBudget? budget, out bool stopped)
    {
        stopped = false;
        switch (lx.Kind)
        {
            case PdfToken.ArrayOpen:
            case PdfToken.DictOpen:
                return ParseContainer(lx, refs, budget, out stopped);
            default:
                var simple = Simple(lx, refs, out bool isValue);
                if (!isValue) stopped = true;
                return simple ?? PdfNull.Instance;
        }
    }

    /// <summary>A value from a non-container token; isValue false for keywords that aren't values (operators, endobj).</summary>
    static PdfObject? Simple(PdfLexer lx, bool refs, out bool isValue)
    {
        isValue = true;
        switch (lx.Kind)
        {
            case PdfToken.Integer:
                {
                    long value = lx.Integer;
                    if (refs && TryReference(lx, out var r)) return r;
                    return PdfNumber.Of(value, true);
                }
            case PdfToken.Real: return PdfNumber.Of(lx.Number, false);
            case PdfToken.Name: return new PdfName(lx.Text);
            case PdfToken.String:
            case PdfToken.HexString: return new PdfString(lx.StringArray());
            case PdfToken.Keyword:
                switch (lx.Text)
                {
                    case "true": return PdfBool.True;
                    case "false": return PdfBool.False;
                    case "null": return PdfNull.Instance;
                }
                isValue = false;
                return null;
            default:
                isValue = false;
                return null;
        }
    }

    /// <summary>After an integer: "gen R" follows → a reference (the lexer then stands on R); otherwise the lexer goes back.</summary>
    static bool TryReference(PdfLexer lx, out PdfRef? r)
    {
        r = null;
        long number = lx.Integer;
        if (number is < 0 or > int.MaxValue) return false;
        long back = lx.Position;
        lx.Mark();
        try
        {
            if (lx.Next() && lx.Kind == PdfToken.Integer && lx.Integer is >= 0 and <= 65535)
            {
                long gen = lx.Integer;
                if (lx.Next() && lx.IsKeyword("R"))
                {
                    r = new PdfRef((int)number, (int)gen);
                    return true;
                }
            }
            lx.Position = back; // the caller kept the integer's value
            return false;
        }
        finally { lx.Unmark(); }
    }

    sealed class Frame
    {
        public PdfArray? Array;
        public PdfDict? Dict;
        public string? Key;
        public PdfObject Container => (PdfObject?)Array ?? Dict!;
    }

    static PdfObject ParseContainer(PdfLexer lx, bool refs, ExtractBudget? budget, out bool stopped)
    {
        stopped = false;
        var stack = new List<Frame>(8);
        int items = 0, skipDepth = 0, tokens = 0;
        PdfObject? result = null;
        bool first = true;
        while (true)
        {
            if (!first && !lx.Next()) break;
            first = false;
            if ((++tokens & 1023) == 0) budget?.Check();
            var kind = lx.Kind;
            if (skipDepth > 0)
            {
                // Too deep: count brackets until back at the cap, keeping nothing.
                if (kind is PdfToken.ArrayOpen or PdfToken.DictOpen) skipDepth++;
                else if (kind is PdfToken.ArrayClose or PdfToken.DictClose) skipDepth--;
                else if (kind == PdfToken.Keyword && lx.Text is "endobj" or "stream" or "endstream") { stopped = true; break; }
                if (skipDepth == 0) Place(stack, PdfNull.Instance, ref items);
                continue;
            }
            switch (kind)
            {
                case PdfToken.ArrayOpen:
                case PdfToken.DictOpen:
                    if (stack.Count >= MaxDepth) { skipDepth = 1; continue; }
                    var frame = kind == PdfToken.ArrayOpen ? new Frame { Array = new PdfArray() } : new Frame { Dict = new PdfDict() };
                    stack.Add(frame);
                    continue;
                case PdfToken.ArrayClose:
                case PdfToken.DictClose:
                    {
                        if (stack.Count == 0) continue;
                        // A mismatched close ends the innermost container all the same (lenient).
                        var done = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (stack.Count == 0) { result = done.Container; goto finished; }
                        Place(stack, done.Container, ref items);
                        continue;
                    }
                case PdfToken.BraceOpen:
                case PdfToken.BraceClose:
                    continue; // PostScript calculator functions: not needed for text
                default:
                    {
                        var value = Simple(lx, refs, out bool isValue);
                        if (!isValue)
                        {
                            if (lx.IsKeyword(")")) continue;
                            // An operator or endobj inside a container: the object ends here.
                            stopped = true;
                            goto closeAll;
                        }
                        Place(stack, value!, ref items);
                        continue;
                    }
            }
        }
    closeAll:
        // Ran out of tokens (or stopped) with containers open: close them all.
        while (stack.Count > 1)
        {
            var done = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Place(stack, done.Container, ref items);
        }
        result = stack.Count == 1 ? stack[0].Container : PdfNull.Instance;
    finished:
        return result ?? PdfNull.Instance;
    }

    static void Place(List<Frame> stack, PdfObject value, ref int items)
    {
        if (stack.Count == 0) return;
        if (++items > MaxItems) return;
        var top = stack[^1];
        if (top.Array != null) { top.Array.Items.Add(value); return; }
        if (top.Key == null)
        {
            if (value is PdfName name) top.Key = name.Value;
            return; // a value without a key: ignored
        }
        top.Dict!.Items[top.Key] = value;
        top.Key = null;
    }

    /// <summary>Calls visit on every string inside an object (dictionaries and arrays, not streams' data), iteratively.</summary>
    public static void ForEachString(PdfObject root, Action<PdfString> visit)
    {
        var stack = new Stack<(PdfObject Obj, int Depth)>();
        stack.Push((root, 0));
        int seen = 0;
        while (stack.Count > 0 && seen++ < MaxItems)
        {
            var (o, depth) = stack.Pop();
            switch (o)
            {
                case PdfString s: visit(s); break;
                case PdfArray a when depth < MaxDepth: foreach (var x in a.Items) stack.Push((x, depth + 1)); break;
                case PdfDict d when depth < MaxDepth: foreach (var x in d.Items.Values) stack.Push((x, depth + 1)); break;
                case PdfStream st when depth < MaxDepth: stack.Push((st.Dict, depth + 1)); break;
            }
        }
    }
}

/// <summary>PDF text strings: UTF-16BE with FE FF, UTF-8 with EF BB BF (PDF 2.0), else PDFDocEncoding.</summary>
internal static class PdfText
{
    /// <summary>A text string's characters.</summary>
    public static string Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b[2..(2 + ((b.Length - 2) & ~1))]);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b[2..(2 + ((b.Length - 2) & ~1))]);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b[3..]);
        var chars = new char[b.Length];
        int n = 0;
        foreach (byte x in b)
        {
            char c = PdfDoc[x];
            if (c != '\0') chars[n++] = c;
        }
        return new string(chars, 0, n);
    }

    /// <summary>PDFDocEncoding (Annex D): '\0' = undefined.</summary>
    public static readonly char[] PdfDoc = BuildPdfDoc();

    static char[] BuildPdfDoc()
    {
        var t = new char[256];
        t[9] = '\t'; t[10] = '\n'; t[13] = '\r';
        "˘ˇˆ˙˝˛˚˜".CopyTo(0, t, 24, 8);
        for (int i = 32; i < 127; i++) t[i] = (char)i;
        "•†‡…—–ƒ⁄‹›−‰„“”‘’‚™ﬁﬂŁŒŠŸŽıłœšž".CopyTo(0, t, 128, 31);
        t[160] = '€';
        for (int i = 161; i < 256; i++) t[i] = (char)i;
        t[173] = '\0';
        return t;
    }
}
