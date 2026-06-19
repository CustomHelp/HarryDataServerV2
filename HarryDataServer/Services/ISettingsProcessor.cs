namespace HarryDataServer.Services;

/// <summary>
/// Consumes "Settings" telegrams from the cameras and persists the Min/Max limits
/// to the <c>settings</c> history table via a background queue. Same pattern as
/// <see cref="IMeasurementProcessor"/>: receive threads only enqueue.
/// </summary>
public interface ISettingsProcessor
{
    int PendingCount { get; }
    long TotalInserted { get; }
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
