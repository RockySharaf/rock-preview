using Anything.Core;

namespace Anything.Tests;

/// <summary>Rock Preview tells people about a new version from GitHub's latest-release answer.</summary>
public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4.0", "1.4.0")]
    [InlineData("v2.0", "2.0.0")]
    [InlineData(" V1.10.3 ", "1.10.3")]
    public void TagsGiveVersions(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateCheck.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.4.0-beta")]
    [InlineData("v1.4.0.7")]
    public void OtherTagsAreIgnored(string? tag) => Assert.Null(UpdateCheck.ParseTag(tag));

    [Fact]
    public void ReadsTheLatestRelease()
    {
        const string json = """{"tag_name":"v1.4.0","name":"Rock Preview 1.4.0","draft":false,"prerelease":false,"assets":[]}""";
        Assert.Equal(new Version(1, 4, 0), UpdateCheck.ParseLatestRelease(json));
    }

    [Theory]
    [InlineData("""{"tag_name":"v1.4.0","draft":true}""")]
    [InlineData("""{"tag_name":"v1.4.0","prerelease":true}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void IgnoresAnythingElse(string json) => Assert.Null(UpdateCheck.ParseLatestRelease(json));

    [Theory]
    [InlineData("1.4.0", "1.3.0", true)]
    [InlineData("1.3.1", "1.3.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.3.0", "1.3.0", false)]
    [InlineData("1.2.1", "1.3.0", false)]
    public void NewerOnlyWhenHigher(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(Version.Parse(latest), Version.Parse(current)));

    [Fact]
    public void AssemblyVersionsCompareByTheirFirstThreeParts() =>
        Assert.False(UpdateCheck.IsNewer(new Version(1, 3, 0), new Version(1, 3, 0, 0)));

    [Fact]
    public void ReleasePageIsOnTheProjectsGitHub() =>
        Assert.Equal("https://github.com/RockySharaf/rock-preview/releases/tag/v1.4.0", UpdateCheck.ReleasePage(new Version(1, 4, 0)));

    const string Hex = "0ca9b0ffda0c0114308d88fe8fa8a122f4b0a4147c0186a2540312055a0046a6";

    static string Release(string name = "RockPreview-Setup.exe", string url = "https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe",
        long size = 82_958_211, string? digest = "sha256:" + Hex)
        => $$"""
            {"tag_name":"v1.7.4","draft":false,"prerelease":false,"assets":[
              {"name":"RockPreview.exe","browser_download_url":"https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview.exe","size":1,"digest":"sha256:{{Hex}}"},
              {"name":"{{name}}","browser_download_url":"{{url}}","size":{{size}}{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}]}
            """;

    [Fact]
    public void ReadsTheInstallerToUpdateFrom()
    {
        var release = UpdateCheck.ParseRelease(Release(), "RockPreview-Setup.exe");
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 7, 4), release.Version);
        var installer = Assert.IsType<ReleaseAsset>(release.Installer);
        Assert.Equal("RockPreview-Setup.exe", installer.Name);
        Assert.Equal(82_958_211, installer.Size);
        Assert.Equal(Hex, installer.Sha256);
        Assert.Equal("https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe", installer.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("RockPreview-Setup-arm64.exe", null, 1L, "sha256:" + Hex)]                                                        // another PC's installer
    [InlineData(null, "http://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe", 1L, "sha256:" + Hex)] // not HTTPS
    [InlineData(null, "https://github.com/someone/else/releases/download/v1.7.4/RockPreview-Setup.exe", 1L, "sha256:" + Hex)]          // another project
    [InlineData(null, "https://example.com/RockPreview-Setup.exe", 1L, "sha256:" + Hex)]                                             // another site
    [InlineData(null, "https://objects.githubusercontent.com/RockPreview-Setup.exe", 1L, "sha256:" + Hex)]                          // storage, not a release link
    [InlineData(null, null, 0L, "sha256:" + Hex)]                                                                                    // empty
    [InlineData(null, null, 500L << 20, "sha256:" + Hex)]                                                                            // too big
    [InlineData(null, null, 1L, null)]                                                                                               // no digest
    [InlineData(null, null, 1L, "sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709")]                                                    // another hash
    [InlineData(null, null, 1L, "sha256:1234")]                                                                                      // not a SHA-256
    public void AnInstallerItCantCheckIsNotUpdatedFrom(string? name, string? url, long size, string? digest)
    {
        var release = UpdateCheck.ParseRelease(Release(name ?? "RockPreview-Setup.exe",
            url ?? "https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe", size, digest), "RockPreview-Setup.exe");
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 7, 4), release.Version); // still announced: its page has the installer
        Assert.Null(release.Installer);
    }

    [Fact]
    public void EachPcUpdatesFromItsOwnInstaller()
    {
        Assert.Equal("RockPreview-Setup.exe", UpdateCheck.InstallerName(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("RockPreview-Setup-arm64.exe", UpdateCheck.InstallerName(System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Theory]
    [InlineData("https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe", false, true)]
    [InlineData("https://GITHUB.com/rockysharaf/Rock-Preview/releases/download/v1.7.4/RockPreview-Setup.exe", false, true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?x=y", true, true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?x=y", false, false)]
    [InlineData("http://release-assets.githubusercontent.com/a", true, false)]
    [InlineData("https://github.com:8443/RockySharaf/rock-preview/releases/download/v1/a.exe", false, false)]
    [InlineData("https://githubusercontent.com.example.com/a", true, false)]
    [InlineData("https://github.com/RockySharaf/rock-preview/archive/main.zip", false, false)]
    public void OnlyThisProjectsReleaseDownloadsAreFetched(string url, bool redirected, bool allowed) =>
        Assert.Equal(allowed, UpdateCheck.IsReleaseDownload(new Uri(url), redirected));
}
