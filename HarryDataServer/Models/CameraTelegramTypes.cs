namespace HarryDataServer.Models;

/// <summary>Signal word at telegram position 2 — selects how the body is parsed.</summary>
public enum TelegramSignal
{
    Unknown,
    Results,
    Settings,
    Diagnostic,
}

/// <summary>Operating mode at telegram position 3 (Results/Diagnostic telegrams).</summary>
public enum CameraOperatingMode
{
    Unknown,
    Normal,
    Msa1,
    Msa3,
    LimitSample,
}

/// <summary>
/// Result code carried by an <c>R_</c> field (CLAUDE.md section 4, "Result Codes").
/// </summary>
public enum MeasurementResult
{
    NotValidated = -2,
    PositionAdjustmentError = -1,
    Bad = 0,
    Good = 1,
    NotEvaluated = 2,
}

/// <summary>Connection state of a single camera client.</summary>
public enum CameraConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}
