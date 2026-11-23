using System.Runtime.CompilerServices;
using Anything.Core.Content.Indexing;

namespace Anything.Tests;

/// <summary>
/// Before any test runs: options that don't name a power policy get one that never pauses, so a nearly full drive on
/// the test PC can't stop the thumbnail store saving or the text index reading. Tests of a pause inject their own.
/// </summary>
static class TestPower
{
    [ModuleInitializer]
    internal static void NeverLowOnDisk() => DefaultPowerPolicy.Current = NoPowerPolicy.Instance;
}
