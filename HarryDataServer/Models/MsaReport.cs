using System.Globalization;
using HarryDataServer.Services;

namespace HarryDataServer.Models;

/// <summary>One measurement line of an MSA PDF report (SOW §3.2.1 + task B: the numbers behind the verdict).</summary>
public sealed class MsaReportRow
{
    /// <summary>The camera the measurement belongs to (disambiguates KF1/KF3 within a module run).</summary>
    public string Controller { get; init; } = string.Empty;

    public string Measurement { get; init; } = string.Empty;

    /// <summary>Number of measured values used.</summary>
    public int N { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }

    /// <summary>Reference value xm (MSA1); null when not applicable.</summary>
    public double? Reference { get; init; }

    /// <summary>Tolerance USL−LSL used; null/0 when no limits stored.</summary>
    public double? Tolerance { get; init; }

    public string Expected { get; init; } = string.Empty;
    public string Actual { get; init; } = string.Empty;

    /// <summary>The metric column: "Cg / Cgk" for MSA1, "%P/T" for MSA3, blank for LimitSample.</summary>
    public string Metric { get; init; } = string.Empty;

    /// <summary>Applied pass criterion, e.g. "Cgk ≥ 1.33".</summary>
    public string Criterion { get; init; } = string.Empty;

    /// <summary>Plain-text reason on FAIL / not-evaluated (never blank when not a clean pass).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether this row was actually assessed against its criterion.</summary>
    public bool Evaluated { get; init; }

    public bool Passed { get; init; }
}

/// <summary>
/// Snapshot of one evaluated MSA run, ready to be rendered as the two PDF reports
/// (all results + failures only) defined in SOW §3.2.1, plus the head context (task B3) and the
/// overall verdict incl. INVALID (task 2).
/// </summary>
public sealed class MsaReportData
{
    public string Module { get; init; } = string.Empty;
    public string TestType { get; init; } = string.Empty; // MSA1 / MSA3 / LimitSample
    public string Controller { get; init; } = string.Empty;
    public string BaseId { get; init; } = string.Empty;
    public DateTime RunAt { get; init; }

    /// <summary>Back-compat convenience: true only when <see cref="Verdict"/> is Pass.</summary>
    public bool OverallPass { get; init; }

    /// <summary>Overall verdict incl. INVALID (never a silent PASS, task 2).</summary>
    public MsaVerdict Verdict { get; init; } = MsaVerdict.Invalid;

    /// <summary>Plain-text reason for a FAIL/INVALID verdict (empty on PASS).</summary>
    public string VerdictReason { get; init; } = string.Empty;

    /// <summary>Cameras that produced no real judgement in the run (task 4), one line each.</summary>
    public IReadOnlyList<string> ControllerWarnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<MsaReportRow> Rows { get; init; } = Array.Empty<MsaReportRow>();

    /// <summary>Explicit output folder for the PDFs (report path with fallback already resolved);
    /// when null the PDF service resolves it from the config itself.</summary>
    public string? OutputDirectory { get; init; }

    // --- Head context (task B3) ---
    public int PartCount { get; init; }
    public int LoopCount { get; init; }
    public DateTime FromTime { get; init; }
    public DateTime ToTime { get; init; }

    /// <summary>The applied pass criterion for this run type (shown in the head).</summary>
    public string Criterion { get; init; } = string.Empty;

    /// <summary>Full path of the reference file used (whether or not it exists).</summary>
    public string ReferenceFile { get; init; } = string.Empty;

    /// <summary>Last-modified time of the reference file, or null if it does not exist.</summary>
    public DateTime? ReferenceFileModified { get; init; }

    /// <summary>Build the report model from a stored run (used by the on-demand UI path). Recomputes
    /// the overall verdict from the stored per-row Evaluated/Passed flags so the UI never shows a
    /// vacuous PASS either. RunAt is taken from the BaseID timestamp so the file name/folder match
    /// the auto-generated report (the UI then opens the original, fully-headed PDF).</summary>
    public static MsaReportData FromRun(MsaRunDto run)
    {
        var rows = run.Rows.Select(r => new MsaReportRow
        {
            Controller = r.Controller,
            Measurement = r.DisplayName,
            N = r.N,
            Mean = r.Mean,
            StdDev = r.StdDev,
            Reference = r.ReferenceValue,
            Tolerance = r.Tolerance,
            Expected = r.Expected ?? string.Empty,
            Actual = r.Actual ?? FormatActual(r),
            Metric = MetricFor(run.MsaType, r),
            Criterion = r.Criterion,
            Reason = r.Reason,
            Evaluated = r.Evaluated,
            Passed = r.Passed,
        }).ToList();

        // Recompute the verdict from the stored rows (same rule as the live evaluation).
        var proxies = run.Rows.Select(r => new MsaMeasurementResult
        {
            Evaluated = r.Evaluated,
            ExpectedReject = string.Equals(r.Expected, "reject", StringComparison.OrdinalIgnoreCase),
            Passed = r.Passed,
        }).ToList();
        var (verdict, reason) = MsaEvaluationText.OverallVerdict(run.MsaType, referenceLoaded: true, proxies);

        var runAt = global::HarryDataServer.Models.BaseId.TryGetTimestamp(run.BaseId, out var dt) ? dt : run.EvaluatedAt;

        return new MsaReportData
        {
            Module = run.Module,
            TestType = run.MsaType.ToDbString(),
            Controller = run.Controller,
            BaseId = run.BaseId,
            RunAt = runAt,
            OverallPass = verdict == MsaVerdict.Pass,
            Verdict = verdict,
            VerdictReason = reason,
            Criterion = run.Rows.Select(r => r.Criterion).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty,
            Rows = rows,
        };
    }

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
