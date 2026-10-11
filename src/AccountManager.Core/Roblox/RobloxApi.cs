using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountManager.Core.Roblox;

public sealed record RobloxUser(long Id, string Name, string DisplayName);

public sealed record UserPresence(long UserId, PresenceType Type, string LastLocation, long PlaceId, string GameId);

public enum PresenceType
{
    Offline = 0,
    Online = 1,
    InGame = 2,
    InStudio = 3,
    Invisible = 4,
}

public sealed record PrivateServerInvite(long PlaceId, string LinkCode);

public sealed class RobloxApiException : Exception
{
    public RobloxApiException(string message, HttpStatusCode? status = null)
        : base(message)
    {
        Status = status;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>True when Roblox rejected the session cookie (expired, logged out or invalid).</summary>
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Thin client over the public Roblox web APIs used by the manager. Only talks to *.roblox.com.</summary>
public sealed class RobloxApi : IDisposable
{
    private const string UserAgent = "AccountManager/1.0 (+https://github.com/S4US/account-manager)";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public RobloxApi(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>Returns the user the cookie belongs to. Throws <see cref="RobloxApiException"/> with 401 when the cookie is invalid.</summary>
    public async Task<RobloxUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Create(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);
        AuthenticatedUserDto dto = await ReadJson<AuthenticatedUserDto>(response, ct).ConfigureAwait(false);
        return new RobloxUser(dto.Id, dto.Name ?? "", dto.DisplayName ?? dto.Name ?? "");
    }

    /// <summary>Requests a one-time authentication ticket used to sign a client launch into this account.</summary>
    public async Task<string> GetAuthenticationTicketAsync(string cookie, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendWithCsrfAsync(
            () =>
            {
                HttpRequestMessage request = Create(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/", cookie);
                request.Headers.Referrer = new Uri("https://www.roblox.com/");
                request.Content = JsonContent.Create(new { });
                return request;
            },
            ct).ConfigureAwait(false);

        if (response.Headers.TryGetValues("rbx-authentication-ticket", out IEnumerable<string>? values))
        {
            string? ticket = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(ticket))
            {
                return ticket;
            }
        }

        await EnsureSuccess(response, ct).ConfigureAwait(false);
        throw new RobloxApiException("Roblox did not return an authentication ticket.", response.StatusCode);
    }

    public async Task<RobloxUser?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users")
        {
            Content = JsonContent.Create(new { usernames = new[] { username }, excludeBannedUsers = true }),
        };
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);
        DataList<AuthenticatedUserDto> list = await ReadJson<DataList<AuthenticatedUserDto>>(response, ct).ConfigureAwait(false);
        AuthenticatedUserDto? dto = list.Data?.FirstOrDefault();
        return dto is null ? null : new RobloxUser(dto.Id, dto.Name ?? "", dto.DisplayName ?? "");
    }

    /// <summary>Returns headshot image URLs keyed by user id.</summary>
    public async Task<Dictionary<long, string>> GetAvatarHeadshotsAsync(IEnumerable<long> userIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (long[] chunk in userIds.Distinct().Chunk(100))
        {
            string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false";
            using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            DataList<ThumbnailDto> list = await ReadJson<DataList<ThumbnailDto>>(response, ct).ConfigureAwait(false);
            foreach (ThumbnailDto item in list.Data ?? [])
            {
                if (item.State == "Completed" && !string.IsNullOrEmpty(item.ImageUrl))
                {
                    result[item.TargetId] = item.ImageUrl;
                }
            }
        }

        return result;
    }

    /// <summary>Presence for several users, as seen by the account owning <paramref name="cookie"/>.</summary>
    public async Task<IReadOnlyList<UserPresence>> GetPresenceAsync(string cookie, IEnumerable<long> userIds, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendWithCsrfAsync(
            () =>
            {
                HttpRequestMessage request = Create(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie);
                request.Content = JsonContent.Create(new { userIds = userIds.Distinct().ToArray() });
                return request;
            },
            ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);
        PresenceListDto list = await ReadJson<PresenceListDto>(response, ct).ConfigureAwait(false);
        return (list.UserPresences ?? [])
            .Select(p => new UserPresence(p.UserId, (PresenceType)p.UserPresenceType, p.LastLocation ?? "", p.PlaceId ?? 0, p.GameId ?? ""))
            .ToList();
    }

