using System.Globalization;
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

    /// <summary>Base folder for CSV exports ([CSV] CSV_BasePath) — used as the export default dir.</summary>
    public string CsvBasePath { get; }

    /// <summary>Host of HarryDataServer's companion broadcast server ([Scanner] CompanionHost,
    /// default 172.29.1.5) — where the companion scanner client connects to receive DMC scans.</summary>
    public string ScannerHost { get; }

    /// <summary>Port of HarryDataServer's companion broadcast server ([Scanner] CompanionPort, default 9000).</summary>
    public int ScannerPort { get; }

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
        CsvBasePath = Str(data["CSV"], "CSV_BasePath", string.Empty);

        var scanner = data["Scanner"];
        ScannerHost = Str(scanner, "CompanionHost", "172.29.1.5");
        ScannerPort = Int(scanner, "CompanionPort", 9000);
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

    /// <summary>
    /// Load Harry.ini using the shared search order (env → per-tool override → F:\002_Configs → exe →
    /// legacy). Non-interactive: throws when nothing is found. Pass <paramref name="toolName"/> so a
    /// per-tool <c>%APPDATA%\&lt;Tool&gt;\config.json</c> override is honoured (see <see cref="ConfigLocator"/>).
    /// </summary>
    public static HarryConfig Load(string? toolName = null)
    {
        var path = ConfigLocator.Resolve(toolName);
        if (path is null)
            throw new FileNotFoundException(
                $"Harry.ini not found. Looked at '{ConfigLocator.ActivePath(toolName)}'. " +
                $"Set HARRY_CONFIG_DIR, place it in {DefaultConfigDir}, or choose it via 'Config-Pfad ändern…'.",
                ConfigLocator.ActivePath(toolName));

        var data = new FileIniDataParser().ReadFile(path);
        return new HarryConfig(path, data);
    }

    /// <summary>
    /// Load like <see cref="Load(string)"/>, but when no config is found show <see cref="ConfigPathDialog"/>
    /// so the operator can pick one; the pick is persisted per tool. Returns null when the user cancels
    /// (the caller should then shut down cleanly). On the production machine F:\002_Configs\Harry.ini
    /// resolves first, so the dialog never appears there — behaviour is unchanged on the line.
    /// </summary>
    public static HarryConfig? LoadInteractive(string toolName)
    {
        var path = ConfigLocator.Resolve(toolName);
        if (path is null)
        {
            var dlg = new ConfigPathDialog(toolName, ConfigLocator.ActivePath(toolName));
            if (dlg.ShowDialog() == true && dlg.SelectedPath is not null)
            {
                ConfigLocator.SaveOverride(toolName, dlg.SelectedPath);
                path = dlg.SelectedPath;
            }
            else
            {
                return null; // cancelled
            }
        }

        var data = new FileIniDataParser().ReadFile(path);
        return new HarryConfig(path, data);
    }

    /// <summary>
    /// Show the "Config-Pfad ändern…" dialog and, if the operator picks a valid Harry.ini, persist it as
    /// the per-tool override. Returns true when the pinned path changed (the caller prompts for a restart,
    /// since config is read once at startup). Safe to call from any window's top-bar button.
    /// </summary>
    public static bool ShowChangeDialog(string toolName)
    {
        var dlg = new ConfigPathDialog(toolName, ConfigLocator.ActivePath(toolName));
        if (dlg.ShowDialog() == true && dlg.SelectedPath is not null)
        {
            ConfigLocator.SaveOverride(toolName, dlg.SelectedPath);
            return true;
        }
        return false;
    }

    private static string Str(KeyDataCollection? keys, string key, string fallback)
    {
        if (keys is null || !keys.ContainsKey(key))
            return fallback;
        var value = keys[key];
        return string.IsNullOrEmpty(value) ? fallback : value.Trim();
    }

    private static int Int(KeyDataCollection? keys, string key, int fallback)
    {
        var raw = Str(keys, key, string.Empty);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}
