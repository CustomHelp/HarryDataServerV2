using System.Globalization;
using HarryDataServer.Services;
using MySqlConnector;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Manages day/range partitions on the measurement tables (CLAUDE.md section 8,
/// step 5 + "Partition Management"). Monthly partitions are created ahead of time
/// by splitting the catch-all <c>p_future</c> partition; retention removes old
/// data with DROP PARTITION (never DELETE).
/// </summary>
public sealed class PartitionManager
{
    /// <summary>Number of future months (in addition to the current one) to keep provisioned.</summary>
    public const int MonthsAhead = 3;

    private const string FuturePartition = "p_future";

    private readonly MySqlRepository _repo;
    private readonly ILogService _log;

    public PartitionManager(MySqlRepository repo, ILogService log)
    {
        _repo = repo;
        _log = log;
    }

    /// <summary>Ensure partitions exist for the current month + next 3 months on all partitioned tables.</summary>
    public async Task EnsurePartitionsAsync(CancellationToken ct = default)
    {
        foreach (var table in DatabaseSchema.PartitionedTables)
            await EnsureMonthlyPartitionsAsync(table, ct).ConfigureAwait(false);
    }

    private async Task EnsureMonthlyPartitionsAsync(string table, CancellationToken ct)
    {
        await using var conn = await _repo.OpenAsync(ct).ConfigureAwait(false);
        var existing = await GetPartitionNamesAsync(conn, table, ct).ConfigureAwait(false);

        var firstOfThisMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var created = 0;

        for (var offset = 0; offset <= MonthsAhead; offset++)
        {
            var month = firstOfThisMonth.AddMonths(offset);
            var name = PartitionName(month);
            if (existing.Contains(name))
                continue;

            // Boundary is the first day of the *following* month (TO_DAYS upper bound).
            var boundary = month.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var sql =
                $"ALTER TABLE `{table}` REORGANIZE PARTITION {FuturePartition} INTO (" +
                $"PARTITION {name} VALUES LESS THAN (TO_DAYS('{boundary}')), " +
                $"PARTITION {FuturePartition} VALUES LESS THAN MAXVALUE);";

            try
            {
                await using var cmd = new MySqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                created++;
                existing.Add(name);
                _log.Information("Created partition {Partition} on {Table} (< {Boundary}).", name, table, boundary);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create partition {Partition} on {Table}.", name, table);
            }
        }

        if (created == 0)
            _log.Debug("Partitions on {Table} already provisioned.", table);
    }

    /// <summary>
    /// Drop partitions whose entire month is older than <paramref name="retentionDays"/>.
    /// Used by the retention job (CLAUDE.md "Never use DELETE for retention").
    /// </summary>
    public async Task DropOldPartitionsAsync(string table, int retentionDays, CancellationToken ct = default)
    {
        if (retentionDays <= 0)
            return;

        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);

        await using var conn = await _repo.OpenAsync(ct).ConfigureAwait(false);
        var names = await GetPartitionNamesAsync(conn, table, ct).ConfigureAwait(false);

        foreach (var name in names)
        {
            if (string.Equals(name, FuturePartition, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryParsePartitionMonth(name, out var month))
                continue;

            // The partition holds rows with measured_at < first-of-next-month.
            var upperExclusive = month.AddMonths(1);
            if (upperExclusive > cutoff)
                continue; // Partition still within retention window.

            try
            {
                await using var cmd = new MySqlCommand($"ALTER TABLE `{table}` DROP PARTITION {name};", conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _log.Information("Dropped expired partition {Partition} on {Table} (retention {Days} days).",
                    name, table, retentionDays);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to drop partition {Partition} on {Table}.", name, table);
            }
        }
    }

    private static async Task<HashSet<string>> GetPartitionNamesAsync(
        MySqlConnection conn, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT PARTITION_NAME
FROM INFORMATION_SCHEMA.PARTITIONS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND PARTITION_NAME IS NOT NULL;";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            names.Add(reader.GetString(0));

        return names;
    }

    private static string PartitionName(DateTime month) =>
        $"p_{month.ToString("yyyy_MM", CultureInfo.InvariantCulture)}";

    private static bool TryParsePartitionMonth(string partitionName, out DateTime month)
    {
        month = default;
        // Expected form: p_YYYY_MM
        if (!partitionName.StartsWith("p_", StringComparison.OrdinalIgnoreCase))
            return false;

        var body = partitionName.Substring(2);
        return DateTime.TryParseExact(body, "yyyy_MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out month);
    }
}
