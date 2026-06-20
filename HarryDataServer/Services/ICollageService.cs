namespace HarryDataServer.Services;

/// <summary>
/// Generates a collage per finished OK part (CLAUDE.md section 12). Triggered by
/// Part Exit, runs on a background task, never blocks the SPS thread.
/// </summary>
public interface ICollageService
{
    /// <summary>Collages waiting to be composed.</summary>
    int PendingCount { get; }

    /// <summary>Total collages written since start.</summary>
    long TotalGenerated { get; }

    /// <summary>Raised when counters change (for UI binding).</summary>
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
