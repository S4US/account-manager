using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Roblox;

/// <summary>
/// Reads and writes the in-game "Maximum frame rate" value stored in GlobalBasicSettings_13.xml.
/// The cap is written before a launch and the file is made read-only so Roblox can't rewrite the value while
/// managed clients run (it saves its settings while running and on exit). The user's own value is kept in a
/// backup file next to it, and the file is unlocked and restored once no managed client is running.
/// </summary>
public static partial class FramerateCapFile
{
    public const int MinCap = 1;
    public const int MaxCap = 1000;

    /// <summary>The caps Roblox's own "Maximum frame rate" menu offers.</summary>
    public static readonly IReadOnlyList<int> RobloxMenuCaps = [30, 60, 144, 240];

    private const string BackupSuffix = ".account-manager-framerate";
    private const string NoValue = "none";

    /// <summary>Keeps a requested cap in range. 0 or less means no cap.</summary>
    public static int Normalize(int cap) => cap <= 0 ? 0 : Math.Clamp(cap, MinCap, MaxCap);

    /// <summary>
    /// Sets the cap for the next launch and locks the file, first saving the user's own value if it isn't saved yet.
    /// </summary>
    public static bool ApplyForLaunch(string path, int cap)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        string backup = path + BackupSuffix;
        if (!File.Exists(backup))
        {
            // A value Roblox's menu can't produce (such as 10, left by earlier versions of this app) isn't the
            // user's own choice; remembering none lets Roblox fall back to its default on restore.
            int? original = Read(path);
            bool isUsersOwn = original is int value && RobloxMenuCaps.Contains(value);
            File.WriteAllText(backup, isUsersOwn ? original!.Value.ToString(CultureInfo.InvariantCulture) : NoValue);
        }

        if (!Write(path, Normalize(cap)))
        {
            return false;
        }

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return true;
    }

    /// <summary>Unlocks the file, puts the user's own value back and deletes the backup. Does nothing when no backup exists.</summary>
    public static bool RestoreUserCap(string path)
    {
        string backup = path + BackupSuffix;
        if (!File.Exists(backup) || !File.Exists(path))
        {
            return false;
        }

        string saved = File.ReadAllText(backup).Trim();
        if (int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            Write(path, value);
        }
        else
        {
            Remove(path);
        }

        ClearReadOnly(path);
        File.Delete(backup);
        return true;
    }

    private static void Remove(string path)
    {
        string xml = ReadShared(path);
        string updated = LineRegex().Replace(xml, "", 1);
        if (updated != xml)
        {
            WriteAllowingReadOnly(path, updated);
        }
    }
    public static int? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Match match = CapRegex().Match(ReadShared(path));
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>Sets the cap. Returns false when the settings file doesn't exist yet (Roblox never ran).</summary>
    public static bool Write(string path, int cap)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        string xml = ReadShared(path);
        string element = $"<int name=\"FramerateCap\">{cap.ToString(CultureInfo.InvariantCulture)}</int>";
        string updated;
        if (CapRegex().IsMatch(xml))
        {
            updated = CapRegex().Replace(xml, element, 1);
        }
        else
        {
            int insertAt = xml.LastIndexOf("</Properties>", StringComparison.Ordinal);
            if (insertAt < 0)
            {
                return false;
            }

            updated = xml.Insert(insertAt, "\t" + element + "\n\t\t");
        }

        if (updated != xml)
        {
            WriteAllowingReadOnly(path, updated);
        }

        return true;
    }

    private static void WriteAllowingReadOnly(string path, string contents)
    {
        ClearReadOnly(path);
        File.WriteAllText(path, contents);
    }

    private static void ClearReadOnly(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("<int\\s+name=\"FramerateCap\">(-?\\d+)</int>")]
    private static partial Regex CapRegex();

    [GeneratedRegex("[ \\t]*<int\\s+name=\"FramerateCap\">-?\\d+</int>\\r?\\n?")]
    private static partial Regex LineRegex();
}
