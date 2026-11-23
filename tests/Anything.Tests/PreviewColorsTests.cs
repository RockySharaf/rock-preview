using System.Drawing;
using Anything.Preview;
using Anything.Preview.UI;

namespace Anything.Tests;

/// <summary>
/// The preview pane's own text colours: the light set is what the views always drew with, and the dark set reads on
/// the dark window colour, with text on a search match still dark on its yellow or orange.
/// </summary>
public sealed class PreviewColorsTests
{
    // Dark mode's SystemColors.Highlight, behind a selected table cell.
    private static readonly Color DarkHighlight = Color.FromArgb(40, 100, 180);

    [Fact]
    public void Light_set_is_the_colours_the_views_always_used()
    {
        var c = PreviewColors.Light;
        Assert.Equal(SystemColors.Window, c.Background);
        AssertRgb(30, 30, 30, c.Text);
        AssertRgb(247, 247, 247, c.GutterBack);
        AssertRgb(150, 150, 150, c.LineNumber);
        AssertRgb(225, 225, 225, c.GutterLine);
        AssertRgb(204, 228, 247, c.Selection);
        Assert.Null(c.MatchText);
        AssertRgb(240, 240, 240, c.GridHeaderBack);
        AssertRgb(225, 225, 225, c.GridLines);
        Assert.Equal(Color.Gray, c.GridRowNumber);
        Assert.Null(c.GridSelectionText);
        AssertRgb(40, 40, 40, c.DocText);
        AssertRgb(31, 78, 121, c.DocHeading);
        AssertRgb(110, 110, 110, c.DocCaption);
        Assert.Null(c.FilePageBack);
        Assert.Null(c.FilePageText);
        Assert.Equal(Color.DimGray, c.InfoSub);
        Assert.Equal(Color.Gray, c.InfoKey);
        Assert.Equal(Color.Firebrick, c.InfoDiag);
        Assert.Equal(Color.DimGray, c.PaneSub);
        Assert.Equal(SystemColors.GrayText, c.PaneFooter);
        Assert.Equal(Color.Gray, c.PaneLoading);
        AssertRgb(255, 236, 130, PreviewColors.Match);
        AssertRgb(255, 175, 70, PreviewColors.CurrentMatch);
        AssertRgb(214, 190, 70, PreviewColors.MatchSelected);
        AssertRgb(230, 140, 40, PreviewColors.CurrentMatchSelected);
    }

    [Fact]
    public void The_mode_picks_the_set()
    {
        Assert.Same(PreviewColors.Dark, PreviewColors.For(dark: true));
        Assert.Same(PreviewColors.Light, PreviewColors.For(dark: false));
    }

    [Fact]
    public void Dark_text_reads_on_what_it_is_drawn_on()
    {
        var c = PreviewColors.Dark;
        var pairs = new (string Name, Color Text, Color Back)[]
        {
            ("text", c.Text, c.Background),
            ("selected text", c.Text, c.Selection),
            ("line numbers", c.LineNumber, c.GutterBack),
            ("row numbers", c.GridRowNumber, c.GridHeaderBack),
            ("selected cell", c.GridSelectionText!.Value, DarkHighlight),
            ("paragraphs", c.DocText, c.Background),
            ("headings", c.DocHeading, c.Background),
            ("captions", c.DocCaption, c.Background),
            ("info format", c.InfoSub, c.Background),
            ("info keys", c.InfoKey, c.Background),
            ("info diagnostics", c.InfoDiag, c.Background),
            ("pane subtitle", c.PaneSub, c.Background),
            ("pane footer", c.PaneFooter, c.Background),
            ("loading", c.PaneLoading, c.Background),
            ("an .rtf file's page", c.FilePageText!.Value, c.FilePageBack!.Value),
            ("a match on an .rtf file's page", c.FilePageText!.Value, PreviewColors.CurrentMatch),
        };
        foreach (var (name, text, back) in pairs)
            Assert.True(Contrast(text, back) >= 4.5, $"{name}: {Contrast(text, back):0.00} to 1");
    }

    [Fact]
    public void Text_on_a_match_is_dark_in_dark_mode()
    {
        var onMatch = PreviewColors.Dark.MatchText!.Value;
        foreach (var back in new[] { PreviewColors.Match, PreviewColors.CurrentMatch, PreviewColors.MatchSelected, PreviewColors.CurrentMatchSelected })
            Assert.True(Contrast(onMatch, back) >= 4.5, $"{back}: {Contrast(onMatch, back):0.00} to 1");
        // The light body text would not do: that is why matches get their own text colour.
        Assert.True(Contrast(PreviewColors.Dark.Text, PreviewColors.Match) < 4.5);
    }

    [Fact]
    public void Separator_lines_show_in_dark_mode()
    {
        var c = PreviewColors.Dark;
        Assert.True(Contrast(c.GutterLine, c.Background) >= 1.3);
        Assert.True(Contrast(c.GridLines, c.Background) >= 1.3);
        Assert.True(Contrast(c.GutterBack, c.Background) > 1.0);
    }

    private static readonly DocBlock[] Blocks =
    [
        new("Slide 1", DocBlockStyle.Title),
        new("a b", DocBlockStyle.ListItem),
        new("x", DocBlockStyle.Caption),
    ];

    private static IReadOnlyList<(int Start, int Length)> FindB(string s)
    {
        var found = new List<(int, int)>();
        for (int i = s.IndexOf('b'); i >= 0; i = s.IndexOf('b', i + 1)) found.Add((i, 1));
        return found;
    }

    [Fact]
    public void Light_documents_are_built_as_always()
    {
        const string head = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;\red40\green40\blue40;\red31\green78\blue121;\red110\green110\blue110;";
        const string body = @"\viewkind4\uc1\f0\fs20\cf1\sa120 " +
            "\\pard\\sa200\\fs40\\b\\cf2 Slide 1\\b0\\i0\\par\n" +
            "\\pard\\li360\\fi-200\\sa60\\fs20\\cf1 \\bullet  a {0}\\b0\\i0\\par\n" +
            "\\pard\\sa120\\fs18\\i\\cf3 x\\b0\\i0\\par\n}";
        Assert.Equal(head + "}" + body.Replace("{0}", "b"), DocumentView.BuildRtf(Blocks, null, PreviewColors.Light));
        Assert.Equal(head + @"\red255\green236\blue130;}" + body.Replace("{0}", @"{\highlight4 b}"), DocumentView.BuildRtf(Blocks, FindB, PreviewColors.Light));
    }

    [Fact]
    public void Dark_documents_get_light_text_and_dark_text_on_matches()
    {
        string rtf = DocumentView.BuildRtf(Blocks, FindB, PreviewColors.Dark);
        Assert.StartsWith(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;\red230\green230\blue230;\red130\green180\blue230;\red170\green170\blue170;" +
            @"\red255\green236\blue130;\red30\green30\blue30;}", rtf);
        Assert.Contains(@"a {\highlight4\cf5 b}", rtf);
        Assert.DoesNotContain(@"\cf5", DocumentView.BuildRtf(Blocks, null, PreviewColors.Dark));
    }

    private static void AssertRgb(int r, int g, int b, Color c) => Assert.Equal((r, g, b), (c.R, c.G, c.B));

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
