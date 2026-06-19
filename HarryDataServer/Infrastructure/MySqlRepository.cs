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
        await SchemaCheckAsync(ct).ConfigureAwait(false);
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
