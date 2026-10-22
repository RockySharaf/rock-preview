using System.Globalization;

namespace Anything.Core;

/// <summary>Why another Windows account stops an uninstall for everyone.</summary>
public enum AccountUse { UsesIt, CouldNotCheck }

/// <summary>
/// Another account that stops an uninstall for everyone: its SID, its name (null when Windows can't name it now), its
/// profile folder (null when Windows has none listed) and why.
/// </summary>
public sealed record OtherAccount(string Sid, string? Name, string? Profile, AccountUse Use);

/// <summary>
/// The uninstall's decisions and words that need nothing from Windows, so they are tested: which running copies stop it,
/// which accounts can't, what it tells the user about those accounts, and what Settings › About says about a copy that
/// isn't installed.
/// </summary>
public static class UninstallRules
{
    /// <summary>
    /// Whether a copy running in session <paramref name="copySession"/> stops a caller in <paramref name="callerSession"/>:
    /// only a copy in another signed-in user's session, which can't be asked to close from here and isn't ended (its user
    /// would lose what it hadn't saved). Session 0 (SYSTEM, a service, a task that runs whether anyone is signed in or
    /// not) is nobody's desktop: a caller there can't ask any copy, so it waits and a quiet one ends them, and a copy
    /// there has nobody to lose anything.
    /// </summary>
    public static bool StopsForOtherSession(int callerSession, int? copySession)
        => callerSession != 0 && copySession is int s && s != 0 && s != callerSession;

    /// <summary>
    /// Whether the account <paramref name="sid"/> is certainly gone: Windows said no such account exists (<paramref
    /// name="notMapped"/>) and it is one of this PC's own local accounts (its SID is <paramref name="machineDomainSid"/>
    /// and one more number), which Windows looks up without asking anyone. Such an account never signs in again, so what
    /// it left can't matter. A work or domain account Windows can't name may just be away from its network, and isn't.
    /// </summary>
    public static bool IsRemovedLocalAccount(string sid, bool notMapped, string? machineDomainSid)
    {
        if (!notMapped || string.IsNullOrEmpty(machineDomainSid)) return false;
        string prefix = machineDomainSid + "-";
        return sid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && uint.TryParse(sid.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// What to tell the user when other accounts stop an uninstall for everyone, with what each can do. An account whose
    /// settings couldn't be checked is never said to use it, and one Windows can't name is shown by its SID and profile
    /// folder: never called removed, and never with advice to delete anything of it.
    /// </summary>
    public static string OtherAccountsMessage(IEnumerable<OtherAccount> others)
    {
        const string TurnOff = "turn off Settings › General › Use instead of Windows File Explorer";
        const string Network = "If it's a work or school account, connecting this PC to that network lets Windows name it.";
        var lines = new List<string> { "Nothing was removed, because other accounts on this PC may still need it:" };
        foreach (var a in others)
        {
            string unknown = $"an account Windows couldn't identify ({a.Sid}{(a.Profile is { Length: > 0 } p ? $", profile folder {p}" : "")})";
            lines.Add((a.Use, a.Name) switch
            {
                (AccountUse.CouldNotCheck, { } name) => $"• {name}'s settings couldn't be checked. Sign in as {name} once, then sign out.",
                (AccountUse.CouldNotCheck, null) => $"• The settings of {unknown} couldn't be checked. Sign in as that account once, then sign out. {Network}",
                (_, { } name) => $"• {name} uses it in place of File Explorer. Sign in as {name} and {TurnOff} (if it's already off, change " +
                                 "any other setting once: that clears what's left).",
                _ => $"• {char.ToUpperInvariant(unknown[0])}{unknown[1..]} uses it in place of File Explorer. Sign in as that account and {TurnOff}. {Network}",
            });
        }
        lines.Add("Then uninstall again.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Settings › About's Uninstall card for a copy that isn't installed (running from <paramref name="folder"/>): what it
    /// set up in Windows for this user, which Remove from this PC turns off while the program stays; or, when it set up
    /// nothing, that deleting it is all there is to do.
    /// </summary>
    public static string NotInstalledCardText(bool takeover, bool startsWithWindows, bool service, string folder)
    {
        var parts = new List<string>();
        if (takeover) parts.Add("folders and Win+E open with it");
        if (startsWithWindows) parts.Add("it starts with Windows");
        if (service) parts.Add("the instant-indexing service is installed");
        if (parts.Count == 0)
            return "Ultimate File Explorer isn't installed on this PC: this copy runs on its own and hasn't set anything up in " +
                   "Windows, so deleting it removes it (your settings and search index stay in the Rock Preview folders in AppData).";
        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"Ultimate File Explorer isn't installed on this PC, but this copy set Windows up for it: {list}. Remove from this PC " +
               $"turns that off and keeps the program in {folder}, so deleting it afterwards leaves nothing pointing at it. " +
               "Your files are not affected.";
    }
}
