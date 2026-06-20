namespace HarryDataServer.Services;

/// <summary>
/// Ring buffer of recent log lines for the UI Log tab, plus running error/warning
/// counts for the status bar. Fed by an in-memory Serilog sink.
/// </summary>
public interface ILogBuffer
{
    /// <summary>Recent log lines, oldest first.</summary>
    IReadOnlyList<string> Snapshot();

    /// <summary>Number of Error/Fatal events since start.</summary>
    int ErrorCount { get; }

    /// <summary>Number of Warning events since start.</summary>
    int WarningCount { get; }

    /// <summary>Raised when a new line is appended.</summary>
    event Action? Changed;
}
