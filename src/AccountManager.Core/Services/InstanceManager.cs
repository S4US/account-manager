using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AccountManager.Core.Models;
using AccountManager.Core.Monitoring;
using AccountManager.Core.Processes;
using AccountManager.Core.Roblox;
using AccountManager.Core.Storage;

namespace AccountManager.Core.Services;

public enum InstanceState
{
    Offline,
    Queued,
    Launching,
    Running,
    Disconnected,
    Restarting,
    Failed,
}

/// <summary>Live state of one account's client. Mutated only by <see cref="InstanceManager"/>.</summary>
public sealed class InstanceInfo
{
    internal InstanceInfo(long accountId)
    {
        AccountId = accountId;
    }

    public long AccountId { get; }

    public InstanceState State { get; internal set; }

    public int ProcessId { get; internal set; }

    public DateTime? StartedAt { get; internal set; }

    public double CpuPercent { get; internal set; }

    public double MemoryMb { get; internal set; }

    /// <summary>Short reason for Failed/Disconnected states, or the restart countdown.</summary>
    public string Detail { get; internal set; } = "";

    public DateTime? RestartAt { get; internal set; }

    public bool CookieRejected { get; internal set; }

    public bool IsActive => State is InstanceState.Running or InstanceState.Launching or InstanceState.Disconnected;

    internal Process? Process { get; set; }

    internal RobloxLogWatcher? Watcher { get; set; }

    internal bool StopRequested { get; set; }

    internal nint Window { get; set; }

    internal DateTime LastRename { get; set; }

    internal DateTime LastAntiAfk { get; set; }

    internal CancellationTokenSource? RestartCts { get; set; }

    internal bool StableRecorded { get; set; }
}

/// <param name="CommitPerClientMb">Commit one more client is expected to need once it's in a game.</param>
/// <param name="PendingCommitMb">Commit the clients still loading will take on top of what they hold now.</param>
public sealed record SystemStatus(double CpuPercent, MemorySnapshot Memory, int RunningClients, double ClientsMemoryMb, double ClientsCpuPercent,
    double CommitPerClientMb = SystemStatus.DefaultClientCommitMb, double PendingCommitMb = 0)
{
    /// <summary>Commit space to keep free for Windows itself (the desktop compositor freezes without it).</summary>
    public const long CommitReserveMb = 2048;

    // A client in a game commits about 1.6-1.7 GB, far more than its working set, and a memory cap doesn't lower it.
    public const double DefaultClientCommitMb = 1800;

    /// <summary>Clients younger than this are still loading their game and keep committing memory.</summary>
    public static readonly TimeSpan CommitSettleTime = TimeSpan.FromMinutes(3);

    public DateTime SampledAt { get; init; }

    /// <summary>Commit left under the limit once the clients still loading have taken theirs.</summary>
    public double CommitLeftMb => Memory.CommitAvailableMb - PendingCommitMb;

    /// <summary>
    /// Estimates one client's commit from the clients that have finished loading, or a typical value while none
    /// have, and how much more the clients still loading will take to reach it.
    /// </summary>
    public static (double PerClientMb, double PendingMb) EstimateClientCommit(IReadOnlyCollection<(double CommitMb, bool Settled)> clients)
    {
        List<double> settled = clients.Where(c => c.Settled).Select(c => c.CommitMb).ToList();
        double perClient = settled.Count > 0 ? Math.Max(1024, settled.Average()) : DefaultClientCommitMb;
        double pending = clients.Where(c => !c.Settled).Sum(c => Math.Max(0, perClient - c.CommitMb));
        return (perClient, pending);
    }
}

/// <summary>
/// Coordinates every client: a launch queue with staggering and memory throttling, per-process tuning after
/// launch, live metrics, disconnect detection with automatic restarts, memory maintenance and anti-AFK.
/// </summary>
public sealed class InstanceManager : IDisposable
{
    private static readonly TimeSpan StableThreshold = TimeSpan.FromMinutes(5);

