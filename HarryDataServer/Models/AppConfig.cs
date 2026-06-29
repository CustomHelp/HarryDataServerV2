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

    /// <summary>Retention (days) for finished collages, configurable independently of the
    /// full-res image types. Falls back to <see cref="FullResRetentionDays"/> when unset.</summary>
    public int RetentionCollageDays { get; init; } = 30;

    /// <summary>Default full-resolution image retention in days (SOW §5.2.3). Used as the
    /// fallback for the per-type NG/Diagnostic/GoldenSample retention when those are unset.</summary>
    public int FullResRetentionDays { get; init; } = 30;

    public bool DeleteAfterCollage { get; init; } = true;

    /// <summary>Part-exit image handling: true = delete source images; false = backup then delete.</summary>
    public bool DeletePictures { get; init; } = true;

    /// <summary>Root backup folder (used when DeletePictures = false). Structure: \YYYY\MM\DD\.</summary>
    public string BackupFolder { get; init; } = string.Empty;
}

public sealed class CollageConfig
{
    public string IniPath { get; init; } = string.Empty;
    public bool Generate { get; init; } = true;

    /// <summary>Folder holding the individual single images to search (Collage_SingleImages).</summary>
    public string SingleImagesPath { get; init; } = string.Empty;

    /// <summary>Output folder for finished collages (Collage_ResultImages).</summary>
    public string ResultImagesPath { get; init; } = string.Empty;

    /// <summary>Maximum collage file size in kilobytes (SOW §5.2.2). The composer
    /// re-encodes at decreasing JPEG quality until the output fits. Default 128 KB.</summary>
    public int MaxFileSizeKb { get; init; } = 128;
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
    /// <summary>Folder holding the per-module MSA reference JSON files (input definitions,
    /// persistent; written by HarryLimitSample, read here). Relative to the config dir.</summary>
    public string ReferencePath { get; init; } = string.Empty;

    /// <summary>Root folder for per-run MSA/LimitSample result collection (output). On run
    /// completion the server gathers the PDF reports, the measurement CSV and the run images
    /// under <c>&lt;ResultPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\{PDF,CSV,IMG}</c>. Kept SEPARATE from
    /// <see cref="ReferencePath"/>. When empty, falls back to ReferencePath\MSA_Results.</summary>
    public string ResultPath { get; init; } = string.Empty;
}
