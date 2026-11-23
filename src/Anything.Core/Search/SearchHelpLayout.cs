namespace Anything.Core.Search;

/// <summary>The faces the help is drawn in; the window picks the real fonts for its scaling.</summary>
public enum HelpFont { Title, Heading, Small, Body, Code }

public enum HelpBoxKind { Title, Intro, Heading, Subtitle, Card, CodeChip, ExampleChip, Meaning, Note }

/// <summary>A rectangle in device pixels, from the top of the help (not of the window: the window scrolls it).</summary>
public readonly record struct HelpRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public bool Intersects(HelpRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>
/// One thing to draw. For chips, <see cref="Text"/> is drawn inside <see cref="Bounds"/> less the chip padding;
/// <see cref="Clipped"/> says it had to be cut short with an ellipsis (only when one chip is wider than the whole card,
/// which the window's minimum width keeps from happening).
/// </summary>
/// <param name="Section">The section it belongs to, or -1 (the title and intro).</param>
/// <param name="Entry">The entry it belongs to, or -1 (headings, cards, notes).</param>
public sealed record HelpBox(HelpBoxKind Kind, HelpRect Bounds, string Text, int Section, int Entry, bool Clipped = false);

/// <summary>An entry's band across its card: what a right-click or a screen reader treats as one item.</summary>
public sealed record HelpRow(int Section, int Entry, HelpRect Bounds, bool Stacked);

/// <summary>Spacing in device pixels: <see cref="Scaled"/> gives the help's own at a scaling.</summary>
public sealed record HelpMetrics(
    int Margin, int CardPadX, int CardPadY, int RowGap, int SectionGap, int HeadingGap, int SubtitleGap,
    int ChipPadX, int ChipPadY, int ChipGap, int ChipLineGap, int ColumnGap, int ExampleGap, int NoteGap, int IntroGap)
{
    /// <summary>At most this share of a card's inside goes to the column of what to type.</summary>
    public double CodeShare { get; init; } = 0.34;

    /// <summary>The help's spacing at <paramref name="dpi"/> (96 = 100%).</summary>
    public static HelpMetrics Scaled(int dpi)
    {
        int S(int v) => (int)Math.Round(v * dpi / 96.0);
        return new(Margin: S(24), CardPadX: S(18), CardPadY: S(14), RowGap: S(12), SectionGap: S(28), HeadingGap: S(8),
            SubtitleGap: S(2), ChipPadX: S(6), ChipPadY: S(2), ChipGap: S(6), ChipLineGap: S(4), ColumnGap: S(16),
            ExampleGap: S(6), NoteGap: S(8), IntroGap: S(20));
    }
}

/// <summary>
/// Where everything in the Search Syntax help goes at a width: a title, then per section a heading over a card with a
/// row per entry: what to type as chips in a column at the left, the meaning wrapping at the right, examples as chips
/// under it. The column is as wide as the widest chip (up to <see cref="HelpMetrics.CodeShare"/> of the card) across
/// ALL sections, so the columns line up from card to card; several chips wrap in it. A code with a chip wider than it goes on its own
/// line, with the meaning under it ("stacked"), so nothing is ever cut off at a narrow width. Kept here, apart from
/// painting, so the arithmetic is tested.
/// </summary>
public sealed class SearchHelpLayout
{
    /// <summary>Measures text in a face: on one line when <c>maxWidth</c> is 0, else wrapped at that width.</summary>
    public delegate (int Width, int Height) Measure(string text, HelpFont font, int maxWidth);

    public IReadOnlyList<HelpBox> Boxes { get; }
    public IReadOnlyList<HelpRow> Rows { get; }
    /// <summary>The whole help's height, bottom margin included.</summary>
    public int Height { get; }
    public int Width { get; }
    /// <summary>The width of the column of what to type.</summary>
    public int CodeColumn { get; }

    SearchHelpLayout(List<HelpBox> boxes, List<HelpRow> rows, int width, int height, int codeColumn)
    {
        Boxes = boxes;
        Rows = rows;
        Width = width;
        Height = height;
        CodeColumn = codeColumn;
    }

    /// <summary>The row at a point of the help, or null.</summary>
    public HelpRow? RowAt(int x, int y)
    {
        foreach (var r in Rows) if (r.Bounds.Contains(x, y)) return r;
        return null;
    }

    /// <summary>The chip at a point of the help, or null.</summary>
    public HelpBox? ChipAt(int x, int y)
    {
        foreach (var b in Boxes)
            if (b.Kind is HelpBoxKind.CodeChip or HelpBoxKind.ExampleChip && b.Bounds.Contains(x, y)) return b;
        return null;
    }

    public static SearchHelpLayout Arrange(IReadOnlyList<HelpSection> sections, string title, string? intro,
                                           int width, HelpMetrics m, Measure measure)
    {
        var boxes = new List<HelpBox>();
        var rows = new List<HelpRow>();
        int inner = Math.Max(1, width - 2 * m.Margin);
        int cardInner = Math.Max(1, inner - 2 * m.CardPadX);
        int codeLine = measure("Ag", HelpFont.Code, 0).Height;
        int bodyLine = measure("Ag", HelpFont.Body, 0).Height;
        int chipHeight = codeLine + 2 * m.ChipPadY;

        int ChipWidth(string text) => measure(text, HelpFont.Code, 0).Width + 2 * m.ChipPadX;

        // The column: as wide as the widest chip that fits the share, across every section so the columns line up.
        // Several chips wrap inside it; a code with a chip wider than it goes above its meaning (stacked).
        int cap = Math.Max(1, (int)(cardInner * m.CodeShare));
        int column = 0;
        foreach (var s in sections)
            foreach (var e in s.Entries)
                foreach (var c in e.Code)
                    if (ChipWidth(c) is var w && w <= cap) column = Math.Max(column, w);

        int y = m.Margin;
        int Text(HelpBoxKind kind, string text, HelpFont font, int x, int top, int w, int section, int entry)
        {
            int h = measure(text, font, w).Height;
            boxes.Add(new HelpBox(kind, new HelpRect(x, top, w, h), text, section, entry));
            return h;
        }

        // Chips flow left to right and wrap as whole chips; one wider than the room is cut short (see HelpBox).
        int Flow(IReadOnlyList<string> texts, HelpBoxKind kind, int x0, int top, int room, int section, int entry)
        {
            int x = x0, lineTop = top;
            foreach (var t in texts)
            {
                int w = ChipWidth(t);
                bool clipped = w > room;
                if (clipped) w = room;
                if (x > x0 && x + w > x0 + room)
                {
                    x = x0;
                    lineTop += chipHeight + m.ChipLineGap;
                }
                boxes.Add(new HelpBox(kind, new HelpRect(x, lineTop, w, chipHeight), t, section, entry, clipped));
                x += w + m.ChipGap;
            }
            return texts.Count == 0 ? 0 : lineTop + chipHeight - top;
        }

        y += Text(HelpBoxKind.Title, title, HelpFont.Title, m.Margin, y, inner, -1, -1);
        if (!string.IsNullOrEmpty(intro))
        {
            y += m.HeadingGap;
            y += Text(HelpBoxKind.Intro, intro, HelpFont.Small, m.Margin, y, inner, -1, -1);
        }
        y += m.IntroGap;

        for (int si = 0; si < sections.Count; si++)
        {
            var s = sections[si];
            if (si > 0) y += m.SectionGap;
            y += Text(HelpBoxKind.Heading, s.Title, HelpFont.Heading, m.Margin, y, inner, si, -1);
            if (s.Subtitle is not null)
            {
                y += m.SubtitleGap;
                y += Text(HelpBoxKind.Subtitle, s.Subtitle, HelpFont.Small, m.Margin, y, inner, si, -1);
            }
            y += m.HeadingGap;
            int cardTop = y, cardIndex = boxes.Count;
            boxes.Add(null!); // the card goes under its rows, so it is drawn first: put in its place once its height is known
            int left = m.Margin + m.CardPadX;
            y += m.CardPadY;
            for (int ei = 0; ei < s.Entries.Length; ei++)
            {
                var e = s.Entries[ei];
                if (ei > 0) y += m.RowGap;
                int rowTop = y;
                bool stacked = column <= 0 || e.Code.Any(c => ChipWidth(c) > column);
                int meaningX = stacked ? left : left + column + m.ColumnGap;
                int meaningW = stacked ? cardInner : cardInner - column - m.ColumnGap;
                if (!stacked && meaningW < cardInner / 3)
                {
                    stacked = true; // too little room beside the column: under it reads better than a narrow strip
                    meaningX = left;
                    meaningW = cardInner;
                }
                int codeBottom = rowTop + Flow(e.Code, HelpBoxKind.CodeChip, left, rowTop, stacked ? cardInner : column, si, ei);
                // The meaning's first line sits level with the chips' text beside them, or under the chips when stacked.
                int meaningTop = stacked ? codeBottom + m.ChipLineGap : rowTop + Math.Max(0, (chipHeight - bodyLine) / 2);
                int bottom = meaningTop + Text(HelpBoxKind.Meaning, e.Meaning, HelpFont.Body, meaningX, meaningTop, meaningW, si, ei);
                if (e.Examples.Length > 0)
                {
                    // Under the meaning, in its column; across the card if one example is wider than the column.
                    bool wide = e.Examples.Any(x => ChipWidth(x) > meaningW);
                    int exTop = (wide ? Math.Max(bottom, codeBottom) : bottom) + m.ExampleGap;
                    bottom = exTop + Flow(e.Examples, HelpBoxKind.ExampleChip, wide ? left : meaningX, exTop, wide ? cardInner : meaningW, si, ei);
                }
                bottom = Math.Max(bottom, codeBottom);
                rows.Add(new HelpRow(si, ei, new HelpRect(m.Margin, rowTop, inner, bottom - rowTop), stacked));
                y = bottom;
            }
            y += m.CardPadY;
            boxes[cardIndex] = new HelpBox(HelpBoxKind.Card, new HelpRect(m.Margin, cardTop, inner, y - cardTop), "", si, -1);
            if (s.Note is not null)
            {
                y += m.NoteGap;
                y += Text(HelpBoxKind.Note, s.Note, HelpFont.Small, m.Margin, y, inner, si, -1);
            }
        }
        y += m.Margin;
        return new SearchHelpLayout(boxes, rows, width, y, column);
    }
}
