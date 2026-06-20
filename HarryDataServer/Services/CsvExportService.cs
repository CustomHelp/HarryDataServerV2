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
    private static readonly string[] MetaHeaders =
    {
        "Timestamp", "DMC", "SerialNumber", "VirtualSerial", "OrderName", "Mode",
        "Result", "M1xModule", "M1xNest", "M3xModule", "M3xNest", "M50Nest", "Humidity",
    };

    private readonly IDatabaseService _database;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;
    private readonly bool _enabled;
    private readonly string _basePath;
    private readonly int _maxRows;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Dynamic column layout (built once when the DB is ready). Two header rows:
    // controller name (row 1) above the parameter/variable name (row 2).
    private readonly List<string> _measurementControllers = new();
    private readonly List<string> _measurementHeaders = new();
    private readonly Dictionary<int, int> _columnByDefinitionId = new();
    private readonly Dictionary<int, int> _valueColumnByResultDefinitionId = new();

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
            var columns = new List<(int Id, string Camera, string Variable, int Index)>();

            await using (var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var cmd = new MySqlCommand(sql, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var definitionId = reader.GetInt32(0);
                    var camera = reader.GetString(1);
                    var variable = reader.GetString(2);
                    var index = _measurementHeaders.Count;

                    _columnByDefinitionId[definitionId] = index;
                    _measurementControllers.Add(camera);
                    _measurementHeaders.Add(variable);
                    columns.Add((definitionId, camera, variable, index));
                }
            }

            BuildResultValuePairs(columns);
            _csv = new CsvFileWriter(_basePath, _maxRows, dateSubfolders: true, _log);
            _layoutBuilt = true;
            _log.Information("CSV layout built: {Meta} meta + {Cols} measurement columns ({Pairs} R_/V_ pairs).",
                MetaHeaders.Length, _measurementHeaders.Count, _valueColumnByResultDefinitionId.Count);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to build CSV column layout.");
            return false;
        }
    }

    private void BuildResultValuePairs(List<(int Id, string Camera, string Variable, int Index)> columns)
    {
        var groups = columns.GroupBy(c => (c.Camera, MeasurementRowBuilder.StripTypePrefix(c.Variable)));
        foreach (var group in groups)
        {
            int? resultId = null;
            int? valueColumn = null;
            foreach (var c in group)
            {
                if (c.Variable.StartsWith("R_", StringComparison.Ordinal))
                    resultId = c.Id;
                else if (c.Variable.StartsWith("V_", StringComparison.Ordinal))
                    valueColumn = c.Index;
            }

            if (resultId.HasValue && valueColumn.HasValue)
                _valueColumnByResultDefinitionId[resultId.Value] = valueColumn.Value;
        }
    }

    private IReadOnlyList<IReadOnlyList<string>> FullHeaderRows()
    {
        var row1 = new List<string>(MetaHeaders.Length + _measurementControllers.Count);
        row1.AddRange(Enumerable.Repeat(string.Empty, MetaHeaders.Length));
        row1.AddRange(_measurementControllers);

        var row2 = new List<string>(MetaHeaders.Length + _measurementHeaders.Count);
        row2.AddRange(MetaHeaders);
        row2.AddRange(_measurementHeaders);

        return new IReadOnlyList<string>[] { row1, row2 };
    }

    private async Task<string?[]> BuildRowAsync(MySqlConnection conn, SpsPartExitData part, CancellationToken ct)
    {
        var row = new string?[MetaHeaders.Length + _measurementHeaders.Count];

        row[0] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        row[1] = part.Dmc;
        row[2] = part.Szid;
        row[3] = part.VirtualSerial;
        row[4] = part.OrderName;
        row[5] = part.Mode;
        row[6] = part.Result.ToString();
        row[7] = part.M1xModule?.ToString(CultureInfo.InvariantCulture);
        row[8] = part.M1xNest?.ToString(CultureInfo.InvariantCulture);
        row[9] = part.M3xModule;
        row[10] = part.M3xNest;
        row[11] = part.M50Nest;
        row[12] = part.Humidity?.ToString(CultureInfo.InvariantCulture);

        await FillMeasurementsAsync(conn, "measurements_serial", "serial_number", part.Szid, row, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(part.VirtualSerial))
            await FillMeasurementsAsync(conn, "measurements_serial_trimmer", "serial_trimmer", part.VirtualSerial, row, ct).ConfigureAwait(false);

        return row;
    }

    private async Task FillMeasurementsAsync(
        MySqlConnection conn, string table, string serialColumn, string serial, string?[] row, CancellationToken ct)
    {
        var sql =
            $"SELECT definition_id, measurement_value, measurement_string, result_status " +
            $"FROM `{table}` WHERE `{serialColumn}` = @serial ORDER BY id;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serial", serial);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var definitionId = reader.GetInt32(0);
            if (!_columnByDefinitionId.TryGetValue(definitionId, out var column))
                continue;

            var str = reader.IsDBNull(2) ? null : reader.GetString(2);
            var value = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
            var result = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);

            if (_valueColumnByResultDefinitionId.TryGetValue(definitionId, out var valueColumn))
            {
                if (result.HasValue)
                    row[MetaHeaders.Length + column] = result.Value.ToString(CultureInfo.InvariantCulture);

                var valueCell = !string.IsNullOrEmpty(str)
                    ? str
                    : value?.ToString(CultureInfo.InvariantCulture);
                if (valueCell is not null)
                    row[MetaHeaders.Length + valueColumn] = valueCell;
            }
            else
            {
                row[MetaHeaders.Length + column] = !string.IsNullOrEmpty(str)
                    ? str
                    : value?.ToString(CultureInfo.InvariantCulture)
                      ?? result?.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
