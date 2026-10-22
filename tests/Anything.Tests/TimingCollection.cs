namespace Anything.Tests;

/// <summary>
/// Tests that measure how long something takes (a stop heard within a bound, say). They run on their own, after the
/// rest, so the busy threads and disk reads of other tests running beside them can't push them past their bounds.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
