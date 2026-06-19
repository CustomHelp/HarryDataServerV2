namespace HarryDataServer.Models;

/// <summary>
/// Strongly-typed view of the whole Harry.ini configuration. Populated once at
/// startup by <see cref="HarryDataServer.Configuration.IniConfigManager"/> and
/// exposed through <see cref="HarryDataServer.Services.IConfigService"/>.
/// </summary>
public sealed class AppConfig
{
    public GeneralConfig General { get; init; } = new();
    public MySqlConfig MySql { get; init; } = new();
    public CsvConfig Csv { get; init; } = new();
    public NasConfig Nas { get; init; } = new();
    public CollageConfig Collage { get; init; } = new();
    public SpsConfig Sps { get; init; } = new();
    public SqlSettingsConfig SqlSettings { get; init; } = new();
    public MsaConfig Msa { get; init; } = new();
    public IReadOnlyList<CameraConfig> Cameras { get; init; } = Array.Empty<CameraConfig>();
}

public sealed class GeneralConfig
{
    public string LogFilePath { get; init; } = @"D:\HarryDataServer\Logs\";
    public bool LoggingActive { get; init; } = true;
    public string Language { get; init; } = "English";
}

public sealed class MySqlConfig
{
    public string Server { get; init; } = "localhost";
    public string Database { get; init; } = "camera_data";
    public string User { get; init; } = "SettData";
    public string Password { get; init; } = "1234Set";
    public int RetentionPeriodDays { get; init; } = 35;
}

public sealed class CsvConfig
{
    public string BasePath { get; init; } = string.Empty;
    public string MsaPath { get; init; } = string.Empty;
    public string DiagnosticPath { get; init; } = string.Empty;
    public int DataSetsPerFile { get; init; } = 10000;
    public bool Save { get; init; } = true;
    public bool MsaSave { get; init; } = true;
    public bool DiagnosticSave { get; init; } = true;
}

public sealed class NasConfig
{
    public string BasePath { get; init; } = string.Empty;
    public string LowResIndividualPath { get; init; } = string.Empty;
    public string CollagePath { get; init; } = string.Empty;
    public string HighResNgPath { get; init; } = string.Empty;
    public string HighResDiagnosticPath { get; init; } = string.Empty;
    public string HighResGoldenSamplePath { get; init; } = string.Empty;
    public int RetentionNgDays { get; init; } = 30;
    public int RetentionDiagnosticDays { get; init; } = 30;
    public int RetentionGoldenSampleDays { get; init; } = 30;
    public bool DeleteAfterCollage { get; init; } = true;
}

public sealed class CollageConfig
{
    public string IniPath { get; init; } = string.Empty;
    public bool Generate { get; init; } = true;
}

public sealed class SpsConfig
{
    public string Ip { get; init; } = "172.29.1.5";
    public int PortKeepAlive { get; init; } = 6000;
    public int PortPartExit { get; init; } = 6001;
    public int PortMsaM10 { get; init; } = 6002;
    public int PortMsaM11 { get; init; } = 6003;
    public int PortMsaM20 { get; init; } = 6004;
    public int PortMsaM21 { get; init; } = 6005;
    public int PortMsaM50 { get; init; } = 6006;
    public bool AutoConnect { get; init; } = true;
}

public sealed class SqlSettingsConfig
{
    public int BatchSize { get; init; } = 100;
    public int SaveIntervalSeconds { get; init; } = 1;
}

public sealed class MsaConfig
{
    /// <summary>Folder holding the per-module MSA reference JSON files (relative to the config dir).</summary>
    public string ReferencePath { get; init; } = string.Empty;
}
