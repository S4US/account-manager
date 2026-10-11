using System.Globalization;
using System.Net;
using AccountManager.Core.Models;

namespace AccountManager.Core.Roblox;

/// <summary>Builds the roblox-player: protocol URI the desktop client accepts as its launch argument.</summary>
public static class LaunchUriBuilder
{
    private const string PlaceLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    public static string BuildPlaceLauncherUrl(JoinTarget target, string browserTrackerId)
    {
        string tracker = Uri.EscapeDataString(browserTrackerId);
        return target.Kind switch
        {
            JoinKind.Place =>
                $"{PlaceLauncher}?request=RequestGame&browserTrackerId={tracker}&placeId={Id(target.PlaceId)}&isPlayTogetherGame=false",
            JoinKind.Server =>
                $"{PlaceLauncher}?request=RequestGameJob&browserTrackerId={tracker}&placeId={Id(target.PlaceId)}&gameId={Uri.EscapeDataString(target.JobId)}&isPlayTogetherGame=false",
            JoinKind.PrivateServer => BuildPrivateServer(target, tracker),
            JoinKind.FollowUser =>
                $"{PlaceLauncher}?request=RequestFollowUser&browserTrackerId={tracker}&userId={Id(target.FollowUserId)}&isPlayTogetherGame=false",
            _ => throw new ArgumentException("A join target is required to build a place launcher URL.", nameof(target)),
        };
    }

    /// <summary>
    /// Builds the full launch URI. With an empty target the client opens to its home screen instead of joining a game.
    /// </summary>
    public static string Build(string ticket, JoinTarget target, string browserTrackerId, DateTimeOffset launchTime, string locale = "en_us")
    {
        ArgumentException.ThrowIfNullOrEmpty(ticket);
        ArgumentException.ThrowIfNullOrEmpty(browserTrackerId);

        long time = launchTime.ToUnixTimeMilliseconds();
        if (target.IsEmpty)
        {
            return $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{time}+browsertrackerid:{browserTrackerId}" +
                   $"+robloxLocale:{locale}+gameLocale:{locale}+channel:+LaunchExp:InApp";
        }

        string placeLauncher = WebUtility.UrlEncode(BuildPlaceLauncherUrl(target, browserTrackerId));
        return $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{time}+placelauncherurl:{placeLauncher}" +
               $"+browsertrackerid:{browserTrackerId}+robloxLocale:{locale}+gameLocale:{locale}+channel:+LaunchExp:InBrowser";
    }

    private static string BuildPrivateServer(JoinTarget target, string tracker)
    {
        if (target.PlaceId <= 0 || (string.IsNullOrEmpty(target.LinkCode) && string.IsNullOrEmpty(target.AccessCode)))
        {
            throw new ArgumentException("A private server needs a place id and a link code or access code.", nameof(target));
        }

        string url = $"{PlaceLauncher}?request=RequestPrivateGame&browserTrackerId={tracker}&placeId={Id(target.PlaceId)}";
        if (!string.IsNullOrEmpty(target.AccessCode))
        {
            url += "&accessCode=" + Uri.EscapeDataString(target.AccessCode);
        }

        if (!string.IsNullOrEmpty(target.LinkCode))
        {
            url += "&linkCode=" + Uri.EscapeDataString(target.LinkCode);
        }

        return url;
    }

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
