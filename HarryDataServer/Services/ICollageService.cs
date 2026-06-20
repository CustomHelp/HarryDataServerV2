using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Generates a collage per finished OK part (CLAUDE.md section 12). Invoked by the
/// part-exit orchestrator; composition runs on a background thread.
/// </summary>
public interface ICollageService
{
    /// <summary>Collages waiting to be composed (always 0 now — synchronous).</summary>
    int PendingCount { get; }

    /// <summary>Total collages written since start.</summary>
    long TotalGenerated { get; }

    /// <summary>Raised when counters change (for UI binding).</summary>
    event Action? StatsChanged;

    /// <summary>Compose the collage for one OK part. Returns false only on a genuine failure.</summary>
    Task<bool> ComposeForPartAsync(SpsPartExitData part, CancellationToken ct);

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
