namespace HarryDataServer.Models;

/// <summary>One measurement line of a stored MSA run (from <c>msa_results</c>).</summary>
public sealed class MsaResultRow
{
    public string Controller { get; init; } = string.Empty;
    public string Dmc { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public double? Cg { get; init; }
    public double? Cgk { get; init; }
    public double? PctTolerance { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }

    // Task B: the numbers behind the verdict, persisted for the report / UI.
    public int N { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }
    public double? ReferenceValue { get; init; }
    public double? Tolerance { get; init; }
    public string Criterion { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether the measurement was actually assessed against its criterion (task 2).</summary>
    public bool Evaluated { get; init; }

    /// <summary>MSA1: the best-matched reference part (label + file) and its score (task C).</summary>
    public string MatchedReference { get; init; } = string.Empty;
    public double? MatchScore { get; init; }

    public bool Passed { get; init; }

    /// <summary>Tri-state result text for the features grid: "ok" / "nicht ok" / "n.a." (task B2).</summary>
    public string ResultText => !Evaluated ? "n.a." : Passed ? "ok" : "nicht ok";
}

/// <summary>
/// One stored MSA evaluation run (all <c>msa_results</c> rows sharing a BaseID),
/// used by the MSA UI to page through historical runs per module and type.
/// </summary>
public sealed class MsaRunDto
{
    public string Module { get; init; } = string.Empty;
    public string Controller { get; init; } = string.Empty;
    public string BaseId { get; init; } = string.Empty;
    public MsaType MsaType { get; init; }
    public DateTime EvaluatedAt { get; init; }
    public IReadOnlyList<MsaResultRow> Rows { get; init; } = Array.Empty<MsaResultRow>();

    public bool OverallPass => Rows.Count > 0 && Rows.All(r => r.Passed);
}
