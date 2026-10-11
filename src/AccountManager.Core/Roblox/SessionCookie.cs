namespace AccountManager.Core.Roblox;

/// <summary>Helpers for recognising and normalising .ROBLOSECURITY values.</summary>
public static class SessionCookie
{
    public const string Name = ".ROBLOSECURITY";
    private const string WarningPrefix = "_|WARNING:-DO-NOT-SHARE-THIS.";

    /// <summary>Strips a "name=" prefix, attributes and quotes. Returns null if it doesn't look like a session cookie.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim().Trim('"', '\'');
        int nameIndex = value.IndexOf(Name + "=", StringComparison.OrdinalIgnoreCase);
        if (nameIndex >= 0)
        {
            value = value[(nameIndex + Name.Length + 1)..];
        }

        int attributes = value.IndexOf(';');
        if (attributes >= 0)
        {
            value = value[..attributes];
        }

        value = value.Trim().Trim('"', '\'');
        return value.StartsWith(WarningPrefix, StringComparison.Ordinal) && value.Length > WarningPrefix.Length + 32 ? value : null;
    }

    /// <summary>Extracts every distinct session cookie from pasted text (one per line, or mixed with other text).</summary>
    public static IReadOnlyList<string> ParseMany(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Tolerate surrounding text (e.g. a copied header line) by starting at the cookie's fixed prefix.
            int start = line.IndexOf(WarningPrefix, StringComparison.Ordinal);
            string? cookie = Normalize(start >= 0 ? line[start..] : line);
            if (cookie is not null && seen.Add(cookie))
            {
                results.Add(cookie);
            }
        }

        return results;
    }
}
