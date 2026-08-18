using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>
/// Converts unlike build metrics into dimensionless completion ratios against
/// the active same-level boss scenario.
/// </summary>
public static class BossMetricBenchmarkResolver
{
    private const double Epsilon = 1e-9;

    public static IReadOnlyDictionary<string, double> Resolve(
        IEnumerable<MetricRule> rules,
        CombatScenario scenario,
        IReadOnlyDictionary<string, double>? currentMetrics = null)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
            if (!string.IsNullOrWhiteSpace(rule.Key))
                result[rule.Key] = Resolve(rule.Key, scenario, currentMetrics?.GetValueOrDefault(rule.Key) ?? 0);
        return result;
    }

    public static double Resolve(string key, CombatScenario scenario, double currentValue = 0)
    {
        var duration = Math.Max(1, scenario.DurationSeconds);
        var rawIncomingDps = Math.Max(1, RawIncomingDps(scenario));
        var normalizedKey = key.Trim().ToLowerInvariant();

        if (normalizedKey == "dps")
            return Math.Max(1, scenario.EnemyHealth) / duration;
        if (normalizedKey is "effectivehealth" or "health")
            return rawIncomingDps * duration;
        if (normalizedKey is "timetokill" or "timetodeath")
            return duration;
        if (normalizedKey is "incomingdps" or "sustain" or "healthregen")
            return rawIncomingDps;
        if (normalizedKey == "offensiveability")
            return Math.Max(1, scenario.EnemyDefensiveAbility);
        if (normalizedKey == "defensiveability")
            return Math.Max(1, scenario.EnemyOffensiveAbility);
        if (normalizedKey == "armor")
            return Math.Max(1, scenario.EnemyArmor);
        if (normalizedKey == "blockamount")
            return Math.Max(1, rawIncomingDps / Math.Max(0.1, scenario.IncomingDamage.HitsPerSecond));
        if (normalizedKey == "resistancecoverage")
            return 1;
        if (IsResistance(normalizedKey))
            return 80;
        if (normalizedKey is "hitchance" or "critchance" or "attackspeed" or "castspeed" or
            "armorabsorption" or "dodgechance" or "blockchance" or "lifesteal")
            return 100;

        // Some user-added item stats have no boss-side equivalent. Keeping the
        // current equipped value as their benchmark preserves a unitless ratio
        // without silently dropping the rule.
        return Math.Abs(currentValue) > Epsilon ? Math.Abs(currentValue) : 1;
    }

    public static double NormalizeValue(string key, double value, double benchmark, MetricDirection direction)
    {
        benchmark = Math.Max(Epsilon, Math.Abs(benchmark));
        if (key.Equals("resistanceCoverage", StringComparison.OrdinalIgnoreCase))
            value = Math.Clamp(value, -1, 1);
        else if (IsResistance(key))
            value = Math.Clamp(value, -100, 80);

        if (direction == MetricDirection.Minimize)
        {
            if (value <= Epsilon) return 10;
            return Math.Clamp(benchmark / value, -10, 10);
        }

        if (double.IsPositiveInfinity(value)) return 10;
        if (double.IsNegativeInfinity(value) || double.IsNaN(value)) return -10;
        return Math.Clamp(value / benchmark, -10, 10);
    }

    public static double ResistanceCoverage(StatBlock stats)
    {
        var ratios = StatKeys.DamageTypes
            .Select(type => Math.Clamp(stats[StatKeys.Resistance(type)], -100, 80) / 80d)
            .ToArray();
        if (ratios.Length == 0) return 0;
        return ratios.Average() * 0.7 + ratios.Min() * 0.3;
    }

    public static double RawIncomingDps(CombatScenario scenario)
    {
        var incoming = scenario.IncomingDamage;
        var perHit = incoming.PhysicalPerHit + incoming.PiercePerHit + incoming.FirePerHit +
                     incoming.ColdPerHit + incoming.LightningPerHit + incoming.AcidPerHit +
                     incoming.VitalityPerHit + incoming.AetherPerHit + incoming.ChaosPerHit;
        return Math.Max(0, perHit * Math.Max(0, incoming.HitsPerSecond));
    }

    private static bool IsResistance(string key) =>
        key.EndsWith("Resistance", StringComparison.OrdinalIgnoreCase) &&
        !key.EndsWith("MaxResistance", StringComparison.OrdinalIgnoreCase);
}
