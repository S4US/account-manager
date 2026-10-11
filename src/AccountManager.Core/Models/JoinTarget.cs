using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Models;

public enum JoinKind
{
    None,
    Place,
    Server,
    PrivateServer,
    FollowUser,
}

/// <summary>Describes where a launched client should go.</summary>
public sealed partial record JoinTarget
{
    public static readonly JoinTarget None = new() { Kind = JoinKind.None };

    public JoinKind Kind { get; init; }

    public long PlaceId { get; init; }

    /// <summary>Specific public server instance (game job id).</summary>
    public string JobId { get; init; } = "";

    /// <summary>Private server link code (from a ?privateServerLinkCode= URL).</summary>
    public string LinkCode { get; init; } = "";

    /// <summary>Private server access code (a GUID, when already known).</summary>
    public string AccessCode { get; init; } = "";

    /// <summary>Unresolved share link code (from roblox.com/share?code=...&amp;type=Server).</summary>
    public string ShareCode { get; init; } = "";

    public long FollowUserId { get; init; }

    public string FollowUsername { get; init; } = "";

    [JsonIgnore]
    public bool IsEmpty => Kind == JoinKind.None;

    public static JoinTarget ForPlace(long placeId) => new() { Kind = JoinKind.Place, PlaceId = placeId };

    public static JoinTarget ForServer(long placeId, string jobId) => new() { Kind = JoinKind.Server, PlaceId = placeId, JobId = jobId };

    public static JoinTarget ForFollow(long userId, string username = "") => new() { Kind = JoinKind.FollowUser, FollowUserId = userId, FollowUsername = username };

    public override string ToString() => Kind switch
    {
        JoinKind.Place => $"Place {PlaceId}",
        JoinKind.Server => $"Place {PlaceId}, server {Shorten(JobId)}",
        JoinKind.PrivateServer => PlaceId > 0 ? $"Place {PlaceId}, private server" : "Private server",
        JoinKind.FollowUser => string.IsNullOrEmpty(FollowUsername) ? $"Follow user {FollowUserId}" : $"Follow {FollowUsername}",
        _ => "Not set",
    };

    private static string Shorten(string value) => value.Length > 8 ? value[..8] + "…" : value;

    /// <summary>
    /// Parses user input: a bare place id, a game URL, a private server URL, a share link,
    /// or "placeId jobId". Returns null when the input is not understood.
    /// </summary>
    public static JoinTarget? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string text = input.Trim();

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long bareId) && bareId > 0)
        {
            return ForPlace(bareId);
        }

        // "placeId jobId" or "placeId/jobId"
        Match pair = PlaceAndJobRegex().Match(text);
        if (pair.Success)
        {
            return ForServer(long.Parse(pair.Groups[1].Value, CultureInfo.InvariantCulture), pair.Groups[2].Value.ToLowerInvariant());
        }

        Match share = ShareCodeRegex().Match(text);
        if (share.Success)
        {
            return new JoinTarget { Kind = JoinKind.PrivateServer, ShareCode = share.Groups[1].Value };
        }

        Match game = GameUrlRegex().Match(text);
        if (game.Success)
        {
            long placeId = long.Parse(game.Groups[1].Value, CultureInfo.InvariantCulture);

            Match link = LinkCodeRegex().Match(text);
            if (link.Success)
            {
                return new JoinTarget { Kind = JoinKind.PrivateServer, PlaceId = placeId, LinkCode = link.Groups[1].Value };
            }

            Match job = GameInstanceRegex().Match(text);
            if (job.Success)
            {
                return ForServer(placeId, job.Groups[1].Value.ToLowerInvariant());
            }

            return ForPlace(placeId);
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+)[\s/]+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex PlaceAndJobRegex();

    [GeneratedRegex(@"/share\?(?:[^#]*&)?code=([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ShareCodeRegex();

    [GeneratedRegex(@"roblox\.com/(?:[a-z]{2}/)?games/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GameUrlRegex();

    [GeneratedRegex(@"privateServerLinkCode=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkCodeRegex();

    [GeneratedRegex(@"gameInstanceId=([0-9a-fA-F-]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex GameInstanceRegex();
}
