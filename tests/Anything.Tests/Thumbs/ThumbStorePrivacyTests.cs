using System.Security.AccessControl;
using System.Security.Principal;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>
/// Who can read the store: a new folder is made private, the store's own folder and files are tightened, anything else
/// keeps its permissions, another account's store is not written into, and a drive without permissions still works.
/// Every permission a test sets is on folders and files inside its own temporary folder.
/// </summary>
public sealed class ThumbStorePrivacyTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-");
    // The place the user chose; the store's folder goes in it.
    string Place => Path.Combine(_dir.FullName, "Pictures drive");
    string Folder => Path.Combine(Place, ThumbStore.ChosenFolderName);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    static readonly FixedPower Roomy = new(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 500L << 30, TotalBytes: 1000L << 30));
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    // A made-up account standing for another person's Windows account on the same PC.
    static readonly SecurityIdentifier Someone = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    public ThumbStorePrivacyTests() => Directory.CreateDirectory(Place);

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static ThumbKey Key(int n) => ThumbKey.Of($@"D:\Pictures\photo {n}.jpg", new ThumbStamp(1000, 5000), 256);

    static byte[] Payload(int n)
    {
        var bytes = new byte[100];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(n * 31 + i);
        return bytes;
    }

    ThumbStore Open(Func<SecurityIdentifier, bool>? accountExists = null, Func<string, bool?>? keepsPermissions = null)
        => ThumbStore.Open(Folder, new ThumbStoreOptions { Power = Roomy, AccountExists = accountExists ?? (_ => false), KeepsPermissions = keepsPermissions });

    static void PutOne(ThumbStore store, int n = 0)
    {
        Assert.True(store.Put(Key(n), StoredKind.Jpeg, 256, 192, Payload(n)));
        Assert.True(store.WaitUntilWritten(Wait));
    }

    string Pack() => Directory.GetFiles(Folder, "pack-*.bin").First();

    static FileSystemSecurity SecurityOf(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    /// <summary>Who may read <paramref name="path"/> (allow entries with ReadData, explicit or inherited).</summary>
    static HashSet<SecurityIdentifier> Readers(string path)
    {
        var readers = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in SecurityOf(path).GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && ((int)rule.FileSystemRights & (0x1 | 0x10000000 | unchecked((int)0x80000000))) != 0)
                readers.Add((SecurityIdentifier)rule.IdentityReference);
        return readers;
    }

    static string Sddl(string path) => SecurityOf(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    /// <summary>A folder everyone on the PC may read, and so may everything made in it, as on a second drive.</summary>
    static void LetUsersRead(string folder) => Allow(folder, Users, FileSystemRights.ReadAndExecute);

    static void Allow(string folder, SecurityIdentifier who, FileSystemRights rights)
    {
        var dir = new DirectoryInfo(folder);
        var security = dir.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    static void AssertOnlyPrivateReaders(string path)
    {
        foreach (var sid in Readers(path))
            Assert.True(sid == Me || sid == SystemSid || sid == Administrators, $"{path} is readable by {sid}");
        Assert.DoesNotContain(Users, Readers(path));
        Assert.DoesNotContain(Everyone, Readers(path));
        Assert.DoesNotContain(AuthenticatedUsers, Readers(path));
    }

    [Fact]
    public void A_new_folder_in_a_place_everyone_reads_is_made_private_and_so_are_its_packs()
    {
        LetUsersRead(Place);
        Assert.Contains(Users, Readers(Place));
        using var store = Open();
        Assert.True(store.IsEnabled);
        Assert.True(store.IsPrivate);
        Assert.True(SecurityOf(Folder).AreAccessRulesProtected);
        AssertOnlyPrivateReaders(Folder);
        PutOne(store);
        AssertOnlyPrivateReaders(Pack());
    }

    [Fact]
    public void A_store_folder_that_lets_others_read_and_holds_only_the_store_is_tightened()
    {
        using (var store = Open()) PutOne(store);
        // Someone let the folder take its permissions from the place again, where everyone may read.
        LetUsersRead(Place);
        var dir = new DirectoryInfo(Folder);
        var loose = dir.GetAccessControl(AccessControlSections.Access);
        loose.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        dir.SetAccessControl(loose);
        Assert.Contains(Users, Readers(Folder));
        Assert.Contains(Users, Readers(Pack()));

        using var again = Open();
        Assert.True(again.IsEnabled);
        Assert.True(again.IsPrivate);
        Assert.True(SecurityOf(Folder).AreAccessRulesProtected);
        AssertOnlyPrivateReaders(Folder);
        AssertOnlyPrivateReaders(Pack());
        Assert.True(again.TryGet(Key(0), out _));
    }

    [Fact]
    public void A_folder_that_lets_others_read_and_holds_a_users_file_keeps_its_permissions()
    {
        LetUsersRead(Place);
        Directory.CreateDirectory(Folder);
        string notes = Path.Combine(Folder, "notes.txt");
        File.WriteAllText(notes, "the user's own notes");
        string folderBefore = Sddl(Folder), notesBefore = Sddl(notes);

        using var store = Open();
        Assert.True(store.IsEnabled);
        Assert.False(store.IsPrivate);
        Assert.Equal(folderBefore, Sddl(Folder));
        Assert.Equal(notesBefore, Sddl(notes));
        PutOne(store);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Pack_permissions_are_checked_when_the_folder_is_tightened_or_unprotected(bool protectedFolder, bool tighten)
    {
        using (var store = Open()) PutOne(store);
        if (tighten) LetUsersRead(Folder);
        if (!protectedFolder)
        {
            // Let the store folder inherit only private permissions, so it needs no tightening at open.
            var dir = new DirectoryInfo(Folder);
            var folderRules = dir.GetAccessControl(AccessControlSections.Access);
            folderRules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            new DirectoryInfo(Place).SetAccessControl(folderRules);
            folderRules.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            dir.SetAccessControl(folderRules);
            AssertOnlyPrivateReaders(Folder);
        }
        Assert.Equal(protectedFolder, SecurityOf(Folder).AreAccessRulesProtected);
        var pack = new FileInfo(Pack());
        var open = new FileSecurity();
        open.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        open.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, AccessControlType.Allow));
        open.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));
        pack.SetAccessControl(open);
        Assert.Contains(Everyone, Readers(pack.FullName));

        using var again = Open();
        Assert.True(again.IsPrivate);
        bool checkedPack = tighten || !protectedFolder;
        Assert.Equal(!checkedPack, SecurityOf(pack.FullName).AreAccessRulesProtected);
        if (checkedPack) AssertOnlyPrivateReaders(pack.FullName);
        Assert.True(again.TryGet(Key(0), out var t));
        Assert.Equal(Payload(0), t.Payload.ToArray());
    }

    [Fact]
    public void A_drive_that_keeps_no_permissions_still_stores_but_is_not_private()
    {
        // FAT or exFAT (a USB stick): the test machine's temp folder is NTFS, so the volume's answer is given.
        using var store = Open(keepsPermissions: _ => false);
        Assert.True(store.IsEnabled);
        Assert.False(store.IsPrivate);
        PutOne(store);
        Assert.True(store.TryGet(Key(0), out _));
    }

    [Fact]
    public void A_drive_that_cannot_say_whether_it_keeps_permissions_is_not_private()
    {
        using var store = Open(keepsPermissions: _ => null);
        Assert.True(store.IsEnabled);
        Assert.False(store.IsPrivate);
    }

    [Fact]
    public void Permissions_that_cannot_be_read_never_stop_the_store()
    {
        using var store = Open(keepsPermissions: _ => throw new UnauthorizedAccessException("A share that refuses it."));
        Assert.True(store.IsEnabled);
        Assert.Equal(ThumbStoreProblem.None, store.Problem);
        Assert.False(store.IsPrivate);
        PutOne(store);
    }

    [Fact]
    public void A_disabled_store_is_not_private()
    {
        using var off = ThumbStore.Disabled();
        Assert.False(off.IsPrivate);
    }

    [Fact]
    public void Another_accounts_store_is_not_written_into()
    {
        Directory.CreateDirectory(Folder);
        Allow(Folder, Someone, FileSystemRights.FullControl);
        string before = Sddl(Folder);

        using var store = Open(accountExists: sid => sid == Someone);
        Assert.False(store.IsEnabled);
        Assert.False(store.IsPrivate);
        Assert.Equal(ThumbStoreProblem.OtherAccount, store.Problem);
        Assert.False(store.Put(Key(0), StoredKind.Jpeg, 256, 192, Payload(0)));
        // Not even the marker, and its permissions as they were.
        Assert.Empty(Directory.GetFileSystemEntries(Folder));
        Assert.Equal(before, Sddl(Folder));
    }

    [Fact]
    public void Full_control_for_an_Entra_account_marks_its_folder_as_another_accounts()
    {
        var entra = new SecurityIdentifier("S-1-12-1-1111111111-2222222222-3333333333-1001");
        Directory.CreateDirectory(Folder);
        Allow(Folder, entra, FileSystemRights.FullControl);
        using var store = Open(accountExists: sid => sid == entra);
        Assert.Equal(ThumbStoreProblem.OtherAccount, store.Problem);
        Assert.Empty(Directory.GetFileSystemEntries(Folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_an_unmapped_account_from_this_PCs_domain_is_treated_as_gone(bool local)
    {
        var localDomain = Assert.IsType<SecurityIdentifier>(ThumbStore.LocalAccountDomain());
        var account = local ? new SecurityIdentifier(localDomain.Value + "-123456789") : Someone;
        Directory.CreateDirectory(Folder);
        Allow(Folder, account, FileSystemRights.FullControl);

        using var store = Open(accountExists: _ => throw new IdentityNotMappedException());
        Assert.Equal(local ? ThumbStoreProblem.None : ThumbStoreProblem.OtherAccount, store.Problem);
        if (local) Assert.DoesNotContain(account, Readers(Folder));
        else Assert.Empty(Directory.GetFileSystemEntries(Folder));
    }

    [Fact]
    public void Full_control_for_an_account_that_no_longer_exists_is_not_another_accounts_store()
    {
        // A deleted local account can no longer sign in to read the folder.
        Directory.CreateDirectory(Folder);
        Allow(Folder, Someone, FileSystemRights.FullControl);

        using var store = Open(accountExists: _ => false);
        Assert.True(store.IsEnabled);
        Assert.True(store.IsPrivate);
        Assert.DoesNotContain(Someone, Readers(Folder));
    }

    [Fact]
    public void Full_control_passed_down_from_the_place_above_does_not_make_the_folder_another_accounts()
    {
        // A share where the place above gives an administrator's account full control over everything in it.
        Allow(Place, Someone, FileSystemRights.FullControl);
        Directory.CreateDirectory(Folder);

        using var store = Open(accountExists: sid => sid == Someone);
        Assert.True(store.IsEnabled);
        Assert.Equal(ThumbStoreProblem.None, store.Problem);
        // Holding nothing, the folder is the store's own and is tightened.
        Assert.True(store.IsPrivate);
        Assert.DoesNotContain(Someone, Readers(Folder));
    }

    [Fact]
    public void A_folder_made_by_the_store_is_never_taken_for_another_accounts()
    {
        // Give the new folder the explicit rule that would identify an existing folder as someone else's.
        using var store = Open(accountExists: sid => sid == Someone, keepsPermissions: folder =>
        {
            Allow(folder, Someone, FileSystemRights.FullControl);
            return true;
        });
        Assert.True(store.IsEnabled);
        Assert.True(store.IsPrivate);
        Assert.DoesNotContain(Someone, Readers(Folder));
    }

    sealed class FixedPower(PowerState state) : IPowerPolicy
    {
        public PowerState Now(string contentFolder) => state;
    }
}
