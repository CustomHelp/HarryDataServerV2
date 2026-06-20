using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the database startup logic of CLAUDE.md section 8 and keeps the
/// <c>cameras</c>, <c>measurement_definitions</c> and <c>setting_definitions</c>
/// tables in sync with Harry.ini and the JSON templates.
/// </summary>
public sealed class MySqlDatabaseService : IDatabaseService
{
    // Exponential backoff for the initial connection: 3s, 6s, 12s, ... capped at 60s.
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    // Steady-state connectivity heartbeat interval (after the DB is Ready).
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly IConfigService _config;
    private readonly MySqlRepository _repo;
    private readonly PartitionManager _partitions;
    private readonly JsonTemplateLoader _templates;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;

    private DatabaseStatus _status = DatabaseStatus.NotStarted;

    public MySqlDatabaseService(
        IConfigService config,
        MySqlRepository repo,
        PartitionManager partitions,
        JsonTemplateLoader templates,
        ISystemHealth health,
        ILogService log)
    {
        _config = config;
        _repo = repo;
        _partitions = partitions;
        _templates = templates;
        _health = health;
        _log = log;
    }

    public DatabaseStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public event Action<DatabaseStatus>? StatusChanged;

    public Task<MySqlConnection> OpenConnectionAsync(CancellationToken ct = default) => _repo.OpenAsync(ct);

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            Status = DatabaseStatus.Connecting;
            await WaitForServerAsync(ct).ConfigureAwait(false);

            Status = DatabaseStatus.Initializing;
            await _repo.InitializeSchemaAsync(ct).ConfigureAwait(false);
            await _partitions.EnsurePartitionsAsync(ct).ConfigureAwait(false);

            var cameraIds = await SyncCamerasAsync(ct).ConfigureAwait(false);
            await SyncDefinitionsAsync(cameraIds, ct).ConfigureAwait(false);

            Status = DatabaseStatus.Ready;
            _health.Clear(HealthSources.Database);
            _log.Information("Database subsystem ready.");

