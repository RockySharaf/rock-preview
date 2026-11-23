using System.Text.RegularExpressions;

namespace Anything.Tests;

/// <summary>
/// Rules of the main window and Settings that can't be tried here, as their windows can't be made in the tests: read
/// from the source, so a bar or menu item added later and forgotten fails.
/// </summary>
public sealed class MainWindowSourceTests
{
    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Anything.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("The tests run outside the source tree: Anything.sln wasn't found above " + AppContext.BaseDirectory);
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anything", file));

    // ---- links on the information bars ---------------------------------------------------------------------

    /// <summary>SystemColors.Info in .NET's dark mode, the information bars' background there (read from .NET 10).</summary>
    static readonly (int R, int G, int B) DarkInfo = (80, 80, 60);

    [Fact]
    public void The_dark_bar_link_colour_reads_on_the_dark_bar()
    {
        var m = Regex.Match(Source("ExplorerTheme.cs"), @"DarkBarLink = Color\.FromArgb\((\d+), (\d+), (\d+)\)");
        Assert.True(m.Success, "ExplorerTheme.DarkBarLink wasn't found: the way it is written changed");
        var link = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        double contrast = Contrast(link, DarkInfo);
        Assert.True(contrast >= 4.5, $"{contrast:0.00} to 1");
    }

    [Fact]
    public void WinForms_default_link_blue_does_not_read_on_the_dark_bar() =>
        Assert.True(Contrast((0, 0, 255), DarkInfo) < 4.5);

    [Fact]
    public void Every_information_bar_gives_its_links_the_bar_colours()
    {
        // A bar is a panel coloured SystemColors.Info; each goes through ExplorerTheme.StyleBarLinks.
        var info = new Regex(@"BackColor = SystemColors\.Info\b");
        int bars = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Anything"), "*.cs"))
        {
            string text = File.ReadAllText(path);
            int here = info.Matches(text).Count, styled = Regex.Matches(text, @"ExplorerTheme\.StyleBarLinks\(").Count;
            Assert.True(here == styled, $"{Path.GetFileName(path)}: {here} bars, {styled} styled");
            bars += here;
        }
        Assert.True(bars >= 8, $"only {bars} bars were found: the way they are written changed");
    }

    [Fact]
    public void Light_mode_keeps_the_links_as_they_are()
    {
        // Light mode must not change: the helper returns before touching a link.
        var m = Regex.Match(Source("ExplorerTheme.cs"), @"static void StyleBarLinks\(Control bar, bool dark\)\s*\{\s*if \(!dark\) return;");
        Assert.True(m.Success, "StyleBarLinks no longer starts by leaving light mode alone");
    }

    static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance((int R, int G, int B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    static double Channel(int v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    // ---- Settings › Defaults names the Search menu's switches -----------------------------------------------

    [Fact]
    public void The_Defaults_note_names_the_Search_menu_switches_in_the_order_of_the_cards()
    {
        string settings = Source("SettingsForm.cs");
        var note = Regex.Match(settings, @"Note\(""These are the Search menu's switches \(([^)]*)\)");
        Assert.True(note.Success, "the note above the match switches wasn't found");
        var named = note.Groups[1].Value.Split(", ");

        // The cards after the note, up to the next header: each switch field, or the inside-files card.
        string after = settings[(note.Index + note.Length)..];
        after = after[..after.IndexOf("Header(", StringComparison.Ordinal)];
        var cards = Regex.Matches(after, @"Card\(""[^""]*"", ""(?:[^""\\]|\\.)*"", (_\w+)\)|ContentSearchDefaultCard\(\)")
            .Select(c => c.Groups[1].Success ? c.Groups[1].Value : "content").ToList();

        // The Search menu's items, by the switch each turns (without the & of their access keys).
        string commands = Source("MainForm.Commands.cs");
        var menu = Regex.Matches(commands, @"_mi\w+ = Item\(""([^""]+)"", \(\) => Toggle\(\(\) => (_\w+) = ")
            .ToDictionary(i => i.Groups[2].Value, i => i.Groups[1].Value.Replace("&", ""));
        var content = Regex.Match(Source("MainForm.Content.cs"), @"_miMatchContent = Item\(""([^""]+)""");
        Assert.True(content.Success, "the Search menu's inside-files item wasn't found");
        menu["content"] = content.Groups[1].Value.Replace("&", "");

        Assert.Equal(6, cards.Count);
        Assert.Equal(cards.Select(c => menu[c]), named);
    }
}
