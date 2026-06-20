using HarryShared.Config;
using MySqlConnector;

namespace HarryShared.Data;

/// <summary>
/// Read-only queries against camera_data shared by the companion tools. Every call
/// opens its own pooled connection (never shared across threads) and uses the
/// GetData account via <see cref="HarryConfig"/>. All I/O uses ConfigureAwait(false).
/// </summary>
public sealed class QueryService
{
    private readonly HarryConfig _config;

    public QueryService(HarryConfig config) => _config = config;

    /// <summary>M20/M21 cameras write the trimmer (virtual serial) table.</summary>
    public static bool IsTrimmerModule(string module) =>
        module.Equals("M20", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("M21", StringComparison.OrdinalIgnoreCase);

    /// <summary>Quick connectivity probe (returns false instead of throwing).</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===== Definitions =====================================================

    /// <summary>All currently-active measurement definitions (effective_end IS NULL) with camera info.</summary>
    public async Task<List<MeasurementDefinitionRow>> GetActiveDefinitionsAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT d.id, d.camera_id, c.camera_name, c.module, d.telegram_place,
       d.variable_name, d.display_name, d.var_type, d.parameter_set, d.feature_group
FROM measurement_definitions d
JOIN cameras c ON c.id = d.camera_id
WHERE d.effective_end IS NULL
ORDER BY c.camera_name, d.telegram_place;";

        var list = new List<MeasurementDefinitionRow>();
        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MeasurementDefinitionRow(
                r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9)));
        }
        return list;
    }

    // ===== Part lookup (HarryAnalysis / HarryLimitSample) ==================

    /// <summary>
    /// Find a part by scan value: tries DMC, then serial_number, then serial_trimmer.
    /// Returns the most recent match or null.
    /// </summary>
    public async Task<PartInfo?> FindPartAsync(string scan, CancellationToken ct = default)
    {
        scan = scan.Trim();
        if (scan.Length == 0)
            return null;

        const string sql = @"
SELECT id, serial_number, serial_trimmer, dmc, m1x_module, m1x_nest, m2x_module, m2x_nest,
       m3x_module, m3x_nest, m50_nest, order_name, m1x_temperature, m1x_humidity,
       result_status, created_at
FROM dmcserial
WHERE dmc = @v OR serial_number = @v OR serial_trimmer = @v
ORDER BY created_at DESC
LIMIT 1;";

        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@v", scan);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new PartInfo(
            r.GetInt32(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetInt32(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetDouble(12),
            r.IsDBNull(13) ? null : r.GetDouble(13),
            r.GetInt32(14),
            r.GetDateTime(15));
    }

    /// <summary>
    /// All measurements for a part: the serial_number rows plus, if present, the
    /// trimmer rows, each joined to its definition and the latest Min/Max limits.
    /// </summary>
    public async Task<List<PartMeasurementRow>> GetPartMeasurementsAsync(PartInfo part, CancellationToken ct = default)
    {
        var rows = new List<PartMeasurementRow>();
        var limits = await GetLatestLimitsAsync(ct).ConfigureAwait(false);

        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);

        await ReadMeasurementsAsync(conn, "measurements_serial", "serial_number", part.SerialNumber,
            limits, rows, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(part.SerialTrimmer))
            await ReadMeasurementsAsync(conn, "measurements_serial_trimmer", "serial_trimmer", part.SerialTrimmer!,
                limits, rows, ct).ConfigureAwait(false);

        return rows.OrderBy(x => x.CameraName).ThenBy(x => x.DisplayName).ToList();
    }

    private static async Task ReadMeasurementsAsync(
        MySqlConnection conn, string table, string serialColumn, string serial,
        Dictionary<(int CameraId, int ParameterSet), (double? Min, double? Max)> limits,
        List<PartMeasurementRow> rows, CancellationToken ct)
    {
        var sql = $@"
SELECT d.display_name, c.camera_name, c.module, d.feature_group, d.parameter_set, d.camera_id,
       m.measurement_value, m.measurement_string, m.result_status, m.measured_at
FROM {table} m
JOIN measurement_definitions d ON d.id = m.definition_id
JOIN cameras c ON c.id = d.camera_id
WHERE m.{serialColumn} = @serial
ORDER BY c.camera_name, d.telegram_place;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serial", serial);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var cameraId = r.GetInt32(5);
            var parameterSet = r.GetInt32(4);
            limits.TryGetValue((cameraId, parameterSet), out var lim);

            rows.Add(new PartMeasurementRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), parameterSet,
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                lim.Min, lim.Max,
                r.GetDateTime(9)));
        }
    }

    /// <summary>Latest Min/Max limit per (camera_id, parameter_set) from the settings history.</summary>
    public async Task<Dictionary<(int CameraId, int ParameterSet), (double? Min, double? Max)>>
        GetLatestLimitsAsync(CancellationToken ct = default)
    {
        // Pull the whole (small) settings history ordered by time and fold the latest
        // value per (camera, parameter_set, limit_type) in code — robust and simple.
        const string sql = @"
SELECT s.camera_id, sd.parameter_set, sd.limit_type, s.limit_value, s.recorded_at
FROM settings s
JOIN setting_definitions sd ON sd.id = s.definition_id
ORDER BY s.recorded_at;";

        var min = new Dictionary<(int, int), double>();
        var max = new Dictionary<(int, int), double>();

        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = (r.GetInt32(0), r.GetInt32(1));
            var limitType = r.GetString(2);
            var value = r.GetDouble(3);
            if (limitType.Equals("Min", StringComparison.OrdinalIgnoreCase))
                min[key] = value;        // later rows overwrite earlier → latest wins
            else if (limitType.Equals("Max", StringComparison.OrdinalIgnoreCase))
                max[key] = value;
        }

        var result = new Dictionary<(int, int), (double?, double?)>();
        foreach (var key in min.Keys.Union(max.Keys))
            result[key] = (min.TryGetValue(key, out var lo) ? lo : null,
                           max.TryGetValue(key, out var hi) ? hi : null);
        return result;
    }

    // ===== Time series (HarryGraph) ========================================

    /// <summary>
    /// Time-series samples for one definition within [from, to], newest last. Picks the
    /// serial or trimmer table based on the definition's module. <paramref name="limit"/>
    /// caps the number of returned points (most recent within range).
    /// </summary>
    public async Task<List<SeriesPoint>> GetSeriesAsync(
        MeasurementDefinitionRow def, DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        var table = def.IsTrimmer ? "measurements_serial_trimmer" : "measurements_serial";
        var serialColumn = def.IsTrimmer ? "serial_trimmer" : "serial_number";

        var sql = $@"
SELECT measured_at, measurement_value, result_status, {serialColumn}
FROM {table}
WHERE definition_id = @def AND measurement_value IS NOT NULL
  AND measured_at >= @from AND measured_at <= @to
ORDER BY measured_at DESC
LIMIT @limit;";

        var list = new List<SeriesPoint>();
        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@def", def.Id);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new SeriesPoint(
                r.GetDateTime(0),
                r.GetDouble(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? string.Empty : r.GetString(3)));
        }
        list.Reverse(); // chronological for plotting
        return list;
    }

    // ===== NG counting (HarryCounter) ======================================

    /// <summary>Number of NG finished parts (result_status = 0) in the range.</summary>
    public async Task<int> GetNgPartCountAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT COUNT(*) FROM dmcserial
