using System.Runtime.InteropServices;

namespace Anything;

/// <summary>
/// Windows 11 File Explorer's top: the tab strip in the Mica colour (the tabs, then empty space to drag the window by,
/// and the caption buttons), with the address row under it. Windows keeps the frame (rounded corners, shadow, side and
/// bottom resize borders); the client area takes the caption's place (WM_NCCALCSIZE), and hit testing (WM_NCHITTEST)
/// gives back what the title bar did: the top resize edge, moving the window from the strip and the address row's empty
/// space (double-click maximizes, right-click opens the window menu) and the caption buttons, reported as such so
/// hovering maximize shows Windows 11's snap layouts.
/// </summary>
internal sealed partial class MainForm
{
    readonly DragPanel _titleRow = new() { Dock = DockStyle.Top };
    readonly CaptionButtons _captionButtons = new();

    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
              WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2;
    const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    void BuildTitleRow()
    {
        _titleRow.BackColor = ExplorerTheme.TitleBar;
        _captionButtons.BackColor = ExplorerTheme.TitleBar;
        _titleRow.Controls.Add(_tabStrip);
        _titleRow.Controls.Add(_captionButtons);
        _titleRow.Paint += PaintTitleRow;
        _titleRow.Resize += (_, _) => PlaceCaptionButtons();
        BuildTabStrip();
    }

    /// <summary>The strip's bottom line under the caption buttons (the tab strip draws the rest).</summary>
    void PaintTitleRow(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(TabStrip.LineColor);
        e.Graphics.DrawLine(pen, 0, _titleRow.Height - 1, _titleRow.Width, _titleRow.Height - 1);
    }

    /// <summary>Explorer's tab strip is 40 high plus a 1px line under it; its caption buttons are 46 by 30, at the top.</summary>
    void ApplyTitleRowMetrics()
    {
        _titleRow.Height = LogicalToDeviceUnits(40) + 1;
        _titleRow.Padding = new Padding(0, 0, 3 * LogicalToDeviceUnits(46), 0); // the tab strip stops at the caption buttons
        _captionButtons.GlyphSize = LogicalToDeviceUnits(10);
        _captionButtons.Maximized = WindowState == FormWindowState.Maximized;
        PlaceCaptionButtons();
    }

    void PlaceCaptionButtons()
    {
        int w = 3 * LogicalToDeviceUnits(46);
        _captionButtons.SetBounds(_titleRow.ClientSize.Width - w, 0, w, LogicalToDeviceUnits(30));
    }

    /// <summary>Frame thickness at this DPI: how far a maximized window hangs over the screen edge, and the top resize band.</summary>
    int FrameThickness()
    {
        uint dpi = (uint)Math.Max(96, DeviceDpi);
        return GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    }

    /// <summary>Recalculates the frame once the window exists, so the title bar goes away immediately.</summary>
    void RemoveTitleBar() =>
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Title-bar messages; true when handled.</summary>
    bool HandleTitleBarMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
            {
                int top = Marshal.ReadInt32(m.LParam, 4); // NCCALCSIZE_PARAMS.rgrc[0].top before Windows adds the caption
                base.WndProc(ref m);
                Marshal.WriteInt32(m.LParam, 4, top + (IsZoomed(Handle) ? FrameThickness() : 0));
                m.Result = IntPtr.Zero;
                return true;
            }
            case WM_NCHITTEST:
            {
                base.WndProc(ref m);
                if ((int)m.Result != HTCLIENT) return true; // side and bottom borders stay Windows'
                var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                var pt = PointToClient(screen);
                int frame = FrameThickness();
                if (!IsZoomed(Handle) && pt.Y < frame)
                {
                    m.Result = pt.X < 2 * frame ? HTTOPLEFT : pt.X >= ClientSize.Width - 2 * frame ? HTTOPRIGHT : HTTOP;
                    return true;
                }
                int button = _captionButtons.HitTest(_captionButtons.PointToClient(screen));
                if (button != 0) m.Result = button;
                else if (pt.Y < _searchBar.Bottom) m.Result = HTCAPTION; // the strip, and the address row's gaps
                return true;
            }
            case WM_NCMOUSEMOVE:
            {
                int ht = (int)m.WParam;
                _captionButtons.Hover = CaptionButtons.IsButton(ht) ? ht : 0;
                if (_captionButtons.Pressed != 0 && _captionButtons.Pressed != ht) _captionButtons.Pressed = 0;
                if (_captionButtons.Hover != 0) TrackNonClientLeave();
                base.WndProc(ref m); // keeps Windows' snap layouts on the maximize button
                return true;
            }
            case WM_NCMOUSELEAVE:
                _captionButtons.Hover = 0;
                _captionButtons.Pressed = 0;
                base.WndProc(ref m);
                return true;
            case WM_NCLBUTTONDOWN when CaptionButtons.IsButton((int)m.WParam):
            case WM_NCLBUTTONDBLCLK when CaptionButtons.IsButton((int)m.WParam):
                _captionButtons.Pressed = (int)m.WParam;
                m.Result = IntPtr.Zero;
                return true;
            case WM_NCLBUTTONUP when CaptionButtons.IsButton((int)m.WParam):
            {
                int ht = (int)m.WParam;
                bool click = _captionButtons.Pressed == ht;
                _captionButtons.Pressed = 0;
                m.Result = IntPtr.Zero;
                if (!click) return true;
                if (ht == CaptionButtons.HTMINBUTTON) WindowState = FormWindowState.Minimized;
                else if (ht == CaptionButtons.HTMAXBUTTON) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                else Close();
                return true;
            }
        }
        return false;
    }

    void TrackNonClientLeave()
    {
        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = Handle };
        TrackMouseEvent(ref tme);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _captionButtons.Maximized = WindowState == FormWindowState.Maximized;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _captionButtons.Active = _tabStrip.Active = false;
    }

    const int SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    const int TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize; public int dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
