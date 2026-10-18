using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

public class SearchEngineTests : SearchTestBase
{
    const string Song = @"C:\Music\song.mp3", SongTwo = @"C:\Music\Song Two.MP3", Track = @"C:\Music\track.wav", Cafe = @"C:\Music\Café.flac";
    const string Report = @"C:\Docs\report.docx", Notes = @"C:\Docs\notes.txt", OldZip = @"C:\Docs\Archive\old.zip", Notes2 = @"C:\Docs\Archive\notes.txt";
    const string App = @"C:\Program Files\App\app.exe", Readme = @"C:\Program Files\App\readme";
    const string FooBar_ = @"D:\foo_bar.txt", FooBar = @"D:\foobar.txt", BarFoo = @"D:\bar foo.txt", Its = @"D:\it's.txt", Clip = @"D:\clip.mkv", Image = @"D:\image.PNG";

    // ---------------- basics ----------------

    [Fact]
    public void Empty_query_returns_all_live_entries()
    {
        Assert.Equal(Tree.Index.LiveCount, Run("").Ids.Length);
        Tree.Index.Remove(Tree.Id(@"C:\Docs\Archive"));
        var r = Find("");
        Assert.Equal(Tree.Index.LiveCount, r.Length);
        Assert.DoesNotContain(OldZip, r);
        Assert.DoesNotContain(@"C:\Docs\Archive", r);
    }

    [Fact]
    public void Plain_terms_are_case_insensitive_substrings_of_the_name()
    {
        AssertFinds("song", Song, SongTwo);
        AssertFinds("SONG", Song, SongTwo);
        AssertFinds("music", @"C:\Music");
        AssertFinds("zzz");
    }

    [Fact]
    public void And_or_not_grouping_quotes()
    {
        AssertFinds("song mp3", Song, SongTwo);
        AssertFinds("song | track", Song, SongTwo, Track);
        AssertFinds("song !two", Song);
        AssertFinds("<song | track> !wav", Song, SongTwo);
        AssertFinds("\"bar foo\"", BarFoo);
        AssertFinds("bar foo", FooBar_, FooBar, BarFoo);
        // OR binds tighter than AND: (foo | song) two
        AssertFinds("foo | song two", SongTwo);
        AssertFinds("!<a | e | o | u> file:", Clip, Its);
        AssertFinds("\"song | track\"");
    }

    [Fact]
    public void Wildcards_match_the_whole_name()
    {
        AssertFinds("*.mp3", Song, SongTwo);
        AssertFinds("*.mp");
        AssertFinds("s*.mp3", Song, SongTwo);
        AssertFinds("so?g.*", Song);
        AssertFinds("t*", Track);
        AssertFinds("*a*e*", App, Cafe, Image, @"C:\Docs\Archive", @"C:\Program Files", Readme);
        AssertFinds("nowildcards:*.mp3");
        AssertFinds(@"C:\Music\*.wav", Track);
        AssertFinds(@"*\archive\*", OldZip, Notes2);
    }

