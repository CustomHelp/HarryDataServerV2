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

        // MSA / LimitSample test parts must never touch the production tables (task 6):
        // their data lives in msa_measurements / msa_results only. Acknowledge the part exit
        // without persisting to dmcserial or running the CSV / Collage / Image flow.
        if (data.IsMsa)
        {
            total.Stop();
            Interlocked.Increment(ref _totalProcessed);
            return true;
        }

        // 1) Persist the part first.
        var dmcOk = await SaveDmcAsync(data, ct).ConfigureAwait(false);

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
            // Source folder for the individual OK-part images. Prefer the explicit [Collage]
            // Collage_SingleImages, but fall back to [NAS] LowResIndividualPath when it is unset —
            // otherwise an empty key silently disables cleanup and the low-res folder fills up.
            var imageSource = !string.IsNullOrWhiteSpace(collage.SingleImagesPath)
                ? collage.SingleImagesPath
                : nas.LowResIndividualPath;

            var dependency = collageEnabled ? collageTask : null;
            imageTask = RunImagesAfterAsync(dependency, serials, imageSource,
                nas.DeletePictures, nas.BackupFolder, ms => imageMs = ms, ct);

            await Task.WhenAll(csvTask, collageTask, imageTask).ConfigureAwait(false);
        }
        else if (data.Result == PartResult.Deleted)
        {
            // DE from ST160: a rejected trimmer sub-assembly never entered a frame, so it has no
            // normal part-exit and no collage/frame images. ST160 sends DE carrying the trimmer
            // serial to purge that trimmer's images. The DB rows (dmcserial result_status=-1 and the
            // measurements_serial_trimmer measurements) are KEPT for traceability — only images go.
            imageTask = RunDeTrimmerDeleteAsync(data, ms => imageMs = ms, ct);
            await Task.WhenAll(csvTask, imageTask).ConfigureAwait(false);
        }
        else // NG: CSV only.
        {
            // SOW §5.2.3: NG parts produce no collage, so their low-res individual images
            // are NOT deleted here. They are kept and removed later by ImageCleanupService,
            // together with the matching full-res NG image (linked by serial prefix).
            imageTask = Task.FromResult(true);

            await csvTask.ConfigureAwait(false);
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

    /// <summary>
    /// DE (ST160) image purge for a rejected trimmer sub-assembly. Searches the low-res individual,
    /// high-res NG and high-res diagnostic roots (each recursively, incl. NAS-sorted day-folders) for
    /// images whose Field 1 starts with the normalised 13-char trimmer serial, and deletes them.
    /// 0 matches is a WARNING (not a failure); only a real exception fails the ACK.
    /// </summary>
    private async Task<bool> RunDeTrimmerDeleteAsync(SpsPartExitData data, Action<long> setMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var trimmer = data.VirtualSerial;
            if (string.IsNullOrWhiteSpace(trimmer))
            {
                _log.Warning("DE part exit without a trimmer serial (SZID='{Szid}'); no images to delete.", data.Szid);
                return true;
            }

            var nas = _config.Config.Nas;
            var roots = new[] { nas.LowResIndividualPath, nas.HighResNgPath, nas.HighResDiagnosticPath }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            var deleted = await _images.DeleteByTrimmerSerialAsync(trimmer, roots, ct).ConfigureAwait(false);
            if (deleted > 0)
                _log.Information("DE: {Count} trimmer image(s) for {Serial} deleted.", deleted, trimmer);
            else
                _log.Warning("DE: no images found for trimmer {Serial} (searched {Roots}).",
                    trimmer, string.Join(", ", roots));
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "DE trimmer image deletion failed for {Serial}.", data.VirtualSerial);
            return false;
        }
        finally
        {
            sw.Stop();
            setMs(sw.ElapsedMilliseconds);
        }
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
  (serial_number, serial_trimmer, dmc, m1x_module, m1x_nest, m2x_module, m2x_nest,
   m3x_module, m3x_nest, m50_nest, order_name, m1x_temperature, m1x_humidity, result_status)
VALUES
  (@serial, @trimmer, @dmc, @m1xmod, @m1xnest, @m2xmod, @m2xnest,
   @m3xmod, @m3xnest, @m50nest, @order, @temp, @humidity, @result)
ON DUPLICATE KEY UPDATE
  serial_trimmer  = VALUES(serial_trimmer),
  dmc             = VALUES(dmc),
  m1x_module      = VALUES(m1x_module),
  m1x_nest        = VALUES(m1x_nest),
  m2x_module      = VALUES(m2x_module),
  m2x_nest        = VALUES(m2x_nest),
  m3x_module      = VALUES(m3x_module),
  m3x_nest        = VALUES(m3x_nest),
  m50_nest        = VALUES(m50_nest),
  order_name      = VALUES(order_name),
  m1x_temperature = VALUES(m1x_temperature),
  m1x_humidity    = VALUES(m1x_humidity),
  result_status   = VALUES(result_status);";

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@serial", CapSerial(part.Szid, "serial_number"));
            cmd.Parameters.AddWithValue("@trimmer", NullableSerial(part.VirtualSerial, "serial_trimmer"));
            cmd.Parameters.AddWithValue("@dmc", Nullable(part.Dmc));
            cmd.Parameters.AddWithValue("@m1xmod", (object?)part.M1xModule ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m1xnest", (object?)part.M1xNest ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m2xmod", (object?)part.M2xModule ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m2xnest", (object?)part.M2xNest ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m3xmod", Nullable(part.M3xModule));
            cmd.Parameters.AddWithValue("@m3xnest", Nullable(part.M3xNest));
            cmd.Parameters.AddWithValue("@m50nest", Nullable(part.M50Nest));
            cmd.Parameters.AddWithValue("@order", Nullable(part.OrderName));
            cmd.Parameters.AddWithValue("@temp", (object?)part.Temperature ?? DBNull.Value);
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

    /// <summary>
    /// Cap a Serial1 value to the DB column width (<see cref="SerialField.MaxLength"/>), logging
    /// a WARNING on truncation. The part-exit telegram comes from the SPS (not the camera parser),
    /// so this is the enforcement point for the VARCHAR(22) serial columns (CLAUDE.md §4).
    /// </summary>
    private string CapSerial(string value, string column)
    {
        if (!string.IsNullOrEmpty(value) && value.Length > SerialField.MaxLength)
        {
            _log.Warning("Part exit {Column} '{Value}' exceeds {Max} chars; truncated.",
                column, value, SerialField.MaxLength);
            return value[..SerialField.MaxLength];
        }
        return value;
    }

    private object NullableSerial(string value, string column) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : CapSerial(value, column);
}
