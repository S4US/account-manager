namespace AccountManager.Core.Monitoring;

public enum LogEvent
{
    None,

    /// <summary>The client lost its game session (kick, disconnect, failed teleport, auth failure).</summary>
    Disconnected,

    /// <summary>The player left the game normally (back to the home screen) or is teleporting.</summary>
    LeftGame,
}

/// <summary>Classifies Roblox client log lines into session events.</summary>
public static class LogLineClassifier
{
    // Error 285 is "the client chose to leave", which is not a failure.
    private const string UserLeftCode = "285";

    private static readonly string[] Ignored =
    [
        "FLog::FastFlag", "FFlag", "DFFlag", "DFInt", "FlagFetching", "Denied local configuration",
        "[DFLog::SignalRCoreError]", "[DFLog::WebSocketTraceError]", "realtime-signalr.roblox.com",
    ];

    private static readonly string[] LeftGameMarkers = ["leaveUGCGame", "Initiating teleport"];

    private static readonly string[] DisconnectMarkers =
    [
        "Authentication Failed",
        "Error Code: 268", // kicked for unexpected client behaviour
        "Error Code: 277", // lost connection
        "Error Code: 279", // failed to connect
        "Global Error:",
        "Teleport failed because",
        "Client has been disconnected with reason:",
        "Lost connection with reason",
    ];

    public static LogEvent Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || ContainsAny(line, Ignored))
        {
            return LogEvent.None;
        }

        if (ContainsAny(line, LeftGameMarkers))
        {
            return LogEvent.LeftGame;
        }

        if (IsDeliberateLeave(line))
        {
            return LogEvent.None;
        }

        if (ContainsAny(line, DisconnectMarkers))
        {
            return LogEvent.Disconnected;
        }

        if (Contains(line, "Kick") && !Contains(line, "KickCommand"))
        {
            return LogEvent.Disconnected;
        }

        if (Contains(line, "Disconnect reason received:")
            || Contains(line, "Disconnection Notification. Reason:")
            || Contains(line, "Disconnected from server for reason:"))
        {
            return line.Contains(UserLeftCode, StringComparison.Ordinal) ? LogEvent.None : LogEvent.Disconnected;
        }

        if (Contains(line, "Connection lost: connectMode:"))
        {
            return Contains(line, "Disconnect ASAP") ? LogEvent.None : LogEvent.Disconnected;
        }

        return LogEvent.None;
    }

    /// <summary>Lines the client writes while tearing down a session the player chose to end.</summary>
    private static bool IsDeliberateLeave(string line) =>
        Contains(line, "Client:Disconnect")
        || Contains(line, "Sending disconnect with reason: " + UserLeftCode)
        || Contains(line, "Time to disconnect")
        || Contains(line, "MegaReplicatorLogDisconnectCleanUpLog");

    private static bool Contains(string line, string value) => line.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string line, string[] values)
    {
        foreach (string value in values)
        {
            if (line.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The human-readable part of a log line (text after the last ']').</summary>
    public static string Describe(string line)
    {
        int bracket = line.LastIndexOf(']');
        string text = bracket >= 0 && bracket < line.Length - 1 ? line[(bracket + 1)..] : line;
        text = text.Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
