using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Anything.Core.Search;

/// <summary>Compiled predicate over an entry id. Must be thread-safe (evaluated from Parallel.For).</summary>
internal abstract class Matcher
{
    /// <summary>Relative evaluation cost; AND/OR evaluate cheaper children first.</summary>
    public abstract int Cost { get; }
    public abstract bool IsMatch(int id, SearchContext ctx);
    public virtual void Prepare(SearchContext ctx, CancellationToken ct) { }
}

sealed class AllMatcher : Matcher
{
    public static readonly AllMatcher Instance = new();
    public override int Cost => 0;
    public override bool IsMatch(int id, SearchContext ctx) => true;
}

sealed class NoneMatcher : Matcher
{
    public static readonly NoneMatcher Instance = new();
    public override int Cost => 0;
    public override bool IsMatch(int id, SearchContext ctx) => false;
}

sealed class AndMatcher(Matcher[] children) : Matcher
{
    readonly Matcher[] _c = children.OrderBy(c => c.Cost).ToArray();
    public override int Cost { get; } = children.Sum(c => c.Cost);
    public override bool IsMatch(int id, SearchContext ctx)
    {
        foreach (var c in _c) if (!c.IsMatch(id, ctx)) return false;
        return true;
    }
}

sealed class OrMatcher(Matcher[] children) : Matcher
{
    readonly Matcher[] _c = children.OrderBy(c => c.Cost).ToArray();
    public override int Cost { get; } = children.Sum(c => c.Cost);
    public override bool IsMatch(int id, SearchContext ctx)
    {
        foreach (var c in _c) if (c.IsMatch(id, ctx)) return true;
        return false;
    }
}

sealed class NotMatcher(Matcher child) : Matcher
{
    public override int Cost => child.Cost;
    public override bool IsMatch(int id, SearchContext ctx) => !child.IsMatch(id, ctx);
}

sealed class KindMatcher(bool folders) : Matcher
{
    public override int Cost => 1;
    public override bool IsMatch(int id, SearchContext ctx) => ctx.Index.IsDirectory(id) == folders;
}

/// <summary>Base for text predicates that see either the name or the full path.</summary>
abstract class TextMatcherBase(bool usePath) : Matcher
{
    protected bool UsePath { get; } = usePath;
    public override bool IsMatch(int id, SearchContext ctx)
        => UsePath ? Test(ctx.FullPath(id)) : Test(ctx.Index.Name(id));
    public abstract bool Test(ReadOnlySpan<char> text);
}

sealed class SubstringMatcher(Needle needle, bool wholeWord, bool usePath) : TextMatcherBase(usePath)
{
    public override int Cost => UsePath ? 20 : 5;
    public override bool Test(ReadOnlySpan<char> text) => needle.Contains(text, wholeWord);
}

sealed class GlobMatcher(GlobPattern glob, bool usePath) : TextMatcherBase(usePath)
{
    public override int Cost => UsePath ? 22 : 6;
    public override bool Test(ReadOnlySpan<char> text) => glob.IsMatch(text);
}

