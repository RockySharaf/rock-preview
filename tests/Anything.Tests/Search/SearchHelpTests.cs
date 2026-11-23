using System.Text.RegularExpressions;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>
/// <see cref="HelpContent"/> and <see cref="SearchHelp"/>: the F1 help as data. Nothing the plain-text help of 1.7.28
/// said may be lost, every name the search understands is either explained or left out on purpose (so a new one can't
/// slip in unexplained), and every key the main window's menus and key handling know is in the help's Keys part.
/// </summary>
public sealed class SearchHelpTests
{
    /// <summary>Every name, sign, value and example the help of 1.7.28 showed, taken from its text.</summary>
    public static readonly string[] OldHelp =
    [
        // Operators, wildcards
        "space", "|", "!", "< >", "\" \"", "*", "?", "path:", "\\",
        // Modifiers
        "case:", "nocase:", "ww:", "wholeword:", "noww:", "path:", "nopath:", "diacritics:", "nodiacritics:", "regex:",
        "noregex:", "file:", "folder:",
        // Functions and their examples
        "ext:", "ext:jpg;png", "size:", "size:>1mb", "size:1kb..10kb", "empty", "tiny", "small", "medium", "large", "huge",
        "gigantic", "dm:", "dc:", "dm:today", "dm:2024", "dm:>=2024-01-01", "dc:lastweek", "attrib:", "attrib:H",
        "parent:", "depth:", "len:", "type:",
        // Comparisons, filter macros
        "=", "<", "<=", ">", ">=", "start..end", "audio:", "zip:", "doc:", "exe:", "pic:", "video:",
        // Search inside files
        "Settings › Indexing › Search inside files", "plain words", "Match inside files", "3 or more letters",
        "Chinese, Japanese, Korean", "Match Path", "Regex", "content:", "content:invoice", "ww:content:report",
        "content:\"annual report\"", "content:INV-2024", "content:inv*24", "case:content:Café", "diacritics:content:café",
        "utf8content:", "ansicontent:", "title:", "subject:", "comment:", "description:", "author:", "authors:",
        "keywords:", "keyword:", "tags:", "tag:", "from:", "sender:", "to:", "recipients:", "To and Cc", "pagecount:",
        "pagecount:>20", "page-count:", "contentstatus:", "ok", "truncated", "notext", "cloud", "optedout", "efs",
        "password", "toobig", "damaged", "unsupported", "denied", "timeout", "crashed", "failed",
        // Pictures' details
        "Also search inside documents and pictures' details", "width:", "height:", "width:>=3000", "wide:", "high:",
        "dimensions:", "dimensions:2480x3508", "dimensions:>=1920x1080", "dimension:", "orientation:", "landscape",
        "portrait", "square", "dpi:", "dpi:<300", "dpi:72..150", "horizontalresolution:", "verticalresolution:",
        "colormode:", "rgb", "cmyk", "gray", "lab", "indexed", "bitmap", "multichannel", "duotone", "cmyk:", "rgb:",
        "cmyk: logo", "colorspace:", "srgb", "uncalibrated", "colorrepresentation:", "bitdepth:", "bitsperpixel:", "bpp:",
        "rating:", "0-5", "datetaken:", "datetaken:2023", "date-taken:lastweek", "camera:", "cameramake:", "cameramaker:",
        "cameramodel:", "camera:canon", "gps:", "!gps:", "date-taken:", "\"title:draft\"", "\"camera:old.jpg\"",
        // Examples
        "report pdf", "*.mp3 | *.flac", "ext:jpg;png size:>2mb", "\"my documents\" !temp", "c:\\projects\\ ext:cs",
        "dm:today folder:", "content:invoice ext:pdf", "dpi:<300 cmyk:", "tags:beach datetaken:2024",
        // The Search menu's switches
        "Ctrl+I", "Ctrl+B", "Ctrl+U", "Ctrl+M", "Ctrl+R",
        // Keys
        "Alt+Down", "F2", "Delete", "Shift+Delete", "Recycle Bin", "Ctrl+Shift+C", "Ctrl+Z", "Ctrl+Y", "F4", "Alt+D",
        "Ctrl+L", "Alt+Left", "Alt+Right", "Alt+Up", "Ctrl+N", "Ctrl+Shift+N", "Ctrl+Shift+1 .. 8", "extra large",
        "thumbnail sizes",
    ];

