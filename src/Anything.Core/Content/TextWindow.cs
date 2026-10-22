using System.Buffers;
using System.Text;
using Anything.Core.Content.Extract;

namespace Anything.Core.Content;

/// <summary>
/// A window of a large text file around a line, for the preview. The app reaches it through
/// IContentIndex.LoadTextWindowAsync, which runs it inside a crash-marker slot like every on-demand read.
/// </summary>
public static class TextWindow
{
    /// <summary>How much of the file's start decides its encoding: what the text reader reads (at most 4 MB).</summary>
    internal const int DetectBytes = 4 << 20;
    const int BlockBytes = 64 * 1024;

    /// <summary>About maxBytes of decoded text centred on the 1-based line, and its first line number; null when unreadable.</summary>
    /// <remarks>
    /// The window starts at a line start and, unless the file ends first, stops at a line end (a single line longer than
    /// the window is cut). Lines are counted by <see cref="TextLines"/>, the text reader's rule, so line N here is the
    /// index's line N. A line past the end of the file gives the file's last lines. Binary files, files that can't be
    /// opened and I/O errors give null; only cancellation throws.
    /// </remarks>
    public static Task<(string Text, int FirstLine)?> LoadAsync(string fullPath, int line, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return Task.Run(() => Load(fullPath, line, maxBytes, ct), ct);
    }

    /// <summary>The synchronous body of <see cref="LoadAsync"/> (tests call it directly).</summary>
    internal static (string Text, int FirstLine)? Load(string fullPath, int line, int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[]? head = null;
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            int want = (int)Math.Min(fs.Length, DetectBytes);
            head = ArrayPool<byte>.Shared.Rent(Math.Max(want, BlockBytes));
            int got = ReadFully(fs, head.AsSpan(0, want), ct);
            var encoding = TextDecoding.Detect(head.AsSpan(0, got));
            if (encoding == null) return null;

            int unit = encoding is UnicodeEncoding ? 2 : encoding is UTF32Encoding ? 4 : 1;
            var scan = new WindowScanner(Math.Max(1, line), Math.Clamp(maxBytes, 4096, 256 << 20) / unit);
            var decoder = encoding.GetDecoder();
            char[] chars = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(BlockBytes + 16));
            try
            {
                var preamble = encoding.GetPreamble();
                int skip = preamble.Length > 0 && head.AsSpan(0, got).StartsWith(preamble) ? preamble.Length : 0;
                // The head first, a block at a time, then the rest of the file.
                for (int at = skip; at < got && !scan.Done; at += BlockBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(BlockBytes, got - at);
                    int c = decoder.GetChars(head, at, n, chars, 0, flush: false);
                    scan.Feed(chars.AsSpan(0, c));
                }
                while (!scan.Done && got == want)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = fs.Read(head, 0, BlockBytes);
                    if (n <= 0) break;
                    int c = decoder.GetChars(head, 0, n, chars, 0, flush: false);
                    scan.Feed(chars.AsSpan(0, c));
                }
                if (!scan.Done)
                {
                    int c = decoder.GetChars(head, 0, 0, chars, 0, flush: true);
                    scan.Feed(chars.AsSpan(0, c));
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
            return scan.Result();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                      or ArgumentException or NotSupportedException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            if (head != null) ArrayPool<byte>.Shared.Return(head);
        }
    }

    static int ReadFully(FileStream fs, Span<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Keeps the decoded text around the target line while the file streams past: whole lines, at most half the budget
    /// before the target line (a longer line just before it is left out), then the target line and what follows until the
    /// budget is spent. Chunks are copied whole; per line only its start or end is noted.
    /// </summary>
    internal sealed class WindowScanner(int target, int budget)
    {
        readonly int _target = Math.Max(1, target);
        readonly int _budget = Math.Max(16, budget);
        readonly StringBuilder _buf = new();
        // Before the target: positions in _buf of line starts after _start, with their line numbers.
        readonly Queue<(int At, int Line)> _starts = new();
        // From the target line on: positions in _buf where a line's text ends (where its line end begins).
        readonly List<int> _ends = [];
        int _start;           // where the kept text starts in _buf: a line start (or _buf.Length while skipping)
        int _firstLine = 1;   // line number of the line at _start
        int _line = 1;        // line the next character belongs to
        int _targetAt = target <= 1 ? 0 : -1; // position of the target line in _buf, once reached
        bool _skipping;       // the line being read, before the target, is longer than half the budget: not kept
        bool _pendingCr;      // the last character fed was '\r' (a '\n' may follow in the next chunk)

        /// <summary>The window is full; feeding more changes nothing.</summary>
        public bool Done { get; private set; }

        int Half => _budget / 2;

        /// <summary>Takes the next decoded characters.</summary>
        public void Feed(ReadOnlySpan<char> text)
        {
            if (Done || text.IsEmpty) return;
            int origin = _buf.Length;
            _buf.Append(text);
            int i = 0;
            if (_pendingCr)
            {
                _pendingCr = false;
                if (text[0] == '\n') i = 1;
                LineEnded(origin + i);
            }
            while (i < text.Length)
            {
                int b = text[i..].IndexOfAny(TextLines.Breaks);
                if (b < 0) break;
                int at = i + b;
                if (_targetAt >= 0) _ends.Add(origin + at);
                if (text[at] == '\r' && at + 1 == text.Length)
                {
                    // A '\r' at the end of the chunk: whether "\r\n" is one line end is known with the next chunk.
                    _pendingCr = true;
                    break;
                }
                i = at + (text[at] == '\r' && text[at + 1] == '\n' ? 2 : 1);
                LineEnded(origin + i);
            }
            if (_targetAt < 0) Settle();
            else if (_buf.Length - _start >= _budget) Done = true;
        }

        /// <summary>The window; null when the file ended before the target line and nothing was kept. Call after the last Feed.</summary>
        public (string Text, int FirstLine)? Result()
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                LineEnded(_buf.Length);
                if (_targetAt < 0) Settle();
            }
            if (_targetAt < 0 && (_skipping || _start >= _buf.Length)) return null;
            int end = _buf.Length;
            if (Done)
            {
                int limit = _start + _budget;
                end = Math.Min(end, limit);
                // Stop at the last line end inside the budget; a target line longer than the budget is cut.
                for (int k = _ends.Count - 1; k >= 0; k--)
                    if (_ends[k] <= limit && _ends[k] >= _targetAt) { end = _ends[k]; break; }
                if (end > _start && end < _buf.Length && char.IsHighSurrogate(_buf[end - 1]) && char.IsLowSurrogate(_buf[end])) end--;
            }
            return (_buf.ToString(_start, end - _start), _firstLine);
        }

        /// <summary>A line ended; the next one starts at <paramref name="next"/>.</summary>
        void LineEnded(int next)
        {
            _line++;
            if (_targetAt >= 0) return;
            if (_skipping)
            {
                _skipping = false;
                _starts.Clear();
                _start = next;
                _firstLine = _line;
            }
            else _starts.Enqueue((next, _line));
            if (_line == _target)
            {
                KeepWithin(next);
                if (_skipping)
                {
                    // The line before the target is too long to come with its start: the window starts at the target.
                    _skipping = false;
                    _start = next;
                    _firstLine = _line;
                }
                _targetAt = next;
                _starts.Clear();
            }
        }

        /// <summary>Moves the kept start to the first line start at most half the budget before <paramref name="end"/>.</summary>
        void KeepWithin(int end)
        {
            while (end - _start > Half && _starts.Count > 0) (_start, _firstLine) = _starts.Dequeue();
            if (end - _start > Half)
            {
                // No line starts close enough: the line being read is too long to come with its start.
                _skipping = true;
                _start = end;
            }
        }

        /// <summary>After a chunk, before the target: drops what can't be in the window and compacts the buffer now and then.</summary>
        void Settle()
        {
            if (!_skipping) KeepWithin(_buf.Length);
            else _start = _buf.Length;
            if (_start <= _budget) return;
            int shift = _start;
            _buf.Remove(0, shift);
            _start = 0;
            for (int k = _starts.Count; k > 0; k--)
            {
                var (at, line) = _starts.Dequeue();
                _starts.Enqueue((at - shift, line));
            }
        }
    }
}

