namespace HarryDataServer.Services;

/// <summary>
/// Pure MSA statistics (CLAUDE.md section 7). No I/O — fully unit-testable.
/// </summary>
public static class MsaCalculator
{
    /// <summary>Pass threshold for Cg and Cgk (MSA1).</summary>
    public const double CapabilityThreshold = 1.33;

    /// <summary>Pass threshold for %Tolerance (MSA3), in percent.</summary>
    public const double ToleranceThresholdPct = 20.0;

    public sealed record Msa1Result(double Cg, double Cgk, bool Passed);

    public sealed record Msa3Result(double PctTolerance, bool Passed);

    /// <summary>Arithmetic mean.</summary>
    public static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }

    /// <summary>Sample standard deviation (n-1).</summary>
    public static double SampleStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = Mean(values);
        double ss = 0;
        foreach (var v in values) ss += (v - mean) * (v - mean);
        return Math.Sqrt(ss / (values.Count - 1));
    }

    /// <summary>
    /// MSA1 (50 measurements of one part). <paramref name="tolerance"/> = USL − LSL,
    /// <paramref name="referenceValue"/> = xm. Cg = 0.20·T/(6σ),
    /// Cgk = (0.20·T − |x̄ − xm|)/(6σ). Both must be ≥ 1.33.
    /// </summary>
    public static Msa1Result Msa1(IReadOnlyList<double> values, double tolerance, double referenceValue)
    {
        var sigma = SampleStdDev(values);
        if (sigma <= 0 || tolerance <= 0)
            return new Msa1Result(0, 0, false);

        var mean = Mean(values);
        var cg = 0.20 * tolerance / (6.0 * sigma);
        var cgk = (0.20 * tolerance - Math.Abs(mean - referenceValue)) / (6.0 * sigma);
        var passed = cg >= CapabilityThreshold && cgk >= CapabilityThreshold;
        return new Msa1Result(cg, cgk, passed);
    }

    /// <summary>
    /// MSA3 (b parts × n repeated measurements). For each part i with mean x̄i:
    /// SumSquares = ΣΣ(x̄i − xij)², DoF = parts·(measPerPart − 1),
    /// %Tolerance = 6·√(SumSquares/DoF)/T. Must be ≤ 20%.
    /// </summary>
    public static Msa3Result Msa3(IReadOnlyList<IReadOnlyList<double>> parts, double tolerance)
    {
        if (parts.Count == 0 || tolerance <= 0)
            return new Msa3Result(0, false);

        double sumSquares = 0;
        var dof = 0;
        foreach (var part in parts)
        {
            if (part.Count < 2)
                continue;
            var mean = Mean(part);
            foreach (var x in part)
                sumSquares += (mean - x) * (mean - x);
            dof += part.Count - 1;
        }

        if (dof <= 0)
            return new Msa3Result(0, false);

        var pct = 6.0 * Math.Sqrt(sumSquares / dof) / tolerance * 100.0;
        return new Msa3Result(pct, pct <= ToleranceThresholdPct);
    }

    /// <summary>
    /// LimitSample: every prepared error must be rejected. Given pairs of
    /// (shouldFail, wasRejected), passes only if all shouldFail entries were rejected.
    /// </summary>
    public static bool LimitSamplePassed(IEnumerable<(bool ShouldFail, bool WasRejected)> entries)
    {
        var any = false;
        foreach (var (shouldFail, wasRejected) in entries)
        {
            if (!shouldFail)
                continue;
            any = true;
            if (!wasRejected)
                return false;
        }
        return any; // must have at least one prepared error, all rejected
    }
}
