namespace AccountManager.Core.Services;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogSeverity Severity, string Message, string? Account = null);

/// <summary>In-memory activity feed shown in the UI, mirrored to a daily file.</summary>
public sealed class ActivityLog
{
    private const int Capacity = 2000;
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly string? _directory;

    public ActivityLog(string? directory = null)
    {
        _directory = directory;
    }

    public event Action<LogEntry>? Added;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public void Info(string message, string? account = null) => Add(LogSeverity.Info, message, account);

    public void Success(string message, string? account = null) => Add(LogSeverity.Success, message, account);

    public void Warning(string message, string? account = null) => Add(LogSeverity.Warning, message, account);

    public void Error(string message, string? account = null) => Add(LogSeverity.Error, message, account);

    public void Add(LogSeverity severity, string message, string? account = null)
    {
        var entry = new LogEntry(DateTime.Now, severity, message, account);
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }

            WriteToFile(entry);
        }

        Added?.Invoke(entry);
    }

    private void WriteToFile(LogEntry entry)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(_directory, $"activity-{entry.Time:yyyy-MM-dd}.log");
            string who = entry.Account is null ? "" : $" [{entry.Account}]";
            File.AppendAllText(path, $"{entry.Time:HH:mm:ss} {entry.Severity,-7}{who} {entry.Message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
