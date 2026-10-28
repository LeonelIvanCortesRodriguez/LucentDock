using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Strata.Core;

namespace Strata.Tests;

/// <summary>Updates from a web release (a GitHub repository), on a fake HTTP handler: nothing goes online.</summary>
public class WebUpdateTests
{
    [Fact]
    public void StageCleanupAcceptsOnlyDirectGuidNamedTempChildren()
    {
        string root = Path.Combine(Path.GetTempPath(), "strata-cleanup-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string owned = Path.Combine(root, "Strata-update-" + Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(root, "Strata-update-not-a-guid");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(owned, "fixture.bin"), "fixture");
        try
        {
            Assert.True(Updater.IsOwnedStageDirectory(owned, root));
            Assert.False(Updater.IsOwnedStageDirectory(unrelated, root));
            Assert.False(Updater.IsOwnedStageDirectory(root, root));
            Assert.True(Updater.CleanupOwnedStage(owned, root));
            Assert.False(Directory.Exists(owned));
            Assert.True(Directory.Exists(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class DeniedStageIO : IUpdateStagingIO
    {
        public int CleanupAttempts;
        public void CreateDirectory(string path) => throw new UnauthorizedAccessException("fixture denial");
        public void CopyFile(string source, string destination) => throw new NotSupportedException();
        public Stream CreateFile(string path) => throw new NotSupportedException();
        public Stream OpenRead(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => CleanupAttempts++;
    }
    /// <summary>Serves fixed responses by URL and records what was asked for.</summary>
    private sealed class FakeWeb(Dictionary<string, (HttpStatusCode Status, byte[] Body)> pages) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string url = request.RequestUri!.ToString();
            Asked.Add(url);
            var (status, body) = pages.TryGetValue(url, out var page) ? page : (HttpStatusCode.NotFound, Array.Empty<byte>());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    /// <summary>A 20 MB unsized body (no Content-Length) that counts how much was read from it. Far larger than
    /// either cap, but finite, so a broken cap fails the test instead of filling the disk.</summary>
    private sealed class Endless : Stream
    {
        private const long Total = 20 * 1024 * 1024;
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, Total - BytesRead);
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessWeb(Endless body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body), RequestMessage = request });
    }

    private sealed class GrowingLocalStageIO(Endless source) : IUpdateStagingIO
    {
        private MemoryStream _staged = new();
        private int _opens;
        public bool Cleaned { get; private set; }
        public void CreateDirectory(string path) { }
        public void CopyFile(string sourcePath, string destination) => throw new InvalidOperationException("Unbounded copy must not be used.");
        public Stream CreateFile(string path) => _staged = new MemoryStream();
        public Stream OpenRead(string path) => _opens++ == 0 ? source : new MemoryStream(_staged.ToArray(), writable: false);
        public void DeleteDirectory(string path) => Cleaned = true;
    }

    [Fact]
    public async Task Reading_stops_at_the_expected_size_even_without_a_declared_length()
    {
        var manifestBody = new Endless();
        var check = await Updater.CheckAsync(Repo, "1.2.0", new HttpClient(new EndlessWeb(manifestBody)));
        Assert.Equal(UpdateState.Invalid, check.State);
        Assert.InRange(manifestBody.BytesRead, 1, Updater.MaxManifestBytes + 16 * 1024);    // stopped near the cap, not at the end

        var m = new UpdateManifest("1.2.1", "Strata.exe", new string('A', 64), 1000);
        var exeBody = new Endless();
        var (staged, why) = await Updater.StageAsync(Repo, m, null, Path.GetTempPath(), new HttpClient(new EndlessWeb(exeBody)));
        Assert.Null(staged);
        Assert.Contains("size", why);
        Assert.InRange(exeBody.BytesRead, 1, 1000 + 200 * 1024);                            // never the whole (endless) body
    }

    [Fact]
    public void Local_staging_stops_one_byte_past_manifest_size_and_cleans_partial_copy()
    {
        var source = new Endless();
        var io = new GrowingLocalStageIO(source);
        var manifest = new UpdateManifest("1.3.0", "Strata.exe", new string('A', 64), 1000);

        var (staged, why) = Updater.Stage(@"X:\fixture", manifest, null, @"X:\temp", io);

        Assert.Null(staged);
        Assert.Contains("size", why);
        Assert.InRange(source.BytesRead, 1, manifest.Size + 1);
        Assert.True(io.Cleaned);
    }

    private const string Repo = "https://github.com/sample-owner/strata";
    private const string Latest = "https://github.com/sample-owner/strata/releases/latest/download/";

    private static byte[] Manifest(string version, byte[] exe, string? sha = null, long? size = null) =>
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        {
            version, file = "Strata.exe", sha256 = sha ?? Convert.ToHexString(SHA256.HashData(exe)), size = size ?? exe.Length,
        }));

    private static (HttpClient Http, FakeWeb Web) Web(params (string Url, HttpStatusCode Status, byte[] Body)[] pages)
    {
        var web = new FakeWeb(pages.ToDictionary(p => p.Url, p => (p.Status, p.Body)));
        return (new HttpClient(web), web);
    }

    [Theory]
    [InlineData("https://github.com/sample-owner/strata", Latest + "update.json")]
    [InlineData("https://github.com/sample-owner/strata/", Latest + "update.json")]
    [InlineData("https://github.com/sample-owner/strata.git", Latest + "update.json")]
    [InlineData("https://github.com/sample-owner/strata/releases", Latest + "update.json")]
    [InlineData("https://example.com/strata/update.json", "https://example.com/strata/update.json")]
    [InlineData("https://example.com/strata/", "https://example.com/strata/update.json")]
    [InlineData("https://example.com/strata", "https://example.com/strata/update.json")]
    [InlineData("http://example.com/strata/update.json", null)]   // https only
    [InlineData(@"C:\Releases", null)]                               // a folder, not a web source
    [InlineData("", null)]
    public void Web_sources_become_an_update_json_address(string source, string? expected) =>
        Assert.Equal(expected, Updater.ManifestUri(source)?.ToString());

    [Fact]
    public async Task A_newer_github_release_is_offered_after_reading_only_update_json()
    {
        byte[] exe = [1, 2, 3, 4];
        var (http, web) = Web((Latest + "update.json", HttpStatusCode.OK, Manifest("1.2.1", exe)));
        var check = await Updater.CheckAsync(Repo, "1.2.0", http);
        Assert.Equal(UpdateState.Available, check.State);
        Assert.Equal("1.2.1", check.Manifest!.Version);
        Assert.Equal([Latest + "update.json"], web.Asked);    // the release itself is downloaded only on Install

        Assert.Equal(UpdateState.UpToDate, (await Updater.CheckAsync(Repo, "1.2.1", http)).State);
    }

    [Fact]
    public async Task Missing_bad_and_unsafe_web_releases_are_reported()
    {
        var (http, _) = Web(
            ("https://example.com/bad/update.json", HttpStatusCode.OK, "{ not json"u8.ToArray()),
            ("https://example.com/huge/update.json", HttpStatusCode.OK, new byte[Updater.MaxManifestBytes + 1]),
            ("https://example.com/down/update.json", HttpStatusCode.ServiceUnavailable, []));
        Assert.Equal(UpdateState.Unreachable, (await Updater.CheckAsync(Repo, "1.2.0", http)).State);               // no release yet
        Assert.Contains("published release", (await Updater.CheckAsync(Repo, "1.2.0", http)).Message);
        Assert.Equal(UpdateState.Invalid, (await Updater.CheckAsync("https://example.com/bad/", "1.2.0", http)).State);
        Assert.Equal(UpdateState.Invalid, (await Updater.CheckAsync("https://example.com/huge/", "1.2.0", http)).State);
        Assert.Equal(UpdateState.Unreachable, (await Updater.CheckAsync("https://example.com/down/", "1.2.0", http)).State);
        Assert.Equal(UpdateState.Invalid, (await Updater.CheckAsync("http://example.com/down/", "1.2.0", http)).State);
        Assert.Equal(UpdateState.Off, (await Updater.CheckAsync("", "1.2.0", http)).State);
    }

    [Fact]
    public async Task Only_a_download_matching_its_size_and_hash_is_staged()
    {
        byte[] exe = [9, 8, 7, 6, 5];
        var m = Updater.Parse(System.Text.Encoding.UTF8.GetString(Manifest("1.2.1", exe)))!;
        var (good, _) = Web((Latest + "Strata.exe", HttpStatusCode.OK, exe));
        var (staged, _) = await Updater.StageAsync(Repo, m, runningExe: null, Path.GetTempPath(), good);
        Assert.NotNull(staged);
        Assert.Equal(exe, File.ReadAllBytes(staged!));

        var (tampered, _) = Web((Latest + "Strata.exe", HttpStatusCode.OK, [9, 8, 7, 6, 4]));
        var (none, why) = await Updater.StageAsync(Repo, m, null, Path.GetTempPath(), tampered);
        Assert.Null(none);
        Assert.Contains("SHA-256", why);

        var (longer, _) = Web((Latest + "Strata.exe", HttpStatusCode.OK, [9, 8, 7, 6, 5, 4, 3]));
        (none, why) = await Updater.StageAsync(Repo, m, null, Path.GetTempPath(), longer);
        Assert.Null(none);
        Assert.Contains("size", why);

        var (shorter, _) = Web((Latest + "Strata.exe", HttpStatusCode.OK, [9, 8]));
        (none, why) = await Updater.StageAsync(Repo, m, null, Path.GetTempPath(), shorter);
        Assert.Null(none);
        Assert.Contains("size", why);

        var (missing, _) = Web();
        (none, why) = await Updater.StageAsync(Repo, m, null, Path.GetTempPath(), missing);
        Assert.Null(none);
        Assert.Contains("404", why);
    }

    [Fact]
    public void Manifest_rejects_non_hex_hashes_and_unbounded_release_sizes()
    {
        Assert.Null(Updater.Parse("{\"version\":\"1.3.0\",\"file\":\"Strata.exe\",\"sha256\":\"" + new string('Z', 64) + "\",\"size\":1}"));
        Assert.Null(Updater.Parse("{\"version\":\"1.3.0\",\"file\":\"Strata.exe\",\"sha256\":\"" + new string('A', 64) + "\",\"size\":" + (Updater.MaxReleaseBytes + 1) + "}"));
    }

    [Fact]
    public async Task Web_staging_access_denial_is_caught_before_download_and_cleanup_is_attempted()
    {
        byte[] exe = [1, 2, 3];
        var manifest = Updater.Parse(System.Text.Encoding.UTF8.GetString(Manifest("1.3.0", exe)))!;
        var (http, web) = Web((Latest + "Strata.exe", HttpStatusCode.OK, exe));
        var io = new DeniedStageIO();
        var (staged, message) = await Updater.StageAsync(Repo, manifest, null, Path.GetTempPath(), http,
            stagingIO: io);
        Assert.Null(staged);
        Assert.Contains("nothing was installed", message);
        Assert.Empty(web.Asked);
        Assert.Equal(1, io.CleanupAttempts);
    }

    [Fact]
    public async Task Failed_web_verification_removes_its_bounded_partial_stage()
    {
        string root = Path.Combine(Path.GetTempPath(), "strata-web-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] expected = [1, 2, 3];
            var manifest = Updater.Parse(System.Text.Encoding.UTF8.GetString(Manifest("1.3.0", expected)))!;
            var (http, _) = Web((Latest + "Strata.exe", HttpStatusCode.OK, [1, 2, 4]));
            var (staged, _) = await Updater.StageAsync(Repo, manifest, null, root, http);
            Assert.Null(staged);
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
