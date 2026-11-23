using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Search;

namespace Anything;

/// <summary>
/// Help › Search Syntax (F1). One for the whole program and modeless, so a search can be typed while reading: F1 again
/// brings it to the front, and moves it to the window that asked when that is another one. It opens under the search
/// box, lined up with its right edge, inside that window, and never off the screen. Its size and place are kept for
/// this session only. Esc closes it.
/// </summary>
internal sealed class SearchSyntaxForm : Form
{
    const int WantedWidth = 720, MinWidth = 420, MinHeight = 240, Gap = 8; // logical pixels (client area)

    static SearchSyntaxForm? s_open;
    // Kept for this session, not in the settings file: the client size in logical pixels, and where it last was for
    // which window (a reopened help goes back there while that window is on the same screen).
    static Size? s_logicalSize;
    static (WeakReference<Form> Owner, Rectangle Bounds, string Screen)? s_place;

    readonly SearchHelpView _view = new() { Dock = DockStyle.Fill };
    Form? _ownerForm;
    Func<Rectangle?> _searchBox = () => null;
    bool _hiddenWithOwner;

    SearchSyntaxForm()
    {
        Text = "Search Syntax - Rock Preview";
        AutoScaleMode = AutoScaleMode.None; // everything is in pixels from DeviceDpi, as in Settings
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = ExplorerTheme.TitleBar;
        ForeColor = ExplorerTheme.Text;
        KeyPreview = true;
        Controls.Add(_view);
    }

    /// <summary>Opens the help for <paramref name="owner"/>, or brings the open one to it.</summary>
    public static void ShowSyntax(MainForm owner) => Show(owner, owner.SearchBoxScreenBounds, owner.TryInSearchBox);

    /// <param name="searchBox">The owner's search box on the screen, or null when it has none showing.</param>
    /// <param name="tryText">Puts a double-clicked name or example in the owner's search box; null: nothing is tried.</param>
    internal static SearchSyntaxForm Show(Form owner, Func<Rectangle?> searchBox, Action<string, HelpInsert>? tryText)
    {
        if (s_open is { IsDisposed: false } open)
        {
            if (!ReferenceEquals(open._ownerForm, owner))
            {
                open.Attach(owner, searchBox, tryText);
                open.MoveTo(open.PlaceFor(owner, open.LogicalClientSize()), owner.DeviceDpi);
            }
            if (!open.Visible) open.Show(owner);
            open.Activate();
            return open;
        }
        var help = new SearchSyntaxForm();
        help.Attach(owner, searchBox, tryText);
        // Placed before the window exists, so Windows makes it on that screen at that screen's scaling.
        help.Bounds = help.Remembered(owner) ?? help.PlaceFor(owner, s_logicalSize ?? new Size(WantedWidth, 0));
        help.MinimumSize = help.MinimumFor(owner.DeviceDpi, help.Size);
        s_open = help;
        help.Show(owner);
        return help;
    }

    /// <summary>
    /// Closes the help when it belongs to <paramref name="owner"/>. For a window closed into the background (Run in
    /// background hides it instead of closing it): the user closed it, so its help must not come back when it is shown
    /// again; only the hotkey's hiding brings the help back with its window.
    /// </summary>
    internal static void CloseFor(Form owner)
    {
        if (s_open is { IsDisposed: false } help && ReferenceEquals(help._ownerForm, owner)) help.Close();
    }

    /// <summary>Whether <paramref name="hwnd"/> is the help shown for <paramref name="owner"/> (the hotkey hides both).</summary>
    internal static bool IsHelpOf(Form owner, IntPtr hwnd) =>
        s_open is { IsDisposed: false, Visible: true, IsHandleCreated: true } help && help.Handle == hwnd && ReferenceEquals(help._ownerForm, owner);

    /// <summary>The open help, for the window probe.</summary>
    internal static SearchSyntaxForm? Current => s_open is { IsDisposed: false } f ? f : null;

    internal SearchHelpView View => _view;

    void Attach(Form owner, Func<Rectangle?> searchBox, Action<string, HelpInsert>? tryText)
    {
        if (_ownerForm is { } old)
        {
            old.VisibleChanged -= OwnerVisibleChanged;
            old.FormClosed -= OwnerClosed;
        }
        _ownerForm = owner;
        _searchBox = searchBox;
        _view.TryText = tryText;
        _hiddenWithOwner = false;
        Owner = owner;
        owner.VisibleChanged += OwnerVisibleChanged;
        owner.FormClosed += OwnerClosed;
    }

