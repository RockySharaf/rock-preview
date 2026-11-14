using Anything.Core;

namespace Anything.Tests;

/// <summary>The address bar's commands: "cmd" and the like run in the open folder, as in File Explorer.</summary>
public sealed class AddressCommandTests
{
    const string Folder = @"C:\Users\ROG\Desktop\Reference";

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
}
