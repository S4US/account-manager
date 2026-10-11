using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Roblox;

/// <summary>
/// Reads and writes the in-game "Maximum frame rate" value stored in GlobalBasicSettings_13.xml.
/// Roblox reads it at startup, so a launch can set it temporarily and restore the original afterwards.
/// </summary>
public static partial class FramerateCapFile
{
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

        if (updated == xml)
        {
            return true;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.WriteAllText(path, updated);
        return true;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("<int\\s+name=\"FramerateCap\">(-?\\d+)</int>")]
    private static partial Regex CapRegex();
}
