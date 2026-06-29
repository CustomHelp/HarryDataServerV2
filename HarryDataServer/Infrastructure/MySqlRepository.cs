using System.Linq;
using HarryDataServer.Models;
using HarryDataServer.Services;
using MySqlConnector;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Low-level MySQL access: connection management and DDL. Implements the
/// schema-bootstrap parts of the startup logic (CLAUDE.md section 8):
/// create database, create tables, and the automatic ADD COLUMN schema-check.
/// One connection is opened per operation (MySqlConnector pools internally);
/// connections are never shared across threads.
/// </summary>
public sealed class MySqlRepository
{
    private readonly MySqlConfig _config;
    private readonly ILogService _log;
    private readonly string _connectionStringWithDb;
    private readonly string _connectionStringServer;

    public MySqlRepository(MySqlConfig config, ILogService log)
    {
        _config = config;
        _log = log;

        var baseBuilder = new MySqlConnectionStringBuilder
        {
            Server = config.Server,
            UserID = config.User,
            Password = config.Password,
            // Allow the application to retrieve generated IDs and run multiple statements.
            AllowUserVariables = true,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 60,
            Pooling = true,
        };

        _connectionStringServer = baseBuilder.ConnectionString;

        baseBuilder.Database = config.Database;
        _connectionStringWithDb = baseBuilder.ConnectionString;
    }

    public string DatabaseName => _config.Database;

