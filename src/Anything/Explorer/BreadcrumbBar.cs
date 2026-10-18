using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anything.Explorer;

/// <summary>
/// Windows 11 File Explorer's address bar: the current location as clickable segments, each with a chevron that
/// drops down that level's subfolders, leading segments folded into an overflow menu when space runs out, and the
/// host's address TextBox swapped in for typing (click empty space or the icon).
/// </summary>
internal sealed class BreadcrumbBar : Control
{
    // Metrics in logical (96 DPI) pixels, from Explorer's address bar on Windows 11 24H2 (UI Automation bounds and
    // pixel measurements at 125%). The icon plus the root chevron span 14..65; a segment is 9 + text + 32 wide
    // (8 after the text, then a 24 wide chevron), 24 tall, segments touch.
    const int BarHeight = 32, Radius = 4, IconLeft = 14, IconSize = 16, SlotLeft = 39, SlotWidth = 26,
              ButtonHeight = 24, TextLeft = 9, TextRight = 8, ChevronWidth = 24, EditLeft = 38, EditRight = 8, MinEmpty = 40;
    const float TextPixels = 14f, GlyphPixels = 12f;   // Segoe UI Variable Text 10.5pt, chevrons 9pt
    const char ChevronRightGlyph = '\uE76C', ChevronDownGlyph = '\uE70D', ChevronLeftGlyph = '\uE76B';
    const int MaxMenuItems = 1000, MenuQuickWaitMs = 150, MenuTimeoutMs = 3000;
    // Explorer's XAML text is greyscale anti-aliased with unhinted advances. Segments are laid out with GDI+
    // typographic widths (within a pixel of Explorer's; GDI's hinted widths run 2-4px wider per label) but drawn
    // with greyscale GDI, whose stroke weight matches Explorer's where GDI+ renders ~20% lighter.
    const System.Drawing.Text.TextRenderingHint MeasureHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    const System.Drawing.Text.TextRenderingHint DrawHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;

    enum PartKind { None, Icon, Root, Overflow, Text, Chevron, Empty }
    readonly record struct Part(PartKind Kind, int Index = -1);

    /// <summary>One level of the path. Icon is the system image list index (-1 = none).</summary>
    sealed record Crumb(string Name, string Target, int Icon, bool IsRoot);
    readonly record struct Child(string Name, string Target, int Icon);

    /// <summary>Results of a background subfolder enumeration, filled under its own lock.</summary>
    sealed class FolderLoad
    {
        public readonly List<Child> Items = [];
        public bool Sort;
    }

    readonly TextBox _edit;
    List<Crumb> _crumbs = [];
    Crumb? _root;                       // namespace root (Desktop): never a segment, but its chevron lists the top level
    int[]? _widths;                     // measured text widths of _crumbs
    int _first;                         // first visible crumb; earlier ones live in the overflow menu
    bool _overflow;
    Rectangle[] _textRects = [], _chevRects = [];
    Rectangle _slotRect;                // root chevron, or the overflow button
    Part _hover, _pressed, _menuPart, _swallow;
    long _swallowTicks;
    bool _editing;
    int _dpi;
    Font _font, _glyphFont;
    Font? _boldMenuFont;
    ContextMenuStrip? _menu;
    ToolStripRenderer? _menuRenderer;
    int _menuRequest;
    bool _reopening;
    CancellationTokenSource? _menuCts;
    readonly Dictionary<int, Bitmap?> _icons = [];  // system image list index -> small icon

    /// <summary>Raised when the user picks a location. Argument: parsing name ("C:\Users", "::{20D04FE0-...}").</summary>
    public event Action<string>? NavigateRequested;

