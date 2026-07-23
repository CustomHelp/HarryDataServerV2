using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;
// Import only the per-part reference types — HarryShared.Data also has an MsaReferenceFile that would
// clash with the server's HarryDataServer.Configuration.MsaReferenceFile.
using LimitSampleReference = HarryShared.Data.LimitSampleReference;
using Msa1Reference = HarryShared.Data.Msa1Reference;

namespace HarryDataServer.Services;

/// <summary>
/// MSA engine. Two responsibilities:
/// 1) Storage — subscribes to MSA-mode "Results" telegrams and persists them to
///    <c>msa_measurements</c> via a background queue (same pattern as the other
///    processors; R_/V_ combined per pair).
/// 2) Evaluation — provides the SPS <see cref="ISpsServer.MsaRequestHandler"/>. A
///    request returns "Wait" while a background evaluation runs; once finished the
///    result is cached and returned as "OK"/"NG" (poll model, CLAUDE.md section 5).
/// </summary>
public sealed class MsaService : IMsaService
{
    private const int MaxItemsPerFlush = 10_000;

    /// <summary>How many times EvaluateAsync re-checks the DB for the run's measurements before
    /// giving up (push model — the PLC does not re-poll). Each wait is one flush interval, so with
    /// the default 1 s interval this is ~60 s for the rows to be committed by the flush loop.</summary>
    private const int MaxGatherAttempts = 60;

    /// <summary>Consecutive stable ticks (row count unchanged) with an EMPTY write queue that mark a
    /// run as COMPLETE before it is evaluated (task A1). An OK must never come from a partial/premature
    /// evaluation, so the gather loop waits for the whole run's rows to have landed and settled.</summary>
    private const int SettleTicks = 3;

    /// <summary>Fallback stability window when the shared write queue never drains (another module's
    /// MSA is running concurrently): accept the run as settled after this many stable ticks even if the
    /// queue is not empty, so a concurrent run cannot block this one forever.</summary>
    private const int SettleTicksBusy = 8;

    private readonly ICameraService _cameras;
    private readonly IDatabaseService _database;
    private readonly MeasurementDefinitionCache _cache;
    private readonly ISpsServer _sps;
    private readonly IConfigService _config;
    private readonly MsaReferenceLoader _referenceLoader;
    private readonly IPdfReportService _pdf;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;

    private readonly ConcurrentQueue<PendingMsaMeasurement> _queue = new();
    private readonly ConcurrentDictionary<string, string> _evaluations = new(); // baseId -> response word
    private readonly ConcurrentDictionary<string, MsaReferenceFile?> _references = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _flushInterval;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private bool _started;

