using System.Collections.ObjectModel;
using System.Windows.Threading;
using AccountManager.App.Services;
using AccountManager.Core.Processes;
using AccountManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppHost _host;

    public DashboardViewModel(AppHost host)
    {
        _host = host;
        _host.Instances.StatusSampled += status => Ui.RunOnUi(() => Apply(status));
        _host.Scheduler.Changed += () => Ui.RunOnUi(UpdateSchedule);
        _host.Log.Added += entry => Ui.RunOnUi(() => AddRecent(entry));
        foreach (LogEntry entry in _host.Log.Snapshot().TakeLast(8))
        {
            Recent.Insert(0, new LogEntryViewModel(entry));
        }

        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateSchedule(), Dispatcher.CurrentDispatcher).Start();
        IsElevated = SystemMemory.IsElevated();
        UpdateSchedule();
    }

    public ObservableCollection<LogEntryViewModel> Recent { get; } = [];

    public IReadOnlyList<Choice<ScheduledAction>> ScheduleActions { get; } =
    [
        new("Close all clients", ScheduledAction.CloseClients),
        new("Shut down this PC", ScheduledAction.ShutdownPc),
    ];

    [ObservableProperty]
    private int _runningClients;

    [ObservableProperty]
    private int _totalAccounts;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private string _cpuText = "0%";

    [ObservableProperty]
    private double _memoryPercent;

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private string _memoryDetail = "";

    [ObservableProperty]
    private double _commitPercent;

    [ObservableProperty]
    private string _commitText = "";

    [ObservableProperty]
    private StatusTone _commitTone;

    [ObservableProperty]
    private string _clientsMemoryText = "0 GB";

    [ObservableProperty]
    private string _clientsMemoryDetail = "";

    [ObservableProperty]
    private string _capacityText = "";

    [ObservableProperty]
    private Choice<ScheduledAction>? _selectedScheduleAction;

    [ObservableProperty]
    private double _scheduleMinutes = 60;

    [ObservableProperty]
    private bool _isScheduled;

    [ObservableProperty]
    private string _scheduleStatus = "";

    [ObservableProperty]
    private bool _isElevated;

    private void Apply(SystemStatus status)
    {
        RunningClients = status.RunningClients;
        TotalAccounts = _host.Accounts.All.Count;
        CpuPercent = status.CpuPercent;
        CpuText = $"{status.CpuPercent:0}%";
        MemoryPercent = status.Memory.LoadPercent;
        MemoryText = $"{status.Memory.UsedGb:0.0} / {status.Memory.TotalGb:0.0} GB";
        MemoryDetail = $"RAM · {status.Memory.AvailableMb:N0} MB free";
        ClientsMemoryText = $"{status.ClientsMemoryMb / 1024:0.0} GB";
        ClientsMemoryDetail = status.RunningClients > 0
            ? $"{status.ClientsMemoryMb / status.RunningClients:0} MB per client · {status.ClientsCpuPercent:0}% CPU"
            : "No clients running";

        // Commit is what actually runs out with many clients: RAM caps and trimming move memory to the page file
        // but never lower it, so free RAM says little about how many more clients fit.
        if (status.Memory.CommitTotalBytes > 0)
        {
            CommitPercent = status.Memory.CommitUsedGb / status.Memory.CommitLimitGb * 100;
            CommitText = $"Committed · {status.Memory.CommitUsedGb:0.0} / {status.Memory.CommitLimitGb:0.0} GB";
            CommitTone = status.Memory.CommitAvailableMb < SystemStatus.CommitReserveMb ? StatusTone.Critical
                : status.CommitLeftMb < status.CommitPerClientMb + SystemStatus.CommitReserveMb ? StatusTone.Caution
                : StatusTone.Neutral;
        }

        // Rough headroom estimate: how many more clients fit in free RAM at the current average (or 800 MB), and
        // under the commit limit, which a memory cap doesn't help with. Whichever runs out first.
        double perClient = status.RunningClients > 0 ? Math.Max(300, status.ClientsMemoryMb / status.RunningClients) : 800;
        long reserve = Math.Max(1024, _host.Settings.Current.LowMemoryThrottleMb);
        long headroom = Math.Max(0, (long)((status.Memory.AvailableMb - reserve) / perClient));
        bool commitBound = false;
        if (status.Memory.CommitTotalBytes > 0)
        {
            long commitHeadroom = Math.Max(0, (long)((status.CommitLeftMb - SystemStatus.CommitReserveMb) / status.CommitPerClientMb));
            commitBound = commitHeadroom < headroom;
            headroom = Math.Min(headroom, commitHeadroom);
        }

        CapacityText = $"Room for about {headroom} more client{(headroom == 1 ? "" : "s")}{(commitBound ? ", limited by the page file size" : "")}";
    }

    private void UpdateSchedule()
    {
        IsScheduled = _host.Scheduler.IsScheduled;
        if (_host.Scheduler.DueAt is { } due)
        {
            TimeSpan left = due - DateTime.Now;
            string remaining = left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{Math.Max(0, left.Minutes)} min {Math.Max(0, left.Seconds)} s";
            ScheduleStatus = $"{ShutdownScheduler.Describe(_host.Scheduler.Action)} at {due:HH:mm} (in {remaining})";
        }
        else
        {
            ScheduleStatus = "Nothing scheduled";
        }

        SelectedScheduleAction ??= ScheduleActions[0];
    }

    private void AddRecent(LogEntry entry)
    {
        Recent.Insert(0, new LogEntryViewModel(entry));
        while (Recent.Count > 8)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }
    }

    [RelayCommand]
    private void TileWindows() => _host.Instances.TileWindows();

    [RelayCommand]
    private void CompactTile() => _host.Instances.TileWindowsCompact();

    [RelayCommand]
    private void MinimizeAll() => _host.Instances.MinimizeAll();

    [RelayCommand]
    private async Task TrimMemoryAsync()
    {
        int trimmed = await _host.Instances.TrimMemoryNowAsync();
        Ui.Success("Memory trimmed", trimmed == 0 ? "No running clients." : $"Released working memory from {trimmed} client{(trimmed == 1 ? "" : "s")}.");
    }

    [RelayCommand]
    private async Task AntiAfkNowAsync() => await _host.Instances.RunAntiAfkNowAsync();

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (_host.Settings.Current.ConfirmKillAll
            && !await Ui.ConfirmAsync("Close all Roblox clients?", "Every running Roblox window on this PC will be closed, including ones not started here.", "Close all", destructive: true))
        {
            return;
        }

        _host.Instances.KillAllRoblox();
    }

    [RelayCommand]
    private void Schedule()
    {
        int minutes = (int)Math.Round(ScheduleMinutes);
        if (minutes is < 1 or > 2880)
        {
            Ui.Error("Choose between 1 and 2880 minutes");
            return;
        }

        _host.Scheduler.Schedule(SelectedScheduleAction?.Value ?? ScheduledAction.CloseClients, TimeSpan.FromMinutes(minutes));
    }

    [RelayCommand]
    private void CancelSchedule() => _host.Scheduler.Cancel();
}

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(LogEntry entry)
    {
        Entry = entry;
    }

    public LogEntry Entry { get; }

    public string Time => Entry.Time.ToString("HH:mm:ss");

    public string Message => Entry.Message;

    public string? Account => Entry.Account;

    public bool HasAccount => !string.IsNullOrEmpty(Entry.Account);

    public string AccountPrefix => HasAccount ? Entry.Account + ": " : "";

    public StatusTone Tone => Entry.Severity switch
    {
        LogSeverity.Success => StatusTone.Success,
        LogSeverity.Warning => StatusTone.Caution,
        LogSeverity.Error => StatusTone.Critical,
        _ => StatusTone.Neutral,
    };

    public string Severity => Entry.Severity.ToString();
}
