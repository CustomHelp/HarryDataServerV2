using System.Collections.Concurrent;
using System.Globalization;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Writes the main production CSV (CLAUDE.md section 13): one row per finished part
/// with every measurement value from every camera. The column layout is built
/// dynamically from <c>measurement_definitions</c>. Triggered by Part Exit, runs on
/// a dedicated background task (queue + per-flush connection), and rotates the file
/// on order-name change or when <c>DataSetsPerFile</c> rows are reached.
/// </summary>
public sealed class CsvExportService : ICsvService
{
    private const int MaxQueue = 100_000;
    private const int MaxItemsPerFlush = 2_000;

    // Fixed meta columns written before the dynamic measurement columns.
    private static readonly string[] MetaHeaders =
    {
        "Timestamp", "DMC", "SerialNumber", "VirtualSerial", "OrderName", "Mode",
        "Result", "M1xModule", "M1xNest", "M3xModule", "M3xNest", "M50Nest", "Humidity",
    };

    private readonly ISpsServer _sps;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;
    private readonly bool _enabled;
    private readonly string _basePath;
    private readonly int _maxRows;
    private readonly TimeSpan _flushInterval;

    private readonly ConcurrentQueue<SpsPartExitData> _queue = new();

    // Dynamic column layout (built once when the DB is ready).
    private readonly List<string> _measurementHeaders = new();
    private readonly Dictionary<int, int> _columnByDefinitionId = new();

    private CsvFileWriter? _csv;
    private string? _currentOrder;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalRows;
    private bool _started;

    public CsvExportService(ISpsServer sps, IDatabaseService database, IConfigService config, ILogService log)
    {
        _sps = sps;
        _database = database;
        _log = log;

        var csv = config.Config.Csv;
        _enabled = csv.Save && !string.IsNullOrWhiteSpace(csv.BasePath);
        _basePath = csv.BasePath;
        _maxRows = csv.DataSetsPerFile;
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));
    }

    public int PendingCount => _queue.Count;
    public long TotalRows => Interlocked.Read(ref _totalRows);
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        if (!_enabled)
        {
            _log.Information("Main CSV export disabled or no base path; service idle.");
            return Task.CompletedTask;
        }

        _sps.PartExitReceived += OnPartExitReceived;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("CSV export service started; writing to {Path}.", _basePath);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_enabled)
            return;

        _sps.PartExitReceived -= OnPartExitReceived;
        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _csv?.Dispose();
    }

    // --- Receive side (SPS thread; in-memory only) ---

    private void OnPartExitReceived(object? sender, SpsPartExitEventArgs e)
    {
        if (_queue.Count >= MaxQueue)
        {
            _log.Warning("CSV queue full ({Max}); dropping part {Serial}.", MaxQueue, e.Data.Szid);
            return;
        }
        _queue.Enqueue(e.Data);
    }

    // --- Flush side (dedicated background task; all DB/file I/O) ---

    private async Task RunAsync(CancellationToken ct)
    {
        if (!await PrepareLayoutAsync(ct).ConfigureAwait(false))
            return;

        _csv = new CsvFileWriter(_basePath, _maxRows, dateSubfolders: true, _log);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "CSV flush failed."); }
        }

        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(ex, "Final CSV flush failed."); }
    }

    /// <summary>Wait for the DB, then build the dynamic measurement-column layout.</summary>
    private async Task<bool> PrepareLayoutAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _database.Status != DatabaseStatus.Ready)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        if (ct.IsCancellationRequested)
            return false;

        const string sql = @"
SELECT md.id, c.camera_name, md.variable_name
FROM measurement_definitions md
JOIN cameras c ON c.id = md.camera_id
WHERE md.effective_end IS NULL
ORDER BY c.camera_name, md.telegram_place;";

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var definitionId = reader.GetInt32(0);
                var camera = reader.GetString(1);
                var variable = reader.GetString(2);
                _columnByDefinitionId[definitionId] = _measurementHeaders.Count;
                _measurementHeaders.Add($"{camera}.{variable}");
            }

            _log.Information("CSV layout built: {Meta} meta + {Cols} measurement columns.",
                MetaHeaders.Length, _measurementHeaders.Count);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to build CSV column layout; CSV service stopping.");
            return false;
        }
    }

    private IReadOnlyList<string> FullHeader()
    {
        var header = new List<string>(MetaHeaders.Length + _measurementHeaders.Count);
        header.AddRange(MetaHeaders);
        header.AddRange(_measurementHeaders);
        return header;
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_csv is null || _queue.IsEmpty)
            return;
        if (_database.Status != DatabaseStatus.Ready)
            return;

        var parts = new List<SpsPartExitData>();
        while (parts.Count < MaxItemsPerFlush && _queue.TryDequeue(out var item))
            parts.Add(item);

        if (parts.Count == 0)
            return;

        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

        var written = 0;
        foreach (var part in parts)
        {
            var row = await BuildRowAsync(conn, part, ct).ConfigureAwait(false);

            // Rotate to a new file when the order changes (CLAUDE.md section 13).
            if (!string.Equals(part.OrderName, _currentOrder, StringComparison.Ordinal))
            {
                _csv.Rotate();
                _csv.Configure(FullHeader(), string.IsNullOrWhiteSpace(part.OrderName) ? "NoOrder" : part.OrderName);
                _currentOrder = part.OrderName;
            }

            _csv.WriteRow(row);
            written++;
        }

        if (written > 0)
        {
            _csv.Flush();
            Interlocked.Add(ref _totalRows, written);
            _log.Debug("Wrote {Count} CSV row(s); {Pending} pending.", written, _queue.Count);
            StatsChanged?.Invoke();
        }
    }

    /// <summary>Build one CSV row: meta columns + one cell per measurement definition.</summary>
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

        // Production measurements keyed by SZID; trimmer (M20/M21) measurements by virtual serial.
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
                continue; // definition not in the current layout (retired)

            // Priority: if a string value exists, use only it; otherwise the numeric
            // measurement_value, otherwise the result_status (R_ and V_ are separate
            // definitions/columns, so result and value are both exported per feature).
            var str = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? cell;
            if (!string.IsNullOrEmpty(str))
                cell = str;
            else if (!reader.IsDBNull(1))
                cell = reader.GetDouble(1).ToString(CultureInfo.InvariantCulture);
            else if (!reader.IsDBNull(3))
                cell = reader.GetInt32(3).ToString(CultureInfo.InvariantCulture);
            else
                cell = null;

            row[MetaHeaders.Length + column] = cell;
        }
    }
}