    public MsaService(
        ICameraService cameras,
        IDatabaseService database,
        MeasurementDefinitionCache cache,
        ISpsServer sps,
        IConfigService config,
        MsaReferenceLoader referenceLoader,
        IPdfReportService pdf,
        ISystemHealth health,
        ILogService log)
    {
        _cameras = cameras;
        _database = database;
        _cache = cache;
        _sps = sps;
        _config = config;
        _referenceLoader = referenceLoader;
        _pdf = pdf;
        _health = health;
        _log = log;
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));
    }

    public int PendingCount => _queue.Count;

    /// <inheritdoc />
    public event Action<MsaRunCompleted>? RunCompleted;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        foreach (var client in _cameras.Clients)
            client.ResultsReceived += OnResultsReceived;

        _sps.MsaRequestHandler = HandleMsaRequest;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("MSA service started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        foreach (var client in _cameras.Clients)
            client.ResultsReceived -= OnResultsReceived;

        _sps.MsaRequestHandler = null;
        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    // --- Storage: receive side (camera thread; in-memory only) ---

    private void OnResultsReceived(object? sender, ResultsTelegramEventArgs e)
    {
        var telegram = e.Telegram;
        if (!telegram.IsMsa)
            return; // production telegrams handled by MeasurementProcessor

        // MSA serial layout: Serial1 (pos 4–35) = BaseID(14) + loop(3); Serial2 (pos 36–67) = DMC.
        var dmc = telegram.Serial2;       // DMC of the test part
        if (!BaseId.TrySplitRun(telegram.Serial1, out var baseId, out var loop) || string.IsNullOrWhiteSpace(dmc))
        {
            _log.Debug("{Camera}: MSA telegram missing/invalid BaseID or DMC; skipped.", telegram.ControllerName);
            return;
        }

        var msaType = MsaTypeExtensions.FromMode(telegram.Mode);
        var measuredAt = DateTime.Now;

        // Problem 2 diagnostics: the msa_measurements insert mapping is identical to the (working)
        // production path (measurement_value ← V_ value, result_status ← R_ status). When the stored
        // rows show a value of only 0/1 and a NULL result_status, the pass/fail is arriving in the
        // V_ field while the R_ field is empty — i.e. an upstream (camera/SPS) telegram-content issue,
        // not a storage bug. Log the raw telegram and the R_/V_ population so a live run pinpoints
        // exactly which field carries what (do not silently accept a mismatch).
        LogMsaExtractionDiagnostics(telegram, e.Measurements);

        // Combine R_/V_ pairs the same way as production measurements.
        var combined = MeasurementRowBuilder.Build(telegram.ControllerName, dmc, false, 0, measuredAt, e.Measurements);
        foreach (var row in combined)
        {
            _queue.Enqueue(new PendingMsaMeasurement
            {
                Dmc = dmc,
                BaseId = baseId,
                ControllerName = telegram.ControllerName,
                VariableName = row.VariableName,
                LoopNumber = loop,
                Value = row.Value,
                MeasurementString = row.MeasurementString,
                ResultStatus = row.ResultStatus,
                MsaType = msaType,
                MsaVersion = string.IsNullOrEmpty(telegram.Version) ? null : telegram.Version,
                MeasuredAt = measuredAt,
            });
        }
    }

    /// <summary>
    /// Summarise an MSA "Results" telegram for troubleshooting Problem 2: how many R_ (result) and
    /// V_ (value) samples were extracted, and how many of each actually carried a parsed
    /// status/value. If R_ statuses are absent but V_ values are 0/1, the camera is placing the
    /// pass/fail in the value field — surfaced here rather than silently stored as value=0/1,
    /// result_status=NULL. Also dumps the raw telegram + first few samples at Debug.
    /// </summary>
    private void LogMsaExtractionDiagnostics(ParsedTelegram telegram, IReadOnlyList<MeasurementSample> samples)
    {
        var results = 0; var resultsWithStatus = 0;
        var values = 0; var valuesWithValue = 0;
        foreach (var s in samples)
        {
            if (s.IsResult) { results++; if (s.ResultStatus.HasValue) resultsWithStatus++; }
            else { values++; if (s.Value.HasValue) valuesWithValue++; }
        }

        if (results > 0 && resultsWithStatus == 0)
            _log.Warning(
                "{Camera}: MSA {Mode} telegram has {Results} R_ result fields but NONE carry a status (result_status will be NULL); " +
                "V_ values present: {ValuesWithValue}/{Values}. Pass/fail is likely in the V_ field — verify camera output (Problem 2).",
                telegram.ControllerName, telegram.Mode, results, valuesWithValue, values);
        else
            _log.Debug(
                "{Camera}: MSA {Mode} extraction — R_ {ResultsWithStatus}/{Results} with status, V_ {ValuesWithValue}/{Values} with value.",
                telegram.ControllerName, telegram.Mode, resultsWithStatus, results, valuesWithValue, values);

        _log.Debug("{Camera}: MSA raw telegram: {Raw}", telegram.ControllerName, telegram.Raw);
    }

    // --- Storage: flush side (dedicated background task) ---

    private async Task RunAsync(CancellationToken ct)
    {
        // Wait for the database and the shared definition cache.
        while (!ct.IsCancellationRequested && _database.Status != DatabaseStatus.Ready)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        if (ct.IsCancellationRequested)
            return;

        if (!_cache.IsLoaded)
        {
            try
            {
                await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
                await _cache.LoadAsync(conn, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.Error(ex, "MSA: failed to load definition cache."); }
        }

        // Ensure a DEMO_<module>.json MSA1 template exists for each module (task C2). Best-effort.
        await EnsureDemoTemplatesAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "MSA measurement flush failed."); }
        }

        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(ex, "Final MSA measurement flush failed."); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty)
        {
            _health.Clear(HealthSources.Msa);
            return;
        }

        var rows = new List<PendingMsaMeasurement>();
        while (rows.Count < MaxItemsPerFlush && _queue.TryDequeue(out var item))
            rows.Add(item);

        if (rows.Count == 0)
            return;

        // Same isolation/requeue guard as the production pipeline.
        var stored = await FlushHelper.WriteAsync(
            rows,
            InsertBatchAsync,
            InsertSingleAsync,
            Requeue,
            _health, _log, HealthSources.Msa,
            r => $"{r.ControllerName}/{r.VariableName} base={r.BaseId}",
            ct).ConfigureAwait(false);

        if (stored > 0)
            _log.Debug("Stored {Count} MSA measurement(s); {Pending} pending.", stored, _queue.Count);
    }

    private async Task<int> InsertBatchAsync(IReadOnlyList<PendingMsaMeasurement> rows, CancellationToken ct)
    {
        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var stored = 0;
        foreach (var row in rows)
            stored += await InsertOneAsync(conn, tx, row, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return stored;
    }

    private async Task InsertSingleAsync(PendingMsaMeasurement row, CancellationToken ct)
    {
        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await InsertOneAsync(conn, null, row, ct).ConfigureAwait(false);
    }

    private async Task<int> InsertOneAsync(MySqlConnection conn, MySqlTransaction? tx, PendingMsaMeasurement row, CancellationToken ct)
    {
        if (!_cache.TryGet(row.ControllerName, row.VariableName, out var definitionId))
            return 0; // unknown definition — not a DB fault

        const string sql = @"
INSERT INTO msa_measurements
  (dmc, base_id, loop_number, controller_name, definition_id, measurement_value, measurement_string, result_status, msa_type, msa_version, measured_at)
VALUES
  (@dmc, @base, @loop, @ctrl, @def, @val, @str, @res, @type, @ver, @at);";

        await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
        cmd.Parameters.AddWithValue("@dmc", row.Dmc);
        cmd.Parameters.AddWithValue("@base", row.BaseId);
        cmd.Parameters.AddWithValue("@loop", row.LoopNumber);
        cmd.Parameters.AddWithValue("@ctrl", row.ControllerName);
        cmd.Parameters.AddWithValue("@def", definitionId);
        cmd.Parameters.AddWithValue("@val", (object?)row.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@str", (object?)row.MeasurementString ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@res", (object?)row.ResultStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", row.MsaType.ToDbString());
        cmd.Parameters.AddWithValue("@ver", (object?)row.MsaVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", row.MeasuredAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return 1;
    }

    private void Requeue(IReadOnlyList<PendingMsaMeasurement> rows)
    {
        foreach (var row in rows)
            _queue.Enqueue(row);
    }

    // --- Read path for the UI (load historical runs from msa_results) ---

    public async Task<IReadOnlyList<MsaRunDto>> GetRunsAsync(string module, MsaType type, CancellationToken ct = default)
    {
        var runs = new List<MsaRunDto>();
        if (_database.Status != DatabaseStatus.Ready)
            return runs;

        const string sql = @"
SELECT base_id, controller_name, evaluated_at, display_name, cg_value, cgk_value, pct_tolerance,
       expected_value, actual_value, passed, n_values, mean_value, std_dev, reference_value,
       tolerance, criterion, reason, evaluated, dmc, matched_reference, match_score
FROM msa_results
WHERE msa_type = @type AND controller_name LIKE @mod
ORDER BY evaluated_at, base_id, id;";

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@type", type.ToDbString());
            cmd.Parameters.AddWithValue("@mod", module + "%");
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var byBaseId = new Dictionary<string, (string Ctrl, DateTime At, List<MsaResultRow> Rows)>();
            var order = new List<string>();

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var baseId = reader.GetString(0);
                if (!byBaseId.TryGetValue(baseId, out var entry))
                {
                    entry = (reader.GetString(1), reader.GetDateTime(2), new List<MsaResultRow>());
                    byBaseId[baseId] = entry;
                    order.Add(baseId);
                }

                entry.Rows.Add(new MsaResultRow
                {
                    Controller = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    DisplayName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Cg = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    Cgk = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    PctTolerance = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    Expected = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Actual = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Passed = reader.GetInt32(9) != 0,
                    N = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    Mean = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    StdDev = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    ReferenceValue = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                    Tolerance = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                    Criterion = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                    Reason = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                    Evaluated = !reader.IsDBNull(17) && reader.GetInt32(17) != 0,
                    Dmc = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                    MatchedReference = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
                    MatchScore = reader.IsDBNull(20) ? null : reader.GetDouble(20),
                });
            }

            foreach (var baseId in order)
            {
                var e = byBaseId[baseId];
                runs.Add(new MsaRunDto
                {
                    Module = module,
                    Controller = e.Ctrl,
                    BaseId = baseId,
                    MsaType = type,
                    EvaluatedAt = e.At,
                    Rows = e.Rows,
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load MSA runs for {Module}/{Type}.", module, type);
        }

        return runs;
    }

    // --- Evaluation: SPS handler (sync, poll model) ---

    private string HandleMsaRequest(string module, string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId))
        {
            _log.Warning("MSA {Module}: request with missing BaseID → Error.", module);
            return "Error;missing BaseID";
        }

        // Already evaluated, in progress, or in a terminal error? Return the cached response.
        // A repeated Request in the "Wait" state MUST answer "Wait" again (idempotent, CLAUDE.md
        // §5): while the evaluation is still running the entry holds "Wait"; while the run data is
        // not yet in the DB the entry is ABSENT (removed by EvaluateAsync), so the branch below
        // re-triggers and again answers "Wait" — it never falls through to Error (Problem 3).
        if (_evaluations.TryGetValue(baseId, out var response))
        {
            _log.Information("MSA {Module} request BaseID {Base}: state={State} → {Response}.",
                module, baseId, DescribeState(response), response);
            return response;
        }

        // No entry yet (first request, or a prior attempt found no data yet and cleared itself):
        // start a fresh evaluation and answer Wait. TryAdd guards against two concurrent requests
        // both starting an evaluation.
        if (_evaluations.TryAdd(baseId, "Wait"))
        {
            _log.Information("MSA {Module} request BaseID {Base}: (absent) → Wait; evaluation started.", module, baseId);
            var token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => EvaluateAsync(module, baseId, token), CancellationToken.None);
        }
        else
        {
            // Lost the race — another request just added the entry; mirror its Wait.
            _log.Information("MSA {Module} request BaseID {Base}: concurrent start → Wait.", module, baseId);
        }

        return "Wait";
    }

    /// <summary>Short human label for a cached response word (for logging state transitions).</summary>
    private static string DescribeState(string response) => response switch
    {
        "Wait" => "evaluating",
        "OK" => "done/OK",
        "NG" => "done/NG",
        _ when response.StartsWith("Error", StringComparison.Ordinal) => "error",
        _ => "unknown",
    };

    private async Task EvaluateAsync(string module, string baseId, CancellationToken ct)
    {
        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

            // PUSH MODEL (CLAUDE.md §5): the PLC sends ONE Request, gets "Wait", and does not poll
            // again — we deliver the result ourselves. So we must not give up when the run's rows are
            // not in the DB yet (they are committed on MsaService's own SaveInterval tick, slightly
            // after the run completes): retry internally until they land, then push, instead of
            // clearing the entry and waiting for a re-request.
            //
            // task A1 — evaluate ONLY the COMPLETE run: the Request can arrive while the last part's
            // telegrams are still in flight / queued, so the first non-zero snapshot may hold only the
            // early parts (this produced a premature 1-part PASS/OK). We therefore wait until the run
            // has SETTLED — the DB row count for this BaseID has not grown for SettleTicks consecutive
            // ticks with the write queue drained (or SettleTicksBusy ticks if the shared queue never
            // empties because another module is running). Until then the PLC keeps its "Wait".
            List<MsaRow> rows = new();
            var stableTicks = 0;
            var lastCount = -1;
            for (var attempt = 1; ; attempt++)
            {
                rows = await GatherAsync(conn, module, baseId, ct).ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    if (attempt >= MaxGatherAttempts)
                    {
                        const string msg = "no measurements received for this run";
                        _evaluations[baseId] = $"Error;{msg}";
                        _log.Warning("MSA {Module} BaseID {Base}: {Msg} after {N} attempts → Error (pushed).",
                            module, baseId, msg, attempt);
                        await _sps.PushMsaResultAsync(module, baseId, $"Error;{msg}", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    _log.Information("MSA {Module} BaseID {Base}: no measurements in DB yet ({Pending} queued); retry {N}/{Max} in {Sec}s (still Wait).",
                        module, baseId, _queue.Count, attempt, MaxGatherAttempts, _flushInterval.TotalSeconds);
                    stableTicks = 0;
                    lastCount = -1;
                    await Task.Delay(_flushInterval, ct).ConfigureAwait(false);
                    continue;
                }

                // Data present — wait for it to settle so an OK can only reflect the WHOLE run (task A1).
                if (rows.Count == lastCount)
                    stableTicks++;
                else
                    stableTicks = 0;
                lastCount = rows.Count;

                var settled = (_queue.IsEmpty && stableTicks >= SettleTicks) || stableTicks >= SettleTicksBusy;
                if (settled)
                    break;

                if (attempt >= MaxGatherAttempts)
                {
                    _log.Warning("MSA {Module} BaseID {Base}: run did not fully settle after {N} attempts; " +
                        "evaluating the {Rows} row(s) present ({Pending} still queued).",
                        module, baseId, attempt, rows.Count, _queue.Count);
                    break;
                }

                _log.Debug("MSA {Module} BaseID {Base}: waiting for the run to settle — {Rows} row(s), stable {Stable}/{Need}, {Pending} queued; tick {N}/{Max} (still Wait).",
                    module, baseId, rows.Count, stableTicks, _queue.IsEmpty ? SettleTicks : SettleTicksBusy, _queue.Count, attempt, MaxGatherAttempts);
                await Task.Delay(_flushInterval, ct).ConfigureAwait(false);
            }

            var msaType = MsaTypeExtensions.FromDbString(rows[0].MsaType);
            var reference = GetReference(module);
            var referenceLoaded = reference is not null;

            // Controllers that produced at least one real OK/NOK judgement (status 0/1). A controller
            // delivering only status 2/−1 "did not evaluate" (task 4) — its LimitSample features are
            // neutralised (neither pass nor fail) and surfaced as a warning.
            var judged = rows.GroupBy(r => r.CameraName)
                .Where(g => g.Any(r => r.ResultStatus is 0 or 1))
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var msaCfg = _config.Config.Msa;
            List<MsaMeasurementResult> results;
            MsaVerdict verdict;
            string verdictReason;
            var notes = new List<string>();
            var legacyReferenceUsed = false;

            if (msaType == MsaType.LimitSample)
            {
                // Per-part evaluation (task A): each DMC is checked against its own reference file.
                (results, verdict, verdictReason, notes, legacyReferenceUsed) =
                    EvaluateLimitSample(rows, module, msaCfg.ReferencePath, judged, reference);
            }
            else if (msaType == MsaType.Msa1)
            {
                // Per-part evaluation with best-match reference assignment (task C).
                (results, verdict, verdictReason, notes, legacyReferenceUsed) =
                    await EvaluateMsa1Async(conn, rows, module, msaCfg.ReferencePath, reference, ct).ConfigureAwait(false);
            }
            else
            {
                // MSA3 is ONE study over all parts (per measurement); keep the per-definition path.
                var toleranceCache = new Dictionary<(int, int), double>();
                results = new List<MsaMeasurementResult>();
                foreach (var group in rows.GroupBy(r => r.DefinitionId))
                {
                    var list = group.ToList();
                    var first = list[0];
                    var tolerance = await GetToleranceAsync(conn, first.CameraId, first.ParameterSet, toleranceCache, ct)
                        .ConfigureAwait(false);
                    results.Add(Evaluate(msaType, list, tolerance, reference, judged.Contains(first.CameraName)));
                }
                (verdict, verdictReason) = MsaEvaluationText.OverallVerdict(msaType, referenceLoaded, results);
            }

            // Cameras that produced no real judgement in this run (task 4).
            var controllerWarnings = rows.Select(r => r.CameraName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => !judged.Contains(c))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{c}: {MsaEvaluationText.CameraDidNotJudge}")
                .ToList();

            if (verdict != MsaVerdict.Pass)
                _log.Warning("MSA {Type} BaseID {Base}: overall {Verdict}{Reason}.", msaType, baseId, verdict,
                    string.IsNullOrEmpty(verdictReason) ? string.Empty : $" — {verdictReason}");
            foreach (var r in results.Where(r => r.Evaluated && !r.Passed))
                _log.Warning("MSA {Type} {Ctrl}/{Name} base={Base}: FAIL — {Reason}",
                    msaType, r.Controller, r.DisplayName, baseId,
                    string.IsNullOrWhiteSpace(r.Reason) ? "(no reason given)" : r.Reason);
            foreach (var w in controllerWarnings)
                _log.Warning("MSA {Type} BaseID {Base}: {Warning}", msaType, baseId, w);
            foreach (var n in notes)
                _log.Information("MSA {Type} BaseID {Base}: {Note}", msaType, baseId, n);

            await StoreResultsAsync(conn, baseId, msaType, results, ct).ConfigureAwait(false);

            // Human-facing run folder: <ReportPath>\<yyyy-MM-dd>\<Module>\<BaseID>\{PDF,RAW,IMG} (task B/D).
            var runAt = BaseId.TryGetTimestamp(baseId, out var dt) ? dt : DateTime.Now;
            var reportDir = MsaResultLayout.EnsureWritableReportDir(
                msaCfg.ReportPath, msaCfg.ReportFallbackPath, msaCfg.ResultPath, module, baseId, runAt, _log);
            var report = BuildReport(baseId, module, msaType, rows, results, verdict, verdictReason,
                controllerWarnings, notes, runAt, msaCfg.ReferencePath, reportDir, legacyReferenceUsed);

            // Per-run measurement summary CSV → [CSV] CSV_MSAPath (Y:), never the config folder (task E).
            ExportCsv(baseId, module, msaType, results);
            // Report PDFs + RAW export + run images into the run folder's subfolders (task B/C).
            // MSA3 is one study report; LimitSample/MSA1 get one PDF pair PER PART (task B4).
            if (msaType == MsaType.Msa3)
                GeneratePdf(report);
            else
                GeneratePerPartPdfs(report, results, msaType);
            await ExportRawDataAsync(conn, baseId, module, msaType,
                reportDir is null ? null : Path.Combine(reportDir, MsaResultLayout.RawSubfolder), ct).ConfigureAwait(false);
            CopyRunImages(baseId, reportDir is null ? null : Path.Combine(reportDir, MsaResultLayout.ImgSubfolder));

            // Map the verdict to the PLC word (never a silent OK): Pass→OK, Fail→NG, Invalid→Error;<reason>.
            var word = verdict switch
            {
                MsaVerdict.Pass => "OK",
                MsaVerdict.Fail => "NG",
                _ => $"Error;{verdictReason}",
            };
            _evaluations[baseId] = word;
            _log.Information("MSA {Type} for BaseID {Base}: {Verdict} ({Count} measurement(s), {Eval} evaluated).",
                msaType, baseId, verdict, results.Count, results.Count(r => r.Evaluated));

            // Notify the UI that a run finished (task A2) — never let a subscriber fault break evaluation.
            try { RunCompleted?.Invoke(new MsaRunCompleted(module, msaType, baseId)); }
            catch (Exception evtEx) { _log.Debug("MSA RunCompleted subscriber threw for {Base}: {Msg}", baseId, evtEx.Message); }

            // Push the result to the PLC on the same open connection — no re-request needed.
            // (The cached word above also lets a late re-Request still answer, as a safety net.)
            await _sps.PushMsaResultAsync(module, baseId, word, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — leave no terminal state cached; a later request re-triggers.
            _evaluations.TryRemove(baseId, out _);
        }
        catch (Exception ex)
        {
            // Genuine fault (DB/reference/etc.) — surface it to the PLC as a terminal Error and log
            // the concrete reason (never a silent Error, CLAUDE.md §5 / Problem 3).
            _evaluations[baseId] = $"Error;{ex.Message}";
            _log.Error(ex, "MSA {Module} evaluation failed for BaseID {Base}: {Reason} → Error.", module, baseId, ex.Message);
            try { await _sps.PushMsaResultAsync(module, baseId, $"Error;{ex.Message}", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception pushEx) { _log.Debug("MSA {Module}: error push failed for {Base}: {Msg}", module, baseId, pushEx.Message); }
        }
    }

    /// <summary>
    /// Evaluate ONE measurement (all rows of one definition = one camera + feature) across all its
    /// parts and loops (CLAUDE.md §7: an MSA is one study per measurement over all parts, loops are
    /// the repetitions — not one MSA per part). Enriches the result with the numbers behind the
    /// verdict (n, mean, σ, reference, tolerance) and a plain-text reason on FAIL (task B).
    /// </summary>
    private MsaMeasurementResult Evaluate(
        MsaType msaType, List<MsaRow> group, double tolerance, MsaReferenceFile? reference, bool controllerJudged)
    {
        var first = group[0];
        var values = group.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList();
        var n = values.Count;
        double? mean = n > 0 ? MsaCalculator.Mean(values) : null;
        double? sd = n >= 2 ? MsaCalculator.SampleStdDev(values) : null;
        double? tol = tolerance > 0 ? tolerance : null;
        var criterion = MsaEvaluationText.Criterion(msaType);

        switch (msaType)
        {
            case MsaType.Msa1:
            {
                var hasRef = reference?.References.ContainsKey(first.DisplayName) ?? false;
                var xm = reference?.References.GetValueOrDefault(first.DisplayName) ?? 0;
                var r = MsaCalculator.Msa1(values, tolerance, xm);
                var (passed, reason) = MsaEvaluationText.Msa1Verdict(n, sd ?? 0, tolerance, r.Cg, r.Cgk, hasRef);
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc,
                    Cg = r.Cg, Cgk = r.Cgk, N = n, Mean = mean, StdDev = sd,
                    ReferenceValue = hasRef ? xm : null, Tolerance = tol,
                    Criterion = criterion, Reason = reason,
                    Evaluated = tolerance > 0 && n >= 2 && (sd ?? 0) > 0, Passed = passed,
                };
            }
            case MsaType.Msa3:
            {
                var partLists = group
                    .GroupBy(r => r.Dmc)
                    .Select(g => g.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList())
                    .ToList();
                var partCount = partLists.Count;
                var dof = partLists.Where(p => p.Count >= 2).Sum(p => p.Count - 1);
                var r = MsaCalculator.Msa3(partLists.Select(p => (IReadOnlyList<double>)p).ToList(), tolerance);
                var (passed, reason) = MsaEvaluationText.Msa3Verdict(partCount, dof, tolerance, r.PctTolerance);
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc,
                    PctTolerance = r.PctTolerance, N = n, Mean = mean, StdDev = sd, Tolerance = tol,
                    Criterion = criterion, Reason = reason,
                    Evaluated = tolerance > 0 && dof > 0, Passed = passed,
                };
            }
            case MsaType.LimitSample:
            {
                var hasRef = reference?.LimitSampleExpected.ContainsKey(first.DisplayName) ?? false;
                var shouldFail = reference?.LimitSampleExpected.GetValueOrDefault(first.DisplayName) ?? false;
                var wasRejected = group.Any(r => r.ResultStatus == 0);

                bool passed, evaluated;
                string reason;
                if (!controllerJudged)
                {
                    // Camera produced no real OK/NOK judgement (only status 2/−1): neither pass nor
                    // fail — surfaced as a controller warning, not counted toward an overall PASS (task 4).
                    passed = true; evaluated = false; reason = MsaEvaluationText.CameraDidNotJudge;
                }
                else
                {
                    (passed, reason) = MsaEvaluationText.LimitSampleVerdict(hasRef, shouldFail, wasRejected);
                    evaluated = hasRef;
                }

                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc,
                    Expected = shouldFail ? "reject" : "accept",
                    Actual = !controllerJudged ? "not evaluated" : (wasRejected ? "rejected" : "accepted"),
                    N = n, Mean = mean, StdDev = sd,
                    Criterion = criterion, Reason = reason,
                    Evaluated = evaluated, ExpectedReject = hasRef && shouldFail, Passed = passed,
                };
            }
            default:
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc, Passed = false,
                    Evaluated = false, Reason = "unknown MSA type",
                };
        }
    }

    /// <summary>
    /// LimitSample evaluation, PER PART (task A): each DMC in the run is checked against its own
    /// reference file <c>&lt;ReferencePath&gt;\&lt;Module&gt;\LimitSamples\&lt;DMC&gt;.json</c>. Every ShouldFail
    /// (prepared error) must be rejected for THAT part; ShouldPass must be accepted. A run DMC with no
    /// reference → the part is INVALID; a taught DMC missing from the run → a note. Falls back to the
    /// legacy module-wide <c>limit_sample_expected</c> (MSA_&lt;module&gt;.json) with a WARNING when no
    /// per-part files exist at all (backward compatibility, task A6).
    /// </summary>
    private (List<MsaMeasurementResult> Results, MsaVerdict Verdict, string Reason, List<string> Notes, bool LegacyUsed)
        EvaluateLimitSample(List<MsaRow> rows, string module, string referenceFolder,
            HashSet<string> judged, MsaReferenceFile? legacy)
    {
        var results = new List<MsaMeasurementResult>();
        var notes = new List<string>();
        var criterion = MsaEvaluationText.Criterion(MsaType.LimitSample);

        // Baugleich modules (M10↔M11, M20↔M21) share references — load the module AND its mirror.
        var taught = LimitSampleReference.LoadAllWithMirror(referenceFolder, module);
        var taughtByDmc = new Dictionary<string, LimitSampleReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in taught)
            taughtByDmc[t.Dmc] = t;

        var legacyMap = legacy?.LimitSampleExpected;   // name → shouldFail (module-wide, old format)
        var useLegacy = taught.Count == 0 && legacyMap is { Count: > 0 };
        if (useLegacy)
            _log.Warning("MSA LimitSample {Module}: no per-part references in {Dir} — using legacy module-wide limit_sample_expected (old format).",
                module, LimitSampleReference.FolderFor(referenceFolder, module));

        var runDmcs = new HashSet<string>(rows.Select(r => r.Dmc), StringComparer.OrdinalIgnoreCase);

        foreach (var dmcGroup in rows.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase))
        {
            var dmc = dmcGroup.Key;
            var defaultCtrl = dmcGroup.First().CameraName;

            Dictionary<string, bool>? expected = null;
            if (taughtByDmc.TryGetValue(dmc, out var partRef))
                expected = partRef.Expected.ToDictionary(
                    kv => kv.Key,
                    kv => string.Equals(kv.Value, LimitSampleReference.ShouldFail, StringComparison.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            else if (useLegacy)
                expected = new Dictionary<string, bool>(legacyMap!, StringComparer.OrdinalIgnoreCase);

            if (expected is null || expected.Count == 0)
            {
                results.Add(new MsaMeasurementResult
                {
                    DisplayName = "(part not referenced)", Controller = defaultCtrl, Dmc = dmc,
                    Evaluated = false, Passed = false, Criterion = criterion, InvalidatesPart = true,
                    Reason = $"part without reference file: {dmc}",
                });
                continue;
            }

            var byFeature = dmcGroup.GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var (feature, shouldFail) in expected)
            {
                byFeature.TryGetValue(feature, out var frows);
                var ctrl = frows is { Count: > 0 } ? frows[0].CameraName : defaultCtrl;

                if (frows is null || frows.Count == 0)
                {
                    results.Add(new MsaMeasurementResult
                    {
                        DisplayName = feature, Controller = ctrl, Dmc = dmc,
                        Expected = shouldFail ? "reject" : "accept", Actual = "no measurement",
                        Evaluated = false, ExpectedReject = shouldFail, Passed = false,
                        Reason = "no measurement for this feature in the run", Criterion = criterion,
                    });
                    continue;
                }

                if (!judged.Contains(ctrl))
                {
                    // Camera produced no OK/NOK (only status 2/−1): neutral, not counted (task 4).
                    results.Add(new MsaMeasurementResult
                    {
                        DisplayName = feature, Controller = ctrl, Dmc = dmc,
                        Expected = shouldFail ? "reject" : "accept", Actual = "not evaluated",
                        Evaluated = false, ExpectedReject = shouldFail, Passed = true,
                        Reason = MsaEvaluationText.CameraDidNotJudge, Criterion = criterion, N = frows.Count,
                    });
                    continue;
                }

                var wasRejected = frows.Any(r => r.ResultStatus == 0);
                // Both directions (task A4): ShouldFail must be rejected, ShouldPass must be accepted.
                var (featurePassed, featureReason) = MsaEvaluationText.LimitSampleFeature(shouldFail, wasRejected);
                results.Add(new MsaMeasurementResult
                {
                    DisplayName = feature, Controller = ctrl, Dmc = dmc,
                    Expected = shouldFail ? "reject" : "accept",
                    Actual = wasRejected ? "rejected" : "accepted",
                    Evaluated = true, ExpectedReject = shouldFail, Passed = featurePassed,
                    Reason = featureReason,
                    Criterion = criterion, N = frows.Count,
                });
            }
        }

        // Taught parts that did not appear in this run → hint (task A3).
        foreach (var t in taught)
            if (!runDmcs.Contains(t.Dmc))
                notes.Add($"taught part {t.Dmc} was not in this run");

        // Overall verdict from the per-part verdicts (task A): any INVALID → Error, any FAIL → NG,
        // else OK. Never a vacuous PASS.
        MsaVerdict verdict;
        string reason;
        if (taught.Count == 0 && !useLegacy)
        {
            verdict = MsaVerdict.Invalid;
            reason = $"no LimitSample reference part found in {LimitSampleReference.FolderFor(referenceFolder, module)}";
        }
        else
        {
            var parts = results.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Dmc: g.Key, Detail: MsaEvaluationText.PartVerdictDetailed(MsaType.LimitSample, g.ToList())))
                .ToList();
            (verdict, reason) = MsaEvaluationText.OverallFromParts(parts.Select(p => (p.Dmc, p.Detail.Verdict)).ToList());

            if (verdict == MsaVerdict.Invalid)
            {
                // Name the concrete cause(s) (task A3), e.g. "Teil ohne Referenzdatei: <DMC>".
                var bad = parts.Where(p => p.Detail.Verdict == MsaVerdict.Invalid)
                    .Select(p => p.Detail.Reason).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(3).ToList();
                if (bad.Count > 0)
                    reason = string.Join("; ", bad);
            }
            else if (verdict == MsaVerdict.Pass && !results.Any(r => r.Evaluated && r.ExpectedReject))
            {
                // Vacuous-PASS guard (task A2): a PASS that verified no prepared error proves nothing.
                verdict = MsaVerdict.Invalid;
                reason = "only good samples in the run, no expected error checked";
            }
        }

        return (results, verdict, reason, notes, useLegacy);
    }

    /// <summary>
    /// Ensure a blank <c>DEMO_&lt;module&gt;.json</c> MSA1 template exists for M10/M11/M20/M21/M50 (task C2):
    /// created from all Result measurement names of the module with empty (0) values and
    /// <c>template:true</c>, so Philipp can copy → rename → fill in. DEMO files are ignored during
    /// evaluation. Best-effort — never throws into the flush loop.
    /// </summary>
    private async Task EnsureDemoTemplatesAsync(CancellationToken ct)
    {
        var refFolder = _config.Config.Msa.ReferencePath;
        if (string.IsNullOrWhiteSpace(refFolder))
            return;

        string[] modules = { "M10", "M11", "M20", "M21", "M50" };
        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            foreach (var module in modules)
            {
                if (File.Exists(Msa1Reference.TemplatePathFor(refFolder, module)))
                    continue;

                const string sql = @"
SELECT DISTINCT md.display_name
FROM measurement_definitions md JOIN cameras c ON c.id = md.camera_id
WHERE c.camera_name LIKE @m AND md.effective_end IS NULL AND md.var_type = 'Result'
ORDER BY md.display_name;";
                var names = new List<string>();
                await using (var cmd = new MySqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@m", module + "%");
                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        names.Add(reader.GetString(0));
                }
                if (names.Count == 0)
                    continue; // definitions not synced for this module yet

                var demo = new Msa1Reference
                {
                    Module = module,
                    Label = $"DEMO template for {module} — copy, rename and fill in the xm values (not evaluated)",
                    CreatedAt = DateTime.Now,
                    Template = true,
                };
                foreach (var n in names)
                    demo.Values[n] = 0.0;

                var written = demo.Save(refFolder, $"{Msa1Reference.TemplatePrefix}{module}.json");
                _log.Information("MSA1 {Module}: created DEMO template {Path} ({Count} measurements).", module, written, names.Count);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "MSA1: failed to ensure DEMO templates.");
        }
    }

    /// <summary>
    /// MSA1 evaluation, PER PART with BEST-MATCH reference assignment (task C). The milled reference
    /// parts have no readable DMC (camera emits fake DMCs), so each part group is matched to the best
    /// <c>&lt;Module&gt;\MSA1\*.json</c> reference by its measured means (<see cref="Msa1Matcher"/>).
    /// With a plausible match: Cg + Cgk (xm from the reference); a feature missing from the matched
    /// reference → n/a (task C3). No plausible match → Cg-only + note (task C5). DEMO_ templates are
    /// ignored. Falls back to the legacy module-wide <c>references</c> block when no MSA1 files exist.
    /// </summary>
    private async Task<(List<MsaMeasurementResult> Results, MsaVerdict Verdict, string Reason, List<string> Notes, bool LegacyUsed)>
        EvaluateMsa1Async(MySqlConnection conn, List<MsaRow> rows, string module, string referenceFolder,
            MsaReferenceFile? legacy, CancellationToken ct)
    {
        var results = new List<MsaMeasurementResult>();
        var notes = new List<string>();
        var criterion = MsaEvaluationText.Criterion(MsaType.Msa1);

        // Tolerance + display name per definition (tolerance is per camera+parameter set, constant across parts).
        var toleranceCache = new Dictionary<(int, int), double>();
        var tolByMeasurement = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var defGroup in rows.GroupBy(r => r.DefinitionId))
        {
            var f = defGroup.First();
            var tol = await GetToleranceAsync(conn, f.CameraId, f.ParameterSet, toleranceCache, ct).ConfigureAwait(false);
            tolByMeasurement.TryAdd(f.DisplayName, tol);
        }

        // Candidate reference parts (non-DEMO); baugleich mirror (M10↔M11, M20↔M21) candidates are
        // included so an MSA1 reference taught on one strand is usable on the other. Legacy block is a fallback.
        var candidates = Msa1Reference.LoadCandidatesWithMirror(referenceFolder, module)
            .Select(r => new Msa1Matcher.Candidate(
                string.IsNullOrWhiteSpace(r.Label) ? Path.GetFileNameWithoutExtension(r.SourceFile) : r.Label,
                Path.GetFileName(r.SourceFile),
                r.Values))
            .ToList();
        var legacyUsed = false;
        if (candidates.Count == 0 && legacy is { References.Count: > 0 })
        {
            candidates.Add(new Msa1Matcher.Candidate($"MSA_{module}.json (legacy)", $"MSA_{module}.json", legacy.References));
            legacyUsed = true;
            _log.Warning("MSA1 {Module}: no MSA1 reference files — using legacy references block (old format).", module);
        }

        foreach (var partGroup in rows.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase))
        {
            var dmc = partGroup.Key;
            var measGroups = partGroup.GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            var partMeans = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var mg in measGroups)
            {
                var vals = mg.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList();
                if (vals.Count > 0)
                    partMeans[mg.Key] = MsaCalculator.Mean(vals);
            }

            var match = Msa1Matcher.Match(partMeans, tolByMeasurement, candidates);
            var refValues = match.Plausible ? match.Best?.Values : null;
            var matchedText = match.Plausible && match.Best is not null ? $"{match.Best.Label} [{match.Best.File}]" : string.Empty;
            double? matchScore = match.Best is not null ? match.Score : null;

            if (candidates.Count > 0 && !match.Plausible)
                notes.Add($"part {dmc}: no reference part assigned (Cg only)");
            if (match.Ambiguous && match.Best is not null)
                notes.Add($"part {dmc}: ambiguous reference match — verify (using {match.Best.Label})");

            foreach (var mg in measGroups)
            {
                var name = mg.Key;
                var ctrl = mg.First().CameraName;
                var defId = mg.First().DefinitionId;
                var vals = mg.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList();
                var n = vals.Count;
                double? mean = n > 0 ? MsaCalculator.Mean(vals) : null;
                double? sd = n >= 2 ? MsaCalculator.SampleStdDev(vals) : null;
                tolByMeasurement.TryGetValue(name, out var tol);
                double? tolN = tol > 0 ? tol : null;

                double? cg = null, cgk = null, xmOut = null;
                bool evaluated, passed;
                string reason;

                if (tol <= 0 || n < 2 || (sd ?? 0) <= 0)
                {
                    evaluated = false; passed = false;
                    reason = tol <= 0 ? MsaEvaluationText.NoTolerance
                        : n < 2 ? $"only n={n} value(s) (need ≥ 2)"
                        : "all values identical (σ = 0)";
                }
                else
                {
                    var sigma = sd!.Value;
                    cg = 0.20 * tol / (6.0 * sigma);
                    if (refValues is not null && refValues.TryGetValue(name, out var xm))
                    {
                        xmOut = xm;
                        cgk = (0.20 * tol - Math.Abs(mean!.Value - xm)) / (6.0 * sigma);
                        evaluated = true;
                        var fails = new List<string>();
                        if (cg < MsaCalculator.CapabilityThreshold) fails.Add($"Cg {cg:0.00} < {MsaCalculator.CapabilityThreshold:0.00}");
                        if (cgk < MsaCalculator.CapabilityThreshold) fails.Add($"Cgk {cgk:0.00} < {MsaCalculator.CapabilityThreshold:0.00}");
                        passed = fails.Count == 0;
                        reason = passed ? string.Empty : string.Join("; ", fails);
                    }
                    else if (refValues is not null)
                    {
                        // Matched a reference, but this feature is not in it (deleted entry) → n/a (task C3).
                        evaluated = false; passed = false;
                        reason = "not in reference part (n/a)";
                    }
                    else
                    {
                        // No plausible reference → Cg only (task C5).
                        evaluated = true;
                        passed = cg >= MsaCalculator.CapabilityThreshold;
                        reason = passed ? "no reference part assigned (Cg only)"
                            : $"Cg {cg:0.00} < {MsaCalculator.CapabilityThreshold:0.00} (Cg only, no reference part)";
                    }
                }

                results.Add(new MsaMeasurementResult
                {
                    DefinitionId = defId, DisplayName = name, Controller = ctrl, Dmc = dmc,
                    Cg = cg, Cgk = cgk, N = n, Mean = mean, StdDev = sd,
                    ReferenceValue = xmOut, Tolerance = tolN,
                    Criterion = criterion, Reason = reason, Evaluated = evaluated, Passed = passed,
                    MatchedReference = matchedText, MatchScore = matchScore,
                });
            }
        }

        var parts = results.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, MsaEvaluationText.PartVerdict(MsaType.Msa1, g.ToList())))
            .ToList();
        var (verdict, reason2) = MsaEvaluationText.OverallFromParts(parts);
        return (results, verdict, reason2, notes, legacyUsed);
    }

    private sealed record MsaRow(
        int DefinitionId, string Dmc, double? Value, int? ResultStatus,
        string MsaType, string? MsaVersion, string DisplayName, int ParameterSet, int CameraId, string CameraName,
        int LoopNumber, DateTime MeasuredAt);

    /// <summary>
    /// Collect every measurement of one MSA run. The run is identified by an EXACT match on
    /// the 14-char <paramref name="baseId"/> (never LIKE/prefix). It is additionally scoped to
    /// the requesting module via <c>controller_name</c> (one run = one module = one msa_type),
    /// so a BaseID can never pull rows from another module even in an edge case (task 3).
    /// </summary>
    private static async Task<List<MsaRow>> GatherAsync(MySqlConnection conn, string module, string baseId, CancellationToken ct)
    {
        const string sql = @"
SELECT m.definition_id, m.dmc, m.measurement_value, m.result_status, m.msa_type, m.msa_version,
       md.display_name, md.parameter_set, md.camera_id, c.camera_name, m.loop_number, m.measured_at
FROM msa_measurements m
JOIN measurement_definitions md ON md.id = m.definition_id
JOIN cameras c ON c.id = md.camera_id
WHERE m.base_id = @b AND m.controller_name LIKE @mod;";

        var rows = new List<MsaRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@b", baseId);
        cmd.Parameters.AddWithValue("@mod", module + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MsaRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.GetDateTime(11)));
        }
        return rows;
    }

    /// <summary>Tolerance = USL − LSL from the latest Min/Max limits for a (camera, parameter set).</summary>
    private static async Task<double> GetToleranceAsync(
        MySqlConnection conn, int cameraId, int parameterSet, Dictionary<(int, int), double> cache, CancellationToken ct)
    {
        var key = (cameraId, parameterSet);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        const string sql = @"
SELECT sd.limit_type, s.limit_value
FROM settings s
JOIN setting_definitions sd ON sd.id = s.definition_id
WHERE s.camera_id = @cam AND sd.parameter_set = @ps
ORDER BY s.recorded_at DESC;";

        double? min = null, max = null;
        await using (var cmd = new MySqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@cam", cameraId);
            cmd.Parameters.AddWithValue("@ps", parameterSet);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var type = reader.GetString(0);
                var value = reader.GetDouble(1);
                if (min is null && type.Equals("Min", StringComparison.OrdinalIgnoreCase)) min = value;
                else if (max is null && type.Equals("Max", StringComparison.OrdinalIgnoreCase)) max = value;
            }
        }

        var tolerance = (min.HasValue && max.HasValue) ? max.Value - min.Value : 0;
        cache[key] = tolerance;
        return tolerance;
    }

    private static async Task StoreResultsAsync(
        MySqlConnection conn, string baseId, MsaType msaType,
        List<MsaMeasurementResult> results, CancellationToken ct)
    {
        // Merge multiple evaluations of the SAME run (task A1): a re-evaluation (e.g. after a restart
        // or a later re-Request) must REPLACE the previous rows for this BaseID, never append stale
        // partial results next to the complete ones. Delete + insert in one transaction so the UI
        // (which groups msa_results by base_id) can never see a mix of two evaluations.
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var del = new MySqlCommand("DELETE FROM msa_results WHERE base_id = @base;", conn) { Transaction = tx })
        {
            del.Parameters.AddWithValue("@base", baseId);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        const string sql = @"
INSERT INTO msa_results
  (controller_name, dmc, base_id, msa_type, msa_version, definition_id, display_name,
   cg_value, cgk_value, pct_tolerance, expected_value, actual_value,
   n_values, mean_value, std_dev, reference_value, tolerance, criterion, reason, evaluated,
   matched_reference, match_score, passed)
VALUES
  (@ctrl, @dmc, @base, @type, @ver, @def, @name,
   @cg, @cgk, @pct, @expected, @actual,
   @n, @mean, @sd, @ref, @tol, @crit, @reason, @eval,
   @mref, @mscore, @passed);";

        foreach (var r in results)
        {
            await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
            // controller/dmc are per result now (a module run spans several cameras, e.g. KF1+KF3).
            cmd.Parameters.AddWithValue("@ctrl", r.Controller);
            cmd.Parameters.AddWithValue("@dmc", r.Dmc);
            cmd.Parameters.AddWithValue("@base", baseId);
            cmd.Parameters.AddWithValue("@type", msaType.ToDbString());
            cmd.Parameters.AddWithValue("@ver", DBNull.Value);
            cmd.Parameters.AddWithValue("@def", r.DefinitionId);
            cmd.Parameters.AddWithValue("@name", r.DisplayName);
            cmd.Parameters.AddWithValue("@cg", (object?)r.Cg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cgk", (object?)r.Cgk ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pct", (object?)r.PctTolerance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@expected", (object?)r.Expected ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@actual", (object?)r.Actual ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", r.N);
            cmd.Parameters.AddWithValue("@mean", (object?)r.Mean ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sd", (object?)r.StdDev ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ref", (object?)r.ReferenceValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tol", (object?)r.Tolerance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@crit", (object?)(string.IsNullOrEmpty(r.Criterion) ? null : r.Criterion) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason", (object?)(string.IsNullOrEmpty(r.Reason) ? null : r.Reason) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@eval", r.Evaluated ? 1 : 0);
            cmd.Parameters.AddWithValue("@mref", (object?)(string.IsNullOrEmpty(r.MatchedReference) ? null : r.MatchedReference) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mscore", (object?)r.MatchScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@passed", r.Passed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private void ExportCsv(string baseId, string module, MsaType msaType, List<MsaMeasurementResult> results)
    {
        var csv = _config.Config.Csv;
        if (!csv.MsaSave)
            return;

        try
        {
            // Task E: the MSA summary CSV goes under [CSV] CSV_MSAPath (e.g. Y:\01_CSV_Evaluation),
            // NOT under [MSA] ReferencePath (which stays pure configuration). The date+BaseID are already
            // in the path, so the writer does not add its own YYYY\MM\DD level.
            if (string.IsNullOrWhiteSpace(csv.MsaPath))
                _log.Warning("MSA CSV: [CSV] CSV_MSAPath is not set — writing to the local fallback. " +
                             "Set it (e.g. Y:\\01_CSV_Evaluation) to collect MSA CSVs centrally.");
            var csvDir = MsaResultLayout.EnsureWritableCsvDir(csv.MsaPath, _config.Config.Msa.ReportFallbackPath, baseId, _log);
            if (csvDir is null)
                return;
            _log.Debug("MSA CSV for BaseID {Base} → {Dir} (the old MSA_References\\MSA_Results tree is no longer written — safe to archive/delete).",
                baseId, csvDir);
            using var writer = new CsvFileWriter(csvDir, int.MaxValue, dateSubfolders: false, _log);
            // Filename label: module + type (CsvFileWriter prepends the DDMMYY_HHMMSS stamp, SOW §5.1.2).
            writer.Configure(
                new[] { "BaseID", "Module", "Controller", "MsaType", "DisplayName", "n", "Mean", "StdDev",
                        "Reference", "Tolerance", "Cg", "Cgk", "PctTolerance", "Criterion", "Passed", "Reason" },
                $"MSA_{module}_{msaType.ToDbString()}");

            foreach (var r in results)
            {
                writer.WriteRow(new[]
                {
                    baseId, module, r.Controller, msaType.ToDbString(), r.DisplayName,
                    r.N.ToString(CultureInfo.InvariantCulture),
                    Num(r.Mean), Num(r.StdDev), Num(r.ReferenceValue), Num(r.Tolerance),
                    Num(r.Cg), Num(r.Cgk), Num(r.PctTolerance),
                    r.Criterion, r.Passed ? "1" : "0", r.Reason,
                });
            }
            writer.Flush();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to write MSA CSV for BaseID {Base}.", baseId);
        }
    }

    private static string? Num(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Raw-data export for the customer's Minitab analysis (task C): one row per measured value in
    /// long format — Controller | BaseID | Loop | DMC | Measurement | Value | Status | Timestamp —
    /// covering every camera, part, loop and measurement of the run. CLAUDE.md §15 forbids Excel
    /// libraries, so this is a ';'-separated UTF-8 (BOM) CSV that opens directly in Excel and imports
    /// 1:1 into Minitab. Written next to the PDF reports (task C2/D). Best-effort — never fails the run.
    /// </summary>
    private async Task ExportRawDataAsync(
        MySqlConnection conn, string baseId, string module, MsaType msaType, string? reportDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportDir))
        {
            _log.Warning("MSA raw-data export skipped for BaseID {Base}: no writable report directory.", baseId);
            return;
        }

        try
        {
            const string sql = @"
SELECT m.controller_name, m.loop_number, m.dmc, md.display_name, m.measurement_value, m.result_status, m.measured_at
FROM msa_measurements m
JOIN measurement_definitions md ON md.id = m.definition_id
WHERE m.base_id = @b AND m.controller_name LIKE @mod
ORDER BY m.controller_name, m.dmc, m.loop_number, md.display_name;";

            // The BOM is written by UTF8Encoding(true) at save time so Excel picks the right encoding.
            var sb = new System.Text.StringBuilder();
            sb.Append("Controller;BaseID;Loop;DMC;Measurement;Value;Status;Timestamp\n");

            var rowCount = 0;
            await using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@b", baseId);
                cmd.Parameters.AddWithValue("@mod", module + "%");
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var controller = reader.GetString(0);
                    var loop = reader.GetInt32(1);
                    var dmc = reader.GetString(2);
                    var name = reader.GetString(3);
                    var value = reader.IsDBNull(4) ? string.Empty : reader.GetDouble(4).ToString("0.####", CultureInfo.InvariantCulture);
                    var status = reader.IsDBNull(5) ? string.Empty : reader.GetInt32(5).ToString(CultureInfo.InvariantCulture);
                    var at = reader.GetDateTime(6).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    sb.Append(Csv(controller)).Append(';')
                      .Append(Csv(baseId)).Append(';')
                      .Append(loop.ToString(CultureInfo.InvariantCulture)).Append(';')
                      .Append(Csv(dmc)).Append(';')
                      .Append(Csv(name)).Append(';')
                      .Append(value).Append(';')
                      .Append(status).Append(';')
                      .Append(at).Append('\n');
                    rowCount++;
                }
            }

            Directory.CreateDirectory(reportDir); // the RAW\ subfolder may not exist yet
            var file = Path.Combine(reportDir, $"{module}_{msaType.ToDbString()}_{FileNaming.Stamp(DateTime.Now)}_RawData.csv");
            await File.WriteAllTextAsync(file, sb.ToString(), new System.Text.UTF8Encoding(true), ct).ConfigureAwait(false);
            _log.Information("MSA raw-data export written for BaseID {Base}: {Rows} row(s) → {File}", baseId, rowCount, file);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to write MSA raw-data export for BaseID {Base}.", baseId);
        }
    }

    /// <summary>Quote a CSV field for the ';'-separated raw export (escapes ';', quotes, newlines).</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// COPY this run's images from the GoldenSample NAS input folder into the run's IMG folder
    /// (task C). Copies (never moves) so the NAS originals stay untouched. A run's images are those
    /// whose filename Field 1 starts with the 14-char BaseID (the loop counter + padding follow).
    /// Missing/unmatched images are NOT a run error — only a log note ("n found / m copied").
    /// </summary>
    private void CopyRunImages(string baseId, string? imgDir)
    {
        if (string.IsNullOrWhiteSpace(imgDir))
            return;

        var goldenSample = _config.Config.Nas.HighResGoldenSamplePath;
        var src = ImageFileName.SortedRoot(goldenSample);
        if (src is null || !Directory.Exists(src))
        {
            _log.Information("MSA images for BaseID {Base}: GoldenSample source '{Src}' not available — 0 copied.",
                baseId, goldenSample);
            return;
        }

        var found = 0;
        var copied = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (!ImageFileName.MatchesBaseId(Path.GetFileName(file), baseId))
                    continue;
                found++;
                try
                {
                    Directory.CreateDirectory(imgDir);
                    File.Copy(file, Path.Combine(imgDir, Path.GetFileName(file)), overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    _log.Debug("Could not copy MSA image {File}: {Message}", file, ex.Message);
                }
            }

            _log.Information("MSA images for BaseID {Base}: {Found} found, {Copied} copied into {Dir}.",
                baseId, found, copied, imgDir);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to copy run images for BaseID {Base}.", baseId);
        }
    }

    /// <summary>Generate the two MSA PDF reports (SOW §3.2.1). Best-effort: never fails the evaluation.</summary>
    private void GeneratePdf(MsaReportData report)
    {
        try
        {
            _pdf.Generate(report);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to generate MSA PDF report for BaseID {Base}.", report.BaseId);
        }
    }

    /// <summary>Build the PDF report model from a freshly-evaluated run, incl. the head context
    /// (parts/loops/time range/reference file) and per-measurement detail (task B).</summary>
    private MsaReportData BuildReport(
        string baseId, string module, MsaType msaType, List<MsaRow> rows,
        List<MsaMeasurementResult> results, MsaVerdict verdict, string verdictReason,
        IReadOnlyList<string> controllerWarnings, IReadOnlyList<string> notes,
        DateTime runAt, string referencePath, string? outputDir, bool legacyReferenceUsed)
    {
        // task A2 — do NOT hard-wire the legacy MSA_<module>.json here (it made the head show
        // "MSA_M50.json (NOT FOUND)" even when per-part references were used). LimitSample resolves
        // its per-part file in PdfReportService; only when the legacy fallback was really used do we
        // carry the legacy path so the head can show a truthful "Legacy fallback" line.
        var referenceFile = legacyReferenceUsed ? MsaReferenceLoader.ReferenceFilePath(referencePath, module) : string.Empty;
        DateTime? referenceModified = !string.IsNullOrEmpty(referenceFile) && File.Exists(referenceFile)
            ? File.GetLastWriteTime(referenceFile) : null;

        return new MsaReportData
        {
            Module = module,
            TestType = msaType.ToDbString(),
            // A module run may span several cameras (e.g. KF1+KF3); list the distinct ones.
            Controller = string.Join(", ", results.Select(r => r.Controller).Distinct()),
            BaseId = baseId,
            RunAt = runAt,
            OverallPass = verdict == MsaVerdict.Pass,
            Verdict = verdict,
            VerdictReason = verdictReason,
            ControllerWarnings = controllerWarnings,
            Notes = notes,
            OutputDirectory = outputDir,
            PartCount = rows.Select(r => r.Dmc).Distinct().Count(),
            LoopCount = rows.Select(r => r.LoopNumber).Distinct().Count(),
            FromTime = rows.Count > 0 ? rows.Min(r => r.MeasuredAt) : runAt,
            ToTime = rows.Count > 0 ? rows.Max(r => r.MeasuredAt) : runAt,
            Criterion = MsaEvaluationText.Criterion(msaType),
            ReferenceFile = referenceFile,
            ReferenceFileModified = referenceModified,
            LegacyReferenceUsed = legacyReferenceUsed,
            Rows = results.Select(r => ToReportRow(r, msaType)).ToList(),
        };
    }

    /// <summary>Map one evaluated measurement to a report row (shared by run-level and per-part reports).</summary>
    private static MsaReportRow ToReportRow(MsaMeasurementResult r, MsaType msaType) => new()
    {
        Controller = r.Controller,
        Dmc = msaType == MsaType.Msa3 ? string.Empty : r.Dmc,
        Measurement = r.DisplayName,
        N = r.N,
        Mean = r.Mean,
        StdDev = r.StdDev,
        Reference = r.ReferenceValue,
        Tolerance = r.Tolerance,
        Expected = r.Expected ?? string.Empty,
        Actual = r.Actual ?? FormatActual(r),
        Metric = msaType switch
        {
            MsaType.Msa1 => $"Cg {FmtMetric(r.Cg)} / Cgk {FmtMetric(r.Cgk)}",
            MsaType.Msa3 => $"%P/T {FmtMetric(r.PctTolerance)}",
            _ => string.Empty,
        },
        Criterion = r.Criterion,
        Reason = r.Reason,
        Evaluated = r.Evaluated,
        MatchedReference = r.MatchedReference,
        Passed = r.Passed,
    };

    /// <summary>
    /// Per-part PDF reports (task B4) for LimitSample/MSA1: one AllResults/FailuresOnly pair per DMC,
    /// the file name carries BaseID AND DMC. Head/verdict are the PART's. Best-effort.
    /// </summary>
    private void GeneratePerPartPdfs(MsaReportData runReport, List<MsaMeasurementResult> results, MsaType msaType)
    {
        foreach (var g in results.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase))
        {
            var partRows = g.ToList();
            var (verdict, reason) = MsaEvaluationText.PartVerdictDetailed(msaType, partRows);
            var matched = partRows.Select(r => r.MatchedReference).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;

            var partReport = new MsaReportData
            {
                Module = runReport.Module,
                TestType = runReport.TestType,
                Controller = string.Join(", ", partRows.Select(r => r.Controller).Distinct()),
                BaseId = runReport.BaseId,
                Dmc = g.Key,
                RunAt = runReport.RunAt,
                OverallPass = verdict == MsaVerdict.Pass,
                Verdict = verdict,
                VerdictReason = string.IsNullOrEmpty(matched) ? reason : $"{reason}{(reason.Length > 0 ? "  ·  " : string.Empty)}reference: {matched}",
                Notes = runReport.Notes.Where(n => n.Contains(g.Key, StringComparison.OrdinalIgnoreCase)).ToList(),
                OutputDirectory = runReport.OutputDirectory,
                Criterion = runReport.Criterion,
                ReferenceFile = runReport.ReferenceFile,
                ReferenceFileModified = runReport.ReferenceFileModified,
                LegacyReferenceUsed = runReport.LegacyReferenceUsed,
                PartCount = 1,
                LoopCount = runReport.LoopCount,
                FromTime = runReport.FromTime,
                ToTime = runReport.ToTime,
                Rows = partRows.Select(r => ToReportRow(r, msaType)).ToList(),
            };
            GeneratePdf(partReport);
        }
    }

    private static string FormatActual(MsaMeasurementResult r) =>
        r.Cg?.ToString("0.###", CultureInfo.InvariantCulture)
        ?? r.PctTolerance?.ToString("0.###", CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static string FmtMetric(double? v) =>
        v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    private MsaReferenceFile? GetReference(string module) =>
        _references.GetOrAdd(module, m => _referenceLoader.Load(_config.Config.Msa.ReferencePath, m));
}
