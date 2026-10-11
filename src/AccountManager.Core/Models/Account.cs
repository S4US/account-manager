namespace AccountManager.Core.Models;

/// <summary>A stored Roblox account. The session cookie is kept encrypted at rest.</summary>
public sealed class Account
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Optional label chosen by the user, shown instead of the username when set.</summary>
    public string Alias { get; set; } = "";

    public string Notes { get; set; } = "";

    /// <summary>Comma separated free-form tags.</summary>
    public string Tags { get; set; } = "";

    public bool IsFavorite { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Random per-account id Roblox uses to correlate launches. Also used to find the account's process.</summary>
    public string BrowserTrackerId { get; set; } = "";

    /// <summary>Encrypted .ROBLOSECURITY value (see <see cref="Security.Vault"/>).</summary>
    public string EncryptedCookie { get; set; } = "";

    public string AvatarUrl { get; set; } = "";

    /// <summary>Where this account joins when launched without an explicit target.</summary>
    public JoinTarget DefaultTarget { get; set; } = JoinTarget.None;

    /// <summary>Per-account FPS cap. 0 = use the global default.</summary>
    public int FpsCap { get; set; }

    /// <summary>Apply the low-graphics FFlag preset when launching this account.</summary>
    public bool UsePerformancePreset { get; set; } = true;

    public bool AutoRestart { get; set; }

    public bool AntiAfk { get; set; }

    /// <summary>Per-account working set cap in MB. 0 = use the global setting.</summary>
    public int RamLimitMb { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLaunchedAt { get; set; }

    public DateTime? CookieValidatedAt { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Alias) ? Username : Alias;

    public static string NewBrowserTrackerId()
    {
        // Roblox tracker ids are numeric; 13 digits keeps them in the same shape as browser-issued ones.
        return Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
    }
}
