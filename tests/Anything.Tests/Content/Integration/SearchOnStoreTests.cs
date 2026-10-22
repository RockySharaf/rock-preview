using Anything.Core.Content.Search;
using Anything.Tests.Content.Search;

namespace Anything.Tests.Content.Integration;

// I1 (spec §13): U4's search tests run again on the real ContentStore (U1) instead of fakes. Each fixture group of
// documents is one real commit; dictionary lookups, postings, stored text, rows, picture records and deletions all come
// from the store's segments, while the counting hooks the tests rely on pass through. Each class makes its fixtures in
// memory (ContentStore.Open(null)) or in a temporary folder (memory-mapped segments, as in the app).

/// <summary>Tracks the store fixtures a test made and closes them (their folders deleted) when the test ends.</summary>
internal sealed class StoreFixtures : IDisposable
{
    readonly List<ContentFixture> _made = [];

    public ContentFixture Make(bool onDisk, DocSetCache? cache = null)
    {
        var f = ContentFixture.OnStore(onDisk, cache);
        lock (_made) _made.Add(f);
        return f;
    }

    public void Dispose()
    {
        lock (_made)
        {
            foreach (var f in _made) f.Dispose();
            _made.Clear();
        }
    }
}

/// <summary>Every ContentSearchTests case on an in-memory ContentStore.</summary>
public sealed class ContentSearchOnMemoryStoreTests : ContentSearchTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture(DocSetCache? cache = null) => _fixtures.Make(onDisk: false, cache);
    public void Dispose() => _fixtures.Dispose();
}

/// <summary>Every ContentSearchTests case on a ContentStore in a folder (mapped segment files).</summary>
public sealed class ContentSearchOnDiskStoreTests : ContentSearchTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture(DocSetCache? cache = null) => _fixtures.Make(onDisk: true, cache);
    public void Dispose() => _fixtures.Dispose();
}

/// <summary>Every picture function on picture records written to and read from a real segment's META section.</summary>
public sealed class PictureFunctionsOnMemoryStoreTests : PictureFunctionTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture() => _fixtures.Make(onDisk: false);
    public void Dispose() => _fixtures.Dispose();
}

/// <summary>Every picture function on a ContentStore in a folder.</summary>
public sealed class PictureFunctionsOnDiskStoreTests : PictureFunctionTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture() => _fixtures.Make(onDisk: true);
    public void Dispose() => _fixtures.Dispose();
}

/// <summary>The cross-keystroke lookups (budget, joining, leases, the per-segment cache) over real segments.</summary>
[Collection(ContentSearchSerial.Name)]
public sealed class ContentLookupOnStoreTests : ContentLookupTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture(DocSetCache? cache = null) => _fixtures.Make(onDisk: false, cache);
    public void Dispose() => _fixtures.Dispose();
}

/// <summary>The brute-force oracle against real dictionary lookups and real stored text, on disk.</summary>
public sealed class ContentOracleOnStoreTests : ContentOracleTests, IDisposable
{
    readonly StoreFixtures _fixtures = new();
    private protected override ContentFixture NewFixture() => _fixtures.Make(onDisk: true);
    public void Dispose() => _fixtures.Dispose();
}
