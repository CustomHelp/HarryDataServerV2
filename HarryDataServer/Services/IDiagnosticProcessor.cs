namespace HarryDataServer.Services;

/// <summary>
/// Consumes "Diagnostic" telegrams from the cameras and writes them directly to
/// CSV (never to the main DB — CLAUDE.md section 4). Same background-queue pattern
/// as <see cref="IMeasurementProcessor"/>.
/// </summary>
public interface IDiagnosticProcessor
{
    int PendingCount { get; }
    long TotalWritten { get; }
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
