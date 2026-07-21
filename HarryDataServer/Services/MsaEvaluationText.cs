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

    /// <summary>Reason/warning when a controller produced no real OK/NOK judgement in the run (only
    /// status 2 "not evaluated" / −1 / −2), so its measurements cannot pass or fail (task 4).</summary>
    public static string CameraDidNotJudge =>
        "camera did not evaluate (only status 2/−1) — check program/mode";

    /// <summary>
    /// Overall run verdict (task 2). Never a silent PASS: returns <see cref="MsaVerdict.Invalid"/>
    /// with a plain reason when nothing could be judged. A LimitSample PASS additionally requires at
    /// least one expected error (prepared reject) to have been actually checked.
    /// </summary>
    public static (MsaVerdict Verdict, string Reason) OverallVerdict(
        MsaType type, bool referenceLoaded, IReadOnlyList<MsaMeasurementResult> results)
    {
        if (results.Count == 0)
            return (MsaVerdict.Invalid, "no measurements were received for this run");

        var evaluated = results.Where(r => r.Evaluated).ToList();
        if (evaluated.Count == 0)
        {
            var why = type switch
            {
                MsaType.LimitSample => referenceLoaded
                    ? "no measurement had a matching reference entry, or the camera did not judge"
                    : "reference file missing — no expected pass/fail defined",
                MsaType.Msa1 or MsaType.Msa3 =>
                    "no measurement could be evaluated (no tolerance/limits — is the settings table empty? — or too few values)",
                _ => "no measurement could be evaluated",
            };
            return (MsaVerdict.Invalid, $"{type.ToDbString()}: {why}");
        }

        if (type == MsaType.LimitSample && !evaluated.Any(r => r.ExpectedReject))
            return (MsaVerdict.Invalid,
                "LimitSample: no prepared error in the reference to verify (need ≥ 1 expected reject)");

        var failed = evaluated.Count(r => !r.Passed);
        if (failed > 0)
            return (MsaVerdict.Fail, $"{failed} of {evaluated.Count} evaluated measurement(s) failed");

        return (MsaVerdict.Pass, string.Empty);
    }

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

    /// <summary>
    /// Overall LimitSample verdict from the per-part results (task A/2). INVALID when there are no
    /// references at all, when a run part has no reference, when nothing was evaluated, or when no
    /// prepared error (ShouldFail) was checked — never a vacuous PASS. PASS only if every evaluated
    /// prepared error was detected.
    /// </summary>
    public static (MsaVerdict Verdict, string Reason) LimitSampleOverall(
        bool referencesExist, bool anyPartUnreferenced,
        IReadOnlyList<MsaMeasurementResult> results, string referencesFolderForMessage)
    {
        if (!referencesExist)
            return (MsaVerdict.Invalid, $"no LimitSample references found in {referencesFolderForMessage}");
        if (anyPartUnreferenced)
            return (MsaVerdict.Invalid, "one or more parts in the run have no LimitSample reference");

        var evaluated = results.Where(r => r.Evaluated).ToList();
        if (evaluated.Count == 0)
            return (MsaVerdict.Invalid, "no measurement could be evaluated (camera did not judge?)");
        if (!evaluated.Any(r => r.ExpectedReject))
            return (MsaVerdict.Invalid, "no prepared error (ShouldFail) to verify");

        var failed = evaluated.Count(r => !r.Passed);
        return failed > 0
            ? (MsaVerdict.Fail, $"{failed} of {evaluated.Count} evaluated measurement(s) failed")
            : (MsaVerdict.Pass, string.Empty);
    }

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? string.Empty : string.Join("; ", list);
    }
}
