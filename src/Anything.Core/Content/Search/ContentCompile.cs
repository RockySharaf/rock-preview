using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>Hooks QueryCompiler calls; they only read what the prefetch resolved before the lock.</summary>
internal static class ContentCompile
{
    /// <summary>Name OR content for a planned plain word (registers the preparer, sets ctx.ContentExpanded); otherwise nameMatcher.</summary>
    public static Matcher Text(TextNode node, Matcher nameMatcher, SearchContext ctx)
    {
        if (ctx.Content is not { } prefetch || ctx.ContentMap == null || !prefetch.Plan.Terms.TryGetValue(node, out var term) || !term.Plain)
            return nameMatcher;
        if (prefetch.For(node) is not { } set) return nameMatcher; // still looking, nothing resolved yet: by name only (ContentPending)
        var content = new ContentSetMatcher(set);
        ctx.AddPreparer(content);
        ctx.ContentExpanded = true;
        return new NameOrContentMatcher(nameMatcher, content);
    }

    /// <summary>The matcher for a content function (NoneMatcher when there is no index or mode); null when not a content name.</summary>
    public static Matcher? Function(FunctionNode node, SearchContext ctx)
    {
        if (!ContentFunctions.IsContentName(node.Name)) return null;
        if (ctx.Content is not { } prefetch || ctx.ContentMap == null || prefetch.For(node) is not { } set) return NoneMatcher.Instance;
        var m = new ContentSetMatcher(set);
        ctx.AddPreparer(m);
        return m;
    }
}
