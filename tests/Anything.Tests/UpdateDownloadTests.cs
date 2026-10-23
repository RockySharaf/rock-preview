using System.Net;
using System.Security.Cryptography;
using Anything.Core;

namespace Anything.Tests;

/// <summary>A pretend GitHub: answers each request with what the test says (the address it "came from" included).</summary>
internal sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Interlocked.Increment(ref Requests);
        cancel.ThrowIfCancellationRequested();
        return Task.FromResult(answer(request));
    }
}

/// <summary>A response body that sends its first bytes, then nothing (a stalled line) until cancelled.</summary>
internal sealed class StallingStream(byte[] first) : Stream
{
    int _sent;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _sent; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (_sent < first.Length)
        {
            int n = Math.Min(buffer.Length, first.Length - _sent);
            first.AsMemory(_sent, n).CopyTo(buffer);
            _sent += n;
            return n;
        }
        await Task.Delay(Timeout.Infinite, cancel);
        return 0;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Update now's download: only the release's installer, only as GitHub describes it, nothing left behind otherwise.</summary>
[Collection(UpdateDownloadSerial.Name)]
public sealed class UpdateDownloadTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "rp-update-tests", Guid.NewGuid().ToString("N"));
    static readonly Uri ReleaseUrl = new("https://github.com/RockySharaf/rock-preview/releases/download/v1.7.4/RockPreview-Setup.exe");
    static readonly Uri Storage = new("https://release-assets.githubusercontent.com/github-production-release-asset/1/2");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    static byte[] Bytes(int n, int seed = 7)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    static ReleaseInfo ReleaseOf(byte[] bytes, Uri? url = null, string? sha = null, long? size = null)
        => new(new Version(1, 7, 4), new ReleaseAsset("RockPreview-Setup.exe", url ?? ReleaseUrl, size ?? bytes.Length,
            sha ?? Convert.ToHexStringLower(SHA256.HashData(bytes))));

    /// <summary>GitHub's answer: the release link redirects to its storage, which sends the bytes.</summary>
    static FakeGitHub Serving(byte[] bytes, Uri? from = null, long? contentLength = null) => new(request =>
    {
        var content = new ByteArrayContent(bytes);
        if (contentLength is { } l) content.Headers.ContentLength = l;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = new HttpRequestMessage(HttpMethod.Get, from ?? Storage) };
    });

    string[] FilesLeft() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*", SearchOption.AllDirectories) : [];

    sealed class Reports : IProgress<double>
    {
        public readonly List<double> Seen = [];
        public void Report(double value) { lock (Seen) Seen.Add(value); }
    }

    [Fact]
    public async Task The_installer_is_downloaded_checked_and_kept_under_its_version()
    {
        var bytes = Bytes(300_000);
        var progress = new Reports();
        string path = await UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", progress, default, Serving(bytes));
        Assert.Equal(Path.Combine(_folder, "1.7.4", "RockPreview-Setup.exe"), path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal([path], FilesLeft()); // no partial file
        Assert.Equal(1.0, progress.Seen[^1]);
    }

    [Fact]
    public async Task An_installer_whose_SHA256_differs_is_refused_and_nothing_is_kept()
    {
        var bytes = Bytes(100_000);
        var release = ReleaseOf(bytes, sha: Convert.ToHexStringLower(SHA256.HashData(Bytes(100_000, seed: 8))));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, _folder, "1.7.3", null, default, Serving(bytes)));
        Assert.Contains("SHA-256", e.Message);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task An_installer_of_another_size_is_refused()
    {
        var bytes = Bytes(100_000);
        // Said up front (Content-Length) ...
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateDownload.DownloadAsync(ReleaseOf(bytes, size: 99_999), _folder, "1.7.3", null, default, Serving(bytes)));
        // ... or only found out while receiving it, more or less than the release says.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateDownload.DownloadAsync(ReleaseOf(bytes, size: 50_000), _folder, "1.7.3", null, default, new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(bytes)),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, Storage),
            })));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateDownload.DownloadAsync(ReleaseOf(bytes, size: 200_000), _folder, "1.7.3", null, default, new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(bytes)),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, Storage),
            })));
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task An_installer_sent_from_another_site_is_refused()
    {
        var bytes = Bytes(10_000);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, default, Serving(bytes, from: new Uri("https://example.com/RockPreview-Setup.exe"))));
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task An_address_that_isnt_a_release_download_is_never_asked_for()
    {
        var bytes = Bytes(10_000);
        var github = Serving(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateDownload.DownloadAsync(ReleaseOf(bytes, url: new Uri("https://example.com/RockPreview-Setup.exe")), _folder, "1.7.3", null, default, github));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateDownload.DownloadAsync(new ReleaseInfo(new Version(1, 7, 4), null), _folder, "1.7.3", null, default, github));
        Assert.Equal(0, github.Requests);
    }

    [Fact]
    public async Task A_copy_downloaded_before_is_used_without_asking_again_and_a_damaged_one_is_replaced()
    {
        var bytes = Bytes(50_000);
        var github = Serving(bytes);
        string path = await UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, default, github);
        Assert.Equal(path, await UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, default, github));
        Assert.Equal(1, github.Requests);
        File.WriteAllBytes(path, Bytes(50_000, seed: 9)); // same size, other bytes
        Assert.Equal(path, await UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, default, github));
        Assert.Equal(2, github.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_stalled_download_ends_and_a_cancelled_one_leaves_nothing()
    {
        var bytes = Bytes(200_000);
        var before = UpdateDownload.StallLimit;
        UpdateDownload.StallLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            var stalls = new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(bytes[..1000])),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, Storage),
            });
            await Assert.ThrowsAsync<TimeoutException>(() => UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, default, stalls));
            Assert.Empty(FilesLeft());

            UpdateDownload.StallLimit = TimeSpan.FromSeconds(30);
            using var cts = new CancellationTokenSource();
            var running = UpdateDownload.DownloadAsync(ReleaseOf(bytes), _folder, "1.7.3", null, cts.Token, stalls);
            await Task.Delay(200);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Empty(FilesLeft());
        }
        finally { UpdateDownload.StallLimit = before; }
    }

    [Fact]
    public async Task Clean_up_keeps_only_newer_versions_and_drops_partial_downloads()
    {
        foreach (var v in new[] { "1.7.2", "1.7.3", "1.7.4", "not-a-version" }) Directory.CreateDirectory(Path.Combine(_folder, v));
        await File.WriteAllTextAsync(Path.Combine(_folder, "1.7.4", "RockPreview-Setup.exe"), "x");
        await File.WriteAllTextAsync(Path.Combine(_folder, "1.7.4", "RockPreview-Setup.exe.partial"), "x");
        UpdateDownload.CleanUp(_folder, new Version(1, 7, 3));
        Assert.Equal(["1.7.4", "not-a-version"], Directory.GetDirectories(_folder).Select(Path.GetFileName).Order());
        Assert.Equal(["RockPreview-Setup.exe"], Directory.GetFiles(Path.Combine(_folder, "1.7.4")).Select(Path.GetFileName));
        UpdateDownload.CleanUp(Path.Combine(_folder, "missing"), new Version(1, 7, 3)); // nothing there: nothing happens
    }
}

