namespace AccountManager.Core.Models;

/// <summary>User preferences. Persisted as JSON by <see cref="Storage.SettingsStore"/>.</summary>
public sealed class AppSettings
{
    // Launching
    public bool MultiInstance { get; set; } = true;

    /// <summary>Also close Roblox's singleton handles inside running clients if holding them is not enough.</summary>
    public bool MultiInstanceHandleFallback { get; set; } = true;

    /// <summary>Seconds to wait between clients when launching several accounts.</summary>
    public int LaunchStaggerSeconds { get; set; } = 12;

    /// <summary>Global FPS cap applied to accounts without their own. 0 = leave Roblox's setting alone.</summary>
    public int DefaultFpsCap { get; set; }

    public bool MinimizeAfterLaunch { get; set; }

    public bool RenameWindows { get; set; } = true;

    /// <summary>Pause launches while free RAM is below this many MB. 0 = disabled.</summary>
    public int LowMemoryThrottleMb { get; set; } = 1024;

    // Per-process tuning
    public bool LowPriority { get; set; } = true;

    public bool EfficiencyMode { get; set; } = true;

    public bool SpreadCpuAffinity { get; set; }

    /// <summary>Working set cap for every client in MB. 0 = disabled. Per-account limits take precedence.</summary>
    public int GlobalRamLimitMb { get; set; }

    public bool AutoMute { get; set; } = true;

    // Memory maintenance
    public bool PeriodicTrim { get; set; } = true;

    public int TrimIntervalMinutes { get; set; } = 10;

    /// <summary>Also purge the standby list (requires administrator).</summary>
    public bool PurgeStandbyList { get; set; }

    public bool CleanOldRobloxVersions { get; set; } = true;

    // Reliability
    public bool AutoRestartEnabled { get; set; } = true;

    /// <summary>Treat leaving to the Roblox home screen as normal instead of a disconnect.</summary>
    public bool AllowLeavingGame { get; set; } = true;

    // Anti-AFK
    public int AntiAfkIntervalMinutes { get; set; } = 15;

    public bool AntiAfkRestoreMinimized { get; set; } = true;

    // Remote panel
    public bool RemoteEnabled { get; set; }

    public int RemotePort { get; set; } = 5280;

    public bool RemoteAllowLan { get; set; }

    public string RemoteToken { get; set; } = "";

    // Notifications
    public string DiscordWebhookUrl { get; set; } = "";

    public bool NotifyOnCrash { get; set; } = true;

    // Interface
    public bool StreamerMode { get; set; }

    public bool ConfirmKillAll { get; set; } = true;
}
