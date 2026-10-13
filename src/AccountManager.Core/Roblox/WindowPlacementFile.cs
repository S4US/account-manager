using System.Globalization;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Roblox;

/// <summary>Window placement values from GlobalBasicSettings_13.xml. Null where the file has no such value.</summary>
public sealed record WindowPlacement(bool? Fullscreen, bool? StartMaximized, (int Width, int Height)? StartSize);

/// <summary>
/// Controls how Roblox opens its window, through the placement it keeps in GlobalBasicSettings_13.xml: Fullscreen,
/// StartMaximized and StartScreenSize. Roblox saves its window state there whenever a client closes, so after one
/// maximized client every later one starts maximized too. Managed launches open as a normal window instead. The
/// user's own values are kept in a backup next to the file and put back once no managed client runs, like
/// <see cref="FramerateCapFile"/>. Writes leave the file's read-only lock as they found it.
/// </summary>
public static partial class WindowPlacementFile
{
    /// <summary>Roblox's own default window: an 800x600 game view plus the frame.</summary>
    public const int DefaultWidth = 816;
    public const int DefaultHeight = 638;

    private const string BackupSuffix = ".account-manager-window";
    private const string NoValue = "none";

    public static WindowPlacement? Read(string path) => File.Exists(path) ? Parse(FramerateCapFile.ReadShared(path)) : null;

    /// <summary>
    /// Makes the next launch open as a normal window of the given size, first saving the user's own placement if it
    /// isn't saved yet.
    /// </summary>
    public static bool ApplyForLaunch(string path, int width, int height)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        string backup = path + BackupSuffix;
        if (!File.Exists(backup))
        {
            File.WriteAllText(backup, Serialize(Parse(FramerateCapFile.ReadShared(path))));
        }

        return Write(path, new WindowPlacement(false, false, (width, height)));
    }

    /// <summary>
    /// For a launch that should open the user's way while managed clients may still run: puts the user's own
    /// placement in the file for this launch but keeps the backup. Does nothing when no backup exists.
    /// </summary>
    public static bool ApplyUserValueForLaunch(string path)
    {
        string backup = path + BackupSuffix;
        return File.Exists(backup) && File.Exists(path) && Write(path, Deserialize(File.ReadAllText(backup)));
    }

    /// <summary>Puts the user's own placement back and deletes the backup. Does nothing when no backup exists.</summary>
    public static bool RestoreUserPlacement(string path)
    {
        string backup = path + BackupSuffix;
        if (!File.Exists(backup) || !File.Exists(path))
        {
            return false;
        }

        Write(path, Deserialize(File.ReadAllText(backup)));
        File.Delete(backup);
        return true;
    }

    /// <summary>Sets every value the placement has; null values are left as they are.</summary>
    private static bool Write(string path, WindowPlacement placement)
    {
        string xml = FramerateCapFile.ReadShared(path);
        string? updated = xml;
        if (placement.Fullscreen is bool fullscreen)
        {
            updated = SetElement(updated, FullscreenRegex(), $"<bool name=\"Fullscreen\">{Format(fullscreen)}</bool>");
        }

        if (updated is not null && placement.StartMaximized is bool maximized)
        {
            updated = SetElement(updated, StartMaximizedRegex(), $"<bool name=\"StartMaximized\">{Format(maximized)}</bool>");
        }

        if (updated is not null && placement.StartSize is (int width, int height))
        {
            string x = width.ToString(CultureInfo.InvariantCulture);
            string y = height.ToString(CultureInfo.InvariantCulture);
            updated = StartSizeRegex().IsMatch(updated)
                ? StartSizeRegex().Replace(updated, m => m.Groups["pre"].Value + x + m.Groups["mid"].Value + y + m.Groups["post"].Value, 1)
                : Insert(updated, $"<Vector2 name=\"StartScreenSize\">\n\t\t\t\t<X>{x}</X>\n\t\t\t\t<Y>{y}</Y>\n\t\t\t</Vector2>");
        }

        if (updated is null)
        {
            return false;
        }

        if (updated != xml)
        {
            WriteKeepingLock(path, updated);
        }

        return true;
    }

    /// <summary>Replaces the element <paramref name="regex"/> finds with <paramref name="element"/>, or inserts it when missing.</summary>
    private static string? SetElement(string xml, Regex regex, string element) =>
        regex.IsMatch(xml) ? regex.Replace(xml, element, 1) : Insert(xml, element);

    private static string? Insert(string xml, string element)
    {
        int insertAt = xml.LastIndexOf("</Properties>", StringComparison.Ordinal);
        return insertAt < 0 ? null : xml.Insert(insertAt, "\t" + element + "\n\t\t");
    }

    private static void WriteKeepingLock(string path, string contents)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool locked = attributes.HasFlag(FileAttributes.ReadOnly);
        if (locked)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.WriteAllText(path, contents);
        if (locked)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    private static WindowPlacement Parse(string xml)
    {
        Match size = StartSizeRegex().Match(xml);
        return new WindowPlacement(
            ParseBool(FullscreenRegex().Match(xml)),
            ParseBool(StartMaximizedRegex().Match(xml)),
            size.Success && TryParseNumber(size.Groups["x"].Value, out int width) && TryParseNumber(size.Groups["y"].Value, out int height)
                ? (width, height)
                : null);
    }

    private static bool? ParseBool(Match match) => match.Success ? match.Groups["value"].Value == "true" : null;

    private static bool TryParseNumber(string text, out int value)
    {
        bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number);
        value = ok ? (int)Math.Round(number) : 0;
        return ok;
    }

    private static string Format(bool value) => value ? "true" : "false";

    // Backup format: one "Name=value" line per setting, "none" where the user's file had no value.
    private static string Serialize(WindowPlacement placement) =>
        $"Fullscreen={(placement.Fullscreen is bool f ? Format(f) : NoValue)}\n" +
        $"StartMaximized={(placement.StartMaximized is bool m ? Format(m) : NoValue)}\n" +
        $"StartScreenSize={(placement.StartSize is (int w, int h) ? string.Create(CultureInfo.InvariantCulture, $"{w},{h}") : NoValue)}\n";

    private static WindowPlacement Deserialize(string text)
    {
        var values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);

        bool? GetBool(string name) => values.TryGetValue(name, out string? v) && v is "true" or "false" ? v == "true" : null;

        (int, int)? size = null;
        if (values.TryGetValue("StartScreenSize", out string? s) && s.Split(',') is [string x, string y]
            && TryParseNumber(x, out int width) && TryParseNumber(y, out int height))
        {
            size = (width, height);
        }

        return new WindowPlacement(GetBool("Fullscreen"), GetBool("StartMaximized"), size);
    }

    [GeneratedRegex("<bool\\s+name=\"Fullscreen\">(?<value>true|false)</bool>")]
    private static partial Regex FullscreenRegex();

    [GeneratedRegex("<bool\\s+name=\"StartMaximized\">(?<value>true|false)</bool>")]
    private static partial Regex StartMaximizedRegex();

    [GeneratedRegex("(?<pre><Vector2\\s+name=\"StartScreenSize\">\\s*<X>)(?<x>[^<]*)(?<mid></X>\\s*<Y>)(?<y>[^<]*)(?<post></Y>)")]
    private static partial Regex StartSizeRegex();
}
