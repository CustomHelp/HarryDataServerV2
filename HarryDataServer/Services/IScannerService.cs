namespace HarryDataServer.Services;

/// <summary>One received scan: server timestamp + the raw DMC code.</summary>
public sealed record ScanEntry(DateTime Timestamp, string Code);

/// <summary>
/// DMC handheld-scanner bridge. Listens for the scanner (TCP client) on the fixed scanner port,
/// keeps a rolling in-memory buffer of the most recent scans for the Scanner tab, and rebroadcasts
/// every scan to the companion apps via the companion broadcast server. Cleared on restart (no
/// persistence).
/// </summary>
public interface IScannerService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    /// <summary>Ring-buffer capacity (from [Scanner] MaxScanHistoryRows) — the UI mirrors this cap.</summary>
    int MaxRows { get; }

    /// <summary>True while the scanner-ingest listener is bound and accepting.</summary>
    bool IsListening { get; }

    /// <summary>True while at least one scanner connection is open.</summary>
    bool ScannerConnected { get; }

    /// <summary>Number of companion apps currently connected to the broadcast server.</summary>
    int CompanionClientCount { get; }

    /// <summary>Snapshot of the current ring buffer, oldest first.</summary>
    IReadOnlyList<ScanEntry> RecentScans();

    /// <summary>Raised (background thread) for each received scan; the UI marshals to the dispatcher.</summary>
    event Action<ScanEntry>? ScanReceived;

    /// <summary>Raised when connection state changes (scanner or companion clients) — UI status refresh.</summary>
    event Action? StatusChanged;
}
