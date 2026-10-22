using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Anything.Core.Content;

namespace Anything.Preview.UI;

/// <summary>
/// Read-only, owner-drawn monospace text view with line numbers. Virtualized: only visible lines are drawn, so a
/// 1 MB / 100k-line file shows instantly (RichTextBox/TextBox are far slower). Line selection with the mouse,
/// Ctrl+A / Ctrl+C, Ctrl+wheel font zoom. Lines end where the index says they do (<see cref="TextLines"/>: "\r\n",
/// '\n', a lone '\r' …), so line N in the gutter is the index's line N. Search matches are highlighted, F3 / Shift+F3
/// move between them, and a scroll target set before the view is laid out waits until it is.
/// </summary>
internal sealed class TextView : Control
{
    private const int MaxDisplayLineLength = 4000;
    // Match ranges of at most this many lines are kept (painted lines and F3 searches); the cache starts over beyond it.
    private const int MaxCachedLines = 20_000;
    // How long finding the first match to scroll to may take on its own; F3 (asked for) may take longer.
    private const int FirstMatchBudgetMs = 80, FindNextBudgetMs = 1500;
    private static readonly (int Start, int Length)[] NoRanges = [];
    private static readonly Color MatchColor = Color.FromArgb(255, 236, 130), CurrentColor = Color.FromArgb(255, 175, 70);

    private string _text = "";
    private string[] _lines = { "" };
    private int _maxLen;
    private readonly VScrollBar _v = new() { Dock = DockStyle.Right };
    private readonly HScrollBar _h = new() { Dock = DockStyle.Bottom };
    private Font _font;
    private int _lineH = 16, _charW = 8;
    private double _advance = 8; // exact advance of one character (GDI advances are whole pixels; _charW is it rounded)
    private int _selA = -1, _selB = -1;
    private bool _selecting;
    private float _fontSize = 10f;
    private bool _vOn, _hOn; // own flags: Control.Visible reads false while the parent is hidden

    private Func<string, IReadOnlyList<(int Start, int Length)>>? _find;
    private readonly Dictionary<int, (int Start, int Length)[]> _ranges = new();
    private int _curLine = -1, _curStart = -1; // the current match (F3 moves it); -1 = none
    private int _pendingLine;                  // 1-based line to scroll to once laid out; 0 = none
    private bool _pendingFirstMatch;           // scroll to the first match once laid out

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

    /// <summary>The number shown beside the first line (a window deep in a large file starts there).</summary>
    public int FirstLineNumber { get; private set; } = 1;

    /// <summary>The line number at the top of the view (diagnostics).</summary>
    internal int TopLine => _v.Value + FirstLineNumber;

    /// <summary>Line number and column of the current match; (0, -1) when none (diagnostics).</summary>
    internal (int Line, int Column) CurrentMatch => _curLine >= 0 ? (_curLine + FirstLineNumber, _curStart) : (0, -1);

