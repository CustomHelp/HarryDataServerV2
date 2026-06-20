namespace HarryDataServer.Services;

/// <summary>Collapsed log level for UI filtering/colouring.</summary>
public enum LogLevelKind { Info, Warning, Error }

/// <summary>Subsystem a log line is attributed to (heuristic, for the source filter).</summary>
public enum LogSource { Camera, Sps, Database, Csv, Collage, Msa, System }

/// <summary>One structured log line held in the UI ring buffer.</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevelKind Level, LogSource Source, string Text);

/// <summary>
/// Ring buffer (max 1000) of recent structured log entries for the UI Log tab, plus
/// running error/warning counts for the status bar. Fed by an in-memory Serilog sink.
/// </summary>
public interface ILogBuffer
{
    /// <summary>Recent log entries, oldest first.</summary>
    IReadOnlyList<LogEntry> Snapshot();

    /// <summary>Number of Error/Fatal events since start.</summary>
    int ErrorCount { get; }

    /// <summary>Number of Warning events since start.</summary>
    int WarningCount { get; }

    /// <summary>Raised when a new entry is appended.</summary>
    event Action? Changed;
}