    /// <summary>Returns place names keyed by place id. Requires a valid cookie.</summary>
    public async Task<Dictionary<long, string>> GetPlaceNamesAsync(string cookie, IEnumerable<long> placeIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (long[] chunk in placeIds.Where(id => id > 0).Distinct().Chunk(50))
        {
            string query = string.Join('&', chunk.Select(id => $"placeIds={id}"));
            using HttpRequestMessage request = Create(HttpMethod.Get, $"https://games.roblox.com/v1/games/multiget-place-details?{query}", cookie);
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            foreach (PlaceDetailsDto place in await ReadJson<List<PlaceDetailsDto>>(response, ct).ConfigureAwait(false))
            {
                result[place.PlaceId] = place.Name ?? "";
            }
        }

        return result;
    }

    public async Task<long?> GetRobuxAsync(string cookie, long userId, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Create(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{userId}/currency", cookie);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        CurrencyDto dto = await ReadJson<CurrencyDto>(response, ct).ConfigureAwait(false);
        return dto.Robux;
    }

    /// <summary>Resolves a roblox.com/share?code=…&amp;type=Server link into a place id and private server link code.</summary>
    public async Task<PrivateServerInvite?> ResolveServerShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendWithCsrfAsync(
            () =>
            {
                HttpRequestMessage request = Create(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie);
                request.Content = JsonContent.Create(new { linkId = shareCode, linkType = "Server" });
                return request;
            },
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        ShareLinkDto dto = await ReadJson<ShareLinkDto>(response, ct).ConfigureAwait(false);
        return dto.PrivateServerInviteData is { PlaceId: > 0 } invite && !string.IsNullOrEmpty(invite.LinkCode)
            ? new PrivateServerInvite(invite.PlaceId, invite.LinkCode)
            : null;
    }

    public void Dispose() => _http.Dispose();

    private static HttpRequestMessage Create(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
        return request;
    }

    /// <summary>
    /// Roblox rejects state-changing requests without an X-CSRF-TOKEN and returns the token to use in the 403 response.
    /// Sends once, and if challenged, resends with the provided token.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithCsrfAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using (HttpRequestMessage first = build())
        {
            HttpResponseMessage response = await _http.SendAsync(first, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Forbidden
                || !response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? tokens))
            {
                return response;
            }

            string token = tokens.First();
            response.Dispose();

            using HttpRequestMessage retry = build();
            retry.Headers.Add("X-CSRF-TOKEN", token);
            return await _http.SendAsync(retry, ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = "";
        try
        {
            detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }

        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The session has expired or was signed out. Sign in to this account again.",
            HttpStatusCode.TooManyRequests => "Roblox is rate limiting requests. Try again in a minute.",
            _ => $"Roblox returned {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
        if (detail.Length is > 0 and < 300 && response.StatusCode != HttpStatusCode.Unauthorized)
        {
            message += " " + detail;
        }

        throw new RobloxApiException(message, response.StatusCode);
    }

    private static async Task<T> ReadJson<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false)
            ?? throw new RobloxApiException("Roblox returned an empty response.", response.StatusCode);
    }

    private sealed class AuthenticatedUserDto
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public string? DisplayName { get; set; }
    }

    private sealed class DataList<T>
    {
        public List<T>? Data { get; set; }
    }

    private sealed class ThumbnailDto
    {
        public long TargetId { get; set; }

        public string? State { get; set; }

        public string? ImageUrl { get; set; }
    }

    private sealed class PresenceListDto
    {
        public List<PresenceDto>? UserPresences { get; set; }
    }

    private sealed class PresenceDto
    {
        public int UserPresenceType { get; set; }

        public string? LastLocation { get; set; }

        public long? PlaceId { get; set; }

        public string? GameId { get; set; }

        public long UserId { get; set; }
    }

    private sealed class PlaceDetailsDto
    {
        public long PlaceId { get; set; }

        public string? Name { get; set; }
    }

    private sealed class CurrencyDto
    {
        public long Robux { get; set; }
    }

    private sealed class ShareLinkDto
    {
        public InviteDto? PrivateServerInviteData { get; set; }
    }

    private sealed class InviteDto
    {
        public long PlaceId { get; set; }

        [JsonPropertyName("linkCode")]
        public string? LinkCode { get; set; }
    }
}