/// <summary>
/// The one rule for where a line of text ends, shared by the preview's text view and <see cref="TextWindow"/>. It is the
/// text reader's rule too (ExtractSink turns each of these into '\n'), so the index's line N is line N in the preview:
/// "\r\n" is one line end, and '\n', '\r', '\v', '\f', U+0085, U+2028 and U+2029 on their own are one each.
/// </summary>
internal static class TextLines
{
    /// <summary>The characters that end a line.</summary>
    public static readonly SearchValues<char> Breaks = SearchValues.Create("\n\r\v\f\u0085\u2028\u2029");

    /// <summary>The character ends a line (alone, or as the '\r' of "\r\n").</summary>
    public static bool IsBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>Length of the line end at index: 2 for "\r\n", 1 for another line end, 0 when there is none.</summary>
    public static int BreakLength(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index >= (uint)text.Length || !IsBreak(text[index])) return 0;
        return text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
    }

    /// <summary>The lines of a text without their line ends: n line ends give n + 1 lines (an empty text is one empty line).</summary>
    public static List<string> Split(string text)
    {
        var lines = new List<string>();
        var s = text.AsSpan();
        int at = 0;
        while (true)
        {
            int b = s[at..].IndexOfAny(Breaks);
            if (b < 0) { lines.Add(text[at..]); return lines; }
            lines.Add(text.Substring(at, b));
            at += b + BreakLength(s, at + b);
        }
    }

    /// <summary>Number of lines: line ends + 1.</summary>
    public static int Count(ReadOnlySpan<char> text)
    {
        int n = 1, at = 0;
        while (true)
        {
            int b = text[at..].IndexOfAny(Breaks);
            if (b < 0) return n;
            n++;
            at += b + BreakLength(text, at + b);
        }
    }
}