    /// <summary>Shows a text; highlights, the current match and any pending scroll are cleared.</summary>
    public void SetText(string text, int firstLineNumber = 1)
    {
        _text = text ?? "";
        var raw = TextLines.Split(_text);
        var lines = new string[raw.Count];
        _maxLen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string l = raw[i];
            if (l.IndexOf('\t') >= 0) l = ExpandTabs(l);
            if (l.Length > MaxDisplayLineLength) l = l[..MaxDisplayLineLength] + " …";
            lines[i] = l;
            if (l.Length > _maxLen) _maxLen = l.Length;
        }
        _lines = lines;
        FirstLineNumber = Math.Max(1, firstLineNumber);
        _find = null;
        _ranges.Clear();
        _curLine = _curStart = -1;
        _pendingLine = 0;
        _pendingFirstMatch = false;
        _selA = _selB = -1;
        _v.Value = 0;
        _h.Value = 0;
        UpdateScroll();
        Invalidate();
    }

    /// <summary>
    /// Highlights what <paramref name="find"/> returns for each displayed line (after tab expansion and the line cut),
    /// worked out for painted lines only and cached; null removes the highlights.
    /// </summary>
    public void SetHighlights(Func<string, IReadOnlyList<(int Start, int Length)>>? find)
    {
        _find = find;
        _ranges.Clear();
        _curLine = _curStart = -1;
        Invalidate();
    }

    /// <summary>Scrolls so the 1-based line (as numbered in the gutter) shows a third of the way down, now or once laid out; its match becomes the current one.</summary>
    public void ScrollToLine(int line)
    {
        _pendingLine = Math.Max(1, line);
        _pendingFirstMatch = false;
        ApplyPending();
    }

    /// <summary>Scrolls to the first match (now or once laid out), if one is found quickly.</summary>
    public void ScrollToFirstMatch()
    {
        _pendingLine = 0;
        _pendingFirstMatch = true;
        ApplyPending();
    }

    /// <summary>Moves to the next (or previous) match, wrapping around; false when there is none.</summary>
    public bool FindNext(bool backwards)
    {
        if (_find == null) return false;
        int line = _curLine >= 0 ? _curLine : Math.Clamp(_v.Value, 0, _lines.Length - 1);
        int after = _curLine >= 0 ? _curStart : backwards ? int.MaxValue : -1;
        if (!Search(line, after, !backwards, wrap: true, FindNextBudgetMs, out int ln, out int start)) return false;
        _curLine = ln;
        _curStart = start;
        Reveal(ln, start, recentre: false);
        return true;
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
        _advance = Math.Max(1, sz.Width / 100.0);
        _charW = Math.Max(1, (int)Math.Round(_advance));
        _lineH = Math.Max(1, _font.Height);
    }

    private int GutterWidth => ShowLineNumbers ? ((FirstLineNumber + _lines.Length - 1).ToString().Length + 1) * _charW + 10 : 4;
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
        ApplyPending();
    }

    private int MaxV => Math.Max(0, _v.Maximum - _v.LargeChange + 1);
    private int MaxH => Math.Max(0, _h.Maximum - _h.LargeChange + 1);

    /// <summary>Applies a scroll target once the view has a size (UpdateScroll does nothing before that).</summary>
    private void ApplyPending()
    {
        if (_pendingLine == 0 && !_pendingFirstMatch) return;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_pendingLine > 0)
        {
            int idx = Math.Clamp(_pendingLine - FirstLineNumber, 0, _lines.Length - 1);
            _pendingLine = 0;
            // The match on that line, or the next one close below it, becomes the current one.
            int col = -1;
            if (_find != null && Search(idx, -1, forward: true, wrap: false, FirstMatchBudgetMs, out int ln, out int start) && ln - idx < VisibleLines)
            {
                _curLine = ln;
                _curStart = start;
                if (ln == idx) col = start;
            }
            Reveal(idx, col, recentre: true);
        }
        else
        {
            _pendingFirstMatch = false;
            if (_find != null && Search(0, -1, forward: true, wrap: false, FirstMatchBudgetMs, out int ln, out int start))
            {
                _curLine = ln;
                _curStart = start;
                Reveal(ln, start, recentre: true);
            }
        }
    }

    /// <summary>Brings a line (and a column on it) into view: a third of the way down when recentring or when it is off screen.</summary>
    private void Reveal(int line, int col, bool recentre)
    {
        int vis = VisibleLines;
        if (recentre || line < _v.Value || line >= _v.Value + vis)
            _v.Value = Math.Clamp(line - vis / 3, 0, MaxV);
        if (col >= 0 && _hOn)
        {
            int cols = VisibleCols;
            if (col < _h.Value || col >= _h.Value + cols - 4) _h.Value = Math.Clamp(col - cols / 4, 0, MaxH);
        }
        else if (recentre && col < 0) _h.Value = 0;
        Invalidate();
    }

    /// <summary>
    /// The next match from (line, after) on: forward, the first range starting after <paramref name="after"/> on that line
    /// or the first on a later line; backward, the last before it. Wraps around once when asked; gives up past the budget.
    /// </summary>
    private bool Search(int line, int after, bool forward, bool wrap, int budgetMs, out int foundLine, out int foundStart)
    {
        foundLine = foundStart = -1;
        int n = _lines.Length;
        var clock = Stopwatch.StartNew();
        for (int k = 0; k <= n; k++)
        {
            int ln = forward ? line + k : line - k;
            if (!wrap && (ln < 0 || ln >= n)) return false;
            ln = (ln % n + n) % n;
            var r = RangesOf(ln);
            if (r.Length > 0)
            {
                int pick = -1;
                if (forward)
                {
                    for (int i = 0; i < r.Length && pick < 0; i++)
                        if (k == 0 ? r[i].Start > after : k < n || r[i].Start <= after) pick = i;
                }
                else
                {
                    for (int i = r.Length - 1; i >= 0 && pick < 0; i--)
                        if (k == 0 ? r[i].Start < after : k < n || r[i].Start >= after) pick = i;
                }
                if (pick >= 0)
                {
                    foundLine = ln;
                    foundStart = r[pick].Start;
                    return true;
                }
            }
            if ((k & 255) == 255 && clock.ElapsedMilliseconds > budgetMs) return false;
        }
        return false;
    }

    /// <summary>Match ranges on a displayed line, sorted, inside the line; cached.</summary>
    private (int Start, int Length)[] RangesOf(int ln)
    {
        if (_find == null || (uint)ln >= (uint)_lines.Length) return NoRanges;
        if (_ranges.TryGetValue(ln, out var cached)) return cached;
        string line = _lines[ln];
        var result = NoRanges;
        if (line.Length > 0)
        {
            IReadOnlyList<(int Start, int Length)>? found = null;
            try { found = _find(line); }
            catch (Exception e) when (e is not OutOfMemoryException) { found = null; } // a finder bug must not take the view down
            if (found is { Count: > 0 })
            {
                var list = new List<(int Start, int Length)>(found.Count);
                foreach (var (s, l) in found)
                {
                    int a = Math.Clamp(s, 0, line.Length), b = Math.Clamp(s + Math.Max(0, l), a, line.Length);
                    if (b > a) list.Add((a, b - a));
                }
                list.Sort((x, y) => x.Start.CompareTo(y.Start));
                if (list.Count > 0) result = list.ToArray();
            }
        }
        if (_ranges.Count >= MaxCachedLines) _ranges.Clear();
        _ranges[ln] = result;
        return result;
    }

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
        using var matchBrush = new SolidBrush(MatchColor);
        using var currentBrush = new SolidBrush(CurrentColor);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;
        for (int i = 0; i < vis; i++)
        {
            int ln = top + i;
            if (ln >= _lines.Length) break;
            int y = i * _lineH;
            if (_selA >= 0 && ln >= s0 && ln <= s1) g.FillRectangle(selBrush, gw - 4, y, width - gw + 4, _lineH);
            if (ShowLineNumbers)
                TextRenderer.DrawText(g, (ln + FirstLineNumber).ToString(), _font, new Rectangle(0, y, gw - 10, _lineH), Color.FromArgb(150, 150, 150),
                    flags | TextFormatFlags.Right);
            string line = _lines[ln];
            if (_find != null) PaintMatches(g, line, ln, left, cols, gw, y, width, matchBrush, currentBrush, flags);
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

    /// <summary>Fills the visible part of each match on a line behind its text.</summary>
    private void PaintMatches(Graphics g, string line, int ln, int left, int cols, int gw, int y, int width, Brush match, Brush current, TextFormatFlags flags)
    {
        foreach (var (s, l) in RangesOf(ln))
        {
            int a = Math.Max(s, left), b = Math.Min(s + l, Math.Min(line.Length, left + cols));
            if (b <= a) continue;
            int x0 = gw + ColumnX(g, line, left, a, flags), x1 = gw + ColumnX(g, line, left, b, flags);
            if (x0 >= width) continue;
            g.FillRectangle(ln == _curLine && s == _curStart ? current : match, x0, y, Math.Max(2, Math.Min(x1, width) - x0), _lineH);
        }
    }

    /// <summary>Where the character at idx starts, from the first shown column: monospace for ASCII, measured otherwise (wide and fallback glyphs).</summary>
    private int ColumnX(Graphics g, string line, int left, int idx, TextFormatFlags flags)
    {
        if (idx <= left) return 0;
        bool ascii = true;
        for (int i = left; i < idx && ascii; i++) ascii = line[i] < 0x80;
        if (ascii) return (int)Math.Round((idx - left) * _advance);
        return TextRenderer.MeasureText(g, line.Substring(left, idx - left), _font, new Size(int.MaxValue, _lineH), flags).Width;
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
            case Keys.F3 when _find != null && !e.Control && !e.Alt: FindNext(e.Shift); break;
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
