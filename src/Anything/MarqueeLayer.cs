using System.Runtime.InteropServices;

namespace Anything;

/// <summary>
/// A selection rectangle in a layer of its own above a control, as Explorer draws it (ExplorerFrame's items view adds a
/// separate "Marquee" element that the window composes over the items): a click-through layered window that Windows
/// composes over the control, so drawing, moving and resizing it never repaints what is under it.
/// </summary>
internal sealed class MarqueeLayer : IDisposable
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    readonly Control _over;
    NativeWindow? _window;
    IntPtr _dc, _bitmap, _oldBitmap, _bits;
    Size _capacity;
    int[] _line = [];
    bool _shown;

    public MarqueeLayer(Control over) => _over = over;

    /// <summary>The window it is drawn in (zero until first shown).</summary>
    public IntPtr Handle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// Shows <paramref name="rect"/> (the control's client coordinates) where it is inside <paramref name="clip"/>: a
    /// 1px <paramref name="border"/> on the sides that are in view around <paramref name="fill"/> (its alpha kept).
    /// </summary>
    public void Show(Rectangle rect, Rectangle clip, Color border, Color fill)
    {
        var shown = Rectangle.Intersect(rect, clip);
        if (shown.Width <= 0 || shown.Height <= 0 || !_over.IsHandleCreated) { Hide(); return; }
        if (!EnsureWindow() || !EnsureBitmap(shown.Size)) return;

        int w = shown.Width, h = shown.Height, stride = _capacity.Width * 4;
        int edge = Premultiplied(Color.FromArgb(255, border)), inside = Premultiplied(fill);
        bool top = shown.Top == rect.Top, bottom = shown.Bottom == rect.Bottom, left = shown.Left == rect.Left, right = shown.Right == rect.Right;
        if (_line.Length < w) _line = new int[_capacity.Width];
        Array.Fill(_line, edge, 0, w);
        if (top) Marshal.Copy(_line, 0, _bits, w);
        if (bottom) Marshal.Copy(_line, 0, _bits + (h - 1) * stride, w);
        Array.Fill(_line, inside, 0, w);
        if (left) _line[0] = edge;
        if (right) _line[w - 1] = edge;
        for (int y = top ? 1 : 0, end = bottom ? h - 1 : h; y < end; y++) Marshal.Copy(_line, 0, _bits + y * stride, w);

        var at = _over.PointToScreen(shown.Location);
        var dst = new POINT { X = at.X, Y = at.Y };
        var size = new SIZE { Width = w, Height = h };
        var src = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(_window!.Handle, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, ULW_ALPHA);
        if (!_shown) { ShowWindow(_window.Handle, SW_SHOWNOACTIVATE); _shown = true; }
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        if (_window != null) ShowWindow(_window.Handle, SW_HIDE);
    }

    static int Premultiplied(Color c) => c.A << 24 | c.R * c.A / 255 << 16 | c.G * c.A / 255 << 8 | c.B * c.A / 255;

    /// <summary>A popup owned by the control's window (so it stays above it), never active and never hit by the mouse.</summary>
    bool EnsureWindow()
    {
        if (_window != null) return true;
        var owner = _over.TopLevelControl ?? _over;
        if (!owner.IsHandleCreated) return false;
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            Parent = owner.Handle,
        });
        _window = window;
        return true;
    }

    /// <summary>A 32-bit top-down bitmap at least <paramref name="size"/>, made once as big as the control and kept.</summary>
    bool EnsureBitmap(Size size)
    {
        if (_bitmap != IntPtr.Zero && size.Width <= _capacity.Width && size.Height <= _capacity.Height) return true;
        FreeBitmap();
        var want = new Size(Math.Max(size.Width, _over.ClientSize.Width), Math.Max(size.Height, _over.ClientSize.Height));
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = want.Width, biHeight = -want.Height, biPlanes = 1, biBitCount = 32 };
        _bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) return false;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        _oldBitmap = SelectObject(_dc, _bitmap);
        _capacity = want;
        return true;
    }

    void FreeBitmap()
    {
        if (_dc != IntPtr.Zero) { SelectObject(_dc, _oldBitmap); DeleteDC(_dc); _dc = IntPtr.Zero; }
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _capacity = Size.Empty;
    }

    public void Dispose()
    {
        _shown = false;
        _window?.DestroyHandle();
        _window = null;
        FreeBitmap();
    }
}
