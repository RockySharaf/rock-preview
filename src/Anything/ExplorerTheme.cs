using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// The Windows 11 File Explorer look, taken from Explorer itself rather than guessed: sizes measured from a live
/// Explorer window (UI Automation), brush names read from its compiled XAML (FileExplorerExtensions resources:
/// NavigationBarControlBackground = LayerOnMicaBaseAltFillColorDefault, AddressBarBackground = ControlFillColorDefault,
/// item hover = SubtleFillColorSecondary...) resolved with the WinUI theme values (they reproduce Explorer's dark
/// pixels exactly: #2C2C2C row, #383838 boxes), and the navigation-pane colours from ExplorerFrame.dll (REA/Ghidra).
/// See docs/research/07-windows-explorer-breakdown.md.
/// </summary>
internal static class ExplorerTheme
{
    public static bool Dark => Application.IsDarkModeEnabled;

    /// <summary>Address row: LayerOnMicaBaseAltFillColorDefault over Mica.</summary>
    public static Color Chrome => Dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xFB, 0xFB, 0xFB);
    /// <summary>Command bar (transparent in Explorer), navigation pane, files and preview: the items view background.</summary>
    public static Color Content => Dark ? Color.FromArgb(0x19, 0x19, 0x19) : Color.White;
    /// <summary>Status bar (DirectUI).</summary>
    public static Color StatusBack => Dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White;
    /// <summary>The 1px lines under the address row and the command bar.</summary>
    public static Color Divider => Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEB, 0xEB, 0xEB);
    /// <summary>Command bar separators (DividerStrokeColorDefault, blended).</summary>
    public static Color Separator => Dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
    /// <summary>Navigation pane group divider: ExplorerFrame.dll uses 0x383838 (dark) / 0xD6D6D6 (light).</summary>
    public static Color NavDivider => Dark ? Color.FromArgb(0x38, 0x38, 0x38) : Color.FromArgb(0xD6, 0xD6, 0xD6);
    /// <summary>TextFillColorPrimary / Secondary / Disabled.</summary>
    public static Color Text => Dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
    public static Color SecondaryText => Dark ? Color.FromArgb(0xC5, 0xC5, 0xC5) : Color.FromArgb(0x61, 0x61, 0x61);
    public static Color DisabledText => Dark ? Color.FromArgb(0x6D, 0x6D, 0x6D) : Color.FromArgb(0xA3, 0xA3, 0xA3);
    /// <summary>Glyph icons (navigation buttons, chevrons).</summary>
    public static Color Icon => Text;
    /// <summary>SubtleFillColorSecondary / Tertiary: translucent overlays, so they work on any surface.</summary>
    public static Color Hover => Dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
    public static Color Pressed => Dark ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0x00, 0x00, 0x00);
    public static Color Checked => Dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
    /// <summary>ControlFillColorDefault (address bar, search box); ControlFillColorInputActive while typing.</summary>
    public static Color FieldFill => Dark ? Color.FromArgb(0x38, 0x38, 0x38) : Color.White;
    public static Color FieldFillFocused => Dark ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.White;
    /// <summary>Explorer's address and search boxes have no visible border (their border brush is the background).</summary>
    public static Color FieldBorder => FieldFill;
    public static Color FieldHoverBorder => FieldFill;
    public static Color MenuBack => Dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
    public static Color MenuBorder => Dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xE0, 0xE0, 0xE0);
    public static Color MenuHover => Hover;

    /// <summary>Windows' default blue for text and marks, whatever the user's accent: "Everything" in the search box, the brand.</summary>
    public static Color Blue => Dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x67, 0xC0);

    /// <summary>Explorer's View › Compact view: its lists' rows closer together.</summary>
    public static bool CompactView
    {
        get
        {
            try
            {
                using var adv = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return adv?.GetValue("UseCompactMode") is int compact && compact != 0;
            }
            catch (Exception) { return false; }
        }
    }

    static Color? s_accentLight, s_accentDark;

    /// <summary>AccentFillColorDefault: the user's accent (SystemAccentColorDark1 in light, Light2 in dark).</summary>
    public static Color Accent
    {
        get
        {
            if (s_accentLight is null)
            {
                s_accentLight = Color.FromArgb(0x00, 0x5F, 0xB8);
                s_accentDark = Color.FromArgb(0x60, 0xCD, 0xFF);
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                    if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
                    {
                        s_accentDark = Color.FromArgb(p[4], p[5], p[6]);    // Light2
                        s_accentLight = Color.FromArgb(p[16], p[17], p[18]); // Dark1
                    }
                }
                catch (Exception) { }
            }
            return Dark ? s_accentDark!.Value : s_accentLight.Value;
        }
    }

    // Glyphs from Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).
    public const char GNew = '', GCut = '', GCopy = '', GPaste = '', GRename = '',
        GShare = '', GDelete = '', GSort = '', GView = '', GMore = '', GPane = '',
        GFilter = '', GClose = '', GZap = '', GGrid = '', GBack = '', GForward = '',
        GUp = '', GRefresh = '', GSearch = '', GChevronDown = '', GChevronRight = '',
        GCheck = '', GSelectAll = '', GSelectNone = '', GProperties = '', GSettings = '',
        GFolder = '', GNewFolder = '', GDetails = '', GPhoto = '';

    static string? s_uiFamily, s_iconFamily;

    /// <summary>"Segoe UI Variable Text" (Explorer's XAML ContentControlThemeFontFamily) or Segoe UI.</summary>
    public static string UiFamily => s_uiFamily ??= HasFamily("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string IconFamily => s_iconFamily ??= HasFamily("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static bool HasFamily(string name)
    {
        try { using var f = new FontFamily(name); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>ControlContentThemeFontSize is 14px = 10.5pt.</summary>
    public static Font UiFont(float points = 10.5f, FontStyle style = FontStyle.Regular) => new(UiFamily, points, style);

    /// <summary>A glyph rendered into a <paramref name="px"/>-square bitmap.</summary>
    public static Bitmap Glyph(char glyph, int px, Color color, float scale = 1f)
    {
        // Drawn as an anti-aliased outline: GDI text (TextRenderer) can't blend into a transparent bitmap, which left
        // curved glyphs like Refresh jagged. The icon fonts' em square is the glyph box, so this centres it.
        var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        using var family = new FontFamily(IconFamily);
        float em = px * scale, offset = (px - em) / 2f;
        float ascent = em * family.GetCellAscent(FontStyle.Regular) / family.GetEmHeight(FontStyle.Regular);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddString(glyph.ToString(), family, (int)FontStyle.Regular, em, new PointF(offset, offset - (ascent - em)), StringFormat.GenericTypographic);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        return bmp;
    }

    /// <summary>Explorer's own SVG icon when this Windows has it, else the icon-font glyph.</summary>
    public static Bitmap Icon16(string? svg, char glyph, int px)
        => (svg is null ? null : ExplorerIcons.Render(svg, px, Dark)) ?? Glyph(glyph, px, Icon);

    /// <summary>Draws a glyph centred in <paramref name="bounds"/> (font size in pixels).</summary>
    public static void DrawGlyph(Graphics g, char glyph, Rectangle bounds, float pixelSize, Color color)
    {
        using var font = new Font(IconFamily, pixelSize, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph.ToString(), font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Rectangle r, float radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new RectangleF(r.X, r.Y, r.Width - 0.5f, r.Height - 0.5f), radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    // ---- window frame ---------------------------------------------------------------------------

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38,
              DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
    const int DWMWCP_ROUND = 2, DWMSBT_MAINWINDOW = 2;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void SetDwm(IntPtr hwnd, int attr, int value)
    {
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch (Exception) { }
    }

    /// <summary>Explorer's tab strip: the Mica base colour (#202020 dark, #F3F3F3 light), even with accent title bars on.</summary>
    public static Color TitleBar => Dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);

    /// <summary>
    /// The frame like File Explorer's: Explorer draws its own title area (the tab strip) in the Mica base colour,
    /// so it never takes the accent colour; a standard caption is given that colour (and Mica behind it).
    /// </summary>
    public static void ApplyFrame(Form form)
    {
        SetDwm(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, Dark ? 1 : 0);
        SetDwm(form.Handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW);
        SetDwm(form.Handle, DWMWA_CAPTION_COLOR, ColorTranslator.ToWin32(TitleBar));
        SetDwm(form.Handle, DWMWA_TEXT_COLOR, ColorTranslator.ToWin32(Text));
    }

    /// <summary>A borderless popup with Windows 11's rounded corners, border and shadow.</summary>
    public static void ApplyPopupFrame(Form form)
    {
        SetDwm(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, Dark ? 1 : 0);
        SetDwm(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
    }

    // ---- menus --------------------------------------------------------------------------------

    static readonly HashSet<ToolStripDropDown> s_styled = [];

    /// <summary>
    /// Gives a drop-down (and, as they open, its submenus) Explorer's flyout look: rounded corners (OverlayCornerRadius),
    /// roomy rows, 14px text. Safe to call repeatedly; menus built while opening are styled as they open.
    /// </summary>
    public static void StyleDropDown(ToolStripDropDown dd)
    {
        if (!s_styled.Add(dd)) return;
        dd.Disposed += (_, _) => s_styled.Remove(dd);
        dd.HandleCreated += (_, _) => SetDwm(dd.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        if (dd.IsHandleCreated) SetDwm(dd.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        dd.Opening += (_, _) => PrepareItems(dd);
    }

    static Font? s_menuFont;

    static void PrepareItems(ToolStripDropDown dd)
    {
        int dpi = dd.DeviceDpi > 0 ? dd.DeviceDpi : 96;
        int S(int v) => v * dpi / 96;
        s_menuFont ??= UiFont(10.5f);
        // One layout pass for the whole menu: padding each row separately re-lays it out per row (seconds for 1000 rows).
        dd.SuspendLayout();
        try
        {
            if (!ReferenceEquals(dd.Font, s_menuFont)) dd.Font = s_menuFont;
            dd.Padding = new Padding(S(4), S(4), S(4), S(4));
            var row = new Padding(S(4), S(6), S(4), S(6));
            var sep = new Padding(0, S(2), 0, S(2));
            foreach (ToolStripItem item in dd.Items)
            {
                if (item is ToolStripSeparator) { if (item.Margin != sep) item.Margin = sep; continue; }
                if (item.Padding != row) item.Padding = row;
                if (item is ToolStripDropDownItem { HasDropDownItems: true } sub) StyleDropDown(sub.DropDown);
            }
        }
        finally { dd.ResumeLayout(); }
    }

    /// <summary>Styles every drop-down reachable from <paramref name="items"/> (menus that already exist).</summary>
    public static void StyleDropDowns(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripDropDownItem ddi) continue;
            StyleDropDown(ddi.DropDown);
            StyleDropDowns(ddi.DropDownItems);
        }
    }
}

/// <summary>Which Explorer surface a ToolStrip is (the renderer lays out and paints its items accordingly).</summary>
internal enum ExplorerBarKind { Plain, Command, Navigation, Status }

/// <summary>A ToolStrip that knows which Explorer surface it is.</summary>
internal sealed class ExplorerToolStrip : ToolStrip
{
    public ExplorerToolStrip(ExplorerBarKind kind)
    {
        Kind = kind;
        GripStyle = ToolStripGripStyle.Hidden;
        RenderMode = ToolStripRenderMode.ManagerRenderMode;
        ShowItemToolTips = true;
        LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ExplorerBarKind Kind { get; }

    /// <summary>Space between the buttons belongs to the window's title area (it drags the window), not to the bar.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PassEmptySpaceToWindow { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (PassEmptySpaceToWindow && m.Msg == 0x84 /* WM_NCHITTEST */)
        {
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (GetItemAt(p) == null) { m.Result = -1; /* HTTRANSPARENT */ return; }
        }
        base.WndProc(ref m);
    }

    /// <summary>Draw the 1px divider along the bottom (command bar).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool BottomDivider { get; set; }

    /// <summary>
    /// Explorer's AppBarButton layout (measured): icon-only buttons 40 wide; with a label: 12 | icon 16 | 8 | text | 8,
    /// plus 16 for a drop-down chevron. Height 43 (command bar) or 32 (navigation buttons).
    /// </summary>
    public void LayoutExplorerItems(Font labelFont)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        int height = Kind == ExplorerBarKind.Command ? S(43) : Kind == ExplorerBarKind.Status ? S(25) : S(32);
        foreach (ToolStripItem item in Items)
        {
            item.AutoSize = false;
            if (item is ToolStripSeparator)
            {
                item.Size = new Size(S(2), height);
                item.Margin = Padding.Empty;
                continue;
            }
            item.Font = labelFont;
            bool iconOnly = item.DisplayStyle == ToolStripItemDisplayStyle.Image || string.IsNullOrEmpty(item.Text);
            int width;
            if (Kind == ExplorerBarKind.Navigation || Kind == ExplorerBarKind.Status) width = height;
            else if (iconOnly) width = S(40) + (item is ToolStripSplitButton sb ? sb.DropDownButtonWidth : 0); // the gear's arrow
            else
            {
                int text = TextRenderer.MeasureText(item.Text, labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                width = S(12) + S(16) + S(8) + text + S(8) + (ExplorerRenderer.HasChevron(item) ? S(16) : 0);
            }
            item.Size = new Size(width, height);
            if (Kind == ExplorerBarKind.Command) item.Margin = new Padding(S(4), 0, S(4), 0);
        }
    }
}

/// <summary>Draws ToolStrips (command bar, navigation buttons, status bar) and menus like Windows 11 File Explorer.</summary>
internal sealed class ExplorerRenderer : ToolStripRenderer
{
    static int S(ToolStripItem item, int v) => v * (item.Owner?.DeviceDpi ?? 96) / 96;

    static ExplorerBarKind KindOf(ToolStripItem item) => item.Owner is ExplorerToolStrip ets ? ets.Kind : ExplorerBarKind.Plain;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripItem, System.Runtime.CompilerServices.StrongBox<float>> s_glow = new();

    /// <summary>An accent glow over a button, from 0 (none) to 1, to draw the eye to it (see MainForm.Hints.cs).</summary>
    public static void SetGlow(ToolStripItem item, float amount)
    {
        if (amount <= 0)
        {
            if (!s_glow.Remove(item)) return;
        }
        else s_glow.GetOrCreateValue(item).Value = Math.Min(1f, amount);
        item.Invalidate();
    }

    static void DrawGlow(Graphics g, ToolStripItem item, Rectangle r)
    {
        if (!s_glow.TryGetValue(item, out var glow) || item.Pressed || item.Selected) return;
        var accent = ExplorerTheme.Accent;
        ExplorerTheme.FillRounded(g, r, S(item, 4), Color.FromArgb((int)(glow.Value * 70), accent));
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Math.Max(1f, S(item, 2) * 0.75f);
        using var path = ExplorerTheme.RoundedRect(new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w - 0.5f, r.Height - w - 0.5f), S(item, 4));
        using var pen = new Pen(Color.FromArgb((int)(glow.Value * 255), accent), w);
        g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }

    public static bool HasChevron(ToolStripItem item) => item switch
    {
        ToolStripSplitButton => true,
        ToolStripDropDownButton dd => dd.ShowDropDownArrow,
        _ => false,
    };

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var color = e.ToolStrip is ToolStripDropDown ? ExplorerTheme.MenuBack : e.ToolStrip.BackColor;
        using var b = new SolidBrush(color);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var ts = e.ToolStrip;
        if (ts is ToolStripDropDown)
        {
            // Windows 11 rounds the window and draws its own border; on Windows 10 draw a thin one.
            if (Environment.OSVersion.Version.Build >= 22000) return;
            using var p = new Pen(ExplorerTheme.MenuBorder);
            e.Graphics.DrawRectangle(p, 0, 0, ts.Width - 1, ts.Height - 1);
            return;
        }
        if (ts is ExplorerToolStrip { BottomDivider: true })
        {
            using var p = new Pen(ExplorerTheme.Divider);
            e.Graphics.DrawLine(p, 0, ts.Height - 1, ts.Width, ts.Height - 1);
        }
    }

    protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }

    /// <summary>The visible button shape: command bar buttons sit 4px inside their 43px slot; others fill it.</summary>
    static Rectangle BackRect(ToolStripItem item)
    {
        var r = new Rectangle(Point.Empty, item.Size);
        if (KindOf(item) == ExplorerBarKind.Command) r.Inflate(0, -S(item, 4));
        return r;
    }

    static void DrawItemBack(Graphics g, ToolStripItem item, bool isChecked)
    {
        Color? c = item.Pressed ? ExplorerTheme.Pressed
                 : item.Selected && item.Enabled ? ExplorerTheme.Hover
                 : isChecked ? ExplorerTheme.Checked : null;
        if (c is null) return;
        ExplorerTheme.FillRounded(g, BackRect(item), S(item, 4), c.Value);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        => DrawItemBack(e.Graphics, e.Item, e.Item is ToolStripButton { Checked: true });

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        => DrawItemBack(e.Graphics, e.Item, false);

    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var sb = (ToolStripSplitButton)e.Item;
        bool on = sb.Tag is true; // a split button used as a toggle sets Tag = true while on
        Color? c = sb.Pressed || sb.ButtonPressed || sb.DropDownButtonPressed ? ExplorerTheme.Pressed
                 : sb.Selected && sb.Enabled ? ExplorerTheme.Hover
                 : on ? ExplorerTheme.Checked : null;
        var r = BackRect(sb);
        if (c is not null) ExplorerTheme.FillRounded(e.Graphics, r, S(sb, 4), c.Value);
        DrawGlow(e.Graphics, sb, r);
        if (sb.Selected && sb.Enabled)
        {
            var arrow = sb.DropDownButtonBounds;
            using var p = new SolidBrush(ExplorerTheme.Separator);
            e.Graphics.FillRectangle(p, arrow.Left, r.Top + S(sb, 6), Math.Max(1, S(sb, 1)), r.Height - S(sb, 12));
        }
        DrawChevron(e.Graphics, sb.DropDownButtonBounds, sb, down: true);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (item.Owner is not ToolStripDropDown)
        {
            DrawItemBack(e.Graphics, item, false);
            return;
        }
        if (!item.Selected || !item.Enabled) return;
        var r = new Rectangle(S(item, 2), 0, item.Width - S(item, 4), item.Height);
        ExplorerTheme.FillRounded(e.Graphics, r, S(item, 4), ExplorerTheme.MenuHover);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var item = e.Item;
        using var b = new SolidBrush(e.Item.Owner is ToolStripDropDown ? ExplorerTheme.Divider : ExplorerTheme.Separator);
        int t = Math.Max(1, S(item, 1));
        if (e.Vertical)
        {
            int h = Math.Min(item.Height, S(item, 32));
            e.Graphics.FillRectangle(b, (item.Width - t) / 2, (item.Height - h) / 2, t, h);
        }
        else e.Graphics.FillRectangle(b, S(item, 4), item.Height / 2, item.Width - S(item, 8), t);
    }

    // ---- item contents: Explorer's own layout for ExplorerToolStrip items ----------------------

    static bool OwnLayout(ToolStripItem item) => KindOf(item) is ExplorerBarKind.Command or ExplorerBarKind.Navigation or ExplorerBarKind.Status
                                                 && item.Owner is not ToolStripDropDown;

    static Rectangle IconRect(ToolStripItem item)
    {
        int px = S(item, 16);
        bool iconOnly = item.DisplayStyle == ToolStripItemDisplayStyle.Image || string.IsNullOrEmpty(item.Text);
        int x = iconOnly || KindOf(item) != ExplorerBarKind.Command ? (item.Width - (item is ToolStripSplitButton sb ? sb.DropDownButtonWidth : 0) - px) / 2 : S(item, 12);
        return new Rectangle(x, (item.Height - px) / 2, px, px);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        bool shortcut = item is ToolStripMenuItem mi && e.Text != mi.Text;
        var color = !item.Enabled ? ExplorerTheme.DisabledText : shortcut ? ExplorerTheme.SecondaryText : ExplorerTheme.Text;
        if (!OwnLayout(item) || KindOf(item) == ExplorerBarKind.Status && item is ToolStripStatusLabel)
        {
            e.TextColor = color;
            if (item.Owner is ToolStripDropDown) // centre menu text on the row (the padding otherwise lifts it above the icon)
                e.TextRectangle = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, item.Height);
            base.OnRenderItemText(e);
            return;
        }
        int x = S(item, 12 + 16 + 8);
        var r = new Rectangle(x, 0, Math.Max(0, item.Width - x), item.Height);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;
        var r = OwnLayout(e.Item) ? IconRect(e.Item) : e.ImageRectangle;
        if (e.Item.Enabled)
        {
            e.Graphics.DrawImage(e.Image, r);
            return;
        }
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4f });
        e.Graphics.DrawImage(e.Image, r, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attrs);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        if (r.Width <= 0) return;
        ExplorerTheme.DrawGlyph(e.Graphics, ExplorerTheme.GCheck, r, S(e.Item, 12), ExplorerTheme.Text);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is null) { base.OnRenderArrow(e); return; }
        var r = e.ArrowRectangle;
        if (OwnLayout(e.Item) && e.Item is ToolStripDropDownButton)
            r = new Rectangle(e.Item.Width - S(e.Item, 20), 0, S(e.Item, 12), e.Item.Height);
        DrawChevron(e.Graphics, r, e.Item, down: e.Direction == ArrowDirection.Down);
    }

    static void DrawChevron(Graphics g, Rectangle r, ToolStripItem item, bool down)
    {
        var color = item.Enabled ? ExplorerTheme.SecondaryText : ExplorerTheme.DisabledText;
        ExplorerTheme.DrawGlyph(g, down ? ExplorerTheme.GChevronDown : ExplorerTheme.GChevronRight, r, S(item, 8), color);
    }
}
