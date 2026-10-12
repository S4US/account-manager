using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using AccountManager.App.Services;
using AccountManager.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

/// <summary>
/// Checks GitHub releases for a newer version, downloads and verifies it in the background, and asks before
/// restarting into it. Shown on the Settings page.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    /// <summary>Command-line flag for the copy started after an update; it waits for the old copy to exit.</summary>
    public const string UpdatedArg = "--updated";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _ready;
    private string? _readyPath;
    private Version? _promptedFor;
    private bool _loading;

    public UpdatesViewModel(AppHost host)
    {
        _host = host;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        _loading = true;
        CheckAutomatically = host.Settings.Current.CheckForUpdates;
        _loading = false;
        Status = CanSelfUpdate ? "" : "This is a development build; updates install only into the published app.";

        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (CheckAutomatically)
            {
                await CheckAsync(userInitiated: false);
            }
        };
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText => $"You have version {CurrentVersion.ToString(3)}.";

    /// <summary>
    /// Only a published single-file exe can replace itself. A development build runs from bin\ with
    /// AccountManager.dll next to the exe; the published app has no such file.
    /// </summary>
    public static bool CanSelfUpdate =>
        Environment.ProcessPath is { Length: > 0 } exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));

    [ObservableProperty] private bool _checkAutomatically;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _releasePage = "";

    partial void OnCheckAutomaticallyChanged(bool value)
    {
        if (!_loading)
        {
            _host.Settings.Update(s => s.CheckForUpdates = value);
        }
    }

    /// <summary>Starts the background schedule. Called once the main window is up.</summary>
    public void Start()
    {
        if (CanSelfUpdate)
        {
            _timer.Start();
        }
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userInitiated: true);

    [RelayCommand]
    private void OpenReleasePage() => Ui.OpenUrl(string.IsNullOrEmpty(ReleasePage) ? _host.Updates.ReleasesPage : ReleasePage);

    [RelayCommand]
    private void InstallAndRestart()
    {
        if (_ready is null || _readyPath is null || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            UpdateInstaller.Install(exe, _readyPath);
        }
        catch (UpdateException ex)
        {
            _host.Log.Error("Update failed: " + ex.Message);
            Ui.Error("Couldn't install the update", ex.Message);
            return;
        }

        _host.Log.Info($"Updating to version {_ready.Version.ToString(3)}; restarting.");
        Process.Start(new ProcessStartInfo(exe, UpdatedArg) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Application.Current.Shutdown();
    }

    private async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy)
        {
            return;
        }

        if (!CanSelfUpdate)
        {
            if (userInitiated)
            {
                Ui.Notify("Updates are off in development builds", "Publish the app to test updating.");
            }

            return;
        }

        IsBusy = true;
        Status = "Checking for updates…";
        try
        {
            UpdateInfo? update = await _host.Updates.CheckAsync(CurrentVersion);
            if (update is null)
            {
                Status = $"You're up to date. Last checked {DateTime.Now:HH:mm}.";
                _host.Updates.CleanDownloads();
                if (userInitiated)
                {
                    Ui.Success("You're up to date");
                }

                return;
            }

            ReleasePage = update.PageUrl;
            string version = update.Version.ToString(3);
            Status = $"Downloading version {version}…";
            string path = await _host.Updates.DownloadAsync(update);
            _host.Updates.CleanDownloads(keep: path);

            _ready = update;
            _readyPath = path;
            IsReady = true;
            Status = $"Version {version} is downloaded and checked. Restart to install it.";
            _host.Log.Info($"Version {version} is ready to install.");
            await OfferRestartAsync(update);
        }
        catch (UpdateException ex)
        {
            Status = ex.Message;
            _host.Log.Warning("Update check failed: " + ex.Message);
            if (userInitiated)
            {
                Ui.Error("Couldn't check for updates", ex.Message);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks once per version; "Later" leaves the install button on the Settings page.</summary>
    private async Task OfferRestartAsync(UpdateInfo update)
    {
        if (_promptedFor == update.Version)
        {
            return;
        }

        _promptedFor = update.Version;
        string version = update.Version.ToString(3);
        bool restart;
        try
        {
            restart = await Ui.ConfirmAsync(
                $"Version {version} is ready",
                "Restart the app now to install it? Running Roblox clients keep running and are picked up again after the restart.",
                "Restart now");
        }
        catch (InvalidOperationException)
        {
            // Another dialog is open; fall back to a notification.
            Ui.Notify($"Version {version} is ready", "Install it from Settings → Updates.");
            return;
        }

        if (restart)
        {
            InstallAndRestart();
        }
        else
        {
            Ui.Notify("Update postponed", "Install it any time from Settings → Updates.");
        }
    }
}