    private readonly AccountService _accounts;
    private readonly RobloxLauncher _launcher;
    private readonly SettingsStore _settings;
    private readonly ActivityLog _log;
    private readonly DiscordNotifier _discord;
    private readonly CrashCircuitBreaker _breaker = new();
    private readonly CpuMeter _cpu = new();
    private readonly ConcurrentDictionary<long, InstanceInfo> _instances = new();
    private readonly Channel<LaunchJob> _queue = Channel.CreateUnbounded<LaunchJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _metricsTimer;
    private readonly Timer _maintenanceTimer;
    private DateTime _lastLaunchStarted = DateTime.MinValue;
    private DateTime _lastAttached = DateTime.MinValue;
    private bool _commitLowWarned;
    private DateTime _lastTrim = DateTime.UtcNow;
    private DateTime _lastMute = DateTime.MinValue;
    private int _maintenanceRunning;
    private SystemStatus _status = new(0, default, 0, 0, 0);

    public InstanceManager(AccountService accounts, RobloxLauncher launcher, SettingsStore settings, ActivityLog log, DiscordNotifier discord)
    {
        _accounts = accounts;
        _launcher = launcher;
        _settings = settings;
        _log = log;
        _discord = discord;
        _ = Task.Run(ProcessQueueAsync);
        _metricsTimer = new Timer(_ => SampleMetrics(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        _maintenanceTimer = new Timer(_ => _ = RunMaintenanceAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Raised (on a background thread) when an account's instance state or metrics change.</summary>
    public event Action<long>? InstanceChanged;

    /// <summary>Raised (on a background thread) after each metrics sample.</summary>
    public event Action<SystemStatus>? StatusSampled;

    /// <summary>
    /// Raised (on a background thread) when clients that are already running have used up the commit reserve, with
    /// the GB left. Once per episode.
    /// </summary>
    public event Action<double>? CommitRunningLow;

    public SystemStatus Status => _status;

    private AppSettings Settings => _settings.Current;

    public InstanceInfo Get(long accountId) => _instances.GetOrAdd(accountId, id => new InstanceInfo(id));

    public IReadOnlyList<InstanceInfo> Active => _instances.Values.Where(i => i.IsActive).ToList();

    public int QueuedCount => _instances.Values.Count(i => i.State == InstanceState.Queued);

    /// <summary>Queues a launch. Ignored if the account is already running or queued.</summary>
    public bool Enqueue(Account account, JoinTarget? target = null)
    {
        InstanceInfo info = Get(account.Id);
        if (info.State is InstanceState.Queued or InstanceState.Launching or InstanceState.Running)
        {
            return false;
        }

        info.RestartCts?.Cancel();
        SetState(info, InstanceState.Queued, "Waiting to launch");
        _queue.Writer.TryWrite(new LaunchJob(account.Id, target));
        return true;
    }

    public int EnqueueMany(IEnumerable<Account> accounts, JoinTarget? target = null) => accounts.Count(a => Enqueue(a, target));

    /// <summary>Closes an account's client on request; it will not be restarted.</summary>
    public void Stop(long accountId)
    {
        InstanceInfo info = Get(accountId);
        info.RestartCts?.Cancel();
        info.StopRequested = true;
        if (info.State == InstanceState.Queued)
        {
            SetState(info, InstanceState.Offline, "");
            return;
        }

        if (info.Process is { } process)
        {
            Kill(process);
        }

        Detach(info);
        SetState(info, InstanceState.Offline, "");
    }

    public void StopAll()
    {
        foreach (InstanceInfo info in _instances.Values.ToList())
        {
            if (info.State != InstanceState.Offline)
            {
                Stop(info.AccountId);
            }
        }
    }

    /// <summary>Closes every Roblox client on the machine, including ones not started by this app.</summary>
    public int KillAllRoblox()
    {
        StopAll();
        int killed = 0;
        foreach (Process process in Process.GetProcessesByName(RobloxInstallation.PlayerProcessName))
        {
            using (process)
            {
                if (Kill(process))
                {
                    killed++;
                }
            }
        }

        _log.Info(killed == 1 ? "Closed 1 Roblox client." : $"Closed {killed} Roblox clients.");
        return killed;
    }

    public void Restart(Account account)
    {
        Stop(account.Id);
        Get(account.Id).StopRequested = false;
        Enqueue(account);
    }

    public void ResetCrashCount(long accountId) => _breaker.Reset(accountId);

    /// <summary>Re-attaches clients that were already running (for example after the manager restarted).</summary>
    public int AdoptRunningClients()
    {
        Dictionary<int, string> commandLines = ProcessCommandLine.GetAll(RobloxInstallation.PlayerExecutable);
        int adopted = 0;
        foreach (Account account in _accounts.All)
        {
            int pid = commandLines.FirstOrDefault(kv => kv.Value.Contains("browsertrackerid:" + account.BrowserTrackerId, StringComparison.Ordinal)).Key;
            if (pid == 0 || Get(account.Id).IsActive)
            {
                continue;
            }

            try
            {
                Attach(account, Process.GetProcessById(pid), adopted: true);
                adopted++;
            }
            catch (ArgumentException)
            {
            }
        }

        if (adopted > 0)
        {
            _log.Info($"Reconnected to {adopted} running client{(adopted == 1 ? "" : "s")}.");
        }

        return adopted;
    }

    public IReadOnlyList<nint> GetWindows() =>
        Active.Select(i => i.Window != 0 ? i.Window : WindowTools.FindMainWindow(i.ProcessId)).Where(w => w != 0).ToList();

    public void TileWindows() => WindowTools.Tile(GetWindows());

    public void TileWindowsCompact() => WindowTools.TileCompact(GetWindows());

    public void MinimizeAll()
    {
        foreach (nint window in GetWindows())
        {
            WindowTools.Minimize(window);
        }
    }

    /// <summary>Trims every client's working set now and, if enabled and elevated, purges the standby list.</summary>
    public async Task<int> TrimMemoryNowAsync()
    {
        int trimmed = 0;
        foreach (InstanceInfo info in Active.Where(i => i.State == InstanceState.Running && IsOptimized(i.AccountId)))
        {
            if (info.Process is { } process && ProcessTuner.TryTrimWorkingSet(process))
            {
                trimmed++;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        bool purged = Settings.PurgeStandbyList && SystemMemory.IsElevated() && SystemMemory.TryPurgeStandbyList(includeNormalPriority: false);
        _lastTrim = DateTime.UtcNow;
        _log.Info($"Trimmed memory of {trimmed} client{(trimmed == 1 ? "" : "s")}{(purged ? " and cleared the standby list" : "")}.");
        return trimmed;
    }

    public async Task RunAntiAfkNowAsync()
    {
        List<InstanceInfo> due = Active.Where(i => i.State == InstanceState.Running && i.Window != 0).ToList();
        int reached = await AntiAfk.PulseAsync(due.Select(i => i.Window).ToList(), Settings.AntiAfkRestoreMinimized, _shutdown.Token).ConfigureAwait(false);
        foreach (InstanceInfo info in due)
        {
            info.LastAntiAfk = DateTime.UtcNow;
        }

        _log.Info($"Anti-AFK reached {reached} of {due.Count} client{(due.Count == 1 ? "" : "s")}.");
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        _metricsTimer.Dispose();
        _maintenanceTimer.Dispose();
        foreach (InstanceInfo info in _instances.Values)
        {
            info.Watcher?.Dispose();
            info.RestartCts?.Cancel();
        }
    }

    // ---- Launch queue ----------------------------------------------------------------------------------------

    private async Task ProcessQueueAsync()
    {
        CancellationToken ct = _shutdown.Token;
        try
        {
            await foreach (LaunchJob job in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                InstanceInfo info = Get(job.AccountId);
                Account? account = _accounts.Find(job.AccountId);
                if (account is null || info.State != InstanceState.Queued)
                {
                    continue;
                }

                await WaitForLaunchSlotAsync(info, ct).ConfigureAwait(false);
                if (info.State != InstanceState.Queued)
                {
                    continue; // cancelled while waiting
                }

                await LaunchOneAsync(account, info, job.Target, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForLaunchSlotAsync(InstanceInfo info, CancellationToken ct)
    {
        // Space clients out so each one finishes its heavy startup before the next begins.
        TimeSpan stagger = TimeSpan.FromSeconds(Math.Max(0, Settings.LaunchStaggerSeconds));
        TimeSpan wait = _lastLaunchStarted + stagger - DateTime.UtcNow;
        while (wait > TimeSpan.Zero && info.State == InstanceState.Queued)
        {
            SetState(info, InstanceState.Queued, $"Launching in {Math.Ceiling(wait.TotalSeconds):0} s");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, wait.TotalSeconds)), ct).ConfigureAwait(false);
            wait = _lastLaunchStarted + stagger - DateTime.UtcNow;
        }

        // Hold launches while the machine is short on RAM, instead of pushing it into heavy paging.
        int threshold = Settings.LowMemoryThrottleMb;
        DateTime giveUp = DateTime.UtcNow.AddMinutes(3);
        while (threshold > 0 && info.State == InstanceState.Queued && DateTime.UtcNow < giveUp)
        {
            MemorySnapshot memory = SystemMemory.GetSnapshot();
            if (memory.TotalBytes == 0 || memory.AvailableMb >= threshold)
            {
                break;
            }

            SetState(info, InstanceState.Queued, $"Waiting for free memory ({memory.AvailableMb:N0} MB free)");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        // Never start a client the commit limit (RAM plus page file) can't hold. Once it's reached Windows can't
        // allocate memory for anything and the desktop freezes, even with physical RAM free. The stagger doesn't
        // wait for a client to finish loading, so the ones still loading count with what they'll take once in
        // their game. Unlike the check above, this one doesn't give up.
        await WaitForSampleAfterLastAttachAsync(info, ct).ConfigureAwait(false);
        bool logged = false;
        while (info.State == InstanceState.Queued)
        {
            MemorySnapshot memory = SystemMemory.GetSnapshot();
            SystemStatus status = _status;
            double left = memory.CommitAvailableMb - status.PendingCommitMb;
            if (memory.CommitTotalBytes == 0 || left >= status.CommitPerClientMb + SystemStatus.CommitReserveMb)
            {
                break;
            }

            double leftGb = Math.Max(0, left) / 1024.0;
            if (!logged)
            {
                string loading = status.PendingCommitMb >= 100 ? $" once the clients still loading take about {status.PendingCommitMb / 1024.0:0.0} GB more" : "";
                _log.Warning($"Launches paused: Windows' memory limit (RAM plus page file) is almost reached, {leftGb:0.0} GB left{loading}. " +
                    "Close some clients, or make the page file larger.");
                logged = true;
            }

            SetState(info, InstanceState.Queued, $"Waiting: Windows memory limit almost reached ({leftGb:0.0} GB left)");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits (briefly) for a metrics sample that includes the client attached last. When its launch took longer
    /// than the stagger, the queue gets here right after it, before any sample counts it as loading.
    /// </summary>
    private async Task WaitForSampleAfterLastAttachAsync(InstanceInfo info, CancellationToken ct)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(5);
        while (_status.SampledAt < _lastAttached && info.State == InstanceState.Queued && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private async Task LaunchOneAsync(Account account, InstanceInfo info, JoinTarget? targetOverride, CancellationToken ct)
    {
        _lastLaunchStarted = DateTime.UtcNow;
        info.StopRequested = false;
        info.CookieRejected = false;
        SetState(info, InstanceState.Launching, "Starting Roblox");

        AppSettings settings = Settings;
        JoinTarget target = targetOverride ?? account.DefaultTarget;
        // An account with optimizations off starts like a normal client: no cap, no low-graphics flags.
        int fps = !account.Optimize ? 0 : account.FpsCap > 0 ? account.FpsCap : settings.DefaultFpsCap;
        bool preset = account.Optimize && account.UsePerformancePreset;
        try
        {
            string cookie = _accounts.GetCookie(account);
            Process process = await _launcher.LaunchAsync(new LaunchRequest(account, cookie, target, fps, preset), ct).ConfigureAwait(false);
            if (info.StopRequested)
            {
                Kill(process);
                SetState(info, InstanceState.Offline, "");
                return;
            }

            account.LastLaunchedAt = DateTime.UtcNow;
            _accounts.Save(account);
            Attach(account, process, adopted: false);
            _log.Success($"Launched{(target.IsEmpty ? "" : " into " + target)}.", account.DisplayLabel);
        }
        catch (LaunchException ex)
        {
            info.CookieRejected = ex.CookieRejected;
            SetState(info, InstanceState.Failed, ex.CookieRejected ? "Signed out. Sign in again." : ex.Message);
            _log.Error(ex.Message, account.DisplayLabel);
        }
        catch (OperationCanceledException)
        {
            SetState(info, InstanceState.Offline, "");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The saved sign-in was encrypted with a vault key that no longer exists (the vault was reset).
            info.CookieRejected = true;
            SetState(info, InstanceState.Failed, "Signed out. Sign in again.");
            _log.Error("The saved sign-in can't be read. Sign in again.", account.DisplayLabel);
        }
        catch (Exception ex)
        {
            SetState(info, InstanceState.Failed, ex.Message);
            _log.Error("Launch failed: " + ex.Message, account.DisplayLabel);
        }
    }

    // ---- Attach / tuning ------------------------------------------------------------------------------------

    private void Attach(Account account, Process process, bool adopted)
    {
        InstanceInfo info = Get(account.Id);
        Detach(info);
        info.Process = process;
        info.ProcessId = process.Id;
        info.StartedAt = SafeStartTime(process);
        info.Window = WindowTools.FindMainWindow(process.Id);
        info.StableRecorded = false;
        info.StopRequested = false;
        info.CookieRejected = false;
        info.LastAntiAfk = DateTime.UtcNow;
        SetState(info, InstanceState.Running, "");
        _lastAttached = DateTime.UtcNow;

        ApplyTuning(account, process);

        int pid = process.Id;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            ProcessTuner.Forget(pid);
            OnProcessExited(account.Id, process);
            _ = RestoreFramerateWhenIdleAsync();
        };

        var claimed = _instances.Values.Where(i => i.Watcher?.AttachedFile is not null).Select(i => i.Watcher!.AttachedFile!).ToList();
        var watcher = new RobloxLogWatcher(RobloxInstallation.LogsDirectory, (info.StartedAt ?? DateTime.Now).ToUniversalTime(), account.BrowserTrackerId, claimed);
        watcher.EventDetected += (kind, line) => OnLogEvent(account.Id, process.Id, kind, line);
        info.Watcher = watcher;
        watcher.Start();

        if (!adopted && account.Optimize && Settings.ShrinkWindowsOnLaunch)
        {
            _ = ShrinkWhileStartingAsync(info, process.Id);
        }

        if (!adopted && Settings.MinimizeAfterLaunch)
        {
            _ = MinimizeLaterAsync(info, process.Id);
        }
    }

    // Times after launch to shrink the window again. Roblox can maximize its window just after it appears (it starts
    // maximized when it was last closed that way), so one pass right away isn't always enough.
    private static readonly TimeSpan[] ShrinkPasses = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    private async Task ShrinkWhileStartingAsync(InstanceInfo info, int pid)
    {
        TimeSpan elapsed = TimeSpan.Zero;
        foreach (TimeSpan at in ShrinkPasses)
        {
            await Task.Delay(at - elapsed).ConfigureAwait(false);
            elapsed = at;
            if (info.ProcessId != pid || info.State != InstanceState.Running)
            {
                return;
            }

            // A no-op once the window is at its minimum.
            WindowTools.ShrinkToMinimum(info.Window != 0 ? info.Window : WindowTools.FindMainWindow(pid));
        }
    }

    private void ApplyTuning(Account account, Process process)
    {
        AppSettings settings = Settings;
        if (!account.Optimize)
        {
            // A fresh client already runs untouched.
            return;
        }

        if (settings.LowPriority)
        {
            ProcessTuner.TrySetPriority(process, ProcessPriorityClass.BelowNormal);
        }

        if (settings.EfficiencyMode)
        {
            ProcessTuner.TrySetEfficiencyMode(process, true);
        }

        if (settings.SpreadCpuAffinity)
        {
            int slot = Active.Count(i => i.AccountId != account.Id);
            ProcessTuner.TrySetAffinity(process, ProcessTuner.ComputeAffinityMask(slot, Environment.ProcessorCount));
        }

        ApplyLimits(account, process, settings, onlyWhenSet: true, optimized: true);
    }

    /// <summary>
    /// Applies the memory and CPU caps, logging when Windows refuses one so a silent no-op can't happen. With
    /// <paramref name="optimized"/> false both caps are removed.
    /// </summary>
    private void ApplyLimits(Account? account, Process process, AppSettings settings, bool onlyWhenSet, bool optimized)
    {
        string? label = account?.DisplayLabel;

        // CPU first: adding a process to a job object drops its hard working set cap, so the memory cap must be
        // (re)applied after the job assignment.
        double cpuPercent = optimized && settings.CpuLimitCores > 0 ? settings.CpuLimitCores / Environment.ProcessorCount * 100 : 0;
        if ((cpuPercent > 0 || !onlyWhenSet) && !ProcessTuner.TryLimitCpu(process, cpuPercent, out int cpuError))
        {
            _log.Warning($"Couldn't apply the CPU limit (Windows error {cpuError}).", label);
        }

        int ramLimit = !optimized ? 0 : account?.RamLimitMb > 0 ? account.RamLimitMb : settings.GlobalRamLimitMb;
        if ((ramLimit > 0 || !onlyWhenSet) && !ProcessTuner.TryLimitWorkingSet(process, ramLimit, out int ramError))
        {
            _log.Warning($"Couldn't apply the memory limit (Windows error {ramError}).", label);
        }
    }

    private bool IsOptimized(long accountId) => _accounts.Find(accountId)?.Optimize ?? true;

    /// <summary>Re-applies the current tuning settings to every running client.</summary>
    public void ReapplyTuning()
    {
        AppSettings settings = Settings;
        int slot = 0;
        foreach (InstanceInfo info in Active.Where(i => i.Process is not null))
        {
            Account? account = _accounts.Find(info.AccountId);
            Process process = info.Process!;
            bool optimized = account?.Optimize ?? true;
            ProcessTuner.TrySetPriority(process, optimized && settings.LowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal);
            ProcessTuner.TrySetEfficiencyMode(process, optimized && settings.EfficiencyMode);
            ProcessTuner.TrySetAffinity(process, optimized && settings.SpreadCpuAffinity ? ProcessTuner.ComputeAffinityMask(slot++, Environment.ProcessorCount) : 0);

            // Unlike at launch, a limit of 0 here removes a cap that was set earlier.
            ApplyLimits(account, process, settings, onlyWhenSet: false, optimized);
        }

        _lastMute = DateTime.MinValue;
    }

    /// <summary>Once the last managed client has closed, puts the user's own frame rate setting back.</summary>
    private async Task RestoreFramerateWhenIdleAsync()
    {
        // Roblox saves its settings file while exiting; restore after that write.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        bool anyPending = _instances.Values.Any(i => i.IsActive || i.State is InstanceState.Queued or InstanceState.Restarting);
        if (!anyPending)
        {
            await _launcher.RestoreUserFramerateCapAsync().ConfigureAwait(false);
        }
    }

    private async Task MinimizeLaterAsync(InstanceInfo info, int pid)
    {
        // Give the client time to finish loading; minimizing too early can stall its first frame.
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (info.ProcessId == pid && info.State == InstanceState.Running)
        {
            WindowTools.Minimize(info.Window != 0 ? info.Window : WindowTools.FindMainWindow(pid));
        }
    }

    private void Detach(InstanceInfo info)
    {
        info.Watcher?.Dispose();
        info.Watcher = null;
        info.Process = null;
        info.Window = 0;
        info.CpuPercent = 0;
        info.MemoryMb = 0;
    }

    // ---- Disconnects and restarts ---------------------------------------------------------------------------

    private void OnLogEvent(long accountId, int pid, LogEvent kind, string line)
    {
        InstanceInfo info = Get(accountId);
        if (info.ProcessId != pid || info.State != InstanceState.Running || info.StopRequested)
        {
            return;
        }

        // Ignore early noise while the client is still connecting.
        if (info.StartedAt is { } started && DateTime.Now - started < TimeSpan.FromSeconds(10))
        {
            return;
        }

        if (kind == LogEvent.LeftGame && Settings.AllowLeavingGame)
        {
            return;
        }

        string reason = kind == LogEvent.LeftGame ? "Left the game" : LogLineClassifier.Describe(line);
        HandleFailure(accountId, reason, processStillRunning: true);
    }

    private void OnProcessExited(long accountId, Process process)
    {
        InstanceInfo info = Get(accountId);
        if (info.ProcessId != process.Id || info.StopRequested)
        {
            return;
        }

        int exitCode = SafeExitCode(process);
        if (info.State == InstanceState.Disconnected)
        {
            // Already reported; the player closed the disconnected client.
            Detach(info);
            SetState(info, InstanceState.Offline, "");
            return;
        }

        if (info.State == InstanceState.Running && exitCode == 0)
        {
            // Closed normally (the player quit). Nothing to recover.
            Detach(info);
            SetState(info, InstanceState.Offline, "");
            _log.Info("Client closed.", _accounts.Find(accountId)?.DisplayLabel);
            return;
        }

        if (info.State == InstanceState.Running)
        {
            HandleFailure(accountId, $"Client exited unexpectedly (code {exitCode}).", processStillRunning: false);
        }
    }

    private void HandleFailure(long accountId, string reason, bool processStillRunning)
    {
        Account? account = _accounts.Find(accountId);
        InstanceInfo info = Get(accountId);
        string who = account?.DisplayLabel ?? accountId.ToString();
        bool autoRestart = account is not null && account.AutoRestart && Settings.AutoRestartEnabled;

        if (!autoRestart)
        {
            if (!processStillRunning)
            {
                Detach(info);
            }

            SetState(info, processStillRunning ? InstanceState.Disconnected : InstanceState.Failed, reason);
            _log.Warning(reason, who);
            Notify($"{who} disconnected", reason, 0xE5A50A);
            return;
        }

        if (info.Process is { } process)
        {
            Kill(process);
        }

        Detach(info);
        RestartDecision decision = _breaker.RecordCrash(accountId);
        if (!decision.Allowed)
        {
            SetState(info, InstanceState.Failed, decision.Reason);
            _log.Error($"{reason} {decision.Reason}", who);
            Notify($"{who} keeps crashing", $"{reason}\n{decision.Reason}", 0xE01B24);
            return;
        }

        _log.Warning($"{reason} {decision.Reason}", who);
        Notify($"{who} disconnected", $"{reason}\n{decision.Reason}", 0xE5A50A);
        ScheduleRestart(account!, info, decision.Delay);
    }

    private void ScheduleRestart(Account account, InstanceInfo info, TimeSpan delay)
    {
        info.RestartCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        info.RestartCts = cts;
        info.RestartAt = DateTime.UtcNow + delay;
        SetState(info, InstanceState.Restarting, "Restarting soon");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                info.RestartAt = null;
                SetState(info, InstanceState.Offline, "");
                Enqueue(account);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void Notify(string title, string description, int color)
    {
        if (Settings.NotifyOnCrash)
        {
            _ = _discord.SendAsync(title, description, color);
        }
    }

    // ---- Metrics and maintenance ----------------------------------------------------------------------------

    private void SampleMetrics()
    {
        try
        {
            double systemCpu = _cpu.SampleSystem();
            double clientsMemory = 0;
            var clientsCommit = new List<(double CommitMb, bool Settled)>();
            double clientsCpu = 0;
            int running = 0;
            AppSettings settings = Settings;

            foreach (InstanceInfo info in _instances.Values)
            {
                if (info.State == InstanceState.Restarting && info.RestartAt is { } at)
                {
                    info.Detail = $"Restarting in {Math.Max(0, Math.Ceiling((at - DateTime.UtcNow).TotalSeconds)):0} s";
                    InstanceChanged?.Invoke(info.AccountId);
                    continue;
                }

                if (info.Process is not { } process || !info.IsActive)
                {
                    continue;
                }

                try
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        continue;
                    }

                    info.MemoryMb = process.WorkingSet64 / 1048576.0;
                    info.CpuPercent = _cpu.SampleProcess(process);
                    if (info.Window == 0)
                    {
                        info.Window = WindowTools.FindMainWindow(process.Id);
                    }

                    running++;
                    clientsMemory += info.MemoryMb;
                    bool settled = info.StartedAt is { } launched && DateTime.Now - launched > SystemStatus.CommitSettleTime;
                    clientsCommit.Add((process.PrivateMemorySize64 / 1048576.0, settled));
                    clientsCpu += info.CpuPercent;

                    if (!info.StableRecorded && info.StartedAt is { } started && DateTime.Now - started > StableThreshold)
                    {
                        _breaker.RecordStable(info.AccountId);
                        info.StableRecorded = true;
                    }

                    if (settings.RenameWindows && info.Window != 0 && DateTime.UtcNow - info.LastRename > TimeSpan.FromSeconds(15))
                    {
                        Account? account = _accounts.Find(info.AccountId);
                        if (account is not null)
                        {
                            WindowTools.SetTitle(info.Window, $"Roblox · {account.DisplayLabel}");
                        }

                        info.LastRename = DateTime.UtcNow;
                    }

                    InstanceChanged?.Invoke(info.AccountId);
                }
                catch (InvalidOperationException)
                {
                }
            }

            _cpu.Forget(_instances.Values.Where(i => i.Process is not null).Select(i => i.ProcessId));
            (double perClient, double pending) = SystemStatus.EstimateClientCommit(clientsCommit);
            _status = new SystemStatus(systemCpu, SystemMemory.GetSnapshot(), running, clientsMemory, clientsCpu, perClient, pending)
            {
                SampledAt = DateTime.UtcNow,
            };
            WarnIfCommitLow(_status);
            StatusSampled?.Invoke(_status);
        }
        catch (Exception ex)
        {
            _log.Error("Metrics sampling failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The launch pause keeps the reserve free, but clients already running can still grow into it, and at the
    /// limit the desktop freezes. Warns once, and again only after enough memory was freed in between.
    /// </summary>
    private void WarnIfCommitLow(SystemStatus status)
    {
        MemorySnapshot memory = status.Memory;
        if (memory.CommitTotalBytes == 0 || status.RunningClients == 0)
        {
            return;
        }

        if (memory.CommitAvailableMb >= SystemStatus.CommitReserveMb * 2)
        {
            _commitLowWarned = false;
            return;
        }

        if (memory.CommitAvailableMb >= SystemStatus.CommitReserveMb || _commitLowWarned)
        {
            return;
        }

        _commitLowWarned = true;
        double leftGb = memory.CommitAvailableMb / 1024.0;
        _log.Warning($"Windows is almost out of memory: {leftGb:0.0} GB left under its limit (RAM plus page file). " +
            "Close a few clients now, or the desktop can freeze.");
        Notify("Windows is almost out of memory", $"{leftGb:0.0} GB left under the limit with {status.RunningClients} clients running.", 0xE01B24);
        CommitRunningLow?.Invoke(leftGb);
    }

    private async Task RunMaintenanceAsync()
    {
        if (Interlocked.Exchange(ref _maintenanceRunning, 1) == 1)
        {
            return;
        }

        try
        {
            AppSettings settings = Settings;
            List<InstanceInfo> running = Active.Where(i => i.State == InstanceState.Running && i.Process is not null).ToList();

            if (settings.AutoMute && DateTime.UtcNow - _lastMute > TimeSpan.FromSeconds(25))
            {
                AudioMuter.SetMute(running.Select(i => i.ProcessId).ToHashSet(), true);
                _lastMute = DateTime.UtcNow;
            }

            if (settings.PeriodicTrim && DateTime.UtcNow - _lastTrim > TimeSpan.FromMinutes(Math.Max(1, settings.TrimIntervalMinutes)))
            {
                // Skip clients still loading: trimming during startup only slows them down.
                foreach (InstanceInfo info in running.Where(i => i.StartedAt is { } s && DateTime.Now - s > TimeSpan.FromSeconds(60) && IsOptimized(i.AccountId)))
                {
                    ProcessTuner.TryTrimWorkingSet(info.Process!);
                    await Task.Delay(25).ConfigureAwait(false);
                }

                if (settings.PurgeStandbyList && SystemMemory.IsElevated())
                {
                    SystemMemory.TryPurgeStandbyList(includeNormalPriority: false);
                }

                _lastTrim = DateTime.UtcNow;
            }

            TimeSpan afkInterval = TimeSpan.FromMinutes(Math.Clamp(settings.AntiAfkIntervalMinutes, 1, 19));
            List<InstanceInfo> afkDue = running
                .Where(i => i.Window != 0 && _accounts.Find(i.AccountId)?.AntiAfk == true && DateTime.UtcNow - i.LastAntiAfk >= afkInterval)
                .ToList();
            if (afkDue.Count > 0)
            {
                await AntiAfk.PulseAsync(afkDue.Select(i => i.Window).ToList(), settings.AntiAfkRestoreMinimized, _shutdown.Token).ConfigureAwait(false);
                foreach (InstanceInfo info in afkDue)
                {
                    info.LastAntiAfk = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Maintenance failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _maintenanceRunning, 0);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private void SetState(InstanceInfo info, InstanceState state, string detail)
    {
        info.State = state;
        info.Detail = detail;
        if (state is InstanceState.Offline or InstanceState.Failed or InstanceState.Queued)
        {
            info.ProcessId = state == InstanceState.Failed ? info.ProcessId : 0;
            info.StartedAt = null;
        }

        InstanceChanged?.Invoke(info.AccountId);
    }

    private static bool Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.Now;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private sealed record LaunchJob(long AccountId, JoinTarget? Target);
}
