using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Anything.Preview.Interop;
using Anything.Preview.UI;

namespace Anything.Preview;

/// <summary>
/// The preview pane: shows any file or folder without opening it. Call <see cref="ShowAsync(string?)"/> whenever the
/// selection changes; stale work is cancelled and never displayed. Rendering happens off the UI thread through
/// <see cref="PreviewRenderer"/>; registered shell preview handlers (Office, Outlook, Visio…) are hosted in place.
/// With a <see cref="PreviewMatch"/> (a search matched inside the file) the pane opens at the match with the words
/// highlighted: text at its line, Office files as their text, workbooks at the sheet, PDFs at the page with boxes, and
/// a banner for everything else. Without one it behaves exactly as it always did.
/// </summary>
public sealed class PreviewPane : UserControl
{
    // The footer's entry for what part of a text file is shown: the head's "first 1.0 MB of 21 MB", or the lines of a
    // window loaded around a match deeper than that.
    private const string ShownKey = "Shown";

    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 4, 6, 2) };
    private readonly Label _name = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _sub = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, ForeColor = Color.DimGray, UseMnemonic = false };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0), Visible = false };
    private readonly Button _prev = new() { Text = "◀", Width = 30, Height = 26, FlatStyle = FlatStyle.System, TabStop = false };
    private readonly Button _next = new() { Text = "▶", Width = 30, Height = 26, FlatStyle = FlatStyle.System, TabStop = false };
    private readonly Label _page = new() { AutoSize = false, Width = 84, Height = 26, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _zoom = new() { AutoSize = false, Dock = DockStyle.Right, Width = 56, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0), ForeColor = SystemColors.GrayText, UseMnemonic = false };
    private readonly MatchBanner _banner = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _loading = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading…", ForeColor = Color.Gray, Visible = false };
    // What File Explorer's preview pane says when nothing is selected.
    private readonly Label _empty = new EmptyPreview() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Select a file to preview.", ForeColor = SystemColors.GrayText, UseMnemonic = false };

    // Views are created lazily and reused.
    private ImageView? _imageView;
    private TextView? _textView;
    private DocumentView? _docView;
    private TableView? _tableView;
    private InfoView? _infoView;
    private MediaView? _mediaView;
    private PreviewHandlerHost? _handlerHost;
    private Control? _active;

    private PreviewResult? _result;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _pageCts;
    private int _generation;
    private int _pageIndex;      // the page asked for (the navigator shows it while it renders)
    private int _shownPage = -1; // the page whose image is on screen; -1 = no paged image
    private Bitmap? _pageBitmap; // owned: currently shown page (when not page 0)
    private readonly System.Windows.Forms.Timer _loadingTimer = new() { Interval = 150 };
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000 };

    // The search match shown (null = none), and how the current result was rendered for it.
    private PreviewMatch? _match;
    private bool _showOriginal;            // "Show original" was chosen for this file: its own preview, no highlights
    private PreviewOptions? _renderedWith; // the options of the current result, or of the render in flight
    private CancellationTokenSource? _matchCts; // the async parts of showing a match (text window, page jump, boxes)
    private int _matchGeneration;
    private int _boxesPage = -1;           // the PDF page _boxes belong to
    private IReadOnlyList<RectangleF>? _boxes;
    private int _headLines;                // lines of the text head (the file's first MaxTextBytes)
    private bool _windowShown;             // the text view shows a window around the match instead of the head

    public PreviewPane()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        _name.Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        _nav.Controls.AddRange(new Control[] { _prev, _page, _next });
        _header.Controls.Add(_name);
        _header.Controls.Add(_sub);
        _header.Controls.Add(_zoom);
        _header.Controls.Add(_nav);
        _content.Controls.Add(_loading);
        _content.Controls.Add(_empty);
        // Docking goes from the last control added: header on top, footer at the bottom, the banner under the header.
        Controls.Add(_content);
        Controls.Add(_banner);
        Controls.Add(_footer);
        Controls.Add(_header);
        _prev.Click += (_, _) => GoToPage(_pageIndex - 1);
        _next.Click += (_, _) => GoToPage(_pageIndex + 1);
        _banner.LinkClicked += (_, _) => ToggleOriginal();
        _loadingTimer.Tick += (_, _) => { _loadingTimer.Stop(); _loading.Visible = true; _loading.BringToFront(); };
        SetEmpty(true);
    }

    /// <summary>Nothing selected: only the "Select a file to preview." message, like File Explorer's pane.</summary>
    private void SetEmpty(bool empty)
    {
        _empty.Visible = empty;
        if (empty) _empty.BringToFront();
        _header.Visible = !empty;
        _footer.Visible = !empty;
        if (empty) _banner.Set(null, null, null);
    }

    /// <summary>The path currently shown (or being loaded); null when cleared.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Host registered shell preview handlers (Office, Outlook…). Default true.</summary>
    public bool AllowPreviewHandlers { get; set; } = true;

    /// <summary>The result currently displayed (owned by the pane — do not dispose).</summary>
    public PreviewResult? CurrentResult => _result;

    /// <summary>The search match shown in the current file; null when none.</summary>
    public PreviewMatch? CurrentMatch => _match;

    /// <summary>Raised on the UI thread after a preview is displayed.</summary>
    public event EventHandler? PreviewShown;

    /// <summary>Shows a preview of the file/folder; cancels any in-flight preview. null/"" clears the pane.</summary>
    public Task ShowAsync(string? path) => ShowAsync(path, null);

    /// <summary>
    /// Shows a preview of the file/folder opened at a search match (null = no match: exactly the plain preview); cancels
    /// any in-flight preview. null/"" clears the pane. For the same file with another query, use <see cref="SetMatch"/>.
    /// </summary>
    public Task ShowAsync(string? path, PreviewMatch? match)
    {
        if (IsDisposed) return Task.CompletedTask;
        if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) _showOriginal = false;
        _match = match;
        return RenderAsync(path);
    }

    /// <summary>
    /// The query changed while the same file stays selected: highlights the new match (null = none) in what is shown, or
    /// renders the file again when the match needs another form (its text instead of a preview handler, another sheet;
    /// or, with no match left, the file's own preview back).
    /// </summary>
    public void SetMatch(PreviewMatch? match)
    {
        if (IsDisposed) return;
        var old = _match;
        _match = match;
        if (CurrentPath == null) return;
        if (SameMatch(old, match)) return;
        var wanted = OptionsFor(AllowPreviewHandlers);
        if (_result == null)
        {
            // Still rendering: the display applies the new match, unless it needs other options.
            if (_renderedWith == null || DiffersForRender(_renderedWith, wanted)) _ = RenderAsync(CurrentPath);
            return;
        }
        if (_renderedWith != null && NeedsRender(_result, _renderedWith, wanted))
        {
            _ = RenderAsync(CurrentPath);
            return;
        }
        ApplyMatch(_result, _generation);
    }

    /// <summary>Moves to the next (or previous) highlighted match in the shown text, document or table; false when there is none.</summary>
    public bool FindNextMatch(bool backwards = false)
    {
        if (_match == null || _result == null) return false;
        if (_active != null && _active == _textView) return _textView.FindNext(backwards);
        if (_active != null && _active == _docView) return _docView.FindNext(backwards);
        if (_active != null && _active == _tableView) return _tableView.FindNext(backwards);
        return false;
    }

    private static bool SameMatch(PreviewMatch? a, PreviewMatch? b) =>
        a == null ? b == null
        : b != null && a.Key == b.Key && a.Page == b.Page && a.Line == b.Line && a.Sheet == b.Sheet && a.Banner == b.Banner;

    private PreviewOptions OptionsFor(bool allowHandler) => new()
    {
        AllowHostedHandler = allowHandler,
        PreferBuiltInText = _match != null && !_showOriginal,
        Sheet = _match?.Sheet,
    };

    private static bool DiffersForRender(PreviewOptions a, PreviewOptions b) =>
        a.PreferBuiltInText != b.PreferBuiltInText || !string.Equals(a.Sheet, b.Sheet, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the shown result would look different with the wanted options.</summary>
    private static bool NeedsRender(PreviewResult r, PreviewOptions rendered, PreviewOptions wanted)
    {
        if (rendered.PreferBuiltInText != wanted.PreferBuiltInText)
        {
            // A match arrived for a file shown plainly: Office, RTF and JSON previews change to text.
            if (wanted.PreferBuiltInText) return PreviewRenderer.PrefersTextForMatch(r.Path);
            // The match went away: back to exactly the plain preview.
            if (r.ShapedByMatch || r.SheetFromOptions) return true;
        }
        if (r.Sheet != null && !string.Equals(r.Sheet, wanted.Sheet, StringComparison.OrdinalIgnoreCase))
            return wanted.Sheet != null || r.SheetFromOptions;
        return false;
    }

    /// <summary>"Show original" and back: the file's own preview without highlights, or its text with them (the banner's link).</summary>
    internal void ToggleOriginal()
    {
        if (CurrentPath == null || _match == null) return;
        _showOriginal = !_showOriginal;
        _ = RenderAsync(CurrentPath);
    }

    private async Task RenderAsync(string? path)
    {
        if (IsDisposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        int gen = ++_generation;
        if (string.IsNullOrEmpty(path))
        {
            Clear();
            return;
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        CurrentPath = path;
        SetEmpty(false);
        ResetContent();
        _name.Text = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;
        _sub.Text = "";
        _footer.Text = "";
        _loadingTimer.Stop();
        _loadingTimer.Start();
        var target = TargetSize();
        var options = OptionsFor(AllowPreviewHandlers);
        _renderedWith = options;
        PreviewResult result;
        try
        {
            result = await Task.Run(() => PreviewRenderer.RenderAsync(path, target, options, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (gen != _generation || IsDisposed) return;
            result = new PreviewResult(path, PreviewKind.Info, "Error") { FormatName = "Preview failed" };
            result.AddMeta("Error", ex.Message);
        }
        if (gen != _generation || cts.IsCancellationRequested || IsDisposed)
        {
            result.Dispose();
            return;
        }
        await DisplayAsync(result, gen, cts.Token);
    }

    /// <summary>Clears the pane and releases everything (media, handlers, bitmaps).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _generation++;
        CurrentPath = null;
        _match = null;
        _showOriginal = false;
        _renderedWith = null;
        ResetContent();
        _name.Text = "";
        _sub.Text = "";
        _footer.Text = "";
        SetEmpty(true);
    }

    private Size TargetSize()
    {
        // Physical pixels of the content area; a hidden/collapsed pane reports 0 → the renderer substitutes a default.
        var s = _content.ClientSize;
        return new Size(Math.Max(0, s.Width), Math.Max(0, s.Height));
    }

    private void ResetContent()
    {
        _loadingTimer.Stop();
        _loading.Visible = false;
        _pageCts?.Cancel();
        _pageCts = null;
        _shownPage = -1;
        CancelMatchWork();
        _boxes = null;
        _boxesPage = -1;
        _windowShown = false;
        _headLines = 0;
        _banner.Set(null, null, null);
        _mediaView?.Stop();
        _handlerHost?.Unload();
        _imageView?.Clear();
        _infoView?.Clear();
        _textView?.SetText("");
        _tableView?.SetData(null);
        _docView?.Reset();
        _pageBitmap?.Dispose();
        _pageBitmap = null;
        _result?.Dispose();
        _result = null;
        _nav.Visible = false;
        _zoom.Text = "";
        ShowView(null);
    }

    private T Ensure<T>(ref T? field, Func<T> create) where T : Control
    {
        if (field == null)
        {
            field = create();
            field.Dock = DockStyle.Fill;
            field.Visible = false;
            _content.Controls.Add(field);
        }
        return field;
    }

    private void ShowView(Control? view)
    {
        if (_active != null && _active != view) _active.Visible = false;
        _active = view;
        if (view != null)
        {
            view.Visible = true;
            view.BringToFront();
        }
    }

    private async Task DisplayAsync(PreviewResult r, int gen, CancellationToken ct)
    {
        _loadingTimer.Stop();
        _loading.Visible = false;
        _result = r;
        _sub.Text = string.Join("  ·  ", new[] { r.FormatName, ProviderLabel(r) }.Where(s => !string.IsNullOrEmpty(s)));
        SetFooter(r, window: null);

        switch (r.Kind)
        {
            case PreviewKind.Image:
            {
                var iv = Ensure(ref _imageView, () =>
                {
                    var v = new ImageView();
                    v.ZoomChanged += (_, _) => UpdateZoomLabel();
                    return v;
                });
                iv.MaxFitUpscale = r.Provider is "DXF" or "PDF" or "Metafile" or "Illustrator (PDF)" ? 4f : 1f;
                iv.SetImage(r.Image, r.Frames, r.FrameDelaysMs, r.HasAlpha);
                ShowView(iv);
                _pageIndex = 0;
                _shownPage = 0;
                UpdateNav();
                break;
            }
            case PreviewKind.Text:
            {
                var tv = Ensure(ref _textView, () => new TextView());
                tv.SetText(r.Text ?? "");
                _headLines = tv.LineCount;
                ShowView(tv);
                break;
            }
            case PreviewKind.Document:
            {
                var dv = Ensure(ref _docView, () => new DocumentView());
                // Shown (laid out at the pane's width) before the text goes in: RichEdit wraps a document set while
                // the view still has its first, tiny size at that size and re-wraps it later in the background,
                // which would move a match scrolled into view meanwhile.
                ShowView(dv);
                dv.SetDocument(r, _match?.Find);
                break;
            }
            case PreviewKind.Table:
            {
                var tv = Ensure(ref _tableView, () => new TableView());
                tv.SetData(r.Table);
                ShowView(tv);
                break;
            }
            case PreviewKind.Media:
            {
                var mv = Ensure(ref _mediaView, () =>
                {
                    var v = new MediaView();
                    v.StatusChanged += (_, s) => { if (s.StartsWith("Cannot", StringComparison.Ordinal)) _footer.Text = s + "   ·   " + _footer.Text; };
                    return v;
                });
                ShowView(mv);
                mv.Open(r.Path, r);
                break;
            }
            case PreviewKind.HostedHandler:
            {
                var host = Ensure(ref _handlerHost, () => new PreviewHandlerHost());
                ShowView(host);
                host.Update();
                bool ok = host.Load(r.Path, r.HandlerClsid);
                if (gen != _generation) { host.Unload(); return; }
                if (!ok)
                {
                    // Handler failed: fall back to the built-in providers.
                    string why = host.Error ?? "failed";
                    var target = TargetSize();
                    var options = OptionsFor(allowHandler: false);
                    _renderedWith = options;
                    PreviewResult fallback;
                    try
                    {
                        fallback = await Task.Run(() => PreviewRenderer.RenderAsync(r.Path, target, options, ct), ct);
                    }
                    catch (OperationCanceledException) { return; }
                    if (gen != _generation) { fallback.Dispose(); return; }
                    fallback.Diagnostics.Insert(0, $"PreviewHandler ({r.HandlerName}): {why}");
                    r.Dispose();
                    _result = null;
                    await DisplayAsync(fallback, gen, ct);
                    return;
                }
                break;
            }
            default:
            {
                var info = Ensure(ref _infoView, () => new InfoView());
                info.SetResult(r);
                ShowView(info);
                break;
            }
        }
        ApplyMatch(r, gen);
        PreviewShown?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ the match

    private void CancelMatchWork()
    {
        _matchCts?.Cancel();
        _matchCts?.Dispose();
        _matchCts = null;
        _matchGeneration++;
    }

    /// <summary>Shows the current match (or none) in the displayed result: banner, highlights, scroll, page, boxes.</summary>
    private void ApplyMatch(PreviewResult r, int gen)
    {
        CancelMatchWork();
        var m = _match;
        int mg = _matchGeneration;
        UpdateBanner(r, m);
        if (_active != null && _active == _textView && r.Kind == PreviewKind.Text) ApplyTextMatch(r, m, gen, mg);
        else if (_active != null && _active == _docView && r.Kind == PreviewKind.Document)
        {
            // pptx text has a "Slide N" heading before each slide (RenderPptxText).
            string? anchor = m is { Page: > 0 } && r.FormatName == "PowerPoint presentation" ? $"Slide {m.Page}" : null;
            _docView.Highlight(m?.Find, anchor);
        }
        else if (_active != null && _active == _tableView && r.Kind == PreviewKind.Table) _tableView.Highlight(m?.Find);
        else if (_active != null && _active == _imageView && r.Kind == PreviewKind.Image && r.Provider is "PDF" or "Illustrator (PDF)")
        {
            _boxes = null;
            _boxesPage = -1;
            _imageView.SetOverlay(null);
            if (m == null) return;
            int page = Math.Clamp(m.Page > 0 ? m.Page - 1 : 0, 0, Math.Max(0, (r.Pages?.Count ?? 1) - 1));
            var cts = _matchCts = new CancellationTokenSource();
            _ = ShowPdfMatchAsync(r, m, page, gen, mg, cts.Token);
        }
    }

    private void UpdateBanner(PreviewResult r, PreviewMatch? m)
    {
        if (m == null) { _banner.Set(null, null, null); return; }
        if (r.InsteadOfOriginal && !_showOriginal)
        {
            string text = m.Banner ?? "Showing the text with matches highlighted";
            _banner.Set(text, m.Banner != null ? m.BannerBold : null, "Show original",
                "Show the file's own preview (the matches aren't highlighted there)");
        }
        else if (_showOriginal) _banner.Set(m.Banner, m.BannerBold, "Highlight matches", "Show the text with the matches highlighted");
        else _banner.Set(m.Banner, m.BannerBold, null);
    }

    private bool MatchIsCurrent(PreviewResult r, int gen, int mg) =>
        !IsDisposed && gen == _generation && mg == _matchGeneration && ReferenceEquals(r, _result);

    private void ApplyTextMatch(PreviewResult r, PreviewMatch? m, int gen, int mg)
    {
        var tv = _textView!;
        if (m == null)
        {
            if (_windowShown) ShowHead(r);
            tv.SetHighlights(null);
            return;
        }
        // The index numbers the lines of the text it read: an HTML or RTF file shown as its source has other lines, so
        // there the first match is shown instead (the app passes no line for them anyway).
        int line = LinesAreTheIndexs(r.Path) ? m.Line : 0;
        // The head's last line may be cut off when the file is longer than what was loaded.
        bool inHead = line <= 0 || !r.TextTruncated || line < _headLines;
        if (inHead || m.TextWindow == null)
        {
            if (_windowShown) ShowHead(r);
            tv.SetHighlights(m.Find);
            if (line > 0) tv.ScrollToLine(Math.Min(line, _headLines));
            else tv.ScrollToFirstMatch();
            return;
        }
        if (_windowShown && line >= tv.FirstLineNumber && line < tv.FirstLineNumber + tv.LineCount - 1)
        {
            tv.SetHighlights(m.Find);
            tv.ScrollToLine(line);
            return;
        }
        // Deeper than the head: highlight the head meanwhile, then show a window around the line.
        tv.SetHighlights(m.Find);
        var cts = _matchCts = new CancellationTokenSource();
        _ = ShowTextWindowAsync(r, m, line, gen, mg, cts.Token);
    }

    private static bool LinesAreTheIndexs(string path) =>
        PreviewRenderer.GetExtension(path) is not (".htm" or ".html" or ".xhtml" or ".shtml" or ".rtf");

    private void ShowHead(PreviewResult r)
    {
        _textView!.SetText(r.Text ?? "");
        _windowShown = false;
        SetFooter(r, window: null);
    }

    /// <summary>
    /// The footer and its tooltip from the result's facts. With <paramref name="window"/> (a text window around a deep
    /// match), the window's lines replace the head's "Lines" and "Shown", which describe the first megabyte only.
    /// </summary>
    private void SetFooter(PreviewResult r, string? window)
    {
        var facts = r.Metadata.Select(kv => kv.Key + ": " + kv.Value);
        if (window != null)
            facts = new[] { ShownKey + ": " + window }.Concat(r.Metadata.Where(kv => kv.Key is not ("Lines" or ShownKey)).Select(kv => kv.Key + ": " + kv.Value));
        var list = facts.ToList();
        _footer.Text = string.Join("   ·   ", list);
        string tip = string.Join("\n", list);
        if (r.Diagnostics.Count > 0) tip += "\n\nProviders that failed first:\n" + string.Join("\n", r.Diagnostics);
        _tip.SetToolTip(_footer, tip);
    }

    private async Task ShowTextWindowAsync(PreviewResult r, PreviewMatch m, int line, int gen, int mg, CancellationToken ct)
    {
        (string Text, int FirstLine)? window;
        try
        {
            window = await Task.Run(() => m.TextWindow!(line, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException) { window = null; }
        if (!MatchIsCurrent(r, gen, mg) || _textView == null) return;
        var tv = _textView;
        if (window is { } w && w.Text.Length > 0)
        {
            tv.SetText(w.Text, w.FirstLine);
            _windowShown = true;
            int last = w.FirstLine + tv.LineCount - 1;
            SetFooter(r, $"lines {w.FirstLine:N0}–{last:N0}, around the match");
            tv.SetHighlights(m.Find);
            tv.ScrollToLine(line);
        }
        else tv.ScrollToLine(_headLines);
    }

    private async Task ShowPdfMatchAsync(PreviewResult r, PreviewMatch m, int page, int gen, int mg, CancellationToken ct)
    {
        // A page that can't be rendered leaves the shown page (and the navigator) where it was; the boxes then wait for
        // their page (ShowBoxes) instead of being drawn on another one.
        if (page != _pageIndex) await GoToPageAsync(page);
        if (m.PageBoxes == null || !MatchIsCurrent(r, gen, mg)) return;
        IReadOnlyList<RectangleF>? boxes;
        try
        {
            boxes = await Task.Run(() => m.PageBoxes(page, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException) { return; } // no boxes: the banner still says where
        if (!MatchIsCurrent(r, gen, mg) || boxes is not { Count: > 0 }) return;
        _boxes = boxes;
        _boxesPage = page;
        if (_shownPage == page) _imageView?.SetOverlay(boxes);
    }

    /// <summary>What the match looks like on screen, in one line (PreviewHarness --match).</summary>
    internal string DescribeMatch()
    {
        var parts = new List<string> { "view=" + (_active?.GetType().Name ?? "none") };
        if (_match != null) parts.Add($"match={_match.Key} page={_match.Page} line={_match.Line} sheet={_match.Sheet ?? "-"}");
        if (_banner.HasContent) parts.Add($"banner='{_banner.BannerText}'" + (_banner.BoldText is { Length: > 0 } b ? $" bold='{b}'" : "")
            + (_banner.LinkText is { } l ? $" link='{l}'" : ""));
        if (_result != null) parts.Add($"result={_result.Kind}/{_result.Provider}" + (_result.InsteadOfOriginal ? " instead-of-original" : "")
            + (_result.Sheet != null ? $" sheet={_result.Sheet}" : "") + (_result.Pages != null ? $" pages={_result.Pages.Count}" : ""));
        if (_active != null && _active == _textView)
            parts.Add($"first-line={_textView.FirstLineNumber} lines={_textView.LineCount} top={_textView.TopLine} current={_textView.CurrentMatch} window={_windowShown}"
                + $" encoding={_result?.TextEncoding} footer='{_footer.Text}'");
        if (_active != null && _active == _docView)
            parts.Add($"doc={_docView.MatchState} hit='{_docView.CurrentHitText}' batch={DocumentView.LastBatchMs}ms highlight={DocumentView.LastHighlightMs}ms");
        if (_active != null && _active == _tableView) parts.Add($"table={_tableView.MatchState}");
        if (_active != null && _active == _imageView) parts.Add($"page-index={_pageIndex} shown-page={_shownPage} boxes={_imageView.OverlayCount}");
        return string.Join("; ", parts);
    }

    /// <summary>The view on screen (diagnostics: PreviewHarness --match checks it with the control's own API).</summary>
    internal Control? ActiveView => _active;

    // ------------------------------------------------------------------

    private static string ProviderLabel(PreviewResult r) =>
        r.Kind == PreviewKind.HostedHandler ? "via " + (r.HandlerName ?? "preview handler") : "via " + r.Provider;

    private void UpdateZoomLabel()
    {
        if (_imageView?.Image == null || _active != _imageView) { _zoom.Text = ""; return; }
        _zoom.Text = (_imageView.CurrentZoom * 100).ToString("0") + "%";
    }

    private void UpdateNav()
    {
        int count = _result?.Pages?.Count ?? 0;
        _nav.Visible = count > 1;
        if (count > 1)
        {
            string unit = _result!.Pages!.Unit;
            _page.Text = $"{unit} {_pageIndex + 1}/{count}";
            _prev.Enabled = _pageIndex > 0;
            _next.Enabled = _pageIndex < count - 1;
        }
        UpdateZoomLabel();
    }

    /// <summary>Moves to page/frame <paramref name="index"/> of a multi-page document (0-based).</summary>
    public async void GoToPage(int index) => await GoToPageAsync(index);

    private async Task GoToPageAsync(int index)
    {
        var r = _result;
        var pages = r?.Pages;
        if (r == null || pages == null || _imageView == null) return;
        index = Math.Clamp(index, 0, pages.Count - 1);
        if (index == _pageIndex) return;
        _pageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pageCts = cts;
        _pageIndex = index;
        UpdateNav();
        int gen = _generation;
        if (index == 0)
        {
            _imageView.SetImage(r.Image, alpha: r.HasAlpha);
            _pageBitmap?.Dispose();
            _pageBitmap = null;
            _shownPage = 0;
            ShowBoxes(0);
            return;
        }
        Bitmap bmp;
        try
        {
            var target = TargetSize();
            bmp = await pages.RenderAsync(index, target, cts.Token);
        }
        catch (Exception)
        {
            // The page can't be rendered (or this jump was given up): unless another jump took over, the navigator goes
            // back to the page still on screen.
            if (gen == _generation && ReferenceEquals(cts, _pageCts) && ReferenceEquals(r, _result) && _shownPage >= 0)
            {
                _pageIndex = _shownPage;
                UpdateNav();
            }
            return;
        }
        if (gen != _generation || cts.IsCancellationRequested || !ReferenceEquals(r, _result))
        {
            bmp.Dispose();
            return;
        }
        _imageView.SetImage(bmp, alpha: r.HasAlpha);
        _pageBitmap?.Dispose();
        _pageBitmap = bmp;
        _shownPage = index;
        ShowBoxes(index);
    }

    /// <summary>The match boxes again when their page is shown (a new page image clears them).</summary>
    private void ShowBoxes(int page)
    {
        if (_boxes != null && _boxesPage == page) _imageView?.SetOverlay(_boxes);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_result?.Pages is { Count: > 1 } && _active == _imageView && ContainsFocus)
        {
            if (keyData is Keys.PageDown or Keys.Right) { GoToPage(_pageIndex + 1); return true; }
            if (keyData is Keys.PageUp or Keys.Left) { GoToPage(_pageIndex - 1); return true; }
        }
        // F3 / Shift+F3: the next or previous match, before the window's own F3 gets it.
        if ((keyData & Keys.KeyCode) == Keys.F3 && (keyData & (Keys.Control | Keys.Alt)) == 0 && ContainsFocus
            && FindNextMatch((keyData & Keys.Shift) != 0))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        UpdateZoomLabel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _generation++;
            ResetContent();
            _loadingTimer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
