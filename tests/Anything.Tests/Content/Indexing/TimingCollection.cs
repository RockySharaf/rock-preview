namespace Anything.Tests.Content.Indexing;

/// <summary>Tests that measure time or wait on real threads: run alone, not beside the rest of the suite competing for CPU.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Content indexing timing";
}
