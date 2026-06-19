using System.Collections.Concurrent;
using System.Globalization;
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
        ILogService log)
    {
        _cameras = cameras;
        _database = database;
        _cache = cache;
        _sps = sps;
        _config = config;
        _referenceLoader = referenceLoader;
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

        var dmc = telegram.Serial1;       // DMC of the test part
        var baseId = telegram.Serial2;    // BaseID
        if (string.IsNullOrWhiteSpace(dmc) || string.IsNullOrWhiteSpace(baseId))
        {
            _log.Debug("{Camera}: MSA telegram missing DMC/BaseID; skipped.", telegram.ControllerName);
            return;
        }

        var msaType = MsaTypeExtensions.FromMode(telegram.Mode);
        var loop = BaseId.TryParse(baseId) is { } b ? b.Loop1 * 100 + b.Loop2 * 10 + b.Loop3 : 0;
        var measuredAt = DateTime.Now;

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
            return;

        var rows = new List<PendingMsaMeasurement>();
        while (rows.Count < MaxItemsPerFlush && _queue.TryDequeue(out var item))
            rows.Add(item);

        if (rows.Count == 0)
            return;

        await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (!_cache.TryGet(row.ControllerName, row.VariableName, out var definitionId))
                continue;

            const string sql = @"
INSERT INTO msa_measurements
  (dmc, base_id, loop_number, controller_name, definition_id, measurement_value, measurement_string, result_status, msa_type, msa_version, measured_at)
VALUES
  (@dmc, @base, @loop, @ctrl, @def, @val, @str, @res, @type, @ver, @at);";

            await using var cmd = new MySqlCommand(sql, conn);
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
        }

        _log.Debug("Stored {Count} MSA measurement(s); {Pending} pending.", rows.Count, _queue.Count);
    }

    // --- Evaluation: SPS handler (sync, poll model) ---

    private string HandleMsaRequest(string module, string baseId)
    {
        if (string.IsNullOrWhiteSpace(baseId))
            return "Error;missing BaseID";

        // Already evaluated (or in progress)?
        if (_evaluations.TryGetValue(baseId, out var response))
            return response;

        // First request: start the evaluation and answer Wait.
        if (_evaluations.TryAdd(baseId, "Wait"))
        {
            var token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => EvaluateAsync(module, baseId, token), CancellationToken.None);
        }

        return "Wait";
    }

    private async Task EvaluateAsync(string module, string baseId, CancellationToken ct)
    {
        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var rows = await GatherAsync(conn, baseId, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                _evaluations[baseId] = "Error;no MSA data for BaseID";
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
            await StoreResultsAsync(conn, baseId, msaType, rows[0], results, ct).ConfigureAwait(false);
            ExportCsv(baseId, module, msaType, rows[0], results);

            _evaluations[baseId] = passed ? "OK" : "NG";
            _log.Information("MSA {Type} for BaseID {Base}: {Verdict} ({Count} measurements).",
                msaType, baseId, passed ? "OK" : "NG", results.Count);
        }
        catch (Exception ex)
        {
            _evaluations[baseId] = $"Error;{ex.Message}";
            _log.Error(ex, "MSA evaluation failed for BaseID {Base}.", baseId);
        }
    }

    private MsaMeasurementResult Evaluate(
        MsaType msaType, List<MsaRow> group, double tolerance, MsaReferenceFile? reference)
    {
        var first = group[0];

        switch (msaType)
        {
            case MsaType.Msa1:
            {
                var values = group.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList();
                var xm = reference?.References.GetValueOrDefault(first.DisplayName) ?? 0;
                var r = MsaCalculator.Msa1(values, tolerance, xm);
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    Cg = r.Cg, Cgk = r.Cgk, Passed = r.Passed,
                };
            }
            case MsaType.Msa3:
            {
                var parts = group
                    .GroupBy(r => r.Dmc)
                    .Select(g => (IReadOnlyList<double>)g.Where(r => r.Value.HasValue).Select(r => r.Value!.Value).ToList())
                    .ToList();
                var r = MsaCalculator.Msa3(parts, tolerance);
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName,
                    PctTolerance = r.PctTolerance, Passed = r.Passed,
                };
            }
            case MsaType.LimitSample:
            {
                var shouldFail = reference?.LimitSampleExpected.GetValueOrDefault(first.DisplayName) ?? false;
                var wasRejected = group.Any(r => r.ResultStatus == 0);
                var passed = !shouldFail || wasRejected;
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName, Passed = passed,
                };
            }
            default:
                return new MsaMeasurementResult
                {
                    DefinitionId = first.DefinitionId, DisplayName = first.DisplayName, Passed = false,
                };
        }
    }

    private sealed record MsaRow(
        int DefinitionId, string Dmc, double? Value, int? ResultStatus,
        string MsaType, string? MsaVersion, string DisplayName, int ParameterSet, int CameraId, string CameraName);

    private static async Task<List<MsaRow>> GatherAsync(MySqlConnection conn, string baseId, CancellationToken ct)
    {
        const string sql = @"
SELECT m.definition_id, m.dmc, m.measurement_value, m.result_status, m.msa_type, m.msa_version,
       md.display_name, md.parameter_set, md.camera_id, c.camera_name
FROM msa_measurements m
JOIN measurement_definitions md ON md.id = m.definition_id
JOIN cameras c ON c.id = md.camera_id
WHERE m.base_id = @b;";

        var rows = new List<MsaRow>();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@b", baseId);
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
                reader.GetString(9)));
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
        MySqlConnection conn, string baseId, MsaType msaType, MsaRow sample,
        List<MsaMeasurementResult> results, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO msa_results
  (controller_name, dmc, base_id, msa_type, msa_version, definition_id, display_name, cg_value, cgk_value, pct_tolerance, passed)
