using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Updates;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string ExeUrl, long ExeSize, string ChecksumUrl);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Finds and downloads new versions from the repository's GitHub releases.
///
/// A release is used when its tag is a version ("v1.2.3") newer than the running one and it has two assets:
/// <c>AccountManager.exe</c> and <c>AccountManager.exe.sha256</c>. The exe is only kept if its SHA-256 matches.
/// Everything goes over HTTPS, and downloads are only accepted from GitHub's own hosts.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    public const string ExeAssetName = "AccountManager.exe";
    public const string ChecksumAssetName = ExeAssetName + ".sha256";
    private const long MaxDownloadBytes = 300L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _downloadDirectory;

    public UpdateService(string downloadDirectory, string repository = "S4US/account-manager", HttpMessageHandler? handler = null)
    {
        _downloadDirectory = downloadDirectory;
        _repository = repository;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AccountManager-Updater (+https://github.com/" + repository + ")");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string ReleasesPage => $"https://github.com/{_repository}/releases";

    /// <summary>Returns the latest release if it is newer than <paramref name="current"/> and installable, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        Release release;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No published release yet.
                return null;
            }

            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync<Release>(ct).ConfigureAwait(false)
                ?? throw new UpdateException("GitHub returned an empty release.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't reach GitHub to check for updates.", ex);
        }

        if (release.Draft || release.Prerelease || ParseVersion(release.TagName) is not { } version || version <= Normalize(current))
        {
            return null;
        }

        ReleaseAsset? exe = release.Assets?.FirstOrDefault(a => a.Name == ExeAssetName);
        ReleaseAsset? checksum = release.Assets?.FirstOrDefault(a => a.Name == ChecksumAssetName);
        if (exe is null || checksum is null || !IsTrustedDownloadUrl(exe.DownloadUrl) || !IsTrustedDownloadUrl(checksum.DownloadUrl))
        {
            // A release without the expected files can't be installed automatically.
            return null;
        }

        return new UpdateInfo(version, release.TagName ?? "", release.Body ?? "", release.HtmlUrl ?? ReleasesPage, exe.DownloadUrl!, exe.Size, checksum.DownloadUrl!);
    }

    /// <summary>
    /// Downloads the update and verifies it against the published checksum. Returns the path of the verified exe.
    /// A verified file from an earlier download is reused.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_downloadDirectory);
        string target = Path.Combine(_downloadDirectory, $"AccountManager-{update.Version}.exe");

        string expected = await DownloadChecksumAsync(update, ct).ConfigureAwait(false);
        if (File.Exists(target) && string.Equals(await HashFileAsync(target, ct).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        if (update.ExeSize > MaxDownloadBytes)
        {
            throw new UpdateException("The update is unexpectedly large and was not downloaded.");
        }

        string partial = target + ".part";
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureTrustedFinalUrl(response);

            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        throw new UpdateException("The update is unexpectedly large and was not downloaded.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            string actual = await HashFileAsync(partial, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("The downloaded update doesn't match its published checksum, so it was discarded.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't download the update.", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Removes downloaded updates other than <paramref name="keep"/>.</summary>
    public void CleanDownloads(string? keep = null)
    {
        if (!Directory.Exists(_downloadDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_downloadDirectory))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>"v1.2.3", "1.2.3" or "v1.2" → a version with build and revision filled in; anything else → null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out Version? version) ? Normalize(version) : null;
    }

    /// <summary>Reads a checksum file: either just the hash or "hash  filename" (sha256sum style).</summary>
    public static string? ParseChecksum(string? text)
    {
        Match match = HashRegex().Match(text ?? "");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>Only HTTPS downloads from GitHub itself are accepted.</summary>
    public static bool IsTrustedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private async Task<string> DownloadChecksumAsync(UpdateInfo update, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(update.ChecksumUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureTrustedFinalUrl(response);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseChecksum(text) ?? throw new UpdateException("The release's checksum file is not readable.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't download the update's checksum.", ex);
        }
    }

    private static void EnsureTrustedFinalUrl(HttpResponseMessage response)
    {
        // Redirects are followed; make sure they ended on GitHub too.
        if (!IsTrustedDownloadUrl(response.RequestMessage?.RequestUri?.ToString()))
        {
            throw new UpdateException("The update download was redirected somewhere other than GitHub, so it was refused.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex HashRegex();

    private sealed class Release
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<ReleaseAsset>? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
