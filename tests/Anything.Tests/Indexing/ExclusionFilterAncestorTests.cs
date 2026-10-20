using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary><see cref="ExclusionFilter.WithoutAncestorsOf"/>: the rules a folder listed under Settings › Folders is indexed with.</summary>
public sealed class ExclusionFilterAncestorTests
{
    const string Desktop = @"C:\Users\me\Desktop";

    static ExclusionFilter Filter(params string[] folders) => new(new IndexSettings { ExcludeFolders = [.. folders] });

    [Fact]
    public void Drops_prefix_rules_for_the_root_and_its_ancestors()
    {
        var f = Filter(@"C:\", @"c:\users", @"C:\Users\me\", Desktop);
        Assert.True(f.ExcludesFolder(Desktop, "Desktop"));

        var g = f.WithoutAncestorsOf(Desktop);
        Assert.NotSame(f, g);
        Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        Assert.False(g.ExcludesFolder(Desktop + @"\Work", "Work"));
        Assert.False(g.ExcludesPathOrAncestor(@"C:\", Desktop + @"\Work\notes"));
        // Nothing is left that needs paths.
        Assert.False(g.NeedsFolderPaths);
        Assert.True(g.IsEmpty);

        // The original filter is unchanged.
        Assert.True(f.ExcludesFolder(Desktop, "Desktop"));
        Assert.True(f.ExcludesFolder(@"C:\Windows", "Windows"));
    }

    [Fact]
    public void Keeps_rules_inside_the_root_and_unrelated_rules()
    {
        var f = Filter(@"C:\Users", Desktop + @"\junk", @"C:\Users\me\Desk", @"D:\Backup");
        var g = f.WithoutAncestorsOf(Desktop);

        Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        Assert.True(g.ExcludesFolder(Desktop + @"\junk", "junk"));
        Assert.True(g.ExcludesFolder(Desktop + @"\junk\old", "old"));
        Assert.True(g.ExcludesPathOrAncestor(Desktop, Desktop + @"\junk\old\file"));
        // "Desk" is a sibling-ish prefix, not an ancestor: still applies to C:\Users\me\Desk.
        Assert.True(g.ExcludesFolder(@"C:\Users\me\Desk", "Desk"));
        Assert.True(g.ExcludesFolder(@"D:\Backup\x", "x"));
        Assert.True(g.NeedsFolderPaths);
    }

    [Fact]
    public void Keeps_name_wildcards_patterns_and_attribute_rules()
    {
        var f = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = [@"C:\Users", "node_modules", "Desk*"],
            ExcludePatterns = ["*.tmp"],
            ExcludeHidden = true,
            ExcludeSystem = true,
        });
        var g = f.WithoutAncestorsOf(Desktop);

        Assert.True(g.ExcludesFolder(Desktop + @"\app\node_modules", "node_modules"));
        // Folder-name wildcards are kept even when they match the root's own name: they are about names, not paths.
        Assert.True(g.ExcludesFolder(Desktop + @"\Desk2", "Desk2"));
        Assert.True(g.ExcludesName("cache.TMP"));
        Assert.True(g.ExcludesAttributes(FileAttributes.Hidden));
        Assert.True(g.ExcludesAttributes(FileAttributes.System));
        Assert.True(g.Excludes("a.tmp", FileAttributes.Normal, null));
        Assert.False(g.Excludes("a.txt", FileAttributes.Normal, null));
        Assert.False(g.ExcludesFolder(Desktop + @"\Work", "Work"));
        Assert.False(g.IsEmpty);
    }

    [Fact]
    public void Drops_path_wildcards_that_match_the_root_or_an_ancestor()
    {
        var f = Filter(@"*\me", @"*\me\Desktop", @"*\Desktop\cache", @"*\obj");
        Assert.True(f.ExcludesFolder(Desktop, "Desktop"));

        var g = f.WithoutAncestorsOf(Desktop);
        Assert.NotSame(f, g);
        Assert.False(g.ExcludesFolder(@"C:\Users\me", "me"));
        Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        Assert.False(g.ExcludesPathOrAncestor(Desktop, Desktop + @"\app\notes"));
        // Wildcards that only match below the root still apply.
        Assert.True(g.ExcludesFolder(Desktop + @"\cache", "cache"));
        Assert.True(g.ExcludesPathOrAncestor(Desktop, Desktop + @"\cache\x"));
        // Unrelated wildcards stay.
        Assert.True(g.ExcludesFolder(@"D:\src\bin\obj", "obj"));
        Assert.True(g.NeedsFolderPaths);
    }

    [Fact]
    public void Path_wildcards_with_a_wildcard_after_a_backslash()
    {
        var f = Filter(@"C:\Users\*\Desktop", @"*\Desktop\*\cache", @"C:\Program*", @"C:\Use?s\me\Docs");
        Assert.True(f.ExcludesFolder(Desktop, "Desktop"));
        Assert.True(f.ExcludesFolder(@"C:\Program Files", "Program Files"));
        Assert.True(f.ExcludesFolder(@"C:\Users\me\Docs", "Docs"));

        var g = f.WithoutAncestorsOf(Desktop);
        Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        Assert.True(g.ExcludesFolder(Desktop + @"\app\cache", "cache"));
        Assert.True(g.ExcludesFolder(@"C:\Program Files", "Program Files"));
    }

    [Fact]
    public void Path_wildcards_match_whole_paths_in_any_case()
    {
        var f = Filter(@"C:\Users\*\AppData\Local\Temp\", @"?:\$Recycle.Bin", "C:/Program*/Common Files", @"*\.git\objects");
        Assert.True(f.ExcludesFolder(@"c:\users\ME\appdata\local\TEMP", "TEMP"));
        Assert.True(f.ExcludesFolder(@"C:\Users\me\AppData\Local\Temp\", "Temp")); // a trailing backslash on the path
        Assert.True(f.ExcludesFolder(@"D:\$RECYCLE.BIN", "$RECYCLE.BIN"));
        Assert.True(f.ExcludesFolder(@"C:\Program Files (x86)\Common Files", "Common Files"));
        Assert.True(f.ExcludesFolder(@"D:\src\app\.git\objects", "objects"));
        // * spans folders: one rule covers every user, however deep.
        Assert.True(f.ExcludesFolder(@"C:\Users\a\b\AppData\Local\Temp", "Temp"));

        // The whole path must match: not a folder above it, nor one that only starts the same.
        Assert.False(f.ExcludesFolder(@"C:\Users\me\AppData\Local", "Local"));
        Assert.False(f.ExcludesFolder(@"C:\Users\me\AppData\Local\Temporary", "Temporary"));
        Assert.False(f.ExcludesFolder(@"DD:\$Recycle.Bin", "$Recycle.Bin"));
        Assert.False(f.ExcludesFolder(@"C:\Program Files\Common Files Extra", "Common Files Extra"));
        // Folders below a match are left out with it.
        Assert.True(f.ExcludesPathOrAncestor(@"C:\", @"C:\Users\me\AppData\Local\Temp\x\y"));
        Assert.False(f.ExcludesPathOrAncestor(@"C:\", @"C:\Users\me\AppData\Local\x\Temp"));
    }

    [Fact]
    public void Folder_name_wildcards_and_name_patterns_still_match_names()
    {
        var f = new ExclusionFilter(new IndexSettings
        {
            ExcludeFolders = ["node_modules", "*.egg-info", "build*", "?bin", "*cache*"],
            ExcludePatterns = ["*.tmp", "~$*", "Thumbs.db", "*.~*", "*"],
        });
        Assert.True(f.ExcludesFolder(null, "NODE_MODULES"));
        Assert.True(f.ExcludesFolder(null, "pkg.egg-info"));
        Assert.True(f.ExcludesFolder(null, "Build-Output"));
        Assert.True(f.ExcludesFolder(null, "obin"));
        Assert.True(f.ExcludesFolder(null, "__pycache__"));
        Assert.False(f.ExcludesFolder(null, "bin"));
        Assert.False(f.ExcludesFolder(null, "node_modules2"));
        Assert.False(f.ExcludesFolder(null, "rebuild"));

        var names = new ExclusionFilter(new IndexSettings { ExcludePatterns = ["*.tmp", "~$*", "Thumbs.db", "*.~*"] });
        Assert.True(names.ExcludesName("x.TMP"));
        Assert.True(names.ExcludesName("~$Report.docx"));
        Assert.True(names.ExcludesName("thumbs.DB"));
        Assert.True(names.ExcludesName("draft.~doc"));
        Assert.False(names.ExcludesName("x.tmp.txt"));
        Assert.False(names.ExcludesName("Thumbs.db.bak"));
        Assert.True(f.ExcludesName("anything")); // "*" leaves out every name
    }

    [Fact]
    public void Attribute_rules_are_reported()
    {
        Assert.False(ExclusionFilter.None.HasAttributeRules);
        Assert.False(Filter(@"C:\Windows", "node_modules").HasAttributeRules);
        Assert.False(new ExclusionFilter(new IndexSettings { ExcludePatterns = ["*.tmp"] }).HasAttributeRules);
        Assert.True(new ExclusionFilter(new IndexSettings { ExcludeHidden = true }).HasAttributeRules);
        Assert.True(new ExclusionFilter(new IndexSettings { ExcludeSystem = true }).HasAttributeRules);
        // Kept for an extra folder: its own subfolders are still checked.
        var f = new ExclusionFilter(new IndexSettings { ExcludeFolders = [@"C:\Users"], ExcludeHidden = true });
        Assert.True(f.WithoutAncestorsOf(Desktop).HasAttributeRules);
    }

    [Fact]
    public void Returns_the_same_instance_when_nothing_changes()
    {
        var noPaths = new ExclusionFilter(new IndexSettings { ExcludeFolders = ["node_modules"], ExcludePatterns = ["*.tmp"] });
        Assert.Same(noPaths, noPaths.WithoutAncestorsOf(Desktop));

        var unrelated = Filter(@"D:\Backup", Desktop + @"\junk", @"*\obj");
        Assert.Same(unrelated, unrelated.WithoutAncestorsOf(Desktop));

        Assert.Same(ExclusionFilter.None, ExclusionFilter.None.WithoutAncestorsOf(Desktop));
        var any = Filter(@"C:\");
        Assert.Same(any, any.WithoutAncestorsOf(""));
    }

    [Fact]
    public void Root_spelling_does_not_matter()
    {
        var f = Filter(@"C:\Users");
        foreach (var root in new[] { Desktop + @"\", @"c:\USERS\me\desktop", @"C:/Users/me/Desktop/" })
        {
            var g = f.WithoutAncestorsOf(root);
            Assert.NotSame(f, g);
            Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        }
    }

    [Fact]
    public void Unc_roots()
    {
        const string docs = @"\\nas\share\docs";
        var f = Filter(@"\\nas", @"\\NAS\Share", docs + @"\tmp", @"*\share", @"\\nas\other");
        Assert.True(f.ExcludesFolder(docs, "docs"));

        var g = f.WithoutAncestorsOf(docs);
        Assert.False(g.ExcludesFolder(docs, "docs"));
        Assert.False(g.ExcludesFolder(docs + @"\letters", "letters"));
        Assert.True(g.ExcludesFolder(docs + @"\tmp", "tmp"));
        Assert.True(g.ExcludesFolder(@"\\nas\other\x", "x"));

        // A share whose name merely starts the same is not an ancestor: its rule stays.
        var h = Filter(@"\\nas\share").WithoutAncestorsOf(@"\\nas\shared\docs");
        Assert.False(h.ExcludesFolder(@"\\nas\shared\docs", "docs"));
        Assert.True(h.ExcludesFolder(@"\\nas\share\docs", "docs"));
    }

    [Fact]
    public void Drive_root_rule_is_dropped_only_for_folders_on_that_drive()
    {
        var f = Filter(@"C:\", @"D:\");
        var g = f.WithoutAncestorsOf(Desktop);
        Assert.False(g.ExcludesFolder(Desktop, "Desktop"));
        Assert.True(g.ExcludesFolder(@"D:\Data", "Data"));
        Assert.True(g.ExcludesFolder("D:", "D:"));
    }
}

/// <summary>The * and ? matcher behind exclude rules: * spans backslashes, ? is one character, nothing escapes.</summary>
public sealed class ExclusionFilterWildcardTests
{
    [Theory]
    [InlineData(@"C:\Users\*\Desktop", @"C:\Users\me\Desktop", true)]
    [InlineData(@"C:\Users\*\Desktop", @"c:\USERS\me\desktop", true)]
    [InlineData(@"C:\Users\*\Desktop", @"C:\Users\a\b\Desktop", true)]
    [InlineData(@"C:\Users\*\Desktop", @"C:\Users\me\Desktop\x", false)]
    [InlineData(@"C:\Users\*\Desktop", @"C:\Users\Desktop", false)]
    [InlineData(@"C:\Program*", @"C:\Program Files (x86)", true)]
    [InlineData(@"C:\Program*", @"C:\Program", true)]
    [InlineData(@"C:\Program*", @"C:\Progra", false)]
    [InlineData(@"*\Desktop\*\cache", @"C:\Users\me\Desktop\app\cache", true)]
    [InlineData(@"*\Desktop\*\cache", @"C:\Users\me\Desktop\cache", false)]
    [InlineData(@"*\Desktop\*\cache", @"C:\Desktop\a\cache\b\cache", true)]
    [InlineData(@"C:\Use?s\me", @"C:\Users\me", true)]
    [InlineData(@"C:\Use?s\me", @"C:\Uses\me", false)]
    [InlineData(@"C:?Users", @"C:\Users", true)] // ? matches a backslash too
    [InlineData(@"*\bin\obj", @"D:\src\bin\obj", true)]
    [InlineData(@"*\bin\obj", @"D:\src\bin\objx", false)]
    [InlineData(@"x\*y", @"x\abcy", true)] // a backslash before * is literal, not an escape
    [InlineData(@"x\*y", "x*y", false)]
    [InlineData("*.tmp", "x.TMP", true)]
    [InlineData("*.tmp", "x.tmp.txt", false)]
    [InlineData("*.tmp", ".tmp", true)]
    [InlineData("~$*", "~$doc.docx", true)]
    [InlineData("thumbs.db", "Thumbs.DB", true)]
    [InlineData("thumbs.db", "thumbs.d", false)]
    [InlineData("???", "abc", true)]
    [InlineData("???", "ab", false)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbYcZ", false)]
    [InlineData("*ab*abc", "abababc", true)] // needs backtracking to the last star
    [InlineData("*a*a*a*b", "aaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("**", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("?", "", false)]
    [InlineData("*?", "", false)]
    [InlineData("*É*", "café", true)] // case-insensitive beyond ASCII
    [InlineData("[ab]", "[AB]", true)] // brackets are literal
    [InlineData("[ab]", "a", false)]
    [InlineData("a", "\u0041", true)]
    [InlineData("@", "`", false)] // differ only in the 0x20 bit, but are not letters
    public void Matches(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, new Wildcard(pattern).IsMatch(text));
        Assert.Equal(expected, Wildcard.IsMatch(pattern, text)); // the general matcher agrees with each shortcut
    }

    [Fact]
    public void Long_paths_with_many_stars_stay_fast()
    {
        string path = @"C:\" + string.Join('\\', Enumerable.Repeat("aaaaaaaaaa", 3000)); // ~33,000 characters
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            Assert.False(Wildcard.IsMatch(@"*\a*\b*", path));
            Assert.True(new Wildcard(@"C:\*a").IsMatch(path));
        }
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }
}
