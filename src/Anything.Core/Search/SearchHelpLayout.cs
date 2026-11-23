namespace Anything.Core.Search;

/// <summary>The faces the help is drawn in; the window picks the real fonts for its scaling.</summary>
public enum HelpFont { Title, Heading, Small, Body, Code, Name }

/// <summary>What a box is; <see cref="Name"/> is a row's plain bold name (parts with <see cref="HelpLabel.Names"/>).</summary>
public enum HelpBoxKind { Title, Intro, Heading, Subtitle, Card, CodeChip, ExampleChip, Meaning, Note, Name }

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
/// <param name="Section">The section it belongs to, counted across every part (see <see cref="SearchHelpLayout.Sections"/>), or -1 (a part's title and intro).</param>
/// <param name="Entry">The entry it belongs to, or -1 (headings, cards, notes).</param>
/// <param name="Part">The part it belongs to.</param>
public sealed record HelpBox(HelpBoxKind Kind, HelpRect Bounds, string Text, int Section, int Entry, bool Clipped = false, int Part = 0);

/// <summary>An entry's band across its card: what a right-click or a screen reader treats as one item.</summary>
public sealed record HelpRow(int Section, int Entry, HelpRect Bounds, bool Stacked, int Part = 0);

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
/// Where everything in the help goes at a width: per part a big heading and its intro, then per section a heading over
/// a card with a row per entry: what it names at the left (chips, or a plain bold name), the meaning wrapping at the
/// right, examples as chips under it. The left column is as wide as the widest name (up to
/// <see cref="HelpMetrics.CodeShare"/> of the card) within the part, so the columns line up from card to card and a
/// long name in one part never moves another part's column; several chips wrap in it, and a bold name wraps in it. A
/// row whose chip (or a word of whose name) is wider than the column goes on its own line, with the meaning under it
/// ("stacked"), so nothing is ever cut off at a narrow width. Kept here, apart from painting, so the arithmetic is tested.
/// </summary>
public sealed class SearchHelpLayout
{
    /// <summary>Measures text in a face: on one line when <c>maxWidth</c> is 0, else wrapped at that width.</summary>
    public delegate (int Width, int Height) Measure(string text, HelpFont font, int maxWidth);

    public IReadOnlyList<HelpBox> Boxes { get; }
    public IReadOnlyList<HelpRow> Rows { get; }
    /// <summary>Every part's sections one after another: <see cref="HelpBox.Section"/> and <see cref="HelpRow.Section"/> count in this list.</summary>
    public IReadOnlyList<HelpSection> Sections { get; }
    /// <summary>The top of each part's heading, in parts' order: where the strip's buttons scroll to.</summary>
    public IReadOnlyList<int> PartTops { get; }
    /// <summary>Each part's left column width, in parts' order.</summary>
    public IReadOnlyList<int> Columns { get; }
    /// <summary>The whole help's height, bottom margin included.</summary>
    public int Height { get; }
    public int Width { get; }

    SearchHelpLayout(List<HelpBox> boxes, List<HelpRow> rows, List<HelpSection> sections, List<int> partTops,
                     List<int> columns, int width, int height)
    {
        Boxes = boxes;
        Rows = rows;
        Sections = sections;
        PartTops = partTops;
        Columns = columns;
        Width = width;
        Height = height;
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

    /// <summary>
    /// The part being read with the help scrolled down by <paramref name="scrollY"/>: the last one whose heading is at
    /// the top or above it (<paramref name="slack"/> pixels below the top count, so a heading just scrolled to counts).
    /// </summary>
    public int PartAt(int scrollY, int slack)
    {
        int part = 0;
        for (int i = 0; i < PartTops.Count; i++) if (PartTops[i] <= scrollY + slack) part = i;
        return part;
    }

    /// <summary>One part's sections under its title and intro, as the syntax help was before it had parts.</summary>
    public static SearchHelpLayout Arrange(IReadOnlyList<HelpSection> sections, string title, string? intro,
                                           int width, HelpMetrics m, Measure measure) =>
        Arrange([new HelpPart(title, intro, sections, HelpLabel.Chips)], width, m, measure);

    public static SearchHelpLayout Arrange(IReadOnlyList<HelpPart> parts, int width, HelpMetrics m, Measure measure)
    {
        var boxes = new List<HelpBox>();
        var rows = new List<HelpRow>();
        var allSections = new List<HelpSection>();
        var partTops = new List<int>();
        var columns = new List<int>();
        int inner = Math.Max(1, width - 2 * m.Margin);
        int cardInner = Math.Max(1, inner - 2 * m.CardPadX);
        int codeLine = measure("Ag", HelpFont.Code, 0).Height;
        int bodyLine = measure("Ag", HelpFont.Body, 0).Height;
        int chipHeight = codeLine + 2 * m.ChipPadY;
        int cap = Math.Max(1, (int)(cardInner * m.CodeShare));

        int ChipWidth(string text) => measure(text, HelpFont.Code, 0).Width + 2 * m.ChipPadX;
        int NameWidth(string text) => measure(text, HelpFont.Name, 0).Width;
        // A bold name wraps in the column, but a word wider than the column can't: the row is stacked instead.
        int WidestWord(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameWidth).DefaultIfEmpty(0).Max();

        int y = m.Margin;
        int Text(HelpBoxKind kind, string text, HelpFont font, int x, int top, int w, int section, int entry, int part)
        {
            int h = measure(text, font, w).Height;
            boxes.Add(new HelpBox(kind, new HelpRect(x, top, w, h), text, section, entry, Part: part));
            return h;
        }

        // Chips flow left to right and wrap as whole chips; one wider than the room is cut short (see HelpBox).
        int Flow(IReadOnlyList<string> texts, HelpBoxKind kind, int x0, int top, int room, int section, int entry, int part)
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
                boxes.Add(new HelpBox(kind, new HelpRect(x, lineTop, w, chipHeight), t, section, entry, clipped, part));
                x += w + m.ChipGap;
            }
            return texts.Count == 0 ? 0 : lineTop + chipHeight - top;
        }

        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            bool names = part.Label == HelpLabel.Names;
            if (pi > 0) y += 2 * m.SectionGap; // a part starts further down than a section
            partTops.Add(y);
            y += Text(HelpBoxKind.Title, part.Title, HelpFont.Title, m.Margin, y, inner, -1, -1, pi);
            if (!string.IsNullOrEmpty(part.Intro))
            {
                y += m.HeadingGap;
                y += Text(HelpBoxKind.Intro, part.Intro, HelpFont.Small, m.Margin, y, inner, -1, -1, pi);
            }
            y += m.IntroGap;

