using System.Text;

namespace Anything.Core.Search;

/// <summary>How a text term chooses between the file name and the full path.</summary>
public enum PathMode : byte
{
    /// <summary>Name, unless the term contains a backslash.</summary>
    Auto,
    /// <summary>Full path (path: modifier or Match Path option).</summary>
    Path,
    /// <summary>Always the name (nopath: modifier).</summary>
    NoPath,
}

/// <summary>Per-term match options. Seeded from <see cref="SearchOptions"/> and overridden by modifiers.</summary>
public readonly record struct TermFlags(
    bool MatchCase,
    bool MatchDiacritics,
    bool WholeWord,
    PathMode Path,
    bool Regex,
    bool Wildcards)
{
    public static TermFlags From(SearchOptions o)
        => new(o.MatchCase, o.MatchDiacritics, o.MatchWholeWord, o.MatchPath ? PathMode.Path : PathMode.Auto, o.Regex, true);

    internal string Prefix()
    {
        var sb = new StringBuilder();
        if (MatchCase) sb.Append("case:");
        if (MatchDiacritics) sb.Append("diacritics:");
        if (WholeWord) sb.Append("ww:");
        if (Path == PathMode.Path) sb.Append("path:");
        if (Path == PathMode.NoPath) sb.Append("nopath:");
        if (Regex) sb.Append("regex:");
        if (!Wildcards) sb.Append("nowildcards:");
        return sb.ToString();
    }
}

public enum CompareOp { None, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

/// <summary>Parsed query tree. <see cref="object.ToString"/> renders a canonical form used by tests.</summary>
public abstract class QueryNode
{
    private protected QueryNode() { }

    public static QueryNode And(params IEnumerable<QueryNode?> nodes)
    {
        var list = new List<QueryNode>();
        foreach (var n in nodes)
        {
            if (n is null || n is AllNode) continue;
            if (n is AndNode a) list.AddRange(a.Children);
            else list.Add(n);
        }
        return list.Count switch { 0 => AllNode.Instance, 1 => list[0], _ => new AndNode(list) };
    }

    public static QueryNode? Or(IEnumerable<QueryNode?> nodes)
    {
        var list = new List<QueryNode>();
        foreach (var n in nodes)
        {
            if (n is null) continue;
            if (n is AllNode) return AllNode.Instance;
            if (n is OrNode o) list.AddRange(o.Children);
            else list.Add(n);
        }
        return list.Count switch { 0 => null, 1 => list[0], _ => new OrNode(list) };
    }
}

/// <summary>Matches every live entry (empty query).</summary>
public sealed class AllNode : QueryNode
{
    public static AllNode Instance { get; } = new();
    AllNode() { }
    public override string ToString() => "ALL";
}

public sealed class AndNode(IReadOnlyList<QueryNode> children) : QueryNode
{
    public IReadOnlyList<QueryNode> Children { get; } = children;
    public override string ToString() => "AND(" + string.Join(",", Children) + ")";
}

public sealed class OrNode(IReadOnlyList<QueryNode> children) : QueryNode
{
    public IReadOnlyList<QueryNode> Children { get; } = children;
    public override string ToString() => "OR(" + string.Join(",", Children) + ")";
}

public sealed class NotNode(QueryNode child) : QueryNode
{
    public QueryNode Child { get; } = child;
    public override string ToString() => "NOT(" + Child + ")";
}

/// <summary>A plain, wildcard or regex text term.</summary>
public sealed class TextNode(string text, TermFlags flags) : QueryNode
{
    public string Text { get; } = text;
    public TermFlags Flags { get; } = flags;
    public bool HasWildcards => Flags.Wildcards && !Flags.Regex && TextUtil.HasWildcards(Text);
    public override string ToString() => Flags.Prefix() + "'" + Text + "'";
}

/// <summary>
/// A search function or restriction (ext:, size:, dm:, file:, audio:, ...). <see cref="Name"/> is the canonical
/// lower-case name; <see cref="Value"/> is the raw value text after the comparison operator.
/// </summary>
public sealed class FunctionNode(string name, CompareOp op, string value, TermFlags flags) : QueryNode
{
    public string Name { get; } = name;
    public CompareOp Op { get; } = op;
    public string Value { get; } = value;
    public TermFlags Flags { get; } = flags;

    public override string ToString() => Name + ":" + OpText(Op) + Value;

    internal static string OpText(CompareOp op) => op switch
    {
        CompareOp.Equal => "=",
        CompareOp.NotEqual => "!=",
        CompareOp.Less => "<",
        CompareOp.LessOrEqual => "<=",
        CompareOp.Greater => ">",
        CompareOp.GreaterOrEqual => ">=",
        _ => "",
    };
}

/// <summary>Thrown for queries that cannot be evaluated (bad regex, bad function value).</summary>
public sealed class QueryException(string message) : Exception(message);
