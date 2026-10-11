using AccountManager.Core.Models;
using AccountManager.Core.Roblox;

namespace AccountManager.Core.Tests;

public class JoinTargetTests
{
    [Theory]
    [InlineData("920587237")]
    [InlineData("https://www.roblox.com/games/920587237/Adopt-Me")]
    [InlineData("https://www.roblox.com/de/games/920587237")]
    [InlineData("  roblox.com/games/920587237?sortFilter=1  ")]
    public void Parses_place(string input)
    {
        JoinTarget? target = JoinTarget.Parse(input);

        Assert.NotNull(target);
        Assert.Equal(JoinKind.Place, target.Kind);
        Assert.Equal(920587237, target.PlaceId);
    }

    [Fact]
    public void Parses_private_server_link()
    {
        JoinTarget? target = JoinTarget.Parse("https://www.roblox.com/games/606849621/Jailbreak?privateServerLinkCode=12345678901234567890");

        Assert.NotNull(target);
        Assert.Equal(JoinKind.PrivateServer, target.Kind);
        Assert.Equal(606849621, target.PlaceId);
        Assert.Equal("12345678901234567890", target.LinkCode);
    }

    [Fact]
    public void Parses_share_link()
    {
        JoinTarget? target = JoinTarget.Parse("https://www.roblox.com/share?code=a1b2c3d4e5f6&type=Server");

        Assert.NotNull(target);
        Assert.Equal(JoinKind.PrivateServer, target.Kind);
        Assert.Equal("a1b2c3d4e5f6", target.ShareCode);
    }

    [Theory]
    [InlineData("1818 0b7c4f5e-9d2a-4c1b-8e3f-123456789abc")]
    [InlineData("1818/0B7C4F5E-9D2A-4C1B-8E3F-123456789ABC")]
    [InlineData("https://www.roblox.com/games/1818/Classic?gameInstanceId=0b7c4f5e-9d2a-4c1b-8e3f-123456789abc")]
    public void Parses_specific_server(string input)
    {
        JoinTarget? target = JoinTarget.Parse(input);

        Assert.NotNull(target);
        Assert.Equal(JoinKind.Server, target.Kind);
        Assert.Equal(1818, target.PlaceId);
        Assert.Equal("0b7c4f5e-9d2a-4c1b-8e3f-123456789abc", target.JobId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a game")]
    [InlineData("https://example.com/games/123")]
    [InlineData("-5")]
    public void Rejects_unrecognised_input(string input)
    {
        Assert.Null(JoinTarget.Parse(input));
    }

    [Fact]
    public void Share_code_query_on_game_url_is_not_a_share_link()
    {
        JoinTarget? target = JoinTarget.Parse("https://www.roblox.com/games/123?code=abc");

        Assert.NotNull(target);
        Assert.Equal(JoinKind.Place, target.Kind);
    }
}

public class SessionCookieTests
{
    private const string Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_ABCDEF0123456789ABCDEF0123456789";

    [Theory]
    [InlineData(Cookie)]
    [InlineData(".ROBLOSECURITY=" + Cookie)]
    [InlineData(".ROBLOSECURITY=" + Cookie + "; domain=.roblox.com; path=/")]
    [InlineData("\"" + Cookie + "\"")]
    public void Normalizes_common_forms(string raw)
    {
        Assert.Equal(Cookie, SessionCookie.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("_|WARNING:-DO-NOT-SHARE-THIS.")]
    public void Rejects_non_cookies(string? raw)
    {
        Assert.Null(SessionCookie.Normalize(raw));
    }

    [Fact]
    public void Parses_many_and_deduplicates()
    {
        string other = Cookie.Replace("ABCDEF", "FEDCBA", StringComparison.Ordinal);
        string text = $"{Cookie}\r\n\r\njunk line\n.ROBLOSECURITY={other}\n{Cookie}";

        IReadOnlyList<string> result = SessionCookie.ParseMany(text);

        Assert.Equal([Cookie, other], result);
    }
}
