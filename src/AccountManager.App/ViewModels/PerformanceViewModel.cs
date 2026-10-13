using System.Diagnostics;
using AccountManager.App.Services;
using AccountManager.Core.Models;
using AccountManager.Core.Processes;
using AccountManager.Core.Roblox;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

/// <summary>Launch, per-client and memory settings. Every change is saved immediately.</summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading;

    public PerformanceViewModel(AppHost host)
    {
        _host = host;
        IsElevated = SystemMemory.IsElevated();
        Load();
    }

    public static IReadOnlyList<Choice<int>> FpsCaps { get; } =
    [
        new("No cap", 0),
        new("5 FPS", 5),
        new("10 FPS", 10),
        new("15 FPS", 15),
        new("20 FPS", 20),
        new("30 FPS", 30),
        new("45 FPS", 45),
        new("60 FPS", 60),
        new("144 FPS", 144),
        new("240 FPS", 240),
    ];

    public static IReadOnlyList<Choice<double>> CpuLimits { get; } =
    [
        new("No limit", 0),
        new("A quarter of a core", 0.25),
        new("Half a core", 0.5),
        new("1 core", 1),
        new("2 cores", 2),
    ];

    public static IReadOnlyList<Choice<int>> RamLimits { get; } =
    [
        new("No limit", 0),
        new("512 MB", 512),
        new("768 MB", 768),
        new("1 GB", 1024),
        new("1.5 GB", 1536),
        new("2 GB", 2048),
    ];

    public bool IsElevated { get; }

    public int LogicalProcessors { get; } = Environment.ProcessorCount;

    [ObservableProperty] private bool _multiInstance;
    [ObservableProperty] private bool _runAsAdministrator;
    [ObservableProperty] private double _launchStaggerSeconds;
    [ObservableProperty] private double _lowMemoryThrottleMb;
    [ObservableProperty] private Choice<int>? _defaultFpsCap;
    [ObservableProperty] private bool _minimizeAfterLaunch;
    [ObservableProperty] private bool _renameWindows;
    [ObservableProperty] private bool _lowPriority;
    [ObservableProperty] private bool _efficiencyMode;
    [ObservableProperty] private bool _spreadCpuAffinity;
    [ObservableProperty] private bool _shrinkWindowsOnLaunch;
    [ObservableProperty] private Choice<int>? _globalRamLimit;
    [ObservableProperty] private Choice<double>? _cpuLimit;
    [ObservableProperty] private bool _autoMute;
    [ObservableProperty] private bool _periodicTrim;
    [ObservableProperty] private double _trimIntervalMinutes;
    [ObservableProperty] private bool _purgeStandbyList;
    [ObservableProperty] private bool _cleanOldRobloxVersions;
    [ObservableProperty] private bool _autoRestartEnabled;
    [ObservableProperty] private bool _allowLeavingGame;
    [ObservableProperty] private double _antiAfkIntervalMinutes;
    [ObservableProperty] private bool _antiAfkRestoreMinimized;
    [ObservableProperty] private string _presetJson = "";
    [ObservableProperty] private string _presetMessage = "";
    [ObservableProperty] private bool _presetHasError;

    private void Load()
    {
        _loading = true;
        AppSettings s = _host.Settings.Current;
        MultiInstance = s.MultiInstance;
        RunAsAdministrator = s.RunAsAdministrator;
        LaunchStaggerSeconds = s.LaunchStaggerSeconds;
        LowMemoryThrottleMb = s.LowMemoryThrottleMb;
        DefaultFpsCap = FpsCaps.FirstOrDefault(c => c.Value == s.DefaultFpsCap) ?? new Choice<int>($"{s.DefaultFpsCap} FPS", s.DefaultFpsCap);
        MinimizeAfterLaunch = s.MinimizeAfterLaunch;
        RenameWindows = s.RenameWindows;
        LowPriority = s.LowPriority;
        EfficiencyMode = s.EfficiencyMode;
        SpreadCpuAffinity = s.SpreadCpuAffinity;
        ShrinkWindowsOnLaunch = s.ShrinkWindowsOnLaunch;
        GlobalRamLimit =RamLimits.FirstOrDefault(c => c.Value == s.GlobalRamLimitMb) ?? RamLimits[0];
        CpuLimit = CpuLimits.FirstOrDefault(c => c.Value == s.CpuLimitCores) ?? CpuLimits[0];
        AutoMute = s.AutoMute;
        PeriodicTrim = s.PeriodicTrim;
        TrimIntervalMinutes = s.TrimIntervalMinutes;
        PurgeStandbyList = s.PurgeStandbyList;
        CleanOldRobloxVersions = s.CleanOldRobloxVersions;
        AutoRestartEnabled = s.AutoRestartEnabled;
        AllowLeavingGame = s.AllowLeavingGame;
        AntiAfkIntervalMinutes = s.AntiAfkIntervalMinutes;
        AntiAfkRestoreMinimized = s.AntiAfkRestoreMinimized;
        PresetJson = _host.PresetJson;
        _loading = false;
    }

    private void Save(Action<AppSettings> change, bool retune = false)
    {
        if (_loading)
        {
            return;
        }

        _host.Settings.Update(change);
        if (retune)
        {
            _host.Instances.ReapplyTuning();
        }
    }

    partial void OnMultiInstanceChanged(bool value) => Save(s => s.MultiInstance = value);

    partial void OnRunAsAdministratorChanged(bool value) => Save(s => s.RunAsAdministrator = value);

    partial void OnLaunchStaggerSecondsChanged(double value) => Save(s => s.LaunchStaggerSeconds = Math.Clamp((int)Math.Round(value), 0, 300));

    partial void OnLowMemoryThrottleMbChanged(double value) => Save(s => s.LowMemoryThrottleMb = Math.Clamp((int)Math.Round(value), 0, 65536));

    partial void OnDefaultFpsCapChanged(Choice<int>? value) => Save(s => s.DefaultFpsCap = value?.Value ?? 0);

    partial void OnMinimizeAfterLaunchChanged(bool value) => Save(s => s.MinimizeAfterLaunch = value);

    partial void OnRenameWindowsChanged(bool value) => Save(s => s.RenameWindows = value);

    partial void OnLowPriorityChanged(bool value) => Save(s => s.LowPriority = value, retune: true);

    partial void OnEfficiencyModeChanged(bool value) => Save(s => s.EfficiencyMode = value, retune: true);

    partial void OnSpreadCpuAffinityChanged(bool value) => Save(s => s.SpreadCpuAffinity = value, retune: true);

    partial void OnShrinkWindowsOnLaunchChanged(bool value) => Save(s => s.ShrinkWindowsOnLaunch = value);

    partial void OnGlobalRamLimitChanged(Choice<int>? value) => Save(s => s.GlobalRamLimitMb = value?.Value ?? 0, retune: true);

    partial void OnCpuLimitChanged(Choice<double>? value) => Save(s => s.CpuLimitCores = value?.Value ?? 0, retune: true);

    partial void OnAutoMuteChanged(bool value)
    {
        Save(s => s.AutoMute = value);
        if (!_loading && !value)
        {
            // Unmute clients that were muted by the manager.
            AudioMuter.SetMute(_host.Instances.Active.Select(i => i.ProcessId).ToHashSet(), false);
        }
    }

    partial void OnPeriodicTrimChanged(bool value) => Save(s => s.PeriodicTrim = value);

    partial void OnTrimIntervalMinutesChanged(double value) => Save(s => s.TrimIntervalMinutes = Math.Clamp((int)Math.Round(value), 1, 240));

    partial void OnPurgeStandbyListChanged(bool value) => Save(s => s.PurgeStandbyList = value);

    partial void OnCleanOldRobloxVersionsChanged(bool value) => Save(s => s.CleanOldRobloxVersions = value);

    partial void OnAutoRestartEnabledChanged(bool value) => Save(s => s.AutoRestartEnabled = value);

    partial void OnAllowLeavingGameChanged(bool value) => Save(s => s.AllowLeavingGame = value);

    partial void OnAntiAfkIntervalMinutesChanged(double value) => Save(s => s.AntiAfkIntervalMinutes = Math.Clamp((int)Math.Round(value), 1, 19));

    partial void OnAntiAfkRestoreMinimizedChanged(bool value) => Save(s => s.AntiAfkRestoreMinimized = value);

    [RelayCommand]
    private void SavePreset()
    {
        string? error = _host.SavePreset(PresetJson);
        PresetHasError = error is not null;
        PresetMessage = error ?? "Saved. Applies to the next launch.";
    }

    [RelayCommand]
    private void ResetPreset()
    {
        _host.ResetPreset();
        PresetJson = _host.PresetJson;
        PresetHasError = false;
        PresetMessage = "Restored the default preset.";
    }

    [RelayCommand]
    private async Task CleanVersionsNowAsync()
    {
        int removed = await Task.Run(() => RobloxInstallation.CleanOldVersions(m => _host.Log.Info(m)));
        Ui.Success(removed == 0 ? "Nothing to clean" : $"Removed {removed} old Roblox version{(removed == 1 ? "" : "s")}");
    }

    [RelayCommand]
    private void ReleaseSingletonNow()
    {
        ReleaseResult release = MultiInstanceGuard.CloseSingletonHandlesInRunningClients(TimeSpan.FromSeconds(30), m => _host.Log.Info(m));
        if (release.Unreachable > 0)
        {
            Ui.Notify("Needs administrator rights", "Restart the app as administrator to release Roblox's lock.", Wpf.Ui.Controls.ControlAppearance.Caution);
            return;
        }

        if (release.HeldElsewhere)
        {
            Ui.Notify("Lock held by another program", "Close Roblox Account Manager (or turn off its Multi Roblox) so clients can run side by side.", Wpf.Ui.Controls.ControlAppearance.Caution);
            return;
        }

        Ui.Notify(release.Closed == 0 ? "No locks to release" : $"Released {release.Closed} lock{(release.Closed == 1 ? "" : "s")}");
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = "--elevated-restart" });
            App.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt.
        }
    }
}
