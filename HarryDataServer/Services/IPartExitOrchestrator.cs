namespace HarryDataServer.Services;

/// <summary>
/// Orchestrates the timing-critical part-exit sequence (CLAUDE.md section 5, channel 2):
/// save dmcserial, then run CSV / Collage / image handling in parallel, then return
/// success so the SPS server can ACK. A new part arrives every ~450 ms.
/// </summary>
public interface IPartExitOrchestrator
{
    /// <summary>Total parts processed since start.</summary>
    long TotalProcessed { get; }

    /// <summary>Last per-task timing string: "CSV Xms | Collage Xms | Images Xms | Total Xms".</summary>
    string LastTiming { get; }

    /// <summary>Raised when counters/timing change (for UI binding).</summary>
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