    /// <summary>Open a pooled connection to the application database.</summary>
    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(_connectionStringWithDb);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Verify the MySQL server is reachable (connects without selecting a database).
    /// Returns false instead of throwing so callers can implement backoff.
    /// </summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new MySqlConnection(_connectionStringServer);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug("MySQL not reachable yet: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Full schema bootstrap: CREATE DATABASE, CREATE TABLE for every table, then
    /// the column-level schema-check. Idempotent — safe to run on every startup.
    /// </summary>
    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseAsync(ct).ConfigureAwait(false);
        await EnsureTablesAsync(ct).ConfigureAwait(false);
        await RebuildOutdatedSerialTablesAsync(ct).ConfigureAwait(false);
        await SchemaCheckAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bring serial columns to their current width by rebuilding any table whose serial column
    /// is still the old width. Serial1 (SZID / Virtual Serial / MSA BaseID+loop) is stored in
    /// VARCHAR(22) (CLAUDE.md §4). Because an ALTER ... MODIFY is awkward on the partitioned
    /// measurement tables and the data is disposable during trial operation, the migration is a
    /// DROP + re-CREATE at the new width. Idempotent: a table already at the expected width is
    /// left untouched, so this only fires on the one-time transition. Runs after the tables are
    /// ensured (so the column can be inspected) and before the ADD COLUMN schema-check; the
    /// index- and partition-checks run later in startup and repopulate the recreated tables.
    /// </summary>
    private async Task RebuildOutdatedSerialTablesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (table, column, length) in DatabaseSchema.SerialColumnWidths)
        {
            if (rebuilt.Contains(table))
                continue;

            var actual = await GetColumnLengthAsync(conn, table, column, ct).ConfigureAwait(false);
            if (actual is null || actual == length)
                continue; // column absent (fresh install already at the new width) or already correct

            _log.Warning(
                "Schema rebuild: {Table}.{Column} is VARCHAR({Actual}) but VARCHAR({Length}) is expected; " +
                "dropping and recreating the table (existing rows are cleared).",
                table, column, actual.Value, length);

            var schema = DatabaseSchema.Tables.First(t => t.Name == table);
            await using (var drop = new MySqlCommand($"DROP TABLE IF EXISTS `{table}`;", conn))
                await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using (var create = new MySqlCommand(schema.CreateSql, conn))
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            rebuilt.Add(table);
            _log.Information("Schema rebuild: recreated {Table} with the current schema.", table);
        }
    }

    /// <summary>The declared length of a VARCHAR column, or null if the table/column is absent.</summary>
    private static async Task<int?> GetColumnLengthAsync(
        MySqlConnection conn, string table, string column, CancellationToken ct)
    {
        const string sql = @"
SELECT CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(_connectionStringServer);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var sql = $"CREATE DATABASE IF NOT EXISTS `{_config.Database}` {DatabaseSchema.DefaultCharset};";
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _log.Information("Database ensured: {Database}", _config.Database);
    }

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        foreach (var table in DatabaseSchema.Tables)
        {
            await using var cmd = new MySqlCommand(table.CreateSql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _log.Debug("Table ensured: {Table}", table.Name);
        }

        _log.Information("All {Count} tables ensured.", DatabaseSchema.Tables.Count);
    }

    /// <summary>
    /// Compare each table's actual columns against the expected list and add any
    /// missing column with ALTER TABLE. Every change is logged. This lets a new
    /// column be deployed by a code change alone (CLAUDE.md section 8).
    /// </summary>
    private async Task SchemaCheckAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changes = 0;

        foreach (var table in DatabaseSchema.Tables)
        {
            var existing = await GetExistingColumnsAsync(conn, table.Name, ct).ConfigureAwait(false);

            foreach (var column in table.Columns)
            {
                if (existing.Contains(column.Name))
                    continue;

                // Strip inline PRIMARY KEY/AUTO_INCREMENT clauses that are only valid
                // at CREATE time; a genuinely missing data column is added plainly.
                var definition = SanitizeForAdd(column.Definition);
                var alter = $"ALTER TABLE `{table.Name}` ADD COLUMN `{column.Name}` {definition};";

                try
                {
                    await using var cmd = new MySqlCommand(alter, conn);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    changes++;
                    _log.Information("Schema change: added column {Table}.{Column} ({Definition})",
                        table.Name, column.Name, definition);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to add column {Table}.{Column}", table.Name, column.Name);
                }
            }
        }

        if (changes == 0)
            _log.Information("Schema-check complete: all tables up to date.");
        else
            _log.Information("Schema-check complete: applied {Count} column change(s).", changes);
    }

    /// <summary>
    /// Ensure every expected secondary index exists, creating any that are missing.
    /// MySQL has no <c>CREATE INDEX IF NOT EXISTS</c>, so each index is first looked
    /// up in INFORMATION_SCHEMA.STATISTICS and only created when absent — the same
    /// approach as the ADD COLUMN schema-check. Runs after the column check at
    /// startup so a new index deploys by a code change alone (CLAUDE.md section 8).
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changes = 0;

        foreach (var index in DatabaseSchema.ExpectedIndexes)
        {
            if (await IndexExistsAsync(conn, index.Table, index.Name, ct).ConfigureAwait(false))
                continue;

            var create = $"CREATE INDEX `{index.Name}` ON `{index.Table}` ({index.Columns});";

            try
            {
                await using var cmd = new MySqlCommand(create, conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                changes++;
                _log.Information("Index created: {Table}.{Index} ({Columns})",
                    index.Table, index.Name, index.Columns);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create index {Table}.{Index}", index.Table, index.Name);
            }
        }

        if (changes == 0)
            _log.Information("Index-check complete: all indexes present.");
        else
            _log.Information("Index-check complete: created {Count} index(es).", changes);
    }

    private static async Task<bool> IndexExistsAsync(
        MySqlConnection conn, string tableName, string indexName, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND INDEX_NAME = @index;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@index", indexName);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return count > 0;
    }

    private static async Task<HashSet<string>> GetExistingColumnsAsync(
        MySqlConnection conn, string tableName, CancellationToken ct)
    {
        const string sql = @"
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            columns.Add(reader.GetString(0));

        return columns;
    }

    /// <summary>
    /// Remove clauses from a column definition that are illegal in ADD COLUMN
    /// (PRIMARY KEY / AUTO_INCREMENT), keeping the plain type and nullability.
    /// </summary>
    private static string SanitizeForAdd(string definition)
    {
        var cleaned = definition
            .Replace("AUTO_INCREMENT", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("PRIMARY KEY", string.Empty, StringComparison.OrdinalIgnoreCase);

        // Collapse the whitespace left behind by the removals.
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
