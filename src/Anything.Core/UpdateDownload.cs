using System.Security.Cryptography;

namespace Anything.Core;

/// <summary>
/// Update now's download: the latest release's installer from GitHub into a folder of the app's own, used only once its
/// size and SHA-256 are the ones GitHub gives for it. Nothing but this project's release downloads is fetched, over
/// HTTPS, whatever a redirect says.
/// </summary>
public static class UpdateDownload
{
    /// <summary>A download that receives nothing for this long has stalled.</summary>
    public static TimeSpan StallLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads the release's installer into <paramref name="folder"/>\&lt;version&gt;\ and returns its path once its size
    /// and SHA-256 are the release's (a copy downloaded before that still is one is used as it is). Throws otherwise, and
    /// leaves no partial file. <paramref name="progress"/> gets the share received, 0 to 1.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string folder, string appVersion, IProgress<double>? progress,
        CancellationToken cancel, HttpMessageHandler? handler = null)
    {
        var asset = release.Installer ?? throw new InvalidOperationException("This release has no installer for this PC.");
        if (!UpdateCheck.IsReleaseDownload(asset.Url))
            throw new InvalidOperationException("The installer isn't one of Ultimate File Explorer's release downloads on GitHub.");
        string dir = Path.Combine(folder, release.Version.ToString(3));
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, asset.Name);
        if (File.Exists(target) && await MatchesAsync(target, asset, cancel).ConfigureAwait(false))
        {
            progress?.Report(1);
            return target;
        }
        string partial = target + ".partial";
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan; // a slow line takes as long as it takes; a stall ends it (StallLimit)
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.ParseAdd($"RockPreview/{appVersion}");
            using var response = await WithStallLimit(t => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, t), cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!UpdateCheck.IsReleaseDownload(response.RequestMessage?.RequestUri, redirected: true))
                throw new InvalidDataException("GitHub sent the installer from somewhere else.");
            if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                throw new InvalidDataException("The installer GitHub sent isn't the size the release says.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                while (true)
                {
                    int n = await WithStallLimit(t => input.ReadAsync(buffer, t).AsTask(), cancel).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                    if (total > asset.Size) throw new InvalidDataException("The installer GitHub sent is larger than the release says.");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    progress?.Report((double)total / asset.Size);
                }
                if (total != asset.Size) throw new InvalidDataException("The download ended early.");
                if (!Convert.ToHexStringLower(sha.GetHashAndReset()).Equals(asset.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The download isn't the release's installer: its SHA-256 differs from the one GitHub gives.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Runs one step that must make progress within <see cref="StallLimit"/>; a stall is a TimeoutException, ct a cancellation.</summary>
    static async Task<T> WithStallLimit<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancel)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallLimit);
        try { return await step(stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The download stopped: nothing came from GitHub for a minute.");
        }
    }

    /// <summary>Whether a file is the release's installer: its size and SHA-256.</summary>
    public static async Task<bool> MatchesAsync(string path, ReleaseAsset asset, CancellationToken cancel)
    {
        try
        {
            if (new FileInfo(path).Length != asset.Size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            return await MatchesAsync(stream, asset, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Whether an open file is the release's installer, read from its start (Update now checks the copy it holds open, so
    /// the file Windows starts is the one checked).
    /// </summary>
    public static async Task<bool> MatchesAsync(Stream stream, ReleaseAsset asset, CancellationToken cancel)
    {
        if (stream.Length != asset.Size) return false;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(asset.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deletes downloaded installers of <paramref name="current"/> and older versions (one that is running stays until
    /// next time), and any partial download.
    /// </summary>
    public static void CleanUp(string folder, Version current)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (UpdateCheck.ParseTag(Path.GetFileName(dir)) is { } v && !UpdateCheck.IsNewer(v, current))
                {
                    try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    continue;
                }
                foreach (var partial in Directory.EnumerateFiles(dir, "*.partial"))
                    try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
