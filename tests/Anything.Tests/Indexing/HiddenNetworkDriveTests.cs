using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// Network drives mapped in the user's normal session are invisible to the app when it runs as administrator: they are
/// found in the user's remembered mappings and indexed by their share instead.
/// </summary>
public sealed class HiddenNetworkDriveTests
{
    const uint C = 1u << 2, Z = 1u << 25;

    [Fact]
    public void Mapping_whose_letter_is_missing_here_is_listed_by_its_share()
    {
        var found = Drives.HiddenMappings(C, [("Z", @"\\fileserver\Projects"), ("y", @"\\fileserver\Scans\")]);
        Assert.Equal(2, found.Count);
        var z = found[0];
        Assert.Equal(@"\\fileserver\Projects\", z.Root);
        Assert.Equal(DriveKind.Network, z.Kind);
        Assert.Equal(@"\\fileserver\Projects", z.NetworkPath);
        Assert.False(z.IsDriveLetter);
        Assert.True(z.IsReady);
        Assert.StartsWith("Z:", z.Label);
        Assert.Equal(@"\\fileserver\Scans", found[1].NetworkPath);
        Assert.StartsWith("Y:", found[1].Label);
    }

    [Fact]
    public void Mapping_seen_under_its_letter_or_malformed_is_not_listed_again()
    {
        Assert.Empty(Drives.HiddenMappings(C | Z, [("Z", @"\\fileserver\Projects")])); // the normal case: Z: is here
        Assert.Empty(Drives.HiddenMappings(C, [("Z", "not a share"), ("", @"\\a\b"), ("1", @"\\a\b")]));
        Assert.Single(Drives.HiddenMappings(C, [("X", @"\\a\b"), ("W", @"\\A\B\")])); // two letters on one share: once
    }

    [Fact]
    public void Hidden_mapping_is_planned_as_a_network_root_named_by_its_share()
    {
        var drives = new List<DriveEntry> { new(@"C:\", DriveKind.Fixed, "NTFS", "", true, null, null) };
        drives.AddRange(Drives.HiddenMappings(C, [("Z", @"\\fileserver\Projects")]));
        var plan = IndexPlan.Plan(new IndexSettings(), drives, ExclusionFilter.None, _ => null);

        var z = Assert.Single(plan, r => r.Name.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.Equal(@"\\fileserver\Projects", z.Name);
        Assert.True(z.Network);
        Assert.False(z.Volume);
        Assert.Equal(@"\\fileserver\Projects", z.Share);

        // Network drives turned off as a group leave it out too.
        var off = IndexPlan.Plan(new IndexSettings { IndexNetworkDrives = false }, drives, ExclusionFilter.None, _ => null);
        Assert.DoesNotContain(off, r => r.Network);
    }

    [Fact]
    public void Share_also_mapped_to_a_visible_letter_is_indexed_once()
    {
        var drives = new List<DriveEntry>
        {
            new(@"Y:\", DriveKind.Network, "", "", true, @"\\fileserver\Projects", null),
        };
        drives.AddRange(Drives.HiddenMappings(1u << 24, [("Z", @"\\fileserver\Projects")]));
        var plan = IndexPlan.Plan(new IndexSettings(), drives, ExclusionFilter.None, _ => null);
        Assert.Equal(["Y:"], plan.Select(r => r.Name));
    }
}
