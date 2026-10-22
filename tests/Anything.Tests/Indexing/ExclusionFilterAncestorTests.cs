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

    [Fact]
    public void Rules_kept_for_the_root_keep_their_text()
    {
        var f = Filter(@"C:\Users", Desktop + @"\junk\", @"*\Desktop\*\cache", "node_modules");
        var g = f.WithoutAncestorsOf(Desktop);
        Assert.Equal(Desktop + @"\junk\", g.RuleHiding(Desktop, Desktop + @"\junk\old"));
        Assert.Equal(@"*\Desktop\*\cache", g.RuleHiding(Desktop, Desktop + @"\app\cache\x"));
        Assert.Equal("node_modules", g.RuleHiding(Desktop, Desktop + @"\app\node_modules"));
        Assert.Null(g.RuleHiding(Desktop, Desktop + @"\app"));
        // The original still names the rule above the root.
        Assert.Equal(@"C:\Users", f.RuleHiding(@"C:\", Desktop + @"\app"));
    }
}

/// <summary><see cref="ExclusionFilter.RuleHiding"/>: which rule leaves a folder out, as it was written.</summary>
public sealed class ExclusionFilterRuleTests
{
    static ExclusionFilter Rules(string[] folders, string[]? patterns = null, bool hidden = false, bool system = false) =>
        new(Settings(folders, patterns, hidden, system));

    static IndexSettings Settings(string[] folders, string[]? patterns = null, bool hidden = false, bool system = false) =>
        new() { ExcludeFolders = [.. folders], ExcludePatterns = [.. patterns ?? []], ExcludeHidden = hidden, ExcludeSystem = system };

    /// <summary>
    /// The boolean rules as they were before RuleHiding (1.5.0), copied so they share no code with the filter: what it
    /// says is left out must not change. (Its IsEmpty shortcut is left out: it changes no answer.)
    /// </summary>
    sealed class OldRules
    {
        readonly string[] _prefixes;
        readonly Wildcard[] _pathWildcards;
        readonly Wildcard[] _nameWildcards;
        readonly Wildcard[] _patterns;

        public OldRules(IndexSettings settings)
        {
            var prefixes = new List<string>();
            var pathWild = new List<Wildcard>();
            var nameWild = new List<Wildcard>();
            foreach (var raw in settings.ExcludeFolders)
            {
                var f = raw?.Trim().Trim('"');
                if (string.IsNullOrEmpty(f)) continue;
                bool wild = f.IndexOfAny(['*', '?']) >= 0;
                if (f.Contains('\\') || f.Contains('/'))
                {
                    f = f.Replace('/', '\\').TrimEnd('\\');
                    if (wild) pathWild.Add(new Wildcard(f));
                    else prefixes.Add(f);
                }
                else nameWild.Add(new Wildcard(f));
            }
            _prefixes = [.. prefixes];
            _pathWildcards = [.. pathWild];
            _nameWildcards = [.. nameWild];
            _patterns = [.. settings.ExcludePatterns.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Select(p => new Wildcard(p))];
        }

        OldRules(OldRules from, string[] prefixes, Wildcard[] pathWildcards)
        {
            _prefixes = prefixes;
            _pathWildcards = pathWildcards;
            _nameWildcards = from._nameWildcards;
            _patterns = from._patterns;
        }

        public OldRules WithoutAncestorsOf(string rootPath)
        {
            string root = rootPath.Replace('/', '\\').TrimEnd('\\');
            if (root.Length == 0 || _prefixes.Length + _pathWildcards.Length == 0) return this;
            var self = new List<string> { root };
            for (int i = root.LastIndexOf('\\'); i > 0; i = root.LastIndexOf('\\', i - 1)) self.Add(root[..i]);
            var prefixes = _prefixes.Where(p => !(root.Equals(p, StringComparison.OrdinalIgnoreCase)
                || (root.Length > p.Length && root.StartsWith(p, StringComparison.OrdinalIgnoreCase) && root[p.Length] == '\\'))).ToArray();
            var wild = _pathWildcards.Where(w => !self.Exists(a => w.IsMatch(a))).ToArray();
            return new OldRules(this, prefixes, wild);
        }

        bool ExcludesName(string name) => _patterns.Any(p => p.IsMatch(name));

        bool ExcludesFolder(string path, string name)
        {
            if (_nameWildcards.Any(p => p.IsMatch(name))) return true;
            path = path.TrimEnd('\\');
            foreach (var p in _prefixes)
                if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase) && (path.Length == p.Length || path[p.Length] == '\\')) return true;
            return _pathWildcards.Any(p => p.IsMatch(path));
        }

        public bool ExcludesPathOrAncestor(string rootPath, string fullPath)
        {
            string root = rootPath.TrimEnd('\\');
            var rel = fullPath.Length > root.Length ? fullPath[(root.Length + 1)..] : "";
            string cur = root;
            foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                cur = cur + "\\" + part;
                if (ExcludesName(part) || ExcludesFolder(cur, part)) return true;
            }
            return false;
        }
    }

    /// <summary>The rule RuleHiding names leaves the path out on its own, as an exclude entry or as a name pattern.</summary>
    static void AssertRuleLeavesItOut(string rule, string root, string path, string? without)
    {
        var alone = new[] { Settings([rule]), Settings([], [rule]) }.Select(s => new ExclusionFilter(s))
            .Select(f => without == null ? f : f.WithoutAncestorsOf(without));
        Assert.Contains(alone, f => f.ExcludesPathOrAncestor(root, path));
    }

    [Fact]
    public void Names_each_kind_of_rule_as_written()
    {
        var f = Rules([@" C:\Users\me\AppData\ ", "C:/Program*", @"""D:\Quoted""", "node_modules", "*.egg-info"], [" *.tmp ", "~$*"]);
        Assert.Equal(@" C:\Users\me\AppData\ ", f.RuleHiding(@"C:\", @"C:\Users\me\AppData\Local\x"));
        Assert.Equal("C:/Program*", f.RuleHiding(@"C:\", @"C:\Program Files\App"));
        Assert.Equal(@"""D:\Quoted""", f.RuleHiding(@"D:\", @"D:\Quoted\x"));
        Assert.Equal("node_modules", f.RuleHiding(@"C:\", @"C:\src\node_modules\pkg"));
        Assert.Equal("*.egg-info", f.RuleHiding(@"C:\", @"C:\src\pkg.egg-info"));
        Assert.Equal(" *.tmp ", f.RuleHiding(@"C:\", @"C:\src\build.tmp\x"));
        Assert.Equal("~$*", f.RuleHiding(@"C:\", @"C:\src\~$draft"));
        Assert.Null(f.RuleHiding(@"C:\", @"C:\src\app"));
        Assert.Null(f.RuleHiding(@"C:\", @"C:\Users\me\AppDataX"));
    }

    [Fact]
    public void Names_the_outermost_folder_left_out()
    {
        Assert.Equal(@"C:\Work", Rules([@"C:\Work\Old\Deep", @"C:\Work"]).RuleHiding(@"C:\", @"C:\Work\Old\Deep\x"));
        Assert.Equal("Old", Rules([@"C:\Work\Old\Deep", "Old"]).RuleHiding(@"C:\", @"C:\Work\Old\Deep\x"));
        // At one folder: name patterns, then folder names, then paths, then path wildcards.
        Assert.Equal("Old*", Rules([@"*\Old", @"C:\Work\Old", "Ol?"], ["Old*"]).RuleHiding(@"C:\", @"C:\Work\Old"));
        Assert.Equal("Ol?", Rules([@"*\Old", @"C:\Work\Old", "Ol?"]).RuleHiding(@"C:\", @"C:\Work\Old"));
        Assert.Equal(@"C:\Work\Old", Rules([@"*\Old", @"C:\Work\Old"]).RuleHiding(@"C:\", @"C:\Work\Old"));
    }

    [Fact]
    public void Only_folders_below_the_root_are_walked()
    {
        // A name rule for the root or a folder above it does not count; a path rule above it still covers what is below.
        var names = Rules(["Desktop", "Users"]);
        Assert.Null(names.RuleHiding(@"C:\Users\me\Desktop", @"C:\Users\me\Desktop\Work"));
        Assert.Equal("Users", names.RuleHiding(@"C:\", @"C:\Users\me\Desktop\Work"));
        var path = Rules([@"C:\Users"]);
        Assert.Equal(@"C:\Users", path.RuleHiding(@"C:\Users\me", @"C:\Users\me\Desktop"));
        Assert.Null(path.RuleHiding(@"C:\Users\me", @"C:\Users\me")); // the root itself: nothing to walk
    }

    [Fact]
    public void Hidden_and_system_are_not_rules()
    {
        var f = Rules([], hidden: true, system: true);
        Assert.False(f.IsEmpty);
        Assert.Null(f.RuleHiding(@"C:\", @"C:\Users\me\AppData"));
        Assert.Null(ExclusionFilter.None.RuleHiding(@"C:\", @"C:\x"));
    }

    // The cases below, each with what it was before RuleHiding: left out (true) or not, by each set of rules.
    static readonly string[] FullFolders = [@"C:\Users\me\AppData", @"c:/program*", @"*\.git\objects", @"\\nas\share\tmp", "node_modules", "build*", @"C:\"];
    static readonly string[] FullPatterns = ["*.tmp", "~$*"];

    [Theory]
    // Expected for: no rules, all rules, all rules for an added Desktop, for an added AppData\Roaming, D: rules, attributes only.
    [InlineData(@"C:\", @"C:\", false, false, false, false, false, false)]
    [InlineData("C:", @"C:\Users", false, true, false, false, false, false)]
    [InlineData(@"C:\", @"C:\Users\me\AppData\Local\Temp", false, true, true, false, false, false)]
    [InlineData(@"C:\", @"C:\Users\me\AppDataX", false, true, false, false, false, false)]
    [InlineData(@"C:\", @"C:\Program Files (x86)\App", false, true, true, true, false, false)]
    [InlineData(@"C:\Users\me\Desktop", @"C:\Users\me\Desktop\app\node_modules\pkg", false, true, true, true, false, false)]
    [InlineData(@"C:\Users\me\Desktop", @"C:\Users\me\Desktop\app\build-out", false, true, true, true, false, false)]
    [InlineData(@"C:\Users\me\Desktop", @"C:\Users\me\Desktop\notes\~$draft", false, true, true, true, false, false)]
    [InlineData(@"C:\Users\me\Desktop", @"C:\Users\me\Desktop\notes", false, true, false, false, false, false)]
    [InlineData(@"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Roaming\Code", false, true, true, false, false, false)]
    [InlineData(@"D:\", @"D:\src\.git\objects\pack", false, true, true, true, false, false)]
    [InlineData(@"D:\", @"D:\Work\old.bak", false, false, false, false, true, false)]
    [InlineData(@"D:\", @"D:\src\bin\obj\Debug", false, false, false, false, true, false)]
    [InlineData(@"D:\", @"D:\src\app", false, false, false, false, false, false)]
    [InlineData(@"\\nas\share", @"\\nas\share\tmp\x", false, true, true, true, false, false)]
    [InlineData(@"\\nas\share", @"\\nas\share\docs", false, false, false, false, false, false)]
    [InlineData(@"Y:\", @"Y:\Albums\Live\", false, false, false, false, false, false)]
    public void Agrees_with_what_was_left_out_before(string root, string path, bool none, bool full, bool inDesktop, bool inRoaming, bool d, bool attributes)
    {
        var fullRules = Rules(FullFolders, FullPatterns, hidden: true);
        var cases = new (ExclusionFilter Filter, bool Expected)[]
        {
            (ExclusionFilter.None, none),
            (fullRules, full),
            (fullRules.WithoutAncestorsOf(@"C:\Users\me\Desktop"), inDesktop),
            (fullRules.WithoutAncestorsOf(@"C:\Users\me\AppData\Roaming"), inRoaming),
            (Rules([@"D:\Work", "obj"], ["*.bak"]), d),
            (Rules([], hidden: true, system: true), attributes),
        };
        foreach (var (f, expected) in cases)
        {
            Assert.Equal(expected, f.ExcludesPathOrAncestor(root, path));
            Assert.Equal(expected, f.RuleHiding(root, path) != null);
        }
        // The rule it names leaves the path out by itself.
        var named = fullRules.RuleHiding(root, path);
        if (named != null) AssertRuleLeavesItOut(named, root, path, null);
    }

    [Fact]
    public void Agrees_with_the_rules_before_for_any_rules()
    {
        // Random rules and paths, compared with the copy of the rules as they were.
        var rng = new Random(15);
        string[] folderPool =
        [
            @"C:\", "C:", @"C:\Users", @"c:/users/me/", @"C:\Users\me\AppData", @"C:\Use?s\*\Desktop", @"*\obj", @"*\.git\objects", @"C:\Program*",
            @"""D:\Quoted""", " node_modules ", "build*", "?bin", "*cache*", "Desk*", @"\\nas\share", @"\\NAS\Share\tmp\", @"*\share", @"D:\Work\Old\",
            @"D:\*\Old", "Old", "", "  ", @"Y:\Albums", @"?:\$Recycle.Bin",
        ];
        string[] patternPool = ["*.tmp", " ~$* ", "Thumbs.db", "*", "Ol?", "*.bak", "", "AppData"];
        string[] roots = [@"C:\", "C:", @"C:\Users\me", @"C:\Users\me\Desktop", @"D:\", @"D:\Work", @"\\nas\share", @"Y:\"];
        string[] parts = ["Users", "me", "AppData", "Desktop", "obj", ".git", "objects", "Program Files", "node_modules", "build-out", "bin", "obin",
            "__pycache__", "Old", "Older", "Work", "Quoted", "tmp", "x.tmp", "~$draft", "Thumbs.DB", "$RECYCLE.BIN", "Albums", "share", "docs"];
        T[] Some<T>(T[] pool, double p) => [.. pool.Where(_ => rng.NextDouble() < p)];

        for (int round = 0; round < 3000; round++)
        {
            var settings = Settings(Some(folderPool, 0.15), Some(patternPool, 0.1), rng.NextDouble() < 0.3, rng.NextDouble() < 0.3);
            var filter = new ExclusionFilter(settings);
            var old = new OldRules(settings);
            string? without = rng.NextDouble() < 0.4 ? roots[rng.Next(roots.Length)].TrimEnd('\\') + "\\" + parts[rng.Next(parts.Length)] : null;
            if (without != null) { filter = filter.WithoutAncestorsOf(without); old = old.WithoutAncestorsOf(without); }
            for (int i = 0; i < 20; i++)
            {
                string root = roots[rng.Next(roots.Length)];
                string path = root.TrimEnd('\\');
                for (int depth = rng.Next(4); depth >= 0; depth--) path += "\\" + parts[rng.Next(parts.Length)];
                if (rng.NextDouble() < 0.1) path += "\\";
                bool expected = old.ExcludesPathOrAncestor(root, path);
                Assert.Equal(expected, filter.ExcludesPathOrAncestor(root, path));
                var rule = filter.RuleHiding(root, path);
                Assert.Equal(expected, rule != null);
                if (rule != null) AssertRuleLeavesItOut(rule, root, path, without);
            }
        }
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
