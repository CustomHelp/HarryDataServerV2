using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>Resolved identifiers for a camera's setting definition.</summary>
public readonly record struct SettingRef(int CameraId, int DefinitionId);

/// <summary>
/// In-memory lookup from (camera name, setting name) to the owning camera id and
/// <c>setting_definitions.id</c>. Loaded once after the database is ready so the
/// camera receive threads never touch the DB. Mirrors <see cref="MeasurementDefinitionCache"/>.
/// </summary>
public sealed class SettingDefinitionCache
{
    private readonly ILogService _log;
    private volatile Dictionary<(string Camera, string Setting), SettingRef> _map = new();

    public SettingDefinitionCache(ILogService log) => _log = log;

    public bool IsLoaded { get; private set; }
    public int Count => _map.Count;

    public bool TryGet(string cameraName, string settingName, out SettingRef reference) =>
        _map.TryGetValue((cameraName, settingName), out reference);

    public async Task LoadAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.camera_name, sd.setting_name, c.id, sd.id
FROM setting_definitions sd
JOIN cameras c ON c.id = sd.camera_id;";

        var map = new Dictionary<(string, string), SettingRef>();

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var camera = reader.GetString(0);
            var setting = reader.GetString(1);
            var cameraId = reader.GetInt32(2);
            var definitionId = reader.GetInt32(3);
            map[(camera, setting)] = new SettingRef(cameraId, definitionId);
        }

        _map = map;
        IsLoaded = true;
        _log.Information("Setting definition cache loaded: {Count} definitions.", map.Count);
    }
}
