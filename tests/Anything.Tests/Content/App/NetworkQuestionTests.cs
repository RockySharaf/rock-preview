using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Indexing;

namespace Anything.Tests.Content.App;

/// <summary>
/// The windows' question about Settings' Network drives switch: which network drives and shares it names
/// (ContentEstimator.NetworkRootsNotReadInside) and what its sentence says (ContentPresentation.NetworkOffer).
/// </summary>
public sealed class NetworkQuestionTests
{
    static DriveEntry Net(string root, string share) => new(root, DriveKind.Network, "NTFS", "", IsReady: true, NetworkPath: share, SubstTarget: null);
    static DriveEntry Local(string root) => new(root, DriveKind.Fixed, "NTFS", "", IsReady: true, NetworkPath: null, SubstTarget: null);

    static FolderCoverage Held(string folder) => new(folder, CoverageReason.Covered, folder, folder, null, null);
    static FolderCoverage TurnedOff(string folder) => new(folder, CoverageReason.DriveTurnedOff, null, folder, null, folder);

    static ContentSettings Content(Action<ContentSettings>? change = null)
    {
        var s = new ContentSettings { Mode = ContentMode.Documents, ModeExplicit = true, UseMyFolders = false, Folders = [@"C:\Users\Me"] };
        change?.Invoke(s);
        return s;
    }

    static IReadOnlyList<string> Named(ContentSettings content, IndexSettings names, IReadOnlyList<DriveEntry> drives, Func<string, FolderCoverage?>? coverage = null)
        => ContentEstimator.NetworkRootsNotReadInside(content, names, drives, coverage ?? Held);

    [Fact]
    public void Mapped_drives_and_shares_searched_by_name_are_named()
    {
        var names = new IndexSettings { Folders = [@"\\archive\scans", @"D:\Projects"] };
        Assert.Equal([@"S:\", @"Z:\", @"\\archive\scans"],
            Named(Content(), names, [Local(@"C:\"), Net(@"S:\", @"\\fs01\shared"), Net(@"Z:\", @"\\nas\jobs")]));
    }

    [Fact]
    public void Drives_the_name_index_doesnt_hold_are_not_named()
    {
        Assert.Empty(Named(Content(), new IndexSettings(), [Net(@"Z:\", @"\\nas\jobs")], TurnedOff));
        Assert.Empty(Named(Content(), new IndexSettings { IndexNetworkDrives = false }, [Net(@"Z:\", @"\\nas\jobs")]));
        // The shares among the folders searched by name don't depend on the network drives' switch.
        Assert.Equal([@"\\archive\scans"], Named(Content(), new IndexSettings { IndexNetworkDrives = false, Folders = [@"\\archive\scans"] }, []));
    }

    [Fact]
    public void Drives_already_read_inside_or_left_out_are_not_named()
    {
        DriveEntry[] drives = [Net(@"Z:\", @"\\nas\jobs"), Net(@"Y:\", @"\\nas\home")];
        Assert.Equal([@"Y:\"], Named(Content(s => s.Folders.Add(@"Z:\")), new IndexSettings(), drives));
        Assert.Equal([@"Y:\"], Named(Content(s => s.Folders.Add(@"\\nas\jobs")), new IndexSettings(), drives)); // through its share
        Assert.Equal([@"Y:\"], Named(Content(s => s.ExcludeFolders.Add(@"Z:\")), new IndexSettings(), drives));
        Assert.Empty(Named(Content(s => s.Folders.Add(@"\\archive\scans")), new IndexSettings { Folders = [@"\\archive\scans"] }, []));
        // A rule by name applies below a share, never to the share itself (as the scope has it).
        Assert.Equal([@"\\archive\scans"], Named(Content(s => s.ExcludeFolders.Add("*scans*")), new IndexSettings { Folders = [@"\\archive\scans"] }, []));
    }

    [Fact]
    public void A_folder_searched_by_name_on_a_mapped_drive_that_isnt_searched_whole_is_named()
    {
        DriveEntry[] drives = [Net(@"Z:\", @"\\nas\jobs")];
        var names = new IndexSettings { IndexNetworkDrives = false, Folders = [@"Z:\Projects", @"D:\Local"] };
        Assert.Equal([@"Z:\Projects"], Named(Content(), names, drives));
        // Read inside already through its share, or as part of the whole drive named: not named again.
        Assert.Empty(Named(Content(s => s.Folders.Add(@"\\nas\jobs\Projects")), names, drives));
        Assert.Equal([@"Z:\"], Named(Content(), new IndexSettings { Folders = [@"Z:\Projects"] }, drives));
    }

    [Fact]
    public void The_question_says_what_reading_them_costs_once_it_is_counted()
    {
        using var _ = new CultureScope("en-US");
        var e = new ContentEstimate(ContentMode.Documents, 12_400, 0, 700_000_000, TimeSpan.FromHours(5), 3_100_000_000, AtLeast: false);
        string text = ContentPresentation.NetworkOffer([@"Z:\"], 8192, elevated: false, e);
        Assert.StartsWith("Files on Z: are found by name only. Search inside it too? 12,400 documents: about 0.7 GB more on this PC, " +
            "reading about 3.1 GB over the network (at most 8 MB/s); the first pass is about ", text);
        Assert.EndsWith(".", text);
        // Nothing counted yet, or nothing found: the plain question.
        Assert.Equal(ContentPresentation.NetworkOffer([@"Z:\"], 8192, elevated: false),
            ContentPresentation.NetworkOffer([@"Z:\"], 8192, elevated: false, e with { Documents = 0 }));
        Assert.Equal(0, ContentPresentation.NetworkOfferFiles(e with { Documents = 0 }));
        Assert.Equal(12_400 + 30, ContentPresentation.NetworkOfferFiles(e with { Mode = ContentMode.DocumentsAndPictures, Pictures = 30 }));
    }
}
