namespace HarryDataServer.Models;

/// <summary>
/// A camera telegram split into its comma-separated fields, with the common
/// header (positions 0–3) and serial-number region (positions 4–67) interpreted.
/// The measurement/setting body is read by index via the JSON templates, so the
/// raw <see cref="Fields"/> array is preserved.
/// </summary>
public sealed class ParsedTelegram
{
    /// <summary>Telegram header position indexes (identical for all telegram types).</summary>
    public const int PosController = 0;
    public const int PosVersion = 1;
    public const int PosSignal = 2;
    public const int PosMode = 3;

    /// <summary>Serial-number region: positions 4–35 (32 fields) and 36–67 (32 fields).</summary>
    public const int Serial1Start = 4;
    public const int Serial1End = 36;   // exclusive
    public const int Serial2Start = 36;
    public const int Serial2End = 68;   // exclusive

    public required string[] Fields { get; init; }

    /// <summary>The original telegram line (without the trailing carriage return).</summary>
    public string Raw { get; init; } = string.Empty;

    public string ControllerName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public TelegramSignal Signal { get; init; }
    public string RawSignal { get; init; } = string.Empty;
    public CameraOperatingMode Mode { get; init; }

    /// <summary>
    /// Positions 4–35: SZID / Virtual Serial in Normal mode; in MSA modes this carries the
    /// BaseID (14 chars) followed by the 3-digit loop counter (split via
    /// <see cref="BaseId.TrySplitRun"/>). Empty if the telegram is too short (e.g. Settings).
    /// </summary>
    public string Serial1 { get; init; } = string.Empty;

    /// <summary>
    /// Positions 36–67: Virtual Serial (M20/M21) in Normal mode; the DMC of the test part
    /// in MSA modes.
    /// </summary>
    public string Serial2 { get; init; } = string.Empty;

    /// <summary>Whether the telegram came from an MSA / LimitSample run.</summary>
    public bool IsMsa => Mode is CameraOperatingMode.Msa1 or CameraOperatingMode.Msa3 or CameraOperatingMode.LimitSample;

    /// <summary>Safe field accessor; returns null when the index is out of range.</summary>
    public string? Field(int index) => index >= 0 && index < Fields.Length ? Fields[index] : null;
}
