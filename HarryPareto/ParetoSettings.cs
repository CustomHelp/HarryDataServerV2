using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;

namespace HarryPareto;

/// <summary>
/// Connection + refresh settings for HarryPareto, persisted to
/// <c>%APPDATA%\HarryPareto\settings.json</c>. The password is encrypted at rest with DPAPI
/// (CurrentUser) — only <see cref="PasswordEnc"/> is written; the plain <see cref="Password"/> is
/// never serialised (task E1). Defaults target the on-machine server with the read-only GetData
/// account (CLAUDE.md §8); the IP is meant to be changed for remote operation on another PC.
/// </summary>
public sealed class ParetoSettings
{
    public string Ip { get; set; } = "172.29.1.5";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "camera_data";
    public string User { get; set; } = "GetData";

    /// <summary>Plain password, held only in memory (encrypted on disk via <see cref="PasswordEnc"/>).</summary>
    [JsonIgnore]
    public string Password { get; set; } = "1234Get";

    /// <summary>DPAPI-protected password (base64) — the only password form written to disk.</summary>
    [JsonPropertyName("password_enc")]
    public string PasswordEnc { get; set; } = string.Empty;

    /// <summary>Auto-refresh interval in seconds (task E3; default 30).</summary>
    public int RefreshSeconds { get; set; } = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HarryPareto", "settings.json");

    /// <summary>Read-only connection string built from the settings.</summary>
    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = string.IsNullOrWhiteSpace(Ip) ? "localhost" : Ip,
        Port = (uint)(Port <= 0 ? 3306 : Port),
        Database = string.IsNullOrWhiteSpace(Database) ? "camera_data" : Database,
        UserID = User,
        Password = Password,
        ConnectionTimeout = 8,
        DefaultCommandTimeout = 30,
        Pooling = true,
    }.ConnectionString;

    /// <summary>Load the saved settings, or defaults when none exist / the file is unreadable.</summary>
    public static ParetoSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new ParetoSettings();

            var s = JsonSerializer.Deserialize<ParetoSettings>(File.ReadAllText(path), JsonOptions)
                    ?? new ParetoSettings();
            if (!string.IsNullOrEmpty(s.PasswordEnc))
            {
                try { s.Password = Dpapi.Unprotect(s.PasswordEnc); }
                catch { s.Password = string.Empty; } // corrupt/foreign-user blob → force re-entry
            }
            return s;
        }
        catch
        {
            return new ParetoSettings();
        }
    }

    /// <summary>Persist the settings, encrypting the password with DPAPI. Best-effort.</summary>
    public void Save()
    {
        try
        {
            PasswordEnc = Dpapi.Protect(Password ?? string.Empty);
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // persistence is best-effort — never crash the app because settings could not be written
        }
    }

    public ParetoSettings Clone() => new()
    {
        Ip = Ip, Port = Port, Database = Database, User = User,
        Password = Password, PasswordEnc = PasswordEnc, RefreshSeconds = RefreshSeconds,
    };
}
