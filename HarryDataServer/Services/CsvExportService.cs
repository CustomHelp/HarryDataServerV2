using System.Globalization;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Writes the main production CSV (CLAUDE.md section 13): one row per finished part
/// with every measurement value from every camera. The column layout is built
/// dynamically from <c>measurement_definitions</c>. Now driven synchronously by the
/// part-exit orchestrator via <see cref="WritePartAsync"/> (one part at a time);
/// rotates the file on order-name change or when <c>DataSetsPerFile</c> rows are reached.
/// </summary>
public sealed class CsvExportService : ICsvService
{
    // Fixed meta columns written before the dynamic measurement columns.
    // "M50St110Kf" was added after "M50Nest" on 2026-07-28: the two ST110 control windows now share
    // their measurement columns, so this records WHICH window (1/3) supplied them — the same kind of
    // traceability M1xModule/M2xModule give for the two strands. Empty when the part has no ST110 row.
    private static readonly string[] MetaHeaders =
    {
        "Timestamp", "DMC", "SerialNumber", "VirtualSerial", "OrderName", "Mode",
        "Result", "M1xModule", "M1xNest", "M2xModule", "M2xNest", "M3xModule", "M3xNest",
        "M50Nest", "M50St110Kf", "Temperature", "Humidity",
    };

    /// <summary>Index of the <c>M50St110Kf</c> meta column, filled while the measurements are read.</summary>
    private static readonly int M50St110KfColumn = Array.IndexOf(MetaHeaders, "M50St110Kf");

    private readonly IDatabaseService _database;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;
    private readonly bool _enabled;
    private readonly string _basePath;
    private readonly int _maxRows;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Dynamic column layout (built once when the DB is ready). Two header rows: merge group /
    // controller name (row 1) above the parameter/variable name (row 2). See CsvColumnLayout for the
    // merging of the parallel strands (M1x/M2x) and the two M50 ST110 control windows.
    private CsvColumnLayout? _layout;

    private CsvFileWriter? _csv;
    private string? _currentOrder;
    private bool _layoutBuilt;
    private long _totalRows;
    private bool _started;

    public CsvExportService(IDatabaseService database, ISystemHealth health, IConfigService config, ILogService log)
    {
        _database = database;
        _health = health;
        _log = log;

        var csv = config.Config.Csv;
        _enabled = csv.Save && !string.IsNullOrWhiteSpace(csv.BasePath);
        _basePath = csv.BasePath;
        _maxRows = csv.DataSetsPerFile;
    }

