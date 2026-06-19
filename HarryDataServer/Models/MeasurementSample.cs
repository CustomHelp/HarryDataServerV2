namespace HarryDataServer.Models;

/// <summary>
/// One parsed entry from a "Results" telegram, matched to its template definition.
/// A template entry is either a Result (<c>R_</c>, carries <see cref="ResultStatus"/>)
/// or a Value (<c>V_</c>, carries <see cref="Value"/>). The measurement pipeline
/// (Phase 4) resolves <c>definition_id</c> and persists these.
/// </summary>
public sealed class MeasurementSample
{
    public required string VariableName { get; init; }
    public required string DisplayName { get; init; }
    public int TelegramPlace { get; init; }

    /// <summary>"Result" or "Value".</summary>
    public required string VarType { get; init; }

    public int ParameterSet { get; init; }
    public string ModuleRef { get; init; } = "NoRef";
    public string FeatureGroup { get; init; } = "NoGroup";

    /// <summary>Parsed float value for Value (<c>V_</c>) entries; null for Result entries.</summary>
    public double? Value { get; init; }

    /// <summary>Parsed result code for Result (<c>R_</c>) entries; null for Value entries.</summary>
    public int? ResultStatus { get; init; }

    /// <summary>The raw field string from the telegram (kept for diagnostics).</summary>
    public string RawField { get; init; } = string.Empty;

    public bool IsResult => string.Equals(VarType, "Result", StringComparison.OrdinalIgnoreCase);
    public bool IsValue => string.Equals(VarType, "Value", StringComparison.OrdinalIgnoreCase);
}