            // Keep watching the connection so a steady-state outage (and its recovery)
            // is detected and reflected in health regardless of pipeline traffic.
            await MonitorConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Database startup cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Status = DatabaseStatus.Failed;
            _health.Report(HealthSources.Database, HealthSeverity.Error,
                $"Database startup failed: {ex.Message}");
            _log.Error(ex, "Database startup failed.");
        }
    }

    /// <summary>
    /// Background heartbeat that runs once the DB is Ready: pings the server every
    /// <see cref="HeartbeatInterval"/> and owns the <see cref="HealthSources.Database"/>
    /// fault. This makes a mid-operation outage <b>and its recovery</b> self-healing —
    /// the KeepAlive channel returns to OK on its own when MySQL comes back, even with
    /// no production traffic to drive a successful flush.
    /// </summary>
    private async Task MonitorConnectionAsync(CancellationToken ct)
    {
        var wasReachable = true;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            bool reachable;
            try { reachable = await _repo.CanConnectAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { reachable = false; }

            if (reachable)
            {
                _health.Clear(HealthSources.Database);
                if (!wasReachable)
                    _log.Information("Database connection restored.");
            }
            else
            {
                _health.Report(HealthSources.Database, HealthSeverity.Error,
                    "Database connection lost; reconnecting");
                if (wasReachable)
                    _log.Warning("Database connection lost; monitoring for recovery.");
            }

            wasReachable = reachable;
        }
    }

    private async Task WaitForServerAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;
        while (!await _repo.CanConnectAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            _health.Report(HealthSources.Database, HealthSeverity.Error,
                $"MySQL server not reachable; retrying in {backoff.TotalSeconds:0}s");
            _log.Warning("MySQL server not reachable; retrying in {Seconds:0}s.", backoff.TotalSeconds);
            await Task.Delay(backoff, ct).ConfigureAwait(false);

            var next = TimeSpan.FromTicks(backoff.Ticks * 2);
            backoff = next > MaxBackoff ? MaxBackoff : next;
        }

        _log.Information("MySQL server reachable.");
    }

    /// <summary>
    /// Upsert one row per configured camera into the <c>cameras</c> table and
    /// return a name → id map for definition syncing.
    /// </summary>
    private async Task<Dictionary<string, int>> SyncCamerasAsync(CancellationToken ct)
    {
        await using var conn = await _repo.OpenAsync(ct).ConfigureAwait(false);

        const string upsert = @"
INSERT INTO cameras (camera_name, module, ip_address, port, active)
VALUES (@name, @module, @ip, @port, 1)
ON DUPLICATE KEY UPDATE
  module = VALUES(module),
  ip_address = VALUES(ip_address),
  port = VALUES(port),
  active = 1;";

        foreach (var cam in _config.Config.Cameras)
        {
            await using var cmd = new MySqlCommand(upsert, conn);
            cmd.Parameters.AddWithValue("@name", cam.CameraName);
            cmd.Parameters.AddWithValue("@module", cam.Module);
            cmd.Parameters.AddWithValue("@ip", cam.Ip);
            cmd.Parameters.AddWithValue("@port", cam.Port);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new MySqlCommand("SELECT id, camera_name FROM cameras;", conn))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                map[reader.GetString(1)] = reader.GetInt32(0);
        }

        _log.Information("Synced {Count} camera(s) to the database.", _config.Config.Cameras.Count);
        return map;
    }

    private async Task SyncDefinitionsAsync(IReadOnlyDictionary<string, int> cameraIds, CancellationToken ct)
    {
        var templates = _templates.LoadAll(_config.Config.Cameras);
        var today = DateOnly.FromDateTime(DateTime.Now);

        await using var conn = await _repo.OpenAsync(ct).ConfigureAwait(false);

        foreach (var cam in _config.Config.Cameras)
        {
            if (!cameraIds.TryGetValue(cam.CameraName, out var cameraId))
            {
                _log.Warning("No camera id for {Camera}; skipping definition sync.", cam.CameraName);
                continue;
            }

            if (!templates.TryGetValue(cam.CameraName, out var camTemplates))
                continue;

            if (camTemplates.Result is not null)
                await SyncMeasurementDefinitionsAsync(conn, cameraId, camTemplates.Result, today, ct)
                    .ConfigureAwait(false);

            if (camTemplates.Settings is not null)
                await SyncSettingDefinitionsAsync(conn, cameraId, camTemplates.Settings, ct)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reconcile measurement definitions for one camera using effective-date
    /// history: new entries are inserted, changed entries close the old row and
    /// insert a new one, and removed entries get an effective_end (section 9).
    /// </summary>
    private async Task SyncMeasurementDefinitionsAsync(
        MySqlConnection conn, int cameraId, ResultTemplateFile template, DateOnly today, CancellationToken ct)
    {
        var active = await LoadActiveMeasurementDefsAsync(conn, cameraId, ct).ConfigureAwait(false);
        var jsonVariables = new HashSet<string>(StringComparer.Ordinal);
        var inserted = 0;
        var changed = 0;
        var closed = 0;

        foreach (var m in template.Measurements)
        {
            jsonVariables.Add(m.VariableName);

            var incoming = new MeasurementDefinition
            {
                CameraId = cameraId,
                TelegramPlace = m.TelegramPlace,
                VariableName = m.VariableName,
                DisplayName = m.DisplayName,
                VarType = m.Type,
                ParameterSet = m.ParameterSet,
                ModuleRef = string.IsNullOrWhiteSpace(m.ModuleRef) ? "NoRef" : m.ModuleRef,
                FeatureGroup = string.IsNullOrWhiteSpace(m.FeatureGroup) ? "NoGroup" : m.FeatureGroup,
                EffectiveFrom = today,
            };

            if (!active.TryGetValue(m.VariableName, out var current))
            {
                await InsertMeasurementDefAsync(conn, incoming, ct).ConfigureAwait(false);
                inserted++;
            }
            else if (current.DiffersFrom(incoming))
            {
                await CloseMeasurementDefAsync(conn, current.Id, today, ct).ConfigureAwait(false);
                await InsertMeasurementDefAsync(conn, incoming, ct).ConfigureAwait(false);
                changed++;
                _log.Information("Definition changed for {Camera}/{Var}; new version recorded.",
                    template.Camera, m.VariableName);
            }
        }

        // Definitions present in the DB but no longer in the JSON are retired.
        foreach (var (variable, def) in active)
        {
            if (jsonVariables.Contains(variable))
                continue;
            await CloseMeasurementDefAsync(conn, def.Id, today, ct).ConfigureAwait(false);
            closed++;
            _log.Information("Definition retired for {Camera}/{Var} (removed from template).",
                template.Camera, variable);
        }

        if (inserted + changed + closed > 0)
            _log.Information("Measurement defs for {Camera}: +{Ins} ~{Chg} -{Cls}.",
                template.Camera, inserted, changed, closed);
    }

    private static async Task<Dictionary<string, MeasurementDefinition>> LoadActiveMeasurementDefsAsync(
        MySqlConnection conn, int cameraId, CancellationToken ct)
    {
        const string sql = @"
SELECT id, telegram_place, variable_name, display_name, var_type, parameter_set, module_ref, feature_group
FROM measurement_definitions
WHERE camera_id = @cam AND effective_end IS NULL;";

        var result = new Dictionary<string, MeasurementDefinition>(StringComparer.Ordinal);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cam", cameraId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var def = new MeasurementDefinition
            {
                Id = reader.GetInt32(0),
                CameraId = cameraId,
                TelegramPlace = reader.GetInt32(1),
                VariableName = reader.GetString(2),
                DisplayName = reader.GetString(3),
                VarType = reader.GetString(4),
                ParameterSet = reader.GetInt32(5),
                ModuleRef = reader.GetString(6),
                FeatureGroup = reader.GetString(7),
            };
            result[def.VariableName] = def;
        }

        return result;
    }

    private static async Task InsertMeasurementDefAsync(
        MySqlConnection conn, MeasurementDefinition def, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO measurement_definitions
  (camera_id, telegram_place, variable_name, display_name, var_type, parameter_set, module_ref, feature_group, effective_from)
VALUES
  (@cam, @place, @var, @disp, @type, @pset, @mref, @fgroup, @from);";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cam", def.CameraId);
        cmd.Parameters.AddWithValue("@place", def.TelegramPlace);
        cmd.Parameters.AddWithValue("@var", def.VariableName);
        cmd.Parameters.AddWithValue("@disp", def.DisplayName);
        cmd.Parameters.AddWithValue("@type", def.VarType);
        cmd.Parameters.AddWithValue("@pset", def.ParameterSet);
        cmd.Parameters.AddWithValue("@mref", def.ModuleRef);
        cmd.Parameters.AddWithValue("@fgroup", def.FeatureGroup);
        cmd.Parameters.AddWithValue("@from", def.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task CloseMeasurementDefAsync(
        MySqlConnection conn, int id, DateOnly end, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "UPDATE measurement_definitions SET effective_end = @end WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("@end", end.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upsert setting definitions for one camera, keyed on (camera_id, setting_name).
    /// </summary>
    private async Task SyncSettingDefinitionsAsync(
        MySqlConnection conn, int cameraId, SettingsTemplateFile template, CancellationToken ct)
    {
        var existing = await LoadSettingDefsAsync(conn, cameraId, ct).ConfigureAwait(false);
        var inserted = 0;
        var updated = 0;

        foreach (var s in template.Settings)
        {
            var incoming = new SettingDefinition
            {
                CameraId = cameraId,
                TelegramPlace = s.TelegramPlace,
                SettingName = s.SettingName,
                ParameterSet = s.ParameterSet,
                LimitType = s.LimitType,
            };

            if (!existing.TryGetValue(s.SettingName, out var current))
            {
                await using var cmd = new MySqlCommand(@"
INSERT INTO setting_definitions (camera_id, telegram_place, setting_name, parameter_set, limit_type)
VALUES (@cam, @place, @name, @pset, @ltype);", conn);
                cmd.Parameters.AddWithValue("@cam", cameraId);
                cmd.Parameters.AddWithValue("@place", incoming.TelegramPlace);
                cmd.Parameters.AddWithValue("@name", incoming.SettingName);
                cmd.Parameters.AddWithValue("@pset", incoming.ParameterSet);
                cmd.Parameters.AddWithValue("@ltype", incoming.LimitType);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                inserted++;
            }
            else if (current.DiffersFrom(incoming))
            {
                await using var cmd = new MySqlCommand(@"
UPDATE setting_definitions
SET telegram_place = @place, parameter_set = @pset, limit_type = @ltype
WHERE id = @id;", conn);
                cmd.Parameters.AddWithValue("@place", incoming.TelegramPlace);
                cmd.Parameters.AddWithValue("@pset", incoming.ParameterSet);
                cmd.Parameters.AddWithValue("@ltype", incoming.LimitType);
                cmd.Parameters.AddWithValue("@id", current.Id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                updated++;
            }
        }

        if (inserted + updated > 0)
            _log.Information("Setting defs for {Camera}: +{Ins} ~{Upd}.", template.Camera, inserted, updated);
    }

    private static async Task<Dictionary<string, SettingDefinition>> LoadSettingDefsAsync(
        MySqlConnection conn, int cameraId, CancellationToken ct)
    {
        const string sql = @"
SELECT id, telegram_place, setting_name, parameter_set, limit_type
FROM setting_definitions
WHERE camera_id = @cam;";

        var result = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cam", cameraId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var def = new SettingDefinition
            {
                Id = reader.GetInt32(0),
                CameraId = cameraId,
                TelegramPlace = reader.GetInt32(1),
                SettingName = reader.GetString(2),
                ParameterSet = reader.GetInt32(3),
                LimitType = reader.GetString(4),
            };
            result[def.SettingName] = def;
        }

        return result;
    }
}