    // Windows doesn't hide an owned window with its owner, so the hotkey hiding the window would leave the help
    // floating alone: it hides with its window and comes back with it.
    void OwnerVisibleChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || _ownerForm is null) return;
        if (!_ownerForm.Visible && Visible)
        {
            _hiddenWithOwner = true;
            Hide();
        }
        else if (_ownerForm.Visible && _hiddenWithOwner)
        {
            _hiddenWithOwner = false;
            Show();
        }
    }

    void OwnerClosed(object? sender, FormClosedEventArgs e)
    {
        if (!IsDisposed) Close();
    }

    // ---- size and place -------------------------------------------------------------------------------

    Size LogicalClientSize() => new(ClientSize.Width * 96 / DeviceDpi, ClientSize.Height * 96 / DeviceDpi);

    /// <summary>The window's outer size for a client size in logical pixels at <paramref name="dpi"/>.</summary>
    Size OuterSize(Size logicalClient, int dpi)
    {
        var cp = CreateParams;
        var r = new RECT { Right = logicalClient.Width * dpi / 96, Bottom = logicalClient.Height * dpi / 96 };
        try { AdjustWindowRectExForDpi(ref r, cp.Style, false, cp.ExStyle, (uint)dpi); }
        catch (EntryPointNotFoundException) { } // before Windows 10 1607: the client size alone, still on the screen
        return new Size(r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Where the help goes for <paramref name="owner"/>; a height of 0 asks for the whole help's height.</summary>
    Rectangle PlaceFor(Form owner, Size logicalClient)
    {
        int dpi = owner.DeviceDpi;
        int S(int v) => v * dpi / 96;
        var screen = Screen.FromControl(owner);
        var ownerBounds = owner.Bounds;
        // Without a search box showing, a stand-in at the window's top right, where the box would be.
        var box = _searchBox() ?? new Rectangle(ownerBounds.Right - S(16) - S(300), ownerBounds.Top + S(40), S(300), S(32));
        int clientWidth = S(Math.Max(MinWidth, logicalClient.Width));
        int clientHeight = logicalClient.Height > 0 ? S(logicalClient.Height) : HeightAt(clientWidth, dpi);
        var outer = OuterSize(new Size(clientWidth * 96 / dpi, clientHeight * 96 / dpi), dpi);
        var minimum = OuterSize(new Size(MinWidth, MinHeight), dpi);
        var r = HelpWindowPlacement.Place(Box(ownerBounds), Box(box), Box(screen.WorkingArea),
            (outer.Width, outer.Height), (minimum.Width, minimum.Height), S(Gap));
        return new Rectangle(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>The whole help's height at a client width (with the scroll bar it will then need), at a scaling.</summary>
    static int HeightAt(int clientWidth, int dpi) =>
        SearchHelpView.ContentHeightAt(dpi, clientWidth - SystemInformation.GetVerticalScrollBarWidthForDpi(dpi));

    Rectangle? _moving;

    /// <summary>
    /// Moves it to bounds already worked out for the scaling of the screen they are on. Moved to a screen with another
    /// scaling, Windows suggests the old size scaled once more; that suggestion is not taken.
    /// </summary>
    void MoveTo(Rectangle bounds, int dpi)
    {
        _moving = bounds;
        // The minimum from the old screen's scaling would hold the new bounds back (wider on a screen with less
        // scaling), so it is lifted for the move and set for the new scaling after it.
        MinimumSize = Size.Empty;
        try { Bounds = bounds; }
        finally
        {
            _moving = null;
            MinimumSize = MinimumFor(dpi, bounds.Size);
        }
    }

    /// <summary>
    /// The minimum size at a scaling, but never more than <paramref name="placed"/>: on a screen too small for the
    /// minimum the help is placed smaller, and a larger minimum would push it off the screen again.
    /// </summary>
    Size MinimumFor(int dpi, Size placed)
    {
        var m = OuterSize(new Size(MinWidth, MinHeight), dpi);
        return new Size(Math.Min(m.Width, placed.Width), Math.Min(m.Height, placed.Height));
    }

    static HelpWindowPlacement.Box Box(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Where it last was for this window, if that window is still on the same screen.</summary>
    Rectangle? Remembered(Form owner)
    {
        if (s_place is not { } p || !p.Owner.TryGetTarget(out var was) || !ReferenceEquals(was, owner)) return null;
        var screen = Screen.FromControl(owner);
        if (screen.DeviceName != p.Screen || !screen.WorkingArea.IntersectsWith(p.Bounds)) return null;
        var area = Box(screen.WorkingArea);
        var r = HelpWindowPlacement.Clamp(Box(p.Bounds), area);
        return new Rectangle(r.X, r.Y, r.Width, r.Height);
    }

    void Remember()
    {
        if (!IsHandleCreated || WindowState != FormWindowState.Normal) return;
        s_logicalSize = LogicalClientSize();
        if (_ownerForm is { } owner) s_place = (new WeakReference<Form>(owner), Bounds, Screen.FromHandle(Handle).DeviceName);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Dragged to a screen with another scaling, Windows' suggested size is taken (the same size to the eye); moved
        // there by MoveTo, the size was already made for that screen.
        if (_moving is not null)
        {
            e.Cancel = true; // MoveTo sets the minimum for the new scaling itself
            base.OnDpiChanged(e);
            return;
        }
        // The old scaling's minimum must not hold back Windows' suggested size on the new screen (larger than it there
        // when the new screen has less scaling), so it is lifted first and set for the new scaling after.
        MinimumSize = Size.Empty;
        base.OnDpiChanged(e);
        MinimumSize = MinimumFor(e.DeviceDpiNew, Size);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _view.Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!_view.Focused) _view.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = e.SuppressKeyPress = true; return; }
        // F1 in the help itself: it is already showing (and must not reach the window behind it).
        if (e.KeyCode == Keys.F1) { e.Handled = e.SuppressKeyPress = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Remember();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_ownerForm is { } owner)
        {
            owner.VisibleChanged -= OwnerVisibleChanged;
            owner.FormClosed -= OwnerClosed;
        }
        if (ReferenceEquals(s_open, this)) s_open = null;
        base.OnFormClosed(e); // a modeless form is disposed by WinForms once closed
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AdjustWindowRectExForDpi(ref RECT rect, int style, bool menu, int exStyle, uint dpi);
}
