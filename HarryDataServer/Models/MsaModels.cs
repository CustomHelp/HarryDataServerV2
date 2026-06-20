namespace HarryDataServer.Models;

/// <summary>The kind of MSA run.</summary>
public enum MsaType
{
    Unknown,
    Msa1,
    Msa3,
    LimitSample,
}

public static class MsaTypeExtensions
{
    public static MsaType FromMode(CameraOperatingMode mode) => mode switch
    {
        CameraOperatingMode.Msa1 => MsaType.Msa1,
        CameraOperatingMode.Msa3 => MsaType.Msa3,
        CameraOperatingMode.LimitSample => MsaType.LimitSample,
        _ => MsaType.Unknown,
    };

    public static string ToDbString(this MsaType type) => type switch
    {
        MsaType.Msa1 => "MSA1",
        MsaType.Msa3 => "MSA3",
        MsaType.LimitSample => "LimitSample",
        _ => "Unknown",
    };

    public static MsaType FromDbString(string value) => value switch
    {
        "MSA1" => MsaType.Msa1,
        "MSA3" => MsaType.Msa3,
        "LimitSample" => MsaType.LimitSample,
        _ => MsaType.Unknown,
    };
}

/// <summary>One MSA measurement queued for insertion into <c>msa_measurements</c>.</summary>
public sealed class PendingMsaMeasurement
{
    public required string Dmc { get; init; }
    public required string BaseId { get; init; }
    public required string ControllerName { get; init; }
    public required string VariableName { get; init; }
    public int LoopNumber { get; init; }
    public double? Value { get; init; }
    public string? MeasurementString { get; init; }
    public int? ResultStatus { get; init; }
    public MsaType MsaType { get; init; }
    public string? MsaVersion { get; init; }
    public DateTime MeasuredAt { get; init; }
}

/// <summary>Computed MSA evaluation result for one measurement (one row of <c>msa_results</c>).</summary>
public sealed class MsaMeasurementResult
{
    public int DefinitionId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public double? Cg { get; init; }
    public double? Cgk { get; init; }
    public double? PctTolerance { get; init; }

    /// <summary>LimitSample only: the prepared expectation ("reject"/"accept").</summary>
    public string? Expected { get; init; }

    /// <summary>LimitSample only: what actually happened ("rejected"/"accepted").</summary>
    public string? Actual { get; init; }

    public bool Passed { get; init; }
}
