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

    /// <summary>The camera this measurement belongs to (disambiguates the same display name across
    /// e.g. KF1/KF3 within one module run).</summary>
    public string Controller { get; init; } = string.Empty;

    /// <summary>A representative DMC (for the msa_results.dmc column); an MSA run aggregates many.</summary>
    public string Dmc { get; init; } = string.Empty;

    public double? Cg { get; init; }
    public double? Cgk { get; init; }
    public double? PctTolerance { get; init; }

    /// <summary>LimitSample only: the prepared expectation ("reject"/"accept").</summary>
    public string? Expected { get; init; }

    /// <summary>LimitSample only: what actually happened ("rejected"/"accepted").</summary>
    public string? Actual { get; init; }

    // --- Reporting context (task B): the numbers behind the verdict ---

    /// <summary>Number of measured values that went into the calculation.</summary>
    public int N { get; init; }

    /// <summary>Mean of the measured values (null when N = 0).</summary>
    public double? Mean { get; init; }

    /// <summary>Sample standard deviation of the measured values (null when N &lt; 2).</summary>
    public double? StdDev { get; init; }

    /// <summary>Reference value xm (MSA1) from the reference file; null when not applicable/known.</summary>
    public double? ReferenceValue { get; init; }

    /// <summary>Tolerance USL−LSL used (from the settings limits); null/0 when no limits are stored.</summary>
    public double? Tolerance { get; init; }

    /// <summary>The pass criterion that was applied, e.g. "Cgk ≥ 1.33" or "%P/T ≤ 20 %".</summary>
    public string Criterion { get; init; } = string.Empty;

    /// <summary>Plain-text reason — always set on FAIL/degenerate (never a silent 0/FAIL), empty on a clean pass.</summary>
    public string Reason { get; init; } = string.Empty;

    public bool Passed { get; init; }
}
