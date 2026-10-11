using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Monitoring;

/// <summary>
/// Follows the log file of one Roblox client and reports session events.
///
/// The log is identified by the timestamp in its file name (close to the process start time) and, when present,
/// the browser tracker id Roblox writes near the top, which is unique per account.
/// </summary>
public sealed partial class RobloxLogWatcher : IDisposable
{
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(2);
    private const int MaxInitialBytes = 256 * 1024;

    private readonly string _logsDirectory;
    private readonly DateTime _processStartUtc;
    private readonly string _browserTrackerId;
    private readonly HashSet<string> _excluded;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private FileStream? _stream;
    private StreamReader? _reader;
    private bool _disposed;

    /// <param name="excludedFiles">Log files already claimed by other clients.</param>
    public RobloxLogWatcher(string logsDirectory, DateTime processStartUtc, string browserTrackerId, IEnumerable<string>? excludedFiles = null)
    {
        _logsDirectory = logsDirectory;
        _processStartUtc = processStartUtc;
        _browserTrackerId = browserTrackerId;
        _excluded = new HashSet<string>(excludedFiles ?? [], StringComparer.OrdinalIgnoreCase);
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a background thread for every classified event, with the original line.</summary>
    public event Action<LogEvent, string>? EventDetected;

    public string? AttachedFile { get; private set; }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
        }
    }

    private void Poll()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_reader is null)
                {
                    if (DateTime.UtcNow - _processStartUtc > DiscoveryTimeout)
                    {
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }

                    TryAttach();
                }

                ReadNewLines();
            }
            catch (IOException)
            {
                // File rotated or locked momentarily; the next poll retries.
            }
        }
    }

    private void TryAttach()
    {
        if (!Directory.Exists(_logsDirectory))
        {
            return;
        }

        foreach (FileInfo candidate in FindCandidates(_logsDirectory, _processStartUtc, _excluded))
        {
            var stream = new FileStream(candidate.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var reader = new StreamReader(stream, Encoding.UTF8);
            string? trackerInFile = FindBrowserId(reader);
            bool mismatch = trackerInFile is not null && !trackerInFile.Equals(_browserTrackerId, StringComparison.Ordinal);

            // The tracker line appears within the first second of startup; while the client is young, wait for it
            // rather than guessing between logs of clients launched close together.
            bool undecided = trackerInFile is null && DateTime.UtcNow - _processStartUtc < TimeSpan.FromSeconds(20);
            if (mismatch || undecided)
            {
                reader.Dispose();
                continue;
            }

            // Skip the backlog of very large files, then process everything after it.
            if (stream.Length > MaxInitialBytes)
            {
                stream.Seek(-MaxInitialBytes, SeekOrigin.End);
                reader.DiscardBufferedData();
                reader.ReadLine();
            }
            else
            {
                stream.Seek(0, SeekOrigin.Begin);
                reader.DiscardBufferedData();
            }

            _stream = stream;
            _reader = reader;
            AttachedFile = candidate.FullName;
            return;
        }
    }

    private void ReadNewLines()
    {
        if (_reader is null)
        {
            return;
        }

        string? line;
        while ((line = _reader.ReadLine()) is not null)
        {
            LogEvent kind = LogLineClassifier.Classify(line);
            if (kind != LogEvent.None)
            {
                EventDetected?.Invoke(kind, line);
            }
        }
    }

    /// <summary>Player logs whose embedded start time is within the match window, closest first.</summary>
    internal static IEnumerable<FileInfo> FindCandidates(string directory, DateTime processStartUtc, IReadOnlySet<string> excluded)
    {
        return new DirectoryInfo(directory)
            .EnumerateFiles("*_Player_*.log")
            .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) && !excluded.Contains(f.FullName))
            .Select(f => (File: f, Started: ParseStartTime(f.Name)))
            .Where(x => x.Started is not null && (x.Started.Value - processStartUtc).Duration() <= MatchWindow)
            .OrderBy(x => (x.Started!.Value - processStartUtc).Duration())
            .Select(x => x.File)
            .ToList();
    }

    /// <summary>Extracts the UTC start time from names like "0.742.0.7421053_20261011T121329Z_Player_AFFA5_last.log".</summary>
    internal static DateTime? ParseStartTime(string fileName)
    {
        Match match = StartTimeRegex().Match(fileName);
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime value)
            ? value
            : null;
    }

    private static string? FindBrowserId(StreamReader reader)
    {
        for (int i = 0; i < 400; i++)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                return null;
            }

            Match match = BrowserIdRegex().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"_(\d{8}T\d{6}Z)_")]
    private static partial Regex StartTimeRegex();

    [GeneratedRegex(@"[&?]browserid=(\d+)")]
    private static partial Regex BrowserIdRegex();
}
