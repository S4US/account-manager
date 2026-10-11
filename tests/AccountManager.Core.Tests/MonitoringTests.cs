using AccountManager.Core.Monitoring;

namespace AccountManager.Core.Tests;

public class LogLineClassifierTests
{
    private const string Prefix = "2026-10-11T12:13:29.154Z,0.154990,c2c0,6,Warning ";

    [Theory]
    [InlineData("[FLog::Network] Disconnect reason received: 277")]
    [InlineData("[FLog::Network] Client has been disconnected with reason: Kicked")]
    [InlineData("[FLog::Network] Lost connection with reason : Lost connection to the game server")]
    [InlineData("[FLog::Network] Connection lost: connectMode: Normal")]
    [InlineData("[FLog::Error] Error Code: 279")]
    [InlineData("[FLog::Output] You were kicked from this experience")]
    [InlineData("[FLog::Output] Teleport failed because the server is full")]
    [InlineData("[FLog::Output] Authentication Failed")]
    [InlineData("[FLog::Network] Disconnected from server for reason: ID_CONNECTION_LOST")]
    public void Detects_disconnects(string line)
    {
        Assert.Equal(LogEvent.Disconnected, LogLineClassifier.Classify(Prefix + line));
    }

    [Theory]
    [InlineData("[FLog::Network] Disconnect reason received: 285")]
    [InlineData("[FLog::Network] Disconnection Notification. Reason: 285")]
    [InlineData("[FLog::Network] Disconnected from server for reason: Player: 285")]
    [InlineData("[FLog::Network] Connection lost: connectMode: Disconnect ASAP")]
    [InlineData("[FLog::Network] Sending disconnect with reason: 285")]
    [InlineData("[FLog::Output] KickCommand registered")]
    [InlineData("[DFLog::SignalRCoreError] Connection lost to realtime-signalr.roblox.com/userhub")]
    [InlineData("[FLog::FastFlag] DFIntConnectionLost=1")]
    [InlineData("[FLog::Output] Something unrelated happened")]
    [InlineData("")]
    public void Ignores_benign_lines(string line)
    {
        Assert.Equal(LogEvent.None, LogLineClassifier.Classify(Prefix + line));
    }

    [Theory]
    [InlineData("[FLog::GameJoinUtil] leaveUGCGame called")]
    [InlineData("[FLog::SingleSurfaceApp] Initiating teleport to place 123")]
    public void Detects_leaving(string line)
    {
        Assert.Equal(LogEvent.LeftGame, LogLineClassifier.Classify(Prefix + line));
    }

    [Fact]
    public void Describe_returns_text_after_last_bracket()
    {
        Assert.Equal("Disconnect reason received: 277", LogLineClassifier.Describe(Prefix + "[FLog::Network] Disconnect reason received: 277"));
    }
}

public class CrashCircuitBreakerTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private CrashCircuitBreaker Create() => new(() => _now);

    [Fact]
    public void Backs_off_then_opens_after_threshold()
    {
        CrashCircuitBreaker breaker = Create();

        RestartDecision first = breaker.RecordCrash(1);
        _now += TimeSpan.FromSeconds(30);
        RestartDecision second = breaker.RecordCrash(1);
        _now += TimeSpan.FromSeconds(30);
        RestartDecision third = breaker.RecordCrash(1);

        Assert.True(first.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(15), first.Delay);
        Assert.True(second.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(27), second.Delay);
        Assert.False(third.Allowed);
        Assert.True(breaker.IsOpen(1));
        Assert.Equal(TimeSpan.FromMinutes(3), third.Delay);
    }

    [Fact]
    public void Stable_period_resets_count()
    {
        CrashCircuitBreaker breaker = Create();
        breaker.RecordCrash(1);
        breaker.RecordCrash(1);

        _now += TimeSpan.FromMinutes(6);
        RestartDecision next = breaker.RecordCrash(1);

        Assert.True(next.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(15), next.Delay);
    }

    [Fact]
    public void Accounts_are_independent()
    {
        CrashCircuitBreaker breaker = Create();
        breaker.RecordCrash(1);
        breaker.RecordCrash(1);
        breaker.RecordCrash(1);

        Assert.True(breaker.IsOpen(1));
        Assert.False(breaker.IsOpen(2));
        Assert.True(breaker.RecordCrash(2).Allowed);
    }

    [Fact]
    public void Open_period_grows_and_is_capped()
    {
        CrashCircuitBreaker breaker = Create();
        TimeSpan last = TimeSpan.Zero;
        for (int i = 0; i < 20; i++)
        {
            last = breaker.RecordCrash(1).Delay;
        }

        Assert.Equal(TimeSpan.FromMinutes(30), last);
    }

    [Fact]
    public void Backoff_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Create().Backoff(50));
    }

    [Fact]
    public void Reset_clears_state()
    {
        CrashCircuitBreaker breaker = Create();
        for (int i = 0; i < 3; i++)
        {
            breaker.RecordCrash(1);
        }

        breaker.Reset(1);

        Assert.False(breaker.IsOpen(1));
    }
}

public class RobloxLogWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("am-logs").FullName;

    [Fact]
    public void Parses_start_time_from_file_name()
    {
        DateTime? time = RobloxLogWatcher.ParseStartTime("0.742.0.7421053_20261011T121329Z_Player_AFFA5_last.log");

        Assert.Equal(new DateTime(2026, 10, 11, 12, 13, 29, DateTimeKind.Utc), time);
        Assert.Equal(DateTimeKind.Utc, time!.Value.Kind);
    }

    [Fact]
    public void Finds_closest_player_log_and_skips_crash_handler_and_claimed()
    {
        Touch("0.742_20261011T121300Z_Player_AAAAA_last.log");
        Touch("0.742_20261011T121330Z_Player_BBBBB_last.log");
        Touch("0.742_20261011T121330Z_Player_CCCCC_CrashHandler_last.log");
        Touch("0.742_20261011T100000Z_Player_DDDDD_last.log");
        string claimed = Touch("0.742_20261011T121328Z_Player_EEEEE_last.log");

        var started = new DateTime(2026, 10, 11, 12, 13, 31, DateTimeKind.Utc);
        List<string> names = RobloxLogWatcher.FindCandidates(_dir, started, new HashSet<string> { claimed }).Select(f => f.Name).ToList();

        Assert.Equal(["0.742_20261011T121330Z_Player_BBBBB_last.log", "0.742_20261011T121300Z_Player_AAAAA_last.log"], names);
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
