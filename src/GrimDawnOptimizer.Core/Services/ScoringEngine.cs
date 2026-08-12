using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal sealed class ScoringEngine
{
    private readonly IReadOnlyList<MetricRule> _rules;
    private readonly IReadOnlyDictionary<string, double> _baseline;
    private readonly IReadOnlyDictionary<string, double> _fallbackSteps;
    private readonly int[] _tiers;

    public ScoringEngine(
        IReadOnlyList<MetricRule> rules,
        IReadOnlyDictionary<string, double> baseline,
        IReadOnlyDictionary<string, double> fallbackSteps)
    {
        _rules = rules;
        _baseline = baseline;
        _fallbackSteps = fallbackSteps;
        _tiers = rules.Where(x => x.PriorityTier > 0).Select(x => x.PriorityTier).Distinct().Order().ToArray();
    }

    public (IReadOnlyList<double> TierScores, IReadOnlyDictionary<string, double> Violations) Score(
        IReadOnlyDictionary<string, double> metrics)
    {
        var violations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
        {
            var value = metrics.GetValueOrDefault(rule.Key);
            var step = ResolveStep(rule);
            if (rule.HardMinimum is { } min && value + 1e-9 < min)
                violations[rule.Key] = (min - value) / step;
            if (rule.HardMaximum is { } max && value - 1e-9 > max)
                violations[rule.Key] = (value - max) / step;
        }

        var scores = new List<double>(_tiers.Length);
        foreach (var tier in _tiers)
        {
            var score = 0d;
            foreach (var rule in _rules.Where(x => x.PriorityTier == tier))
            {
                var current = _baseline.GetValueOrDefault(rule.Key);
                var candidate = metrics.GetValueOrDefault(rule.Key);
                var direction = rule.Direction == MetricDirection.Maximize ? 1 : -1;
                var normalized = rule.NormalizationStep > 0 || Math.Abs(current) < 1e-9
                    ? (candidate - current) / ResolveStep(rule)
                    : (candidate - current) / Math.Abs(current) * 100d;
                score += direction * rule.Weight * normalized;
            }
            scores.Add(score);
        }

        return (scores, violations);
    }

    public double PartialHeuristic(LoadoutResult result)
    {
        var score = -result.ConstraintViolations.Values.Sum() * 1000d;
        var multiplier = 1_000_000d;
        foreach (var tier in result.TierScores)
        {
            score += tier * multiplier;
            multiplier /= 1000d;
        }
        return score;
    }

    private double ResolveStep(MetricRule rule)
    {
        if (rule.NormalizationStep > 0) return rule.NormalizationStep;
        var current = Math.Abs(_baseline.GetValueOrDefault(rule.Key));
        if (current > 1e-9) return current / 100d;
        return Math.Max(1e-9, _fallbackSteps.GetValueOrDefault(rule.Key, 1d));
    }
}

internal sealed class LoadoutResultComparer : IComparer<LoadoutResult>
{
    public static readonly LoadoutResultComparer Instance = new();

    public int Compare(LoadoutResult? x, LoadoutResult? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var validity = y.Loadout.IsValid.CompareTo(x.Loadout.IsValid);
        if (validity != 0) return validity;
        var feasibility = y.IsFeasible.CompareTo(x.IsFeasible);
        if (feasibility != 0) return feasibility;

        var violation = x.ConstraintViolations.Values.Sum().CompareTo(y.ConstraintViolations.Values.Sum());
        if (violation != 0) return violation;

        var count = Math.Max(x.TierScores.Count, y.TierScores.Count);
        for (var i = 0; i < count; i++)
        {
            var xs = i < x.TierScores.Count ? x.TierScores[i] : 0;
            var ys = i < y.TierScores.Count ? y.TierScores[i] : 0;
            var comparison = ys.CompareTo(xs);
            if (comparison != 0) return comparison;
        }

        var xd = string.Join('|', x.Loadout.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Value.Item.Id));
        var yd = string.Join('|', y.Loadout.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Value.Item.Id));
        return string.CompareOrdinal(xd, yd);
    }
}
