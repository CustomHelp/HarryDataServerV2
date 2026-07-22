using MySqlConnector;

namespace HarryPareto;

/// <summary>Per-feature defect aggregate for one time window.</summary>
public sealed record ParetoAgg(
    int DefId, string Controller, string DisplayName, string ModuleRef, string Module,
    int AffectedParts, int Occurrences);

/// <summary>KPI head numbers for a window: inspected parts vs. bad parts (distinct serials).</summary>
public sealed record ParetoKpi(int Inspected, int Bad)
{
    public double RatePct => Inspected > 0 ? 100.0 * Bad / Inspected : 0.0;
}

/// <summary>Per-controller judgement tally (used for the "camera did not judge" warning, task E3).</summary>
public sealed record ControllerJudge(string Controller, long Judged, long NotJudged);

/// <summary>
/// Strictly read-only queries for the Pareto view. Every call opens its own pooled connection from
/// the current <see cref="ParetoSettings"/> and only issues SELECTs.
///
/// The defect metric is the number of AFFECTED PARTS per feature — <c>COUNT(DISTINCT serial)</c>
/// where <c>result_status = 0</c> — with the total occurrence count as a secondary figure. Status 2
/// ("not evaluated") never counts. Only production is considered (<c>run_type = 0</c>); MSA/diagnostic
/// rows live in other tables and are excluded. BOTH measurement tables are combined via UNION ALL —
/// <c>measurements_serial</c> (frame serial) AND <c>measurements_serial_trimmer</c> (its
/// <c>serial_trimmer</c> column) — so M20/M21 trimmer defects are included. There is deliberately NO
/// join to <c>dmcserial</c>, so parts that have not yet finished still count (task E2).
/// </summary>
public sealed class ParetoDb
{
    private readonly ParetoSettings _settings;

    public ParetoDb(ParetoSettings settings) => _settings = settings;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>Connectivity probe — returns false instead of throwing.</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Active controller names for the filter drop-down.</summary>
    public async Task<List<string>> GetControllersAsync(CancellationToken ct = default)
    {
        var list = new List<string>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(
            "SELECT camera_name FROM cameras WHERE active = 1 ORDER BY camera_name;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(r.GetString(0));
        return list;
    }

    /// <summary>KPI: distinct inspected parts (status 0/1) and distinct bad parts (status 0) in the window.</summary>
    public async Task<ParetoKpi> GetKpiAsync(DateTime from, DateTime to, string? controller, CancellationToken ct = default)
    {
        var hasCtrl = !string.IsNullOrEmpty(controller);
        var join = hasCtrl
            ? " JOIN measurement_definitions d ON d.id = x.definition_id JOIN cameras c ON c.id = d.camera_id"
            : string.Empty;
        var where = hasCtrl ? " WHERE c.camera_name = @ctrl" : string.Empty;

        var sql = $@"
SELECT COUNT(DISTINCT x.serial),
       COUNT(DISTINCT CASE WHEN x.result_status = 0 THEN x.serial END)
FROM (
    SELECT definition_id, serial_number  AS serial, result_status FROM measurements_serial
    WHERE run_type = 0 AND result_status IN (0, 1) AND measured_at >= @from AND measured_at < @to
    UNION ALL
    SELECT definition_id, serial_trimmer AS serial, result_status FROM measurements_serial_trimmer
    WHERE run_type = 0 AND result_status IN (0, 1) AND measured_at >= @from AND measured_at < @to
) x{join}{where};";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        if (hasCtrl) cmd.Parameters.AddWithValue("@ctrl", controller);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return new ParetoKpi(0, 0);
        return new ParetoKpi(
            r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0)),
            r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1)));
    }

    /// <summary>Per-feature defect aggregate (affected parts + occurrences) for the window.</summary>
    public async Task<List<ParetoAgg>> GetAggAsync(DateTime from, DateTime to, string? controller, CancellationToken ct = default)
    {
        var hasCtrl = !string.IsNullOrEmpty(controller);
        var ctrlClause = hasCtrl ? " AND c.camera_name = @ctrl" : string.Empty;

        var sql = $@"
SELECT d.id, c.camera_name, d.display_name, d.module_ref, c.module,
       COUNT(DISTINCT x.serial) AS affected, COUNT(*) AS occ
FROM (
    SELECT definition_id, serial_number  AS serial FROM measurements_serial
    WHERE result_status = 0 AND run_type = 0 AND measured_at >= @from AND measured_at < @to
    UNION ALL
    SELECT definition_id, serial_trimmer AS serial FROM measurements_serial_trimmer
    WHERE result_status = 0 AND run_type = 0 AND measured_at >= @from AND measured_at < @to
) x
JOIN measurement_definitions d ON d.id = x.definition_id
JOIN cameras c ON c.id = d.camera_id
WHERE 1 = 1{ctrlClause}
GROUP BY d.id, c.camera_name, d.display_name, d.module_ref, c.module
ORDER BY affected DESC, occ DESC;";

        var list = new List<ParetoAgg>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        if (hasCtrl) cmd.Parameters.AddWithValue("@ctrl", controller);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ParetoAgg(
                r.GetInt32(0),
                r.IsDBNull(1) ? "(none)" : r.GetString(1),
                r.IsDBNull(2) ? "(none)" : r.GetString(2),
                r.IsDBNull(3) ? "NoRef" : r.GetString(3),
                r.IsDBNull(4) ? string.Empty : r.GetString(4),
                Convert.ToInt32(r.GetValue(5)),
                Convert.ToInt32(r.GetValue(6))));
        }
        return list;
    }

    /// <summary>Controllers that produced status-2 rows (and how many they did/didn't judge) in the window.</summary>
    public async Task<List<ControllerJudge>> GetStatus2Async(DateTime from, DateTime to, string? controller, CancellationToken ct = default)
    {
        var hasCtrl = !string.IsNullOrEmpty(controller);
        var ctrlClause = hasCtrl ? " AND c.camera_name = @ctrl" : string.Empty;

        var sql = $@"
SELECT c.camera_name,
       SUM(x.result_status IN (0, 1)) AS judged,
       SUM(x.result_status = 2)       AS not_judged
FROM (
    SELECT definition_id, result_status FROM measurements_serial
    WHERE run_type = 0 AND measured_at >= @from AND measured_at < @to
    UNION ALL
    SELECT definition_id, result_status FROM measurements_serial_trimmer
    WHERE run_type = 0 AND measured_at >= @from AND measured_at < @to
) x
JOIN measurement_definitions d ON d.id = x.definition_id
JOIN cameras c ON c.id = d.camera_id
WHERE 1 = 1{ctrlClause}
GROUP BY c.camera_name
HAVING not_judged > 0
ORDER BY not_judged DESC;";

        var list = new List<ControllerJudge>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        if (hasCtrl) cmd.Parameters.AddWithValue("@ctrl", controller);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ControllerJudge(
                r.IsDBNull(0) ? "(none)" : r.GetString(0),
                r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1)),
                r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2))));
        }
        return list;
    }
}
