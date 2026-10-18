using System;
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
/// The preview pane: shows any file or folder without opening it. Call <see cref="ShowAsync"/> whenever the selection
/// changes; stale work is cancelled and never displayed. Rendering happens off the UI thread through
/// <see cref="PreviewRenderer"/>; registered shell preview handlers (Office, Outlook, Visio…) are hosted in place.
/// </summary>
public sealed class PreviewPane : UserControl
{
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 4, 6, 2) };
    private readonly Label _name = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _sub = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, ForeColor = Color.DimGray, UseMnemonic = false };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0), Visible = false };
    private readonly Button _prev = new() { Text = "◀", Width = 30, Height = 26, FlatStyle = FlatStyle.System, TabStop = false };
    private readonly Button _next = new() { Text = "▶", Width = 30, Height = 26, FlatStyle = FlatStyle.System, TabStop = false };
    private readonly Label _page = new() { AutoSize = false, Width = 84, Height = 26, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _zoom = new() { AutoSize = false, Dock = DockStyle.Right, Width = 56, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0), ForeColor = SystemColors.GrayText, UseMnemonic = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _loading = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading…", ForeColor = Color.Gray, Visible = false };
    // What File Explorer's preview pane says when nothing is selected.
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Select a file to preview.", ForeColor = SystemColors.GrayText, UseMnemonic = false };

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
    private int _pageIndex;
    private Bitmap? _pageBitmap; // owned: currently shown page (when not page 0)
    private readonly System.Windows.Forms.Timer _loadingTimer = new() { Interval = 150 };
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000 };

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
        Controls.Add(_content);
        Controls.Add(_footer);
        Controls.Add(_header);
        _prev.Click += (_, _) => GoToPage(_pageIndex - 1);
        _next.Click += (_, _) => GoToPage(_pageIndex + 1);
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
    }

    /// <summary>The path currently shown (or being loaded); null when cleared.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Host registered shell preview handlers (Office, Outlook…). Default true.</summary>
    public bool AllowPreviewHandlers { get; set; } = true;

    /// <summary>The result currently displayed (owned by the pane — do not dispose).</summary>
    public PreviewResult? CurrentResult => _result;

    /// <summary>Raised on the UI thread after a preview is displayed.</summary>
    public event EventHandler? PreviewShown;

    /// <summary>Shows a preview of the file/folder; cancels any in-flight preview. null/"" clears the pane.</summary>
    public async Task ShowAsync(string? path)
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
        var options = new PreviewOptions { AllowHostedHandler = AllowPreviewHandlers };
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
        _mediaView?.Stop();
        _handlerHost?.Unload();
        _imageView?.Clear();
        _infoView?.Clear();
        _textView?.SetText("");
        _tableView?.SetData(null);
        if (_docView != null) _docView.Clear();
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
        _footer.Text = string.Join("   ·   ", r.Metadata.Select(kv => kv.Key + ": " + kv.Value));
        string tip = string.Join("\n", r.Metadata.Select(kv => kv.Key + ": " + kv.Value));
        if (r.Diagnostics.Count > 0) tip += "\n\nProviders that failed first:\n" + string.Join("\n", r.Diagnostics);
        _tip.SetToolTip(_footer, tip);

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
                UpdateNav();
                break;
            }
            case PreviewKind.Text:
            {
                var tv = Ensure(ref _textView, () => new TextView());
                tv.SetText(r.Text ?? "");
                ShowView(tv);
                break;
            }
            case PreviewKind.Document:
            {
                var dv = Ensure(ref _docView, () => new DocumentView());
                dv.SetDocument(r);
                ShowView(dv);
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
                    PreviewResult fallback;
                    try
                    {
                        fallback = await Task.Run(() => PreviewRenderer.RenderAsync(r.Path, target, new PreviewOptions { AllowHostedHandler = false }, ct), ct);
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
        PreviewShown?.Invoke(this, EventArgs.Empty);
    }

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
    public async void GoToPage(int index)
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
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_result?.Pages != null && _active == _imageView && ContainsFocus)
        {
            if (keyData is Keys.PageDown or Keys.Right) { GoToPage(_pageIndex + 1); return true; }
            if (keyData is Keys.PageUp or Keys.Left) { GoToPage(_pageIndex - 1); return true; }
        }
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