    public BreadcrumbBar(TextBox editBox)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, false);
        TabStop = false;
        AccessibleName = "Address";
        AccessibleRole = AccessibleRole.ToolBar;

        _dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        (_font, _glyphFont) = CreateFonts(_dpi);

        _edit = editBox;
        _edit.BorderStyle = BorderStyle.None;
        _edit.Dock = DockStyle.None;
        _edit.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _edit.Margin = Padding.Empty;
        _edit.Font = _font;
        _edit.Visible = false;
        ApplyEditColors();
        _edit.Leave += (_, _) =>
        {
            // Deferred: hiding the focused box while focus is still moving confuses the container's focus logic.
            if (_editing && IsHandleCreated) BeginInvoke(() => { if (_editing && !_edit.ContainsFocus) EndEdit(); });
        };
        Controls.Add(_edit);
        Size = new Size(D(400), D(BarHeight));     // last: resizing lays out the TextBox
    }

    protected override Size DefaultSize => new(400, BarHeight);

    // ---- public API ---------------------------------------------------------------------------

    /// <summary>Full path or parsing name shown in the TextBox when editing starts (set by the host).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EditText { get; set; } = "";

    /// <summary>True while the address TextBox is shown instead of the segments.</summary>
    public bool Editing => _editing;

    /// <summary>Renderer for the chevron and overflow drop-downs (null = WinForms default).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ToolStripRenderer? MenuRenderer
    {
        get => _menuRenderer;
        set
        {
            _menuRenderer = value;
            if (_menu == null) return;
            if (value != null) _menu.Renderer = value;
            else _menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        }
    }

    /// <summary>Shows <paramref name="parsingName"/> as segments (This PC › Local Disk (C:) › Users). Never throws.</summary>
    public void SetLocation(string? parsingName)
    {
        CloseMenu();
        var crumbs = new List<Crumb>();
        Crumb? root = null;
        if (!string.IsNullOrWhiteSpace(parsingName))
        {
            try { root = Walk(parsingName, crumbs); }
            catch (Exception) { crumbs.Clear(); root = null; }
            if (crumbs.Count == 0) crumbs.Add(new Crumb(parsingName, parsingName, -1, false));
        }
        _crumbs = crumbs;
        _root = root;
        _widths = null;
        _hover = _pressed = default;
        LayoutCrumbs();
        Invalidate();
    }

    /// <summary>Swaps the segments for the TextBox holding <see cref="EditText"/>, all selected and focused.</summary>
    public void BeginEdit()
    {
        CloseMenu();
        _editing = true;
        _hover = _pressed = default;
        ApplyEditColors();
        _edit.Text = EditText;
        LayoutEdit();
        _edit.Visible = true;
        _edit.BringToFront();
        _edit.Focus();
        _edit.SelectAll();
        Invalidate();
    }

    /// <summary>Hides the TextBox and shows the segments again.</summary>
    public void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        _edit.Visible = false;
        Invalidate();
    }

    // ---- shell model --------------------------------------------------------------------------

    /// <summary>
    /// Fills <paramref name="crumbs"/> top-down and returns the namespace root when it was left out. Plain file-system
    /// paths climb by directory (the shell would jump from C:\Users\name straight to Desktop) until a drive or
    /// share root, then follow IShellItem.GetParent like everything else.
    /// </summary>
    Crumb? Walk(string parsingName, List<Crumb> crumbs)
    {
        var chain = new List<Crumb>();     // deepest first
        IShellItem? item = CreateItem(parsingName);
        for (int depth = 0; item != null && depth < 64; depth++)
        {
            IShellItem? next = null;
            try
            {
                string target = ShellNative.NameOf(item, SIGDN.DesktopAbsoluteParsing)
                                ?? ShellNative.NameOf(item, SIGDN.DesktopAbsoluteEditing) ?? parsingName;
                string name = ShellNative.NameOf(item, SIGDN.NormalDisplay) ?? target;
                // GetParent failing marks the root, whose own parsing name is the Desktop directory: check it first.
                if (item.GetParent(out var parent) == ShellNative.S_OK && parent != null)
                {
                    next = parent;
                    string? parentPath = IsPlainPath(target) ? Path.GetDirectoryName(target) : null;
                    if (parentPath != null && CreateItem(parentPath) is { } dirParent)
                    {
                        Release(parent);
                        next = dirParent;
                    }
                }
                chain.Add(new Crumb(name, target, IconIndexOf(item), next == null));
            }
            finally { Release(item); }
            item = next;
        }
        Release(item);

        Crumb? root = null;
        if (chain.Count > 1 && chain[^1].IsRoot)
        {
            root = chain[^1];
            chain.RemoveAt(chain.Count - 1);
        }
        for (int i = chain.Count - 1; i >= 0; i--) crumbs.Add(chain[i]);
        return root;
    }

    /// <summary>"C:\x" or "\\server\share\x" (not "::{guid}", "shell:", "\\?\", URLs).</summary>
    static bool IsPlainPath(string s)
        => (s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] == '\\')
           || (s.Length > 2 && s[0] == '\\' && s[1] == '\\' && s[2] != '?' && s[2] != '.');

    static IShellItem? CreateItem(string parsingName)
    {
        try
        {
            var iid = ShellNative.IID_IShellItem;
            return ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) == ShellNative.S_OK ? item : null;
        }
        catch (Exception) { return null; }
    }

    static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); }
        catch (Exception) { }
    }

    /// <summary>System image list index of <paramref name="item"/>; also caches its small icon (UI thread).</summary>
    int IconIndexOf(IShellItem item)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (ShellNative.SHGetIDListFromObject(item, out pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) return -1;
            return CacheIcon(pidl);
        }
        catch (Exception) { return -1; }
        finally { if (pidl != IntPtr.Zero) ShellNative.ILFree(pidl); }
    }

    int CacheIcon(IntPtr pidl)
    {
        var info = new Native.FileInfo();
        Native.SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<Native.FileInfo>(),
            Native.SHGFI_PIDL | Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
        if (info.hIcon == IntPtr.Zero) return -1;
        if (_icons.ContainsKey(info.iIcon)) Native.DestroyIcon(info.hIcon);
        else _icons[info.iIcon] = ToBitmap(info.hIcon);
        return info.iIcon;
    }

    /// <summary>Cached small icon for a system image list index, loading it through <paramref name="target"/> if new.</summary>
    Bitmap? IconFor(int index, string? target)
    {
        if (index < 0) return null;
        if (_icons.TryGetValue(index, out var bmp)) return bmp;
        if (target == null) return null;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Native.SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out _) != ShellNative.S_OK || pidl == IntPtr.Zero) return null;
            CacheIcon(pidl);
        }
        catch (Exception) { }
        finally { if (pidl != IntPtr.Zero) ShellNative.ILFree(pidl); }
        if (!_icons.ContainsKey(index)) _icons[index] = null;   // don't retry a failing index for every item
        return _icons[index];
    }

    static Bitmap? ToBitmap(IntPtr hIcon)
    {
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return icon.ToBitmap();     // keeps the alpha channel of 32-bit icons
        }
        catch (Exception) { return null; }
        finally { Native.DestroyIcon(hIcon); }
    }

    /// <summary>
    /// Enumerates the subfolders of <paramref name="target"/> on a background STA thread: names, parsing names and
    /// image list indexes only (no COM object leaves the thread). Explorer's Hidden / ShowSuperHidden settings apply.
    /// </summary>
    static Task LoadFoldersAsync(string target, bool isRoot, FolderLoad load, CancellationToken ct)
    {
        bool hidden = false, superHidden = false;
        try
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            hidden = Registry.GetValue(key, "Hidden", 2) is int h && h == 1;
            superHidden = hidden && Registry.GetValue(key, "ShowSuperHidden", 0) is int s && s == 1;
        }
        catch (Exception) { }
        var flags = SHCONTF.FOLDERS | SHCONTF.NAVIGATION_ENUM
                    | (hidden ? SHCONTF.INCLUDEHIDDEN : 0) | (superHidden ? SHCONTF.INCLUDESUPERHIDDEN : 0);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { EnumerateFolders(target, isRoot, (uint)flags, hidden, load, ct); }
            catch (Exception) { }
            finally { done.TrySetResult(); }
        })
        { IsBackground = true, Name = "Breadcrumb folders" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    static void EnumerateFolders(string target, bool isRoot, uint flags, bool showHidden, FolderLoad load, CancellationToken ct)
    {
        IntPtr parentPidl = IntPtr.Zero;
        object? folderObj = null, enumObj = null;
        try
        {
            IntPtr pFolder;
            if (isRoot)
            {
                // The namespace root's own parsing name is the Desktop *directory*, so ask for the root folder itself.
                if (Native.SHGetDesktopFolder(out pFolder) != ShellNative.S_OK || pFolder == IntPtr.Zero) return;
            }
            else
            {
                var item = CreateItem(target);
                if (item == null) return;
                try
                {
                    if (ShellNative.SHGetIDListFromObject(item, out parentPidl) != ShellNative.S_OK) return;
                    lock (load) load.Sort = ShellNative.NameOf(item, SIGDN.FileSysPath) != null; // virtual folders keep their order
                    var bhid = Native.BHID_SFObject;
                    var iid = Native.IID_IShellFolder;
                    if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out pFolder) != ShellNative.S_OK || pFolder == IntPtr.Zero) return;
                }
                finally { Release(item); }
            }
            folderObj = Marshal.GetObjectForIUnknown(pFolder);
            Marshal.Release(pFolder);
            var folder = (IBreadcrumbShellFolder)folderObj;

            // S_FALSE with no enumerator = nothing to list.
            if (folder.EnumObjects(IntPtr.Zero, flags, out var pEnum) != ShellNative.S_OK || pEnum == IntPtr.Zero) return;
            enumObj = Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            var items = (IBreadcrumbEnumIDList)enumObj;

            int count = 0;
            while (count < MaxMenuItems && !ct.IsCancellationRequested
                   && items.Next(1, out var child, out uint fetched) == ShellNative.S_OK && fetched == 1 && child != IntPtr.Zero)
            {
                IntPtr abs = IntPtr.Zero;
                try
                {
                    uint attrs = Native.SFGAO_FOLDER | Native.SFGAO_STREAM | Native.SFGAO_HIDDEN;
                    var one = child;
                    if (folder.GetAttributesOf(1, ref one, ref attrs) != ShellNative.S_OK) continue;
                    if ((attrs & Native.SFGAO_FOLDER) == 0 || (attrs & Native.SFGAO_STREAM) != 0) continue;  // zip/cab files
                    if (!showHidden && (attrs & Native.SFGAO_HIDDEN) != 0) continue;

                    abs = parentPidl == IntPtr.Zero ? child : Native.ILCombine(parentPidl, child);
                    if (abs == IntPtr.Zero) continue;
                    string? name = ShellNative.NameOf(abs, SIGDN.NormalDisplay);
                    string? parsing = ShellNative.NameOf(abs, SIGDN.DesktopAbsoluteParsing);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parsing)) continue;
                    var info = new Native.FileInfo();
                    Native.SHGetFileInfo(abs, 0, ref info, (uint)Marshal.SizeOf<Native.FileInfo>(),
                        Native.SHGFI_PIDL | Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON);
                    lock (load) load.Items.Add(new Child(name, parsing, info.iIcon));
                    count++;
                }
                finally
                {
                    if (abs != IntPtr.Zero && abs != child) ShellNative.ILFree(abs);
                    Marshal.FreeCoTaskMem(child);
                }
            }
        }
        finally
        {
            Release(enumObj);
            Release(folderObj);
            if (parentPidl != IntPtr.Zero) ShellNative.ILFree(parentPidl);
        }
    }

    // ---- menus --------------------------------------------------------------------------------

    ContextMenuStrip EnsureMenu()
    {
        if (_menu != null) return _menu;
        var menu = new ContextMenuStrip { ShowImageMargin = true, ShowCheckMargin = false };
        menu.ImageScalingSize = new Size(D(IconSize), D(IconSize));
        if (_menuRenderer != null) menu.Renderer = _menuRenderer;
        ExplorerTheme.StyleDropDown(menu);    // the app's Windows 11 menu look (rounded corners, roomy rows)
        menu.Closing += (_, e) =>
        {
            // A click on the chevron that owns the open menu should only close it, not reopen it.
            if (_reopening || e.CloseReason != ToolStripDropDownCloseReason.AppClicked) return;
            if (PartBounds(_menuPart).Contains(PointToClient(Cursor.Position)))
            {
                _swallow = _menuPart;
                _swallowTicks = Environment.TickCount64;
            }
        };
        menu.Closed += (_, _) =>
        {
            if (_reopening) return;
            _menuRequest++;
            _menuCts?.Cancel();
            _menuPart = default;
            _hover = _editing ? default : HitTest(PointToClient(Cursor.Position));   // HitTest is None outside
            Invalidate();
        };
        return _menu = menu;
    }

    void CloseMenu()
    {
        _menuRequest++;
        _menuCts?.Cancel();
        _menuCts = null;
        if (_menu is { Visible: true }) _menu.Close();
        if (_menuPart != default) { _menuPart = default; Invalidate(); }
    }

    void ShowMenu(ToolStripItem[] items, Rectangle anchor)
    {
        var menu = EnsureMenu();
        if (menu.Visible)
        {
            // Close and reopen rather than swap items in place, so the size and screen position are recomputed.
            _reopening = true;
            try { menu.Close(); }
            finally { _reopening = false; }
        }
        // Long lists scroll inside the larger of the spaces below / above the bar instead of covering it.
        var screen = Screen.FromControl(this).WorkingArea;
        var bar = RectangleToScreen(ClientRectangle);
        int room = Math.Max(screen.Bottom - bar.Bottom, bar.Top - screen.Top) - D(8);
        menu.MaximumSize = new Size(0, Math.Max(D(120), room));
        // StyleDropDown pads every row as the menu opens and each change re-lays out the whole menu, which is
        // quadratic for a 1000-folder list. Applying the same padding here, while layout is suspended, makes
        // those assignments no-ops.
        int dpi = menu.DeviceDpi > 0 ? menu.DeviceDpi : 96;
        var rowPadding = new Padding(2 * dpi / 96, 5 * dpi / 96, 2 * dpi / 96, 5 * dpi / 96);
        foreach (var item in items) item.Padding = rowPadding;
        menu.SuspendLayout();
        menu.Items.Clear();
        menu.Items.AddRange(items);
        menu.ResumeLayout();
        menu.Show(this, new Point(anchor.Left, Height + D(2)));
    }

    ToolStripMenuItem MenuItem(string text, Image? image, string target, bool bold)
    {
        var item = new ToolStripMenuItem(text.Replace("&", "&&"), image);
        if (bold)
        {
            _boldMenuFont ??= new Font(EnsureMenu().Font, FontStyle.Bold);
            item.Font = _boldMenuFont;
        }
        item.Click += (_, _) => NavigateRequested?.Invoke(target);
        return item;
    }

    static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    /// <summary>Subfolders of a segment (or of the root); the next segment of the current path is bold.</summary>
    async void OpenFolderMenu(Part part, Crumb crumb, string? next)
    {
        try
        {
            CloseMenu();
            int request = ++_menuRequest;
            _menuPart = part;
            Invalidate();
            var anchor = PartBounds(part);
            var cts = _menuCts = new CancellationTokenSource();
            var load = new FolderLoad();
            var work = LoadFoldersAsync(crumb.Target, crumb.IsRoot, load, cts.Token);

            // Local folders answer within a few ms: show the finished menu. Slow ones get "Loading…" first.
            if (await Task.WhenAny(work, Task.Delay(MenuQuickWaitMs)) != work)
            {
                if (request != _menuRequest || IsDisposed) return;
                ShowMenu([Disabled("Loading…")], anchor);
                await Task.WhenAny(work, Task.Delay(MenuTimeoutMs - MenuQuickWaitMs));
            }
            if (request != _menuRequest || IsDisposed) return;
            cts.Cancel();   // on timeout, stop the enumeration and show what arrived so far

            List<Child> children;
            bool sort;
            lock (load) { children = [.. load.Items]; sort = load.Sort && !crumb.IsRoot; }
            if (sort) children.Sort((a, b) => Native.StrCmpLogicalW(a.Name, b.Name));
            var items = new List<ToolStripItem>(children.Count);
            foreach (var c in children)
            {
                bool current = next != null && string.Equals(c.Target, next, StringComparison.OrdinalIgnoreCase);
                items.Add(MenuItem(c.Name, IconFor(c.Icon, c.Target), c.Target, current));
            }
            if (items.Count == 0) items.Add(Disabled(work.IsCompleted ? "No folders" : "Not responding"));
            ShowMenu([.. items], anchor);
        }
        catch (Exception) { }
    }

    /// <summary>The leading segments that didn't fit, nearest first.</summary>
    void OpenOverflowMenu()
    {
        CloseMenu();
        _menuPart = new Part(PartKind.Overflow);
        var items = new List<ToolStripItem>();
        for (int i = Math.Min(_first, _crumbs.Count) - 1; i >= 0; i--)
            items.Add(MenuItem(_crumbs[i].Name, IconFor(_crumbs[i].Icon, _crumbs[i].Target), _crumbs[i].Target, false));
        if (items.Count == 0) { _menuPart = default; return; }
        Invalidate();
        ShowMenu([.. items], _slotRect);
    }

    // ---- layout -------------------------------------------------------------------------------

    int D(int logical) => (int)Math.Round(logical * _dpi / 96.0);

    static (Font Text, Font Glyph) CreateFonts(int dpi)
        => (new Font(ExplorerTheme.UiFamily, TextPixels * dpi / 96f, GraphicsUnit.Pixel),
            new Font(ExplorerTheme.IconFamily, GlyphPixels * dpi / 96f, GraphicsUnit.Pixel));

    void UpdateMetrics(int dpi)
    {
        if (dpi <= 0 || dpi == _dpi) return;
        _dpi = dpi;
        CloseMenu();
        var (oldFont, oldGlyph) = (_font, _glyphFont);
        (_font, _glyphFont) = CreateFonts(dpi);
        _edit.Font = _font;
        oldFont.Dispose();
        oldGlyph.Dispose();
        if (_menu != null)
        {
            _menu.Items.Clear();    // the old rows hold the bold font disposed below
            _menu.ImageScalingSize = new Size(D(IconSize), D(IconSize));
        }
        _boldMenuFont?.Dispose();
        _boldMenuFont = null;
        _widths = null;
        LayoutEdit();
        LayoutCrumbs();
        Invalidate();
    }

    protected override void RescaleConstantsForDpi(int deviceDpiOld, int deviceDpiNew)
    {
        base.RescaleConstantsForDpi(deviceDpiOld, deviceDpiNew);
        UpdateMetrics(deviceDpiNew);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateMetrics(DeviceDpi);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateMetrics(DeviceDpi);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutEdit();
        LayoutCrumbs();
    }

    void LayoutEdit()
    {
        int left = D(EditLeft);
        _edit.SetBounds(left, (Height - _edit.Height) / 2, Math.Max(0, Width - left - D(EditRight)), _edit.Height);
    }

    void MeasureCrumbs()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = MeasureHint;
        _widths = new int[_crumbs.Count];
        for (int i = 0; i < _crumbs.Count; i++)
            _widths[i] = (int)Math.Ceiling(g.MeasureString(_crumbs[i].Name, _font, PointF.Empty, StringFormat.GenericTypographic).Width);
    }

    /// <summary>Places the slot (root chevron / overflow) and the segments, dropping leading ones that don't fit.</summary>
    void LayoutCrumbs()
    {
        int n = _crumbs.Count;
        _textRects = new Rectangle[n];
        _chevRects = new Rectangle[n];
        _slotRect = Rectangle.Empty;
        _overflow = false;
        _first = 0;
        if (n == 0) return;
        if (_widths == null || _widths.Length != n) MeasureCrumbs();
        var widths = _widths!;

        int h = D(ButtonHeight), top = (Height - h) / 2;
        int pad = D(TextLeft) + D(TextRight), chev = D(ChevronWidth), slot = D(SlotWidth);
        int x = D(SlotLeft), limit = Math.Max(x, Width - D(MinEmpty));
        int Need(int i) => widths[i] + pad + chev;

        int total = 0;
        for (int i = 0; i < n; i++) total += Need(i);
        if (n > 1 && total > limit - x - (_root != null ? slot : 0))
        {
            // Keep the deepest segments; the overflow button takes the root chevron's place.
            int avail = limit - x - slot, used = Need(n - 1);
            _first = n - 1;
            while (_first > 0 && used + Need(_first - 1) <= avail) used += Need(--_first);
        }
        _overflow = _first > 0;
        if (_overflow || _root != null)
        {
            _slotRect = new Rectangle(x, top, slot, h);
            x += slot;
        }
        for (int i = _first; i < n; i++)
        {
            int w = widths[i] + pad;
            int room = limit - x - chev;
            if (w > room) w = Math.Max(room, pad + D(16));    // a lone overlong segment: ellipsis
            _textRects[i] = new Rectangle(x, top, w, h);
            x += w;
            _chevRects[i] = new Rectangle(x, top, chev, h);
            x += chev;
        }
    }

    Rectangle PartBounds(Part part) => part.Kind switch
    {
        PartKind.Root or PartKind.Overflow => _slotRect,
        PartKind.Text when part.Index >= 0 && part.Index < _textRects.Length => _textRects[part.Index],
        PartKind.Chevron when part.Index >= 0 && part.Index < _chevRects.Length => _chevRects[part.Index],
        _ => Rectangle.Empty,
    };

    /// <summary>Segments are hit over the full bar height (by x only), like Explorer.</summary>
    Part HitTest(Point p)
    {
        if (!ClientRectangle.Contains(p)) return default;
        static bool InX(Rectangle r, int x) => r.Width > 0 && x >= r.Left && x < r.Right;
        if (InX(_slotRect, p.X)) return new Part(_overflow ? PartKind.Overflow : PartKind.Root);
        for (int i = _first; i < _crumbs.Count; i++)
        {
            if (InX(_textRects[i], p.X)) return new Part(PartKind.Text, i);
            if (InX(_chevRects[i], p.X)) return new Part(PartKind.Chevron, i);
        }
        return new Part(p.X < D(SlotLeft) ? PartKind.Icon : PartKind.Empty);
    }

    // ---- mouse --------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = _editing ? default : HitTest(e.Location);
        if (hover == _hover) return;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == default) return;
        _hover = default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_editing) { _edit.Focus(); return; }
        var part = HitTest(e.Location);
        bool swallow = part == _swallow && Environment.TickCount64 - _swallowTicks < 500;
        _swallow = default;
        if (swallow) return;
        switch (part.Kind)
        {
            case PartKind.Chevron:
                OpenFolderMenu(part, _crumbs[part.Index], part.Index + 1 < _crumbs.Count ? _crumbs[part.Index + 1].Target : null);
                break;
            case PartKind.Root when _root != null:
                OpenFolderMenu(part, _root, _crumbs.Count > 0 ? _crumbs[0].Target : null);
                break;
            case PartKind.Overflow:
                OpenOverflowMenu();
                break;
            case PartKind.Text:
                _pressed = part;
                Invalidate();
                break;
            case PartKind.Icon:
            case PartKind.Empty:
                BeginEdit();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed.Kind != PartKind.Text) return;
        var pressed = _pressed;
        _pressed = default;
        Invalidate();
        if (e.Button == MouseButtons.Left && HitTest(e.Location) == pressed && pressed.Index < _crumbs.Count)
            NavigateRequested?.Invoke(_crumbs[pressed.Index].Target);
    }

    // ---- painting -----------------------------------------------------------------------------

    // Explorer's AddressBarItemBackground* brushes (SubtleFillColorSecondary / Tertiary), blended over the field.
    static Color HoverFill => ExplorerTheme.Dark ? Color.FromArgb(0x0F, 255, 255, 255) : Color.FromArgb(0x09, 0, 0, 0);
    static Color PressedFill => ExplorerTheme.Dark ? Color.FromArgb(0x0A, 255, 255, 255) : Color.FromArgb(0x06, 0, 0, 0);
    /// <summary>Hairline round the other half of a hovered segment (Explorer's split-button look).</summary>
    static Color PairOutline => ExplorerTheme.Dark ? Color.FromArgb(0x0F, 255, 255, 255) : Color.FromArgb(0x0F, 0, 0, 0);
    /// <summary>TextFillColorPrimary.</summary>
    static Color TextColor => ExplorerTheme.Dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
    /// <summary>Explorer's chevrons render at full text colour (their pixels reach #FFFFFF in dark mode).</summary>
    static Color ChevronColor => TextColor;

    void ApplyEditColors()
    {
        _edit.BackColor = ExplorerTheme.FieldFill;
        _edit.ForeColor = TextColor;
    }

    Color ParentBack()
    {
        for (Control? p = Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return ExplorerTheme.Chrome;
    }

    Color? StateFill(Part part)
    {
        if (_menuPart == part) return PressedFill;
        if (_pressed == part) return _hover == part ? PressedFill : HoverFill;
        return _hover == part && _pressed.Kind == PartKind.None && _menuPart.Kind == PartKind.None ? HoverFill : null;
    }

    protected override void OnParentBackColorChanged(EventArgs e)
    {
        base.OnParentBackColorChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }   // everything is drawn in OnPaint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(ParentBack())) g.FillRectangle(back, ClientRectangle);
        if (Width < 4 || Height < 4) return;

        // Field: rounded, borderless (Explorer's border brush is its background), 2px accent along the bottom while typing.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var field = FillPath(ClientRectangle))
        {
            using (var fill = new SolidBrush(ExplorerTheme.FieldFill)) g.FillPath(fill, field);
            if (_editing)
            {
                var state = g.Save();
                g.SetClip(new Rectangle(0, Height - D(2), Width, D(2)));
                using var accent = new SolidBrush(ExplorerTheme.Accent);
                g.FillPath(accent, field);
                g.Restore(state);
            }
        }
        g.SmoothingMode = SmoothingMode.None;

        DrawLocationIcon(g);
        if (_editing) return;

        g.TextRenderingHint = DrawHint;     // TextRenderer maps it to greyscale GDI font quality
        var text = TextColor;
        var chevron = ChevronColor;
        if (_slotRect.Width > 0)
        {
            var slot = new Part(_overflow ? PartKind.Overflow : PartKind.Root);
            if (StateFill(slot) is Color c)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = FillPath(_slotRect);
                using var brush = new SolidBrush(c);
                g.FillPath(brush, path);
                g.SmoothingMode = SmoothingMode.None;
            }
            if (_overflow)
            {
                // "«": two left chevrons nudged together.
                int nudge = D(3);
                DrawGlyph(g, ChevronLeftGlyph, new Rectangle(_slotRect.X - nudge, _slotRect.Y, _slotRect.Width, _slotRect.Height), chevron);
                DrawGlyph(g, ChevronLeftGlyph, new Rectangle(_slotRect.X + nudge, _slotRect.Y, _slotRect.Width, _slotRect.Height), chevron);
            }
            else DrawGlyph(g, _menuPart == slot ? ChevronDownGlyph : ChevronRightGlyph, _slotRect, chevron);
        }

        int left = D(TextLeft);
        for (int i = _first; i < _crumbs.Count; i++)
        {
            var tr = _textRects[i];
            var cr = _chevRects[i];
            var chevPart = new Part(PartKind.Chevron, i);
            Color? textFill = StateFill(new Part(PartKind.Text, i)), chevFill = StateFill(chevPart);
            if (textFill != null || chevFill != null)
            {
                // A segment and its chevron form a split button: the active half filled, a hairline round the other.
                var pair = Rectangle.Union(tr, cr);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = FillPath(pair))
                {
                    FillPart(g, path, tr, textFill);
                    FillPart(g, path, cr, chevFill);
                }
                var state = g.Save();
                if (textFill != null) g.ExcludeClip(tr);
                if (chevFill != null) g.ExcludeClip(cr);
                using (var outline = ExplorerTheme.RoundedRect(new RectangleF(pair.X, pair.Y, pair.Width - 1, pair.Height - 1), D(Radius)))
                using (var pen = new Pen(PairOutline))
                    g.DrawPath(pen, outline);   // 1px pen on integer coordinates = GDI+ pixel centres: crisp
                g.Restore(state);
                g.SmoothingMode = SmoothingMode.None;
            }
            // The right padding is overhang room for GDI's slightly wider glyphs. GDI centres the whole line box,
            // where Explorer's text sits a pixel higher.
            var textBounds = Rectangle.FromLTRB(tr.Left + left, tr.Top - D(1), tr.Right, tr.Bottom - D(1));
            TextRenderer.DrawText(g, _crumbs[i].Name, _font, textBounds, text, TextFlags);
            DrawGlyph(g, _menuPart == chevPart ? ChevronDownGlyph : ChevronRightGlyph, cr, chevron);
        }
    }

    /// <summary>Rounded outline that covers exactly the pixels of <paramref name="r"/> (GDI+ pixel centres are integers).</summary>
    GraphicsPath FillPath(Rectangle r) => ExplorerTheme.RoundedRect(new RectangleF(r.X - 0.5f, r.Y - 0.5f, r.Width, r.Height), D(Radius));

    static void FillPart(Graphics g, GraphicsPath path, Rectangle part, Color? color)
    {
        if (color is not Color c) return;
        var state = g.Save();
        g.SetClip(part);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
        g.Restore(state);
    }

    /// <summary>Icon-font glyph centred in <paramref name="bounds"/> (one pixel lower, matching Explorer's chevrons).</summary>
    void DrawGlyph(Graphics g, char glyph, Rectangle bounds, Color color)
    {
        g.TextRenderingHint = DrawHint;
        TextRenderer.DrawText(g, glyph.ToString(), _glyphFont, new Rectangle(bounds.X, bounds.Y + D(1), bounds.Width, bounds.Height), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Explorer shows a monochrome icon of the top-level place (its own addressbar.*.svg: a monitor for This PC, a house
    /// for Home...) rather than the folder's icon; other places (OneDrive, Linux, libraries) get the shell icon.
    /// </summary>
    Bitmap? AddressBarIcon()
    {
        if (_crumbs.Count == 0) return null;
        string root = _crumbs[0].Target;
        string? svg = root.Contains("20D04FE0-3AEA-1069-A2D8-08002B30309D", StringComparison.OrdinalIgnoreCase) ? ExplorerIcons.AddressThisPc
                    : root.Contains("f874310e-b6b7-47dc-bc84-b9e6b38f5903", StringComparison.OrdinalIgnoreCase) ? ExplorerIcons.AddressHome
                    : root.Contains("e88865ea-0e1c-4e20-9aa6-edcd0212c87c", StringComparison.OrdinalIgnoreCase) ? ExplorerIcons.AddressGallery
                    : root.Contains("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", StringComparison.OrdinalIgnoreCase) || root.StartsWith(@"\\", StringComparison.Ordinal) ? ExplorerIcons.AddressNetwork
                    : null;
        if (svg is null) return null;
        string key = svg + "@" + DeviceDpi + (ExplorerTheme.Dark ? "d" : "l");
        if (_addressIconKey != key)
        {
            _addressIcon?.Dispose();
            _addressIcon = ExplorerIcons.Render(svg, D(IconSize), ExplorerTheme.Dark);
            _addressIconKey = key;
        }
        return _addressIcon;
    }

    Bitmap? _addressIcon;
    string? _addressIconKey;

    void DrawLocationIcon(Graphics g)
    {
        if (AddressBarIcon() is { } placeIcon)
        {
            int s = D(IconSize);
            g.DrawImage(placeIcon, new Rectangle(D(IconLeft), (Height - s) / 2, s, s));
            return;
        }
        if (_crumbs.Count == 0 || !_icons.TryGetValue(_crumbs[^1].Icon, out var icon) || icon == null) return;
        int size = D(IconSize);
        var r = new Rectangle(D(IconLeft), (Height - size) / 2, size, size);
        if (icon.Width == size && icon.Height == size)
        {
            g.DrawImage(icon, r);
            return;
        }
        // The shell hands out system-DPI icons; rescale for other monitors.
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(icon, r);
        g.Restore(state);
    }

    // ---- lifetime -----------------------------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menuCts?.Cancel();
            _menu?.Dispose();
            _boldMenuFont?.Dispose();
            foreach (var bmp in _icons.Values) bmp?.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _font.Dispose();
            _glyphFont.Dispose();
        }
    }

    // ---- interop ------------------------------------------------------------------------------

    /// <summary>IShellFolder (ShObjIdl_core.h), only the slots used here (vtable prefix).</summary>
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBreadcrumbShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, ref IntPtr apidl, ref uint rgfInOut);
    }

    /// <summary>IEnumIDList (ShObjIdl_core.h), Next only.</summary>
    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBreadcrumbEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    }

    static class Native
    {
        public const uint SHGFI_ICON = 0x100, SHGFI_SYSICONINDEX = 0x4000, SHGFI_SMALLICON = 0x1, SHGFI_PIDL = 0x8;
        public const uint SFGAO_FOLDER = 0x20000000, SFGAO_STREAM = 0x00400000, SFGAO_HIDDEN = 0x00080000;

        public static readonly Guid BHID_SFObject = new("3981e224-f559-11d3-8e3a-00c04f6837d5");
        public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref FileInfo psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")] public static extern int SHGetDesktopFolder(out IntPtr ppshf);
        [DllImport("shell32.dll")] public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string psz1, string psz2);
    }
}
