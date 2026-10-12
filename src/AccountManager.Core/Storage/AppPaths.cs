namespace AccountManager.Core.Storage;

/// <summary>Locations of the app's data on disk.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AccountManager");
    }

    public string Root { get; }

    public string Database => Path.Combine(Root, "accounts.db");

    public string Vault => Path.Combine(Root, "vault.json");

    public string Settings => Path.Combine(Root, "settings.json");

    public string PerformancePreset => Path.Combine(Root, "performance-flags.json");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Downloaded app updates waiting to be installed.</summary>
    public string Updates => Path.Combine(Root, "updates");

    /// <summary>Per-account WebView2 profiles used for logging in and opening the website.</summary>
    public string BrowserProfiles => Path.Combine(Root, "browser");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(BrowserProfiles);
    }
}
