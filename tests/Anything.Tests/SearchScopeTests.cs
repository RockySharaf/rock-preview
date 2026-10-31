using Anything.Core;
using static Anything.Core.SearchScope;

namespace Anything.Tests;

/// <summary>The search box's scope button: whenever it names a folder, a click switches to everything, and back.</summary>
public sealed class SearchScopeTests
{
    [Fact]
    public void TheOpenFolderSwitchesToEverything()
    {
        var b = Describe(everywhere: false, selected: null, folder: "Downloads", zap: null);
        Assert.Equal(("Downloads", false, Click.ToEverything), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching only in Downloads. Click to search everything", b.Tip);
    }

    [Fact]
    public void ASelectedFolderSwitchesToEverythingToo()
    {
        // Back or Up selects the folder you came from: its name showed, and a click did nothing.
        var b = Describe(everywhere: false, selected: "images", folder: "Pictures", zap: null);
        Assert.Equal(("images", false, Click.ToEverything), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching only in the selected images. Click to search everything", b.Tip);
    }

    [Fact]
    public void EverythingGoesBackToTheOpenFolder()
    {
        var b = Describe(everywhere: true, selected: null, folder: "Downloads", zap: null);
        Assert.Equal(("Everything", true, Click.ToFolder), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching everything. Click to search only in Downloads", b.Tip);
    }

    [Fact]
    public void EverythingGoesBackToTheSelectedFolders()
    {
        var b = Describe(everywhere: true, selected: "3 folders", folder: "Projects", zap: null);
        Assert.Equal(("Everything", true, Click.ToFolder), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching everything. Click to search only in the selected 3 folders", b.Tip);
    }

    [Fact]
    public void EveryFileInAFolderSwitchesToEverything()
    {
        var b = Describe(everywhere: false, selected: null, folder: "Downloads", zap: "Downloads");
        Assert.Equal(("Downloads", false, Click.ToEverything), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching the files in Downloads. Click to search everything", b.Tip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoFolderThereIsNothingToSwitchTo(bool everywhere)
    {
        var b = Describe(everywhere, selected: null, folder: null, zap: null);
        Assert.Equal(("Everything", true, Click.None), (b.Text, b.Everything, b.Click));
        Assert.Equal("Searching everything", b.Tip);
    }

    [Fact]
    public void ClickingTwiceComesBack()
    {
        foreach (var (selected, folder) in new[] { ((string?)null, "Downloads"), ("images", "Pictures"), ("images", (string?)null) })
        {
            var scoped = Describe(everywhere: false, selected, folder, zap: null);
            Assert.Equal(Click.ToEverything, scoped.Click);
            var everything = Describe(everywhere: true, selected, folder, zap: null);
            Assert.Equal(Click.ToFolder, everything.Click);
            Assert.Equal(selected ?? folder, Describe(everywhere: false, selected, folder, zap: null).Text);
        }
    }
}
