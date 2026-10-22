using Anything.Core;

namespace Anything.Tests;

/// <summary>What the uninstall decides and says without Windows: running copies, other accounts, a portable copy's card.</summary>
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
    public void AnAccountWindowsCantNameIsShownAndNeverToldToDeleteAnything(AccountUse use)
    {
        string text = UninstallRules.OtherAccountsMessage([new(Domain, null, @"C:\Users\sam.work", use)]);
        Assert.Contains(Domain, text);
        Assert.Contains(@"profile folder C:\Users\sam.work", text);
        Assert.Contains("Sign in as that account", text);
        Assert.Contains("connecting this PC to that network", text);
        Assert.DoesNotContain("elete", text);
        Assert.DoesNotContain("removed account", text);
    }

    [Fact]
    public void AnUnnamedAccountThatUsesItStartsItsLineWithACapital()
        => Assert.Contains($"• An account Windows couldn't identify ({Domain}) uses it in place of File Explorer.",
            UninstallRules.OtherAccountsMessage([new(Domain, null, null, AccountUse.UsesIt)]));

    // ---- a copy that isn't installed ----------------------------------------------------------

    [Fact]
    public void APortableCopyThatSetNothingUpIsRemovedByDeletingIt()
    {
        string text = UninstallRules.NotInstalledCardText(false, false, false, @"E:\Tools");
        Assert.Contains("hasn't set anything up in Windows, so deleting it removes it", text);
        Assert.DoesNotContain("Remove from this PC", text);
    }

    [Fact]
    public void APortableCopysSetupIsListed()
    {
        Assert.Contains("set Windows up for it: folders and Win+E open with it. Remove from this PC turns that off and keeps the program in E:\\Tools",
            UninstallRules.NotInstalledCardText(true, false, false, @"E:\Tools"));
        Assert.Contains(": it starts with Windows and the instant-indexing service is installed.",
            UninstallRules.NotInstalledCardText(false, true, true, @"E:\Tools"));
        Assert.Contains(": folders and Win+E open with it, it starts with Windows and the instant-indexing service is installed.",
            UninstallRules.NotInstalledCardText(true, true, true, @"E:\Tools"));
    }
}
