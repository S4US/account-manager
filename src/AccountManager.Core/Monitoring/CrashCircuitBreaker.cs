namespace AccountManager.Core.Monitoring;

public readonly record struct RestartDecision(bool Allowed, TimeSpan Delay, string Reason);

/// <summary>
/// Decides whether and when to restart an account after a crash. Backs off exponentially and stops restarting
/// an account that keeps crashing, so a broken client can't loop forever.
/// </summary>
public sealed class CrashCircuitBreaker
{
    private readonly Dictionary<long, Record> _records = [];
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    public CrashCircuitBreaker(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Consecutive crashes that open the circuit.</summary>
    public int Threshold { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Running this long without crashing resets the count.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long restarts stay blocked once the circuit opens. Grows with further crashes.</summary>
    public TimeSpan OpenFor { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan MaxOpenFor { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Records a crash and returns whether a restart is allowed and how long to wait first.</summary>
    public RestartDecision RecordCrash(long accountId)
    {
        lock (_lock)
        {
            DateTime now = _clock();
            Record record = Get(accountId);

            if (record.Count > 0 && now - record.LastCrash > StableAfter)
            {
                record.Count = 0;
                record.OpenUntil = DateTime.MinValue;
            }

            record.Count++;
            record.LastCrash = now;

            if (record.Count >= Threshold)
            {
                int over = record.Count - Threshold;
                double minutes = Math.Min(OpenFor.TotalMinutes * Math.Pow(1.5, over), MaxOpenFor.TotalMinutes);
                record.OpenUntil = now + TimeSpan.FromMinutes(minutes);
                return new RestartDecision(false, record.OpenUntil - now,
                    $"Crashed {record.Count} times in a row. Automatic restarts are paused for {FormatDuration(record.OpenUntil - now)}.");
            }

            TimeSpan delay = Backoff(record.Count);
            return new RestartDecision(true, delay, $"Restarting in {FormatDuration(delay)} (crash {record.Count} of {Threshold}).");
        }
    }

    /// <summary>Whether restarts are currently allowed for this account.</summary>
    public bool IsOpen(long accountId)
    {
        lock (_lock)
        {
            return _records.TryGetValue(accountId, out Record? r) && _clock() < r.OpenUntil;
        }
    }

    public void RecordStable(long accountId)
    {
        lock (_lock)
        {
            _records.Remove(accountId);
        }
    }

    public void Reset(long accountId) => RecordStable(accountId);

    internal TimeSpan Backoff(int consecutiveCrashes)
    {
        int n = Math.Clamp(consecutiveCrashes, 1, 8);
        double seconds = BaseDelay.TotalSeconds * Math.Pow(1.8, n - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    private Record Get(long accountId)
    {
        if (!_records.TryGetValue(accountId, out Record? record))
        {
            _records[accountId] = record = new Record();
        }

        return record;
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{Math.Ceiling(span.TotalMinutes):0} min" : $"{Math.Ceiling(span.TotalSeconds):0} s";

    private sealed class Record
    {
        public int Count;
        public DateTime LastCrash;
        public DateTime OpenUntil = DateTime.MinValue;
    }
}
