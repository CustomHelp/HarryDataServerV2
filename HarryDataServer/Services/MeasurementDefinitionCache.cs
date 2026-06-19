using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// In-memory lookup from (camera name, variable name) to the active
/// <c>measurement_definitions.id</c>. Loaded once after the database is ready so
/// the camera receive threads can resolve <c>definition_id</c> without DB I/O.
/// </summary>
public sealed class MeasurementDefinitionCache
{
    private readonly ILogService _log;
    private volatile Dictionary<(string Camera, string Variable), int> _map = new();

    public MeasurementDefinitionCache(ILogService log) => _log = log;

    public bool IsLoaded { get; private set; }

    public int Count => _map.Count;

    /// <summary>Resolve the active definition id for a camera's variable.</summary>
    public bool TryGet(string cameraName, string variableName, out int definitionId) =>
        _map.TryGetValue((cameraName, variableName), out definitionId);

    /// <summary>
    /// (Re)load all active definitions into the cache. Atomically swaps the backing
    /// dictionary so concurrent readers always see a consistent map.
    /// </summary>
    public async Task LoadAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.camera_name, md.variable_name, md.id
FROM measurement_definitions md
JOIN cameras c ON c.id = md.camera_id
WHERE md.effective_end IS NULL;";

        var map = new Dictionary<(string, string), int>();

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var camera = reader.GetString(0);
            var variable = reader.GetString(1);
            var id = reader.GetInt32(2);
            map[(camera, variable)] = id;
        }

        _map = map;
        IsLoaded = true;
        _log.Information("Measurement definition cache loaded: {Count} active definitions.", map.Count);
    }
}
