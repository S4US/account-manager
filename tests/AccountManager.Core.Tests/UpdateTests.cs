using System.Net;
using System.Security.Cryptography;
using System.Text;
using AccountManager.Core.Updates;

namespace AccountManager.Core.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string ExeUrl = "https://github.com/S4US/account-manager/releases/download/v0.2.0/AccountManager.exe";
    private const string SumUrl = "https://github.com/S4US/account-manager/releases/download/v0.2.0/AccountManager.exe.sha256";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("new version of the app");
    private readonly string _dir = Directory.CreateTempSubdirectory("am-update").FullName;

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("V0.10.0", "0.10.0.0")]
    public void Parses_release_tags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.x")]
    public void Rejects_non_version_tags(string tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    [Fact]
    public void Reads_checksum_files_with_or_without_a_file_name()
    {
        string hash = new('a', 64);
        Assert.Equal(hash, UpdateService.ParseChecksum(hash));
        Assert.Equal(hash, UpdateService.ParseChecksum(hash.ToUpperInvariant() + "  AccountManager.exe\n"));
        Assert.Null(UpdateService.ParseChecksum("not a hash"));
    }

    [Theory]
    [InlineData("https://github.com/S4US/account-manager/releases/download/v1/AccountManager.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("http://github.com/S4US/account-manager/releases/download/v1/AccountManager.exe", false)]
    [InlineData("https://evil.example.com/AccountManager.exe", false)]
    [InlineData("https://github.com.evil.example/AccountManager.exe", false)]
    public void Only_trusts_https_github_downloads(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateService.IsTrustedDownloadUrl(url));
    }

    [Fact]
    public async Task Finds_a_newer_release()
    {
        using var service = Service(Release("v0.2.0"));
        UpdateInfo? update = await service.CheckAsync(new Version(0, 1, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0, 0), update.Version);
        Assert.Equal(ExeUrl, update.ExeUrl);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    public async Task Ignores_the_same_or_an_older_release(string tag)
    {
        using var service = Service(Release(tag));
        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task Ignores_a_release_without_a_checksum()
    {
        string json = $$"""{ "tag_name": "v0.2.0", "assets": [ { "name": "AccountManager.exe", "browser_download_url": "{{ExeUrl}}", "size": 10 } ] }""";
        using var service = Service(json);
        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task No_release_yet_is_not_an_error()
    {
        using var service = new UpdateService(_dir, handler: new FakeGitHub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task Keeps_a_download_whose_checksum_matches()
    {
        using var service = Service(Release("v0.2.0"), checksum: Sha256(Payload));
        UpdateInfo update = (await service.CheckAsync(new Version(0, 1, 0)))!;

        string path = await service.DownloadAsync(update);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Discards_a_download_whose_checksum_differs()
    {
        using var service = Service(Release("v0.2.0"), checksum: new string('0', 64));
        UpdateInfo update = (await service.CheckAsync(new Version(0, 1, 0)))!;

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(update));
        Assert.Empty(Directory.GetFiles(_dir, "*.exe"));
    }

    private UpdateService Service(string releaseJson, string? checksum = null) =>
        new(_dir, handler: new FakeGitHub(request => request.RequestUri!.ToString() switch
        {
            var u when u.Contains("api.github.com") => Text(releaseJson),
            SumUrl => Text((checksum ?? Sha256(Payload)) + "  AccountManager.exe"),
            ExeUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

    private static string Release(string tag) => $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/S4US/account-manager/releases/tag/{{tag}}", "body": "Notes",
          "assets": [
            { "name": "AccountManager.exe", "browser_download_url": "{{ExeUrl}}", "size": {{Payload.Length}} },
            { "name": "AccountManager.exe.sha256", "browser_download_url": "{{SumUrl}}", "size": 90 } ] }
        """;

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("am-install").FullName;

    [Fact]
    public void Swaps_in_the_new_exe_and_cleans_up_the_old_one()
    {
        string current = Path.Combine(_dir, "AccountManager.exe");
        string downloaded = Path.Combine(_dir, "AccountManager-0.2.0.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(downloaded, "new");

        UpdateInstaller.Install(current, downloaded);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.True(File.Exists(current + ".old"));

        UpdateInstaller.CleanUpPrevious(current);
        Assert.False(File.Exists(current + ".old"));
    }

    [Fact]
    public void Puts_the_original_back_when_the_new_exe_is_missing()
    {
        string current = Path.Combine(_dir, "AccountManager.exe");
        File.WriteAllText(current, "old");

        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(current, Path.Combine(_dir, "missing.exe")));

        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".old"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