VALUES
  (@ctrl, @dmc, @base, @type, @ver, @def, @name, @cg, @cgk, @pct, @passed);";

        foreach (var r in results)
        {
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ctrl", sample.CameraName);
            cmd.Parameters.AddWithValue("@dmc", sample.Dmc);
            cmd.Parameters.AddWithValue("@base", baseId);
            cmd.Parameters.AddWithValue("@type", msaType.ToDbString());
            cmd.Parameters.AddWithValue("@ver", (object?)sample.MsaVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@def", r.DefinitionId);
            cmd.Parameters.AddWithValue("@name", r.DisplayName);
            cmd.Parameters.AddWithValue("@cg", (object?)r.Cg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cgk", (object?)r.Cgk ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pct", (object?)r.PctTolerance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@passed", r.Passed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private void ExportCsv(string baseId, string module, MsaType msaType, MsaRow sample, List<MsaMeasurementResult> results)
    {
        var csv = _config.Config.Csv;
        if (!csv.MsaSave || string.IsNullOrWhiteSpace(csv.MsaPath))
            return;

        try
        {
            using var writer = new CsvFileWriter(csv.MsaPath, int.MaxValue, dateSubfolders: true, _log);
            writer.Configure(
                new[] { "BaseID", "Module", "Controller", "MsaType", "DMC", "DisplayName", "Cg", "Cgk", "PctTolerance", "Passed" },
                $"MSA_{module}_{baseId}");

            foreach (var r in results)
            {
                writer.WriteRow(new[]
                {
                    baseId, module, sample.CameraName, msaType.ToDbString(), sample.Dmc, r.DisplayName,
                    r.Cg?.ToString("0.###", CultureInfo.InvariantCulture),
                    r.Cgk?.ToString("0.###", CultureInfo.InvariantCulture),
                    r.PctTolerance?.ToString("0.###", CultureInfo.InvariantCulture),
                    r.Passed ? "1" : "0",
                });
            }
            writer.Flush();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to write MSA CSV for BaseID {Base}.", baseId);
        }
    }

    private MsaReferenceFile? GetReference(string module) =>
        _references.GetOrAdd(module, m => _referenceLoader.Load(_config.Config.Msa.ReferencePath, m));
}
