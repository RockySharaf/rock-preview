using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Text encoding detection and decoding shared by the text reader and the preview window (from TextPreview, with UTF-8
/// checked over the whole window and the PC's own ANSI code page as the fallback).
/// </summary>
internal static partial class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding of a file head (BOM, BOM-less UTF-16, valid UTF-8, else the ANSI code page); null = binary.</summary>
    public static Encoding? Detect(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty) return new UTF8Encoding(false);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return new UTF8Encoding(true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0, 0])) return new UTF32Encoding(false, true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0, 0, 0xFE, 0xFF])) return new UTF32Encoding(true, true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return new UnicodeEncoding(false, true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return new UnicodeEncoding(true, true);

        var sample = head[..Math.Min(head.Length, 64 * 1024)];
        // BOM-less UTF-16: lots of zero bytes in alternating positions.
        if (sample.Length >= 16)
        {
            int zeroEven = 0, zeroOdd = 0;
            for (int i = 0; i + 1 < sample.Length; i += 2) { if (sample[i] == 0) zeroEven++; if (sample[i + 1] == 0) zeroOdd++; }
            int pairs = sample.Length / 2;
            if (zeroOdd > pairs * 0.4 && zeroEven < pairs * 0.05) return new UnicodeEncoding(false, false);
            if (zeroEven > pairs * 0.4 && zeroOdd < pairs * 0.05) return new UnicodeEncoding(true, false);
        }
        // An 8-bit file with NULs anywhere, or more than 1% control bytes in its head, is binary.
        if (head.Contains((byte)0)) return null;
        int control = 0;
        foreach (byte b in sample)
            if (b < 0x20 && b is not (9 or 10 or 12 or 13 or 27 or 11 or 8)) control++;
        if (control > sample.Length / 100 + 1) return null;
        // UTF-8 only when the whole window is valid (an ANSI CSV can be pure ASCII for its first 64 KB).
        if (Utf8.IsValid(head[..CompleteLength(head)])) return new UTF8Encoding(false);
        return Ansi();
    }

    /// <summary>Decodes bytes, dropping a BOM (and a character cut off at the end of the window).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
        string text = encoding.GetString(bytes);
        return text.Length > 0 && text[^1] == '�' ? text[..^1] : text;
    }

    /// <summary>The PC's ANSI code page (GetACP), or 1252 when that is UTF-8 or unavailable.</summary>
    public static Encoding Ansi()
    {
        try
        {
            int acp = GetACP();
            if (acp is > 0 and not 65001) return Encoding.GetEncoding(acp);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException) { }
        return Encoding.GetEncoding(1252);
    }

    /// <summary>
    /// The encoding for a MIME or HTML charset name (aliases such as "utf8", "ks_c_5601-1987", "latin1" included); null
    /// when unknown, so the caller keeps its own fallback. Never throws.
    /// </summary>
    public static Encoding? ForCharset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
        n = n switch
        {
            "utf8" or "utf-8" or "unicode-1-1-utf-8" => "utf-8",
            "utf16" or "utf-16" or "unicode" => "utf-16",
            "latin1" or "latin-1" or "l1" => "iso-8859-1",
            "ansi" or "cp1252" or "x-ansi" => "windows-1252",
            "x-sjis" or "sjis" or "shift-jis" or "ms_kanji" or "cp932" => "shift_jis",
            "ks_c_5601-1987" or "ks_c_5601" or "ksc5601" or "cp949" or "x-windows-949" => "ks_c_5601-1987",
            "gb2312-80" or "cp936" or "x-gbk" => "gbk",
            "x-user-defined" => "windows-1252",
            _ => n,
        };
        if (n.StartsWith("cp", StringComparison.Ordinal) && int.TryParse(n.AsSpan(2), out int cp)) n = "windows-" + cp;
        try { return Encoding.GetEncoding(n); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        if (n.StartsWith("windows-", StringComparison.Ordinal) && int.TryParse(n.AsSpan(8), out int page))
        {
            try { return Encoding.GetEncoding(page); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>The charset an HTML file declares in its first 1,024 bytes (&lt;meta charset&gt; or http-equiv content); null when none or unknown.</summary>
    public static Encoding? HtmlCharset(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head[..Math.Min(head.Length, 1024)]);
        for (int at = text.IndexOf("<meta", StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf("<meta", at + 5, StringComparison.OrdinalIgnoreCase))
        {
            int end = text.IndexOf('>', at);
            if (end < 0) break;
            var tag = text.AsSpan(at, end - at);
            int cs = tag.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
            if (cs < 0) continue;
            var rest = tag[(cs + 7)..].TrimStart();
            if (rest.IsEmpty || rest[0] != '=') continue;
            rest = rest[1..].TrimStart().TrimStart("\"'");
            int stop = rest.IndexOfAny("\"'; />");
            var name = (stop < 0 ? rest : rest[..stop]).ToString();
            if (ForCharset(name) is { } enc) return enc is UnicodeEncoding or UTF32Encoding ? new UTF8Encoding(false) : enc;
        }
        return null;
    }

    /// <summary>The window's length without a UTF-8 sequence cut off at its end (at most 3 bytes dropped).</summary>
    static int CompleteLength(ReadOnlySpan<byte> s)
    {
        for (int back = 1; back <= 3 && back <= s.Length; back++)
        {
            byte b = s[^back];
            if ((b & 0xC0) == 0x80) continue; // continuation byte: look further back for its lead
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return need > back ? s.Length - back : s.Length;
        }
        return s.Length;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetACP();
}
