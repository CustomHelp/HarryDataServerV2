using System.Globalization;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Pure text/verdict helpers for MSA evaluation (task B2): turns the raw numbers into the applied
/// pass criterion and a plain-language reason so the report/log never shows a silent 0/FAIL. No I/O,
/// no DB — fully unit-testable.
/// </summary>
public static class MsaEvaluationText
{
    /// <summary>Recommended number of repeated measurements for an MSA1 study (VDA/AIAG classic 50).</summary>
    public const int Msa1RecommendedN = 50;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The pass criterion applied for a run type (shown in the report head/column).</summary>
    public static string Criterion(MsaType type) => type switch
    {
        MsaType.Msa1 => $"Cg ≥ {MsaCalculator.CapabilityThreshold:0.00} and Cgk ≥ {MsaCalculator.CapabilityThreshold:0.00}",
        MsaType.Msa3 => $"%P/T ≤ {MsaCalculator.ToleranceThresholdPct:0} %",
        MsaType.LimitSample => "every prepared error rejected",
        _ => string.Empty,
    };

    /// <summary>The shared "no limits" reason — the most common live cause (empty settings table).</summary>
    public static string NoTolerance =>
        "no tolerance available (no Min/Max limits stored for this parameter set — request a Settings telegram)";

    /// <summary>MSA1 verdict + reason from the computed capability and context.</summary>
    public static (bool Passed, string Reason) Msa1Verdict(
        int n, double sigma, double tolerance, double cg, double cgk, bool hasReference)
    {
        if (tolerance <= 0)
            return (false, NoTolerance);
        if (n < 2)
            return (false, $"only n={n} value(s) (need ≥ 2)");
        if (sigma <= 0)
            return (false, "all values identical (σ = 0) — capability cannot be computed");

        var fails = new List<string>();
        if (cg < MsaCalculator.CapabilityThreshold)
            fails.Add($"Cg {cg.ToString("0.00", Inv)} < {MsaCalculator.CapabilityThreshold:0.00}");
        if (cgk < MsaCalculator.CapabilityThreshold)
            fails.Add($"Cgk {cgk.ToString("0.00", Inv)} < {MsaCalculator.CapabilityThreshold:0.00}");

        var notes = new List<string>();
        if (!hasReference)
            notes.Add("no reference value xm found (used 0)");
        if (n < Msa1RecommendedN)
            notes.Add($"n={n} (< recommended {Msa1RecommendedN})");

        if (fails.Count > 0)
            return (false, Join(fails.Concat(notes)));
        return (true, Join(notes)); // pass; notes are informational only
    }

    /// <summary>MSA3 verdict + reason from the computed %P/T and context.</summary>
    public static (bool Passed, string Reason) Msa3Verdict(
        int parts, int degreesOfFreedom, double tolerance, double pctTolerance)
    {
        if (tolerance <= 0)
            return (false, NoTolerance);
        if (parts < 1 || degreesOfFreedom <= 0)
            return (false, "insufficient repeated measurements (need ≥ 2 measurements per part)");
        if (pctTolerance > MsaCalculator.ToleranceThresholdPct)
            return (false, $"%P/T {pctTolerance.ToString("0.0", Inv)} % > {MsaCalculator.ToleranceThresholdPct:0} %");
        return (true, string.Empty);
    }

    /// <summary>LimitSample verdict + reason.</summary>
    public static (bool Passed, string Reason) LimitSampleVerdict(
        bool hasReference, bool shouldFail, bool wasRejected)
    {
        if (!hasReference)
            return (true, "no reference entry for this measurement (expected pass/fail undefined — not evaluated)");
        if (shouldFail && !wasRejected)
            return (false, "prepared error was NOT rejected");
        return (true, string.Empty);
    }

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? string.Empty : string.Join("; ", list);
    }
}
