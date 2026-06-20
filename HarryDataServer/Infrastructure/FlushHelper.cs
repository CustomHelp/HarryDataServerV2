using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Shared write-with-isolation logic for the queue-backed DB processors.
///
/// Without this, a single failing batch INSERT loses every already-dequeued row and
/// is swallowed in a catch (CLAUDE.md "blind running" concern). Instead we:
///   1. try the batch write — the fast path (must be atomic / all-or-nothing);
///   2. on failure, retry row by row so the good rows still land;
///   3. a failed row is only treated as <b>poison</b> (bad data, dropped) once a later
///      row succeeds — proving the database is actually up. Until then it is held;
///   4. if many rows fail in a row, the database is assumed <b>down</b>: the held rows
///      and the remainder are <b>requeued</b> (no data loss) and a sticky ERROR is raised.
///
/// This ordering matters: during a real outage the first rows must NOT be misread as
/// poison and dropped — they are good data the DB simply could not accept yet.
/// </summary>
internal static class FlushHelper
{
    /// <summary>Consecutive per-row failures that flip "one poison row" into "DB is down".</summary>
    public const int DbDownThreshold = 10;

    /// <summary>How long a transient warning (rejected row / retrying) stays visible to the SPS.</summary>
    private static readonly TimeSpan TransientTtl = TimeSpan.FromMinutes(2);

    /// <param name="rows">The drained slice to persist.</param>
    /// <param name="batchWriter">Writes all rows atomically (opens its own connection/transaction); returns the count written.</param>
    /// <param name="singleWriter">Writes exactly one row (opens its own connection); throws on failure.</param>
    /// <param name="requeue">Puts the given rows back on the queue (DB-down path; no data loss).</param>
    /// <param name="describe">Short human description of a row for the log on poison drop.</param>
    /// <returns>The number of rows successfully written.</returns>
    public static async Task<int> WriteAsync<T>(
        IReadOnlyList<T> rows,
        Func<IReadOnlyList<T>, CancellationToken, Task<int>> batchWriter,
        Func<T, CancellationToken, Task> singleWriter,
        Action<IReadOnlyList<T>> requeue,
        ISystemHealth health,
        ILogService log,
        string source,
        Func<T, string> describe,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return 0;

        // Fast path: the whole batch succeeds atomically.
        try
        {
            var written = await batchWriter(rows, ct).ConfigureAwait(false);
            health.Clear(source);
            return written;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            requeue(rows);
            return 0;
        }
        catch (Exception ex)
        {
            log.Warning("{Source}: batch write failed ({Message}); isolating rows.", source, ex.Message);
        }

        // Isolation path: retry row by row.
        var ok = 0;
        var consecutiveFailures = 0;
        var pending = new List<T>();     // failed rows not yet confirmed as poison
        Exception? lastError = null;

        for (var i = 0; i < rows.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                requeue(Concat(pending, rows, i));
                return ok;
            }

            try
            {
                await singleWriter(rows[i], ct).ConfigureAwait(false);
                ok++;

                // A success proves the DB is up, so every buffered failure was genuine
                // bad data — only now is it safe to drop them as poison.
                DropPoison(pending, health, log, source, describe);
                pending.Clear();
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                requeue(Concat(pending, rows, i));
                return ok;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                pending.Add(rows[i]);
                lastError = ex;

                // Many failures back to back → the database is unavailable, not one bad
                // row. Requeue the held rows and everything still pending: no data loss.
                if (consecutiveFailures >= DbDownThreshold)
                {
                    var requeued = Concat(pending, rows, i + 1);
                    requeue(requeued);
                    health.Report(source, HealthSeverity.Error, $"Database write failing: {ex.Message}");
                    log.Error(ex, "{Source}: database appears unavailable; {Count} row(s) requeued.",
                        source, requeued.Count);
                    return ok;
                }
            }
        }

        // Reached the end with a trailing run of failures.
        if (pending.Count > 0)
        {
            if (ok > 0)
            {
                // The DB accepted other rows this pass → the trailing failures are poison.
                DropPoison(pending, health, log, source, describe);
            }
            else
            {
                // Nothing succeeded all pass: ambiguous (brief outage or all-poison). Do
                // not lose data — requeue and warn. A sustained outage escalates to ERROR
                // on a later pass once ≥threshold rows fail consecutively.
                requeue(pending);
                health.Report(source, HealthSeverity.Warning,
                    $"Database write retrying ({pending.Count} row(s)): {lastError?.Message}", TransientTtl);
                return ok;
            }
        }

        if (ok > 0)
            health.Clear(source);
        return ok;
    }

    private static void DropPoison<T>(
        List<T> poison, ISystemHealth health, ILogService log, string source, Func<T, string> describe)
    {
        foreach (var row in poison)
        {
            log.Error("{Source}: row rejected by database, dropped: {Row}", source, describe(row));
            health.Report($"{source}:rejected", HealthSeverity.Warning,
                $"Row rejected by database: {describe(row)}", TransientTtl);
        }
    }

    /// <summary>Combine the held rows with the not-yet-processed tail (rows[start..]).</summary>
    private static IReadOnlyList<T> Concat<T>(List<T> held, IReadOnlyList<T> rows, int start)
    {
        var result = new List<T>(held.Count + Math.Max(0, rows.Count - start));
        result.AddRange(held);
        for (var i = start; i < rows.Count; i++)
            result.Add(rows[i]);
        return result;
    }
}
