using System.Text.Json.Nodes;
using AccountManager.Core.Models;
using AccountManager.Core.Processes;
using AccountManager.Core.Remote;
using AccountManager.Core.Roblox;
using AccountManager.Core.Security;
using AccountManager.Core.Services;
using AccountManager.Core.Storage;

namespace AccountManager.App.Services;

/// <summary>Owns the app's long-lived services. Created once the vault is unlocked.</summary>
public sealed class AppHost : IDisposable
{
    private JsonObject? _preset;

    public AppHost(AppPaths paths, SettingsStore settings, Vault vault)
    {
        Paths = paths;
        Settings = settings;
        Vault = vault;
        Log = new ActivityLog(paths.Logs);
        Api = new RobloxApi();
        Store = new AccountStore(paths.Database);
        Store.Initialize();
        Accounts = new AccountService(Store, vault, Api);
        Discord = new DiscordNotifier(() => Settings.Current.DiscordWebhookUrl);
        LoadPreset();
        Launcher = new RobloxLauncher(Api, () => Settings.Current, () => _preset, Log);
        Instances = new InstanceManager(Accounts, Launcher, settings, Log, Discord);
        Scheduler = new ShutdownScheduler(() => Instances.KillAllRoblox(), Log);
        RemoteController = new RemoteController(Accounts, Instances, Scheduler, () => Settings.Current.StreamerMode);
    }

    public static AppHost Current { get; set; } = null!;

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public Vault Vault { get; }

    public ActivityLog Log { get; }

    public RobloxApi Api { get; }

    public AccountStore Store { get; }

    public AccountService Accounts { get; }

    public DiscordNotifier Discord { get; }

    public RobloxLauncher Launcher { get; }

    public InstanceManager Instances { get; }

    public ShutdownScheduler Scheduler { get; }

    public RemoteController RemoteController { get; }

    public RemoteServer? Remote { get; private set; }

    public string PresetJson => File.Exists(Paths.PerformancePreset) ? File.ReadAllText(Paths.PerformancePreset) : ClientSettingsWriter.DefaultPresetJson;

    /// <summary>Validates and saves the graphics preset. Returns an error message, or null on success.</summary>
    public string? SavePreset(string json)
    {
        (JsonObject? preset, string? error) = ClientSettingsWriter.ParsePreset(json);
        if (preset is null)
        {
            return error;
        }

        File.WriteAllText(Paths.PerformancePreset, json);
        _preset = preset;
        return null;
    }

    public void ResetPreset()
    {
        File.WriteAllText(Paths.PerformancePreset, ClientSettingsWriter.DefaultPresetJson);
        LoadPreset();
    }

    /// <summary>Starts work that shouldn't delay the window: adopting running clients, avatars, cleanup.</summary>
    public async Task StartBackgroundAsync()
    {
        Accounts.Load();
        AppSettings settings = Settings.Current;
        ApplyRemoteSettings();

        await Task.Run(() =>
        {
            // A cap left over from a previous session (the app closed while clients ran) is undone here.
            try
            {
                RestoreFramerateIfNoClients();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("Couldn't restore your Roblox frame rate setting: " + ex.Message);
            }

            Instances.AdoptRunningClients();
            if (settings.CleanOldRobloxVersions)
            {
                RobloxInstallation.CleanOldVersions(message => Log.Info(message));
            }
        }).ConfigureAwait(false);

        try
        {
            await Accounts.RefreshAvatarsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RobloxApiException)
        {
            Log.Warning("Couldn't refresh avatars: " + ex.Message);
        }
    }

    /// <summary>Starts, restarts or stops the remote panel to match the current settings.</summary>
    public string? ApplyRemoteSettings()
    {
        AppSettings settings = Settings.Current;
        Remote?.Dispose();
        Remote = null;
        if (!settings.RemoteEnabled)
        {
            return null;
        }

        if (string.IsNullOrEmpty(settings.RemoteToken))
        {
            settings.RemoteToken = RemoteServer.GenerateToken();
            Settings.Save();
        }

        try
        {
            var server = new RemoteServer(RemoteController, settings.RemoteToken);
            server.Start(settings.RemotePort, settings.RemoteAllowLan);
            Remote = server;
            Log.Info($"Remote panel listening on port {server.Port}{(server.AllowLan ? " (local network)" : " (this PC only)")}.");
            return null;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Error($"Remote panel couldn't start on port {settings.RemotePort}: {ex.Message}");
            return ex.Message;
        }
    }

    public void Dispose()
    {
        Remote?.Dispose();
        Instances.Dispose();
        Scheduler.Dispose();
        Discord.Dispose();
        Api.Dispose();
        try
        {
            // Clients read the flag file at startup only, so restoring the user's own flags is safe now.
            ClientSettingsWriter.RestoreAll(RobloxInstallation.GetPlayerVersionDirectories());
            RestoreFramerateIfNoClients();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Roblox writes its frame rate setting back when a client exits, so the user's own value can only be put back
    /// while no client is running.
    /// </summary>
    private static void RestoreFramerateIfNoClients()
    {
        System.Diagnostics.Process[] clients = System.Diagnostics.Process.GetProcessesByName(RobloxInstallation.PlayerProcessName);
        bool anyRunning = clients.Length > 0;
        foreach (System.Diagnostics.Process client in clients)
        {
            client.Dispose();
        }

        if (!anyRunning)
        {
            FramerateCapFile.RestoreUserCap(RobloxInstallation.GlobalBasicSettingsPath);
        }
    }

    private void LoadPreset()
    {
        (JsonObject? preset, string? _) = ClientSettingsWriter.ParsePreset(PresetJson);
        _preset = preset ?? ClientSettingsWriter.ParsePreset(ClientSettingsWriter.DefaultPresetJson).Preset;
    }
}
