namespace HarryDataServer.Services;

/// <summary>
/// Best-match of an MSA1 part (a fake-DMC group) to a reference part, by comparing the part's measured
/// means to each candidate reference's xm values (task C4). Pure / no I/O — unit-testable.
///
/// Metric: a measurement is a HIT when |mean − xm| ≤ <see cref="HitFraction"/>·tolerance; the best
/// reference has the most hits, ties broken by the smallest normalised total deviation
/// Σ|mean−xm|/tolerance over the shared measurements. A match is PLAUSIBLE only when the hit ratio
/// reaches <see cref="PlausibleHitRatio"/>; two references with equal hits and near-equal deviation
/// are flagged AMBIGUOUS.
/// </summary>
public static class Msa1Matcher
{
    /// <summary>A measurement counts as a hit when |mean − xm| ≤ f·tolerance.</summary>
    public const double HitFraction = 0.10;

    /// <summary>Minimum hits/comparable ratio for the best match to be considered plausible.</summary>
    public const double PlausibleHitRatio = 0.5;

    /// <summary>Relative deviation gap below which a tied-hits runner-up makes the match ambiguous.</summary>
    public const double AmbiguityRelativeGap = 0.05;

    public sealed record Candidate(string Label, string File, IReadOnlyDictionary<string, double> Values);

    public sealed record MatchResult(
        Candidate? Best, int Hits, int Comparable, double NormDeviation, bool Plausible, bool Ambiguous)
    {
        public double HitRatio => Comparable > 0 ? (double)Hits / Comparable : 0;
        public double Score => HitRatio; // 0..1, shown in the report/UI
    }

    private sealed record Scored(Candidate Candidate, int Hits, int Comparable, double NormDeviation);

    public static MatchResult Match(
        IReadOnlyDictionary<string, double> partMeans,
        IReadOnlyDictionary<string, double> tolerances,
        IReadOnlyList<Candidate> candidates)
    {
        var scored = new List<Scored>();
        foreach (var c in candidates)
        {
            var hits = 0;
            var comparable = 0;
            var normDev = 0.0;
            foreach (var (name, xm) in c.Values)
            {
                if (!partMeans.TryGetValue(name, out var mean))
                    continue;
                if (!tolerances.TryGetValue(name, out var tol) || tol <= 0)
                    continue;
                comparable++;
                var dev = Math.Abs(mean - xm);
                normDev += dev / tol;
                if (dev <= HitFraction * tol)
                    hits++;
            }
            if (comparable > 0)
                scored.Add(new Scored(c, hits, comparable, normDev));
        }

        if (scored.Count == 0)
            return new MatchResult(null, 0, 0, 0, Plausible: false, Ambiguous: false);

        scored.Sort((a, b) => a.Hits != b.Hits
            ? b.Hits.CompareTo(a.Hits)              // more hits first
            : a.NormDeviation.CompareTo(b.NormDeviation)); // then smaller deviation

        var best = scored[0];
        var plausible = best.Comparable > 0 && (double)best.Hits / best.Comparable >= PlausibleHitRatio;

        var ambiguous = false;
        if (scored.Count > 1)
        {
            var second = scored[1];
            var denom = Math.Max(best.NormDeviation, 1e-9);
            var relGap = Math.Abs(second.NormDeviation - best.NormDeviation) / denom;
            ambiguous = second.Hits == best.Hits && relGap <= AmbiguityRelativeGap;
        }

        return new MatchResult(best.Candidate, best.Hits, best.Comparable, best.NormDeviation, plausible, ambiguous);
    }
}
