using System.Net.Http.Json;

namespace AccountManager.Core.Services;

/// <summary>Posts optional status messages to a Discord webhook the user configured. Sends account names and events only.</summary>
public sealed class DiscordNotifier : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<string> _webhookUrl;

    public DiscordNotifier(Func<string> webhookUrl)
    {
        _webhookUrl = webhookUrl;
    }

    public static bool IsValidWebhookUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    public async Task<bool> SendAsync(string title, string description, int color, CancellationToken ct = default)
    {
        string url = _webhookUrl();
        if (!IsValidWebhookUrl(url))
        {
            return false;
        }

        try
        {
            var payload = new
            {
                username = "Account manager",
                embeds = new[] { new { title, description, color, timestamp = DateTime.UtcNow.ToString("O") } },
            };
            using HttpResponseMessage response = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
