using System.Diagnostics;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the parallel part-exit sequence. On each Part Exit (channel 2) it saves
/// the part to <c>dmcserial</c>, then runs the per-part tasks in parallel via
/// <see cref="Task.WhenAll(Task[])"/> and returns overall success for the V1 ACK:
///
///   OK  → CSV (always) ‖ Collage (if Collage_Generate) ‖ image delete/backup (always)
///   NG  → CSV (always) ‖ image delete/backup (always)   [no collage]
///
/// Each task is timed separately. Budget: ~450 ms per part.
/// </summary>
public sealed class PartExitOrchestrator : IPartExitOrchestrator
{
    private const int BudgetMs = 450;

    private readonly ISpsServer _sps;
    private readonly IDatabaseService _database;
    private readonly ICsvService _csv;
    private readonly ICollageService _collage;
    private readonly ImageHandler _images;
    private readonly IConfigService _config;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;

    private CancellationTokenSource? _cts;
    private long _totalProcessed;
    private bool _started;

    public PartExitOrchestrator(
        ISpsServer sps, IDatabaseService database, ICsvService csv, ICollageService collage,
        ImageHandler images, IConfigService config, ISystemHealth health, ILogService log)
    {
        _sps = sps;
        _database = database;
        _csv = csv;
        _collage = collage;
        _images = images;
        _config = config;
        _health = health;
        _log = log;
    }

    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);
    public string LastTiming { get; private set; } = "—";
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sps.PartExitHandler = HandleAsync;
        _log.Information("Part exit orchestrator started (parallel CSV/Collage/Images, {Budget}ms budget).", BudgetMs);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _sps.PartExitHandler = null;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Process one part and return success for the ACK.</summary>
    private async Task<bool> HandleAsync(SpsPartExitData data)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var total = Stopwatch.StartNew();

        // 1) Persist the part first.
        var dmcOk = await SaveDmcAsync(data, ct).ConfigureAwait(false);

        // MSA test parts are not exported / collaged / cleaned by the production flow.
        if (data.IsMsa)
        {
            total.Stop();
            Interlocked.Increment(ref _totalProcessed);
            return dmcOk;
        }

        long csvMs = 0, collageMs = 0, imageMs = 0;
        var serials = CollageService.FormattedSerials(data);
        var collage = _config.Config.Collage;
        var nas = _config.Config.Nas;

        // 2) Parallel tasks.
        var csvTask = Timed(() => _csv.WritePartAsync(data, ct), ms => csvMs = ms);

        Task<bool> collageTask = Task.FromResult(true);
        Task<bool> imageTask;

        if (data.Result == PartResult.Ok)
        {
            var collageEnabled = collage.Generate;
            if (collageEnabled)
                collageTask = Timed(() => _collage.ComposeForPartAsync(data, ct), ms => collageMs = ms);

            // Images always run; when a collage is being made it must read the images
            // first, so the image task waits for it (untimed) before its own work.
            var dependency = collageEnabled ? collageTask : null;
            imageTask = RunImagesAfterAsync(dependency, serials, collage.SingleImagesPath,
                nas.DeletePictures, nas.BackupFolder, ms => imageMs = ms, ct);

            await Task.WhenAll(csvTask, collageTask, imageTask).ConfigureAwait(false);
        }
        else // NG (or deleted): CSV + images, no collage.
        {
            imageTask = RunImagesAfterAsync(null, serials, collage.SingleImagesPath,
                nas.DeletePictures, nas.BackupFolder, ms => imageMs = ms, ct);

            await Task.WhenAll(csvTask, imageTask).ConfigureAwait(false);
        }

        total.Stop();

        var success = dmcOk && csvTask.Result && collageTask.Result && imageTask.Result;
        LastTiming = $"CSV {csvMs}ms | Collage {collageMs}ms | Images {imageMs}ms | Total {total.ElapsedMilliseconds}ms";
        Interlocked.Increment(ref _totalProcessed);
        StatsChanged?.Invoke();

        if (total.ElapsedMilliseconds > BudgetMs)
            _log.Warning("Part exit took {Ms}ms (> {Budget}ms budget) for {Serial}.",
                total.ElapsedMilliseconds, BudgetMs, data.Szid);

        return success;
    }

    private static async Task<bool> Timed(Func<Task<bool>> action, Action<long> setMs)
    {
        var sw = Stopwatch.StartNew();
        try { return await action().ConfigureAwait(false); }
        catch { return false; }
        finally { sw.Stop(); setMs(sw.ElapsedMilliseconds); }
    }

    /// <summary>
    /// Wait for the (optional) collage to finish reading the images, then handle them.
    /// Only the actual image work is timed — the collage wait is excluded.
    /// </summary>
    private async Task<bool> RunImagesAfterAsync(
        Task<bool>? dependency, IReadOnlyList<string> serials, string searchPath,
        bool deletePictures, string backupFolder, Action<long> setMs, CancellationToken ct)
    {
        if (dependency is not null)
        {
            try { await dependency.ConfigureAwait(false); }
            catch { /* collage failure must not block image cleanup */ }
        }

        var sw = Stopwatch.StartNew();
        try { return await _images.HandleAsync(serials, searchPath, deletePictures, backupFolder, ct).ConfigureAwait(false); }
        catch { return false; }
        finally { sw.Stop(); setMs(sw.ElapsedMilliseconds); }
    }

    private async Task<bool> SaveDmcAsync(SpsPartExitData part, CancellationToken ct)
    {
        if (_database.Status != DatabaseStatus.Ready)
        {
            _health.Report(HealthSources.PartExit, HealthSeverity.Error, "Part exit: database not ready");
            return false;
        }

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

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
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

            _health.Clear(HealthSources.PartExit);
            return true;
        }
        catch (Exception ex)
        {
            _health.Report(HealthSources.PartExit, HealthSeverity.Error, $"Part exit DB write failing: {ex.Message}");
            _log.Error(ex, "Failed to save dmcserial for {Serial}.", part.Szid);
            return false;
        }
    }

    private static object Nullable(string value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
