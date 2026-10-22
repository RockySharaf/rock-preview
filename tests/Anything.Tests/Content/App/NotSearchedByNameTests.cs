using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Indexing;

namespace Anything.Tests.Content.App;

/// <summary>
/// The folders searched inside that the name index doesn't hold (so never read inside), and how Settings, setup and the
/// offer say so: ContentEstimator.FoldersNotSearchedByName and its words in ContentPresentation.
/// </summary>
public sealed class NotSearchedByNameTests
{
    const string Desktop = @"C:\Users\Me\Desktop", Documents = @"C:\Users\Me\Documents", Downloads = @"C:\Users\Me\Downloads",
        Pictures = @"C:\Users\Me\Pictures", OneDrive = @"C:\Users\Me\OneDrive";

    /// <summary>My folders without OneDrive (its variables unset).</summary>
    static readonly string[] Mine = [Desktop, Documents, Downloads, Pictures];

    static IReadOnlyList<string> Missing(ContentSettings content, IndexSettings names, IReadOnlyList<string>? mine = null)
        => ContentEstimator.FoldersNotSearchedByName(content, names, mine ?? Mine);

    /// <summary>The brief's PC: C:\ left out of the name index, Desktop and Downloads searched by name on their own.</summary>
    static IndexSettings ThisPc() => new() { ExcludeFolders = [@"C:\"], Folders = [Desktop, Downloads] };

    // ---- which folders ---------------------------------------------------------------------------

    [Fact]
    public void Every_folder_is_held_while_whole_drives_are_searched_by_name()
    {
        Assert.Empty(Missing(new ContentSettings(), new IndexSettings()));
        Assert.Empty(Missing(new ContentSettings { Folders = [@"D:\Projects", @"E:\"] }, new IndexSettings(), [.. Mine, OneDrive]));
    }

    [Fact]
    public void A_drive_left_out_leaves_out_the_folders_not_searched_by_name_on_their_own()
    {
        Assert.Equal([Documents, Pictures], Missing(new ContentSettings(), ThisPc()));
        // Folders and drives turned off the same way: a drive switched off, a folder left out, whole drives off.
        Assert.Equal([Desktop, Documents, Downloads, Pictures], Missing(new ContentSettings(), new IndexSettings { SkipDrives = [@"C:\"] }));
        Assert.Equal([Pictures], Missing(new ContentSettings(), new IndexSettings { ExcludeFolders = [Pictures] }));
        Assert.Equal([Documents, Pictures], Missing(new ContentSettings(), new IndexSettings { IndexNtfsVolumes = false, Folders = [Desktop, Downloads] }));
    }

    [Fact]
    public void Nested_folders_follow_the_folder_searched_by_name_that_holds_them()
    {
        // A folder searched by name above them holds them all, even with its drive left out.
        Assert.Empty(Missing(new ContentSettings(), new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\Me"] }));
        // One inside Documents holds only part of it: Documents is still listed (adding it holds the rest too).
        var part = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [Desktop, Downloads, Documents + @"\Work"] };
        Assert.Equal([Documents, Pictures], Missing(new ContentSettings(), part));
        // A folder left out inside one searched by name.
        var inner = new IndexSettings { ExcludeFolders = [@"C:\", Documents], Folders = [@"C:\Users\Me"] };
        Assert.Equal([Documents], Missing(new ContentSettings(), inner));
        // A folder added inside one of My folders is part of it: listed once, as that folder.
        Assert.Equal([Documents, Pictures], Missing(new ContentSettings { Folders = [Documents + @"\Clients"] }, ThisPc()));
    }

    [Fact]
    public void Added_folders_are_listed_like_my_folders()
    {
        var content = new ContentSettings { Folders = [@"D:\Projects", @"\\nas\jobs", @"E:\"] };
        var names = new IndexSettings { SkipDrives = [@"D:\"] };
        Assert.Equal([@"D:\Projects", @"\\nas\jobs"], Missing(content, names));
        // A share is held only as a folder searched by name, then with everything below it.
        names.Folders = [@"\\nas\jobs"];
        Assert.Equal([@"D:\Projects"], Missing(content, names));
        Assert.Empty(Missing(new ContentSettings { UseMyFolders = false, Folders = [@"\\nas\jobs\2024"] }, names));
        // With My folders off, only the folders added count.
        Assert.Equal([@"D:\Projects"], Missing(new ContentSettings { UseMyFolders = false, Folders = [@"D:\Projects", Documents] }, new IndexSettings { SkipDrives = [@"D:\"] }));
        Assert.Equal([Documents], Missing(new ContentSettings { UseMyFolders = false, Folders = [Documents, Desktop] }, ThisPc()));
        Assert.Empty(Missing(new ContentSettings { UseMyFolders = false }, ThisPc()));
    }

    [Fact]
    public void OneDrive_is_listed_only_when_it_is_there()
    {
        Assert.Equal([Documents, Pictures], Missing(new ContentSettings(), ThisPc(), Mine));
        Assert.Equal([Documents, Pictures, OneDrive], Missing(new ContentSettings(), ThisPc(), [.. Mine, OneDrive]));
        // Folders moved into OneDrive (Windows' backup of Desktop, Documents and Pictures): OneDrive holds them.
        string[] moved = [OneDrive + @"\Desktop", OneDrive + @"\Documents", Downloads, OneDrive + @"\Pictures", OneDrive];
        Assert.Equal([OneDrive], Missing(new ContentSettings(), ThisPc(), moved));
    }

    [Fact]
    public void Folders_left_out_of_search_inside_are_not_listed()
    {
        Assert.Equal([Documents], Missing(new ContentSettings { ExcludeFolders = [Pictures] }, ThisPc()));
        Assert.Equal([Pictures], Missing(new ContentSettings { ExcludeFolders = [@"C:\Users\Me\Documents\"] }, ThisPc()));
        Assert.Empty(Missing(new ContentSettings { ExcludeFolders = [@"C:\Users"] }, ThisPc()));
    }

    [Fact]
    public void Adding_the_folders_listed_to_those_searched_by_name_holds_them()
    {
        var names = ThisPc();
        var missing = Missing(new ContentSettings { Folders = [@"\\nas\jobs"] }, names);
        Assert.Equal([Documents, Pictures, @"\\nas\jobs"], missing);
        names.Folders = [.. names.Folders, .. missing];
        Assert.Empty(Missing(new ContentSettings { Folders = [@"\\nas\jobs"] }, names));
    }

    [Fact]
    public void A_share_is_held_by_the_name_index_only_as_a_folder_searched_by_name()
    {
        Assert.False(ContentEstimator.NameIndexRules(@"\\nas\jobs", new IndexSettings(), []).Held);
        Assert.False(ContentEstimator.NameIndexRules(@"\\nas\jobs\2024", new IndexSettings(), []).Held);
        Assert.True(ContentEstimator.NameIndexRules(@"\\nas\jobs\2024", new IndexSettings { Folders = [@"\\nas\jobs"] }, []).Held);
        Assert.True(ContentEstimator.NameIndexRules(@"D:\Projects", new IndexSettings(), []).Held);
    }

    [Fact]
    public void Without_a_drive_list_whole_drives_follow_the_switches_of_their_kind()
    {
        // Setup's check: E: is a USB drive and Z: a network drive, as Windows says without touching them.
        static DriveKind Kind(string root) => char.ToUpperInvariant(root[0]) switch { 'E' => DriveKind.Removable, 'Z' => DriveKind.Network, _ => DriveKind.Fixed };
        static bool Held(string folder, IndexSettings names) => ContentEstimator.NameIndexRules(folder, names, [], Kind, _ => null).Held;

        Assert.True(Held(@"E:\Work", new IndexSettings()));
        Assert.False(Held(@"E:\Work", new IndexSettings { IndexRemovableDrives = false }));
        Assert.False(Held(@"E:\", new IndexSettings { IndexRemovableDrives = false }));
        Assert.False(Held(@"Z:\Docs", new IndexSettings { IndexNetworkDrives = false }));
        Assert.True(Held(@"D:\Projects", new IndexSettings { IndexRemovableDrives = false, IndexNetworkDrives = false }));
        // 1.4.0's list of drives keeps a fixed drive missing from it off; other kinds follow their own switches.
        var listed = new IndexSettings { Volumes = [@"C:\"] };
        Assert.True(Held(Documents, listed));
        Assert.False(Held(@"D:\Projects", listed));
        Assert.True(Held(@"E:\Work", listed));
        // In a folder searched by name, its drive's switches don't count: it is searched as a folder.
        Assert.True(Held(@"E:\Work\2024", new IndexSettings { IndexRemovableDrives = false, Folders = [@"E:\Work"] }));
    }

    [Fact]
    public void Without_a_drive_list_a_subst_drive_is_held_where_the_folder_it_shows_is()
    {
        // S: shows C:\Work and T: shows S:\Deep. Windows calls both fixed drives, as it does a SUBST of a fixed drive's folder.
        static string? Subst(string root) => char.ToUpperInvariant(root[0]) switch { 'S' => @"C:\Work", 'T' => @"S:\Deep", 'L' => @"L:\Loop", _ => null };
        static bool Held(string folder, IndexSettings names) => ContentEstimator.NameIndexRules(folder, names, [], _ => DriveKind.Fixed, Subst).Held;

        // 1.4.0's list of drives names C: only: S: isn't on it, but its files are searched under C:, as IndexPlan.Coverage finds.
        var listed = new IndexSettings { Volumes = [@"C:\"] };
        Assert.True(Held(@"S:\Proj", listed));
        Assert.True(Held(@"S:\", listed));
        Assert.True(Held(@"T:\Notes", listed)); // C:\Work\Deep\Notes
        Assert.False(Held(@"S:\Proj", new IndexSettings { Volumes = [@"D:\"] }));
        // The folder it shows decides: C: switched off, the folder or one above it left out, or searched by name.
        Assert.False(Held(@"S:\Proj", new IndexSettings { SkipDrives = [@"C:\"] }));
        Assert.False(Held(@"S:\Proj", new IndexSettings { ExcludeFolders = [@"C:\Work\Proj"] }));
        Assert.True(Held(@"S:\Proj", new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"C:\Work"] }));
        Assert.True(Held(@"S:\Proj", new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"S:\Proj"] }));
        // A SUBST that shows itself ends: the plan holds nothing on it.
        Assert.False(Held(@"L:\Proj", new IndexSettings()));
        // Setup lists it where the name index's rules see its paths: under the folder it shows, or as searched by name.
        static string Path(string folder, IndexSettings names) => ContentEstimator.NameIndexRules(folder, names, [], _ => DriveKind.Fixed, Subst).Path;
        Assert.Equal(@"C:\Work\Proj", Path(@"S:\Proj", listed));
        Assert.Equal(@"C:\Work", Path(@"S:\", listed));
        Assert.Equal(@"C:\Work\Deep\Notes", Path(@"T:\Notes", listed));
        Assert.Equal(@"S:\Proj", Path(@"S:\Proj", new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"S:\Proj"] }));
        Assert.Equal(@"D:\Projects", Path(@"D:\Projects", listed));
    }

    [Fact]
    public void With_a_drive_list_the_plans_own_answer_decides()
    {
        // Settings and the offer ask IndexPlan.Coverage, as the folder bar does: a hidden folder left out, a folder behind
        // a junction and a USB drive switched off aren't held, though setup's check (no drive list) holds the first two.
        List<DriveEntry> drives =
        [
            new(@"C:\", DriveKind.Fixed, "NTFS", "", true, null, null),
            new(@"E:\", DriveKind.Removable, "exFAT", "", true, null, null),
            new(@"G:\", DriveKind.Fixed, "", "", false, null, null), // locked
        ];
        var attributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Private"] = FileAttributes.Directory | FileAttributes.Hidden,
            [@"C:\Games"] = FileAttributes.Directory | FileAttributes.ReparsePoint,
        };
        var names = new IndexSettings { ExcludeHidden = true, IndexRemovableDrives = false };
        FolderCoverage Coverage(string folder) => IndexPlan.Coverage(names, drives, folder, attributesOf: p => attributes.TryGetValue(p, out var a) ? a : null);
        var content = new ContentSettings { UseMyFolders = false, Folders = [@"C:\Private", @"C:\Games\Saves", @"E:\Work", @"G:\Cards", @"C:\Work"] };

        var missing = ContentEstimator.FoldersNotSearchedByName(content, names, coverageOf: Coverage);
        Assert.Equal([@"C:\Private", @"C:\Games\Saves", @"E:\Work"], missing);
        Assert.Empty(ContentEstimator.FoldersNotSearchedByName(new ContentSettings { UseMyFolders = false, Folders = [@"C:\Private", @"C:\Games\Saves"] },
            new IndexSettings { ExcludeHidden = true }));
        // "Search them by name too" adds them to the folders searched by name, which holds them.
        names.Folders = [.. missing];
        Assert.Empty(ContentEstimator.FoldersNotSearchedByName(content, names, coverageOf: Coverage));
    }

    [Fact]
    public void A_drive_that_is_not_ready_holds_its_folders_once_it_is()
    {
        // Every switch lets it in: adding its folders to the folders searched by name would change nothing.
        Assert.True(ContentEstimator.HeldByName(new FolderCoverage(@"G:\Cards", CoverageReason.DriveNotReady, null, @"G:\Cards", null, @"G:\")));
        Assert.True(ContentEstimator.HeldByName(new FolderCoverage(@"C:\Work", CoverageReason.Covered, "C:", @"C:\Work", null, null)));
        Assert.True(ContentEstimator.HeldByName(null)); // the demo
        Assert.False(ContentEstimator.HeldByName(new FolderCoverage(@"E:\Work", CoverageReason.DriveKindOff, null, @"E:\Work", null, @"E:\")));
        Assert.False(ContentEstimator.HeldByName(new FolderCoverage(@"\\nas\jobs", CoverageReason.NotSearched, null, @"\\nas\jobs", null, null)));
    }

    [Fact]
    public void A_folder_added_on_a_drive_that_is_not_plugged_in_is_searched_by_name_too()
    {
        // E:\Photos typed while its USB disk is unplugged: the app takes a letter it doesn't know as a ready drive of no
        // kind, which every switch lets in, so the plan calls the folder held.
        List<DriveEntry> drives =
        [
            new(@"C:\", DriveKind.Fixed, "NTFS", "", true, null, null),
            new(@"E:\", DriveKind.Unknown, "", "", true, null, null),
        ];
        var names = new IndexSettings { IndexRemovableDrives = false };
        var coverage = IndexPlan.Coverage(names, drives, @"E:\Photos", attributesOf: _ => null, linkTargetOf: _ => null);
        Assert.True(coverage.Covered);
        static bool Known(string root) => string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase);
        // Plugged in, it may be a USB drive with USB drives off: added to the folders searched by name, it is searched anyway.
        Assert.False(ContentEstimator.HeldByName(@"E:\Photos", coverage, Known));
        // A drive the list or Windows knows goes by the plan; so does a share, and the demo (no answer).
        var onC = IndexPlan.Coverage(names, drives, @"C:\Work", attributesOf: _ => null, linkTargetOf: _ => null);
        Assert.True(ContentEstimator.HeldByName(@"C:\Work", onC, Known));
        Assert.False(ContentEstimator.HeldByName(@"\\nas\jobs", new FolderCoverage(@"\\nas\jobs", CoverageReason.NotSearched, null, @"\\nas\jobs", null, null), Known));
        Assert.True(ContentEstimator.HeldByName(@"E:\Photos", null, Known));
    }

    // ---- the words ---------------------------------------------------------------------------------

    [Fact]
    public void Folders_are_named_by_their_last_part_three_at_most()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("", ContentPresentation.FolderList([]));
        Assert.Equal("Documents", ContentPresentation.FolderList([Documents]));
        Assert.Equal("Documents and Pictures", ContentPresentation.FolderList([Documents, Pictures]));
        Assert.Equal("Documents, Pictures and OneDrive", ContentPresentation.FolderList([Documents, Pictures, OneDrive]));
        Assert.Equal("Desktop, Documents, Downloads and 1 more", ContentPresentation.FolderList([Desktop, Documents, Downloads, Pictures]));
        Assert.Equal("Desktop, Documents, Downloads and 2 more", ContentPresentation.FolderList([Desktop, Documents, Downloads, Pictures, OneDrive]));
        // Drives and shares by their whole paths; folders named alike too.
        Assert.Equal(@"E:\ and \\nas\jobs", ContentPresentation.FolderList([@"E:\", @"\\nas\jobs"]));
        Assert.Equal(@"Projects and 2024", ContentPresentation.FolderList([@"D:\Projects", @"\\nas\jobs\2024"]));
        Assert.Equal(@"C:\Work\Projects, D:\Projects and Documents", ContentPresentation.FolderList([@"C:\Work\Projects", @"D:\Projects", Documents]));
    }

    [Fact]
    public void Settings_note_and_its_button()
    {
        Assert.Equal("Documents and Pictures aren't searched by name, so their insides aren't read either.",
            ContentPresentation.NotSearchedByName(Missing(new ContentSettings(), ThisPc())));
        Assert.Equal("Pictures isn't searched by name, so its insides aren't read either.", ContentPresentation.NotSearchedByName([Pictures]));
        Assert.Equal("", ContentPresentation.NotSearchedByName([]));
        Assert.Equal("Search them by name too", ContentPresentation.SearchByNameButton(2));
        Assert.Equal("Search it by name too", ContentPresentation.SearchByNameButton(1));
        Assert.Equal("Documents and Pictures are added to the folders searched by name; their insides are read after you close Settings.",
            ContentPresentation.AddedToNames([Documents, Pictures]));
        Assert.Equal("Pictures is added to the folders searched by name; its insides are read after you close Settings.",
            ContentPresentation.AddedToNames([Pictures]));
        Assert.Equal("", ContentPresentation.AddedToNames([]));
    }

    [Fact]
    public void The_documents_choice_says_which_folders_are_left_out()
    {
        Assert.Equal(" (Documents and Pictures aren't searched by name, so they're left out; Settings › Indexing can add them.)",
            ContentPresentation.LeftOutClause([Documents, Pictures]));
        Assert.Equal(" (Pictures isn't searched by name, so it's left out; Settings › Indexing can add it.)",
            ContentPresentation.LeftOutClause([Pictures]));
        Assert.Equal(" (Documents and Pictures aren't searched by name, so they're left out; “Search them by name too” below adds them.)",
            ContentPresentation.LeftOutClause([Documents, Pictures], inSettings: true));
        Assert.Equal("", ContentPresentation.LeftOutClause([]));
        Assert.Equal(" (Documents and Pictures aren't counted until they're searched by name, after you close Settings.)",
            ContentPresentation.NotCountedYetClause([Documents, Pictures]));
        Assert.Equal("", ContentPresentation.NotCountedYetClause([]));
    }

    [Fact]
    public void Settings_mode_card_puts_the_clause_after_the_documents_choice()
    {
        using var _ = new CultureScope("en-US");
        var documents = new ContentEstimate(ContentMode.Documents, 5230, 0, 28_000_000, TimeSpan.FromMinutes(2.3), 0, false);
        var pictures = new ContentEstimate(ContentMode.DocumentsAndPictures, 5230, 9754, 30_000_000, TimeSpan.FromMinutes(3.1), 0, false);
        string text = ContentPresentation.ModeCardDescription(documents, pictures, 0, notByName: [Documents, Pictures]);
        Assert.Contains("Inside documents: about 28 MB for 5,230 documents, about 3 minutes. (Documents and Pictures aren't searched by name, "
            + "so they're left out; “Search them by name too” below adds them.) With pictures' details: about 30 MB in all", text);
        Assert.Contains("minutes. (Pictures isn't counted until it's searched by name, after you close Settings.) With pictures'",
            ContentPresentation.ModeCardDescription(documents, pictures, 0, notCountedYet: [Pictures]));
        Assert.Equal(ContentPresentation.ModeCardDescription(documents, pictures, 0), ContentPresentation.ModeCardDescription(documents, pictures, 0, notByName: [], notCountedYet: []));
        Assert.Contains("Inside documents: counting… (Documents isn't searched by name",
            ContentPresentation.ModeCardDescription(null, null, 0, notByName: [Documents]));
    }

    [Fact]
    public void Setup_line_and_offer_mention_the_folders_left_out()
    {
        using var _ = new CultureScope("en-US");
        var estimate = new ContentEstimate(ContentMode.Documents, 5230, 0, 28_000_000, TimeSpan.FromMinutes(2.3), 0, false);
        Assert.Equal("About 28 MB for 5,230 documents; the first pass takes about 3 minutes in the background. "
            + "(Documents and Pictures aren't searched by name, so they're left out; Settings › Indexing can add them.)",
            ContentPresentation.OptionDetail(ContentMode.Documents, estimate, new ContentSettings()) + ContentPresentation.LeftOutClause([Documents, Pictures]));
        Assert.Equal("Search inside your documents too? About 28 MB; the first pass takes about 3 minutes in the background. "
            + "Documents and Pictures would be left out (not searched by name).", ContentPresentation.Offer(estimate, [Documents, Pictures]));
        Assert.EndsWith("become searchable. Pictures would be left out (not searched by name).", ContentPresentation.Offer(null, [Pictures]));
        Assert.Equal(ContentPresentation.Offer(estimate), ContentPresentation.Offer(estimate, []));
        Assert.DoesNotContain("left out", ContentPresentation.Offer(estimate));
    }
}
