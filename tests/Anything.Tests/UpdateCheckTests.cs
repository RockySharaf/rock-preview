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
}