    /// <summary>The four names the README shows that the old help lacked.</summary>
    public static readonly string[] Added = ["da:", "rc:", "dr:", "runcount:", "da:yesterday", "rc:today", "dr:thisweek", "runcount:>2"];

    /// <summary>Names the search understands that the help leaves out on purpose: long spellings of names it shows,
    /// and Everything's rarer functions (the decision for this release was to add only what the README shows).</summary>
    static readonly HashSet<string> NotShown = new(StringComparer.OrdinalIgnoreCase)
    {
        "wholewords", "nowholeword", "nowholewords", "wildcards", "nowildcards", "files", "folders", "root", "empty", "dupe",
        "extension", "datemodified", "datecreated", "dateaccessed", "recentchange", "daterun", "attributes", "infolder",
        "nosubfolders", "startwith", "endwith", "parents", "child", "childcount", "childfilecount", "childfoldercount",
        // Everything's other spellings of content:, which the help sums up as "Everything's other content: names".
        "contenta", "utf16content", "utf16lecontent", "utf16becontent", "unicodecontent", "wcharcontent", "contentw",
    };

    static readonly IReadOnlyList<HelpPart> Parts = HelpContent.Build(null);
    static readonly string Plain = HelpContent.PlainText(Parts);

    /// <summary>The help with no shortcut working, and with one set to something else: both must keep the rules.</summary>
    public static TheoryData<string?> Hotkeys() => [null, "Ctrl+Shift+Space"];

    public static TheoryData<string> OldNames() => [.. OldHelp.Distinct()];
    public static TheoryData<string> AddedNames() => [.. Added];

    [Theory, MemberData(nameof(OldNames))]
    public void Nothing_the_old_help_said_is_lost(string name) => Assert.Contains(name, Plain, StringComparison.Ordinal);

    [Theory, MemberData(nameof(AddedNames))]
    public void The_names_the_README_shows_are_added(string name) => Assert.Contains(name, Plain, StringComparison.Ordinal);

    [Fact]
    public void The_help_has_three_parts_in_order_and_the_strip_names_them()
    {
        Assert.Equal(["How to use Rock Preview", "Keys", "Search syntax"], Parts.Select(p => p.Title));
        Assert.Equal(Parts.Count, HelpContent.StripLabels.Count);
        Assert.Equal(["How to use", "Keys", "Search syntax"], HelpContent.StripLabels);
        Assert.Same(SearchHelp.Part, Parts[HelpContent.Syntax]);
        Assert.Equal(HelpLabel.Names, Parts[HelpContent.HowTo].Label);
        Assert.Equal(HelpLabel.Chips, Parts[HelpContent.Keys].Label);
        Assert.Equal(HelpLabel.Chips, Parts[HelpContent.Syntax].Label);
    }

    [Fact]
    public void The_plain_text_has_the_three_headings_in_order()
    {
        int at = -1;
        foreach (var p in Parts)
        {
            int next = Plain.IndexOf(p.Title + "\r\n\r\n", at + 1, StringComparison.Ordinal);
            Assert.True(next > at, $"'{p.Title}' is missing or out of order");
            at = next;
        }
        Assert.StartsWith(Parts[0].Title, Plain);
        Assert.Equal(Plain, string.Join("\r\n", Parts.Select(p => p.PlainText())));
    }

    [Fact]
    public void The_syntax_part_is_the_syntax_help_without_its_keys()
    {
        Assert.Equal(SearchHelp.Title, SearchHelp.Part.Title);
        Assert.Equal(SearchHelp.Intro, SearchHelp.Part.Intro);
        Assert.Same(SearchHelp.Sections, SearchHelp.Part.Sections);
        Assert.DoesNotContain(SearchHelp.Sections, s => s.Title == "Keys");
        Assert.Equal(SearchHelp.PlainText(), SearchHelp.Part.PlainText());
    }

