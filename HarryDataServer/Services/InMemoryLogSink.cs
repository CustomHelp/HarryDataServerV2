using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace HarryDataServer.Services;

/// <summary>
/// Serilog sink that keeps the most recent log entries in memory for the UI Log tab —
/// structured with a collapsed level and a heuristic source so the tab can filter and
/// colour them. Thread-safe.
///
/// Two ring buffers are kept: a general one (all levels, max 1000) for the live tail, and
/// a dedicated Warning+Error one (max 1000). Idle keepalive Debug chatter would otherwise
/// evict warnings/errors from the general buffer within seconds; the dedicated buffer means
/// recent Warning/Error entries stay visible regardless of Debug volume (Option 4).
/// <see cref="Snapshot"/> merges both (de-duplicated, time-ordered).
/// </summary>
public sealed partial class InMemoryLogSink : ILogBuffer, ILogEventSink
{
    private const int Capacity = 1000;
    private const int ImportantCapacity = 1000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new(Capacity);
    private readonly Queue<LogEntry> _important = new(ImportantCapacity);
    private int _errorCount;
    private int _warningCount;

    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int WarningCount => Volatile.Read(ref _warningCount);
    public event Action? Changed;

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += " | " + logEvent.Exception.Message;

        var level = Collapse(logEvent.Level);
        var text = $"{logEvent.Timestamp:HH:mm:ss.fff} [{Abbrev(logEvent.Level)}] {message}";
        var entry = new LogEntry(logEvent.Timestamp.DateTime, level, Classify(message), text);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
                _entries.Dequeue();

            // Same instance also goes into the dedicated Warning+Error buffer so it survives
            // Debug-chatter eviction from the general buffer (de-duped by reference in Snapshot).
            if (level is LogLevelKind.Warning or LogLevelKind.Error)
            {
                _important.Enqueue(entry);
                while (_important.Count > ImportantCapacity)
                    _important.Dequeue();
            }
        }

        if (level == LogLevelKind.Error)
            Interlocked.Increment(ref _errorCount);
        else if (level == LogLevelKind.Warning)
            Interlocked.Increment(ref _warningCount);

        Changed?.Invoke();
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            // Merge the general tail with any Warning/Error entries already evicted from it.
            // The same LogEntry instance is enqueued in both buffers, so de-dup by reference,
            // then order oldest-first (the Log tab renders newest-on-top from this order).
            var seen = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
            var merged = new List<LogEntry>(_entries.Count + _important.Count);
            foreach (var e in _entries)
                if (seen.Add(e))
                    merged.Add(e);
            foreach (var e in _important)
                if (seen.Add(e))
                    merged.Add(e);
            merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return merged;
        }
    }

    private static LogLevelKind Collapse(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => LogLevelKind.Warning,
        LogEventLevel.Error or LogEventLevel.Fatal => LogLevelKind.Error,
        _ => LogLevelKind.Info,
    };

    private static LogSource Classify(string message)
    {
        if (CameraRegex().IsMatch(message) || message.Contains("camera", StringComparison.OrdinalIgnoreCase))
            return LogSource.Camera;
        if (message.Contains("SPS", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Part Exit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("KeepAlive", StringComparison.OrdinalIgnoreCase))
            return LogSource.Sps;
        if (message.Contains("Collage", StringComparison.OrdinalIgnoreCase))
            return LogSource.Collage;
        if (message.Contains("MSA", StringComparison.OrdinalIgnoreCase))
            return LogSource.Msa;
        if (message.Contains("CSV", StringComparison.OrdinalIgnoreCase))
            return LogSource.Csv;
        if (message.Contains("Database", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MySQL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("partition", StringComparison.OrdinalIgnoreCase)
            || message.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || message.Contains("definition", StringComparison.OrdinalIgnoreCase))
            return LogSource.Database;
        return LogSource.System;
    }

    private static string Abbrev(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };

    [GeneratedRegex(@"\bM\d\d_ST", RegexOptions.IgnoreCase)]
    private static partial Regex CameraRegex();
}
