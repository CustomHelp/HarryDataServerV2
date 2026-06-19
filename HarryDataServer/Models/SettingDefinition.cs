namespace HarryDataServer.Models;

/// <summary>
/// A row of the <c>setting_definitions</c> table — the meaning of a single
/// telegram position in a camera's "Settings" telegram (a Min or Max limit).
/// </summary>
public sealed class SettingDefinition
{
    public int Id { get; set; }
    public int CameraId { get; set; }
    public int TelegramPlace { get; set; }
    public string SettingName { get; set; } = string.Empty;
    public int ParameterSet { get; set; }

    /// <summary>"Min" or "Max".</summary>
    public string LimitType { get; set; } = string.Empty;

    /// <summary>Returns true if the mutable fields differ from <paramref name="other"/>.</summary>
    public bool DiffersFrom(SettingDefinition other) =>
        TelegramPlace != other.TelegramPlace ||
        ParameterSet != other.ParameterSet ||
        !string.Equals(LimitType, other.LimitType, StringComparison.Ordinal);
}