    public int PendingCount => 0; // synchronous now — no queue
    public long TotalRows => Interlocked.Read(ref _totalRows);
    public string? ActiveFilePath { get; private set; }
    public DateTime? LastWriteTime { get; private set; }
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;
        _log.Information(_enabled ? "CSV export service ready; writing to {Path}." : "Main CSV export disabled; service idle.",
            _basePath);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _csv?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write one part's row (called by the orchestrator). Returns false on failure so
    /// the part-exit ACK can report it. Serialized — one writer at a time.
    /// </summary>
    public async Task<bool> WritePartAsync(SpsPartExitData part, CancellationToken ct = default)
    {
        if (!_enabled)
            return true; // disabled = nothing to do, not a failure

        if (_database.Status != DatabaseStatus.Ready)
        {
            _health.Report(HealthSources.Csv, HealthSeverity.Error, "CSV: database not ready");
            return false;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureLayoutAsync(ct).ConfigureAwait(false))
                return false;

            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var row = await BuildRowAsync(conn, part, ct).ConfigureAwait(false);

            // Rotate on order-name change (CLAUDE.md section 13). Row-limit rotation
            // is handled inside CsvFileWriter (MaxRowsPerFile = DataSetsPerFile).
            if (!string.Equals(part.OrderName, _currentOrder, StringComparison.Ordinal))
            {
                _csv!.Rotate();
                _csv.Configure(FullHeaderRows(), string.IsNullOrWhiteSpace(part.OrderName) ? "NoOrder" : part.OrderName);
                _currentOrder = part.OrderName;
            }

            _csv!.WriteRow(row);
            _csv.Flush();

            ActiveFilePath = _csv.CurrentPath;
            LastWriteTime = DateTime.Now;
            Interlocked.Increment(ref _totalRows);
            _health.Clear(HealthSources.Csv);
            StatsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _health.Report(HealthSources.Csv, HealthSeverity.Error, $"CSV export failing: {ex.Message}");
            _log.Error(ex, "CSV export failed for part {Serial}.", part.Szid);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Build the dynamic measurement-column layout once (DB must be ready).</summary>
    private async Task<bool> EnsureLayoutAsync(CancellationToken ct)
    {
        if (_layoutBuilt)
            return true;

        const string sql = @"
SELECT md.id, c.camera_name, md.variable_name
FROM measurement_definitions md
JOIN cameras c ON c.id = md.camera_id
WHERE md.effective_end IS NULL
ORDER BY c.camera_name, md.telegram_place;";

        try
        {
            var sources = new List<CsvColumnSource>();

            await using (var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var cmd = new MySqlCommand(sql, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    sources.Add(new CsvColumnSource(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }

            var layout = CsvColumnLayout.Build(sources);

            // A variable without a partner on the other strand/window is never folded away silently.
            foreach (var warning in layout.Warnings)
                _log.Warning("CSV layout: {Warning}.", warning);

            _layout = layout;
            _csv = new CsvFileWriter(_basePath, _maxRows, dateSubfolders: true, _log);
            _layoutBuilt = true;
            _log.Information(
                "CSV layout built: {Meta} meta + {Cols} measurement columns ({Pairs} R_/V_ pairs); " +
                "{Sources} definitions folded into {Merged} shared column(s) (M1x/M2x strands + M50_ST110 windows).",
                MetaHeaders.Length, layout.ColumnCount, layout.ValueColumnByResultDefinitionId.Count,
                layout.SourceCount, layout.MergedColumnCount);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to build CSV column layout.");
            return false;
        }
    }

    private IReadOnlyList<IReadOnlyList<string>> FullHeaderRows()
    {
        var layout = _layout!;

        var row1 = new List<string>(MetaHeaders.Length + layout.ColumnCount);
        row1.AddRange(Enumerable.Repeat(string.Empty, MetaHeaders.Length));
        row1.AddRange(layout.ControllerHeaders);

        var row2 = new List<string>(MetaHeaders.Length + layout.ColumnCount);
        row2.AddRange(MetaHeaders);
        row2.AddRange(layout.VariableHeaders);

        return new IReadOnlyList<string>[] { row1, row2 };
    }

    private async Task<string?[]> BuildRowAsync(MySqlConnection conn, SpsPartExitData part, CancellationToken ct)
    {
        var row = new string?[MetaHeaders.Length + _layout!.ColumnCount];

        row[0] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        row[1] = part.Dmc;
        row[2] = part.Szid;
        row[3] = part.VirtualSerial;
        row[4] = part.OrderName;
        row[5] = part.Mode;
        row[6] = part.Result.ToString();
        row[7] = part.M1xModule?.ToString(CultureInfo.InvariantCulture);
        row[8] = part.M1xNest?.ToString(CultureInfo.InvariantCulture);
        row[9] = part.M2xModule?.ToString(CultureInfo.InvariantCulture);
        row[10] = part.M2xNest?.ToString(CultureInfo.InvariantCulture);
        row[11] = part.M3xModule;
        row[12] = part.M3xNest;
        row[13] = part.M50Nest;
        // row[14] = M50St110Kf — filled from the measurements below (which control window supplied them).
        row[15] = part.Temperature?.ToString(CultureInfo.InvariantCulture);
        row[16] = part.Humidity?.ToString(CultureInfo.InvariantCulture);

        // Tracks which controller wrote a shared column, so a (theoretical) collision between the two
        // strands / control windows is resolved by the documented priority instead of "last one wins".
        var fill = new CsvMergeFill(part, _log);

        await FillMeasurementsAsync(conn, "measurements_serial", "serial_number", part.Szid, row, fill, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(part.VirtualSerial))
            await FillMeasurementsAsync(conn, "measurements_serial_trimmer", "serial_trimmer", part.VirtualSerial, row, fill, ct)
                .ConfigureAwait(false);

        row[M50St110KfColumn] = fill.M50St110Kf;
        fill.ReportConflicts(part);
        return row;
    }

    private async Task FillMeasurementsAsync(
        MySqlConnection conn, string table, string serialColumn, string serial, string?[] row,
        CsvMergeFill fill, CancellationToken ct)
    {
        // Exact match first (the normalised serial should match what the camera pipeline stored).
        var filled = await FillFromQueryAsync(conn, table, serialColumn, serial, exact: true, row, fill, ct).ConfigureAwait(false);
        if (filled > 0)
            return;

        // No measurement rows for this part. Warn (with the searched serial + table) so a future
        // serial mismatch is spotted immediately, then try a prefix fallback as a safety net — the
        // clean fix is the shared serial normalisation (SerialNumberHelper), this is belt-and-suspenders.
        _log.Warning("Part-exit CSV: no rows in {Table} for {Column}='{Serial}' (len {Len}); trying prefix fallback.",
            table, serialColumn, serial, serial.Length);

        if (string.IsNullOrEmpty(serial))
            return;

        var viaPrefix = await FillFromQueryAsync(conn, table, serialColumn, serial, exact: false, row, fill, ct).ConfigureAwait(false);
        if (viaPrefix > 0)
            _log.Warning("Part-exit CSV: matched {Count} row(s) in {Table} for {Column} via prefix '{Serial}%' — verify serial normalisation.",
                viaPrefix, table, serialColumn, serial);
    }

    /// <summary>
    /// Fill measurement cells from <paramref name="table"/> for one serial. When <paramref name="exact"/>
    /// is false a prefix match (<c>LIKE serial%</c>) is used. Returns the number of rows read.
    /// </summary>
    private async Task<int> FillFromQueryAsync(
        MySqlConnection conn, string table, string serialColumn, string serial, bool exact, string?[] row,
        CsvMergeFill fill, CancellationToken ct)
    {
        var layout = _layout!;
        var predicate = exact ? "= @serial" : "LIKE @serial";
        var sql =
            $"SELECT definition_id, measurement_value, measurement_string, result_status " +
            $"FROM `{table}` WHERE `{serialColumn}` {predicate} ORDER BY id;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serial", exact ? serial : serial + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count++;
            var definitionId = reader.GetInt32(0);
            if (!layout.ColumnByDefinitionId.TryGetValue(definitionId, out var column))
                continue;

            var camera = layout.CameraByDefinitionId.GetValueOrDefault(definitionId, string.Empty);
            fill.NoteController(camera);

            var str = reader.IsDBNull(2) ? null : reader.GetString(2);
            var value = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
            var result = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);

            if (layout.ValueColumnByResultDefinitionId.TryGetValue(definitionId, out var valueColumn))
            {
                if (result.HasValue)
                    fill.Write(row, MetaHeaders.Length, column, camera, result.Value.ToString(CultureInfo.InvariantCulture));

                var valueCell = !string.IsNullOrEmpty(str)
                    ? str
                    : value?.ToString(CultureInfo.InvariantCulture);
                if (valueCell is not null)
                    fill.Write(row, MetaHeaders.Length, valueColumn, camera, valueCell);
            }
            else
            {
                var cell = !string.IsNullOrEmpty(str)
                    ? str
                    : value?.ToString(CultureInfo.InvariantCulture)
                      ?? result?.ToString(CultureInfo.InvariantCulture);
                if (cell is not null)
                    fill.Write(row, MetaHeaders.Length, column, camera, cell);
            }
        }

        return count;
    }

}
