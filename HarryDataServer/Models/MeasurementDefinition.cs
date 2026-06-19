namespace HarryDataServer.Models;

/// <summary>
/// A row of the <c>measurement_definitions</c> table — the meaning of a single
/// telegram position for a camera, with historical validity tracking
/// (<see cref="EffectiveFrom"/>/<see cref="EffectiveEnd"/>).
/// </summary>
public sealed class MeasurementDefinition
{
    public int Id { get; set; }
    public int CameraId { get; set; }
    public int TelegramPlace { get; set; }
    public string VariableName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Result" or "Value".</summary>
    public string VarType { get; set; } = string.Empty;

    public int ParameterSet { get; set; }
    public string ModuleRef { get; set; } = "NoRef";
    public string FeatureGroup { get; set; } = "NoGroup";

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveEnd { get; set; }

    /// <summary>True when the definition has no end date (currently in use).</summary>
    public bool IsActive => EffectiveEnd is null;

    /// <summary>
    /// Returns true if the descriptive fields differ from <paramref name="other"/>
    /// (used to decide whether a historical version change is needed).
    /// </summary>
    public bool DiffersFrom(MeasurementDefinition other) =>
        TelegramPlace != other.TelegramPlace ||
        !string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) ||
        !string.Equals(VarType, other.VarType, StringComparison.Ordinal) ||
        ParameterSet != other.ParameterSet ||
        !string.Equals(ModuleRef, other.ModuleRef, StringComparison.Ordinal) ||
        !string.Equals(FeatureGroup, other.FeatureGroup, StringComparison.Ordinal);
}
