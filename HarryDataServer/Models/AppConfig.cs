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
    public DiagnosticConfig Diagnostic { get; init; } = new();
    public NasConfig Nas { get; init; } = new();
    public CollageConfig Collage { get; init; } = new();
    public SpsConfig Sps { get; init; } = new();
    public SqlSettingsConfig SqlSettings { get; init; } = new();
    public MsaConfig Msa { get; init; } = new();
    public ScannerConfig Scanner { get; init; } = new();
    public RetentionConfig Retention { get; init; } = new();
    public IReadOnlyList<CameraConfig> Cameras { get; init; } = Array.Empty<CameraConfig>();
}

public sealed class GeneralConfig
{
    public string LogFilePath { get; init; } = @"D:\HarryDataServer\Logs\";
    public bool LoggingActive { get; init; } = true;
    public string Language { get; init; } = "English";

    /// <summary>
    /// Meaningful (unpadded) length of a Serial1 frame/trimmer serial. The camera pads Serial1 with
    /// trailing '0' to its field width; the SPS delivers it unpadded. Both are normalised to this
    /// length so the DB serials match at part-exit lookup (see
    /// <see cref="HarryDataServer.Infrastructure.SerialNumberHelper"/>). Default 19 (live line).
    /// </summary>
    public int SerialNumberLength { get; init; } = HarryDataServer.Infrastructure.SerialNumberHelper.DefaultMeaningfulLength;

    /// <summary>
    /// Meaningful (unpadded) length of a TRIMMER serial (M20/M21 Virtual Serial). The camera pads it
    /// with trailing '0'; the SPS delivers it unpadded (13 chars on the live line). Both are normalised
    /// to this length so measurements_serial_trimmer matches the part-exit serial and trimmer image
    /// search works. Default 13 (live line).
    /// </summary>
    public int TrimmerSerialNumberLength { get; init; } = HarryDataServer.Infrastructure.SerialNumberHelper.DefaultTrimmerLength;
}

public sealed class MySqlConfig
{
    public string Server { get; init; } = "localhost";
    public string Database { get; init; } = "camera_data";
    public string User { get; init; } = "SettData";
    public string Password { get; init; } = "1234Set";
}

public sealed class CsvConfig
{
    public string BasePath { get; init; } = string.Empty;

    /// <summary>Root for the MSA/LimitSample summary CSV ([CSV] CSV_MSAPath, e.g. Y:\01_CSV_Evaluation).
    /// The CSV lands in &lt;CSV_MSAPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\; empty → local fallback. It is deliberately
    /// NOT under [MSA] ReferencePath, which stays pure configuration (the old MSA_Results tree there is
    /// no longer written).</summary>
    public string MsaPath { get; init; } = string.Empty;

    public string DiagnosticPath { get; init; } = string.Empty;
    public int DataSetsPerFile { get; init; } = 10000;
    public bool Save { get; init; } = true;
    public bool MsaSave { get; init; } = true;
    public bool DiagnosticSave { get; init; } = true;
}

public sealed class DiagnosticConfig
{
    /// <summary>
    /// Output folder for the raw diagnostic CSV dump (<c>[Diagnostic] DiagnosticPath</c>). Falls
    /// back to the legacy <c>[CSV] CSV_DiagnosticPath</c> when the [Diagnostic] key is empty, so the
    /// path is never duplicated.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Max rows per diagnostic CSV file before rotating to a new one (<c>[Diagnostic] MaxRows</c>, default 1000).</summary>
    public int MaxRows { get; init; } = 1000;
}

public sealed class NasConfig
{
    public string LowResIndividualPath { get; init; } = string.Empty;
    public string CollagePath { get; init; } = string.Empty;
    public string HighResNgPath { get; init; } = string.Empty;
    public string HighResDiagnosticPath { get; init; } = string.Empty;
    public string HighResGoldenSamplePath { get; init; } = string.Empty;

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

/// <summary>
/// DMC handheld-scanner bridge ([Scanner] section). The server listens for the scanner on
/// <see cref="ListenPort"/> (fixed by the scanner hardware), keeps the last
/// <see cref="MaxScanHistoryRows"/> scans for the Scanner tab, and rebroadcasts every scan to the
/// companion apps connected on <see cref="CompanionPort"/> (CLAUDE.md §… / SOW scanner bridge).
/// </summary>
public sealed class ScannerConfig
{
    /// <summary>Port the scanner (TCP client) connects to. Fixed on the scanner side — do not change.</summary>
    public int ListenPort { get; init; } = 9004;

    /// <summary>Port the companion apps connect to for the rebroadcast of each scan.</summary>
    public int CompanionPort { get; init; } = 9000;

