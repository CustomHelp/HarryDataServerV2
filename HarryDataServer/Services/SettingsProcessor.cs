using System.Collections.Concurrent;
using System.Text;
using HarryDataServer.Communication;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Background settings pipeline. Subscribes to every camera's
/// <see cref="TcpCameraClient.SettingsReceived"/> event, enqueues the limit
/// samples (no I/O on the receive thread) and batches them into the
/// <c>settings</c> history table. Identical pattern to <see cref="MeasurementProcessor"/>.
/// </summary>
public sealed class SettingsProcessor : ISettingsProcessor
{
    private const int MaxQueue = 200_000;
    private const int MaxItemsPerFlush = 10_000;

    private readonly ICameraService _cameras;
    private readonly IDatabaseService _database;
    private readonly SettingDefinitionCache _cache;
    private readonly ILogService _log;

    private readonly ConcurrentQueue<PendingSetting> _queue = new();
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly HashSet<string> _warnedUnknown = new();

    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalInserted;
    private bool _started;

    public SettingsProcessor(
        ICameraService cameras,
        IDatabaseService database,
        SettingDefinitionCache cache,
        IConfigService config,
        ILogService log)
    {
        _cameras = cameras;
        _database = database;
        _cache = cache;
        _log = log;

        _batchSize = Math.Max(1, config.Config.SqlSettings.BatchSize);
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));
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
            client.SettingsReceived += OnSettingsReceived;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Settings processor started (batch={Batch}, interval={Interval}s).",
            _batchSize, _flushInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        foreach (var client in _cameras.Clients)
            client.SettingsReceived -= OnSettingsReceived;

        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    // --- Receive side (camera thread; in-memory only) ---

    private void OnSettingsReceived(object? sender, SettingsTelegramEventArgs e)
    {
        if (_queue.Count >= MaxQueue)
        {
            _log.Warning("Settings queue full ({Max}); dropping {Camera} settings.",
                MaxQueue, e.Telegram.ControllerName);
            return;
        }

        var recordedAt = DateTime.Now;
        foreach (var s in e.Settings)
        {
            _queue.Enqueue(new PendingSetting
            {
                CameraName = e.Telegram.ControllerName,
                SettingName = s.SettingName,
                ParameterSet = s.ParameterSet,
                LimitValue = s.Value,
                Version = string.IsNullOrEmpty(e.Telegram.Version) ? null : e.Telegram.Version,
                RecordedAt = recordedAt,
            });
        }
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
            catch (Exception ex) { _log.Error(ex, "Settings flush failed."); }
        }

        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(ex, "Final settings flush failed."); }
    }

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
            _log.Error(ex, "Failed to load setting definition cache; settings processor stopping.");
            return false;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty)
            return;

        var rows = new List<PendingSetting>();
        while (rows.Count < MaxItemsPerFlush && _queue.TryDequeue(out var item))
            rows.Add(item);

        if (rows.Count == 0)
            return;

        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

        var inserted = 0;
        for (var offset = 0; offset < rows.Count; offset += _batchSize)
        {
            var chunk = rows.GetRange(offset, Math.Min(_batchSize, rows.Count - offset));
            inserted += await InsertChunkAsync(conn, chunk, ct).ConfigureAwait(false);
        }

        if (inserted > 0)
        {
            Interlocked.Add(ref _totalInserted, inserted);
            _log.Debug("Flushed {Inserted} setting(s); {Pending} pending.", inserted, _queue.Count);
            StatsChanged?.Invoke();
        }
    }

    private async Task<int> InsertChunkAsync(MySqlConnection conn, List<PendingSetting> chunk, CancellationToken ct)
    {
        var sql = new StringBuilder(
            "INSERT INTO `settings` (camera_id, definition_id, parameter_set, limit_value, version, recorded_at) VALUES ");

        await using var cmd = new MySqlCommand { Connection = conn };

        var rowIndex = 0;
        foreach (var row in chunk)
        {
            if (!_cache.TryGet(row.CameraName, row.SettingName, out var reference))
            {
                WarnUnknown(row);
                continue;
            }

            if (rowIndex > 0)
                sql.Append(',');
            sql.Append($"(@c{rowIndex},@d{rowIndex},@p{rowIndex},@v{rowIndex},@ver{rowIndex},@r{rowIndex})");

            cmd.Parameters.AddWithValue($"@c{rowIndex}", reference.CameraId);
            cmd.Parameters.AddWithValue($"@d{rowIndex}", reference.DefinitionId);
            cmd.Parameters.AddWithValue($"@p{rowIndex}", row.ParameterSet);
            cmd.Parameters.AddWithValue($"@v{rowIndex}", row.LimitValue);
            cmd.Parameters.AddWithValue($"@ver{rowIndex}", (object?)row.Version ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@r{rowIndex}", row.RecordedAt);
            rowIndex++;
        }

        if (rowIndex == 0)
            return 0;

        cmd.CommandText = sql.ToString();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rowIndex;
    }

    private void WarnUnknown(PendingSetting row)
    {
        var key = $"{row.CameraName}/{row.SettingName}";
        lock (_warnedUnknown)
        {
            if (!_warnedUnknown.Add(key))
                return;
        }
        _log.Warning("No setting definition for {Key}; setting dropped (logged once).", key);
    }
}
