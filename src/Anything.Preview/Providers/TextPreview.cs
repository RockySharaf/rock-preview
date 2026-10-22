using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// Text detection (BOM, UTF-16 heuristics, UTF-8 validity, control-byte ratio) and decoding. A plain preview checks UTF-8
/// on the first 64 KB, as it always did, and 8-bit text falls back to the PC's ANSI code page (GetACP). Showing a search
/// match, the encoding is decided as the text reader in Core decides it (<see cref="TextDecoding.Detect"/> over the
/// same first 4 MB), so the preview shows the words the index found and numbers its lines the same way.
/// </summary>
internal static class TextPreview
{
    public static readonly HashSet<string> KnownTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".md", ".markdown", ".rst", ".adoc", ".json", ".jsonc", ".json5", ".xml", ".xaml", ".xsd",
        ".xsl", ".xslt", ".config", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".props", ".targets", ".sln", ".slnx", ".nuspec",
        ".resx", ".manifest", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".env", ".editorconfig",
        ".gitignore", ".gitattributes", ".cs", ".csx", ".vb", ".fs", ".fsx", ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh",
        ".hxx", ".inl", ".m", ".mm", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle", ".go", ".rs", ".swift", ".dart",
        ".py", ".pyw", ".pyi", ".rb", ".php", ".pl", ".pm", ".lua", ".r", ".jl", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".vue", ".svelte", ".css", ".scss", ".sass", ".less", ".html", ".htm", ".xhtml", ".shtml", ".svg", ".sql", ".sh",
        ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".reg", ".inf", ".nfo", ".diz", ".srt", ".vtt",
        ".ass", ".sub", ".tex", ".bib", ".sty", ".cls", ".asm", ".s", ".hlsl", ".glsl", ".fx", ".shader", ".cmake", ".mk",
        ".makefile", ".dockerfile", ".tf", ".hcl", ".graphql", ".gql", ".proto", ".plist", ".gcode", ".nc", ".obj", ".mtl",
        ".ply", ".pem", ".crt", ".cer", ".key", ".pub", ".asc", ".ics", ".vcf", ".eml", ".mbox", ".rtf", ".lst", ".map",
        ".csv", ".tsv", ".patch", ".diff", ".lock", ".gitmodules", ".npmrc", ".babelrc", ".eslintrc", ".prettierrc", ".ahk",
        ".au3", ".vbs", ".vba", ".bas", ".frm", ".pas", ".dpr", ".f90", ".for", ".cob", ".ada", ".erl", ".ex", ".exs", ".hs",
        ".elm", ".clj", ".lisp", ".el", ".scm", ".ml", ".nim", ".zig", ".v", ".sv", ".vhd", ".vhdl", ".tcl", ".awk", ".sed",
        ".m3u", ".m3u8", ".pls", ".cue", ".url", ".desktop", ".ipynb", ".geojson", ".kml", ".gpx", ".wkt", ".sol", ".razor", ".cshtml",
    };

    /// <summary>Sniffs <paramref name="head"/> (first bytes of the file) and decides whether it is text.</summary>
    public static bool LooksLikeText(byte[] head, out Encoding? encoding)
    {
        encoding = null;
        if (head.Length == 0) { encoding = new UTF8Encoding(false); return true; }
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { encoding = new UTF8Encoding(true); return true; }
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0) { encoding = new UTF32Encoding(false, true); return true; }
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); return true; }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); return true; }

        int n = Math.Min(head.Length, 64 * 1024);
        // BOM-less UTF-16: lots of zero bytes in alternating positions.
        if (n >= 16)
        {
            int zeroEven = 0, zeroOdd = 0;
            for (int i = 0; i + 1 < n; i += 2) { if (head[i] == 0) zeroEven++; if (head[i + 1] == 0) zeroOdd++; }
            int pairs = n / 2;
            if (zeroOdd > pairs * 0.4 && zeroEven < pairs * 0.05) { encoding = new UnicodeEncoding(false, false); return true; }
            if (zeroEven > pairs * 0.4 && zeroOdd < pairs * 0.05) { encoding = new UnicodeEncoding(true, false); return true; }
        }
        int control = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = head[i];
            if (b == 0) return false;
            if (b < 0x20 && b is not (9 or 10 or 12 or 13 or 27 or 11 or 8)) control++;
        }
        if (control > n / 100 + 1) return false;
        if (IsValidUtf8(head.AsSpan(0, n))) { encoding = new UTF8Encoding(false); return true; }
        // Legacy 8-bit text: high bytes but almost no control characters, in the PC's ANSI code page.
        encoding = TextDecoding.Ansi();
        return true;
    }

    /// <summary>Valid UTF-8, allowing one character cut off at the end of the sample.</summary>
    private static bool IsValidUtf8(ReadOnlySpan<byte> s)
    {
        // Leave out a trailing sequence that the sample cut short (at most 3 bytes).
        int end = s.Length;
        for (int back = 1; back <= 3 && back <= s.Length; back++)
        {
            byte b = s[^back];
            if ((b & 0xC0) == 0x80) continue;
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            if (need > back) end = s.Length - back;
            break;
        }
        return Utf8.IsValid(s[..end]);
    }

    static TextPreview()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static (string? Text, string? EncodingName) Decode(byte[] bytes) => Decode(bytes, null);

    /// <param name="known">The encoding already decided (as the index decides it); null = detect it from the bytes.</param>
    private static (string? Text, string? EncodingName) Decode(byte[] bytes, Encoding? known)
    {
        var enc = known;
        if (enc == null && (!LooksLikeText(bytes, out enc) || enc == null)) return (null, null);
        int skip = enc.GetPreamble().Length;
        if (skip > 0 && !bytes.AsSpan().StartsWith(enc.GetPreamble())) skip = 0;
        // Trim a possibly split trailing multibyte char.
        string text = enc.GetString(bytes, skip, bytes.Length - skip);
        if (text.Length > 0 && text[^1] == '�') text = text[..^1];
        return (text, DescribeEncoding(enc, skip > 0));
    }

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".htm", ".html", ".xhtml", ".shtml" };

    /// <summary>
    /// HTML: a byte order mark decides first, then the charset the page declares in its first 1,024 bytes
    /// (&lt;meta charset&gt;), then the usual detection, as the HTML text reader in Core does.
    /// </summary>
    private static (string? Text, string? EncodingName) DecodeHtml(byte[] bytes, Encoding? known)
    {
        var enc = known;
        if (enc == null && (!LooksLikeText(bytes, out enc) || enc == null)) return (null, null);
        bool bom = enc.GetPreamble() is { Length: > 0 } pre && bytes.AsSpan().StartsWith(pre);
        if (bom || TextDecoding.HtmlCharset(bytes) is not { } declared || declared.CodePage == enc.CodePage) return Decode(bytes, enc);
        string text = declared.GetString(bytes);
        if (text.Length > 0 && text[^1] == '\uFFFD') text = text[..^1];
        return (text, declared.WebName + " (declared)");
    }

    private static string DescribeEncoding(Encoding e, bool bom) => e switch
    {
        UTF8Encoding => bom ? "UTF-8 (BOM)" : "UTF-8",
        UnicodeEncoding u => (u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE ? "UTF-16 BE" : "UTF-16 LE") + (bom ? " (BOM)" : ""),
        UTF32Encoding => "UTF-32",
        _ => e.WebName,
    };

    /// <param name="keepLayout">
    /// Show the file's own lines as the index numbers them (a search match points at one): minified JSON isn't
    /// reformatted, and the encoding is the one the text reader decides over its first 4 MB.
    /// </param>
    public static PreviewResult? Render(string path, string ext, int maxBytes, bool force, bool keepLayout = false)
    {
        long len = new FileInfo(path).Length;
        byte[] bytes;
        Encoding? asIndexed = null;
        if (keepLayout && maxBytes < TextWindow.DetectBytes && len > maxBytes)
        {
            // Only read further to decide the encoding (a file can be valid UTF-8 for its first megabyte only).
            var window = FileUtil.ReadHead(path, TextWindow.DetectBytes);
            asIndexed = TextDecoding.Detect(window);
            bytes = window.Length > maxBytes ? window[..maxBytes] : window;
        }
        else
        {
            bytes = FileUtil.ReadHead(path, maxBytes);
            if (keepLayout) asIndexed = TextDecoding.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, TextWindow.DetectBytes)));
        }
        if (!force && !LooksLikeText(bytes.Length > 65536 ? bytes[..65536] : bytes, out _)) return null;
        // asIndexed is null without a match, or when the index's rule finds the file binary (a NUL past the first
        // 64 KB, so the index has no words of it): then the preview decides as it always did.
        var (text, encName) = HtmlExtensions.Contains(ext) ? DecodeHtml(bytes, asIndexed) : Decode(bytes, asIndexed);
        if (text == null)
        {
            if (!force) return null;
            text = Encoding.Latin1.GetString(bytes);
            encName = "binary (shown as Latin-1)";
        }
        bool truncated = len > bytes.Length;
        string format = ext.TrimStart('.').ToUpperInvariant() + " text";
        bool layoutKept = false;
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && !truncated)
        {
            if (!keepLayout) text = TryPrettyJson(text, ref format);
            else layoutKept = IsMinified(text);
        }
        var res = new PreviewResult(path, PreviewKind.Text, "Text")
        {
            FormatName = format,
            Text = text,
            TextTruncated = truncated,
            TextEncoding = encName,
            ShapedByMatch = layoutKept,
        };
        // Lines end where the text view and the index say they do ("\r\n", "\n", a lone "\r" ...).
        int lines = TextLines.Count(text);
        res.AddMeta("Lines", lines.ToString("N0") + (truncated ? "+" : ""));
        res.AddMeta("Encoding", encName);
        if (truncated) res.AddMeta("Shown", $"first {FileUtil.FormatShortSize(bytes.Length)} of {FileUtil.FormatShortSize(len)}");
        return res;
    }

    /// <summary>"Minified" JSON: few line breaks for its size.</summary>
    private static bool IsMinified(string text)
    {
        int nl = 0;
        foreach (char c in text) if (c == '\n') nl++;
        return text.Length >= 200 && nl <= text.Length / 200;
    }

    private static string TryPrettyJson(string text, ref string format)
    {
        // Only reformat "minified" JSON (few line breaks for its size).
        if (!IsMinified(text)) return text;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                doc.WriteTo(w);
            format = "JSON (pretty-printed)";
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return text;
        }
    }
}
