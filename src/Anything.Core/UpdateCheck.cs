using System.Net;
using System.Text.Json;

namespace Anything.Core;

/// <summary>
/// Finds out whether a newer Rock Preview is out: one HTTPS request to GitHub's API for the latest release of
/// RockySharaf/rock-preview. It sends nothing but Rock Preview's version (in the User-Agent GitHub requires) and
/// downloads nothing; what to do with the answer is up to the app.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "RockySharaf/rock-preview";
    static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

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
    public static Version? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            return root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? ParseTag(tag.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True when <paramref name="latest"/> is a newer version than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version? latest, Version current) =>
        latest is not null && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>Asks GitHub for the latest release's version (null when there is none). Throws when GitHub can't be reached.</summary>
    public static async Task<Version?> FetchLatestAsync(string appVersion, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.ParseAdd($"RockPreview/{appVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no published release yet
        response.EnsureSuccessStatusCode();
        return ParseLatestRelease(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
    }
}
