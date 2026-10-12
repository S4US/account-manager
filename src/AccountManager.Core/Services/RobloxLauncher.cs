using System.Diagnostics;
using System.Text.Json.Nodes;
using AccountManager.Core.Models;
using AccountManager.Core.Processes;
using AccountManager.Core.Roblox;

namespace AccountManager.Core.Services;

public sealed record LaunchRequest(Account Account, string Cookie, JoinTarget Target, int FpsCap, bool UsePerformancePreset);

public sealed class LaunchException : Exception
{
    public LaunchException(string message, Exception? inner = null, bool cookieRejected = false)
        : base(message, inner)
    {
        CookieRejected = cookieRejected;
    }

    public bool CookieRejected { get; }
}

/// <summary>
/// Starts one Roblox client signed in as a given account. Launches are serialized because the fast-flag file
/// and the frame rate setting are shared by every client and are read at startup.
/// </summary>
public sealed class RobloxLauncher
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);

    private readonly RobloxApi _api;
    private readonly ClientSettingsWriter _flags = new();
    private readonly Func<AppSettings> _settings;
    private readonly Func<JsonObject?> _preset;
    private readonly ActivityLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _versionHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private (string? Version, DateTime At) _publishedVersion;

    public RobloxLauncher(RobloxApi api, Func<AppSettings> settings, Func<JsonObject?> preset, ActivityLog log)
    {
        _api = api;
        _settings = settings;
        _preset = preset;
        _log = log;
    }

    public async Task<Process> LaunchAsync(LaunchRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LaunchCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Process> LaunchCoreAsync(LaunchRequest request, CancellationToken ct)
    {
        Account account = request.Account;
        AppSettings settings = _settings();

        string? executable = RobloxInstallation.FindPlayerExecutable(await GetPublishedVersionAsync(ct).ConfigureAwait(false));
        if (executable is null)
        {
            throw new LaunchException("Roblox isn't installed. Install it from roblox.com, start it once, then try again.");
        }

        JoinTarget target = await ResolveTargetAsync(request.Cookie, request.Target, ct).ConfigureAwait(false);

        string ticket;
        try
        {
            ticket = await _api.GetAuthenticationTicketAsync(request.Cookie, ct).ConfigureAwait(false);
        }
        catch (RobloxApiException ex)
        {
            throw new LaunchException(ex.Message, ex, ex.IsUnauthorized);
        }
        catch (HttpRequestException ex)
        {
            throw new LaunchException("Couldn't reach Roblox. Check your internet connection.", ex);
        }

        // Per-launch fast flags. Roblox ignores frame rate flags now, so the cap goes through its in-game setting.
        JsonObject overrides = ClientSettingsWriter.BuildOverrides(request.UsePerformancePreset ? _preset() : null);
        _flags.Apply(overrides, RobloxInstallation.GetPlayerVersionDirectories());
        ApplyFramerateCap(request.FpsCap);

        if (settings.MultiInstance)
        {
            // Clients a few seconds old are past their startup wait on the singleton mutex.
            int released = MultiInstanceGuard.CloseSingletonHandlesInRunningClients(TimeSpan.FromSeconds(5));
            if (released > 0)
            {
                _log.Info($"Released {released} Roblox single-instance lock{(released == 1 ? "" : "s")} so another client can start.", account.DisplayLabel);
            }
        }

        string uri = LaunchUriBuilder.Build(ticket, target, account.BrowserTrackerId, DateTimeOffset.UtcNow);
        var known = Process.GetProcessesByName(RobloxInstallation.PlayerProcessName).Select(p => p.Id).ToHashSet();

        Process started = Process.Start(new ProcessStartInfo(executable, uri)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        }) ?? throw new LaunchException("Windows refused to start the Roblox client.");

        Process client = await CaptureClientAsync(started, known, account.BrowserTrackerId, ct).ConfigureAwait(false);
        await WaitForWindowAsync(client, ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Puts the user's own "Maximum frame rate" back. Call once no managed client is running: Roblox writes the
    /// value it started with back to the file when it exits.
    /// </summary>
    public async Task RestoreUserFramerateCapAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            FramerateCapFile.RestoreUserCap(RobloxInstallation.GlobalBasicSettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Couldn't restore your Roblox frame rate setting: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyFramerateCap(int requested)
    {
        string path = RobloxInstallation.GlobalBasicSettingsPath;
        int cap = FramerateCapFile.Normalize(requested);
        try
        {
            if (cap > 0)
            {
                FramerateCapFile.ApplyForLaunch(path, cap);
            }
            else
            {
                FramerateCapFile.RestoreUserCap(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Couldn't set the frame rate cap: " + ex.Message);
        }
    }

    /// <summary>Resolves share links into a private server target; other targets pass through.</summary>
    private async Task<JoinTarget> ResolveTargetAsync(string cookie, JoinTarget target, CancellationToken ct)
    {
        if (target.Kind != JoinKind.PrivateServer || string.IsNullOrEmpty(target.ShareCode))
        {
            return target;
        }

        PrivateServerInvite? invite = await _api.ResolveServerShareLinkAsync(cookie, target.ShareCode, ct).ConfigureAwait(false);
        return invite is null
            ? throw new LaunchException("That private server link is invalid or expired.")
            : target with { PlaceId = invite.PlaceId, LinkCode = invite.LinkCode, ShareCode = "" };
    }

    /// <summary>
    /// The process we start is normally the client itself. If it hands off to another process (for example while
    /// Roblox updates), find the client by the account's browser tracker id in its command line.
    /// </summary>
    private static async Task<Process> CaptureClientAsync(Process started, HashSet<int> known, string trackerId, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + CaptureTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            started.Refresh();
            if (!started.HasExited && (DateTime.Now - started.StartTime).TotalSeconds >= 3)
            {
                return started;
            }

            if (started.HasExited)
            {
                foreach ((int pid, string commandLine) in ProcessCommandLine.GetAll(RobloxInstallation.PlayerExecutable))
                {
                    if (!known.Contains(pid) && commandLine.Contains(trackerId, StringComparison.Ordinal))
                    {
                        try
                        {
                            return Process.GetProcessById(pid);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new LaunchException("Roblox started but its window never appeared. It may be updating; try again in a minute.");
    }

    private static async Task WaitForWindowAsync(Process client, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + WindowTimeout;
        while (DateTime.UtcNow < deadline)
        {
            client.Refresh();
            if (client.HasExited)
            {
                throw new LaunchException($"Roblox closed during startup (exit code {client.ExitCode}).");
            }

            if (WindowTools.FindMainWindow(client.Id) != 0)
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetPublishedVersionAsync(CancellationToken ct)
    {
        if (_publishedVersion.Version is not null && DateTime.UtcNow - _publishedVersion.At < TimeSpan.FromMinutes(30))
        {
            return _publishedVersion.Version;
        }

        string? version = await RobloxInstallation.GetPublishedVersionAsync(_versionHttp, ct).ConfigureAwait(false);
        _publishedVersion = (version, DateTime.UtcNow);
        return version;
    }
}
