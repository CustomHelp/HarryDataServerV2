using System.Collections.Concurrent;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Background Part Exit pipeline. Subscribes to <see cref="ISpsServer.PartExitReceived"/>,
/// enqueues the parsed telegram (no I/O on the SPS thread) and upserts one row per
/// finished part into <c>dmcserial</c> (keyed on serial_number). Same pattern as
/// <see cref="MeasurementProcessor"/>.
/// </summary>
public sealed class PartExitProcessor : IPartExitProcessor
{
    private const int MaxQueue = 100_000;
    private const int MaxItemsPerFlush = 5_000;

    private readonly ISpsServer _sps;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;
    private readonly TimeSpan _flushInterval;

    private readonly ConcurrentQueue<SpsPartExitData> _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalUpserted;
    private bool _started;

    public PartExitProcessor(ISpsServer sps, IDatabaseService database, IConfigService config, ILogService log)
    {
        _sps = sps;
        _database = database;
        _log = log;
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));
    }

    public int PendingCount => _queue.Count;
    public long TotalUpserted => Interlocked.Read(ref _totalUpserted);
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        _sps.PartExitReceived += OnPartExitReceived;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Part Exit processor started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _sps.PartExitReceived -= OnPartExitReceived;

        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    // --- Receive side (SPS thread; in-memory only) ---

    private void OnPartExitReceived(object? sender, SpsPartExitEventArgs e)
    {
        if (_queue.Count >= MaxQueue)
        {
            _log.Warning("Part Exit queue full ({Max}); dropping part {Serial}.", MaxQueue, e.Data.Szid);
            return;
        }

        _queue.Enqueue(e.Data);
    }

    // --- Flush side (dedicated background task; performs all DB I/O) ---

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "Part Exit flush failed."); }
        }

        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(ex, "Final Part Exit flush failed."); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty)
            return;

        // Wait until the database is ready before draining (avoids losing the queue).
        if (_database.Status != DatabaseStatus.Ready)
            return;

        var rows = new List<SpsPartExitData>();
        while (rows.Count < MaxItemsPerFlush && _queue.TryDequeue(out var item))
            rows.Add(item);

        if (rows.Count == 0)
            return;

        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

        var upserted = 0;
        foreach (var part in rows)
            upserted += await UpsertAsync(conn, part, ct).ConfigureAwait(false);

        if (upserted > 0)
        {
            Interlocked.Add(ref _totalUpserted, upserted);
            _log.Debug("Upserted {Count} part(s) into dmcserial; {Pending} pending.", upserted, _queue.Count);
            StatsChanged?.Invoke();
        }
    }

    private static async Task<int> UpsertAsync(MySqlConnection conn, SpsPartExitData part, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dmcserial
  (serial_number, serial_trimmer, dmc, m1x_module, m1x_nest, m3x_module, m3x_nest, m50_nest,
   order_name, m1x_humidity, result_status)
VALUES
  (@serial, @trimmer, @dmc, @m1xmod, @m1xnest, @m3xmod, @m3xnest, @m50nest,
   @order, @humidity, @result)
ON DUPLICATE KEY UPDATE
  serial_trimmer = VALUES(serial_trimmer),
  dmc            = VALUES(dmc),
  m1x_module     = VALUES(m1x_module),
  m1x_nest       = VALUES(m1x_nest),
  m3x_module     = VALUES(m3x_module),
  m3x_nest       = VALUES(m3x_nest),
  m50_nest       = VALUES(m50_nest),
  order_name     = VALUES(order_name),
  m1x_humidity   = VALUES(m1x_humidity),
  result_status  = VALUES(result_status);";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@serial", part.Szid);
        cmd.Parameters.AddWithValue("@trimmer", Nullable(part.VirtualSerial));
        cmd.Parameters.AddWithValue("@dmc", Nullable(part.Dmc));
        cmd.Parameters.AddWithValue("@m1xmod", (object?)part.M1xModule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@m1xnest", (object?)part.M1xNest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@m3xmod", Nullable(part.M3xModule));
        cmd.Parameters.AddWithValue("@m3xnest", Nullable(part.M3xNest));
        cmd.Parameters.AddWithValue("@m50nest", Nullable(part.M50Nest));
        cmd.Parameters.AddWithValue("@order", Nullable(part.OrderName));
        cmd.Parameters.AddWithValue("@humidity", (object?)part.Humidity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@result", part.ResultStatusCode);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return 1;
    }

    private static object Nullable(string value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
