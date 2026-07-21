using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

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
       tolerance, criterion, reason
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
            var rows = await GatherAsync(conn, module, baseId, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                // No rows YET — the run's measurements are almost certainly still in the flush queue
                // (they are committed on the SaveInterval tick, not synchronously with the run). This
                // is NOT an error: clear the entry so the next Request re-triggers and again answers
                // "Wait" until the data lands (idempotent poll, CLAUDE.md §5 / Problem 3). Only a
                // genuine exception below is cached as a terminal "Error".
                _evaluations.TryRemove(baseId, out _);
                _log.Information("MSA {Module} BaseID {Base}: no measurements in DB yet ({Pending} still queued); will retry on next request (Wait).",
                    module, baseId, _queue.Count);
                return;
            }

            var msaType = MsaTypeExtensions.FromDbString(rows[0].MsaType);
            var reference = GetReference(module);
            var toleranceCache = new Dictionary<(int, int), double>();
            var results = new List<MsaMeasurementResult>();

            foreach (var group in rows.GroupBy(r => r.DefinitionId))
            {
                var first = group.First();
                var tolerance = await GetToleranceAsync(conn, first.CameraId, first.ParameterSet, toleranceCache, ct)
                    .ConfigureAwait(false);

                results.Add(Evaluate(msaType, group.ToList(), tolerance, reference));
            }

            var passed = results.Count > 0 && results.All(r => r.Passed);

            // Never a silent 0/FAIL (task B2): log every FAIL/degenerate measurement with its reason.
            foreach (var r in results.Where(r => !r.Passed))
                _log.Warning("MSA {Type} {Ctrl}/{Name} base={Base}: FAIL — {Reason}",
                    msaType, r.Controller, r.DisplayName, baseId,
                    string.IsNullOrWhiteSpace(r.Reason) ? "(no reason given)" : r.Reason);

            await StoreResultsAsync(conn, baseId, msaType, results, ct).ConfigureAwait(false);

            // Human-facing output folder: <ReportPath>\<Module>\<yyyy-MM-dd> with network fallback (task D).
            var msa = _config.Config.Msa;
            var runAt = BaseId.TryGetTimestamp(baseId, out var dt) ? dt : DateTime.Now;
            var reportDir = MsaResultLayout.EnsureWritableReportDir(
                msa.ReportPath, msa.ReportFallbackPath, msa.ResultPath, module, runAt, _log);
            var report = BuildReport(baseId, module, msaType, rows, results, passed, runAt, msa.ReferencePath, reportDir);

            // Existing per-run collection (measurement summary CSV + images) stays under ResultPath.
            ExportCsv(baseId, module, msaType, results);
            MoveRunImages(baseId);
            // Reports + raw-data export (Minitab) into the report folder (task B/C).
            GeneratePdf(report);
            await ExportRawDataAsync(conn, baseId, module, msaType, reportDir, ct).ConfigureAwait(false);

            _evaluations[baseId] = passed ? "OK" : "NG";
            _log.Information("MSA {Type} for BaseID {Base}: {Verdict} ({Count} measurements).",
                msaType, baseId, passed ? "OK" : "NG", results.Count);
        }
        catch (Exception ex)
        {
            // Genuine fault (DB/reference/etc.) — surface it to the PLC as a terminal Error and log
            // the concrete reason (never a silent Error, CLAUDE.md §5 / Problem 3).
            _evaluations[baseId] = $"Error;{ex.Message}";
            _log.Error(ex, "MSA {Module} evaluation failed for BaseID {Base}: {Reason} → Error.", module, baseId, ex.Message);
        }
    }

    /// <summary>
    /// Evaluate ONE measurement (all rows of one definition = one camera + feature) across all its
    /// parts and loops (CLAUDE.md §7: an MSA is one study per measurement over all parts, loops are
    /// the repetitions — not one MSA per part). Enriches the result with the numbers behind the
    /// verdict (n, mean, σ, reference, tolerance) and a plain-text reason on FAIL (task B).
    /// </summary>
    private MsaMeasurementResult Evaluate(
        MsaType msaType, List<MsaRow> group, double tolerance, MsaReferenceFile? reference)
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
                    Criterion = criterion, Reason = reason, Passed = passed,
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
                    Criterion = criterion, Reason = reason, Passed = passed,
                };
            }
            case MsaType.LimitSample:
            {
                var hasRef = reference?.LimitSampleExpected.ContainsKey(first.DisplayName) ?? false;
                var shouldFail = reference?.LimitSampleExpected.GetValueOrDefault(first.DisplayName) ?? false;
                var wasRejected = group.Any(r => r.ResultStatus == 0);
                var (passed, reason) = MsaEvaluationText.LimitSampleVerdict(hasRef, shouldFail, wasRejected);
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc,
                    Expected = shouldFail ? "reject" : "accept",
                    Actual = wasRejected ? "rejected" : "accepted",
                    N = n, Mean = mean, StdDev = sd,
                    Criterion = criterion, Reason = reason, Passed = passed,
                };
            }
            default:
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Controller = first.CameraName, Dmc = first.Dmc, Passed = false,
                    Reason = "unknown MSA type",
                };
        }
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
        const string sql = @"