WHERE result_status = 0 AND created_at >= @from AND created_at <= @to;";
        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        var n = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(n);
    }

    /// <summary>Total finished parts (any result) in the range — for yield context.</summary>
    public async Task<int> GetTotalPartCountAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT COUNT(*) FROM dmcserial
WHERE created_at >= @from AND created_at <= @to;";
        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        var n = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(n);
    }

    /// <summary>Failing measurements (result_status = 0) grouped by feature_group in the range.</summary>
    public async Task<List<CountRow>> GetNgByFeatureGroupAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"
SELECT d.feature_group, COUNT(*) AS cnt
FROM measurements_serial m
JOIN measurement_definitions d ON d.id = m.definition_id
WHERE m.result_status = 0 AND m.run_type = 0
  AND m.measured_at >= @from AND m.measured_at <= @to
GROUP BY d.feature_group
ORDER BY cnt DESC;";
        return await CountQueryAsync(sql, from, to, ct).ConfigureAwait(false);
    }

    /// <summary>Failing measurements grouped by the measurement display name in the range.</summary>
    public async Task<List<CountRow>> GetNgByMeasurementAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"
SELECT CONCAT(c.camera_name, ' · ', d.display_name) AS grp, COUNT(*) AS cnt
FROM measurements_serial m
JOIN measurement_definitions d ON d.id = m.definition_id
JOIN cameras c ON c.id = d.camera_id
WHERE m.result_status = 0 AND m.run_type = 0
  AND m.measured_at >= @from AND m.measured_at <= @to
GROUP BY grp
ORDER BY cnt DESC;";
        return await CountQueryAsync(sql, from, to, ct).ConfigureAwait(false);
    }

    /// <summary>NG finished parts grouped by an M50 nest (or m1x/m3x) in the range.</summary>
    public async Task<List<CountRow>> GetNgByNestAsync(string nestColumn, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // nestColumn is validated against an allow-list by the caller (never user text).
        var sql = $@"
SELECT COALESCE(CAST({nestColumn} AS CHAR), '(none)') AS grp, COUNT(*) AS cnt
FROM dmcserial
WHERE result_status = 0 AND created_at >= @from AND created_at <= @to
GROUP BY grp
ORDER BY cnt DESC;";
        return await CountQueryAsync(sql, from, to, ct).ConfigureAwait(false);
    }

    /// <summary>Allowed nest columns for <see cref="GetNgByNestAsync"/> (guards against injection).</summary>
    public static readonly IReadOnlyList<string> NestColumns = new[]
    {
        "m50_nest", "m1x_nest", "m3x_nest", "m1x_module", "m3x_module", "order_name",
    };

    private async Task<List<CountRow>> CountQueryAsync(string sql, DateTime from, DateTime to, CancellationToken ct)
    {
        var list = new List<CountRow>();
        await using var conn = await _config.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new CountRow(r.IsDBNull(0) ? "(none)" : r.GetString(0), r.GetInt32(1)));
        return list;
    }
}
