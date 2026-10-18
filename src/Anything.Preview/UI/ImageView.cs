using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Anything.Preview.Util;

namespace Anything.Preview.UI;

/// <summary>
/// Zoomable image surface. Layout is computed from the CURRENT client size on every paint, so the image refits after
/// any resize / re-show (no cached sizes, zero sizes are simply skipped). Fit-to-pane by default; Ctrl+wheel zooms at
/// the cursor; drag pans when zoomed; double-click toggles fit ↔ 100%. Checkerboard behind transparent images,
/// high-quality cached downscale, nearest-neighbour when magnified a lot. Animated frames supported.
/// </summary>
internal sealed class ImageView : Control
{
    private Bitmap? _image;               // not owned
    private IReadOnlyList<Bitmap>? _frames; // not owned
    private IReadOnlyList<int>? _delays;
    private int _frame;
    private readonly Timer _anim = new();
    private bool _alpha;
    private bool _fit = true;
    private float _zoom = 1f;
    private PointF _pan;
    private Point _dragStart;
    private PointF _panStart;
    private bool _dragging;
    private Bitmap? _scaled;               // owned: HQ downscaled copy of the current frame
    private Size _scaledFor;
    private Bitmap? _scaledSource;

    public event EventHandler? ZoomChanged;

    public ImageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(243, 243, 243);
        _anim.Tick += (_, _) => NextFrame();
    }

    /// <summary>Upscale small images to fill the pane (vector renders, embedded thumbnails) up to this factor.</summary>
    public float MaxFitUpscale { get; set; } = 1f;

    public Bitmap? Image => _image;
    public bool IsFit => _fit;
    public float CurrentZoom => _image == null ? 1 : Effective(out _);

    public void SetImage(Bitmap? image, IReadOnlyList<Bitmap>? frames = null, IReadOnlyList<int>? delays = null, bool alpha = false, bool keepZoom = false)
    {
        _anim.Stop();
        _image = image;
        _frames = frames is { Count: > 1 } ? frames : null;
        _delays = delays;
        _frame = 0;
        _alpha = alpha;
        DropScaled();
        if (!keepZoom)
        {
            _fit = true;
            _zoom = 1;
            _pan = PointF.Empty;
        }
        if (_frames != null) StartAnimation();
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => SetImage(null);

    public void ZoomToFit()
    {
        _fit = true;
        _pan = PointF.Empty;
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomActual(Point? at = null)
    {
        SetZoom(1f, at ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2));
    }

    private Bitmap? Current => _frames != null ? _frames[Math.Min(_frame, _frames.Count - 1)] : _image;

    private void StartAnimation()
    {
        if (_frames == null) return;
        int d = _delays != null && _frame < _delays.Count ? _delays[_frame] : 100;
        _anim.Interval = Math.Max(20, d);
        _anim.Start();
    }

    private void NextFrame()
    {
        if (_frames == null || !Visible) { _anim.Stop(); return; }
        _frame = (_frame + 1) % _frames.Count;
        _anim.Stop();
        StartAnimation();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _frames != null) StartAnimation();
        else _anim.Stop();
    }

    private float FitScale(Size img)
    {
        var cs = ClientSize;
        if (cs.Width <= 0 || cs.Height <= 0 || img.Width <= 0 || img.Height <= 0) return 1;
        float pad = 0;
        float s = Math.Min((cs.Width - pad) / img.Width, (cs.Height - pad) / img.Height);
        float maxUp = MaxFitUpscale;
        if (Math.Max(img.Width, img.Height) < 400) maxUp = Math.Max(maxUp, 2f);
        return Math.Min(s, maxUp);
    }

    private float Effective(out Size img)
    {
        var b = Current;
        img = b?.Size ?? Size.Empty;
        return _fit ? FitScale(img) : _zoom;
    }

    private RectangleF DestRect(out float z)
    {
        z = Effective(out var img);
        var cs = ClientSize;
        float w = img.Width * z, h = img.Height * z;
        ClampPan(w, h);
        return new RectangleF((cs.Width - w) / 2f + _pan.X, (cs.Height - h) / 2f + _pan.Y, w, h);
    }

    private void ClampPan(float w, float h)
    {
        var cs = ClientSize;
        float mx = Math.Max(0, (w - cs.Width) / 2f), my = Math.Max(0, (h - cs.Height) / 2f);
        _pan = new PointF(Math.Clamp(_pan.X, -mx, mx), Math.Clamp(_pan.Y, -my, my));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var bmp = Current;
        var cs = ClientSize;
        if (bmp == null || cs.Width <= 0 || cs.Height <= 0) return;
        var dest = DestRect(out float z);
        var destI = Rectangle.Round(dest);
        if (destI.Width <= 0 || destI.Height <= 0) return;
        if (_alpha) BitmapUtil.FillChecker(g, Rectangle.Intersect(destI, ClientRectangle));

        if (z < 0.999f && _frames == null)
        {
            // High-quality downscale, cached per target size (rebuilt only when the size changes).
            if (_scaled == null || _scaledFor != destI.Size || !ReferenceEquals(_scaledSource, bmp))
            {
                DropScaled();
                _scaled = BitmapUtil.Resize(bmp, destI.Size);
                _scaledFor = destI.Size;
                _scaledSource = bmp;
            }
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(_scaled, destI.Location);
            return;
        }
        g.InterpolationMode = z >= 3f ? InterpolationMode.NearestNeighbor : z > 1.001f ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (Math.Abs(z - 1f) < 0.001f)
        {
            g.DrawImage(bmp, new Rectangle(destI.X, destI.Y, bmp.Width, bmp.Height));
        }
        else
        {
            // Draw only the visible part of the source (fast when zoomed far in).
            var vis = RectangleF.Intersect(dest, ClientRectangle);
            if (vis.Width <= 0 || vis.Height <= 0) return;
            var src = new RectangleF((vis.X - dest.X) / z, (vis.Y - dest.Y) / z, vis.Width / z, vis.Height / z);
            g.DrawImage(bmp, vis, src, GraphicsUnit.Pixel);
        }
    }

    private void DropScaled()
    {
        _scaled?.Dispose();
        _scaled = null;
        _scaledSource = null;
        _scaledFor = Size.Empty;
    }

    private void SetZoom(float newZoom, Point anchor)
    {
        if (Current == null) return;
        var before = DestRect(out float z0);
        newZoom = Math.Clamp(newZoom, 0.02f, 64f);
        // Keep the image point under the anchor stationary.
        float ix = (anchor.X - before.X) / z0, iy = (anchor.Y - before.Y) / z0;
        _fit = false;
        _zoom = newZoom;
        var img = Current.Size;
        float w = img.Width * newZoom, h = img.Height * newZoom;
        var cs = ClientSize;
        float baseX = (cs.Width - w) / 2f, baseY = (cs.Height - h) / 2f;
        _pan = new PointF(anchor.X - ix * newZoom - baseX, anchor.Y - iy * newZoom - baseY);
        ClampPan(w, h);
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Current == null) return;
        if ((ModifierKeys & Keys.Control) != 0)
        {
            float z = Effective(out _);
            float f = (float)Math.Pow(1.2, e.Delta / 120.0);
            SetZoom(z * f, e.Location);
            if (e is HandledMouseEventArgs h) h.Handled = true;
            return;
        }
        // Plain wheel pans vertically when zoomed in.
        var dest = DestRect(out _);
        if (dest.Height > ClientSize.Height)
        {
            _pan = new PointF(_pan.X, _pan.Y + e.Delta / 2f);
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        var dest = DestRect(out _);
        if (dest.Width > ClientSize.Width || dest.Height > ClientSize.Height)
        {
            _dragging = true;
            _dragStart = e.Location;
            _panStart = _pan;
            Cursor = Cursors.SizeAll;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _pan = new PointF(_panStart.X + e.X - _dragStart.X, _panStart.Y + e.Y - _dragStart.Y);
            Invalidate();
        }
        else
        {
            var dest = DestRect(out _);
            Cursor = dest.Width > ClientSize.Width || dest.Height > ClientSize.Height ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Current == null) return;
        if (_fit && Math.Abs(Effective(out _) - 1f) > 0.01f) ZoomActual(e.Location);
        else ZoomToFit();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode is Keys.D0 or Keys.NumPad0) { ZoomToFit(); e.Handled = true; }
        else if (e.Control && e.KeyCode is Keys.D1 or Keys.NumPad1) { ZoomActual(); e.Handled = true; }
        else if (e.Control && e.KeyCode is Keys.Oemplus or Keys.Add) { SetZoom(Effective(out _) * 1.25f, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); e.Handled = true; }
        else if (e.Control && e.KeyCode is Keys.OemMinus or Keys.Subtract) { SetZoom(Effective(out _) / 1.25f, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); e.Handled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            DropScaled();
        }
        base.Dispose(disposing);
    }
}
