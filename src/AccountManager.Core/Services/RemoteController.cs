using AccountManager.Core.Models;
using AccountManager.Core.Remote;

namespace AccountManager.Core.Services;

/// <summary>Connects the remote panel to the running app.</summary>
public sealed class RemoteController : IRemoteController
{
    private readonly AccountService _accounts;
    private readonly InstanceManager _instances;
    private readonly ShutdownScheduler _scheduler;
    private readonly Func<bool> _streamerMode;
    private readonly DateTime _startedAt = DateTime.Now;

    public RemoteController(AccountService accounts, InstanceManager instances, ShutdownScheduler scheduler, Func<bool> streamerMode)
    {
        _accounts = accounts;
        _instances = instances;
        _scheduler = scheduler;
        _streamerMode = streamerMode;
    }

    public RemoteStatus GetStatus()
    {
        SystemStatus status = _instances.Status;
        bool hideNames = _streamerMode();
        var accounts = _accounts.All.Select((account, index) =>
        {
            InstanceInfo info = _instances.Get(account.Id);
            long uptime = info.StartedAt is { } started && info.State == InstanceState.Running ? (long)(DateTime.Now - started).TotalSeconds : 0;
            string name = hideNames ? $"Account {index + 1}" : account.DisplayLabel;
            return new RemoteAccount(account.Id, name, info.State.ToString(), info.Detail, Math.Round(info.CpuPercent, 1), Math.Round(info.MemoryMb), uptime);
        }).ToList();

        return new RemoteStatus(
            (long)(DateTime.Now - _startedAt).TotalSeconds,
            Math.Round(status.CpuPercent, 1),
            Math.Round(status.Memory.UsedGb, 2),
            Math.Round(status.Memory.TotalGb, 2),
            status.RunningClients,
            Math.Round(status.ClientsMemoryMb),
            _scheduler.IsScheduled ? ShutdownScheduler.Describe(_scheduler.Action) : null,
            _scheduler.DueAt is { } due ? Math.Max(0, (long)(due - DateTime.Now).TotalSeconds) : null,
            accounts);
    }

    public bool Launch(long accountId)
    {
        Account? account = _accounts.Find(accountId);
        return account is not null && (_instances.Enqueue(account) || _instances.Get(accountId).IsActive);
    }

    public bool Stop(long accountId)
    {
        if (_accounts.Find(accountId) is null)
        {
            return false;
        }

        _instances.Stop(accountId);
        return true;
    }

    public bool Restart(long accountId)
    {
        Account? account = _accounts.Find(accountId);
        if (account is null)
        {
            return false;
        }

        _instances.Restart(account);
        return true;
    }

    public int CloseAll() => _instances.KillAllRoblox();

    public void ScheduleShutdown(int minutes) => _scheduler.Schedule(ScheduledAction.ShutdownPc, TimeSpan.FromMinutes(minutes));

    public void CancelSchedule() => _scheduler.Cancel();
}
