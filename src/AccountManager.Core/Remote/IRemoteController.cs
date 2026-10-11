namespace AccountManager.Core.Remote;

public sealed record RemoteAccount(long Id, string Name, string State, string Detail, double CpuPercent, double MemoryMb, long UptimeSeconds);

public sealed record RemoteStatus(
    long AppUptimeSeconds,
    double CpuPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    int Running,
    double ClientsMemoryMb,
    string? ScheduledAction,
    long? ScheduledInSeconds,
    IReadOnlyList<RemoteAccount> Accounts);

/// <summary>Operations the remote panel can perform.</summary>
public interface IRemoteController
{
    RemoteStatus GetStatus();

    bool Launch(long accountId);

    bool Stop(long accountId);

    bool Restart(long accountId);

    int CloseAll();

    void ScheduleShutdown(int minutes);

    void CancelSchedule();
}
