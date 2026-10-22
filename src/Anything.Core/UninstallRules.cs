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
/// Whom the instant-indexing service (one for the whole PC) answers, seen from this user: not them (or it isn't installed,
/// or its list couldn't be read), only them, or them and other accounts.
/// </summary>
public enum ServiceFor { NotThisUser, OnlyThisUser, ThisUserAndOthers }

/// <summary>
/// The uninstall's decisions and words that need nothing from Windows, so they are tested: which running copies stop it,
/// which accounts can't, what it tells the user about those accounts, whose the instant-indexing service is, and what
/// Settings › About's Uninstall card says.
/// </summary>
public static class UninstallRules
{
    /// <summary>
    /// Said when Remove from this PC leaves the instant-indexing service because other accounts use it too. Never with how
    /// to delete it: this user can't see the other accounts' copies, so it would invite breaking their instant indexing,
    /// which only an administrator can turn back on.
    /// </summary>
    public const string ServiceStays = "The instant-indexing service stays: other accounts on this PC use it too.";

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
    /// folder: never called removed, and deleting its profile is offered only as a last resort, once its organization has
    /// confirmed the account is gone (a deleted work account can't sign in, and Windows can't tell it from one away from
    /// its network).
    /// </summary>
    public static string OtherAccountsMessage(IEnumerable<OtherAccount> others)
    {
        const string TurnOff = "turn off Settings › General › Use instead of Windows File Explorer";
        const string Network = "If it's a work or school account that still exists, connecting this PC to that network lets Windows name it.";
        var lines = new List<string> { "Nothing was removed, because other accounts on this PC may still need it:" };
        foreach (var a in others)
        {
            string? profile = a.Profile is { Length: > 0 } p ? p : null;
            string unknown = $"an account Windows couldn't identify ({a.Sid}{(profile is not null ? $", profile folder {profile}" : "")})";
            // Never this app's guess: only the organization knows the account is gone and not just away from its network.
            string lastResort = "If your organization has confirmed that this account no longer exists (someone who has left), an " +
                                "administrator can delete its profile in System Properties › Advanced › User Profiles, after saving " +
                                $"anything still needed from {profile ?? "its profile folder"}.";
            lines.Add((a.Use, a.Name) switch
            {
                (AccountUse.CouldNotCheck, { } name) => $"• {name}'s settings couldn't be checked. Sign in as {name} once, then sign out.",
                (AccountUse.CouldNotCheck, null) => $"• The settings of {unknown} couldn't be checked. Sign in as that account once, then sign out. {Network} {lastResort}",
                (_, { } name) => $"• {name} uses it in place of File Explorer. Sign in as {name} and {TurnOff} (if it's already off, change " +
                                 "any other setting once: that clears what's left).",
                _ => $"• {char.ToUpperInvariant(unknown[0])}{unknown[1..]} uses it in place of File Explorer. Sign in as that account and {TurnOff}. {Network} {lastResort}",
            });
        }
        lines.Add("Then uninstall again.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Whether a start-up or folder command runs the program file <paramref name="exe"/>: the program it names, compared as
    /// a full path (not as text, so another spelling of the same file matches, and a copy in a subfolder or another file in
    /// the same folder doesn't). Settings point at the running copy's own file, whatever it is called (a second download's
    /// "RockPreview (1).exe", a copy renamed UFE.exe), so that file is what is looked for.
    /// </summary>
    public static bool RunsProgram(string command, string exe)
    {
        if (ProgramOf(command) is not { } program) return false;
        try { return string.Equals(Path.GetFullPath(program).TrimEnd('\\'), Path.GetFullPath(exe).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>The program a start-up or folder command runs: its quoted first part, or the text up to ".exe".</summary>
    public static string? ProgramOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) return command.IndexOf('"', 1) is int end and > 1 ? command[1..end] : null;
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }

    /// <summary>
    /// Whom the instant-indexing service answers, from its list of allowed users (SIDs) and this user's SID (<paramref
    /// name="me"/>, null when unknown). Remove from this PC removes the service only when it is this user's alone: another
    /// account's copy (an install of its own, which this user can't see) may depend on it. An account <paramref
    /// name="isGone"/> says is certainly gone doesn't count: the list only ever grows, and a deleted account's SID would
    /// keep the service for nobody. It is asked only about other accounts, and only until one that isn't gone is found.
    /// </summary>
    public static ServiceFor ServiceIsFor(IEnumerable<string> allowedUsers, string? me, Func<string, bool>? isGone = null)
    {
        if (string.IsNullOrEmpty(me)) return ServiceFor.NotThisUser;
        bool mine = false, others = false;
        foreach (string sid in allowedUsers)
        {
            if (string.Equals(sid, me, StringComparison.OrdinalIgnoreCase)) mine = true;
            else if (!others && isGone?.Invoke(sid) != true) others = true;
        }
        return !mine ? ServiceFor.NotThisUser : others ? ServiceFor.ThisUserAndOthers : ServiceFor.OnlyThisUser;
    }

    /// <summary>
    /// Settings › About's Uninstall card for the installed copy. When this window is another copy (<paramref
    /// name="copy"/>, its program file: a portable copy run while an install exists) that folders and Win+E open with
    /// (<paramref name="takeover"/>) or that starts with Windows, the uninstall leaves that, since it is that copy's own:
    /// the card says so, never that folders go back to File Explorer then, and that copy's Remove from this PC, offered
    /// once the installed copy is gone, turns it off.
    /// </summary>
    public static string InstalledCardText(string? copy, bool takeover, bool startsWithWindows)
    {
        const string Files = "Your files are not affected.";
        if (copy is null || !(takeover || startsWithWindows))
            return "Removes Ultimate File Explorer from this PC: the program, its shortcuts, start with Windows and the instant-indexing " +
                   $"service, and gives folders and Win+E back to File Explorer. {Files}";
        string removes = startsWithWindows ? "the program, its shortcuts and the instant-indexing service"
                                           : "the program, its shortcuts, start with Windows and the instant-indexing service";
        string still = (takeover, startsWithWindows) switch
        {
            (true, true) => "folders and Win+E open with it and it starts with Windows",
            (true, false) => "folders and Win+E open with it",
            _ => "it starts with Windows",
        };
        return $"Removes the installed Ultimate File Explorer from this PC: {removes}" +
               $"{(takeover ? "" : ", and gives folders and Win+E back to File Explorer")}. This window is another copy ({copy}): " +
               $"{still}, which Uninstall doesn't change. Once the installed copy is uninstalled, Settings › About in this copy " +
               $"offers Remove from this PC to turn that off. {Files}";
    }

    /// <summary>
    /// Settings › About's Uninstall card for a copy that isn't installed (running from <paramref name="folder"/>): what it
    /// set up in Windows for this user, which Remove from this PC turns off while the program stays; or, when it set up
    /// nothing, that deleting it is all there is to do. The instant-indexing service counts only when it is this user's
    /// alone; when other accounts use it too, it is said to stay.
    /// </summary>
    public static string NotInstalledCardText(bool takeover, bool startsWithWindows, ServiceFor service, string folder)
    {
        var parts = new List<string>();
        if (takeover) parts.Add("folders and Win+E open with it");
        if (startsWithWindows) parts.Add("it starts with Windows");
        if (service == ServiceFor.OnlyThisUser) parts.Add("the instant-indexing service is installed");
        string stays = service == ServiceFor.ThisUserAndOthers ? " " + ServiceStays : "";
        if (parts.Count == 0)
            return service == ServiceFor.ThisUserAndOthers
                ? "Ultimate File Explorer isn't installed on this PC: this copy runs on its own, so deleting it removes it (your " +
                  $"settings and search index stay in the Rock Preview folders in AppData).{stays}"
                : "Ultimate File Explorer isn't installed on this PC: this copy runs on its own and hasn't set anything up in " +
                  "Windows, so deleting it removes it (your settings and search index stay in the Rock Preview folders in AppData).";
        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"Ultimate File Explorer isn't installed on this PC, but this copy set Windows up for it: {list}. Remove from this PC " +
               $"turns that off and keeps the program in {folder}, so deleting it afterwards leaves nothing pointing at it.{stays} " +
               "Your files are not affected.";
    }
}
