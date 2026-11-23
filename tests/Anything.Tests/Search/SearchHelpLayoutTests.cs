using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>
/// <see cref="SearchHelpLayout"/>: where the help's parts go at a width. Measured with made-up but realistic font
/// sizes (about Consolas 13px and Segoe UI 14px, scaled), so the arithmetic is checked at every scaling and width; the
/// real look is checked on pictures rendered with the real fonts.
/// </summary>
public sealed class SearchHelpLayoutTests
{
    /// <summary>Average character widths and line heights at 100%, scaled; words wrap greedily, long ones by letter.</summary>
    static SearchHelpLayout.Measure Fake(double scale) => (text, font, maxWidth) =>
    {
        (double cw, int line) = font switch
        {
            HelpFont.Code => (7.2, 17),
            HelpFont.Body => (7.0, 19),
            HelpFont.Name => (7.6, 19),
            HelpFont.Small => (6.0, 16),
            HelpFont.Heading => (8.5, 20),
            _ => (10.5, 27),
        };
        cw *= scale;
        int lineHeight = (int)Math.Ceiling(line * scale);
        int Width(string s) => (int)Math.Ceiling(s.Length * cw);
        if (maxWidth <= 0) return (Width(text), lineHeight);
        int lines = 1, x = 0, widest = 0;
        foreach (var word in text.Split(' '))
        {
            int w = Width(word), space = x > 0 ? Width(" ") : 0;
            if (x > 0 && x + space + w > maxWidth) { lines++; x = 0; space = 0; }
            while (w > maxWidth) { lines++; w -= maxWidth; widest = maxWidth; }
            x += space + w;
            widest = Math.Max(widest, x);
        }
        return (Math.Min(widest, maxWidth), lines * lineHeight);
    };

    public static TheoryData<int, int> Sizes()
    {
        var data = new TheoryData<int, int>();
        foreach (int dpi in new[] { 96, 120, 144, 192 })
            foreach (int logical in new[] { 404, 480, 720, 1000, 1600 }) // 404: the minimum window's client width less a scroll bar
                data.Add(dpi, logical * dpi / 96);
        return data;
    }

    static readonly IReadOnlyList<HelpPart> Parts = HelpContent.Build(null);

    static SearchHelpLayout Arrange(int dpi, int width) =>
        SearchHelpLayout.Arrange(Parts, width, HelpMetrics.Scaled(dpi), Fake(dpi / 96.0));

    static SearchHelpLayout Arrange(int dpi, int width, IReadOnlyList<HelpSection> sections) =>
        SearchHelpLayout.Arrange(sections, SearchHelp.Title, SearchHelp.Intro, width, HelpMetrics.Scaled(dpi), Fake(dpi / 96.0));

    [Theory, MemberData(nameof(Sizes))]
    public void Everything_stays_inside_the_width_and_chips_inside_their_card(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        var cards = layout.Boxes.Where(b => b.Kind == HelpBoxKind.Card).ToDictionary(b => b.Section);
        foreach (var b in layout.Boxes)
        {
            Assert.True(b.Bounds.X >= 0 && b.Bounds.Right <= width, $"{b.Kind} '{b.Text}' at {b.Bounds} leaves width {width}");
            Assert.True(b.Bounds.Width > 0 && b.Bounds.Height > 0, $"{b.Kind} '{b.Text}' is empty");
            if (b.Entry < 0) continue;
            var card = cards[b.Section].Bounds;
            Assert.True(b.Bounds.X >= card.X && b.Bounds.Right <= card.Right && b.Bounds.Y >= card.Y && b.Bounds.Bottom <= card.Bottom,
                $"{b.Kind} '{b.Text}' at {b.Bounds} leaves its card {card}");
        }
    }

    [Theory, MemberData(nameof(Sizes))]
    public void No_chip_is_cut_short_at_the_minimum_width_or_wider(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        Assert.DoesNotContain(layout.Boxes, b => b.Clipped);
        // In all three parts: there are rows in each to be cut short or not.
        Assert.Equal(Parts.Count, layout.Rows.Select(r => r.Part).Distinct().Count());
    }

