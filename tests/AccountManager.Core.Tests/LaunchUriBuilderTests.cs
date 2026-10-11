using System.Net;
using AccountManager.Core.Models;
using AccountManager.Core.Roblox;

namespace AccountManager.Core.Tests;

public class LaunchUriBuilderTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public void Builds_place_launch()
    {
        string uri = LaunchUriBuilder.Build("TICKET", JoinTarget.ForPlace(123), "4567", Time);

        Assert.StartsWith("roblox-player:1+launchmode:play+gameinfo:TICKET+launchtime:1700000000000+placelauncherurl:", uri, StringComparison.Ordinal);
        Assert.Contains("+browsertrackerid:4567+", uri, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", uri, StringComparison.Ordinal);

        string placeLauncher = ExtractPlaceLauncher(uri);
        Assert.Equal("https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId=4567&placeId=123&isPlayTogetherGame=false", placeLauncher);
    }

    [Fact]
    public void Builds_server_launch()
    {
        string url = LaunchUriBuilder.BuildPlaceLauncherUrl(JoinTarget.ForServer(5, "abc-def"), "1");

        Assert.Contains("request=RequestGameJob", url, StringComparison.Ordinal);
        Assert.Contains("placeId=5", url, StringComparison.Ordinal);
        Assert.Contains("gameId=abc-def", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Builds_private_server_launch()
    {
        var target = new JoinTarget { Kind = JoinKind.PrivateServer, PlaceId = 9, LinkCode = "LINK" };

        string url = LaunchUriBuilder.BuildPlaceLauncherUrl(target, "1");

        Assert.Contains("request=RequestPrivateGame", url, StringComparison.Ordinal);
        Assert.Contains("linkCode=LINK", url, StringComparison.Ordinal);
        Assert.DoesNotContain("accessCode", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_server_requires_code()
    {
        var target = new JoinTarget { Kind = JoinKind.PrivateServer, PlaceId = 9 };

        Assert.Throws<ArgumentException>(() => LaunchUriBuilder.BuildPlaceLauncherUrl(target, "1"));
    }

    [Fact]
    public void Builds_follow_launch()
    {
        string url = LaunchUriBuilder.BuildPlaceLauncherUrl(JoinTarget.ForFollow(77), "1");

        Assert.Contains("request=RequestFollowUser", url, StringComparison.Ordinal);
        Assert.Contains("userId=77", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_target_opens_app_home()
    {
        string uri = LaunchUriBuilder.Build("T", JoinTarget.None, "1", Time);

        Assert.Contains("launchmode:app", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("placelauncherurl", uri, StringComparison.Ordinal);
    }

    private static string ExtractPlaceLauncher(string uri)
    {
        const string marker = "+placelauncherurl:";
        int start = uri.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = uri.IndexOf('+', start);
        return WebUtility.UrlDecode(uri[start..end]);
    }
}
