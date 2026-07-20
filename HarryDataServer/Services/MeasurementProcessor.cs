using System.Collections.Concurrent;
using System.Text;
using HarryDataServer.Communication;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Background measurement pipeline. Subscribes to every camera's
/// <see cref="TcpCameraClient.ResultsReceived"/> event, enqueues the samples
/// (no I/O on the receive thread) and batches them into <c>measurements_serial</c>
/// or <c>measurements_serial_trimmer</c> on a single dedicated connection.
/// Only Normal-mode telegrams are persisted here; MSA runs go to the MSA pipeline.
/// </summary>
public sealed class MeasurementProcessor : IMeasurementProcessor
{
    private const int MaxQueue = 500_000;       // back-pressure guard if the DB is down
    private const int MaxItemsPerFlush = 10_000; // bound the work of a single flush cycle

    private readonly ICameraService _cameras;
    private readonly IDatabaseService _database;
    private readonly MeasurementDefinitionCache _cache;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;

    private readonly ConcurrentQueue<PendingMeasurement> _queue = new();
    private readonly Dictionary<string, bool> _isTrimmerByCamera;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    private readonly HashSet<string> _warnedUnknown = new();

    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalInserted;
    private bool _started;

    public MeasurementProcessor(
        ICameraService cameras,
        IDatabaseService database,
        MeasurementDefinitionCache cache,
        IConfigService config,
        ISystemHealth health,
        ILogService log)
    {
        _cameras = cameras;
        _database = database;
        _cache = cache;
        _health = health;
        _log = log;

        _batchSize = Math.Max(1, config.Config.SqlSettings.BatchSize);
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));

        // Routing (CLAUDE.md §4/§8): in Normal mode M2X (M20/M21) cameras carry the Virtual
        // Serial in Serial1 and their measurements go to measurements_serial_trimmer; M1X/M5X
        // carry the SZID and go to measurements_serial. The module is taken from the camera's
        // INI config (authoritative) rather than re-parsing the controller name per telegram.
        _isTrimmerByCamera = config.Config.Cameras.ToDictionary(
            c => c.CameraName,
            c => c.Module is "M20" or "M21",
            StringComparer.OrdinalIgnoreCase);
    }

    public int PendingCount => _queue.Count;
    public long TotalInserted => Interlocked.Read(ref _totalInserted);

    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        foreach (var client in _cameras.Clients)
            client.ResultsReceived += OnResultsReceived;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Measurement processor started (batch={Batch}, interval={Interval}s).",
            _batchSize, _flushInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        foreach (var client in _cameras.Clients)
            client.ResultsReceived -= OnResultsReceived;

        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    // --- Receive side (runs on the camera thread; in-memory only) ---

    private void OnResultsReceived(object? sender, ResultsTelegramEventArgs e)
    {
        var telegram = e.Telegram;

        // MSA / LimitSample runs are persisted by the MSA pipeline (Phase 10), not here.
        if (telegram.Mode != CameraOperatingMode.Normal)
            return;

        // Serial1 is already normalised by the parser; run it through the single helper again so
        // every write path canonicalises identically (drops controller padding, caps to 22) and the
        // stored serial matches the SPS part-exit serial for the measurement lookup (Problem 1).
        var serial = SerialNumberHelper.Normalize(telegram.Serial1);
        if (string.IsNullOrWhiteSpace(serial))
        {
            _log.Debug("{Camera}: results telegram without serial; skipped.", telegram.ControllerName);
            return;
        }

        if (_queue.Count >= MaxQueue)
        {
            _health.Report(HealthSources.MeasurementQueue, HealthSeverity.Error,
                $"Measurement queue full ({MaxQueue}); samples are being dropped");
            _log.Warning("Measurement queue full ({Max}); dropping samples from {Camera}.",
                MaxQueue, telegram.ControllerName);
            return;
        }

        var isTrimmer = _isTrimmerByCamera.GetValueOrDefault(telegram.ControllerName);
        var measuredAt = DateTime.Now;

        // Combine each R_/V_ pair into ONE row (keyed by the R_ definition): result
        // and value live in the same row (CLAUDE.md section 4).
        var rows = MeasurementRowBuilder.Build(
            telegram.ControllerName, serial, isTrimmer, runType: 0, measuredAt, e.Measurements);

        foreach (var item in rows)
            _queue.Enqueue(item);
    }

    // --- Flush side (dedicated background task; performs all DB I/O) ---

    private async Task RunAsync(CancellationToken ct)
    {
        if (!await PrepareCacheAsync(ct).ConfigureAwait(false))
            return;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "Measurement flush failed."); }
        }

        // Final drain on shutdown (best effort).
        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(ex, "Final measurement flush failed."); }
    }

    /// <summary>Wait until the database is ready, then load the definition cache.</summary>
    private async Task<bool> PrepareCacheAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _database.Status != DatabaseStatus.Ready)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        if (ct.IsCancellationRequested)
            return false;

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await _cache.LoadAsync(conn, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load measurement definition cache; processor stopping.");
            return false;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty)
        {
            // Nothing pending → no write problem; clear any stale flush fault.
            _health.Clear(HealthSources.Measurements);
            UpdateQueueHealth();
            return;
        }

        // Drain a bounded slice, split by destination table.
        var serialRows = new List<PendingMeasurement>();
        var trimmerRows = new List<PendingMeasurement>();
        var drained = 0;

        while (drained < MaxItemsPerFlush && _queue.TryDequeue(out var item))
        {
            (item.IsTrimmer ? trimmerRows : serialRows).Add(item);
            drained++;
        }

        if (drained == 0)
        {
            UpdateQueueHealth();
            return;
        }

        // Write each table through the isolation/requeue helper: a single poison row
        // no longer kills the batch, and a DB outage requeues instead of losing data.
        var inserted = 0;
        inserted += await FlushTableAsync("measurements_serial", "serial_number", serialRows, ct).ConfigureAwait(false);
        inserted += await FlushTableAsync("measurements_serial_trimmer", "serial_trimmer", trimmerRows, ct).ConfigureAwait(false);

        if (inserted > 0)
        {
            Interlocked.Add(ref _totalInserted, inserted);
            _log.Debug("Flushed {Inserted} measurement(s); {Pending} pending.", inserted, _queue.Count);
            StatsChanged?.Invoke();
        }

        UpdateQueueHealth();
    }

    private Task<int> FlushTableAsync(
        string table, string serialColumn, IReadOnlyList<PendingMeasurement> rows, CancellationToken ct) =>
        FlushHelper.WriteAsync(
            rows,
            (batch, c) => InsertBatchAsync(table, serialColumn, batch, c),
            (row, c) => InsertSingleAsync(table, serialColumn, row, c),
            Requeue,
            _health, _log, HealthSources.Measurements,
            r => $"{r.CameraName}/{r.VariableName} serial={r.Serial}",
            ct);

    /// <summary>
    /// Insert all rows for one table atomically (single transaction), chunked by batch
    /// size. All-or-nothing so the helper's row-by-row retry path starts from a clean
    /// slate — a mid-batch failure rolls back and cannot double-insert earlier chunks.
    /// </summary>
    private async Task<int> InsertBatchAsync(
        string table, string serialColumn, IReadOnlyList<PendingMeasurement> rows, CancellationToken ct)
    {
        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var inserted = 0;
        for (var offset = 0; offset < rows.Count; offset += _batchSize)
        {
            var count = Math.Min(_batchSize, rows.Count - offset);
            var chunk = new List<PendingMeasurement>(count);
            for (var i = 0; i < count; i++)
                chunk.Add(rows[offset + i]);
            inserted += await InsertChunkAsync(conn, tx, table, serialColumn, chunk, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    /// <summary>Insert exactly one row on its own connection (isolation path).</summary>
    private async Task InsertSingleAsync(
        string table, string serialColumn, PendingMeasurement row, CancellationToken ct)
    {
        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await InsertChunkAsync(conn, null, table, serialColumn, new[] { row }, ct).ConfigureAwait(false);
    }

    private void Requeue(IReadOnlyList<PendingMeasurement> rows)
    {
        foreach (var row in rows)
            _queue.Enqueue(row);
    }

    /// <summary>Reflect queue depth in the SPS health signal (filling up → WARNING, near full → ERROR).</summary>
    private void UpdateQueueHealth()
    {
        var count = _queue.Count;
        if (count >= MaxQueue)
            _health.Report(HealthSources.MeasurementQueue, HealthSeverity.Error,
                $"Measurement queue full ({count}/{MaxQueue}); samples are being dropped");
        else if (count >= MaxQueue / 2)
            _health.Report(HealthSources.MeasurementQueue, HealthSeverity.Warning,
                $"Measurement queue filling up ({count}/{MaxQueue})");
        else
            _health.Clear(HealthSources.MeasurementQueue);
    }

    private async Task<int> InsertChunkAsync(
        MySqlConnection conn, MySqlTransaction? tx, string table, string serialColumn,
        IReadOnlyList<PendingMeasurement> chunk, CancellationToken ct)
    {
        var sql = new StringBuilder(
            $"INSERT INTO `{table}` (`{serialColumn}`, definition_id, measurement_value, measurement_string, result_status, run_type, measured_at) VALUES ");

        await using var cmd = new MySqlCommand { Connection = conn, Transaction = tx };

        var rowIndex = 0;
        for (var i = 0; i < chunk.Count; i++)
        {
            var row = chunk[i];
            if (!_cache.TryGet(row.CameraName, row.VariableName, out var definitionId))
            {
                WarnUnknown(row);
                continue;
            }

            if (rowIndex > 0)
                sql.Append(',');
            sql.Append($"(@s{rowIndex},@d{rowIndex},@v{rowIndex},@ms{rowIndex},@r{rowIndex},@rt{rowIndex},@m{rowIndex})");

            cmd.Parameters.AddWithValue($"@s{rowIndex}", row.Serial);
            cmd.Parameters.AddWithValue($"@d{rowIndex}", definitionId);
            cmd.Parameters.AddWithValue($"@v{rowIndex}", (object?)row.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@ms{rowIndex}", (object?)row.MeasurementString ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@r{rowIndex}", (object?)row.ResultStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@rt{rowIndex}", row.RunType);
            cmd.Parameters.AddWithValue($"@m{rowIndex}", row.MeasuredAt);
            rowIndex++;
        }

        if (rowIndex == 0)
            return 0; // nothing resolved in this chunk

        cmd.CommandText = sql.ToString();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rowIndex;
    }

    private void WarnUnknown(PendingMeasurement row)
    {
        var key = $"{row.CameraName}/{row.VariableName}";
        lock (_warnedUnknown)
        {
            if (!_warnedUnknown.Add(key))
                return;
        }
        _log.Warning("No active definition for {Key}; measurement dropped (logged once).", key);
    }
}