/// <summary>A fact that reaches the real GitHub: only with RP_NET_IT=1 (no test run depends on the internet otherwise).</summary>
public sealed class GitHubFactAttribute : FactAttribute
{
    public GitHubFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RP_NET_IT") != "1") Skip = "Reaches GitHub: set RP_NET_IT=1 to run it.";
    }
}

/// <summary>Update now against the real GitHub: the latest release's installer, its real redirect and digest.</summary>
[Collection(UpdateDownloadSerial.Name)]
public sealed class UpdateFromGitHubTests
{
    [GitHubFact]
    public async Task The_latest_release_is_downloaded_from_GitHub_and_matches_its_digest()
    {
        var release = await UpdateCheck.FetchLatestReleaseAsync("0.0.0", UpdateCheck.InstallerName(System.Runtime.InteropServices.Architecture.X64), default);
        Assert.NotNull(release);
        var installer = Assert.IsType<ReleaseAsset>(release.Installer);
        string folder = Path.Combine(Path.GetTempPath(), "rp-update-it", Guid.NewGuid().ToString("N"));
        try
        {
            string path = await UpdateDownload.DownloadAsync(release, folder, "0.0.0", null, default);
            Assert.Equal(installer.Size, new FileInfo(path).Length);
            Assert.True(await UpdateDownload.MatchesAsync(path, installer, default));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateDownloadSerial
{
    public const string Name = "Update now's download (StallLimit is shared)";
}
