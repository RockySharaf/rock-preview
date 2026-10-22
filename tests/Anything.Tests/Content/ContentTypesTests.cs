using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class ContentTypesTests
{
    [Theory]
    [InlineData("report.pdf", ContentKind.Pdf)]
    [InlineData("REPORT.PDF", ContentKind.Pdf)]
    [InlineData("a.docx", ContentKind.Office), InlineData("b.xlsm", ContentKind.Office), InlineData("c.ppsx", ContentKind.Office)]
    [InlineData("d.odt", ContentKind.Office), InlineData("e.otp", ContentKind.Office)]
    [InlineData("notes.txt", ContentKind.Text), InlineData("readme.md", ContentKind.Text), InlineData("data.tsv", ContentKind.Text)]
    [InlineData("old.rtf", ContentKind.Text), InlineData("page.HTML", ContentKind.Text)]
    [InlineData("mail.eml", ContentKind.Mail), InlineData("mail.msg", ContentKind.Mail)]
    [InlineData("logo.psd", ContentKind.Design), InlineData("logo.ai", ContentKind.Design), InlineData("book.indd", ContentKind.Design)]
    [InlineData("book.idml", ContentKind.Design), InlineData("art.eps", ContentKind.Design)]
    [InlineData("photo.jpg", ContentKind.Picture), InlineData("photo.HEIC", ContentKind.Picture), InlineData("scan.tiff", ContentKind.Picture)]
    [InlineData("old.doc", ContentKind.None), InlineData("old.xls", ContentKind.None), InlineData("raw.dng", ContentKind.None)]
    [InlineData("archive.zip", ContentKind.None), InlineData("app.log", ContentKind.None), InlineData("noext", ContentKind.None)]
    [InlineData(".pdf", ContentKind.None), InlineData("a.", ContentKind.None), InlineData("", ContentKind.None)]
    public void Kinds_by_extension(string name, ContentKind kind) => Assert.Equal(kind, ContentTypes.KindOf(name));

    [Fact]
    public void Every_extension_has_its_kind_and_a_reader()
    {
        foreach (var kind in Enum.GetValues<ContentKind>().Where(k => k != ContentKind.None))
        {
            Assert.NotEmpty(ContentTypes.Extensions(kind));
            foreach (var ext in ContentTypes.Extensions(kind))
            {
                Assert.Equal(kind, ContentTypes.KindOf("x." + ext));
                Assert.NotNull(ContentReaders.For(ext));
            }
        }
        Assert.Empty(ContentTypes.Extensions(ContentKind.None));
    }

    [Fact]
    public void Flags_of_kinds()
    {
        Assert.Equal(ContentKinds.Pdf, ContentTypes.FlagOf(ContentKind.Pdf));
        Assert.Equal(ContentKinds.Office, ContentTypes.FlagOf(ContentKind.Office));
        Assert.Equal(ContentKinds.Text, ContentTypes.FlagOf(ContentKind.Text));
        Assert.Equal(ContentKinds.Mail, ContentTypes.FlagOf(ContentKind.Mail));
        Assert.Equal(ContentKinds.Design, ContentTypes.FlagOf(ContentKind.Design));
        Assert.Equal(ContentKinds.None, ContentTypes.FlagOf(ContentKind.Picture));
        Assert.Equal(ContentKinds.None, ContentTypes.FlagOf(ContentKind.None));
    }

    [Theory]
    [InlineData(".env"), InlineData(".env.local"), InlineData(".ENV.production"), InlineData("id_rsa"), InlineData("id_rsa.pub")]
    [InlineData("id_ed25519"), InlineData("id_ecdsa.pub"), InlineData("credentials"), InlineData("credentials.json")]
    [InlineData("server.key"), InlineData("cert.PEM"), InlineData("me.pfx"), InlineData("me.p12"), InlineData("vault.kdbx")]
    [InlineData("putty.ppk"), InlineData("key.asc"), InlineData("x.gpg"), InlineData("x.pgp"), InlineData("work.ovpn"), InlineData("pc.rdp")]
    [InlineData(".npmrc"), InlineData(".pypirc"), InlineData(".netrc"), InlineData(".pgpass"), InlineData(".htpasswd"), InlineData(".git-credentials")]
    [InlineData("~$report.docx"), InlineData("x.tmp"), InlineData("big.iso.crdownload"), InlineData("video.mp4.part"), InlineData("y.partial")]
    public void Secrets_and_partial_files_are_denied(string name) => Assert.True(ContentTypes.IsDeniedFile(name), name);

    /// <summary>The files people keep secrets in as text, under the names browsers, password managers and sites give them.</summary>
    [Theory]
    [InlineData("Chrome Passwords.csv"), InlineData("Microsoft Edge Passwords.csv"), InlineData("Google Passwords.csv"), InlineData("logins.csv")]
    [InlineData("Firefox logins.csv"), InlineData("bitwarden_export_20260101120000.csv"), InlineData("lastpass_export.csv")]
    [InlineData("1PasswordExport-2026.csv"), InlineData("BitLocker Recovery Key 1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D.TXT")]
    [InlineData("github-recovery-codes.txt"), InlineData("Backup-codes-me.txt"), InlineData("rootkey.csv"), InlineData("ROG_accessKeys.csv")]
    [InlineData("client_secret_1234.apps.googleusercontent.com.json"), InlineData("dashlane-credential-export.csv"), InlineData("passwords.xlsx")]
    [InlineData("Password policy.pdf")] // an accepted cost of matching by name
    public void Password_exports_and_recovery_codes_are_denied(string name) => Assert.True(ContentTypes.IsDeniedFile(name), name);

    [Theory]
    [InlineData("report.pdf"), InlineData("keynote.txt"), InlineData("environment.txt"), InlineData("my_credentials_list.docx")]
    [InlineData("pem-notes.md"), InlineData("party.txt"), InlineData("notes.csv"), InlineData("Secretary notes.docx")]
    [InlineData("logins.docx"), InlineData("password-reset screenshot.png")]
    public void Ordinary_files_are_not_denied(string name) => Assert.False(ContentTypes.IsDeniedFile(name), name);

    [Theory]
    [InlineData("AppData", true), InlineData("appdata", true), InlineData(".ssh", true), InlineData(".gnupg", true), InlineData(".aws", true)]
    [InlineData(".azure", true), InlineData(".kube", true), InlineData(".git", true), InlineData(".hg", true), InlineData(".svn", true)]
    [InlineData("node_modules", true), InlineData("__pycache__", true), InlineData(".venv", true), InlineData("$Recycle.Bin", true)]
    [InlineData("System Volume Information", true), InlineData("Documents", false), InlineData("git", false), InlineData("", false)]
    public void Denied_folders(string name, bool denied) => Assert.Equal(denied, ContentTypes.IsDeniedFolder(name));

    [Fact]
    public void Denied_folder_paths_hold_temp_and_the_app_data()
    {
        var paths = ContentTypes.DeniedFolderPaths();
        Assert.Contains(paths, p => p.EndsWith(@"\Rock Preview", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ContentSettings.Normalize(Path.GetTempPath()), paths);
    }

    [Fact]
    public void The_deny_list_beats_extra_extensions()
    {
        var extras = ContentTypes.ExtraSet([" .Log ", "*.json", "key", "pem", ".env", "pdf", "a/b", ""]);
        Assert.Contains("log", extras);
        Assert.Contains("json", extras);
        Assert.DoesNotContain("a/b", extras);

        Assert.Equal(ContentKind.Text, ContentTypes.KindOf("app.LOG", extras));
        Assert.Equal(ContentKind.Text, ContentTypes.KindOf("server.key", extras));
        Assert.Equal(ContentKind.Pdf, ContentTypes.KindOf("a.pdf", extras)); // built-in kinds win over extras

        Assert.Equal(ContentKind.Text, ContentTypes.CandidateKind("app.log", ContentKinds.All, ContentMode.Documents, extras));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("server.key", ContentKinds.All, ContentMode.Documents, extras));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("cert.pem", ContentKinds.All, ContentMode.Documents, extras));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind(".env", ContentKinds.All, ContentMode.Documents, extras));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("credentials.json", ContentKinds.All, ContentMode.Documents, extras));
    }

    [Fact]
    public void Candidates_follow_kinds_and_mode()
    {
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("a.pdf", ContentKinds.All, ContentMode.NamesOnly, null));
        Assert.Equal(ContentKind.Pdf, ContentTypes.CandidateKind("a.pdf", ContentKinds.All, ContentMode.Documents, null));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("a.pdf", ContentKinds.Office, ContentMode.Documents, null));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("a.jpg", ContentKinds.All, ContentMode.Documents, null));
        Assert.Equal(ContentKind.Picture, ContentTypes.CandidateKind("a.jpg", ContentKinds.None, ContentMode.DocumentsAndPictures, null));
        Assert.Equal(ContentKind.Design, ContentTypes.CandidateKind("a.psd", ContentKinds.Design, ContentMode.Documents, null));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("a.log", ContentKinds.All, ContentMode.Documents, null));
    }

    [Fact]
    public void Span_lookups_dont_allocate()
    {
        var extras = ContentTypes.ExtraSet(["log"]);
        ReadOnlySpan<char> names = "report.PDF|app.log|id_rsa.pub|photo.jpeg|noext|node_modules";
        int sink = 0;
        void Run(ReadOnlySpan<char> all)
        {
            foreach (var range in all.Split('|'))
            {
                var name = all[range];
                sink += (int)ContentTypes.KindOf(name) + (int)ContentTypes.KindOf(name, extras)
                    + (int)ContentTypes.CandidateKind(name, ContentKinds.All, ContentMode.DocumentsAndPictures, extras)
                    + (ContentTypes.IsDeniedFile(name) ? 1 : 0) + (ContentTypes.IsDeniedFolder(name) ? 1 : 0);
            }
        }
        for (int i = 0; i < 100; i++) Run(names);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run(names);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0, sink);
    }
}