            // The column: as wide as the widest name that fits the share, across this part's sections only, so the
            // columns line up within the part. A bold name wider than the share wraps in it (the column is then the share).
            int column = 0;
            foreach (var s in part.Sections)
                foreach (var e in s.Entries)
                    if (names) column = Math.Max(column, Math.Min(cap, NameWidth(string.Join(" ", e.Code))));
                    else
                        foreach (var c in e.Code)
                            if (ChipWidth(c) is var w && w <= cap) column = Math.Max(column, w);
            columns.Add(column);

            for (int ps = 0; ps < part.Sections.Count; ps++)
            {
                var s = part.Sections[ps];
                int si = allSections.Count;
                allSections.Add(s);
                if (ps > 0) y += m.SectionGap;
                y += Text(HelpBoxKind.Heading, s.Title, HelpFont.Heading, m.Margin, y, inner, si, -1, pi);
                if (s.Subtitle is not null)
                {
                    y += m.SubtitleGap;
                    y += Text(HelpBoxKind.Subtitle, s.Subtitle, HelpFont.Small, m.Margin, y, inner, si, -1, pi);
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
                    string name = string.Join(" ", e.Code);
                    bool stacked = column <= 0 || (names ? WidestWord(name) > column : e.Code.Any(c => ChipWidth(c) > column));
                    int meaningX = stacked ? left : left + column + m.ColumnGap;
                    int meaningW = stacked ? cardInner : cardInner - column - m.ColumnGap;
                    if (!stacked && meaningW < cardInner / 3)
                    {
                        stacked = true; // too little room beside the column: under it reads better than a narrow strip
                        meaningX = left;
                        meaningW = cardInner;
                    }
                    int codeBottom = names
                        ? rowTop + Text(HelpBoxKind.Name, name, HelpFont.Name, left, rowTop, stacked ? cardInner : column, si, ei, pi)
                        : rowTop + Flow(e.Code, HelpBoxKind.CodeChip, left, rowTop, stacked ? cardInner : column, si, ei, pi);
                    // The meaning's first line sits level with the name or the chips' text beside it, or under them when stacked.
                    int meaningTop = stacked ? codeBottom + m.ChipLineGap
                        : names ? rowTop : rowTop + Math.Max(0, (chipHeight - bodyLine) / 2);
                    int bottom = meaningTop + Text(HelpBoxKind.Meaning, e.Meaning, HelpFont.Body, meaningX, meaningTop, meaningW, si, ei, pi);
                    if (e.Examples.Length > 0)
                    {
                        // Under the meaning, in its column; across the card if one example is wider than the column.
                        bool wide = e.Examples.Any(x => ChipWidth(x) > meaningW);
                        int exTop = (wide ? Math.Max(bottom, codeBottom) : bottom) + m.ExampleGap;
                        bottom = exTop + Flow(e.Examples, HelpBoxKind.ExampleChip, wide ? left : meaningX, exTop, wide ? cardInner : meaningW, si, ei, pi);
                    }
                    bottom = Math.Max(bottom, codeBottom);
                    rows.Add(new HelpRow(si, ei, new HelpRect(m.Margin, rowTop, inner, bottom - rowTop), stacked, pi));
                    y = bottom;
                }
                y += m.CardPadY;
                boxes[cardIndex] = new HelpBox(HelpBoxKind.Card, new HelpRect(m.Margin, cardTop, inner, y - cardTop), "", si, -1, Part: pi);
                if (s.Note is not null)
                {
                    y += m.NoteGap;
                    y += Text(HelpBoxKind.Note, s.Note, HelpFont.Small, m.Margin, y, inner, si, -1, pi);
                }
            }
        }
        y += m.Margin;
        return new SearchHelpLayout(boxes, rows, allSections, partTops, columns, width, y);
    }
}
