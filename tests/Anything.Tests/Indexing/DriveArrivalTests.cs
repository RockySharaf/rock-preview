using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>A USB drive plugged in: which drives are new between two listings.</summary>
public sealed class DriveArrivalTests
{
    static DriveEntry Drive(string root, DriveKind kind = DriveKind.Removable, bool ready = true, string label = "",
        string? network = null, string? subst = null) => new(root, kind, "NTFS", label, ready, network, subst);

    static readonly DriveEntry C = Drive(@"C:\", DriveKind.Fixed, label: "Windows");

    [Fact]
    public void A_stick_plugged_in_is_new()
    {
        var e = Drive(@"E:\", label: "SANDISK");
        Assert.Equal([e], DriveArrival.Plugged([C], [C, e]));
    }

    [Fact]
    public void A_usb_disk_shows_as_fixed_and_counts_too()
    {
        var f = Drive(@"F:\", DriveKind.Fixed, label: "Backup");
        Assert.Equal([f], DriveArrival.Plugged([C], [C, f]));
    }

    [Fact]
    public void What_was_there_at_start_is_not_offered()
        => Assert.Empty(DriveArrival.Plugged(null, [C, Drive(@"E:\")]));

    [Fact]
    public void A_card_put_in_a_reader_counts()
        => Assert.Single(DriveArrival.Plugged([C, Drive(@"G:\", ready: false)], [C, Drive(@"G:\")]));

    [Fact]
    public void Network_subst_optical_folder_mounts_and_empty_readers_are_not_offered()
    {
        var now = new[]
        {
            C,
            Drive(@"Z:\", DriveKind.Network, network: @"\\server\share"),
            Drive(@"S:\", DriveKind.Fixed, subst: @"C:\Projects"),
            Drive(@"D:\", DriveKind.Optical),
            Drive(@"C:\Mount\Data\", DriveKind.Fixed),
            Drive(@"H:\", ready: false),
        };
        Assert.Empty(DriveArrival.Plugged([C], now));
    }

    [Fact]
    public void Still_there_is_not_new_again()
    {
        var e = Drive(@"E:\");
        Assert.Empty(DriveArrival.Plugged([C, e], [C, e]));
    }

    [Theory]
    [InlineData("SANDISK", "SANDISK (E:)")]
    [InlineData("", "USB drive (E:)")]
    public void Named_by_its_label_and_letter(string label, string name)
        => Assert.Equal(name, DriveArrival.Name(Drive(@"E:\", label: label)));
}
