using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Anything.Preview.UI;

/// <summary>
/// Read-only, owner-drawn monospace text view with line numbers. Virtualized: only visible lines are drawn, so a
/// 1 MB / 100k-line file shows instantly (RichTextBox/TextBox are far slower). Line selection with the mouse,
/// Ctrl+A / Ctrl+C, Ctrl+wheel font zoom.
/// </summary>
internal sealed class TextView : Control
{
    private const int MaxDisplayLineLength = 4000;
    private string _text = "";
    private string[] _lines = { "" };
    private int _maxLen;
    private readonly VScrollBar _v = new() { Dock = DockStyle.Right };
    private readonly HScrollBar _h = new() { Dock = DockStyle.Bottom };
    private Font _font;
    private int _lineH = 16, _charW = 8;
    private int _selA = -1, _selB = -1;
    private bool _selecting;
    private float _fontSize = 10f;
    private bool _vOn, _hOn; // own flags: Control.Visible reads false while the parent is hidden

    public TextView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = SystemColors.Window;
        ForeColor = Color.FromArgb(30, 30, 30);
        _font = new Font("Consolas", _fontSize);
        Controls.Add(_v);
        Controls.Add(_h);
        _v.Scroll += (_, _) => Invalidate();
        _h.Scroll += (_, _) => Invalidate();
        _v.ValueChanged += (_, _) => Invalidate();
        _h.ValueChanged += (_, _) => Invalidate();
        Measure();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (_, _) => Copy());
        menu.Items.Add("Select all", null, (_, _) => SelectAll());
        ContextMenuStrip = menu;
    }

    public bool ShowLineNumbers { get; set; } = true;
    public int LineCount => _lines.Length;

    public void SetText(string text)
    {
        _text = text ?? "";
        var raw = _text.Split('\n');
        _maxLen = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            string l = raw[i];
            if (l.Length > 0 && l[^1] == '\r') l = l[..^1];
            if (l.IndexOf('\t') >= 0) l = ExpandTabs(l);
            if (l.Length > MaxDisplayLineLength) l = l[..MaxDisplayLineLength] + " …";
            raw[i] = l;
            if (l.Length > _maxLen) _maxLen = l.Length;
        }
        _lines = raw;
        _selA = _selB = -1;
        _v.Value = 0;
        _h.Value = 0;
        UpdateScroll();
        Invalidate();
    }

    private static string ExpandTabs(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s)
        {
            if (c == '\t') sb.Append(' ', 4 - sb.Length % 4);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private void Measure()
    {
        var sz = TextRenderer.MeasureText(new string('M', 100), _font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        _charW = Math.Max(1, (int)Math.Round(sz.Width / 100.0));
        _lineH = Math.Max(1, _font.Height);
    }

    private int GutterWidth => ShowLineNumbers ? (_lines.Length.ToString().Length + 1) * _charW + 10 : 4;
    private int VisibleLines => Math.Max(1, (ClientSize.Height - (_hOn ? _h.Height : 0)) / _lineH);
    private int VisibleCols => Math.Max(1, (ClientSize.Width - GutterWidth - (_vOn ? _v.Width : 0)) / _charW);

    private void UpdateScroll()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        int vis = VisibleLines;
        _v.Minimum = 0;
        _v.Maximum = Math.Max(0, _lines.Length - 1);
        _v.LargeChange = Math.Max(1, vis);
        _v.SmallChange = 1;
        _vOn = _lines.Length > vis; _v.Visible = _vOn;
        int cols = VisibleCols;
        _h.Minimum = 0;
        _h.Maximum = Math.Max(0, _maxLen + 2);
        _h.LargeChange = Math.Max(1, cols);
        _hOn = _maxLen + 2 > cols; _h.Visible = _hOn;
        if (_v.Value > MaxV) _v.Value = MaxV;
        if (_h.Value > MaxH) _h.Value = MaxH;
    }

    private int MaxV => Math.Max(0, _v.Maximum - _v.LargeChange + 1);
    private int MaxH => Math.Max(0, _h.Maximum - _h.LargeChange + 1);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int top = _v.Value, left = _h.Value, gw = GutterWidth;
        int vis = VisibleLines + 1, cols = VisibleCols + 2;
        int width = ClientSize.Width - (_vOn ? _v.Width : 0);
        if (ShowLineNumbers)
        {
            using var gb = new SolidBrush(Color.FromArgb(247, 247, 247));
            g.FillRectangle(gb, 0, 0, gw - 4, ClientSize.Height);
        }
        int s0 = Math.Min(_selA, _selB), s1 = Math.Max(_selA, _selB);
        using var selBrush = new SolidBrush(Color.FromArgb(204, 228, 247));
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;
        for (int i = 0; i < vis; i++)
        {
            int ln = top + i;
            if (ln >= _lines.Length) break;
            int y = i * _lineH;
            if (_selA >= 0 && ln >= s0 && ln <= s1) g.FillRectangle(selBrush, gw - 4, y, width - gw + 4, _lineH);
            if (ShowLineNumbers)
                TextRenderer.DrawText(g, (ln + 1).ToString(), _font, new Rectangle(0, y, gw - 10, _lineH), Color.FromArgb(150, 150, 150),
                    flags | TextFormatFlags.Right);
            string line = _lines[ln];
            if (left < line.Length)
            {
                string part = line.Substring(left, Math.Min(cols, line.Length - left));
                TextRenderer.DrawText(g, part, _font, new Point(gw, y), ForeColor, flags);
            }
        }
        if (ShowLineNumbers)
        {
            using var p = new Pen(Color.FromArgb(225, 225, 225));
            g.DrawLine(p, gw - 4, 0, gw - 4, ClientSize.Height);
        }
    }

    private void ScrollBy(int lines)
    {
        _v.Value = Math.Clamp(_v.Value + lines, 0, MaxV);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0)
        {
            _fontSize = Math.Clamp(_fontSize + (e.Delta > 0 ? 1 : -1), 6, 36);
            _font.Dispose();
            _font = new Font("Consolas", _fontSize);
            Measure();
            UpdateScroll();
            Invalidate();
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            _h.Value = Math.Clamp(_h.Value - e.Delta / 120 * 8, 0, MaxH);
            return;
        }
        ScrollBy(-e.Delta / 120 * SystemInformation.MouseWheelScrollLines);
    }

    private int LineAt(int y) => Math.Clamp(_v.Value + y / _lineH, 0, _lines.Length - 1);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _selA = _selB = LineAt(e.Y);
        _selecting = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;
        _selB = LineAt(e.Y);
        if (e.Y < 0) ScrollBy(-1);
        else if (e.Y > ClientSize.Height) ScrollBy(1);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _selecting = false;
        Capture = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollBy(-1); break;
            case Keys.Down: ScrollBy(1); break;
            case Keys.PageUp: ScrollBy(-VisibleLines); break;
            case Keys.PageDown: ScrollBy(VisibleLines); break;
            case Keys.Home: if (e.Control) _v.Value = 0; else _h.Value = 0; break;
            case Keys.End: if (e.Control) _v.Value = MaxV; break;
            case Keys.Left: _h.Value = Math.Max(0, _h.Value - 4); break;
            case Keys.Right: _h.Value = Math.Min(MaxH, _h.Value + 4); break;
            case Keys.A when e.Control: SelectAll(); break;
            case Keys.C when e.Control: Copy(); break;
            default: return;
        }
        e.Handled = true;
    }

    public void SelectAll()
    {
        _selA = 0;
        _selB = _lines.Length - 1;
        Invalidate();
    }

    public void Copy()
    {
        string text;
        if (_selA < 0 || (Math.Min(_selA, _selB) == 0 && Math.Max(_selA, _selB) == _lines.Length - 1)) text = _text;
        else
        {
            int s0 = Math.Min(_selA, _selB), s1 = Math.Max(_selA, _selB);
            text = string.Join(Environment.NewLine, _lines, s0, s1 - s0 + 1);
        }
        if (text.Length > 0)
        {
            try { Clipboard.SetText(text); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
