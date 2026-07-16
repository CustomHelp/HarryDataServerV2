using System.Globalization;
using System.IO;
using HarryDataServer.Models;
using IniParser;
using IniParser.Model;

namespace HarryDataServer.Configuration;

/// <summary>
/// Reads Harry.ini from disk and maps it into a strongly-typed <see cref="AppConfig"/>.
/// Camera sections ([Camera1], [Camera2], ...) are discovered dynamically — the
/// number of cameras is never hardcoded. Missing keys fall back to the defaults
/// defined on the model classes.
/// </summary>
public sealed class IniConfigManager
{
    private readonly FileIniDataParser _parser = new();

    /// <summary>
    /// Parse the INI file at <paramref name="iniPath"/>. Throws
    /// <see cref="FileNotFoundException"/> if the file does not exist.
    /// </summary>
    public AppConfig Load(string iniPath)
    {
        if (!File.Exists(iniPath))
            throw new FileNotFoundException($"Harry.ini not found at '{iniPath}'.", iniPath);

        IniData data = _parser.ReadFile(iniPath);

        // Relative paths in Harry.ini (e.g. template files) are resolved against
        // the directory that contains Harry.ini, so the whole config folder
        // (Harry.ini + Templates\) is portable.
        var configDir = Path.GetDirectoryName(Path.GetFullPath(iniPath)) ?? AppContext.BaseDirectory;

        var csv = ParseCsv(data);

        return new AppConfig
        {
            General = ParseGeneral(data),
            MySql = ParseMySql(data),
            Csv = csv,
            Diagnostic = ParseDiagnostic(data, csv),
            Nas = ParseNas(data),
            Collage = ParseCollage(data, configDir),
            Sps = ParseSps(data),
            SqlSettings = ParseSqlSettings(data),
            Msa = ParseMsa(data, configDir),
            Scanner = ParseScanner(data),
            Cameras = ParseCameras(data, configDir),
        };
    }

    private static ScannerConfig ParseScanner(IniData data)
    {
        var s = data["Scanner"];
        return new ScannerConfig
        {
            ListenPort = Int(s, "ScannerListenPort", 9004),
            CompanionPort = Int(s, "CompanionPort", 9000),
            MaxScanHistoryRows = Int(s, "MaxScanHistoryRows", 100),
        };
    }

    private static MsaConfig ParseMsa(IniData data, string configDir)
    {
        var s = data["MSA"];
        return new MsaConfig
        {
            ReferencePath = ResolvePath(Str(s, "ReferencePath", string.Empty), configDir),
            ResultPath = ResolvePath(Str(s, "ResultPath", string.Empty), configDir),
        };
    }

