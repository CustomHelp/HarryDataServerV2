namespace HarryDataServer.Services;

/// <summary>
/// Consumes "Results" telegrams from the cameras and persists them to the
/// partitioned measurement tables via a background queue (CLAUDE.md section 14).
/// The camera receive threads only enqueue; this processor performs all DB I/O.
/// </summary>
public interface IMeasurementProcessor
{
    /// <summary>Number of measurements currently waiting to be written.</summary>
    int PendingCount { get; }

    /// <summary>Total measurements inserted since startup.</summary>
    long TotalInserted { get; }

    /// <summary>Raised after each flush so the UI can show throughput.</summary>
    event Action? StatsChanged;

    /// <summary>Subscribe to the cameras and start the flush loop.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Unsubscribe, flush remaining measurements, and stop.</summary>
    Task StopAsync();
}
