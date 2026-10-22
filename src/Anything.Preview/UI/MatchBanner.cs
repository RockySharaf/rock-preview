using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Anything.Preview.UI;

/// <summary>
/// One line above the preview saying where a search matched ("Page 4: …invoice…", "Keywords: sunset, beach"), with the
/// matched words bold, cut with an ellipsis when too long (the tooltip has it all), and an optional link on the right
/// ("Show original"). Hidden while it has nothing to say.
/// </summary>
internal sealed class MatchBanner : Control
{
    // Longer banners are cut before drawing; nobody reads past a screen width anyway.
    private const int MaxChars = 2000;
    private readonly LinkLabel _link = new()
    {
        AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 6, 0),
        LinkBehavior = LinkBehavior.HoverUnderline, Visible = false, UseMnemonic = false,
    };
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000 };
    private string _text = "";
    private (int Start, int Length)[] _bold = [];
    private Font? _boldFont;
    private bool _hasLink; // own flag: Control.Visible reads false while the banner itself is hidden

    /// <summary>The link was clicked.</summary>
    public event EventHandler? LinkClicked;

    public MatchBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(255, 248, 214);
        ForeColor = Color.FromArgb(64, 52, 16);
        Dock = DockStyle.Top;
        Visible = false;
        AccessibleRole = AccessibleRole.StaticText;
        Controls.Add(_link);
        _link.LinkClicked += (_, _) => LinkClicked?.Invoke(this, EventArgs.Empty);
        UpdateHeight();
    }

    /// <summary>The banner has text or a link.</summary>
    public bool HasContent => _text.Length > 0 || _hasLink;

    /// <summary>The text shown (line ends and control characters made spaces).</summary>
    public string BannerText => _text;

    /// <summary>The link's text; null = no link.</summary>
    public string? LinkText => _hasLink ? _link.Text : null;

    /// <summary>The bold parts of the shown text, '|' between them (diagnostics).</summary>
    internal string BoldText => string.Join("|", Array.ConvertAll(_bold, b => _text.Substring(b.Start, b.Length)));

    /// <summary>Shows a banner; null text and null link hide it.</summary>
    /// <param name="bold">Ranges of <paramref name="text"/> drawn bold (the matched words), as offsets into it as given.</param>
    /// <param name="linkTip">Tooltip of the link.</param>
    public void Set(string? text, IReadOnlyList<(int Start, int Length)>? bold, string? link, string? linkTip = null)
    {
        _text = Clean(text, out int dropped);
        _bold = Normalize(bold, _text.Length, dropped);
        _link.Text = link ?? "";
        _hasLink = !string.IsNullOrEmpty(link);
        _link.Visible = _hasLink;
        _tip.SetToolTip(this, _text.Length > 0 ? _text : null);
        _tip.SetToolTip(_link, linkTip);
        AccessibleName = _hasLink ?(_text.Length > 0 ? _text + ". " : "") + _link.Text : _text;
        Visible = HasContent;
        Invalidate();
    }

    /// <summary>
    /// The text on one line: line ends and control characters become spaces, blanks at both ends go, and it is cut at
    /// <see cref="MaxChars"/>; <paramref name="dropped"/> is how many characters left the start (the bold ranges move with it).
    /// </summary>
    private static string Clean(string? text, out int dropped)
    {
        dropped = 0;
        if (string.IsNullOrEmpty(text)) return "";
        while (dropped < text.Length && (char.IsWhiteSpace(text[dropped]) || char.IsControl(text[dropped]))) dropped++;
        var sb = new StringBuilder(Math.Min(text.Length - dropped, MaxChars));
        for (int i = dropped; i < text.Length && sb.Length < MaxChars; i++)
        {
            char c = text[i];
            sb.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Ranges of the text as given, moved back by the characters cut from its start, clamped, sorted and merged.</summary>
    private static (int Start, int Length)[] Normalize(IReadOnlyList<(int Start, int Length)>? ranges, int length, int dropped)
    {
        if (ranges is not { Count: > 0 } || length == 0) return [];
        var list = new List<(int Start, int Length)>();
        foreach (var (s, l) in ranges)
        {
            long start = (long)s - dropped, end = start + Math.Max(0, l);
            int a = (int)Math.Clamp(start, 0, length), b = (int)Math.Clamp(end, a, length);
            if (b > a) list.Add((a, b - a));
        }
        list.Sort((x, y) => x.Start.CompareTo(y.Start));
        // Merge overlaps so each character is drawn once.
        var merged = new List<(int Start, int Length)>();
        foreach (var r in list)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, r.Start + r.Length) - last.Start);
            }
            else merged.Add(r);
        }
        return merged.ToArray();
    }

    private void UpdateHeight() => Height = Font.Height + 10;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _boldFont?.Dispose();
        _boldFont = null;
        UpdateHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var line = new Pen(Color.FromArgb(232, 214, 150))) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        if (_text.Length == 0) return;
        _boldFont ??= new Font(Font, FontStyle.Bold);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        int x = 8, right = (_hasLink ? _link.Left : Width) - 8;
        var bounds = new Rectangle(0, 0, 0, Height - 1);
        int at = 0, b = 0;
        while (at < _text.Length && x < right)
        {
            bool isBold = b < _bold.Length && _bold[b].Start == at;
            int end = isBold ? _bold[b].Start + _bold[b].Length : b < _bold.Length ? _bold[b].Start : _text.Length;
            string piece = _text[at..end];
            var font = isBold ? _boldFont : Font;
            int w = TextRenderer.MeasureText(g, piece, font, new Size(int.MaxValue, Height), flags).Width;
            bounds.X = x;
            if (x + w > right)
            {
                // The rest doesn't fit: this piece ends with an ellipsis and nothing follows.
                bounds.Width = right - x;
                TextRenderer.DrawText(g, piece, font, bounds, ForeColor, flags | TextFormatFlags.EndEllipsis);
                break;
            }
            bounds.Width = w;
            TextRenderer.DrawText(g, piece, font, bounds, ForeColor, flags);
            x += w;
            at = end;
            if (isBold) b++;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
