namespace HarryDataServer.Models;

/// <summary>
/// One parsed Min/Max limit from a "Settings" telegram, matched to its template
/// definition. Persisted to the <c>settings</c> history table in Phase 5.
/// </summary>
public sealed class SettingSample
{
    public required string SettingName { get; init; }
    public int TelegramPlace { get; init; }
    public int ParameterSet { get; init; }

    /// <summary>"Min" or "Max".</summary>
    public required string LimitType { get; init; }

    public double Value { get; init; }
    public string RawField { get; init; } = string.Empty;
}
