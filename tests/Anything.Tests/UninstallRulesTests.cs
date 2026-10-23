using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// What the uninstall decides and says without Windows: running copies, other accounts, whose the service is, which file a
/// command runs, the Uninstall card.
/// </summary>
public sealed class UninstallRulesTests
{
    // ---- copies running in other sessions ---------------------------------------------------------

    [Theory]
    [InlineData(1, 2, true)]    // another signed-in user's copy: not asked from here, not ended
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]   // the caller's own session: asked to exit
    [InlineData(1, null, false)] // its session couldn't be read: as before, asked and waited for
    [InlineData(0, 1, false)]   // SYSTEM or a deployment tool: waits, and a quiet one ends it, as before 1.7.0
    [InlineData(0, 2, false)]
    [InlineData(1, 0, false)]   // a copy in session 0 is nobody's to lose anything
    public void OnlyAnotherSignedInUsersCopyStops(int caller, int? copy, bool stops)
        => Assert.Equal(stops, UninstallRules.StopsForOtherSession(caller, copy));

    // ---- accounts that were removed -------------------------------------------------------------

    const string Machine = "S-1-5-21-1111111111-2222222222-3333333333";

    [Fact]
    public void ADeletedLocalAccountIsRemoved()
        => Assert.True(UninstallRules.IsRemovedLocalAccount(Machine + "-1001", notMapped: true, Machine));

    [Fact]
    public void ALocalAccountWindowsStillKnowsIsNot()
        => Assert.False(UninstallRules.IsRemovedLocalAccount(Machine + "-1001", notMapped: false, Machine));

    [Theory]
    [InlineData("S-1-5-21-4444444444-5555555555-6666666666-1105")] // a domain account, maybe just away from its network
    [InlineData("S-1-12-1-1234567890-1234567890-1234567890-1234567890")] // a work (Entra ID) account
    [InlineData(Machine + "1-1001")]  // starts with the same digits, but another domain
    [InlineData(Machine + "-1001-7")] // not one number after this PC's domain
    [InlineData(Machine)]
    public void AnAccountOfAnotherDomainIsNeverRemoved(string sid)
        => Assert.False(UninstallRules.IsRemovedLocalAccount(sid, notMapped: true, Machine));

    [Fact]
    public void WithoutThisPcsDomainNothingIsRemoved()
        => Assert.False(UninstallRules.IsRemovedLocalAccount(Machine + "-1001", notMapped: true, null));

    // ---- what the uninstall says about other accounts ---------------------------------------------

    const string Domain = "S-1-5-21-4444444444-5555555555-6666666666-1105";

    [Fact]
    public void ANamedAccountThatUsesItIsToldHowToTurnItOff()
    {
        string text = UninstallRules.OtherAccountsMessage([new(Domain, @"PC\Sam", @"C:\Users\Sam", AccountUse.UsesIt)]);
        Assert.Contains(@"PC\Sam uses it in place of File Explorer. Sign in as PC\Sam and turn off Settings › General › Use instead of Windows File Explorer", text);
        Assert.EndsWith("Then uninstall again.", text);
    }

    [Fact]
    public void SettingsThatCouldNotBeCheckedAreNeverSaidToUseIt()
    {
        string named = UninstallRules.OtherAccountsMessage([new(Domain, @"PC\Sam", @"C:\Users\Sam", AccountUse.CouldNotCheck)]);
        string unknown = UninstallRules.OtherAccountsMessage([new(Domain, null, @"C:\Users\Sam", AccountUse.CouldNotCheck)]);
        Assert.Contains(@"PC\Sam's settings couldn't be checked. Sign in as PC\Sam once, then sign out.", named);
        Assert.Contains($@"The settings of an account Windows couldn't identify ({Domain}, profile folder C:\Users\Sam) couldn't be checked.", unknown);
        Assert.DoesNotContain("uses it", named);
        Assert.DoesNotContain("uses it", unknown);
    }

    [Theory]
    [InlineData(AccountUse.UsesIt)]
    [InlineData(AccountUse.CouldNotCheck)]
    public void AnAccountWindowsCantNameIsToldToDeleteNothingUnlessItsOrganizationConfirmsItIsGone(AccountUse use)
    {
        string text = UninstallRules.OtherAccountsMessage([new(Domain, null, @"C:\Users\sam.work", use)]);
        Assert.Contains(Domain, text);
        Assert.Contains(@"profile folder C:\Users\sam.work", text);
        Assert.Contains("Sign in as that account", text);
        Assert.Contains("If it's a work or school account that still exists, connecting this PC to that network", text);
        Assert.DoesNotContain("removed account", text);
        // Deleting its profile is a last resort, only once its organization says the account is gone: never the app's guess.
        const string lastResort = "If your organization has confirmed that this account no longer exists (someone who has left), an " +
                                  "administrator can delete its profile in System Properties › Advanced › User Profiles, after saving " +
                                  @"anything still needed from C:\Users\sam.work.";
        Assert.Contains(lastResort, text);
        string rest = text.Replace(lastResort, "");
        Assert.DoesNotContain("elete", rest);
        Assert.DoesNotContain("User Profiles", rest);
    }

    [Fact]
    public void AnUnnamedAccountWithoutAProfileFolderIsStillToldWhereToSaveFrom()
        => Assert.Contains("after saving anything still needed from its profile folder.",
            UninstallRules.OtherAccountsMessage([new(Domain, null, null, AccountUse.UsesIt)]));

    [Fact]
    public void ANamedAccountIsNeverToldToDeleteItsProfile()
    {
        foreach (var use in new[] { AccountUse.UsesIt, AccountUse.CouldNotCheck })
            Assert.DoesNotContain("elete", UninstallRules.OtherAccountsMessage([new(Domain, @"PC\Sam", @"C:\Users\Sam", use)]));
    }

    [Fact]
    public void AnUnnamedAccountThatUsesItStartsItsLineWithACapital()
        => Assert.Contains($"• An account Windows couldn't identify ({Domain}) uses it in place of File Explorer.",
            UninstallRules.OtherAccountsMessage([new(Domain, null, null, AccountUse.UsesIt)]));

    // ---- a copy that isn't installed ----------------------------------------------------------

    [Fact]
    public void APortableCopyThatSetNothingUpIsRemovedByDeletingIt()
    {
        // Also when the service is installed for other accounts only (another account's install turned it on).
        string text = UninstallRules.NotInstalledCardText(false, false, ServiceFor.NotThisUser, @"E:\Tools");
        Assert.Contains("hasn't set anything up in Windows, so deleting it removes it", text);
        Assert.DoesNotContain("Remove from this PC", text);
        Assert.DoesNotContain("instant-indexing", text);
    }

    [Fact]
    public void APortableCopysSetupIsListed()
    {
        Assert.Contains("set Windows up for it: folders and Win+E open with it. Remove from this PC turns that off and keeps the program in E:\\Tools",
            UninstallRules.NotInstalledCardText(true, false, ServiceFor.NotThisUser, @"E:\Tools"));
        Assert.Contains(": it starts with Windows and the instant-indexing service is installed.",
            UninstallRules.NotInstalledCardText(false, true, ServiceFor.OnlyThisUser, @"E:\Tools"));
        Assert.Contains(": folders and Win+E open with it, it starts with Windows and the instant-indexing service is installed.",
            UninstallRules.NotInstalledCardText(true, true, ServiceFor.OnlyThisUser, @"E:\Tools"));
    }

    [Fact]
    public void AServiceOtherAccountsUseTooIsSaidToStay()
    {
        string alone = UninstallRules.NotInstalledCardText(false, false, ServiceFor.ThisUserAndOthers, @"E:\Tools");
        Assert.Contains("deleting it removes it", alone);
        Assert.DoesNotContain("hasn't set anything up", alone);
        Assert.DoesNotContain("Remove from this PC", alone);
        Assert.EndsWith(UninstallRules.ServiceStays, alone);

        string withTakeover = UninstallRules.NotInstalledCardText(true, false, ServiceFor.ThisUserAndOthers, @"E:\Tools");
        Assert.Contains("set Windows up for it: folders and Win+E open with it. Remove from this PC", withTakeover);
        Assert.Contains(UninstallRules.ServiceStays, withTakeover);
        Assert.DoesNotContain("service is installed", withTakeover);
    }

    [Fact]
    public void AServiceThatStaysIsNeverSaidHowToDelete()
        // Another account's copy, which this user can't see, may still need it: deleting it would break that copy's
        // instant indexing.
        => Assert.DoesNotContain("sc delete", UninstallRules.ServiceStays);

    // ---- an installed copy, from another copy ---------------------------------------------------

    const string Installed = "Removes Rock Preview from this PC: the program, its shortcuts, start with Windows and the " +
                             "instant-indexing service, and gives folders and Win+E back to File Explorer. Your files are not affected.";

    [Fact]
    public void TheInstalledCopysCardSaysFoldersGoBackToFileExplorer()
    {
        Assert.Equal(Installed, UninstallRules.InstalledCardText(null, false, false));
        // Another copy that set nothing up for itself changes nothing.
        Assert.Equal(Installed, UninstallRules.InstalledCardText(@"E:\Downloads\RockPreview.exe", false, false));
    }

    [Fact]
    public void AnotherCopyFoldersOpenWithIsNeverSaidToGiveThemBack()
    {
        string text = UninstallRules.InstalledCardText(@"E:\Downloads\RockPreview.exe", takeover: true, startsWithWindows: false);
        Assert.DoesNotContain("back to File Explorer", text);
        Assert.Contains(@"This window is another copy (E:\Downloads\RockPreview.exe): folders and Win+E open with it, which Uninstall doesn't change.", text);
        Assert.Contains("Once the installed copy is uninstalled, Settings › About in this copy offers Remove from this PC", text);
        Assert.Contains("the program, its shortcuts, start with Windows and the instant-indexing service.", text);
    }

    [Fact]
    public void AnotherCopyThatStartsWithWindowsIsSaidToKeepDoingSo()
    {
        string text = UninstallRules.InstalledCardText(@"E:\Downloads\RockPreview.exe", takeover: false, startsWithWindows: true);
        Assert.Contains("the program, its shortcuts and the instant-indexing service, and gives folders and Win+E back to File Explorer.", text);
        Assert.Contains(": it starts with Windows, which Uninstall doesn't change.", text);

        string both = UninstallRules.InstalledCardText(@"E:\Downloads\RockPreview.exe", takeover: true, startsWithWindows: true);
        Assert.DoesNotContain("back to File Explorer", both);
        Assert.DoesNotContain("start with Windows and", both);
        Assert.Contains(": folders and Win+E open with it and it starts with Windows, which Uninstall doesn't change.", both);
    }

    // ---- whose the instant-indexing service is ------------------------------------------------

    const string Me = Machine + "-1001", Other = Machine + "-1002";

    [Fact]
    public void TheServiceIsThisUsersAloneWhenOnlyTheyAreAllowed()
    {
        Assert.Equal(ServiceFor.OnlyThisUser, UninstallRules.ServiceIsFor([Me], Me));
        Assert.Equal(ServiceFor.OnlyThisUser, UninstallRules.ServiceIsFor([Me.ToLowerInvariant()], Me));
    }

    [Fact]
    public void AnotherAllowedAccountSharesTheService()
        => Assert.Equal(ServiceFor.ThisUserAndOthers, UninstallRules.ServiceIsFor([Other, Me], Me));

    [Fact]
    public void TheServiceIsNotThisUsersWhenTheyArentAllowed()
    {
        Assert.Equal(ServiceFor.NotThisUser, UninstallRules.ServiceIsFor([Other], Me));
        Assert.Equal(ServiceFor.NotThisUser, UninstallRules.ServiceIsFor([], Me)); // the list couldn't be read
        Assert.Equal(ServiceFor.NotThisUser, UninstallRules.ServiceIsFor([Me], null)); // who this is couldn't be found out
    }

    [Fact]
    public void AnAccountThatIsCertainlyGoneDoesntShareTheService()
    {
        // Its SID stays on the list, which only ever grows; it would otherwise keep the service for nobody.
        Assert.Equal(ServiceFor.OnlyThisUser, UninstallRules.ServiceIsFor([Other, Me], Me, sid => sid == Other));
        Assert.Equal(ServiceFor.ThisUserAndOthers, UninstallRules.ServiceIsFor([Other, Domain, Me], Me, sid => sid == Other));
        Assert.Equal(ServiceFor.NotThisUser, UninstallRules.ServiceIsFor([Other], Me, _ => true));
    }

    [Fact]
    public void OnlyOtherAccountsAreLookedUpAndOnlyUntilOneIsntGone()
    {
        var asked = new List<string>();
        Assert.Equal(ServiceFor.ThisUserAndOthers, UninstallRules.ServiceIsFor([Me, Domain, Other], Me, sid => { asked.Add(sid); return false; }));
        Assert.Equal([Domain], asked);
    }

    // ---- the program a command runs ---------------------------------------------------------------

    [Theory]
    [InlineData("\"E:\\Tools\\UFE.exe\" -explorer -path \"%1\"", @"E:\Tools\UFE.exe", true)]          // a copy renamed UFE.exe
    [InlineData("\"E:\\Tools\\RockPreview (1).exe\" -startup", @"E:\Tools\RockPreview (1).exe", true)] // a second download
    [InlineData("\"e:\\tools\\.\\ufe.exe\" -startup", @"E:\Tools\UFE.exe", true)]                    // another spelling
    [InlineData(@"E:\Tools\UFE.exe -startup", @"E:\Tools\UFE.exe", true)]                            // not quoted
    [InlineData("\"E:\\Tools\\UFE.exe\" -startup", @"E:\Tools\RockPreview.exe", false)]              // another file in the folder
    [InlineData("\"E:\\Tools\\RockPreview.exe\" -startup", @"E:\Tools\UFE.exe", false)]
    [InlineData("\"E:\\Tools\\Old\\UFE.exe\" -startup", @"E:\Tools\UFE.exe", false)]                 // a copy in a subfolder
    [InlineData("\"\" -startup", @"E:\Tools\UFE.exe", false)]
    public void ACommandRunsExactlyTheFileItNames(string command, string exe, bool runs)
        => Assert.Equal(runs, UninstallRules.RunsProgram(command, exe));
}
