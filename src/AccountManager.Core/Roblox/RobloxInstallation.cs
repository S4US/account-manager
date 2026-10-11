using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AccountManager.Core.Roblox;

/// <summary>Finds the installed Roblox client and its per-user files.</summary>
public sealed partial class RobloxInstallation
{
    public const string PlayerProcessName = "RobloxPlayerBeta";
    public const string PlayerExecutable = "RobloxPlayerBeta.exe";

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string LogsDirectory => Path.Combine(LocalAppData, "Roblox", "logs");

    public static string GlobalBasicSettingsPath => Path.Combine(LocalAppData, "Roblox", "GlobalBasicSettings_13.xml");

    public static IEnumerable<string> VersionRoots
    {
        get
        {
            yield return Path.Combine(LocalAppData, "Roblox", "Versions");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions");
        }
    }

    /// <summary>Every version folder that contains a player executable.</summary>
    public static List<DirectoryInfo> GetPlayerVersionDirectories()
    {
        var result = new List<DirectoryInfo>();
        foreach (string root in VersionRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, PlayerExecutable)))
                {
                    result.Add(new DirectoryInfo(dir));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Picks the client to launch: the version Roblox currently publishes (if known), then the one the
    /// roblox-player: protocol is registered to, then the most recently installed official version folder.
    /// </summary>
    public static string? FindPlayerExecutable(string? currentVersion = null)
    {
        List<DirectoryInfo> versions = GetPlayerVersionDirectories();

        if (!string.IsNullOrEmpty(currentVersion))
        {
            DirectoryInfo? current = versions.FirstOrDefault(v => v.Name.Equals(currentVersion, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                return Path.Combine(current.FullName, PlayerExecutable);
            }
        }

        string? registered = GetProtocolHandlerExecutable();
        if (registered is not null && File.Exists(registered))
        {
            return registered;
        }

        DirectoryInfo? newest = versions
            .OrderByDescending(v => v.Name.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => File.GetLastWriteTimeUtc(Path.Combine(v.FullName, PlayerExecutable)))
            .FirstOrDefault();
        return newest is null ? null : Path.Combine(newest.FullName, PlayerExecutable);
    }

    /// <summary>The executable registered for roblox-player: links, if any.</summary>
    public static string? GetProtocolHandlerExecutable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command")
                ?? Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
            string command = key?.GetValue("")?.ToString() ?? "";
            Match match = QuotedPathRegex().Match(command);
            string path = match.Success ? match.Groups[1].Value : command.Split(' ')[0];
            return path.EndsWith(PlayerExecutable, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Asks Roblox's client settings service which Windows player version is current (e.g. "version-abc123…").</summary>
    public static async Task<string?> GetPublishedVersionAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            ClientVersionDto? dto = await http.GetFromJsonAsync<ClientVersionDto>(
                "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer", ct).ConfigureAwait(false);
            return dto?.ClientVersionUpload;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes stale version folders, keeping the two newest, the registered one and anything touched in the last week.
    /// Skips entirely while any Roblox client is running. Returns the number of folders removed.
    /// </summary>
    public static int CleanOldVersions(Action<string>? log = null)
    {
        if (Process.GetProcessesByName(PlayerProcessName).Length > 0)
        {
            return 0;
        }

        string? registered = GetProtocolHandlerExecutable();
        string? registeredDir = registered is null ? null : Path.GetDirectoryName(registered);
        int removed = 0;

        foreach (IGrouping<string, DirectoryInfo> root in GetPlayerVersionDirectories().GroupBy(d => d.Parent!.FullName))
        {
            foreach (DirectoryInfo stale in root
                         .OrderByDescending(d => d.LastWriteTimeUtc)
                         .Skip(2)
                         .Where(d => d.Name.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                         .Where(d => DateTime.UtcNow - d.LastWriteTimeUtc > TimeSpan.FromDays(7))
                         .Where(d => !string.Equals(d.FullName, registeredDir, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    foreach (FileInfo file in stale.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    stale.Delete(recursive: true);
                    removed++;
                    log?.Invoke($"Removed old Roblox version {stale.Name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Could not remove {stale.Name}: {ex.Message}");
                }
            }
        }

        return removed;
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedPathRegex();

    private sealed class ClientVersionDto
    {
        public string? ClientVersionUpload { get; set; }
    }
}
