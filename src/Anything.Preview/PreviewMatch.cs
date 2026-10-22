using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace Anything.Preview;

/// <summary>Where a search matched inside the previewed file, so the pane opens there with the words highlighted; plain delegates, no Core types.</summary>
/// <param name="Key">Changes when the query's content terms change (SetMatch on the same file).</param>
/// <param name="Find">Matching ranges in a piece of displayed text; called from any thread (the pane scans tables and pages off the UI thread), so it must be thread-safe (ContentHighlighter.Find is).</param>
/// <param name="Page">1-based page or slide to show; 0 = none.</param>
/// <param name="Line">1-based line to scroll to (text); 0 = none.</param>
/// <param name="Sheet">Sheet to render (xlsx/ods); null = the first visible one.</param>
/// <param name="Banner">One line shown above the content ("Page 4: …", "Keywords: …"); null = none.</param>
/// <param name="BannerBold">Ranges of the banner drawn bold (the matched words); null = none.</param>
public sealed record PreviewMatch(string Key, Func<string, IReadOnlyList<(int Start, int Length)>> Find, int Page = 0, int Line = 0,
    string? Sheet = null, string? Banner = null, IReadOnlyList<(int Start, int Length)>? BannerBold = null)
{
    /// <summary>Match boxes on a 0-based PDF page, as rectangles in 0..1 of the shown page; null = none. Awaited off the UI thread.</summary>
    public Func<int, CancellationToken, Task<IReadOnlyList<RectangleF>>>? PageBoxes { get; init; }
    /// <summary>Text around a line deeper than the first megabyte, with its first line number; null = not needed.</summary>
    public Func<int, CancellationToken, Task<(string Text, int FirstLine)?>>? TextWindow { get; init; }
}
