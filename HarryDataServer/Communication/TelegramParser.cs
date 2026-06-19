using System.Globalization;
using HarryDataServer.Configuration;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Communication;

/// <summary>
/// Parses Keyence camera telegrams (CLAUDE.md section 4). Stateless and shared by
/// all camera clients: the header (positions 0–3) and serial region (4–67) are
/// interpreted by fixed index, while the measurement/setting body is read by the
/// <c>telegram_place</c> indexes declared in each camera's JSON templates.
/// </summary>
public sealed class TelegramParser
{
    private const char Delimiter = ',';

    private readonly ILogService _log;

    public TelegramParser(ILogService log) => _log = log;

    /// <summary>
    /// Parse a single raw telegram line (without the trailing carriage return)
    /// into a <see cref="ParsedTelegram"/>. Returns null for empty lines or lines
    /// too short to contain a header.
    /// </summary>
    public ParsedTelegram? ParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var line = raw.Trim('\r', '\n');
        if (line.Length == 0)
            return null;

        var fields = line.Split(Delimiter);
        if (fields.Length <= ParsedTelegram.PosSignal)
        {
            _log.Debug("Telegram too short to contain a header ({Count} fields): {Raw}", fields.Length, line);
            return null;
        }

        var signalRaw = fields[ParsedTelegram.PosSignal];
        var signal = ParseSignal(signalRaw);

        // Operating mode lives at position 3 only for Results/Diagnostic telegrams;
        // in Settings telegrams that position is already a limit value.
        var mode = CameraOperatingMode.Unknown;
        if (signal is TelegramSignal.Results or TelegramSignal.Diagnostic)
            mode = ParseMode(fields.Length > ParsedTelegram.PosMode ? fields[ParsedTelegram.PosMode] : string.Empty);

        // Serials are only present in Results/Diagnostic telegrams.
        var serial1 = string.Empty;
        var serial2 = string.Empty;
        if (signal is TelegramSignal.Results or TelegramSignal.Diagnostic)
        {
            serial1 = ConcatRange(fields, ParsedTelegram.Serial1Start, ParsedTelegram.Serial1End);
            serial2 = ConcatRange(fields, ParsedTelegram.Serial2Start, ParsedTelegram.Serial2End);
        }

        return new ParsedTelegram
        {
            Fields = fields,
            ControllerName = fields[ParsedTelegram.PosController],
            Version = fields.Length > ParsedTelegram.PosVersion ? fields[ParsedTelegram.PosVersion] : string.Empty,
            Signal = signal,
            RawSignal = signalRaw,
            Mode = mode,
            Serial1 = serial1,
            Serial2 = serial2,
        };
    }

    /// <summary>Extract all measurement samples described by the result template.</summary>
    public IReadOnlyList<MeasurementSample> ExtractMeasurements(ParsedTelegram telegram, ResultTemplateFile template)
    {
        var samples = new List<MeasurementSample>(template.Measurements.Count);
        var missing = 0;

        foreach (var entry in template.Measurements)
        {
            var raw = telegram.Field(entry.TelegramPlace);
            if (raw is null)
            {
                missing++;
                continue;
            }

            raw = raw.Trim();
            var isResult = string.Equals(entry.Type, "Result", StringComparison.OrdinalIgnoreCase);

            samples.Add(new MeasurementSample
            {
                VariableName = entry.VariableName,
                DisplayName = entry.DisplayName,
                TelegramPlace = entry.TelegramPlace,
                VarType = entry.Type,
                ParameterSet = entry.ParameterSet,
                ModuleRef = entry.ModuleRef,
                FeatureGroup = entry.FeatureGroup,
                ResultStatus = isResult ? TryParseInt(raw) : null,
                Value = isResult ? null : TryParseDouble(raw),
                RawField = raw,
            });
        }

        if (missing > 0)
            _log.Debug("{Camera}: {Missing} measurement field(s) missing from telegram (length {Len}).",
                template.Camera, missing, telegram.Fields.Length);

        return samples;
    }

    /// <summary>Extract all Min/Max limits described by the settings template.</summary>
    public IReadOnlyList<SettingSample> ExtractSettings(ParsedTelegram telegram, SettingsTemplateFile template)
    {
        var samples = new List<SettingSample>(template.Settings.Count);

        foreach (var entry in template.Settings)
        {
            var raw = telegram.Field(entry.TelegramPlace);
            if (raw is null)
                continue;

            raw = raw.Trim();
            var value = TryParseDouble(raw);
            if (value is null)
                continue;

            samples.Add(new SettingSample
            {
                SettingName = entry.SettingName,
                TelegramPlace = entry.TelegramPlace,
                ParameterSet = entry.ParameterSet,
                LimitType = entry.LimitType,
                Value = value.Value,
                RawField = raw,
            });
        }

        return samples;
    }

    private static TelegramSignal ParseSignal(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "results" => TelegramSignal.Results,
        "settings" => TelegramSignal.Settings,
        "diagnostic" => TelegramSignal.Diagnostic,
        _ => TelegramSignal.Unknown,
    };

    private static CameraOperatingMode ParseMode(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "normal" => CameraOperatingMode.Normal,
        "msa1" => CameraOperatingMode.Msa1,
        "msa3" => CameraOperatingMode.Msa3,
        "limitsample" => CameraOperatingMode.LimitSample,
        _ => CameraOperatingMode.Unknown,
    };

    /// <summary>
    /// Join a contiguous range of fields (the serial region) into a single string.
    /// Works whether the serial occupies one field with empty padding or one field
    /// per character. Out-of-range indexes are skipped.
    /// </summary>
    private static string ConcatRange(string[] fields, int start, int endExclusive)
    {
        if (start >= fields.Length)
            return string.Empty;

        var end = Math.Min(endExclusive, fields.Length);
        var combined = string.Concat(fields[start..end]);
        return combined.Trim();
    }

    private static int? TryParseInt(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? TryParseDouble(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
