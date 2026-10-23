using System.Net;
using System.Text.Json;

namespace Anything.Core;

/// <summary>The latest release: its version, and its installer for this PC when it has one the app can update from.</summary>
public sealed record ReleaseInfo(Version Version, ReleaseAsset? Installer);

/// <summary>A release's installer: where GitHub serves it, its size and its SHA-256 (GitHub's own digest, lower-case hex).</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string Sha256);

/// <summary>
/// Finds out whether a newer Rock Preview is out: one HTTPS request to GitHub's API for the latest release of
/// RockySharaf/rock-preview. It sends nothing but Rock Preview's version (in the User-Agent GitHub requires) and
/// downloads nothing; what to do with the answer is up to the app (Update now downloads the installer it names,
/// <see cref="UpdateDownload"/>).
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "RockySharaf/rock-preview";
    static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>Installers larger than this aren't downloaded (today's is about 80 MB).</summary>
    public const long MaxInstallerBytes = 400L << 20;

    /// <summary>The installer this PC updates from: "RockPreview-Setup.exe", or "RockPreview-Setup-arm64.exe" on ARM64 (as Publish.ps1 names them).</summary>
    public static string InstallerName(System.Runtime.InteropServices.Architecture architecture) =>
        architecture == System.Runtime.InteropServices.Architecture.Arm64 ? "RockPreview-Setup-arm64.exe" : "RockPreview-Setup.exe";

    /// <summary>
    /// Whether an address is one of this project's release downloads on GitHub (https only): github.com's own
    /// /RockySharaf/rock-preview/releases/download/..., or the GitHub storage host it redirects to.
    /// </summary>
    public static bool IsReleaseDownload(Uri? url, bool redirected = false)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort) return false;
        string host = url.IdnHost;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
        return redirected && host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The latest release in GitHub's JSON, with the installer named <paramref name="installerName"/> when it is one of
    /// this project's release downloads, of a sensible size, with a SHA-256 digest; null for a draft, a pre-release or no
    /// usable tag.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string json, string installerName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String || ParseTag(tag.GetString()) is not { } version)
                return null;
            ReleaseAsset? installer = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || Text(a, "name") is not { } name || !name.Equals(installerName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Text(a, "browser_download_url") is not { } link || !Uri.TryCreate(link, UriKind.Absolute, out var url) || !IsReleaseDownload(url)) break;
                    if (!a.TryGetProperty("size", out var s) || !s.TryGetInt64(out long size) || size <= 0 || size > MaxInstallerBytes) break;
                    if (Sha256Of(Text(a, "digest")) is not { } sha) break;
                    installer = new ReleaseAsset(name, url, size, sha);
                    break;
                }
            return new ReleaseInfo(version, installer);
        }
        catch (JsonException) { return null; }
    }

    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The hex of a "sha256:..." digest, lower case; null for anything else.</summary>
    static string? Sha256Of(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[7..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigitLower) ? hex : null;
    }

    /// <summary>The release page for a version: its notes and the installer.</summary>
    public static string ReleasePage(Version version) => $"https://github.com/{Repository}/releases/tag/v{version.ToString(3)}";

    /// <summary>The version in a release tag ("v1.4.0", "1.4"), as major.minor.build; null for anything else ("1.4.0-beta").</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim();
        if (t[0] is 'v' or 'V') t = t[1..];
        if (!Version.TryParse(t, out var v) || v.Revision > 0) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>The version of the release in GitHub's "latest release" JSON; null for a draft, a pre-release or no tag.</summary>
    public static Version? ParseLatestRelease(string json) => ParseRelease(json, "")?.Version;

    /// <summary>True when <paramref name="latest"/> is a newer version than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version? latest, Version current) =>
        latest is not null && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>Asks GitHub for the latest release's version (null when there is none). Throws when GitHub can't be reached.</summary>
    public static async Task<Version?> FetchLatestAsync(string appVersion, CancellationToken cancel)
        => (await FetchLatestReleaseAsync(appVersion, "", cancel).ConfigureAwait(false))?.Version;

    /// <summary>
    /// Asks GitHub for the latest release, with its installer named <paramref name="installerName"/> (null when there is
    /// no release). Throws when GitHub can't be reached.
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestReleaseAsync(string appVersion, string installerName, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.ParseAdd($"RockPreview/{appVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no published release yet
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false), installerName);
    }
}