sealed class RegexMatcher(Regex regex, bool usePath) : TextMatcherBase(usePath)
{
    public override int Cost => UsePath ? 50 : 30;
    public override bool Test(ReadOnlySpan<char> text)
    {
        try { return regex.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

enum AffixKind { Start, End }

sealed class AffixMatcher(Needle needle, AffixKind kind, bool usePath) : TextMatcherBase(usePath)
{
    public override int Cost => UsePath ? 18 : 3;
    public override bool Test(ReadOnlySpan<char> text) => kind == AffixKind.Start ? needle.StartsWith(text) : needle.EndsWith(text);
}

sealed class ExtMatcher : Matcher
{
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    readonly GlobPattern[] _globs;
    readonly bool _any;

    public ExtMatcher(IEnumerable<string> exts)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globs = new List<GlobPattern>();
        foreach (var raw in exts)
        {
            string e = raw.Trim().TrimStart('.');
            if (TextUtil.HasWildcards(e)) globs.Add(new GlobPattern(e, false, true));
            else set.Add(e);
        }
        _lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
        _globs = globs.ToArray();
        _any = set.Count > 0 || _globs.Length > 0;
    }

    public override int Cost => 2;

    /// <summary>Same rule as FileIndex.Extension, without allocating.</summary>
    public static ReadOnlySpan<char> ExtensionOf(FileIndex index, int id)
    {
        if (index.IsDirectory(id)) return default;
        string n = index.Name(id);
        int dot = n.LastIndexOf('.');
        return dot <= 0 || dot == n.Length - 1 ? default : n.AsSpan(dot + 1);
    }

    public override bool IsMatch(int id, SearchContext ctx)
    {
        if (!_any) return false;
        var ext = ExtensionOf(ctx.Index, id);
        if (_lookup.Contains(ext)) return true;
        foreach (var g in _globs) if (g.IsMatch(ext)) return true;
        return false;
    }
}

enum NumProp { Size, Modified, Created, Length, Depth, ChildCount, ChildFiles, ChildFolders, Accessed, RecentChange, RunCount, DateRun }

sealed class NumMatcher(NumProp prop, NumCond[] conds, bool fromDisk = false) : Matcher
{
    // Date accessed read from the disk (not indexed) is by far the slowest test: everything else filters first.
    public override int Cost => fromDisk ? 1000
        : prop is NumProp.Depth or NumProp.ChildCount or NumProp.ChildFiles or NumProp.ChildFolders or NumProp.RunCount or NumProp.DateRun ? 2 : 1;

    public override bool IsMatch(int id, SearchContext ctx)
    {
        var idx = ctx.Index;
        long v;
        bool known = true;
        switch (prop)
        {
            case NumProp.Size: v = idx.Size(id); known = v >= 0; break;
            case NumProp.Modified: v = idx.Modified(id); known = v != 0; break;
            case NumProp.Created: v = idx.Created(id); known = v != 0; break;
            case NumProp.Length: v = idx.Name(id).Length; break;
            case NumProp.Depth: v = ctx.Depth(id); break;
            case NumProp.Accessed: v = ctx.Accessed(id); known = v != 0; break;
            case NumProp.RecentChange: v = idx.RecentChange(id); known = v != 0; break;
            case NumProp.RunCount: v = ctx.Runs!.Get(id).Count; break;
            case NumProp.DateRun: v = ctx.Runs!.Get(id).LastRun; known = v != 0; break;
            default:
                if (!idx.IsDirectory(id)) return false;
                v = prop switch
                {
                    NumProp.ChildFiles => ctx.ChildFiles![id],
                    NumProp.ChildFolders => ctx.ChildFolders![id],
                    _ => ctx.ChildFiles![id] + ctx.ChildFolders![id],
                };
                break;
        }
        return ValueParsers.Eval(conds, v, known);
    }
}

sealed class AttribMatcher(FileAttributes mask) : Matcher
{
    public override int Cost => 1;
    public override bool IsMatch(int id, SearchContext ctx)
    {
        var a = ctx.Index.Attributes(id);
        var m = mask;
        if ((m & FileAttributes.Normal) != 0)
        {
            // 'N' means no other attributes (stored as Normal or 0).
            if ((a & ~FileAttributes.Normal) != 0) return false;
            m &= ~FileAttributes.Normal;
        }
        return (a & m) == m;
    }
}

/// <summary>parent:/infolder:/nosubfolders: — the containing folder equals the value. Cached per folder.</summary>
sealed class ParentMatcher(string folder) : Matcher
{
    readonly string _folder = Normalize(folder);
    sbyte[]? _cache;

    static string Normalize(string p) => p.Replace('/', '\\').TrimEnd('\\');

    public override int Cost => 3;
    public override void Prepare(SearchContext ctx, CancellationToken ct) => _cache = new sbyte[ctx.Count];

    public override bool IsMatch(int id, SearchContext ctx)
    {
        int parent = ctx.Index.Parent(id);
        if (parent == FileIndex.NoParent) return false;
        var cache = _cache!;
        sbyte c = cache[parent];
        if (c == 0)
        {
            string prefix = ctx.Paths.Prefix(parent);
            c = prefix.AsSpan(0, prefix.Length - 1).Equals(_folder, StringComparison.OrdinalIgnoreCase) ? (sbyte)1 : (sbyte)-1;
            cache[parent] = c;
        }
        return c > 0;
    }
}

/// <summary>root: — entries directly inside a root.</summary>
sealed class RootMatcher : Matcher
{
    public override int Cost => 1;
    public override bool IsMatch(int id, SearchContext ctx)
    {
        int p = ctx.Index.Parent(id);
        return p != FileIndex.NoParent && ctx.Index.Parent(p) == FileIndex.NoParent;
    }
}

/// <summary>empty: — folders without live children.</summary>
sealed class EmptyMatcher : Matcher
{
    public override int Cost => 2;
    public override bool IsMatch(int id, SearchContext ctx)
        => ctx.Index.IsDirectory(id) && ctx.ChildFiles![id] == 0 && ctx.ChildFolders![id] == 0;
}

/// <summary>child: — folders with a child whose name matches. Precomputed in one parallel pass.</summary>
sealed class ChildMatcher(TextMatcherBase nameTest) : Matcher
{
    bool[]? _has;
    public override int Cost => 2;

    public override void Prepare(SearchContext ctx, CancellationToken ct)
    {
        var has = new bool[ctx.Count];
        var idx = ctx.Index;
        Parallel.For(0, (ctx.Count + 16383) / 16384, new ParallelOptions { CancellationToken = ct }, chunk =>
        {
            int lo = chunk * 16384, hi = Math.Min(ctx.Count, lo + 16384);
            for (int id = lo; id < hi; id++)
            {
                if (!idx.IsAlive(id)) continue;
                int p = idx.Parent(id);
                if ((uint)p < (uint)has.Length && !has[p] && nameTest.Test(idx.Name(id))) has[p] = true;
            }
        });
        _has = has;
    }

    public override bool IsMatch(int id, SearchContext ctx) => ctx.Index.IsDirectory(id) && _has![id];
}

sealed class TypeMatcher(TextMatcherBase test) : Matcher
{
    readonly ConcurrentDictionary<string, bool> _byExt = new(StringComparer.OrdinalIgnoreCase);
    bool? _folder;
    public override int Cost => 4;

    public override bool IsMatch(int id, SearchContext ctx)
    {
        if (ctx.Index.IsDirectory(id)) return _folder ??= test.Test(ValueParsers.TypeName(true, ""));
        string ext = ctx.Index.Extension(id);
        return _byExt.GetOrAdd(ext, e => test.Test(ValueParsers.TypeName(false, e)));
    }
}

/// <summary>dupe: — marker; the engine post-filters the result set.</summary>
sealed class DupeMatcher : Matcher
{
    public override int Cost => 0;
    public override bool IsMatch(int id, SearchContext ctx) => true;
}

/// <summary>Turns a parsed <see cref="QueryNode"/> tree into matchers.</summary>
internal sealed class QueryCompiler(SearchContext ctx, bool largeIndex)
{
    public Matcher Compile(QueryNode node) => node switch
    {
        AllNode => AllMatcher.Instance,
        AndNode a => Simplify(a.Children.Select(Compile).ToArray(), and: true),
        OrNode o => Simplify(o.Children.Select(Compile).ToArray(), and: false),
        NotNode n => Compile(n.Child) switch
        {
            AllMatcher => NoneMatcher.Instance,
            NoneMatcher => AllMatcher.Instance,
            var m => new NotMatcher(m),
        },
        TextNode t => Content.Search.ContentCompile.Text(t, CompileText(t.Text, t.Flags, allowWholeWord: true), ctx),
        FunctionNode f => CompileFunction(f),
        _ => throw new QueryException("Unsupported query node " + node.GetType().Name),
    };

    static Matcher Simplify(Matcher[] children, bool and)
    {
        var list = new List<Matcher>();
        foreach (var c in children)
        {
            if (and)
            {
                if (c is NoneMatcher) return NoneMatcher.Instance;
                if (c is AllMatcher) continue;
            }
            else
            {
                if (c is AllMatcher) return AllMatcher.Instance;
                if (c is NoneMatcher) continue;
            }
            list.Add(c);
        }
        if (list.Count == 0) return and ? AllMatcher.Instance : NoneMatcher.Instance;
        if (list.Count == 1) return list[0];
        return and ? new AndMatcher(list.ToArray()) : new OrMatcher(list.ToArray());
    }

    static bool UsePath(string text, TermFlags f) => f.Path switch
    {
        PathMode.Path => true,
        PathMode.NoPath => false,
        _ => !f.Regex && text.Contains('\\'),
    };

    internal TextMatcherBase CompileText(string text, TermFlags f, bool allowWholeWord)
    {
        bool usePath = UsePath(text, f);
        if (f.Regex)
        {
            var opts = RegexOptions.CultureInvariant | (f.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            if (largeIndex) opts |= RegexOptions.Compiled;
            try { return new RegexMatcher(new Regex(text, opts, TimeSpan.FromMilliseconds(100)), usePath); }
            catch (ArgumentException ex) { throw new QueryException($"Invalid regular expression '{text}': {ex.Message}"); }
        }
        if (f.Wildcards && TextUtil.HasWildcards(text))
            return new GlobMatcher(new GlobPattern(text, f.MatchCase, f.MatchDiacritics), usePath);
        return new SubstringMatcher(new Needle(text, f.MatchCase, f.MatchDiacritics), allowWholeWord && f.WholeWord, usePath);
    }

    Matcher CompileFunction(FunctionNode f)
    {
        string v = f.Value;
        switch (f.Name)
        {
            case "file": return new KindMatcher(folders: false);
            case "folder": return new KindMatcher(folders: true);
            case "audio" or "zip" or "doc" or "exe" or "pic" or "video":
                return new ExtMatcher(ValueParsers.MacroExtensions[f.Name].Split(';'));
            case "ext":
                return new ExtMatcher(v.Split(';'));
            case "size":
                return Num(NumProp.Size, f, ValueParsers.ParseSize, dash: true);
            case "dm":
                return Num(NumProp.Modified, f, s => ValueParsers.ParseDate(s, ctx.Now), dash: false);
            case "dc":
                return Num(NumProp.Created, f, s => ValueParsers.ParseDate(s, ctx.Now), dash: false);
            case "da":
                return Num(NumProp.Accessed, f, s => ValueParsers.ParseDate(s, ctx.Now), dash: false, fromDisk: !ctx.Index.HasDateAccessed);
            case "rc":
                return Num(NumProp.RecentChange, f, s => ValueParsers.ParseDate(s, ctx.Now), dash: false);
            case "dr":
                ctx.NeedRuns = true;
                return Num(NumProp.DateRun, f, s => ValueParsers.ParseDate(s, ctx.Now), dash: false);
            case "runcount":
                ctx.NeedRuns = true;
                return Num(NumProp.RunCount, f, ValueParsers.ParseInt, dash: true);
            case "len":
                return Num(NumProp.Length, f, ValueParsers.ParseInt, dash: true);
            case "depth":
                return Num(NumProp.Depth, f, ValueParsers.ParseInt, dash: true);
            case "childcount":
                ctx.NeedChildCounts = true;
                return Num(NumProp.ChildCount, f, ValueParsers.ParseInt, dash: true);
            case "childfilecount":
                ctx.NeedChildCounts = true;
                return Num(NumProp.ChildFiles, f, ValueParsers.ParseInt, dash: true);
            case "childfoldercount":
                ctx.NeedChildCounts = true;
                return Num(NumProp.ChildFolders, f, ValueParsers.ParseInt, dash: true);
            case "attrib":
            {
                if (string.IsNullOrWhiteSpace(v)) return AllMatcher.Instance;
                var mask = ValueParsers.ParseAttributes(v) ?? throw new QueryException($"attrib: invalid value '{v}' (use letters A C D E H I L N O P R S T U V)");
                return new AttribMatcher(mask);
            }
            case "parent":
            {
                if (string.IsNullOrWhiteSpace(v)) return AllMatcher.Instance;
                var m = new ParentMatcher(v);
                ctx.AddPreparer(m);
                return m;
            }
            case "startwith" or "endwith":
            {
                if (v.Length == 0) return AllMatcher.Instance;
                bool usePath = f.Flags.Path == PathMode.Path;
                return new AffixMatcher(new Needle(v, f.Flags.MatchCase, f.Flags.MatchDiacritics),
                    f.Name == "startwith" ? AffixKind.Start : AffixKind.End, usePath);
            }
            case "root": return new RootMatcher();
            case "empty":
                ctx.NeedChildCounts = true;
                return new EmptyMatcher();
            case "child":
            {
                if (v.Length == 0)
                {
                    ctx.NeedChildCounts = true; // child: alone = folders with at least one child
                    return new AndMatcher([new KindMatcher(true), new NotMatcher(new EmptyMatcher())]);
                }
                var test = CompileText(v, f.Flags with { Path = PathMode.NoPath, Regex = false }, allowWholeWord: true);
                var m = new ChildMatcher(test);
                ctx.AddPreparer(m);
                return m;
            }
            case "type":
            {
                if (v.Length == 0) return AllMatcher.Instance;
                return new TypeMatcher(CompileText(v, f.Flags with { Path = PathMode.NoPath, Regex = false }, allowWholeWord: true));
            }
            case "dupe":
                ctx.Dupe = true;
                return new DupeMatcher();
        }
        if (Content.Search.ContentCompile.Function(f, ctx) is { } cm) return cm;
        throw new QueryException($"Unknown function '{f.Name}:'");
    }

    static Matcher Num(NumProp prop, FunctionNode f, Func<string, ValueRange?> parse, bool dash, bool fromDisk = false)
    {
        var conds = ValueParsers.ParseConds(f.Name, f.Op, f.Value, parse, dash);
        return conds is null ? AllMatcher.Instance : new NumMatcher(prop, conds, fromDisk);
    }
}
