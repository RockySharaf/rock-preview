namespace Anything.Core;

/// <summary>The switches of how search words match: Search › Match case and the rest.</summary>
public enum MatchSwitch { Case, WholeWord, Path, Diacritics, Regex, InsideFiles }

/// <summary>
/// The saved choice of each matching switch: what a new tab, a new window and the next start begin with. Settings ›
/// Defaults changes it, and so does flipping a switch in a window (the window's own tab follows at once, other open tabs
/// keep what they have).
/// </summary>
public readonly record struct MatchChoices(bool Case, bool WholeWord, bool Path, bool Diacritics, bool Regex, bool InsideFiles)
{
    /// <summary>Plain matching, as a new install starts: only Match inside files on (it does nothing until search inside files is on).</summary>
    public static MatchChoices Default => new(false, false, false, false, false, true);

    public bool this[MatchSwitch s] => s switch
    {
        MatchSwitch.Case => Case,
        MatchSwitch.WholeWord => WholeWord,
        MatchSwitch.Path => Path,
        MatchSwitch.Diacritics => Diacritics,
        MatchSwitch.Regex => Regex,
        _ => InsideFiles,
    };

    /// <summary>
    /// The switches a new tab starts with: the saved ones, as a new window and the next start begin; a duplicated tab
    /// (<paramref name="duplicated"/> given, the tab's own switches) keeps that tab's.
    /// </summary>
    public static MatchChoices ForNewTab(MatchChoices saved, MatchChoices? duplicated) => duplicated ?? saved;

    /// <summary>The same choices with <paramref name="s"/> set to <paramref name="on"/>: a switch flipped in a window is stored so.</summary>
    public MatchChoices With(MatchSwitch s, bool on) => s switch
    {
        MatchSwitch.Case => this with { Case = on },
        MatchSwitch.WholeWord => this with { WholeWord = on },
        MatchSwitch.Path => this with { Path = on },
        MatchSwitch.Diacritics => this with { Diacritics = on },
        MatchSwitch.Regex => this with { Regex = on },
        _ => this with { InsideFiles = on },
    };
}

/// <summary>
/// How the saved matching choices are read from and written to RockPreview.ini (match_case, default_match_whole_word,
/// match_path, match_diacritics, match_regex). Match inside files has its own key among search inside files' settings.
/// </summary>
public static class MatchDefaults
{
    /// <summary>
    /// Written at every save. Up to 1.7.30 every save wrote default_match_whole_word=1, the old default, whether or not
    /// it was chosen: an ini without this key has that leftover, so whole word starts off once, whatever it says.
    /// </summary>
    public const string ResetKey = "match_defaults_reset";

    /// <summary>The choices an ini holds (<paramref name="value"/> gives a key's text, null when missing); Match inside files stays as <paramref name="insideFiles"/>.</summary>
    public static MatchChoices Load(Func<string, string?> value, bool insideFiles = true)
    {
        bool Bool(string k, bool def) => value(k) is { } x ? x.Trim() is "1" or "true" or "True" : def;
        bool reset = value(ResetKey) is not null;
        return new MatchChoices(
            Bool("match_case", false),
            reset && Bool("default_match_whole_word", false),
            Bool("match_path", false),
            Bool("match_diacritics", false),
            Bool("match_regex", false),
            insideFiles);
    }

    /// <summary>The keys and values a save writes for <paramref name="c"/>, the reset key with them so it never happens again.</summary>
    public static IEnumerable<(string Key, string Value)> Save(MatchChoices c)
    {
        static string B(bool on) => on ? "1" : "0";
        yield return ("match_case", B(c.Case));
        yield return ("default_match_whole_word", B(c.WholeWord));
        yield return ("match_path", B(c.Path));
        yield return ("match_diacritics", B(c.Diacritics));
        yield return ("match_regex", B(c.Regex));
        yield return (ResetKey, "1");
    }
}
