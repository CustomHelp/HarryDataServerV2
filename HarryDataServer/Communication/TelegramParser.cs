using System.Globalization;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Communication;

/// <summary>
/// Parses Keyence camera telegrams (CLAUDE.md section 4). Stateless and shared by
/// all camera clients. The fixed-layout region is interpreted by index: header
/// (tokens 0–2), Serial1 (3–34), Serial2 (35–66), the four boolean mode flags
/// (67–70) and the overall result (71). The measurement/setting body (token 72+)
/// is read by the <c>telegram_place</c> indexes declared in each camera's JSON
/// templates.
/// </summary>
public sealed class TelegramParser
{
    private const char Delimiter = ',';

    /// <summary>The literal token that identifies a Diagnostic telegram (CLAUDE.md §4).</summary>
    private const string DiagnosticSignalWord = "Diagnostic";

    private readonly ILogService _log;

    public TelegramParser(ILogService log) => _log = log;

    /// <summary>
    /// True when a line is a Keyence command reply rather than a measurement
    /// telegram: the version-request reply (e.g. "MR,1.1") or an error reply
    /// ("ER" / "ER,..."). These are keepalive traffic and must not be parsed.
    /// Measurement telegrams start with the controller name ("M50_...", i.e. 'M'
    /// followed by a digit), so they never collide with the "MR,"/"ER" prefixes.
    /// </summary>
    public bool IsKeepAliveReply(string text)
    {
        var t = text.Trim('\r', '\n', ' ', '\t');
        return t.StartsWith("MR,", StringComparison.Ordinal)
            || t.Equals("ER", StringComparison.Ordinal)
            || t.StartsWith("ER,", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parse a single raw telegram line (without the trailing carriage return)
    /// into a <see cref="ParsedTelegram"/>. Returns null for empty lines, keepalive
    /// replies, or lines too short to contain a header.
    /// </summary>
    public ParsedTelegram? ParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var line = raw.Trim('\r', '\n');
        if (line.Length == 0)
            return null;

        // Defense in depth: never interpret a keepalive reply as a telegram.
        if (IsKeepAliveReply(line))
            return null;

        var fields = line.Split(Delimiter);
        if (fields.Length <= ParsedTelegram.PosSignal)
        {
            _log.Debug("Telegram too short to contain a header ({Count} fields): {Raw}", fields.Length, line);
            return null;
        }

        // A Diagnostic telegram has a DIFFERENT layout (CLAUDE.md §4): no version field, serials
        // first, and the literal word "Diagnostic" at ~token 65 — NOT at the signal position.
        // Detect it by scanning the tokens (Results/Settings bodies are serials/numbers and never
        // contain that word, so there are no false positives) and handle it before the normal
        // signal-word dispatch so it can never be misread as Results.
        var diagIndex = FindDiagnosticToken(fields);
        if (diagIndex >= 0)
            return BuildDiagnostic(fields, line, diagIndex);

        var signalRaw = fields[ParsedTelegram.PosSignal];
        var signal = ParseSignal(signalRaw);

        // The serial + mode-flag + result block is present only on Results/Diagnostic
        // telegrams; Settings telegrams carry limit values directly from token 3 onward.
        var serial1 = string.Empty;
        var serial2 = string.Empty;
        var mode = CameraOperatingMode.Unknown;
        var isDiagnostic = false;
        int? overallResult = null;
        if (signal is TelegramSignal.Results or TelegramSignal.Diagnostic)
        {
            // Serial1 (tokens 3–34) is transmitted as 32 chars, right-padded with '0'. Normalise
            // here at the single parse chokepoint so the stored value drops the controller padding
            // and matches the unpadded serial the SPS delivers at part-exit (Problem 1). The result
            // is also capped to the VARCHAR(22) width. Serial2 (tokens 35–66) keeps its full 32 chars.
            serial1 = SerialNumberHelper.Normalize(ConcatRange(fields, ParsedTelegram.Serial1Start, ParsedTelegram.Serial1End));
            serial2 = ConcatRange(fields, ParsedTelegram.Serial2Start, ParsedTelegram.Serial2End);

            // Four independent boolean flags at tokens 67–70 (CLAUDE.md §4): Mode_Diagnostic is
            // INFO only; the operating mode is derived from GoldenSample/MSA1/MSA3.
            isDiagnostic = ReadFlag(fields, ParsedTelegram.PosModeDiagnostic);
            mode = DeriveMode(fields, fields[ParsedTelegram.PosController]);

            // Total_Result (token 71) is the camera's overall part result (SINT) — display only.
            var resultRaw = FieldAt(fields, ParsedTelegram.PosTotalResult);
            if (resultRaw is not null)
                overallResult = TryParseInt(resultRaw.Trim());
        }

        return new ParsedTelegram
        {
            Fields = fields,
            Raw = line,
            ControllerName = fields[ParsedTelegram.PosController],
            Version = fields.Length > ParsedTelegram.PosVersion ? fields[ParsedTelegram.PosVersion] : string.Empty,
            Signal = signal,
            RawSignal = signalRaw,
            Mode = mode,
            IsDiagnostic = isDiagnostic,
            OverallResult = overallResult,
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

    /// <summary>
    /// Scan the tokens for an exact "Diagnostic" token (case-insensitive, trimmed). Returns its
    /// index, or −1 if not present. Detection is by content, not position (CLAUDE.md §4).
    /// </summary>
    private static int FindDiagnosticToken(string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (string.Equals(fields[i].Trim(), DiagnosticSignalWord, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Build a Diagnostic telegram (CLAUDE.md §4): no version field; Serial1 = tokens 1–32
    /// (truncated to 22 chars, same rule as the main pipeline); Serial2 = tokens 33–64 (full 32);
    /// the raw payload (word + label + arbitrary values) starts at <paramref name="diagIndex"/>.
    /// The operating-mode block does not apply, so <see cref="ParsedTelegram.Mode"/> is Unknown and
    /// <see cref="ParsedTelegram.IsDiagnostic"/> is false (that flag is the unrelated Mode_Diagnostic).
    /// </summary>
    private ParsedTelegram BuildDiagnostic(string[] fields, string line, int diagIndex)
    {
        var serial1 = SerialNumberHelper.Normalize(ConcatRange(fields, ParsedTelegram.DiagSerial1Start, ParsedTelegram.DiagSerial1End));
        var serial2 = ConcatRange(fields, ParsedTelegram.DiagSerial2Start, ParsedTelegram.DiagSerial2End);

        _log.Debug("{Camera}: Diagnostic telegram ({Tokens} tokens, payload from index {Index}).",
            fields[ParsedTelegram.PosController], fields.Length, diagIndex);

        return new ParsedTelegram
        {
            Fields = fields,
            Raw = line,
            ControllerName = fields[ParsedTelegram.PosController],
            Version = string.Empty,
            Signal = TelegramSignal.Diagnostic,
            RawSignal = fields[diagIndex].Trim(),
            Mode = CameraOperatingMode.Unknown,
            IsDiagnostic = false,
            OverallResult = null,
            Serial1 = serial1,
            Serial2 = serial2,
            DiagnosticStart = diagIndex,
        };
    }

    private static TelegramSignal ParseSignal(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "results" => TelegramSignal.Results,
        "settings" => TelegramSignal.Settings,
        "diagnostic" => TelegramSignal.Diagnostic,
        _ => TelegramSignal.Unknown,
    };

    /// <summary>
    /// Derive the operating mode from the three mode flags at tokens 68–70 (CLAUDE.md §4):
    /// all 0 → Normal; MSA1/MSA3/GoldenSample → the matching mode (GoldenSample → LimitSample).
    /// Only one is ever set; if more than one is set the telegram is treated as Normal and a
    /// WARNING is logged. (Mode_Diagnostic at token 67 is independent and read separately.)
    /// </summary>
    private CameraOperatingMode DeriveMode(string[] fields, string controller)
    {
        var goldenSample = ReadFlag(fields, ParsedTelegram.PosModeGoldenSample);
        var msa1 = ReadFlag(fields, ParsedTelegram.PosModeMsa1);
        var msa3 = ReadFlag(fields, ParsedTelegram.PosModeMsa3);

        var setCount = (goldenSample ? 1 : 0) + (msa1 ? 1 : 0) + (msa3 ? 1 : 0);
        if (setCount == 0)
            return CameraOperatingMode.Normal;

        if (setCount > 1)
        {
            _log.Warning(
                "{Camera}: multiple operating-mode flags set (GoldenSample={GSM}, MSA1={M1}, MSA3={M3}); treating as Normal.",
                controller, goldenSample, msa1, msa3);
            return CameraOperatingMode.Normal;
        }

        if (msa1) return CameraOperatingMode.Msa1;
        if (msa3) return CameraOperatingMode.Msa3;
        return CameraOperatingMode.LimitSample;   // GoldenSample
    }

    /// <summary>Read a boolean mode flag (0/1) at the given token; true when non-zero.</summary>
    private static bool ReadFlag(string[] fields, int index)
    {
        var raw = FieldAt(fields, index);
        return raw is not null && TryParseInt(raw.Trim()) is { } value && value != 0;
    }

    /// <summary>Safe token accessor; returns null when the index is out of range.</summary>
    private static string? FieldAt(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index] : null;

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
