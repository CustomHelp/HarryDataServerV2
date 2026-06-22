using System.Globalization;

namespace HarryDataServer.Models;

/// <summary>One measurement line of an MSA PDF report (SOW §3.2.1).</summary>
public sealed class MsaReportRow
{
    public string Measurement { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public string Actual { get; init; } = string.Empty;

    /// <summary>The metric column: "Cg / Cgk" for MSA1, "%P/T" for MSA3, blank for LimitSample.</summary>
    public string Metric { get; init; } = string.Empty;

    public bool Passed { get; init; }
}

/// <summary>
/// Snapshot of one evaluated MSA run, ready to be rendered as the two PDF reports
/// (all results + failures only) defined in SOW §3.2.1.
/// </summary>
public sealed class MsaReportData
{
    public string Module { get; init; } = string.Empty;
    public string TestType { get; init; } = string.Empty; // MSA1 / MSA3 / LimitSample
    public string Controller { get; init; } = string.Empty;
    public string BaseId { get; init; } = string.Empty;
    public DateTime RunAt { get; init; }
    public bool OverallPass { get; init; }
    public IReadOnlyList<MsaReportRow> Rows { get; init; } = Array.Empty<MsaReportRow>();

    /// <summary>Build the report model from a stored run (used by the on-demand UI path).</summary>
    public static MsaReportData FromRun(MsaRunDto run) => new()
    {
        Module = run.Module,
        TestType = run.MsaType.ToDbString(),
        Controller = run.Controller,
        BaseId = run.BaseId,
        RunAt = run.EvaluatedAt,
        OverallPass = run.OverallPass,
        Rows = run.Rows.Select(r => new MsaReportRow
        {
            Measurement = r.DisplayName,
            Expected = r.Expected ?? string.Empty,
            Actual = r.Actual ?? FormatActual(r),
            Metric = MetricFor(run.MsaType, r),
            Passed = r.Passed,
        }).ToList(),
    };

    private static string FormatActual(MsaResultRow r) =>
        r.Cg?.ToString("0.###", CultureInfo.InvariantCulture)
        ?? r.PctTolerance?.ToString("0.###", CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static string MetricFor(MsaType type, MsaResultRow r) => type switch
    {
        MsaType.Msa1 => $"Cg {Fmt(r.Cg)} / Cgk {Fmt(r.Cgk)}",
        MsaType.Msa3 => $"%P/T {Fmt(r.PctTolerance)}",
        _ => string.Empty,
    };

    private static string Fmt(double? v) =>
        v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
}
