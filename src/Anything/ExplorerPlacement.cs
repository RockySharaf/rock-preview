using System.Runtime.InteropServices;

namespace Anything;

/// <summary>
/// Where File Explorer would open its first window, so Rock Preview's first window opens the same way. File Explorer
/// (ExplorerFrame.dll, read with REA) first loads the window it saved last time from the per-user, all-folders "Shell"
/// view-state bag (SHGetViewStatePropertyBag): ShowCmd and the rectangle WinPos{width}x{height}x{dpi}({monitors}), in
/// work-area coordinates, for the current screen setup. With nothing saved it uses its default: 1139 x 641 at 96 DPI
/// (800 wide on a screen narrower than 1281, 600 tall on one shorter than 961, both at 96 DPI), scaled to the monitor,
/// centred in the work area, and maximized when that fills the work area.
/// </summary>
internal static class ExplorerPlacement
{
    public readonly record struct Placement(Rectangle Bounds, bool Maximized);

    /// <summary>The first window's placement: File Explorer's saved one when there is one, else its default on <paramref name="screen"/>.</summary>
    public static Placement ForFirstWindow(Screen screen) => Saved() ?? Default(screen);

    static Placement? Saved()
    {
        try
        {
            if (Screen.PrimaryScreen is not { } primary) return null;
            var iid = typeof(IPropertyBag).GUID;
            if (SHGetViewStatePropertyBag(IntPtr.Zero, "Shell", SHGVSPB_PERUSER | SHGVSPB_ALLFOLDERS, ref iid, out var unknown) < 0 || unknown is not IPropertyBag bag)
                return null;
            try
            {
                // The screen signature Explorer adds to the name: primary monitor size, DPI and the number of monitors.
                string screens = $"{primary.Bounds.Width}x{primary.Bounds.Height}x{{0}}({Screen.AllScreens.Length})";
                RECT r = default;
                bool found = false;
                foreach (uint dpi in new[] { MonitorDpi(primary), GetDpiForSystem() }.Distinct())
                    if (PSPropertyBag_ReadRECTL(bag, "WinPos" + string.Format(screens, dpi), out r) >= 0) { found = true; break; }
                if (!found || r.Right <= r.Left || r.Bottom <= r.Top) return null;
                if (PSPropertyBag_ReadDWORD(bag, "ShowCmd", out uint showCmd) < 0) showCmd = 1;
                // Work-area coordinates (as GetWindowPlacement gives them): relative to the primary work area's corner.
                var area = primary.WorkingArea;
                var bounds = new Rectangle(r.Left + area.Left - primary.Bounds.Left, r.Top + area.Top - primary.Bounds.Top, r.Right - r.Left, r.Bottom - r.Top);
                if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds))) return null;
                return new Placement(bounds, showCmd == 3 /* SW_SHOWMAXIMIZED */);
            }
            finally { Marshal.ReleaseComObject(bag); }
        }
        catch (Exception) { return null; }
    }

    static Placement Default(Screen screen)
    {
        int dpi = (int)MonitorDpi(screen);
        int width = MulDiv(screen.Bounds.Width, 96, dpi) < 1281 ? 800 : 1139;
        int height = MulDiv(screen.Bounds.Height, 96, dpi) < 961 ? 600 : 641;
        var area = screen.WorkingArea;
        width = Math.Min(MulDiv(width, dpi, 96), area.Width);
        height = Math.Min(MulDiv(height, dpi, 96), area.Height);
        if (width == area.Width && height == area.Height) return new Placement(area, Maximized: true);
        return new Placement(new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height), false);
    }

    static uint MonitorDpi(Screen screen)
    {
        var center = new POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
        IntPtr monitor = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint x, out _) >= 0 && x > 0 ? x : Math.Max(96, GetDpiForSystem());
    }

    const uint SHGVSPB_PERUSER = 0x1, SHGVSPB_ALLFOLDERS = 0x8;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetViewStatePropertyBag(IntPtr pidl, string bagName, uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object bag);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int PSPropertyBag_ReadRECTL(IPropertyBag bag, string name, out RECT value);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int PSPropertyBag_ReadDWORD(IPropertyBag bag, string name, out uint value);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("kernel32.dll")] static extern int MulDiv(int number, int numerator, int denominator); // rounds like Explorer's
}
