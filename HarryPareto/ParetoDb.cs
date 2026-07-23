using MySqlConnector;

namespace HarryPareto;

/// <summary>
/// One defective (feature × part) pair: a distinct affected serial for a single measurement
/// definition, with its occurrence count. This is the FINEST grain the Pareto works at — bars are
/// aggregated from these rows in memory (by station and/or sensor family), which is the only way to
/// keep <c>COUNT(DISTINCT serial)</c> correct across cameras/sensors (task B/C — a summed count would
/// double-count a part flagged by both KF1 and KF3 or by two sensors of the same family).
/// </summary>
public sealed record DefectPart(
    int DefId, string Controller, string DisplayName, string ModuleRef, string Module,
    string Serial, int Occurrences, string? OriginModule);

/// <summary>
/// One origin cell (task A): affected vs. inspected parts for a defect feature, broken down by the
/// origin module (M10/M11, M20/M21, …) and nest read from <c>dmcserial</c>. Both figures come from the
/// SAME feature rows (affected = status 0, inspected = status 0/1), so <see cref="RatePct"/> is a real
/// defect rate for parts of that origin.
/// </summary>
public sealed record OriginCell(string OriginModule, string Nest, int Inspected, int Affected)
{
    public double RatePct => Inspected > 0 ? 100.0 * Affected / Inspected : 0.0;
}

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

    /// <summary>
    /// Every defective (feature × part) pair in the window — one row per (definition, distinct serial)
    /// with its occurrence count AND the part's origin module (task 1b). The Pareto bars, the module
    /// chart and the trend are all aggregated from these rows in memory so the distinct-part counts stay
    /// correct under the station / sensor groupings (task B/C). Only production defects (status 0,
    /// run_type 0) are returned, from BOTH measurement tables (frame + trimmer).
    ///
    /// <para><b>Origin module.</b> Each row is LEFT-joined to <c>dmcserial</c> and carries the real
    /// origin module for its <c>module_ref</c> (<c>M1x</c>→<c>m1x_module</c> = 10/11, <c>M2x</c>→
    /// <c>m2x_module</c> = 20/21, <c>M3x</c>→<c>m3x_module</c>); <c>NULL</c> when the part has not exited
    /// yet (no <c>dmcserial</c> row). It is a LEFT join, so unfinished parts are NEVER dropped — they
    /// simply have an unknown origin. Occurrences are counted BEFORE the join (frame serial is unique in
    /// <c>dmcserial</c>; the trimmer origin is read via a scalar sub-select) so the join can never inflate
    /// the count.</para>
    /// </summary>
    public async Task<List<DefectPart>> GetDefectPartsAsync(DateTime from, DateTime to, string? controller, CancellationToken ct = default)
    {
        var hasCtrl = !string.IsNullOrEmpty(controller);
        var ctrlClause = hasCtrl ? " AND c.camera_name = @ctrl" : string.Empty;

        var sql = $@"
SELECT g.id, g.camera_name, g.display_name, g.module_ref, g.module, g.serial, g.occ,
       CASE UPPER(g.module_ref)
            WHEN 'M1X' THEN CAST(g.m1x AS CHAR)
            WHEN 'M2X' THEN CAST(g.m2x AS CHAR)
            WHEN 'M3X' THEN CAST(g.m3x AS CHAR)
            ELSE NULL END AS origin_module
FROM (
    SELECT d.id, c.camera_name, d.display_name, d.module_ref, c.module, f.serial, f.occ,
           f.m1x, f.m2x, f.m3x
    FROM (
        -- Frame table: serial_number is UNIQUE in dmcserial → 1:1 join, MAX just reads the value.
        SELECT ms.definition_id AS did, ms.serial_number AS serial, COUNT(*) AS occ,
               MAX(ds.m1x_module) AS m1x, MAX(ds.m2x_module) AS m2x, MAX(ds.m3x_module) AS m3x
        FROM measurements_serial ms
        LEFT JOIN dmcserial ds ON ds.serial_number = ms.serial_number
        WHERE ms.result_status = 0 AND ms.run_type = 0 AND ms.measured_at >= @from AND ms.measured_at < @to
        GROUP BY ms.definition_id, ms.serial_number
        UNION ALL
        -- Trimmer table (M2x only): origin via scalar sub-select so the count is never inflated.
        SELECT mt.definition_id AS did, mt.serial_trimmer AS serial, COUNT(*) AS occ,
               NULL AS m1x,
               (SELECT ds.m2x_module FROM dmcserial ds WHERE ds.serial_trimmer = mt.serial_trimmer LIMIT 1) AS m2x,
               NULL AS m3x
        FROM measurements_serial_trimmer mt
        WHERE mt.result_status = 0 AND mt.run_type = 0 AND mt.measured_at >= @from AND mt.measured_at < @to
        GROUP BY mt.definition_id, mt.serial_trimmer
    ) f
    JOIN measurement_definitions d ON d.id = f.did
    JOIN cameras c ON c.id = d.camera_id
    WHERE f.serial IS NOT NULL AND f.serial <> ''{ctrlClause}
) g;";

        var list = new List<DefectPart>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        if (hasCtrl) cmd.Parameters.AddWithValue("@ctrl", controller);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DefectPart(
                r.GetInt32(0),
                r.IsDBNull(1) ? "(none)" : r.GetString(1),
                r.IsDBNull(2) ? "(none)" : r.GetString(2),
                r.IsDBNull(3) ? "NoRef" : r.GetString(3),
                r.IsDBNull(4) ? string.Empty : r.GetString(4),
                r.IsDBNull(5) ? string.Empty : r.GetString(5),
                Convert.ToInt32(r.GetValue(6)),
                r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    /// <summary>
    /// Origin breakdown (task A) for one Pareto feature — the set of measurement <paramref name="defIds"/>
    /// that make up the clicked bar. Joins the affected/inspected serials to <c>dmcserial</c> and groups
    /// by the origin module + nest that <paramref name="moduleRef"/> selects (<c>M1x</c>→<c>m1x_*</c>,
    /// <c>M2x</c>→<c>m2x_*</c>, <c>M3x</c>→<c>m3x_*</c>). Affected = <c>result_status = 0</c>, inspected =
    /// <c>0/1</c>, both from the same feature rows → the per-cell rate is a real defect rate. Parts with
    /// no <c>dmcserial</c> row (not yet exited) are excluded — the origin is only known after part exit.
    /// Returns an empty list for module_refs without origin columns (e.g. <c>NoRef</c>).
    /// </summary>
    public async Task<List<OriginCell>> GetOriginAsync(
        DateTime from, DateTime to, IReadOnlyCollection<int> defIds, string moduleRef, CancellationToken ct = default)
    {
        var (modCol, nestCol) = moduleRef?.Trim().ToUpperInvariant() switch
        {
            "M1X" => ("m1x_module", "m1x_nest"),
            "M2X" => ("m2x_module", "m2x_nest"),
            "M3X" => ("m3x_module", "m3x_nest"),
            _ => (string.Empty, string.Empty),
        };
        if (modCol.Length == 0 || defIds is null || defIds.Count == 0)
            return new List<OriginCell>();

        // defIds are integer primary keys read straight from the DB, so inlining them is injection-safe.
        var idList = string.Join(",", defIds);

        var sql = $@"
SELECT origin_module, nest,
       COUNT(DISTINCT serial)                                        AS inspected,
       COUNT(DISTINCT CASE WHEN rs = 0 THEN serial END)             AS affected
FROM (
    SELECT ms.serial_number AS serial, ms.result_status AS rs,
           CAST(ds.{modCol} AS CHAR) AS origin_module, CAST(ds.{nestCol} AS CHAR) AS nest
    FROM measurements_serial ms
    JOIN dmcserial ds ON ds.serial_number = ms.serial_number
    WHERE ms.run_type = 0 AND ms.result_status IN (0, 1)
      AND ms.measured_at >= @from AND ms.measured_at < @to
      AND ms.definition_id IN ({idList})
    UNION ALL
    SELECT mt.serial_trimmer, mt.result_status,
           CAST(ds.{modCol} AS CHAR), CAST(ds.{nestCol} AS CHAR)
    FROM measurements_serial_trimmer mt
    JOIN dmcserial ds ON ds.serial_trimmer = mt.serial_trimmer
    WHERE mt.run_type = 0 AND mt.result_status IN (0, 1)
      AND mt.measured_at >= @from AND mt.measured_at < @to
      AND mt.definition_id IN ({idList})
) x
GROUP BY origin_module, nest
ORDER BY origin_module, nest;";

        var list = new List<OriginCell>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new OriginCell(
                r.IsDBNull(0) ? "?" : r.GetString(0),
                r.IsDBNull(1) ? "?" : r.GetString(1),
                r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3))));
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
