using System.Diagnostics;

namespace AccountManager.Core.Services;

public enum ScheduledAction
{
    CloseClients,
    ShutdownPc,
}

/// <summary>Runs one timed action: close every client, or shut the PC down.</summary>
public sealed class ShutdownScheduler : IDisposable
{
    private readonly Func<int> _closeAllClients;
    private readonly ActivityLog _log;
    private readonly object _lock = new();
    private Timer? _timer;

    public ShutdownScheduler(Func<int> closeAllClients, ActivityLog log)
    {
        _closeAllClients = closeAllClients;
        _log = log;
    }

    public event Action? Changed;

    public DateTime? DueAt { get; private set; }

    public ScheduledAction Action { get; private set; }

    public bool IsScheduled => DueAt is not null;

    public void Schedule(ScheduledAction action, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromDays(2))
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Choose between 1 minute and 48 hours.");
        }

        lock (_lock)
        {
            _timer?.Dispose();
            Action = action;
            DueAt = DateTime.Now + delay;
            _timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
        }

        _log.Info($"{Describe(action)} scheduled for {DueAt:HH:mm}.");
        Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (DueAt is null)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            DueAt = null;
        }

        _log.Info("Scheduled action cancelled.");
        Changed?.Invoke();
    }

    public static string Describe(ScheduledAction action) => action == ScheduledAction.ShutdownPc ? "PC shutdown" : "Closing all clients";

    private void Fire()
    {
        ScheduledAction action;
        lock (_lock)
        {
            action = Action;
            _timer?.Dispose();
            _timer = null;
            DueAt = null;
        }

        _closeAllClients();
        if (action == ScheduledAction.ShutdownPc)
        {
            _log.Warning("Shutting down the PC in 60 seconds. Run \"shutdown /a\" to abort.");
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 60 /c \"Scheduled by Account manager\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();
}
