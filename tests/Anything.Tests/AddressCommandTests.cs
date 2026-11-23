using Anything.Core;

namespace Anything.Tests;

/// <summary>The address bar's commands: "cmd" and the like run in the open folder, as in File Explorer.</summary>
public sealed class AddressCommandTests
{
    const string Folder = @"C:\Users\Sam\Desktop\Reference";

    [Theory]
    [InlineData("cmd", "cmd", "")]
    [InlineData("  powershell  ", "powershell", "")]
    [InlineData("notepad notes.txt", "notepad", "notes.txt")]
    [InlineData("code .", "code", ".")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --new  window", @"C:\Program Files\App\app.exe", "--new  window")]
    [InlineData("\"C:\\Tools\\x.exe", @"C:\Tools\x.exe", "")]
    public void The_first_word_or_quoted_path_is_the_program(string typed, string file, string args)
    {
        var run = AddressCommand.Parse(typed, Folder);
        Assert.NotNull(run);
        Assert.Equal(file, run.Value.File);
        Assert.Equal(args, run.Value.Arguments);
        Assert.Equal(Folder, run.Value.WorkingFolder);
    }

    [Fact]
    public void Nothing_typed_is_nothing_to_run()
    {
        Assert.Null(AddressCommand.Parse("   ", Folder));
        Assert.Null(AddressCommand.Parse("\"\"", Folder));
    }

    [Fact]
    public void Without_a_folder_open_it_starts_where_Windows_starts_it()
        => Assert.Null(AddressCommand.Parse("cmd", null)!.Value.WorkingFolder);

    [Theory]
    [InlineData(Folder, "-d \"" + Folder + "\"")]
    [InlineData(@"C:\", "-d \"C:\\\\\"")] // a drive's root: the backslash before the quote is doubled, or it hides the quote
    public void Windows_Terminal_is_told_the_folder(string folder, string args)
    {
        Assert.Equal(args, AddressCommand.Parse("wt", folder)!.Value.Arguments);
        Assert.Equal(args, AddressCommand.Parse("WT.exe", folder)!.Value.Arguments);
        Assert.Equal("-p PowerShell", AddressCommand.Parse("wt -p PowerShell", folder)!.Value.Arguments); // its own arguments stay
    }

    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("http://localhost:5000/x", true)]
    [InlineData("www.example.com", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("cmd", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData("shell:Downloads", false)]
    [InlineData("www.example.com something", false)]
    public void Web_addresses_go_to_the_browser(string typed, bool web) => Assert.Equal(web, AddressCommand.IsWebAddress(typed));

    [Theory]
    [InlineData(@"This PC\Downloads", new[] { "This PC", "Downloads" })]
    [InlineData(@"  This PC\Documents\  ", new[] { "This PC", "Documents" })]
    [InlineData("Downloads", new[] { "Downloads" })]
    [InlineData(@"This PC\Local Disk (C:)\Users", new[] { "This PC", "Local Disk (C:)", "Users" })]
    [InlineData(@"""This PC\Music""", new[] { "This PC", "Music" })]
    public void A_path_of_display_names_is_split_into_its_names(string typed, string[] parts)
        => Assert.Equal(parts, AddressCommand.DisplayNameParts(typed));

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\Users")]
    [InlineData("C:")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\Windows")]
    [InlineData("shell:Downloads")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("https://github.com")]
    [InlineData(@"This PC\\Downloads")]
    [InlineData(@"This PC\..\Downloads")]
    [InlineData("a/b")]
    [InlineData("a|b")]
    public void Paths_and_names_that_are_not_display_names_are_not_split(string typed)
        => Assert.Null(AddressCommand.DisplayNameParts(typed));

    static readonly string[] PlaceNames = ["Desktop", "Home", "Documents", "Downloads", "Dokumente"];

    [Theory]
    [InlineData(@"This PC\Downloads")]
    [InlineData(@"this pc\Local Disk (C:)\Users")]
    [InlineData("This PC")]
    [InlineData("Downloads")]
    [InlineData(@"documents\Reports")]
    [InlineData(@"Dokumente\Berichte")] // a folder's name in the user's language
    [InlineData(@"Desktop\Notes")]
    public void Text_beginning_with_a_place_name_is_looked_for(string typed)
        => Assert.True(AddressCommand.WorthLookingFor(AddressCommand.DisplayNameParts(typed)!, PlaceNames));

    [Theory]
    [InlineData("notepda")]
    [InlineData(@"tools\x.exe")]
    [InlineData("ms-settings:display")]
    [InlineData(@"Network\server")]
    [InlineData("This PCs")]
    [InlineData(@"Downloadsx\a")]
    public void Other_text_is_answered_at_once_without_looking(string typed)
        => Assert.False(AddressCommand.WorthLookingFor(AddressCommand.DisplayNameParts(typed)!, PlaceNames));

    [Fact]
    public void Nothing_is_looked_for_without_a_name()
        => Assert.False(AddressCommand.WorthLookingFor([], PlaceNames));

    [Fact]
    public void Only_the_latest_lookup_answers()
    {
        var lookups = new AddressCommand.Lookups();
        int first = lookups.Begin();
        Assert.True(lookups.IsCurrent(first));
        int second = lookups.Begin(); // another address typed while the first was looked for
        Assert.False(lookups.IsCurrent(first));
        Assert.True(lookups.IsCurrent(second));
        lookups.Drop(); // another folder opened meanwhile
        Assert.False(lookups.IsCurrent(second));
    }

    [Fact]
    public void A_program_is_found_by_its_name_in_the_folders_given_with_or_without_its_extension()
    {
        string root = Path.Combine(Path.GetTempPath(), "rp-findsprogram-" + Guid.NewGuid().ToString("N"));
        string tools = Path.Combine(root, "tools"), other = Path.Combine(root, "other");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(tools, "Downloads.exe")); // a folder, not a program
        try
        {
            File.WriteAllText(Path.Combine(tools, "editor.exe"), "");
            File.WriteAllText(Path.Combine(tools, "build.CMD"), "");
            string[] folders = ["", @"relative\folder", "\"" + other + "\"", tools];
            const string pathExt = ".COM;.EXE; .BAT ;.CMD;;CMD";
            Assert.True(AddressCommand.FindsProgram("editor", folders, pathExt));
            Assert.True(AddressCommand.FindsProgram("EDITOR.exe", folders, pathExt));
            Assert.True(AddressCommand.FindsProgram("build", folders, pathExt));
            Assert.False(AddressCommand.FindsProgram("editor", folders, ".BAT")); // .exe isn't one of the extensions
            Assert.False(AddressCommand.FindsProgram("Downloads", folders, pathExt));
            Assert.False(AddressCommand.FindsProgram("missing", folders, pathExt));
            Assert.False(AddressCommand.FindsProgram("editor", [other], pathExt));
            Assert.False(AddressCommand.FindsProgram(@"tools\editor", [root], pathExt)); // a bare name only
            Assert.False(AddressCommand.FindsProgram("", folders, pathExt));
            Assert.False(AddressCommand.FindsProgram("editor", folders, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
