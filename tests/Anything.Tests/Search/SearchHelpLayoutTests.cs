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

    static SearchHelpLayout Arrange(int dpi, int width, IReadOnlyList<HelpSection>? sections = null) =>
        SearchHelpLayout.Arrange(sections ?? SearchHelp.Sections, SearchHelp.Title, SearchHelp.Intro, width,
            HelpMetrics.Scaled(dpi), Fake(dpi / 96.0));

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
        => Assert.DoesNotContain(Arrange(dpi, width).Boxes, b => b.Clipped);

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
    public void The_columns_line_up_across_every_section(int dpi, int width)
    {
        var layout = Arrange(dpi, width);
        var beside = layout.Rows.Where(r => !r.Stacked).ToList();
        Assert.NotEmpty(beside);
        var meaningX = beside.Select(r => layout.Boxes.Single(b => b.Kind == HelpBoxKind.Meaning && b.Section == r.Section && b.Entry == r.Entry).Bounds.X).Distinct();
        Assert.Single(meaningX);
        var codeX = layout.Rows.Select(r => layout.Boxes.First(b => b.Kind == HelpBoxKind.CodeChip && b.Section == r.Section && b.Entry == r.Entry).Bounds.X).Distinct();
        Assert.Single(codeX);
        // Code chips beside the meaning stay in the column.
        foreach (var r in beside)
            foreach (var chip in layout.Boxes.Where(b => b.Kind == HelpBoxKind.CodeChip && b.Section == r.Section && b.Entry == r.Entry))
                Assert.True(chip.Bounds.Right <= meaningX.Single());
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
    }
}
