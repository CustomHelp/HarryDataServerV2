namespace HarryDataServer.Models;

/// <summary>
/// A camera telegram split into its comma-separated fields. Results/Settings telegrams
/// share the header (tokens 0–2) + serial region (3–66); a Diagnostic telegram has a
/// different layout (serials first, the literal word "Diagnostic" at ~token 65). The
/// measurement/setting body is read by index via the JSON templates, so the raw
/// <see cref="Fields"/> array is preserved.
/// </summary>
public sealed class ParsedTelegram
{
    /// <summary>Telegram header position indexes (identical for all telegram types).</summary>
    public const int PosController = 0;
    public const int PosVersion = 1;
    public const int PosSignal = 2;

    /// <summary>
    /// Serial-number region (confirmed from the live Keyence "Datenausgabe" config, CLAUDE.md §4):
    /// the camera emits each serial as 32 separate comma-tokens. Serial1 occupies tokens 3–34
    /// (immediately after the signal word), Serial2 tokens 35–66.
    /// </summary>
    public const int Serial1Start = 3;
    public const int Serial1End = 35;   // exclusive → tokens 3..34 (32 fields)
    public const int Serial2Start = 35;
    public const int Serial2End = 67;   // exclusive → tokens 35..66 (32 fields)

    /// <summary>
    /// Operating-mode block: four independent boolean flags (0/1) at tokens 67–70, then the
    /// camera's overall part result (SINT) at token 71. Measurements begin at token 72.
    /// <see cref="PosModeDiagnostic"/> is independent of the operating mode (it can be set in
    /// any mode) and is INFO only — it never affects processing or routing.
    /// </summary>
    public const int PosModeDiagnostic = 67;
    public const int PosModeGoldenSample = 68;
    public const int PosModeMsa1 = 69;
    public const int PosModeMsa3 = 70;
    public const int PosTotalResult = 71;

    /// <summary>
    /// Diagnostic-telegram layout (CLAUDE.md §4) — DIFFERENT from Results/Settings: there is no
    /// version field, the serials come first (Serial1 tokens 1–32, Serial2 tokens 33–64) and the
    /// literal word "Diagnostic" sits at token 65, followed by a label and arbitrary values. The
    /// telegram is detected by scanning for the "Diagnostic" token, not by position; these
    /// constants give the fixed serial ranges once it is known to be a diagnostic telegram.
    /// </summary>
    public const int DiagSerial1Start = 1;
    public const int DiagSerial1End = 33;   // exclusive → tokens 1..32 (32 fields)
    public const int DiagSerial2Start = 33;
    public const int DiagSerial2End = 65;   // exclusive → tokens 33..64 (32 fields)

    /// <summary>
    /// Serial1 is transmitted in a 32-char field but only the first 22 characters are
    /// meaningful (SPS agreement); the rest is padding. The parser truncates Serial1 to this
    /// length so the stored value matches the 22-char Field 1 of the image filename (§4/§11).
    /// Serial2 keeps its full 32 chars (needed for DMC uniqueness in MSA).
    /// </summary>
    public const int Serial1MaxLength = 22;

    public required string[] Fields { get; init; }

    /// <summary>The original telegram line (without the trailing carriage return).</summary>
    public string Raw { get; init; } = string.Empty;

    public string ControllerName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public TelegramSignal Signal { get; init; }
    public string RawSignal { get; init; } = string.Empty;

    /// <summary>
    /// Operating mode derived from the boolean flags at tokens 68–70 (CLAUDE.md §4): all flags 0
    /// → <see cref="CameraOperatingMode.Normal"/>; MSA1/MSA3/GoldenSample → the matching mode
    /// (GoldenSample maps to <see cref="CameraOperatingMode.LimitSample"/>). Drives the
    /// production-vs-MSA routing.
    /// </summary>
    public CameraOperatingMode Mode { get; init; }

    /// <summary>
    /// The <c>Mode_Diagnostic</c> flag (token 67) — independent of <see cref="Mode"/> and can be
    /// set in any mode. INFO only (shown in the camera control); it never affects processing or
    /// routing.
    /// </summary>
    public bool IsDiagnostic { get; init; }

    /// <summary>
    /// The camera's overall part result (<c>Total_Result</c>, token 71; SINT −2/−1/0/1/2). Display
    /// only — the authoritative OK/NG decision comes from the PLC at part-exit, not from here.
    /// Null on telegrams without the result block (e.g. Settings).
    /// </summary>
    public int? OverallResult { get; init; }

    /// <summary>
    /// Tokens 3–34 (32 fields, concatenated), truncated by the parser to the first
    /// <see cref="Serial1MaxLength"/> (22) meaningful characters. Normal mode: SZID (M1X/M5X) or
    /// Virtual Serial (M2X). MSA modes: the BaseID (14 chars) followed by the 3-digit loop counter
    /// (split via <see cref="BaseId.TrySplitRun"/>). Empty if the telegram is too short (e.g. Settings).
    /// </summary>
    public string Serial1 { get; init; } = string.Empty;

    /// <summary>
    /// Tokens 35–66 (32 fields, concatenated), full 32 chars: Virtual Serial (M20/M21) in Normal
    /// mode; the DMC of the test part in MSA modes (kept at full width for DMC uniqueness).
    /// </summary>
    public string Serial2 { get; init; } = string.Empty;

    /// <summary>
    /// For a Diagnostic telegram, the token index of the literal "Diagnostic" word (the start of
    /// the raw payload: word + label + values, dumped verbatim to the diagnostic CSV). −1 for
    /// Results/Settings telegrams. See <see cref="TelegramSignal.Diagnostic"/>.
    /// </summary>
    public int DiagnosticStart { get; init; } = -1;

    /// <summary>Whether the telegram came from an MSA / LimitSample run.</summary>
    public bool IsMsa => Mode is CameraOperatingMode.Msa1 or CameraOperatingMode.Msa3 or CameraOperatingMode.LimitSample;

    /// <summary>
    /// True when this is a Results telegram whose Serial1 (SZID) is missing or all-zero. The
    /// controller produced a bad telegram and the data must not be trusted (CLAUDE.md §4): it is
    /// dropped from the DB pipeline and surfaced as <c>NoSerial</c> in the camera control. Only
    /// Results telegrams are checked — Settings/Diagnostic have their own paths. Uses the
    /// already-parsed (concatenated + 22-char-truncated) <see cref="Serial1"/>.
    /// </summary>
    public bool IsNoSerial => Signal == TelegramSignal.Results && IsBlankOrAllZero(Serial1);

    /// <summary>True when the string is empty/whitespace or consists solely of '0' characters.</summary>
    private static bool IsBlankOrAllZero(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return true;
        foreach (var c in s)
            if (c != '0')
                return false;
        return true;
    }

    /// <summary>Safe field accessor; returns null when the index is out of range.</summary>
    public string? Field(int index) => index >= 0 && index < Fields.Length ? Fields[index] : null;
}