    /// <summary>Ring-buffer size for the Scanner tab (in-memory, cleared on restart).</summary>
    public int MaxScanHistoryRows { get; init; } = 100;
}

public sealed class MsaConfig
{
    /// <summary>Folder holding the per-module MSA reference JSON files (input definitions,
    /// persistent; written by HarryLimitSample, read here). Relative to the config dir.</summary>
    public string ReferencePath { get; init; } = string.Empty;

    /// <summary>Root folder for per-run MSA/LimitSample result collection (output). On run
    /// completion the server gathers the measurement CSV and the run images
    /// under <c>&lt;ResultPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\{CSV,IMG}</c>. Kept SEPARATE from
    /// <see cref="ReferencePath"/>. When empty, falls back to ReferencePath\MSA_Results.</summary>
    public string ResultPath { get; init; } = string.Empty;

    /// <summary>Root folder for the human-facing MSA outputs — the PDF reports AND the raw-data
    /// export (Minitab). Files land in <c>&lt;ReportPath&gt;\&lt;Module&gt;\&lt;yyyy-MM-dd&gt;\</c>.
    /// Absolute local, mapped-drive and UNC (<c>\\server\share</c>) paths are all supported; a
    /// relative path resolves against the config dir. When empty, falls back to
    /// <see cref="ReportFallbackPath"/> (then ResultPath, then a built-in local default).</summary>
    public string ReportPath { get; init; } = string.Empty;

    /// <summary>Local fallback for <see cref="ReportPath"/> when the primary (e.g. a network
    /// drive) is not reachable at write time. A WARNING is logged and the run is still written —
    /// never a crash or data loss. Default <c>D:\HarryDataServer\MSA_Reports</c> when empty.</summary>
    public string ReportFallbackPath { get; init; } = string.Empty;
}

/// <summary>
/// Central retention policy ([Retention] section). ONE place for every "how long do we keep …"
/// decision — images, MSA reports, CSV exports and the database — consumed by the single
/// <see cref="HarryDataServer.Services.RetentionService"/>.
/// <para>Semantics everywhere: the value is a number of DAYS; <b>0 = never delete</b>.</para>
/// The legacy keys ([MySQL] RetentionPeriodDays, [NAS] Retention*Days / BackupRetentionDays /
/// FullResRetentionDays) are still read as a fallback (with a deprecation WARNING) when the matching
/// [Retention] key is absent — see <see cref="Deprecations"/>.
/// </summary>
public sealed class RetentionConfig
{
    public int ImagesNg { get; init; } = 30;              // Z:\03_High_Resolution_NG
    public int ImagesDiagnostic { get; init; } = 30;      // Z:\04_High_Resolution_Diagnostic
    public int ImagesGoldenSample { get; init; } = 30;    // Z:\05_High_Resolution_GoldenSample
    public int ImagesCollage { get; init; } = 30;         // Z:\02_Low_Resolution_Collage
    public int ImagesBackup { get; init; } = 30;          // Z:\06_Backup (part-exit backup tree)

    /// <summary>Age (days) after which a file still sitting in a <c>…\Input</c> folder counts as a
    /// leftover from a failed pipeline run and is deleted (with a WARNING). Default 3.</summary>
    public int ImagesInputLeftovers { get; init; } = 3;

    /// <summary>Production tables: measurements_serial(_trimmer) (DROP PARTITION) + dmcserial (batch DELETE).</summary>
    public int DatabaseProduction { get; init; } = 35;

    /// <summary>MSA tables (msa_measurements, msa_results). Default 0 = NEVER — QS data; only the
    /// customer/QS enables ageing.</summary>
    public int DatabaseMsa { get; init; }                 // default 0 = never

    /// <summary>MSA report/raw folders under [MSA] ReportPath. Default 0 = NEVER (QS evidence).</summary>
    public int ReportsMsa { get; init; }                  // default 0 = never

    public int CsvEvaluation { get; init; } = 365;        // Y:\01_CSV_Evaluation ([CSV] CSV_MSAPath)
    public int CsvMerge { get; init; } = 365;             // Y:\02_CSV_Merge ([CSV] CSV_BasePath) — production evidence
    public int CsvExtraResults { get; init; } = 90;       // Y:\03_CSV_ExtraResults ([CSV] CSV_DiagnosticPath)

    /// <summary>Human-readable deprecation notes for legacy keys that were used as a fallback
    /// (logged once at startup). Empty when the [Retention] section fully supersedes them.</summary>
    public IReadOnlyList<string> Deprecations { get; init; } = Array.Empty<string>();
}
