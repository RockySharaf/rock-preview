using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>Name matches first, then content-only, each in the given order (the user's sort, either direction, either sort path).</summary>
internal static class ContentRanking
{
    const int Chunk = 16384;

    /// <summary>Re-tests the same matcher with ctx.NameOnlyPass, partitions orderedIds in place (stable) and returns the first group's size.</summary>
    public static int NameFirst(int[] orderedIds, Matcher matcher, SearchContext ctx, CancellationToken ct)
    {
        int n = orderedIds.Length;
        if (n == 0) return 0;
        var byName = new bool[n];
        ctx.NameOnlyPass = true;
        try
        {
            Parallel.For(0, (n + Chunk - 1) / Chunk, new ParallelOptions { CancellationToken = ct }, c =>
            {
                int lo = c * Chunk, hi = Math.Min(n, lo + Chunk);
                for (int i = lo; i < hi; i++)
                {
                    if ((i & 2047) == 0 && ct.IsCancellationRequested) return;
                    byName[i] = matcher.IsMatch(orderedIds[i], ctx);
                }
            });
        }
        finally
        {
            ctx.NameOnlyPass = false;
        }
        ct.ThrowIfCancellationRequested();

        // Stable partition: name matches move forward in order (writing behind the reading position), the rest after them.
        int first = 0;
        foreach (bool b in byName) if (b) first++;
        if (first == 0 || first == n) return first;
        var rest = new int[n - first];
        int a = 0, r = 0;
        for (int i = 0; i < n; i++)
        {
            if (byName[i]) orderedIds[a++] = orderedIds[i];
            else rest[r++] = orderedIds[i];
        }
        rest.CopyTo(orderedIds, a);
        return first;
    }
}
