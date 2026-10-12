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

public sealed record SystemStatus(double CpuPercent, MemorySnapshot Memory, int RunningClients, double ClientsMemoryMb, double ClientsCpuPercent);

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
        foreach (InstanceInfo info in Active.Where(i => i.State == InstanceState.Running))
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
    }

    private async Task LaunchOneAsync(Account account, InstanceInfo info, JoinTarget? targetOverride, CancellationToken ct)
    {
        _lastLaunchStarted = DateTime.UtcNow;
        info.StopRequested = false;
        info.CookieRejected = false;
        SetState(info, InstanceState.Launching, "Starting Roblox");

        AppSettings settings = Settings;
        JoinTarget target = targetOverride ?? account.DefaultTarget;
        int fps = account.FpsCap > 0 ? account.FpsCap : settings.DefaultFpsCap;
        try
        {
            string cookie = _accounts.GetCookie(account);
            Process process = await _launcher.LaunchAsync(new LaunchRequest(account, cookie, target, fps, account.UsePerformancePreset), ct).ConfigureAwait(false);
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

        if (!adopted && Settings.MinimizeAfterLaunch)
        {
            _ = MinimizeLaterAsync(info, process.Id);
        }
    }

    private void ApplyTuning(Account account, Process process)
    {
        AppSettings settings = Settings;
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

        ApplyLimits(account, process, settings, onlyWhenSet: true);
    }

    /// <summary>Applies the memory and CPU caps, logging when Windows refuses one so a silent no-op can't happen.</summary>
    private void ApplyLimits(Account? account, Process process, AppSettings settings, bool onlyWhenSet)
    {
        string? label = account?.DisplayLabel;

        // CPU first: adding a process to a job object drops its hard working set cap, so the memory cap must be
        // (re)applied after the job assignment.
        double cpuPercent = settings.CpuLimitCores > 0 ? settings.CpuLimitCores / Environment.ProcessorCount * 100 : 0;
        if ((cpuPercent > 0 || !onlyWhenSet) && !ProcessTuner.TryLimitCpu(process, cpuPercent, out int cpuError))
        {
            _log.Warning($"Couldn't apply the CPU limit (Windows error {cpuError}).", label);
        }

        int ramLimit = account?.RamLimitMb > 0 ? account.RamLimitMb : settings.GlobalRamLimitMb;
        if ((ramLimit > 0 || !onlyWhenSet) && !ProcessTuner.TryLimitWorkingSet(process, ramLimit, out int ramError))
        {
            _log.Warning($"Couldn't apply the memory limit (Windows error {ramError}).", label);
        }
    }

    /// <summary>Re-applies the current tuning settings to every running client.</summary>
    public void ReapplyTuning()
    {
        AppSettings settings = Settings;
        int slot = 0;
        foreach (InstanceInfo info in Active.Where(i => i.Process is not null))
        {
            Account? account = _accounts.Find(info.AccountId);
            Process process = info.Process!;
            ProcessTuner.TrySetPriority(process, settings.LowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal);
            ProcessTuner.TrySetEfficiencyMode(process, settings.EfficiencyMode);
            ProcessTuner.TrySetAffinity(process, settings.SpreadCpuAffinity ? ProcessTuner.ComputeAffinityMask(slot++, Environment.ProcessorCount) : 0);

            // Unlike at launch, a limit of 0 here removes a cap that was set earlier.
            ApplyLimits(account, process, settings, onlyWhenSet: false);
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
            _status = new SystemStatus(systemCpu, SystemMemory.GetSnapshot(), running, clientsMemory, clientsCpu);
            StatusSampled?.Invoke(_status);
        }
        catch (Exception ex)
        {
            _log.Error("Metrics sampling failed: " + ex.Message);
        }
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
                foreach (InstanceInfo info in running.Where(i => i.StartedAt is { } s && DateTime.Now - s > TimeSpan.FromSeconds(60)))
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
