using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// The status bar while a drive is scanned: a fixed drive scanned instead of read says instant indexing is off where it
/// can be turned on, and only that folders are scanned in a copy that can't turn it on (the Microsoft Store's, which has
/// no service and may not ask for administrator rights).
/// </summary>
public sealed class ScanningStatusTests
{
    [Fact]
    public void A_fixed_drive_scanned_instead_says_instant_indexing_is_off_where_it_can_be_turned_on() =>
        Assert.Equal($"Instant indexing is off: scanning folders (C:\\, {1234:N0} objects)",
            IndexManager.ScanningText(@"C:\", 1234, scannedInstead: true, canTurnOnInstantIndexing: true));

    [Fact]
    public void Where_it_cannot_be_turned_on_the_drive_is_just_scanning_folders()
    {
        string text = IndexManager.ScanningText(@"C:\", 1234, scannedInstead: true, canTurnOnInstantIndexing: false);
        Assert.Equal($"Scanning folders (C:\\, {1234:N0} objects)", text);
        Assert.DoesNotContain("instant", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("administrator", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Any_other_root_says_what_it_scans_either_way(bool canTurnOn) =>
        Assert.Equal($"Scanning E:\\ ({56:N0} objects)", IndexManager.ScanningText(@"E:\", 56, scannedInstead: false, canTurnOnInstantIndexing: canTurnOn));

    [Fact]
    public void An_index_can_turn_instant_indexing_on_unless_told_otherwise()
    {
        using (var made = new IndexManager()) Assert.True(made.CanTurnOnInstantIndexing);
        using (var usual = (IndexManager)Services.CreateIndexManager()) Assert.True(usual.CanTurnOnInstantIndexing);
        using (var store = (IndexManager)Services.CreateIndexManager(canTurnOnInstantIndexing: false)) Assert.False(store.CanTurnOnInstantIndexing);
    }
}
