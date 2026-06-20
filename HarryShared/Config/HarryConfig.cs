using System.IO;
using IniParser;
using IniParser.Model;
using MySqlConnector;

namespace HarryShared.Config;

/// <summary>
/// Lightweight Harry.ini reader for the companion tools. Resolves the central
/// config folder the same way the main server does and exposes the MySQL
/// connection details. The companion tools connect with the read-only
/// <c>GetData</c> account (CLAUDE.md section 8) — never the server's SettData.
/// Server/Database are read from Harry.ini so the tools follow the deployment;
/// the read-only credentials default to GetData/1234Get but can be overridden in
/// Harry.ini via <c>[MySQL] GetUser</c> / <c>GetPassword</c> after the customer
/// changes the password.
/// </summary>
public sealed class HarryConfig
{
    public const string DefaultConfigDir = @"F:\002_Configs";

    public string IniPath { get; }
    public string ConfigDir { get; }
    public string Server { get; }
    public string Database { get; }
    public string ReadUser { get; }
    public string ReadPassword { get; }

    /// <summary>Folder holding the per-module MSA reference JSON files ([MSA] ReferencePath).</summary>
    public string MsaReferencePath { get; }

    /// <summary>Collage.ini layout path ([Collage] Collage_IniPath), resolved against the config dir.</summary>
    public string CollageIniPath { get; }

    /// <summary>Folder with the individual single images ([Collage] Collage_SingleImages).</summary>
    public string CollageSingleImagesPath { get; }

    private HarryConfig(string iniPath, IniData data)
    {
        IniPath = iniPath;
        ConfigDir = Path.GetDirectoryName(Path.GetFullPath(iniPath)) ?? AppContext.BaseDirectory;

        var sql = data["MySQL"];
        Server = Str(sql, "Server", "localhost");
        Database = Str(sql, "Database", "camera_data");
        ReadUser = Str(sql, "GetUser", "GetData");
        ReadPassword = Str(sql, "GetPassword", "1234Get");

        MsaReferencePath = ResolvePath(Str(data["MSA"], "ReferencePath", string.Empty));
        CollageIniPath = ResolvePath(Str(data["Collage"], "Collage_IniPath", string.Empty));
        CollageSingleImagesPath = Str(data["Collage"], "Collage_SingleImages", string.Empty);
    }

    /// <summary>Read-only (GetData) connection string for the application database.</summary>
    public string ReadOnlyConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Server,
        Database = Database,
        UserID = ReadUser,
        Password = ReadPassword,
        ConnectionTimeout = 10,
        DefaultCommandTimeout = 60,
        Pooling = true,
    }.ConnectionString;

    /// <summary>Open a pooled read-only connection to camera_data.</summary>
    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(ReadOnlyConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private string ResolvePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(ConfigDir, value));
    }

    /// <summary>Load Harry.ini from the resolved path (env → F:\002_Configs → exe dir → legacy).</summary>
    public static HarryConfig Load()
    {
        var path = ResolveIniPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Harry.ini not found. Looked at '{path}'. Set HARRY_CONFIG_DIR or place it in {DefaultConfigDir}.",
                path);

        var data = new FileIniDataParser().ReadFile(path);
        return new HarryConfig(path, data);
    }

    private static string ResolveIniPath()
    {
        var candidates = new List<string>();

        var envDir = Environment.GetEnvironmentVariable("HARRY_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            candidates.Add(Path.Combine(envDir, "Harry.ini"));

        candidates.Add(Path.Combine(DefaultConfigDir, "Harry.ini"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Harry.ini"));
        candidates.Add(@"D:\HarryDataServer\Harry.ini");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return candidates[0];
    }

    private static string Str(KeyDataCollection? keys, string key, string fallback)
    {
        if (keys is null || !keys.ContainsKey(key))
            return fallback;
        var value = keys[key];
        return string.IsNullOrEmpty(value) ? fallback : value.Trim();
    }
}
