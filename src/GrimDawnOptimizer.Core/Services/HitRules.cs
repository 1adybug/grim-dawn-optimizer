namespace GrimDawnOptimizer.Core.Services;

internal sealed record HitOutcome(double Probability, double Multiplier, bool IsCritical = false);

/// <summary>Continuous roll intervals from the current combat guide, including misses.</summary>
internal sealed class HitDistribution
{
    public required IReadOnlyList<HitOutcome> Outcomes { get; init; }
    public double HitChance => Outcomes.Where(x => x.Multiplier > 0).Sum(x => x.Probability);
    // The report's critical chance is conditional on a landed hit.
    public double CritChance => HitChance > 0
        ? Outcomes.Where(x => x.IsCritical).Sum(x => x.Probability) / HitChance : 0;
    public double MeanMultiplier => Outcomes.Sum(x => x.Probability * x.Multiplier);

    public double Expected(Func<double, double> onHit) =>
        Outcomes.Where(x => x.Multiplier > 0).Sum(x => x.Probability * onHit(x.Multiplier));

    public HitOutcome Sample(Random rng)
    {
        var roll = rng.NextDouble();
        foreach (var outcome in Outcomes)
        {
            roll -= outcome.Probability;
            if (roll < 0) return outcome;
        }
        return Outcomes[^1];
    }

    public static HitDistribution Guaranteed { get; } = new() { Outcomes = [new(1, 1)] };
}

internal static class HitRules
{
    internal static double ProbabilityToHit(double oa, double da)
    {
        oa = Math.Max(1, oa);
        da = Math.Max(1, da);
        return Math.Max(55, 315 * oa / (3.5 * oa + da) + .02275 * (oa - da) + 20);
    }

    internal static HitDistribution Resolve(double oa, double da, double criticalDamagePercent = 0) =>
        FromProbabilityToHit(ProbabilityToHit(oa, da), criticalDamagePercent);

    internal static HitDistribution FromProbabilityToHit(double pth, double criticalDamagePercent = 0)
    {
        pth = Math.Max(55, pth);
        var denominator = Math.Max(100, pth);
        var outcomes = new List<HitOutcome>();
        if (pth < 100) outcomes.Add(new((100 - pth) / 100, 0));
        outcomes.Add(new(Math.Min(90, pth) / denominator, pth < 70 ? pth / 70 : 1));
        var boundaries = new[] { 90d, 105, 120, 130, 135, double.PositiveInfinity };
        var multipliers = new[] { 1.1, 1.2, 1.3, 1.4, 1.5 };
        for (var i = 0; i < multipliers.Length; i++)
        {
            var width = Math.Max(0, Math.Min(pth, boundaries[i + 1]) - boundaries[i]);
            if (width > 0) outcomes.Add(new(width / denominator,
                Math.Max(1, multipliers[i] + criticalDamagePercent / 100), true));
        }
        return new() { Outcomes = outcomes };
    }
}
