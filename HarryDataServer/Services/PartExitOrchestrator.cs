using System.Diagnostics;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the parallel part-exit sequence. On each Part Exit (channel 2) it saves the part to
/// <c>dmcserial</c>, then runs the per-part tasks in parallel via
/// <see cref="Task.WhenAll(Task[])"/> and returns overall success + duration for the V1 ACK.
///
/// <para><b>Target concept (Philipp, 2026-07-28) — image actions always work on the low-res tree
/// (<c>[NAS] LowResIndividualPath</c>) ONLY. The NG (03), diagnostic (04) and GoldenSample (05)
/// trees are written by the camera and aged out by the retention service; no part-exit flow touches
/// them.</b></para>
///
/// <list type="table">
///   <item><term>OK</term><description>CSV · then, depending on <c>[Collage] Collage_Generate</c>
///     ONLY: <c>true</c> → build the collage into <c>[Collage] Collage_ResultImages</c>, then
///     <b>delete</b> the originals; <c>false</c> → <b>move</b> the originals to
///     <c>[NAS] BackupFolder\YYYY\MM\DD</c> (copy → size-verify → delete).
///     <c>[NAS] DeletePictures</c> is deprecated and ignored.</description></item>
///   <item><term>NG</term><description>CSV · low-res images <b>deleted without replacement</b> (the
///     NG evidence is the full-res image in 03, which is untouched here).</description></item>
///   <item><term>Unknown</term><description>(field 14 is neither OK/NG/DE) <c>dmcserial</c> like NG,
///     images deleted like NG, but <b>NO CSV row</b> (the production CSV stays clean) and always a
///     WARNING carrying the raw field and the raw telegram.</description></item>
///   <item><term>DE</term><description>images deleted, <b>no</b> <c>dmcserial</c>, <b>no</b> CSV.</description></item>
///   <item><term>MSA</term><description>acknowledged only — production tables/files are never touched.</description></item>
/// </list>
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

    /// <summary>Process one part and return success + duration for the ACK.</summary>
    private async Task<PartExitOutcome> HandleAsync(SpsPartExitData data)
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
            return new PartExitOutcome(true, total.ElapsedMilliseconds);
        }

        // DE (ST160): a scrapped part. It is NOT a finished part, so write NOTHING to dmcserial and
        // NOTHING to the production CSV — only purge its low-res images. DE is polymorphic on the live
        // line: most carry the full frame SZID (assembled part discarded), a few only the trimmer
        // serial (loose rejected trimmer), so we delete by BOTH serials when present. The measurement
        // rows stay untouched; the log line is the record of the removal.
        if (data.Result == PartResult.Deleted)
        {
            long deMs = 0;
            var deOk = await RunImagesAsync("DE", data, PartImageAction.Delete, null, ms => deMs = ms, ct)
                .ConfigureAwait(false);
            total.Stop();
            LastTiming = $"DE image delete {deMs}ms | Total {total.ElapsedMilliseconds}ms";
            Interlocked.Increment(ref _totalProcessed);
            StatsChanged?.Invoke();
            return new PartExitOutcome(deOk, total.ElapsedMilliseconds);
        }

        // An unrecognised field 14 used to be processed exactly like NG without a single log line
        // (finding B2). It now always reports itself with the raw field and the raw telegram, and it
        // is kept OUT of the production CSV — a part whose result we do not understand must not
        // appear as a finished part there.
        if (data.Result == PartResult.Unknown)
            _log.Warning("Part exit with unknown result '{Raw}' for SZID '{Szid}' — handled like NG " +
                         "(dmcserial + image delete), but NO CSV row. Raw telegram: '{Telegram}'.",
                data.ResultRaw, data.Szid, data.RawTelegram);

        // 1) Persist the part first.
        var dmcOk = await SaveDmcAsync(data, ct).ConfigureAwait(false);

        long csvMs = 0, collageMs = 0, imageMs = 0;
        var collage = _config.Config.Collage;

        // 2) Parallel tasks. The CSV is written for OK and NG only — Unknown is excluded on purpose.
        var csvTask = data.Result == PartResult.Unknown
            ? Task.FromResult(true)
            : Timed(() => _csv.WritePartAsync(data, ct), ms => csvMs = ms);

        Task<bool> collageTask = Task.FromResult(true);
        Task<bool> imageTask;

        if (data.Result == PartResult.Ok)
        {
            // The OK behaviour depends on [Collage] Collage_Generate ONLY (DeletePictures is
            // deprecated/ignored): with a collage the originals are redundant → delete; without one
            // the collage would be the only remaining evidence → move them to the backup tree.
            var collageEnabled = collage.Generate;
            if (collageEnabled)
                collageTask = Timed(() => _collage.ComposeForPartAsync(data, ct), ms => collageMs = ms);

            var action = collageEnabled ? PartImageAction.Delete : PartImageAction.MoveToBackup;

            // When a collage is being made it must read the images first, so the image task waits
            // for it (untimed) before its own work.
            imageTask = RunImagesAsync("OK", data, action, collageEnabled ? collageTask : null,
                ms => imageMs = ms, ct);

            await Task.WhenAll(csvTask, collageTask, imageTask).ConfigureAwait(false);
        }
        else
        {
            // NG / Unknown: the low-res images are deleted WITHOUT a replacement — the NG evidence is
            // the full-res image in 03_High_Resolution_NG, which no part-exit flow touches.
            imageTask = RunImagesAsync(data.Result == PartResult.Ng ? "NG" : "Unknown", data,
                PartImageAction.Delete, null, ms => imageMs = ms, ct);

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

        return new PartExitOutcome(success, total.ElapsedMilliseconds);
    }

    private static async Task<bool> Timed(Func<Task<bool>> action, Action<long> setMs)
    {
        var sw = Stopwatch.StartNew();
        try { return await action().ConfigureAwait(false); }
        catch { return false; }
        finally { sw.Stop(); setMs(sw.ElapsedMilliseconds); }
    }

    /// <summary>
    /// THE image step of every part state (OK / NG / DE / Unknown). Searches the low-res individual
    /// tree ONLY — the NG (03), diagnostic (04) and GoldenSample (05) trees belong to the camera and
    /// the retention service — matches field-accurately on Serial1 by the frame SZID (19) and/or the
    /// trimmer serial (13), skips MSA images, and then deletes or moves to the backup tree.
    ///
    /// <para>0 matches is a WARNING, not a failure; only a real file-system failure fails the ACK.
    /// When <paramref name="dependency"/> is set (the collage must read the images first) it is
    /// awaited before the work starts and is excluded from the timing.</para>
    /// </summary>
    private async Task<bool> RunImagesAsync(
        string context, SpsPartExitData data, PartImageAction action,
        Task<bool>? dependency, Action<long> setMs, CancellationToken ct)
    {
        if (dependency is not null)
        {
            try { await dependency.ConfigureAwait(false); }
            catch { /* collage failure must not block image cleanup */ }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var serials = new[] { data.Szid, data.VirtualSerial }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (serials.Length == 0)
            {
                _log.Warning("{Context} part exit with neither a frame nor a trimmer serial; no image touched.",
                    context);
                return true;
            }

            // Source of the part's low-res images. Prefer the explicit [Collage] Collage_SingleImages,
            // fall back to [NAS] LowResIndividualPath — an empty key must not silently disable cleanup.
            var collage = _config.Config.Collage;
            var lowRes = !string.IsNullOrWhiteSpace(collage.SingleImagesPath)
                ? collage.SingleImagesPath
                : _config.Config.Nas.LowResIndividualPath;

            // ImageHandler logs the count + keys itself (deletion is irreversible → never silent).
            var result = await _images
                .ApplyAsync(context, serials, lowRes, action, _config.Config.Nas.BackupFolder, ct)
                .ConfigureAwait(false);
            return result.Failed == 0;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Context} image handling failed for SZID='{Szid}' trimmer='{Trimmer}'.",
                context, data.Szid, data.VirtualSerial);
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
        // A part without a frame serial cannot be identified. Writing it would store
        // serial_number = '' and — because of UNIQUE KEY uk_serial + ON DUPLICATE KEY UPDATE — every
        // such part would overwrite ONE shared row (finding B1). A row without a serial carries no
        // usable information (no measurement lookup, no image link, no traceability), so the insert is
        // skipped and reported instead. NULL is deliberately not used: it would let unlimited
        // serial-less rows accumulate (UNIQUE ignores NULL), which is just as useless but unbounded.
        if (string.IsNullOrWhiteSpace(part.Szid))
        {
            _log.Warning("Part exit without a frame serial (result={Result}, DMC='{Dmc}', trimmer='{Trimmer}') — " +
                         "no dmcserial row written. Raw telegram: '{Telegram}'.",
                part.Result, part.Dmc, part.VirtualSerial, part.RawTelegram);
            return true;   // not a processing failure — the PLC gets a positive ACK
        }

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
