using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Preview;

/// <summary>A temporary folder deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnythingTextWindow-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, byte[] bytes)
    {
        string p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class TextWindowTests(ITestOutputHelper output)
{
    /// <summary>Line n of the generated files: its number, then letters of a length that varies with n.</summary>
    static string LineText(int n) => $"line {n} " + new string((char)('a' + n % 26), 20 + n % 61);

    static string Lines(int count, string newline)
    {
        var sb = new StringBuilder();
        for (int n = 1; n <= count; n++) sb.Append(LineText(n)).Append(newline);
        return sb.ToString();
    }

    /// <summary>The window starts and ends at line boundaries and holds the target line where its numbering says.</summary>
    static List<string> AssertWindow((string Text, int FirstLine)? result, int target, Func<int, string> lineText)
    {
        Assert.NotNull(result);
        var (text, first) = result!.Value;
        var lines = TextLines.Split(text);
        Assert.InRange(target, first, first + lines.Count - 1);
        Assert.Equal(lineText(target), lines[target - first]);
        Assert.Equal(lineText(first), lines[0]);
        Assert.Equal(lineText(first + lines.Count - 1), lines[^1]);
        return lines;
    }

    [Fact]
    public void A_line_deep_in_a_20_MB_file_comes_with_its_neighbours()
    {
        using var dir = new TempFolder();
        const int count = 400_000; // about 25 MB
        string path = dir.File("big.txt", Encoding.UTF8.GetBytes(Lines(count, "\n")));
        Assert.True(new FileInfo(path).Length > 20 << 20);
        const int target = 351_234;
        const int maxBytes = 1 << 20;

        var sw = Stopwatch.StartNew();
        var result = TextWindow.Load(path, target, maxBytes, CancellationToken.None);
        long cold = sw.ElapsedMilliseconds;
        sw.Restart();
        var again = TextWindow.Load(path, target, maxBytes, CancellationToken.None);
        output.WriteLine($"{new FileInfo(path).Length >> 20} MB file, line {target}: {cold} ms first, {sw.ElapsedMilliseconds} ms again");
        Assert.Equal(result, again);

        var lines = AssertWindow(result, target, LineText);
        var (text, first) = result!.Value;
        Assert.InRange(text.Length, maxBytes * 9 / 10, maxBytes);
        // Centred: about half the window comes before the target line.
        int before = lines.Take(target - first).Sum(l => l.Length + 1);
        Assert.InRange(before, maxBytes * 4 / 10, maxBytes / 2);
    }

    [Fact]
    public async Task LoadAsync_returns_the_same_window()
    {
        using var dir = new TempFolder();
        string path = dir.File("a.txt", Encoding.UTF8.GetBytes(Lines(50_000, "\r\n")));
        var sync = TextWindow.Load(path, 40_000, 64 * 1024, CancellationToken.None);
        var async = await TextWindow.LoadAsync(path, 40_000, 64 * 1024, CancellationToken.None);
        Assert.Equal(sync, async);
        AssertWindow(async, 40_000, LineText);
    }

    [Theory]
    [InlineData("utf-16 bom")]
    [InlineData("utf-16BE bom")]
    [InlineData("utf-16 no bom")]
    [InlineData("utf-16BE no bom")]
    [InlineData("utf-32 bom")]
    [InlineData("utf-8 bom")]
    public void Unicode_files_are_decoded_as_the_reader_decodes_them(string kind)
    {
        Encoding encoding = kind.Split(' ')[0] switch
        {
            "utf-16" => new UnicodeEncoding(false, true),
            "utf-16BE" => new UnicodeEncoding(true, true),
            "utf-32" => new UTF32Encoding(false, true),
            _ => new UTF8Encoding(true),
        };
        bool bom = !kind.EndsWith("no bom", StringComparison.Ordinal);
        // Non-ASCII text on every line, so a wrong guess can't pass.
        string Text(int n) => LineText(n) + " Grüße 東京 " + n;
        var sb = new StringBuilder();
        for (int n = 1; n <= 60_000; n++) sb.Append(Text(n)).Append(n % 3 == 0 ? "\r\n" : "\n");
        byte[] body = encoding.GetBytes(sb.ToString());
        using var dir = new TempFolder();
        string path = dir.File("u.txt", bom ? [.. encoding.GetPreamble(), .. body] : body);

        var result = TextWindow.Load(path, 55_555, 256 * 1024, CancellationToken.None);
        AssertWindow(result, 55_555, Text);
        Assert.False(result!.Value.Text.StartsWith('﻿'));
    }

    [Fact]
    public void Ansi_files_use_the_pcs_code_page()
    {
        // 0xE9 followed by ASCII is not UTF-8, so the reader's rule falls back to the ANSI code page (GetACP).
        var ansi = TextDecoding.Ansi();
        var lines = new List<byte[]>();
        for (int n = 1; n <= 2000; n++)
        {
            var line = Encoding.ASCII.GetBytes(LineText(n));
            lines.Add(n % 100 == 7 ? [.. line, (byte)' ', 0xE9, (byte)'t', (byte)'e', (byte)' ', 0xC0, (byte)' ', (byte)'x'] : line);
        }
        using var dir = new TempFolder();
        string path = dir.File("ansi.txt", lines.SelectMany(l => l.Append((byte)'\n')).ToArray());

        var result = TextWindow.Load(path, 1807, 16 * 1024, CancellationToken.None);
        AssertWindow(result, 1807, n => ansi.GetString(lines[n - 1]));
        Assert.Contains(ansi.GetString([0xE9, (byte)'t', (byte)'e']), result!.Value.Text);
    }

    [Fact]
    public void Every_line_end_of_the_reader_counts()
    {
        string[] expected = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", ""];
        string text = "one\r\ntwo\rthree\nfour\vfive\fsix\u0085seven" + (char)0x2028 + "eight" + (char)0x2029 + "nine\n";
        using var dir = new TempFolder();
        string path = dir.File("ends.txt", Encoding.UTF8.GetBytes(text));
        for (int line = 1; line <= expected.Length; line++)
        {
            var result = TextWindow.Load(path, line, 4096, CancellationToken.None);
            Assert.NotNull(result);
            var lines = TextLines.Split(result!.Value.Text);
            Assert.Equal(expected[line - 1], lines[line - result.Value.FirstLine]);
            Assert.Equal(1, result.Value.FirstLine);
            Assert.Equal(expected, lines);
        }
    }

    [Theory]
    [InlineData(64 * 1024)]              // the end of the head's first block
    [InlineData(4 << 20)]                // the end of the head: the rest is read block by block
    [InlineData((4 << 20) + 64 * 1024)]  // a block boundary after the head
    public void Line_ends_split_between_two_blocks_count_once(int boundary)
    {
        // An ASCII file in which one "\r\n" straddles the boundary and a lone '\r' ends the next block.
        var sb = new StringBuilder();
        var expected = new List<string>();
        void Add(string line, string end) { expected.Add(line); sb.Append(line).Append(end); }
        int n = 0;
        while (sb.Length + 200 < boundary) Add(LineText(++n), "\n");
        Add(new string('p', boundary - 1 - sb.Length), "\r\n");
        while (sb.Length + 200 < boundary + 64 * 1024) Add(LineText(++n), "\n");
        Add(new string('q', boundary + 64 * 1024 - 1 - sb.Length), "\r");
        while (sb.Length < boundary + 300_000) Add(LineText(++n), "\n");
        expected.Add(""); // after the last line end
        string text = sb.ToString();
        Assert.Equal("\r\n", text.Substring(boundary - 1, 2));
        Assert.Equal("\rl", text.Substring(boundary + 64 * 1024 - 1, 2));
        Assert.Equal(expected.Count, TextLines.Count(text));

        using var dir = new TempFolder();
        string path = dir.File("split.txt", Encoding.ASCII.GetBytes(text));
        int p = expected.FindIndex(l => l.StartsWith('p')) + 1, q = expected.FindIndex(l => l.StartsWith('q')) + 1;
        foreach (int target in new[] { p - 1, p, p + 1, p + 2, q, q + 1, expected.Count - 3 })
            AssertWindow(TextWindow.Load(path, target, 16 * 1024, CancellationToken.None), target, k => expected[k - 1]);
    }

    [Fact]
    public void A_lone_cr_at_the_end_of_a_block_is_a_line_end()
    {
        const int boundary = 64 * 1024;
        string first = new string('x', boundary - 1); // its '\r' is the block's last byte
        string text = first + "\rsecond\nthird\n";
        using var dir = new TempFolder();
        string path = dir.File("cr.txt", Encoding.ASCII.GetBytes(text));
        var result = TextWindow.Load(path, 2, 8 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(result);
        var lines = TextLines.Split(result!.Value.Text);
        Assert.Equal("second", lines[2 - result.Value.FirstLine]);
        Assert.Equal("third", lines[3 - result.Value.FirstLine]);
    }

    [Fact]
    public void Line_one_past_the_end_and_empty_files()
    {
        using var dir = new TempFolder();
        string small = dir.File("small.txt", Encoding.UTF8.GetBytes(Lines(10, "\n")));
        var whole = TextWindow.Load(small, 1, 64 * 1024, CancellationToken.None);
        Assert.Equal((Lines(10, "\n"), 1), whole);

        // A line past the end (the file got shorter): its last lines.
        var past = TextWindow.Load(small, 500, 64 * 1024, CancellationToken.None);
        Assert.Equal((Lines(10, "\n"), 1), past);
        string big = dir.File("big.txt", Encoding.UTF8.GetBytes(Lines(20_000, "\n")));
        var tail = TextWindow.Load(big, 90_000, 64 * 1024, CancellationToken.None);
        Assert.NotNull(tail);
        var tailLines = TextLines.Split(tail!.Value.Text);
        Assert.Equal("", tailLines[^1]);
        Assert.Equal(LineText(20_000), tailLines[^2]);
        Assert.Equal(LineText(tail.Value.FirstLine), tailLines[0]);
        Assert.True(tail.Value.Text.Length <= 32 * 1024 + 100);

        string empty = dir.File("empty.txt", []);
        Assert.Equal(("", 1), TextWindow.Load(empty, 1, 4096, CancellationToken.None));
        Assert.Null(TextWindow.Load(empty, 3, 4096, CancellationToken.None));
        Assert.Equal(("", 1), TextWindow.Load(empty, 0, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task Binary_missing_and_cancelled()
    {
        using var dir = new TempFolder();
        var bytes = Encoding.ASCII.GetBytes(Lines(100, "\n"));
        bytes[500] = 0;
        string binary = dir.File("data.bin", bytes);
        Assert.Null(TextWindow.Load(binary, 5, 4096, CancellationToken.None));
        Assert.Null(TextWindow.Load(Path.Combine(dir.Path, "missing.txt"), 5, 4096, CancellationToken.None));
        Assert.Null(await TextWindow.LoadAsync(Path.Combine(dir.Path, "no", "such", "folder.txt"), 5, 4096, CancellationToken.None));

        string big = dir.File("big.txt", Encoding.UTF8.GetBytes(Lines(100_000, "\n")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TextWindow.LoadAsync(big, 90_000, 4096, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => TextWindow.Load(big, 90_000, 4096, cts.Token));
    }

    [Fact]
    public void A_file_open_for_writing_elsewhere_can_be_read()
    {
        using var dir = new TempFolder();
        string path = dir.File("log.txt", Encoding.UTF8.GetBytes(Lines(1000, "\n")));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        AssertWindow(TextWindow.Load(path, 900, 8192, CancellationToken.None), 900, LineText);
    }

    [Fact]
    public void Lines_longer_than_half_the_window_are_left_out_before_the_target_and_cut_at_it()
    {
        string huge = new string('h', 300_000);
        string text = "first\n" + huge + "\nthird\nfourth\n" + huge + "\nsixth";
        using var dir = new TempFolder();
        string path = dir.File("long.txt", Encoding.UTF8.GetBytes(text));
        const int budget = 64 * 1024;

        // Line 3: the huge line 2 can't fit before it with its start, so the window starts at line 3.
        var third = TextWindow.Load(path, 3, budget, CancellationToken.None);
        Assert.NotNull(third);
        Assert.Equal(3, third!.Value.FirstLine);
        var lines = TextLines.Split(third.Value.Text);
        Assert.Equal("third", lines[0]);
        Assert.Equal("fourth", lines[1]);
        Assert.True(third.Value.Text.Length <= budget);

        // Line 2 itself: kept from its start and cut at the budget.
        var second = TextWindow.Load(path, 2, budget, CancellationToken.None);
        Assert.NotNull(second);
        var l2 = TextLines.Split(second!.Value.Text);
        Assert.Equal(1, second.Value.FirstLine);
        Assert.Equal("first", l2[0]);
        Assert.StartsWith(l2[1], huge, StringComparison.Ordinal);
        Assert.Equal(budget, second.Value.Text.Length);

        // Past a huge last line: nothing fits, so nothing comes back.
        Assert.Null(TextWindow.Load(dir.File("tail.txt", Encoding.UTF8.GetBytes("a\n" + huge)), 9, budget, CancellationToken.None));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // One long line of emoji: the window is cut inside it.
        string line = string.Concat(Enumerable.Repeat("😀", 40_000));
        using var dir = new TempFolder();
        string path = dir.File("emoji.txt", Encoding.UTF8.GetBytes("a\n" + line + "\nb\n"));
        for (int budget = 4096; budget < 4100; budget++)
        {
            var result = TextWindow.Load(path, 2, budget, CancellationToken.None);
            Assert.NotNull(result);
            string text = result!.Value.Text;
            Assert.False(char.IsHighSurrogate(text[^1]));
        }
    }
}

public class TextLinesTests
{
    [Theory]
    [InlineData("", new[] { "" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a", "" })]
    [InlineData("a\r\n\r\nb", new[] { "a", "", "b" })]
    [InlineData("\r", new[] { "", "" })]
    [InlineData("a\n\rb", new[] { "a", "", "b" })]
    [InlineData("a\r\rb", new[] { "a", "", "b" })]
    [InlineData("a\tb\fc", new[] { "a\tb", "c" })]
    public void Split_follows_the_rule(string text, string[] lines)
    {
        Assert.Equal(lines, TextLines.Split(text));
        Assert.Equal(lines.Length, TextLines.Count(text));
    }

    [Fact]
    public void Break_lengths()
    {
        Assert.Equal(2, TextLines.BreakLength("\r\n", 0));
        Assert.Equal(1, TextLines.BreakLength("\r\n", 1));
        Assert.Equal(1, TextLines.BreakLength("\rx", 0));
        Assert.Equal(1, TextLines.BreakLength("\r", 0));
        Assert.Equal(0, TextLines.BreakLength("x", 0));
        Assert.Equal(0, TextLines.BreakLength("x", 5));
        foreach (char c in "\n\r\v\f\u0085" + (char)0x2028 + (char)0x2029) Assert.True(TextLines.IsBreak(c));
        foreach (char c in "\t  ​\u001E\u001F") Assert.False(TextLines.IsBreak(c));
    }

    /// <summary>The text reader's sink turns exactly these line ends into '\n', so the index's line N is line N here.</summary>
    [Fact]
    public void Lines_are_the_text_readers_lines()
    {
        var random = new Random(20261021);
        string alphabet = "ab \t\n\r\v\f\u0085" + (char)0x2028 + (char)0x2029;
        for (int round = 0; round < 3000; round++)
        {
            var chars = new char[random.Next(0, 40)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            string text = new(chars);

            var sink = new ExtractSink(ExtractLimits.Local);
            // Fed in two pieces, as a reader may: "\r" and "\n" in different calls are still one line end.
            int cut = random.Next(0, text.Length + 1);
            sink.Text(text.AsSpan(0, cut));
            sink.Text(text.AsSpan(cut));
            string body = sink.ToDoc(ContentKind.Text, DocStatus.Ok, 1, 0, 0).Body;

            Assert.Equal(body.Split('\n'), TextLines.Split(text));
            Assert.Equal(body.Count(c => c == '\n') + 1, TextLines.Count(text));
        }
    }
}