    [Theory, MemberData(nameof(Sizes))]
    public void Nothing_in_an_entry_overlaps_and_rows_and_cards_follow_one_another(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        foreach (var group in layout.Boxes.Where(b => b.Entry >= 0).GroupBy(b => (b.Section, b.Entry)))
        {
            var parts = group.ToList();
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                    Assert.False(parts[i].Bounds.Intersects(parts[j].Bounds), $"'{parts[i].Text}' overlaps '{parts[j].Text}'");
        }
        for (int i = 1; i < layout.Rows.Count; i++)
            Assert.True(layout.Rows[i].Bounds.Y >= layout.Rows[i - 1].Bounds.Bottom, $"row {i} overlaps the one before");
        var cards = layout.Boxes.Where(b => b.Kind == HelpBoxKind.Card).ToList();
        for (int i = 1; i < cards.Count; i++) Assert.True(cards[i].Bounds.Y > cards[i - 1].Bounds.Bottom);
        // The height takes in the last line, with the margin below it.
        Assert.Equal(layout.Boxes.Max(b => b.Bounds.Bottom) + HelpMetrics.Scaled(dpi).Margin, layout.Height);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void The_columns_line_up_within_each_part(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        for (int p = 0; p < Parts.Count; p++)
        {
            var rows = layout.Rows.Where(r => r.Part == p).ToList();
            var beside = rows.Where(r => !r.Stacked).ToList();
            Assert.NotEmpty(beside);
            var meaningX = beside.Select(r => layout.Boxes.Single(b => b.Kind == HelpBoxKind.Meaning && b.Section == r.Section && b.Entry == r.Entry).Bounds.X).Distinct().ToList();
            Assert.Single(meaningX);
            // What a row names: chips in Keys and Search syntax, a bold name in How to use.
            var kind = Parts[p].Label == HelpLabel.Names ? HelpBoxKind.Name : HelpBoxKind.CodeChip;
            var nameX = rows.Select(r => layout.Boxes.First(b => b.Kind == kind && b.Section == r.Section && b.Entry == r.Entry).Bounds.X).Distinct();
            Assert.Single(nameX);
            // Names beside the meaning stay in the column.
            foreach (var r in beside)
                foreach (var b in layout.Boxes.Where(b => b.Kind == kind && b.Section == r.Section && b.Entry == r.Entry))
                    Assert.True(b.Bounds.Right <= meaningX[0], $"'{b.Text}' runs into the meaning");
        }
    }

    [Theory, MemberData(nameof(Sizes))]
    public void Each_part_works_out_its_own_column(int dpi, int width)
    {
        // The syntax part's column is what it is alone: How to use's long names and the keys don't move it.
        var whole = Arrange(dpi, width);
        for (int p = 0; p < Parts.Count; p++)
        {
            var alone = SearchHelpLayout.Arrange([Parts[p]], width, HelpMetrics.Scaled(dpi), Fake(dpi / 96.0));
            Assert.Equal(alone.Columns[0], whole.Columns[p]);
        }
        Assert.Equal(Arrange(dpi, width, SearchHelp.Sections).Columns[0], whole.Columns[HelpContent.Syntax]);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void The_strip_scrolls_to_each_part_s_first_heading(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        Assert.Equal(HelpContent.StripLabels.Count, layout.PartTops.Count);
        for (int p = 0; p < Parts.Count; p++)
        {
            var first = layout.Boxes.First(b => b.Part == p);
            Assert.Equal(HelpBoxKind.Title, first.Kind);
            Assert.Equal(Parts[p].Title, first.Text);
            Assert.Equal(first.Bounds.Y, layout.PartTops[p]);
            // Scrolled there, that part is the one being read: its button shows as current.
            Assert.Equal(p, layout.PartAt(layout.PartTops[p], 0));
            Assert.Equal(p, layout.PartAt(layout.PartTops[p] - 2, 4));
            if (p > 0) Assert.Equal(p - 1, layout.PartAt(layout.PartTops[p] - 1, 0));
        }
        Assert.Equal(0, layout.PartAt(0, 0));
        Assert.Equal(Parts.Count - 1, layout.PartAt(layout.Height, 0));
        Assert.Equal(layout.Rows.Select(r => r.Section).Distinct().Count(), layout.Sections.Count);
        Assert.Equal(Parts.Sum(p => p.Sections.Count), layout.Sections.Count);
    }

    [Fact]
    public void A_code_wider_than_the_column_goes_above_its_meaning()
    {
        HelpSection[] sections =
        [
            new("A", null, [new(["ext:"], "Short.", [], HelpInsert.AtCaret), new(["a-very-long-name-that-cannot-fit-the-column:"], "Under it.", [], HelpInsert.AtCaret)], null),
        ];
        var layout = Arrange(96, 720, sections);
        Assert.False(layout.Rows[0].Stacked);
        Assert.True(layout.Rows[1].Stacked);
        var chip = layout.Boxes.Single(b => b.Kind == HelpBoxKind.CodeChip && b.Entry == 1);
        var meaning = layout.Boxes.Single(b => b.Kind == HelpBoxKind.Meaning && b.Entry == 1);
        Assert.True(meaning.Bounds.Y >= chip.Bounds.Bottom);
        Assert.Equal(chip.Bounds.X, meaning.Bounds.X);
    }

    [Fact]
    public void A_long_name_wraps_in_its_column_and_a_word_too_long_for_it_goes_above()
    {
        HelpPart part = new("P", null,
        [
            new("A", null,
            [
                new(["Short"], "Beside it.", [], HelpInsert.None),
                new(["A name of many words that wraps in the column"], "Beside it too.", [], HelpInsert.None),
                new(["Averyveryveryverylongwordthatcannotfitthecolumn"], "Under it.", [], HelpInsert.None),
            ], null),
        ], HelpLabel.Names);
        var layout = SearchHelpLayout.Arrange([part], 720, HelpMetrics.Scaled(96), Fake(1));
        Assert.False(layout.Rows[0].Stacked);
        Assert.False(layout.Rows[1].Stacked);
        Assert.True(layout.Rows[2].Stacked);
        var wrapped = layout.Boxes.Single(b => b.Kind == HelpBoxKind.Name && b.Entry == 1);
        Assert.True(wrapped.Bounds.Height > 19, "the long name is on more than one line");
        Assert.Equal(layout.Columns[0], wrapped.Bounds.Width);
        Assert.True(layout.Rows[1].Bounds.Bottom >= wrapped.Bounds.Bottom);
        var name = layout.Boxes.Single(b => b.Kind == HelpBoxKind.Name && b.Entry == 2);
        var meaning = layout.Boxes.Single(b => b.Kind == HelpBoxKind.Meaning && b.Entry == 2);
        Assert.True(meaning.Bounds.Y >= name.Bounds.Bottom);
        Assert.DoesNotContain(layout.Boxes, b => b.Kind is HelpBoxKind.CodeChip);
    }

    [Fact]
    public void A_chip_wider_than_the_whole_card_is_cut_short_not_left_sticking_out()
    {
        HelpSection[] sections = [new("A", null, [new([new string('x', 200)], "Meaning.", [], HelpInsert.AtCaret)], null)];
        var layout = Arrange(96, 420, sections);
        var chip = layout.Boxes.Single(b => b.Kind == HelpBoxKind.CodeChip);
        Assert.True(chip.Clipped);
        Assert.True(chip.Bounds.Right <= layout.Boxes.Single(b => b.Kind == HelpBoxKind.Card).Bounds.Right);
    }

    [Fact]
    public void Hit_testing_finds_the_row_and_chip_under_a_point()
    {
        var layout = Arrange(96, 720);
        var chip = layout.Boxes.First(b => b.Kind == HelpBoxKind.ExampleChip);
        var hit = layout.ChipAt(chip.Bounds.X + 1, chip.Bounds.Y + 1);
        Assert.Same(chip, hit);
        var row = layout.RowAt(chip.Bounds.X + 1, chip.Bounds.Y + 1);
        Assert.NotNull(row);
        Assert.Equal((chip.Section, chip.Entry), (row!.Section, row.Entry));
        Assert.Null(layout.RowAt(1, 1));
        // A how-to row is found by its name (to copy it), but a name is not a chip to double-click.
        var name = layout.Boxes.First(b => b.Kind == HelpBoxKind.Name);
        Assert.Null(layout.ChipAt(name.Bounds.X + 1, name.Bounds.Y + 1));
        Assert.Equal((name.Section, name.Entry), (layout.RowAt(name.Bounds.X + 1, name.Bounds.Y + 1)!.Section, name.Entry));
        Assert.Equal(HelpContent.HowTo, layout.RowAt(name.Bounds.X + 1, name.Bounds.Y + 1)!.Part);
    }
}
