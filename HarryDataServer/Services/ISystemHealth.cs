namespace HarryDataServer.Services;

/// <summary>Severity of a reported fault. Higher value = more severe.</summary>
public enum HealthSeverity
{
    Warning = 1,
    Error = 2,
}

/// <summary>One active fault, keyed by its source.</summary>
public sealed record HealthFault(string Source, HealthSeverity Severity, string Message);

/// <summary>
/// Immutable view of the overall system health at one instant.
/// <see cref="SignalWord"/> is the word the SPS KeepAlive channel reports:
/// "OK" when healthy, otherwise "WARNING" / "ERROR".
/// </summary>
public sealed record HealthSnapshot(
    HealthSeverity? Worst,
    string SignalWord,
    string Message,
    IReadOnlyList<HealthFault> Faults)
{
    public bool IsHealthy => Worst is null;
}

/// <summary>
/// Central, thread-safe health/fault registry (CLAUDE.md section 5, channel 1).
/// Every processor reports faults here and clears them when the condition is gone;
/// the SPS KeepAlive channel surfaces the aggregated state to the PLC so the
/// "blind" server cannot silently swallow failures. Reads are lock-free.
/// </summary>
public interface ISystemHealth
{
    /// <summary>
    /// Register or update a fault for <paramref name="source"/>. A later report for
    /// the same source replaces the previous one. Pass <paramref name="ttl"/> for
    /// transient events (e.g. one rejected row) that should auto-expire; omit it for
    /// state faults (DB down, flush failing) that stay until <see cref="Clear"/>.
    /// </summary>
    void Report(string source, HealthSeverity severity, string message, TimeSpan? ttl = null);

    /// <summary>Remove the fault for <paramref name="source"/> (condition resolved).</summary>
    void Clear(string source);

    /// <summary>Aggregate the currently active (non-expired) faults.</summary>
    HealthSnapshot Snapshot();

    /// <summary>Raised whenever the set of active faults changes (for UI binding).</summary>
    event Action? Changed;
}

/// <summary>Stable source keys so reports and clears always match.</summary>
public static class HealthSources
{
    public const string Database = "Database";
    public const string Measurements = "Measurements";
    public const string MeasurementQueue = "MeasurementQueue";
    public const string Settings = "Settings";
    public const string PartExit = "PartExit";
    public const string Csv = "Csv";
    public const string Msa = "Msa";
}
