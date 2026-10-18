using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace Anything.Preview;

/// <summary>What kind of content a <see cref="PreviewResult"/> carries (decides which view the pane shows).</summary>
public enum PreviewKind
{
    /// <summary>Nothing to show (cleared).</summary>
    Empty,
    /// <summary>One bitmap, an animation (<see cref="PreviewResult.Frames"/>) or a paged document (<see cref="PreviewResult.Pages"/>).</summary>
    Image,
    /// <summary>Plain text / source code (<see cref="PreviewResult.Text"/>).</summary>
    Text,
    /// <summary>Simple structured document: paragraphs with heading levels (<see cref="PreviewResult.Document"/>).</summary>
    Document,
    /// <summary>Grid of cells: spreadsheets, CSV, archive listings (<see cref="PreviewResult.Table"/>).</summary>
    Table,
    /// <summary>Info card: icon + metadata + optional hex dump.</summary>
    Info,
    /// <summary>A registered shell IPreviewHandler should be hosted (UI thread only) — see <see cref="PreviewResult.HandlerClsid"/>.</summary>
    HostedHandler,
    /// <summary>Audio/video: the pane hosts a player; <see cref="PreviewResult.Image"/> is the poster frame / album art.</summary>
    Media,
}

/// <summary>A lazily rendered multi-page source (PDF pages, TIFF pages).</summary>
public interface IPageSource : IDisposable
{
    int Count { get; }
    /// <summary>"Page" or "Frame" — used by the navigator label.</summary>
    string Unit { get; }
    /// <summary>Renders page <paramref name="index"/> (0-based) to fit <paramref name="target"/>. Caller owns the bitmap.</summary>
    Task<Bitmap> RenderAsync(int index, Size target, CancellationToken ct);
}

public enum DocBlockStyle { Normal, Heading1, Heading2, Heading3, Title, ListItem, Caption }

/// <summary>One paragraph of a <see cref="PreviewKind.Document"/> result.</summary>
public sealed record DocBlock(string Text, DocBlockStyle Style = DocBlockStyle.Normal);

/// <summary>Cell grid for <see cref="PreviewKind.Table"/>.</summary>
public sealed class TableData
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<string[]> Rows { get; init; }
    /// <summary>Show 1-based row numbers in a header column (spreadsheets).</summary>
    public bool ShowRowNumbers { get; init; }
    /// <summary>More rows/columns existed than were loaded.</summary>
    public bool Truncated { get; init; }
    public string? Caption { get; init; }
}

/// <summary>Media facts known before playback starts.</summary>
public sealed class MediaInfo
{
    public bool IsVideo { get; init; }
    public TimeSpan? Duration { get; init; }
    public Size? FrameSize { get; init; }
}

/// <summary>
/// The output of <see cref="PreviewRenderer.RenderAsync(string, Size, CancellationToken)"/>. Owns its bitmaps and page
/// source; dispose it when it is no longer displayed.
/// </summary>
public sealed class PreviewResult : IDisposable
{
    public PreviewResult(string path, PreviewKind kind, string provider)
    {
        Path = path;
        Kind = kind;
        Provider = provider;
    }

    public string Path { get; }
    public PreviewKind Kind { get; internal set; }
    /// <summary>Which provider produced this result (e.g. "WIC", "PDF", "CorelDRAW", "ShellThumbnail").</summary>
    public string Provider { get; internal set; }
    /// <summary>Human readable format, e.g. "JPEG image", "PDF document".</summary>
    public string? FormatName { get; internal set; }

    /// <summary>Main bitmap: the image, first page, poster frame, rendered font sample, or info-card icon.</summary>
    public Bitmap? Image { get; internal set; }
    /// <summary>The image has meaningful transparency (draw a checkerboard behind it).</summary>
    public bool HasAlpha { get; internal set; }
    /// <summary>Pixel size of the source image before any decode-time downscale.</summary>
    public Size? SourcePixelSize { get; internal set; }

    /// <summary>Animation frames (Image == Frames[0]); delays in milliseconds.</summary>
    public IReadOnlyList<Bitmap>? Frames { get; internal set; }
    public IReadOnlyList<int>? FrameDelaysMs { get; internal set; }

    /// <summary>Lazy pages (PDF / multi-page TIFF). Image is page 0 already rendered.</summary>
    public IPageSource? Pages { get; internal set; }

    public int PageCount => Pages?.Count ?? Frames?.Count ?? (Image != null ? 1 : 0);

    public string? Text { get; internal set; }
    public bool TextTruncated { get; internal set; }
    public string? TextEncoding { get; internal set; }

    public IReadOnlyList<DocBlock>? Document { get; internal set; }
    /// <summary>Raw RTF for <see cref="PreviewKind.Document"/> results that came from .rtf files.</summary>
    public string? Rtf { get; internal set; }
    /// <summary>Grid for <see cref="PreviewKind.Table"/>; also the folder listing on folder info cards.</summary>
    public TableData? Table { get; internal set; }

    public Guid HandlerClsid { get; internal set; }
    public string? HandlerName { get; internal set; }

    public MediaInfo? Media { get; internal set; }

    /// <summary>Hex dump of the first bytes (binary files on the info card).</summary>
    public string? HexDump { get; internal set; }

    /// <summary>Key/value lines for the footer / info card (dimensions, pages, duration, size, dates…).</summary>
    public List<KeyValuePair<string, string>> Metadata { get; } = new();

    /// <summary>Providers that were tried and failed before this one ("provider: message").</summary>
    public List<string> Diagnostics { get; } = new();

    public void AddMeta(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Metadata.Add(new(key, value));
    }

    public string? GetMeta(string key)
    {
        foreach (var kv in Metadata) if (kv.Key == key) return kv.Value;
        return null;
    }

    /// <summary>True when the result actually carries something visible.</summary>
    public bool HasContent => Kind switch
    {
        PreviewKind.Image => Image != null,
        PreviewKind.Text => Text != null,
        PreviewKind.Document => Document is { Count: > 0 } || Rtf != null,
        PreviewKind.Table => Table != null,
        PreviewKind.HostedHandler => HandlerClsid != Guid.Empty,
        PreviewKind.Media => true,
        PreviewKind.Info => true,
        _ => false,
    };

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Frames != null)
        {
            foreach (var f in Frames) if (!ReferenceEquals(f, Image)) f.Dispose();
        }
        Image?.Dispose();
        Image = null;
        try { Pages?.Dispose(); } catch { }
        Pages = null;
    }

    public override string ToString() =>
        $"{Kind} via {Provider}" + (Image != null ? $" [{Image.Width}x{Image.Height}]" : "") +
        (PageCount > 1 ? $" pages={PageCount}" : "") + (Text != null ? $" text={Text.Length}ch" : "");
}

/// <summary>Options for <see cref="PreviewRenderer.RenderAsync(string, Size, PreviewOptions, CancellationToken)"/>.</summary>
public sealed class PreviewOptions
{
    public static readonly PreviewOptions Default = new();
    /// <summary>Return <see cref="PreviewKind.HostedHandler"/> when a shell preview handler is registered (pane only).</summary>
    public bool AllowHostedHandler { get; init; } = true;
    /// <summary>Max bytes of text to load.</summary>
    public int MaxTextBytes { get; init; } = 1024 * 1024;
}
