using System.Collections.Concurrent;

namespace HarryDataServer.Services;

/// <summary>
/// Thread-safe implementation of <see cref="ISystemHealth"/>. Backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by source so reports/clears
/// from any thread are lock-free, and <see cref="Snapshot"/> can be called on the
/// SPS receive thread at KeepAlive frequency without contention.
/// </summary>
public sealed class SystemHealthService : ISystemHealth
{
    private sealed record Entry(HealthSeverity Severity, string Message, DateTime? ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _faults = new();

    public event Action? Changed;

    public void Report(string source, HealthSeverity severity, string message, TimeSpan? ttl = null)
    {
        if (string.IsNullOrEmpty(source))
            return;

        var expires = ttl.HasValue ? DateTime.UtcNow + ttl.Value : (DateTime?)null;
        _faults[source] = new Entry(severity, message ?? string.Empty, expires);
        Changed?.Invoke();
    }

    public void Clear(string source)
    {
        if (_faults.TryRemove(source, out _))
            Changed?.Invoke();
    }

    public HealthSnapshot Snapshot()
    {
        var now = DateTime.UtcNow;
        var active = new List<HealthFault>();

        foreach (var kvp in _faults)
        {
            // Drop expired transient faults lazily as we read them.
            if (kvp.Value.ExpiresAtUtc is { } expiry && expiry <= now)
            {
                _faults.TryRemove(kvp.Key, out _);
                continue;
            }

            active.Add(new HealthFault(kvp.Key, kvp.Value.Severity, kvp.Value.Message));
        }

        if (active.Count == 0)
            return new HealthSnapshot(null, "OK", string.Empty, active);

        // Worst severity wins the signal word; messages are listed worst-first.
        var worst = active.Max(f => f.Severity);
        var ordered = active
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Source, StringComparer.Ordinal)
            .ToList();

        var signalWord = worst == HealthSeverity.Error ? "ERROR" : "WARNING";
        var message = string.Join(" | ", ordered.Select(f => f.Message));
        return new HealthSnapshot(worst, signalWord, message, ordered);
    }
}