    [Fact]
    public void Backslash_switches_to_full_path_matching()
    {
        AssertFinds(@"music\", Song, SongTwo, Track, Cafe);
        AssertFinds(@"\docs\archive", @"C:\Docs\Archive", OldZip, Notes2);
        AssertFinds("path:archive", @"C:\Docs\Archive", OldZip, Notes2);
        AssertFinds(@"nopath:music\");
        AssertFinds("music", new SearchOptions { MatchPath = true }, @"C:\Music", Song, SongTwo, Track, Cafe);
        AssertFinds("nopath:music", new SearchOptions { MatchPath = true }, @"C:\Music");
        AssertFinds(@"C:\", AllFolders.Concat(new[] { Song, SongTwo, Track, Cafe, Report, Notes, OldZip, Notes2, App, Readme }).Where(p => p.StartsWith(@"C:")).ToArray());
    }

    [Fact]
    public void Case_modifiers_and_option()
    {
        AssertFinds("case:Song", SongTwo);
        AssertFinds("case:song", Song);
        AssertFinds("song", new SearchOptions { MatchCase = true }, Song);
        AssertFinds("nocase:song", new SearchOptions { MatchCase = true }, Song, SongTwo);
        AssertFinds("case:<Song | track>", SongTwo, Track);
    }

    [Fact]
    public void Diacritics_are_ignored_unless_requested()
    {
        AssertFinds("cafe", Cafe);
        AssertFinds("CAFÉ", Cafe);
        AssertFinds("diacritics:cafe");
        AssertFinds("diacritics:café", Cafe);
        AssertFinds("cafe", new SearchOptions { MatchDiacritics = true });
        AssertFinds("nodiacritics:cafe", new SearchOptions { MatchDiacritics = true }, Cafe);
        AssertFinds("caf?.flac", Cafe);
    }

    [Fact]
    public void Whole_word()
    {
        AssertFinds("ww:foo", FooBar_, BarFoo);
        AssertFinds("wholeword:bar", FooBar_, BarFoo);
        AssertFinds("foo", new SearchOptions { MatchWholeWord = true }, FooBar_, BarFoo);
        AssertFinds("nowholeword:foo", new SearchOptions { MatchWholeWord = true }, FooBar_, FooBar, BarFoo);
    }

    [Fact]
    public void Regex_terms_and_errors()
    {
        AssertFinds("regex:^song", Song, SongTwo);
        AssertFinds("case:regex:^Song", SongTwo);
        AssertFinds(@"regex:\.(wav|flac)$", Track, Cafe);
        AssertFinds("<regex:^tr> wav", Track);
        AssertFinds(@"path:regex:\\music\\s", Song, SongTwo);
        AssertFinds(@"^s.*\.mp3$", new SearchOptions { Regex = true }, Song, SongTwo);
        AssertFinds("noregex:song", new SearchOptions { Regex = false }, Song, SongTwo);

        var bad = Run("regex:[abc");
        Assert.NotNull(bad.Error);
        Assert.Contains("regular expression", bad.Error);
        Assert.Empty(bad.Ids);
        Assert.NotNull(Run("(unclosed", new SearchOptions { Regex = true }).Error);
    }

    [Fact]
    public void File_and_folder_restrictions()
    {
        AssertFinds("folder:", AllFolders);
        AssertFinds("folders:<doc | mus>", @"C:\Docs", @"C:\Music");
        AssertFinds("file: notes", Notes, Notes2);
        AssertFinds("folder:a", @"C:\Docs\Archive", @"C:\Program Files", @"C:\Program Files\App");
        Assert.Equal(Tree.Index.LiveCount - AllFolders.Length, Find("files:").Length);
        AssertFinds("!file: !folder:");
    }

    // ---------------- functions ----------------

    [Fact]
    public void Ext_function()
    {
        AssertFinds("ext:mp3;wav", Song, SongTwo, Track);
        AssertFinds("ext:MP3", Song, SongTwo);
        AssertFinds("ext:.png", Image);
        AssertFinds("ext:fl*", Cafe);
        AssertFinds("ext: file:", Readme);
        AssertFinds("extension:zip", OldZip);
    }

    [Fact]
    public void Size_function_comparisons_ranges_constants()
    {
        AssertFinds("size:>1mb", Song, Cafe, OldZip);
        AssertFinds("size:>=1mb", Song, Cafe, OldZip, App);
        AssertFinds("size:<=3kb", SongTwo, Notes, Notes2, Readme, FooBar_, FooBar, BarFoo, Its, Clip, Image);
        AssertFinds("size:<1kb file: !parent:D:", Notes, Notes2, Readme);
        AssertFinds("size:1mb..10mb", App, Song);
        AssertFinds("size:1mb", App);
        AssertFinds("size:=5mb", Song);
        AssertFinds("size:!=1 parent:D:");
        AssertFinds("size:1kb-4kb", SongTwo);
        AssertFinds("size:1.5kb..3072", SongTwo);
        AssertFinds("size:0.5gb..");
        AssertFinds("size:..100 file: !parent:D:", Notes, Notes2, Readme);
        AssertFinds("size:empty", Notes);
        AssertFinds("size:tiny", SongTwo, Notes2, Readme, FooBar_, FooBar, BarFoo, Its, Clip, Image);
        AssertFinds("size:small", Track);
        AssertFinds("size:medium", Report, App);
        AssertFinds("size:large", Song);
        AssertFinds("size:huge", Cafe);
        AssertFinds("size:gigantic", OldZip);
        AssertFinds("size:unknown", AllFolders);
        AssertFinds("size:empty;gigantic", Notes, OldZip);
        Assert.NotNull(Run("size:abc").Error);
        Assert.Equal(Tree.Index.LiveCount, Find("size:").Length);
    }

    [Fact]
    public void Date_absolute_formats()
    {
        AssertFinds("dm:2024-05-15", Song);
        AssertFinds("dm:15/05/2024", Song);
        AssertFinds("dm:05/15/2024", Song);
        AssertFinds("dm:2024/05/15", Song);
        AssertFinds("dm:2024-05", Song, SongTwo, Track, App);
        AssertFinds("dm:2024", Song, SongTwo, Track, Cafe, Notes, App);
        AssertFinds("dm:2020", OldZip);
        AssertFinds("dm:\"2024-05-15 10\"", Song);
        AssertFinds("dm:2024-05-15T10:00", Song);
        AssertFinds("dm:2024-05-15T10:01");
        AssertFinds("datemodified:2023-06-01", Report);
        AssertFinds("dc:2024-01-01", Song, SongTwo);
        AssertFinds("datecreated:2023", Track, Cafe);
        Assert.NotNull(Run("dm:2024-13-01").Error);
        Assert.NotNull(Run("dm:notadate").Error);
    }

    [Fact]
    public void Date_comparisons_and_ranges_use_periods()
    {
        AssertFinds("dm:>2024-05-14", Song, App);
        AssertFinds("dm:>=2024-05-14", Song, SongTwo, App);
        AssertFinds("dm:<2024", Report, OldZip, Readme);
        AssertFinds("dm:<=2023", Report, OldZip, Readme);
        AssertFinds("dm:2024-04..2024-05", Cafe, Song, SongTwo, Track, App);
        AssertFinds("dm:2022..2023", Report, Readme);
        AssertFinds("dm:2024-05-14;2020", SongTwo, OldZip);
    }

    [Fact]
    public void Date_keywords()
    {
        AssertFinds("dm:today", Song);
        AssertFinds("dm:yesterday", SongTwo);
        AssertFinds("dm:tomorrow", App);
        AssertFinds("dm:thisweek", Song, SongTwo, App);
        AssertFinds("dm:lastweek", Track);
        AssertFinds("dm:nextweek");
        AssertFinds("dm:thismonth", Song, SongTwo, Track, App);
        AssertFinds("dm:lastmonth", Cafe);
        AssertFinds("dm:thisyear", Song, SongTwo, Track, Cafe, Notes, App);
        AssertFinds("dm:lastyear", Report);
        AssertFinds("dm:pastweek", Song, SongTwo, Track);
        AssertFinds("dm:pastmonth", Song, SongTwo, Track, Cafe);
        AssertFinds("dm:pastyear", Song, SongTwo, Track, Cafe, Notes, Report);
        AssertFinds("dm:last2weeks", Song, SongTwo, Track);
        AssertFinds("dm:past3days", Song, SongTwo);
        AssertFinds("dm:last3hours", Song);
        AssertFinds("dm:last10mins");
        AssertFinds("dm:next1day", App);
        AssertFinds("dm:may", Song, SongTwo, Track, App);
        AssertFinds("dm:april", Cafe);
        AssertFinds("dm:jan", Notes);
        AssertFinds("dm:>=yesterday", Song, SongTwo, App);
        AssertFinds("dm:unknown file:", Notes2, FooBar_, FooBar, BarFoo, Its, Clip, Image);
        AssertFinds("dc:thisyear", Song, SongTwo);
    }

    [Fact]
    public void Attributes()
    {
        AssertFinds("attrib:H", Notes);
        AssertFinds("attrib:R", OldZip);
        AssertFinds("attributes:S", App);
        AssertFinds("attrib:D", AllFolders);
        AssertFinds("attrib:HA", Notes);
        AssertFinds("attrib:RH");
        AssertFinds("!attrib:A", AllFolders);
        Assert.NotNull(Run("attrib:Q").Error);
    }

    [Fact]
    public void Parent_functions()
    {
        AssertFinds(@"parent:C:\Music", Song, SongTwo, Track, Cafe);
        AssertFinds(@"parent:c:\music\", Song, SongTwo, Track, Cafe);
        AssertFinds("infolder:\"C:\\Program Files\\App\"", App, Readme);
        AssertFinds(@"nosubfolders:C:\Docs", Report, Notes, @"C:\Docs\Archive");
        AssertFinds(@"parent:D:", DFiles.Append(@"D:\R&D").ToArray());
        AssertFinds(@"parent:D:\ file:", DFiles);
        AssertFinds(@"parent:C:\Nope");
    }

    [Fact]
    public void Name_shape_functions()
    {
        AssertFinds("startwith:song", Song, SongTwo);
        AssertFinds("case:startwith:Song", SongTwo);
        AssertFinds("endwith:.txt", Notes, Notes2, FooBar_, FooBar, BarFoo, Its);
        AssertFinds("endwith:o.txt", BarFoo);
        AssertFinds("len:7 file:", App, OldZip);
        AssertFinds("len:<=3", "C:", "D:", @"C:\Program Files\App", @"D:\R&D");
        AssertFinds("len:11..12", SongTwo, Report, FooBar_, BarFoo);
    }

    [Fact]
    public void Depth_and_root()
    {
        AssertFinds("depth:0", "C:", "D:");
        AssertFinds("depth:3", OldZip, Notes2, App, Readme);
        AssertFinds("parents:3", OldZip, Notes2, App, Readme);
        AssertFinds("depth:>=3 ext:txt", Notes2);
        var rootLevel = new[] { @"C:\Music", @"C:\Docs", @"C:\Empty", @"C:\Program Files", @"D:\R&D" }.Concat(DFiles).ToArray();
        AssertFinds("depth:1", rootLevel);
        AssertFinds("root:", rootLevel);
    }

    [Fact]
    public void Children_functions()
    {
        AssertFinds("empty:", @"C:\Empty", @"D:\R&D");
        AssertFinds("child:song", @"C:\Music");
        AssertFinds("child:*.exe", @"C:\Program Files\App");
        AssertFinds("child:app", @"C:\Program Files", @"C:\Program Files\App");
        AssertFinds("child:", AllFolders.Except([@"C:\Empty", @"D:\R&D"]).ToArray());
        AssertFinds("childcount:0", @"C:\Empty", @"D:\R&D");
        AssertFinds("childcount:4", "C:", @"C:\Music");
        AssertFinds("childcount:>=5", "D:");
        AssertFinds("childfilecount:2", @"C:\Docs", @"C:\Docs\Archive", @"C:\Program Files\App");
        AssertFinds("childfoldercount:1", @"C:\Docs", @"C:\Program Files", "D:");
        Tree.Index.Remove(Tree.Id(@"C:\Docs\Archive\old.zip"));
        Tree.Index.Remove(Tree.Id(@"C:\Docs\Archive\notes.txt"));
        AssertFinds("empty:", @"C:\Empty", @"D:\R&D", @"C:\Docs\Archive");
    }

    [Fact]
    public void Dupe_post_filter()
    {
        AssertFinds("dupe:", Notes, Notes2);
        AssertFinds("dupe: ext:txt", Notes, Notes2);
        AssertFinds("dupe: notes !archive\\");
        Tree.File(@"D:\SONG.MP3", 1);
        AssertFinds("dupe: *.mp3", Song, @"D:\SONG.MP3");
    }

    [Fact]
    public void Type_function()
    {
        AssertFinds("type:\"file folder\"", AllFolders);
        AssertFinds("type:\"Text Document\"", Notes, Notes2, FooBar_, FooBar, BarFoo, Its);
        AssertFinds("type:\"mp3 file\"", Song, SongTwo);
        AssertFinds("type:\"MKV File\"", Clip);
        AssertFinds("type:application", App);
    }

    [Fact]
    public void Macros()
    {
        AssertFinds("audio:", Song, SongTwo, Track, Cafe);
        AssertFinds("audio:song", Song, SongTwo);
        AssertFinds("zip:", OldZip);
        AssertFinds("doc:", Report, Notes, Notes2, FooBar_, FooBar, BarFoo, Its);
        AssertFinds("exe:", App);
        AssertFinds("pic:", Image);
        AssertFinds("video:", Clip);
        AssertFinds("!audio: path:music", @"C:\Music");
        AssertFinds("apos:", Its);
        AssertFinds("amp:", @"D:\R&D");
        AssertFinds("quot:");
        AssertFinds("lt: | gt:");
    }

    [Fact]
    public void Filter_is_anded_with_the_query()
    {
        AssertFinds("song", new SearchOptions { Filter = "ext:mp3" }, Song, SongTwo);
        AssertFinds("t", new SearchOptions { Filter = "audio:" }, SongTwo, Track);
        AssertFinds("", new SearchOptions { Filter = "folder:" }, AllFolders);
        AssertFinds("^s", new SearchOptions { Regex = true, Filter = "ext:mp3" }, Song, SongTwo);
        Assert.NotNull(Run("x", new SearchOptions { Filter = "size:bogus" }).Error);
    }

    [Fact]
    public void Results_report_elapsed()
    {
        var r = Run("song");
        Assert.True(r.Elapsed > TimeSpan.Zero);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Engine.Search(Tree.Index, "song", new SearchOptions(), SortKey.Name, false, cts.Token));
    }

    [Fact]
    public async Task Search_holds_read_lock_without_deadlocking_writers()
    {
        var t = Task.Run(() => { for (int i = 0; i < 50; i++) Tree.Index.Add("x" + i, Tree.Id("D:"), FileAttributes.Archive, 1, 0, 0); });
        for (int i = 0; i < 50; i++) Run("x");
        await t;
        Assert.Equal(50, Find("startwith:x").Length);
    }
}