INSERT INTO msa_results
  (controller_name, dmc, base_id, msa_type, msa_version, definition_id, display_name,
   cg_value, cgk_value, pct_tolerance, expected_value, actual_value,
   n_values, mean_value, std_dev, reference_value, tolerance, criterion, reason, passed)
VALUES
  (@ctrl, @dmc, @base, @type, @ver, @def, @name,
   @cg, @cgk, @pct, @expected, @actual,
   @n, @mean, @sd, @ref, @tol, @crit, @reason, @passed);";

        foreach (var r in results)
        {
            await using var cmd = new MySqlCommand(sql, conn);
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
            cmd.Parameters.AddWithValue("@passed", r.Passed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private void ExportCsv(string baseId, string module, MsaType msaType, List<MsaMeasurementResult> results)
    {
        var csv = _config.Config.Csv;
        if (!csv.MsaSave)
            return;

        try
        {
            // The MSA summary CSV goes into the run's CSV subfolder (the date is already in the path,
            // so the writer does not add its own YYYY\MM\DD level).
            var msa = _config.Config.Msa;
            var csvDir = MsaResultLayout.CsvDir(msa.ResultPath, msa.ReferencePath, baseId);
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
    /// Move every image of this run out of the GoldenSample input folder into the run's IMG
    /// subfolder (<c>&lt;ResultPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\IMG</c>). A run's images are those whose
    /// filename Field 1 starts with the 14-char BaseID (the loop counter + zero padding follow).
    /// Best-effort: never fails the evaluation.
    /// </summary>
    private void MoveRunImages(string baseId)
    {
        var src = ImageFileName.SortedRoot(_config.Config.Nas.HighResGoldenSamplePath);
        if (src is null || !Directory.Exists(src))
            return;

        try
        {
            var msa = _config.Config.Msa;
            var imgDir = MsaResultLayout.ImgDir(msa.ResultPath, msa.ReferencePath, baseId);
            var moved = 0;

            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (!ImageFileName.MatchesBaseId(Path.GetFileName(file), baseId))
                    continue;
                try
                {
                    Directory.CreateDirectory(imgDir);
                    File.Move(file, Path.Combine(imgDir, Path.GetFileName(file)), overwrite: true);
                    moved++;
                }
                catch (Exception ex)
                {
                    _log.Debug("Could not move MSA image {File}: {Message}", file, ex.Message);
                }
            }

            if (moved > 0)
                _log.Information("Moved {Count} run image(s) for BaseID {Base} into {Dir}.", moved, baseId, imgDir);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to move run images for BaseID {Base}.", baseId);
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
        List<MsaMeasurementResult> results, bool passed, DateTime runAt, string referencePath, string? outputDir)
    {
        var referenceFile = MsaReferenceLoader.ReferenceFilePath(referencePath, module);
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
            OverallPass = passed,
            OutputDirectory = outputDir,
            PartCount = rows.Select(r => r.Dmc).Distinct().Count(),
            LoopCount = rows.Select(r => r.LoopNumber).Distinct().Count(),
            FromTime = rows.Count > 0 ? rows.Min(r => r.MeasuredAt) : runAt,
            ToTime = rows.Count > 0 ? rows.Max(r => r.MeasuredAt) : runAt,
            Criterion = MsaEvaluationText.Criterion(msaType),
            ReferenceFile = referenceFile,
            ReferenceFileModified = referenceModified,
            Rows = results.Select(r => new MsaReportRow
            {
                Controller = r.Controller,
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
                Passed = r.Passed,
            }).ToList(),
        };
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