    [Theory, MemberData(nameof(Hotkeys))]
    public void Every_section_has_entries_and_no_entry_is_empty(string? hotkey)
    {
        foreach (var part in HelpContent.Build(hotkey))
        {
            Assert.False(string.IsNullOrWhiteSpace(part.Title));
            Assert.NotEmpty(part.Sections);
            foreach (var s in part.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Title));
                Assert.NotEmpty(s.Entries);
                foreach (var e in s.Entries)
                {
                    Assert.NotEmpty(e.Code);
                    Assert.All(e.Code, c => Assert.False(string.IsNullOrWhiteSpace(c)));
                    Assert.False(string.IsNullOrWhiteSpace(e.Meaning));
                    Assert.All(e.Examples, x => Assert.False(string.IsNullOrWhiteSpace(x)));
                    // One wrapping sentence or a few: no hand-made line breaks or space-made columns.
                    Assert.DoesNotContain('\n', e.Meaning);
                    Assert.DoesNotContain("   ", e.Meaning);
                    Assert.DoesNotContain("  ", e.Meaning); // a double space shows as a gap in the wrapped text
                    Assert.DoesNotContain(":.", e.Meaning); // a name then a full stop reads as a typo: names go in brackets
                    Assert.True(e.Meaning.TrimEnd().EndsWith('.'), $"'{e.Meaning}' doesn't end with a full stop");
                    if (part.Label == HelpLabel.Names) Assert.Single(e.Code); // one name a row, drawn as one bold line
                }
            }
        }
    }

    [Theory, MemberData(nameof(Hotkeys))]
    public void Every_key_is_named_once_in_its_group(string? hotkey)
    {
        foreach (var s in HelpContent.Build(hotkey)[HelpContent.Keys].Sections)
        {
            var keys = s.Entries.SelectMany(e => e.Code).ToList();
            var twice = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0, $"{s.Title}: named more than once: {string.Join(", ", twice)}");
        }
    }

    [Theory, MemberData(nameof(Hotkeys))]
    public void The_help_names_the_shortcut_that_opens_Rock_Preview(string? hotkey)
    {
        var parts = HelpContent.Build(hotkey);
        string key = hotkey ?? HelpContent.DefaultHotkey;
        Assert.Contains(key, parts[HelpContent.HowTo].Sections[0].Entries[0].Meaning);
        Assert.Equal(key, parts[HelpContent.Keys].Sections[0].Entries[0].Code[0]);
        // Without a working shortcut the help says Alt+X is only the usual one.
        Assert.Equal(hotkey is null, parts[HelpContent.HowTo].Sections[0].Entries[0].Meaning.Contains("unless you changed it"));
    }

    [Fact]
    public void Plain_text_holds_every_section_and_every_entry_as_drawn()
    {
        foreach (var part in Parts)
            foreach (var s in part.Sections)
            {
                Assert.Contains(SearchHelp.PlainHeading(s), Plain);
                if (s.Note is not null) Assert.Contains(s.Note, Plain);
                foreach (var e in s.Entries)
                {
                    Assert.Contains(e.PlainText(), Plain);
                    // The very strings the window draws, so Copy gives what is shown.
                    Assert.Contains(e.Meaning, e.PlainText());
                    Assert.All(e.Code, c => Assert.Contains(c, e.PlainText()));
                    Assert.All(e.Examples, x => Assert.Contains(x, e.PlainText()));
                }
            }
        Assert.DoesNotContain("\n\n\n", Plain.Replace("\r", ""));
    }

    [Fact]
    public void Every_name_the_search_understands_is_explained_or_left_out_on_purpose()
    {
        var missing = QueryParser.KnownNames.Where(n => !NotShown.Contains(n) && !Plain.Contains(n + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0, "Not in the help: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("", 0, 0, "ext:", HelpInsert.AtCaret, "ext:", 4)]
    [InlineData("report", 6, 0, "ext:", HelpInsert.AtCaret, "report ext:", 11)]
    [InlineData("report ", 7, 0, "ext:", HelpInsert.AtCaret, "report ext:", 11)]
    [InlineData("report", 0, 0, "case:", HelpInsert.AtCaret, "case: report", 5)]
    [InlineData("a b", 1, 0, "file:", HelpInsert.AtCaret, "a file: b", 7)]
    [InlineData("a old b", 2, 3, "ext:", HelpInsert.AtCaret, "a ext: b", 6)]
    [InlineData("anything", 3, 0, "report pdf", HelpInsert.Replace, "report pdf", 10)]
    [InlineData("keep", 2, 0, "F2", HelpInsert.None, "keep", 2)]
    [InlineData("short", 99, 99, "ext:", HelpInsert.AtCaret, "short ext:", 10)]
    public void A_double_click_puts_a_name_at_the_caret_and_an_example_in_place_of_the_search(
        string box, int start, int length, string text, HelpInsert how, string expected, int caret)
        => Assert.Equal((expected, caret), SearchHelp.Insert(box, start, length, text, how));

    [Fact]
    public void Examples_parse_and_keys_and_signs_are_not_put_in_the_search_box()
    {
        foreach (var s in SearchHelp.Sections)
            foreach (var e in s.Entries)
            {
                foreach (var x in e.Examples) AssertValidSearch(x);
                if (e.Insert == HelpInsert.Replace) AssertValidSearch(e.Code[0]);
            }
        // Keys and how-to rows have nothing to put in the search box, and no examples to try.
        foreach (var part in new[] { Parts[HelpContent.HowTo], Parts[HelpContent.Keys] })
            foreach (var s in part.Sections)
                Assert.All(s.Entries, e => { Assert.Equal(HelpInsert.None, e.Insert); Assert.Empty(e.Examples); });
        Assert.All(SearchHelp.Sections.Single(s => s.Title == "Operators").Entries, e => Assert.Equal(HelpInsert.None, e.Insert));
        Assert.All(SearchHelp.Sections.Single(s => s.Title == "Examples").Entries, e => Assert.Equal(HelpInsert.Replace, e.Insert));
    }

    // ---- every key the main window knows is in the help -------------------------------------------------

    /// <summary>
    /// The keys in MainForm.Commands.cs: the menus' ShortcutKeys and shown keys (display: "Ctrl+A"), and the keys its
    /// key handlers switch on (case Keys.Control | Keys.E:). Read from the source, as the app's window can't be made
    /// here; a key added there and forgotten in the help fails this test.
    /// </summary>
    static IEnumerable<string> MainWindowKeys()
    {
        string file = Path.Combine(RepoRoot(), "src", "Anything", "MainForm.Commands.cs");
        var combo = new Regex(@"Keys\.\w+(?:\s*\|\s*Keys\.\w+)*");
        var shown = new Regex("display: \"([^\"]+)\"");
        foreach (var line in File.ReadLines(file))
        {
            if (!line.Contains("Item(") && !line.TrimStart().StartsWith("case Keys", StringComparison.Ordinal)) continue;
            foreach (Match m in shown.Matches(line)) yield return m.Groups[1].Value;
            foreach (Match m in combo.Matches(line))
                if (KeyText(m.Value) is { } key) yield return key;
        }
    }

    /// <summary>"Keys.Control | Keys.Shift | Keys.Tab" as the help writes it, "Ctrl+Shift+Tab"; null for no key.</summary>
    static string? KeyText(string code)
    {
        var names = code.Split('|').Select(p => p.Trim()["Keys.".Length..]).ToList();
        string? key = names.FirstOrDefault(n => n is not ("Control" or "Shift" or "Alt"));
        if (key is null or "None" or "Modifiers" or "KeyCode") return null;
        string mods = (names.Contains("Control") ? "Ctrl+" : "") + (names.Contains("Shift") ? "Shift+" : "") + (names.Contains("Alt") ? "Alt+" : "");
        return mods + key switch
        {
            "Oemcomma" => ",",
            "Oemplus" or "Add" => "+",
            "OemMinus" or "Subtract" => "-",
            "Back" => "Backspace",
            "Escape" => "Esc",
            "Next" => "PageDown",
            "Prior" => "PageUp",
            "Return" => "Enter",
            _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
            _ when key.StartsWith("NumPad", StringComparison.Ordinal) => key["NumPad".Length..],
            _ => key,
        };
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Anything.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("The tests run outside the source tree: Anything.sln wasn't found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_key_the_main_window_knows_is_in_the_keys_part()
    {
        string keysPart = Parts[HelpContent.Keys].PlainText();
        var keys = MainWindowKeys().Distinct().ToList();
        Assert.True(keys.Count > 30, $"only {keys.Count} keys were read: the way they are written in the source changed");
        var missing = keys.Where(k => !keysPart.Contains(k, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Keys not in the help's Keys part: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Keys.Control | Keys.Shift | Keys.Tab", "Ctrl+Shift+Tab")]
    [InlineData("Keys.Control | Keys.Oemcomma", "Ctrl+,")]
    [InlineData("Keys.Control | Keys.NumPad0", "Ctrl+0")]
    [InlineData("Keys.Control | Keys.Back", "Ctrl+Backspace")]
    [InlineData("Keys.F1", "F1")]
    [InlineData("Keys.None", null)]
    [InlineData("Keys.Control | Keys.Shift", null)]
    public void Keys_in_the_source_are_read_as_the_help_writes_them(string code, string? expected) =>
        Assert.Equal(expected, KeyText(code));

    [Fact]
    public void The_Search_syntax_item_shows_no_F1()
    {
        // F1 opens the help at its top, so the drop-down's Search syntax item must not show it.
        string file = Path.Combine(RepoRoot(), "src", "Anything", "MainForm.Browse.cs");
        var line = File.ReadLines(file).Single(l => l.Contains("new ToolStripMenuItem(\"Search syntax\"", StringComparison.Ordinal));
        Assert.DoesNotContain("F1", line);
        Assert.Contains("ShowSyntax", line);
    }

    // ---- how the first two parts are written ---------------------------------------------------------------

    static IEnumerable<HelpEntry> HowToAndKeys(string? hotkey) =>
        HelpContent.Build(hotkey).Take(HelpContent.Syntax).SelectMany(p => p.Sections).SelectMany(s => s.Entries);

    static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text, @"(?<=[.;])\s+").Select(s => s.Trim()).Where(s => s.Length > 0);

    [Theory, MemberData(nameof(Hotkeys))]
    public void No_sentence_has_a_menu_as_its_subject(string? hotkey)
    {
        // "Help › Get Started shows the tour" reads oddly; "choose Help › Get Started" says what to do.
        var bad = new Regex(@"^(?:Under the gear's arrow, )?[A-Z][\w ]* › [^.;]*\b(?:shows|opens|hides|changes|ends|runs|looks|keeps)\b");
        foreach (var e in HowToAndKeys(hotkey))
            foreach (var s in Sentences(e.Meaning))
                Assert.False(bad.IsMatch(s), $"'{s}' has a menu as its subject");
    }

    [Theory, MemberData(nameof(Hotkeys))]
    public void No_row_only_repeats_its_own_name(string? hotkey)
    {
        foreach (var e in HowToAndKeys(hotkey))
        {
            string meaning = e.Meaning.TrimEnd('.').Trim();
            Assert.DoesNotContain(e.Code, c => string.Equals(c, meaning, StringComparison.OrdinalIgnoreCase));
            // "Alt+Home: Home." said nothing: a key's meaning is more than a word that is also in its name.
            Assert.False(e.Code.Any(c => c.Split('+').Any(p => string.Equals(p, meaning, StringComparison.OrdinalIgnoreCase))),
                $"'{e.Meaning}' says no more than its name");
        }
    }

    [Fact]
    public void No_button_is_pressed_as_if_it_were_a_key()
    {
        foreach (var e in HowToAndKeys(null))
            Assert.False(Regex.IsMatch(e.Meaning, @"\b[Pp]ress (?:the [\w ]+ button|it again to go back)\b"), $"'{e.Meaning}' presses a button as a key");
    }

    [Fact]
    public void The_rows_the_checker_named_say_what_the_program_does()
    {
        string how = Parts[HelpContent.HowTo].PlainText(), keys = Parts[HelpContent.Keys].PlainText();
        Assert.Contains("Ctrl+T, or the + after the tabs, opens a tab", how);
        Assert.Contains("Choose Search options › Kind, or right-click the status bar, to show only audio", how);
        Assert.Contains("In the list of results, drag from an empty part to select with a box, holding Ctrl to add.", how);
        Assert.Contains("Type how much space it may use in Keep at most (in GB) and tick the kinds of files under Keep " +
            "thumbnails of. Choose where with Change location (the new place starts empty).", how);
        Assert.Contains("Rename; with one search result selected, it is renamed in place.", keys);
        // The order in which saved thumbnails go is on the Settings card itself, not repeated here.
        Assert.DoesNotContain("dropped first", how);
        Assert.Contains($"click {SearchScope.EverythingInsteadLink} in the empty list.", how);
        Assert.Contains("A switch stays as you set it: new tabs, new windows and the next start begin with it.", how);
        Assert.Contains("Go to Home, as in File Explorer (This PC on a Windows without Home).", keys);
        Assert.Contains("In the list of results (not Thumbnails): select with a box", keys);
    }

    [Theory, MemberData(nameof(Hotkeys))]
    public void No_sentence_is_over_thirty_words(string? hotkey)
    {
        var tooLong = HowToAndKeys(hotkey).SelectMany(e => Regex.Split(e.Meaning, @"(?<=\.)\s+"))
            .Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 30);
        Assert.Empty(tooLong);
    }

    [Fact]
    public void Up_is_told_with_Back_and_Forward()
    {
        var rows = Parts[HelpContent.HowTo].Sections.SelectMany(s => s.Entries).ToList();
        Assert.DoesNotContain(rows, e => e.Code[0] == "Up");
        Assert.Contains("The up arrow beside them, or Alt+Up, opens the folder that holds this one.",
            rows.Single(e => e.Code[0] == "Back and Forward").Meaning);
    }

    [Fact]
    public void The_kind_that_shows_every_kind_is_not_called_Everything()
    {
        // "Everything" is the scope chip's word for searching everywhere; the kind with no filter is "All kinds".
        Assert.Contains("the \u00D7 beside the button, or All kinds in the same list, shows every kind again.", Parts[HelpContent.HowTo].PlainText());
    }

    [Fact]
    public void The_Store_copy_is_told_that_the_Store_updates_it()
    {
        static string Updates(bool store) =>
            HelpContent.Build(null, store)[HelpContent.HowTo].Sections.SelectMany(s => s.Entries).Single(e => e.Code[0] == "Updates").Meaning;
        Assert.Contains("Update automatically", Updates(store: false));
        Assert.DoesNotContain("Update automatically", Updates(store: true));
        Assert.StartsWith("The Microsoft Store keeps Rock Preview up to date.", Updates(store: true));
        Assert.Contains("Help › Check for Updates", Updates(store: true));
    }

    [Fact]
    public void The_syntax_part_says_how_to_try_things_once_in_plain_words()
    {
        Assert.Equal("Type these in the search box. Double-click a name or an example to try it.", SearchHelp.Intro);
        string all = HelpContent.PlainText(Parts);
        Assert.DoesNotContain("Filter macros", all);
        Assert.DoesNotContain("caret", all);
    }

    // ---- examples ------------------------------------------------------------------------------------------

    // The parser never throws: a misspelt name (titel:draft) just becomes a plain word, and a bad value only fails when
    // the search is put together. So an example counts as valid when no plain word in it still looks like "name:", and
    // it compiles the way a search does, names and contents both, without a QueryException.
    static readonly Regex LooksLikeName = new("^[A-Za-z][A-Za-z-]+:");

    static void AssertValidSearch(string search)
    {
        var options = new SearchOptions { MatchContent = true };
        var node = QueryParser.Parse(search, options);
        foreach (var text in Words(node))
            Assert.False(LooksLikeName.IsMatch(text), $"'{search}': '{text}' is not a name the search knows");
        var index = new FileIndex();
        var ctx = new SearchContext(index, new FolderPathCache(index), DateTime.Now);
        var ex = Record.Exception(() =>
        {
            new QueryCompiler(ctx, largeIndex: false).Compile(node);
            ContentPlan.Build(node, options, ContentMode.DocumentsAndPictures, DateTime.Now);
        });
        Assert.True(ex is null, $"'{search}' does not compile: {ex?.Message}");
    }

    static IEnumerable<string> Words(QueryNode node) => node switch
    {
        TextNode t => [t.Text],
        AndNode a => a.Children.SelectMany(Words),
        OrNode o => o.Children.SelectMany(Words),
        NotNode n => Words(n.Child),
        _ => [],
    };

    [Theory]
    [InlineData("titel:draft")]
    [InlineData("size:>2mbb")]
    public void A_wrong_example_would_be_caught(string search) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertValidSearch(search));
}
