using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>Text detection (BOM, UTF-16 heuristics, UTF-8 validity, control-byte ratio) and decoding.</summary>
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
        // Legacy 8-bit text: high bytes but almost no control characters.
        encoding = Encoding.GetEncoding(1252);
        return true;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length)
        {
            byte b = s[i];
            if (b < 0x80) { i++; continue; }
            int len = b >= 0xF0 && b <= 0xF4 ? 4 : b >= 0xE0 ? 3 : b >= 0xC2 && b < 0xE0 ? 2 : 0;
            if (len == 0) return false;
            if (i + len > s.Length) return true; // truncated at the end of the sample
            for (int k = 1; k < len; k++) if ((s[i + k] & 0xC0) != 0x80) return false;
            i += len;
        }
        return true;
    }

    static TextPreview()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static (string? Text, string? EncodingName) Decode(byte[] bytes)
    {
        if (!LooksLikeText(bytes, out var enc) || enc == null) return (null, null);
        int skip = enc.GetPreamble().Length;
        if (skip > 0 && !bytes.AsSpan().StartsWith(enc.GetPreamble())) skip = 0;
        // Trim a possibly split trailing multibyte char.
        string text = enc.GetString(bytes, skip, bytes.Length - skip);
        if (text.Length > 0 && text[^1] == '�') text = text[..^1];
        return (text, DescribeEncoding(enc, skip > 0));
    }

    private static string DescribeEncoding(Encoding e, bool bom) => e switch
    {
        UTF8Encoding => bom ? "UTF-8 (BOM)" : "UTF-8",
        UnicodeEncoding u => (u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE ? "UTF-16 BE" : "UTF-16 LE") + (bom ? " (BOM)" : ""),
        UTF32Encoding => "UTF-32",
        _ => e.WebName,
    };

    public static PreviewResult? Render(string path, string ext, int maxBytes, bool force)
    {
        long len = new FileInfo(path).Length;
        var bytes = FileUtil.ReadHead(path, maxBytes);
        if (!force && !LooksLikeText(bytes.Length > 65536 ? bytes[..65536] : bytes, out _)) return null;
        var (text, encName) = Decode(bytes);
        if (text == null)
        {
            if (!force) return null;
            text = Encoding.Latin1.GetString(bytes);
            encName = "binary (shown as Latin-1)";
        }
        bool truncated = len > bytes.Length;
        string format = ext.TrimStart('.').ToUpperInvariant() + " text";
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && !truncated) text = TryPrettyJson(text, ref format);
        var res = new PreviewResult(path, PreviewKind.Text, "Text")
        {
            FormatName = format,
            Text = text,
            TextTruncated = truncated,
            TextEncoding = encName,
        };
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        res.AddMeta("Lines", lines.ToString("N0") + (truncated ? "+" : ""));
        res.AddMeta("Encoding", encName);
        if (truncated) res.AddMeta("Shown", $"first {FileUtil.FormatShortSize(bytes.Length)} of {FileUtil.FormatShortSize(len)}");
        return res;
    }

    private static string TryPrettyJson(string text, ref string format)
    {
        // Only reformat "minified" JSON (few line breaks for its size).
        int nl = 0;
        foreach (char c in text) if (c == '\n') nl++;
        if (text.Length < 200 || nl > text.Length / 200) return text;
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
