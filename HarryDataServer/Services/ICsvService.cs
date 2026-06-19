namespace HarryDataServer.Services;

/// <summary>
/// Main CSV export (CLAUDE.md section 13). On each Part Exit, writes one wide row
/// containing all measurement values from all cameras for that part. Same
/// background-queue pattern as the other processors.
/// </summary>
public interface ICsvService
{
    int PendingCount { get; }
    long TotalRows { get; }
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
