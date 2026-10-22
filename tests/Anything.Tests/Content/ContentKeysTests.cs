using Anything.Core.Content;

namespace Anything.Tests.Content;

public class ContentKeysTests
{
    const string Vol = "vol:{0d5e8b6a-1f2c-4c3d-9e8f-123456789abc}";

    // Computed by an independent FNV-1a reference (upper-cased UTF-16, low byte then high byte, '\' before each
    // component). A shared index needs the same keys on every PC, so these never change.
    [Theory]
    [InlineData(Vol, "", 0x60298CDC920504C8UL)]
    [InlineData(Vol, @"Users\Me\a.pdf", 0xCA0C657503E6622BUL)]
    [InlineData(@"unc:\\NAS\JOBS", @"2024\Job 17\Proof.pdf", 0x9CBEE24E12639AA2UL)]
    [InlineData("path:C:", @"Données\Rapport été.docx", 0x4368CAE48F308F5FUL)]
    [InlineData(@"unc:\\nas\jobs", @"東京\見積書.xlsx", 0xE5E37800ED69144DUL)]
    public void Known_answers(string volume, string relativePath, ulong expected)
        => Assert.Equal(expected, ContentKeys.Of(volume, relativePath));

    [Theory]
    [InlineData("a.pdf", 0x6B02D848U)]
    [InlineData("Ünïcode.txt", 0x8CA82663U)]
    [InlineData("REPORT.PDF", 0x6A59B8C7U)]
    public void Name_hash_known_answers(string name, uint expected) => Assert.Equal(expected, ContentKeys.NameHash(name));

    [Fact]
    public void Incremental_equals_whole()
    {
        ulong state = ContentKeys.Start(Vol);
        Assert.Equal(ContentKeys.Of(Vol, ""), state);
        foreach (var part in new[] { "Users", "Me", "Documents", "a.pdf" }) state = ContentKeys.Continue(state, part);
        Assert.Equal(ContentKeys.Of(Vol, @"Users\Me\Documents\a.pdf"), state);
    }

    [Fact]
    public void Case_and_separators_dont_matter()
    {
        ulong k = ContentKeys.Of(Vol, @"Users\Me\a.pdf");
        Assert.Equal(k, ContentKeys.Of(Vol.ToUpperInvariant(), @"USERS\ME\A.PDF"));
        Assert.Equal(k, ContentKeys.Of(Vol, "Users/Me/a.pdf"));
        Assert.Equal(k, ContentKeys.Of(Vol, @"\Users\\Me\a.pdf\"));
        Assert.Equal(ContentKeys.NameHash("Proof.PDF"), ContentKeys.NameHash("proof.pdf"));
    }

    [Fact]
    public void Volumes_and_components_keep_keys_apart()
    {
        Assert.NotEqual(ContentKeys.Of(@"unc:\\NAS\JOBS", "a.pdf"), ContentKeys.Of(Vol, "a.pdf"));
        Assert.NotEqual(ContentKeys.Of(@"unc:\\NAS\JOBS", "a.pdf"), ContentKeys.Of(@"unc:\\NAS\JOB", @"S\a.pdf"));
        Assert.NotEqual(ContentKeys.Of(Vol, @"ab\c"), ContentKeys.Of(Vol, @"a\bc"));
        Assert.NotEqual(ContentKeys.Of("path:C:", @"Data\x.txt"), ContentKeys.Of("path:D:", @"Data\x.txt"));
        // A folder root ("D:\Data") is its volume key plus the prefix components below it.
        Assert.Equal(ContentKeys.Of("path:D:", @"Data\x.txt"), ContentKeys.Continue(ContentKeys.Of("path:D:", "Data"), "x.txt"));
    }
}
