using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// <see cref="MatchDefaults"/> and <see cref="MatchChoices"/>: searches match plainly unless chosen otherwise; whole
/// word, saved on by every save up to 1.7.30, starts off once; a switch flipped in a window is the saved choice.
/// </summary>
public sealed class SearchMatchDefaultsTests
{
    static Func<string, string?> Ini(params string[] lines)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            v[line[..eq]] = line[(eq + 1)..];
        }
        return k => v.TryGetValue(k, out var x) ? x : null;
    }

    static Dictionary<string, string> Saved(MatchChoices c) => MatchDefaults.Save(c).ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void ANewInstallMatchesPlainly()
    {
        var c = MatchDefaults.Load(Ini());
        Assert.Equal(MatchChoices.Default, c);
        Assert.False(c.WholeWord);
        Assert.Equal("1", Saved(c)[MatchDefaults.ResetKey]); // its first save marks it, so nothing is reset later
    }

    [Fact]
    public void AnIniFromBeforeTurnsWholeWordOffOnce()
    {
        // Every save up to 1.7.30 wrote the old default; the other switches are left as they are.
        var c = MatchDefaults.Load(Ini("default_match_whole_word=1", "match_case=1", "match_path=0", "match_diacritics=1", "match_regex=0"));
        Assert.Equal(new MatchChoices(Case: true, WholeWord: false, Path: false, Diacritics: true, Regex: false, InsideFiles: true), c);
        var saved = Saved(c);
        Assert.Equal("0", saved["default_match_whole_word"]);
        Assert.Equal("1", saved[MatchDefaults.ResetKey]);
    }

    [Fact]
    public void AfterTheResetWholeWordStaysAsChosen()
    {
        var on = MatchDefaults.Load(Ini("default_match_whole_word=1", "match_defaults_reset=1"));
        Assert.True(on.WholeWord);
        var off = MatchDefaults.Load(Ini("default_match_whole_word=0", "match_defaults_reset=1"));
        Assert.False(off.WholeWord);
    }

    [Fact]
    public void WhatIsSavedLoadsBackTheSame()
    {
        foreach (var c in new[] { MatchChoices.Default, new MatchChoices(true, true, true, true, true, true), new MatchChoices(false, true, false, true, false, true) })
        {
            var saved = Saved(c);
            Assert.Equal(c, MatchDefaults.Load(k => saved.TryGetValue(k, out var x) ? x : null));
        }
    }

    [Fact]
    public void MatchInsideFilesIsKeptApart()
    {
        // Its key belongs to search inside files' settings: loaded from there, never written here.
        Assert.False(MatchDefaults.Load(Ini(), insideFiles: false).InsideFiles);
        Assert.DoesNotContain("match_content", Saved(MatchChoices.Default).Keys);
    }

    [Theory]
    [InlineData(MatchSwitch.Case)]
    [InlineData(MatchSwitch.WholeWord)]
    [InlineData(MatchSwitch.Path)]
    [InlineData(MatchSwitch.Diacritics)]
    [InlineData(MatchSwitch.Regex)]
    [InlineData(MatchSwitch.InsideFiles)]
    public void AFlippedSwitchIsStoredAloneAndKeptAtTheNextStart(MatchSwitch which)
    {
        var before = new MatchChoices(Case: false, WholeWord: true, Path: false, Diacritics: true, Regex: false, InsideFiles: true);
        var after = before.With(which, !before[which]);
        Assert.Equal(!before[which], after[which]);
        foreach (var other in Enum.GetValues<MatchSwitch>().Where(s => s != which))
            Assert.Equal(before[other], after[other]); // the other switches keep what was saved
        if (which == MatchSwitch.InsideFiles) return; // saved with search inside files' settings
        var saved = Saved(after);
        Assert.Equal(after with { InsideFiles = true }, MatchDefaults.Load(k => saved.TryGetValue(k, out var x) ? x : null));
    }

    [Fact]
    public void TurningWholeWordOnInAWindowSurvivesTheNextStart()
    {
        // A user upgrading: whole word goes off once; they turn it back on; the next start keeps it on.
        var first = MatchDefaults.Load(Ini("default_match_whole_word=1"));
        Assert.False(first.WholeWord);
        var saved = Saved(first.With(MatchSwitch.WholeWord, true));
        Assert.True(MatchDefaults.Load(k => saved.TryGetValue(k, out var x) ? x : null).WholeWord);
    }

    [Fact]
    public void ANewTabStartsWithTheSavedSwitches()
    {
        // Match inside files too: the saved one, not the tab used before.
        var saved = new MatchChoices(Case: true, WholeWord: false, Path: true, Diacritics: false, Regex: false, InsideFiles: false);
        Assert.Equal(saved, MatchChoices.ForNewTab(saved, null));
    }

    [Fact]
    public void ADuplicatedTabKeepsItsOwnSwitches()
    {
        var saved = MatchChoices.Default;
        var tab = new MatchChoices(Case: true, WholeWord: true, Path: false, Diacritics: true, Regex: true, InsideFiles: false);
        Assert.Equal(tab, MatchChoices.ForNewTab(saved, tab));
        foreach (var which in Enum.GetValues<MatchSwitch>())
            Assert.Equal(tab[which], MatchChoices.ForNewTab(saved, tab)[which]);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    public void SwitchesReadAsTheIniAlwaysDid(string value, bool on) =>
        Assert.Equal(on, MatchDefaults.Load(Ini($"match_case={value}")).Case);
}
