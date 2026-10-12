using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Roblox;

/// <summary>
/// Writes the per-launch fast-flag file (ClientSettings\ClientAppSettings.json) that Roblox reads once at startup.
/// Launches are serialized, so each client starts with the flags of the account being launched.
/// A flag file the user already had is preserved as the base layer and restored when nothing needs overriding.
/// </summary>
public sealed class ClientSettingsWriter
{
    private const string FileName = "ClientAppSettings.json";
    private const string UserBackupName = "ClientAppSettings.user.json";
    private const string MarkerName = ".account-manager";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Default "performance preset": rendering-only flags that cut GPU/CPU use per client. Roblox only accepts flags
    /// on its published allowlist (devforum.roblox.com/t/3966569); every flag here is on it. The sky is drawn flat
    /// gray and the lighting voxelizer is paused, which saves CPU in games with dynamic lighting.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, object> DefaultPerformancePreset = new Dictionary<string, object>
    {
        ["DFIntDebugFRMQualityLevelOverride"] = 1,
        ["FFlagDebugGraphicsPreferD3D11"] = true,
        ["FFlagDebugSkyGray"] = true,
        ["DFFlagDebugPauseVoxelizer"] = true,
        ["FIntDebugForceMSAASamples"] = 0,
        ["DFFlagTextureQualityOverrideEnabled"] = true,
        ["DFIntTextureQualityOverride"] = 0,
        ["FIntFRMMinGrassDistance"] = 0,
        ["FIntFRMMaxGrassDistance"] = 0,
        ["DFIntCSGLevelOfDetailSwitchingDistance"] = 0,
        ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = 0,
        ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = 0,
        ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = 0,
    };

    public static string DefaultPresetJson => JsonSerializer.Serialize(DefaultPerformancePreset, Indented);

    /// <summary>Validates preset JSON. Returns the parsed object or an error message.</summary>
    public static (JsonObject? Preset, string? Error) ParsePreset(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject obj)
            {
                return (null, "The preset must be a JSON object of flag names to values.");
            }

            foreach ((string name, JsonNode? value) in obj)
            {
                if (value is not JsonValue)
                {
                    return (null, $"\"{name}\" must be a number, string or true/false.");
                }
            }

            return (obj, null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The overrides for one launch. Frame rate is not set here: Roblox no longer accepts that flag locally.</summary>
    public static JsonObject BuildOverrides(JsonObject? preset) =>
        preset is null ? new JsonObject() : (JsonObject)preset.DeepClone();

    /// <summary>Applies the overrides to every installed version folder. Returns the folders written.</summary>
    public int Apply(JsonObject overrides, IEnumerable<DirectoryInfo> versionDirectories)
    {
        int written = 0;
        foreach (DirectoryInfo version in versionDirectories)
        {
            ApplyToVersion(overrides, Path.Combine(version.FullName, "ClientSettings"));
            written++;
        }

        return written;
    }

    internal static void ApplyToVersion(JsonObject overrides, string settingsDir)
    {
        string file = Path.Combine(settingsDir, FileName);
        string backup = Path.Combine(settingsDir, UserBackupName);
        string marker = Path.Combine(settingsDir, MarkerName);
        bool managed = File.Exists(marker);

        // First time we touch this folder: keep whatever flags the user already had.
        if (!managed)
        {
            if (File.Exists(file))
            {
                File.Copy(file, backup, overwrite: true);
            }
            else
            {
                TryDelete(backup);
            }
        }

        JsonObject merged = ReadObject(backup) ?? new JsonObject();
        foreach ((string name, JsonNode? value) in overrides)
        {
            merged[name] = value?.DeepClone();
        }

        if (merged.Count == 0)
        {
            // Nothing to apply: put the folder back the way we found it.
            if (managed && File.Exists(file))
            {
                File.Delete(file);
            }

            TryDelete(marker);
            if (Directory.Exists(settingsDir) && !Directory.EnumerateFileSystemEntries(settingsDir).Any())
            {
                Directory.Delete(settingsDir);
            }

            return;
        }

        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(marker, "Managed by Account manager. ClientAppSettings.user.json holds your original flags.");
        WriteAtomically(file, merged.ToJsonString(Indented));
    }

    /// <summary>Restores every folder to the user's original flags and stops managing it.</summary>
    public static void RestoreAll(IEnumerable<DirectoryInfo> versionDirectories)
    {
        foreach (DirectoryInfo version in versionDirectories)
        {
            string dir = Path.Combine(version.FullName, "ClientSettings");
            string marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker))
            {
                continue;
            }

            string file = Path.Combine(dir, FileName);
            string backup = Path.Combine(dir, UserBackupName);
            if (File.Exists(backup))
            {
                File.Move(backup, file, overwrite: true);
            }
            else
            {
                TryDelete(file);
            }

            TryDelete(marker);
        }
    }

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
