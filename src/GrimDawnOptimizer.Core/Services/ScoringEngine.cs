using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal sealed class ScoringEngine
{
    private readonly IReadOnlyList<MetricRule> _rules;
    private readonly IReadOnlyDictionary<string, double> _benchmarks;

    public ScoringEngine(
        IReadOnlyList<MetricRule> rules,
        IReadOnlyDictionary<string, double> benchmarks)
    {
        _rules = rules;
        _benchmarks = benchmarks;
    }

    public (IReadOnlyList<double> TierScores, IReadOnlyDictionary<string, double> Contributions) Score(
        IReadOnlyDictionary<string, double> metrics)
    {
        var score = 0d;
        var contributions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
        {
            if (Math.Abs(rule.Weight) < 1e-12 || string.IsNullOrWhiteSpace(rule.Key)) continue;
            var benchmark = _benchmarks.GetValueOrDefault(rule.Key, 1);
            var normalized = BossMetricBenchmarkResolver.NormalizeValue(
                rule.Key, metrics.GetValueOrDefault(rule.Key), benchmark, rule.Direction);
            var contribution = rule.Weight * normalized;
            contributions[rule.Key] = contributions.GetValueOrDefault(rule.Key) + contribution;
            score += contribution;
        }

        return (new[] { score }, contributions);
    }

    public double PartialHeuristic(LoadoutResult result)
    {
        return result.TierScores.FirstOrDefault();
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
        var score = y.TierScores.FirstOrDefault().CompareTo(x.TierScores.FirstOrDefault());
        if (score != 0) return score;

        var xd = string.Join('|', x.Loadout.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Value.Item.Id));
        var yd = string.Join('|', y.Loadout.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Value.Item.Id));
        return string.CompareOrdinal(xd, yd);
    }
}