    /// <summary>Resolve a path from the INI: absolute paths are kept, relative paths
    /// are combined with the config directory. Empty stays empty.</summary>
    private static string ResolvePath(string value, string configDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(configDir, value));
    }

    private static GeneralConfig ParseGeneral(IniData data)
    {
        var s = data["General"];
        return new GeneralConfig
        {
            LogFilePath = Str(s, "LogFilePath", @"D:\HarryDataServer\Logs\"),
            LoggingActive = Bool(s, "LoggingActive", true),
            Language = Str(s, "Language", "English"),
        };
    }

    private static MySqlConfig ParseMySql(IniData data)
    {
        var s = data["MySQL"];
        return new MySqlConfig
        {
            Server = Str(s, "Server", "localhost"),
            Database = Str(s, "Database", "camera_data"),
            User = Str(s, "User", "SettData"),
            Password = Str(s, "Password", "1234Set"),
            RetentionPeriodDays = Int(s, "RetentionPeriodDays", 35),
        };
    }

    private static CsvConfig ParseCsv(IniData data)
    {
        var s = data["CSV"];
        return new CsvConfig
        {
            BasePath = Str(s, "CSV_BasePath", string.Empty),
            DiagnosticPath = Str(s, "CSV_DiagnosticPath", string.Empty),
            DataSetsPerFile = Int(s, "DataSetsPerFile", 10000),
            Save = Bool(s, "CSV_Save", true),
            MsaSave = Bool(s, "CSVMSA_Save", true),
            DiagnosticSave = Bool(s, "CSVDiagnostic_Save", true),
        };
    }

    /// <summary>
    /// Parse the [Diagnostic] section (raw diagnostic CSV dump). The output path reuses the legacy
    /// [CSV] CSV_DiagnosticPath when [Diagnostic] DiagnosticPath is omitted, so it is never duplicated.
    /// </summary>
    private static DiagnosticConfig ParseDiagnostic(IniData data, CsvConfig csv)
    {
        var s = data["Diagnostic"];
        var path = Str(s, "DiagnosticPath", string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            path = csv.DiagnosticPath;   // reuse existing [CSV] CSV_DiagnosticPath

        return new DiagnosticConfig
        {
            Path = path,
            MaxRows = Int(s, "MaxRows", 1000),
        };
    }

    private static NasConfig ParseNas(IniData data)
    {
        var s = data["NAS"];
        // Default full-res retention; the per-type retentions fall back to it when unset.
        var fullRes = Int(s, "FullResRetentionDays", 30);
        return new NasConfig
        {
            BasePath = Str(s, "NAS_BasePath", string.Empty),
            LowResIndividualPath = Str(s, "LowResIndividualPath", string.Empty),
            CollagePath = Str(s, "CollagePath", string.Empty),
            HighResNgPath = Str(s, "HighResNGPath", string.Empty),
            HighResDiagnosticPath = Str(s, "HighResDiagnosticPath", string.Empty),
            HighResGoldenSamplePath = Str(s, "HighResGoldenSamplePath", string.Empty),
            FullResRetentionDays = fullRes,
            RetentionNgDays = Int(s, "RetentionNGDays", fullRes),
            RetentionDiagnosticDays = Int(s, "RetentionDiagnosticDays", fullRes),
            RetentionGoldenSampleDays = Int(s, "RetentionGoldenSampleDays", fullRes),
            RetentionCollageDays = Int(s, "RetentionCollageDays", fullRes),
            DeleteAfterCollage = Bool(s, "DeleteAfterCollage", true),
            DeletePictures = Bool(s, "DeletePictures", true),
            BackupFolder = Str(s, "BackupFolder", string.Empty),
        };
    }

    private static CollageConfig ParseCollage(IniData data, string configDir)
    {
        var s = data["Collage"];
        return new CollageConfig
        {
            IniPath = ResolvePath(Str(s, "Collage_IniPath", string.Empty), configDir),
            Generate = Bool(s, "Collage_Generate", true),
            SingleImagesPath = Str(s, "Collage_SingleImages", string.Empty),
            ResultImagesPath = Str(s, "Collage_ResultImages", string.Empty),
            MaxFileSizeKb = Int(s, "MaxFileSizeKB", 128),
        };
    }

    private static SpsConfig ParseSps(IniData data)
    {
        var s = data["SPS"];
        return new SpsConfig
        {
            Ip = Str(s, "IP", "172.29.1.5"),
            PortKeepAlive = Int(s, "PortKeepAlive", 6000),
            PortPartExit = Int(s, "PortPartExit", 6001),
            PortMsaM10 = Int(s, "PortMSA_M10", 6002),
            PortMsaM11 = Int(s, "PortMSA_M11", 6003),
            PortMsaM20 = Int(s, "PortMSA_M20", 6004),
            PortMsaM21 = Int(s, "PortMSA_M21", 6005),
            PortMsaM50 = Int(s, "PortMSA_M50", 6006),
            AutoConnect = Bool(s, "AutoConnect", true),
        };
    }

    private static SqlSettingsConfig ParseSqlSettings(IniData data)
    {
        var s = data["SQLSettings"];
        return new SqlSettingsConfig
        {
            BatchSize = Int(s, "BatchSize", 100),
            SaveIntervalSeconds = Int(s, "SaveIntervalSeconds", 1),
        };
    }

    /// <summary>
    /// Discover all [CameraN] sections dynamically and build a <see cref="CameraConfig"/>
    /// for each, ordered by index. Sections without an IP are skipped.
    /// </summary>
    private static IReadOnlyList<CameraConfig> ParseCameras(IniData data, string configDir)
    {
        var cameras = new List<CameraConfig>();

        foreach (SectionData section in data.Sections)
        {
            if (!section.SectionName.StartsWith("Camera", StringComparison.OrdinalIgnoreCase))
                continue;

            var indexText = section.SectionName.Substring("Camera".Length);
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue; // Not a "CameraN" section (e.g. a [Camera] heading).

            var keys = section.Keys;
            var cameraName = Str(keys, "CameraName", string.Empty);
            var ip = Str(keys, "IP", string.Empty);

            if (string.IsNullOrWhiteSpace(ip))
                continue;

            cameras.Add(new CameraConfig
            {
                Index = index,
                CameraName = cameraName,
                Module = DeriveModule(cameraName),
                Ip = ip,
                Port = Int(keys, "Port", 8500),
                JsonParameters = ResolvePath(Str(keys, "JsonParameters", string.Empty), configDir),
                JsonSettings = ResolvePath(Str(keys, "JsonSettings", string.Empty), configDir),
                AutoConnect = Bool(keys, "AutoConnect", true),
            });
        }

        cameras.Sort((a, b) => a.Index.CompareTo(b.Index));
        return cameras;
    }

    /// <summary>Extract the module prefix (e.g. "M50") from a camera name "M50_ST110_KF1".</summary>
    private static string DeriveModule(string cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            return string.Empty;

        var underscore = cameraName.IndexOf('_');
        return underscore > 0 ? cameraName.Substring(0, underscore) : cameraName;
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

    private static bool Bool(KeyDataCollection? keys, string key, bool fallback)
    {
        var raw = Str(keys, key, string.Empty);
        if (string.IsNullOrEmpty(raw))
            return fallback;

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }
}
