namespace Anything.Core;

/// <summary>
/// The search box's scope button, kept apart from the window so it can be tested: where a search looks (everything, the
/// folders selected in the view, the open folder, or the files "every file in" a folder shows) and what clicking it does.
/// Whenever it names a folder a click switches to everything, and from everything a click goes back to the folder there
/// is, so a click always does something unless there is no folder to look in.
/// </summary>
public static class SearchScope
{
    /// <summary>What clicking the button does.</summary>
    public enum Click { None, ToEverything, ToFolder }

    /// <summary>The button: "Everything" (in blue) or a folder's name, its tooltip, and what a click does.</summary>
    public readonly record struct Button(string Text, bool Everything, string Tip, Click Click);

    /// <param name="everywhere">The tab looks everywhere (Alt+X, this button, Search options), whatever is open or selected.</param>
    /// <param name="selected">The folders selected in the view a search looks inside ("images", "3 folders"), or null.</param>
    /// <param name="folder">The open folder's name, or null when none is (Home, This PC).</param>
    /// <param name="zap">The folders "every file in" shows, or null when it isn't showing.</param>
    public static Button Describe(bool everywhere, string? selected, string? folder, string? zap)
    {
        if (zap != null) return new(zap, false, $"Searching the files in {zap}. Click to search everything", Click.ToEverything);
        if ((selected ?? folder) is not { } scoped) return new("Everything", true, "Searching everything", Click.None);
        string where = selected != null ? $"the selected {selected}" : scoped;
        return everywhere
            ? new("Everything", true, $"Searching everything. Click to search only in {where}", Click.ToFolder)
            : new(scoped, false, $"Searching only in {where}. Click to search everything", Click.ToEverything);
    }
}
