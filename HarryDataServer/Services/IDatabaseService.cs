using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>Lifecycle state of the database subsystem, surfaced to the UI.</summary>
public enum DatabaseStatus
{
    NotStarted,
    Connecting,
    Initializing,
    Ready,
    Failed,
}

/// <summary>Live production figures for the Overview tab (from <c>dmcserial</c>).</summary>
public sealed record ProductionSnapshot(long TodayOk, long TodayNg, DateTime? LastPartExit, string ActiveOrder);

/// <summary>
/// High-level database orchestration. Runs the full startup logic from CLAUDE.md
/// section 8 (connect with backoff, create schema, provision partitions, sync
/// cameras and definitions) and provides connections to the rest of the app.
/// </summary>
public interface IDatabaseService
{
    DatabaseStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes (for UI binding).</summary>
    event Action<DatabaseStatus>? StatusChanged;

    /// <summary>
    /// Run the complete startup sequence. Retries the initial connection with
    /// exponential backoff until it succeeds or <paramref name="ct"/> is cancelled.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Open a pooled connection to the application database.</summary>
    Task<MySqlConnection> OpenConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Approximate row counts per owned table (from INFORMATION_SCHEMA — cheap, good
    /// enough for a live UI counter). Returns an empty map if the DB is not ready.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetRowCountsAsync(CancellationToken ct = default);

    /// <summary>Today's OK/NG counts, last part-exit time and current order for the Overview tab.</summary>
    Task<ProductionSnapshot> GetProductionSnapshotAsync(CancellationToken ct = default);
}
