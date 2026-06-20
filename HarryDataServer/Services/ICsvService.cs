using HarryDataServer.Models;

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

    /// <summary>Path of the CSV file currently being written, or null if none yet.</summary>
    string? ActiveFilePath { get; }

    /// <summary>Local timestamp of the last row written, or null if nothing written yet.</summary>
    DateTime? LastWriteTime { get; }

    event Action? StatsChanged;

    /// <summary>Write one finished part's CSV row (called by the part-exit orchestrator).
    /// Returns false on failure so the SPS ACK can report it.</summary>
    Task<bool> WritePartAsync(SpsPartExitData part, CancellationToken ct = default);

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
