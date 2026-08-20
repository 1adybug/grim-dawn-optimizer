using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ScoringEngineTests
{
    [Fact]
    public void WeightedBossRatiosPreferLargeSurvivalGainOverOneDps()
    {
        var scenario = new CombatScenario
        {
            EnemyHealth = 300_000,
            DurationSeconds = 10,
            IncomingDamage = new IncomingDamageProfile(
                HitsPerSecond: 1.2, PhysicalPerHit: 1200, FirePerHit: 450,
                ColdPerHit: 0, LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0,
                VitalityPerHit: 0, AetherPerHit: 0, ChaosPerHit: 0, BleedingPerHit: 0)
        };
        var rules = new[]
        {
            new MetricRule { Key = "dps", Weight = 1 },
            new MetricRule { Key = "effectiveHealth", Weight = 1 }
        };
        var scoring = new ScoringEngine(rules, BossMetricBenchmarkResolver.Resolve(rules, scenario));

        var first = scoring.Score(new Dictionary<string, double>
        {
            ["dps"] = 10_000, ["effectiveHealth"] = 9_000
        }).TierScores.Single();
        var second = scoring.Score(new Dictionary<string, double>
        {
            ["dps"] = 9_999, ["effectiveHealth"] = 10_000
        }).TierScores.Single();

        Assert.True(second > first);
        Assert.Equal(30_000, BossMetricBenchmarkResolver.Resolve("dps", scenario));
        Assert.Equal(19_800, BossMetricBenchmarkResolver.Resolve("effectiveHealth", scenario));
    }

    [Fact]
    public void ResistanceCoverageCapsAtEightyAndPenalizesLowestGap()
    {
        var capped = new StatBlock();
        var oneGap = new StatBlock();
        foreach (var type in StatKeys.DamageTypes)
        {
            capped.Set(StatKeys.Resistance(type), 120);
            oneGap.Set(StatKeys.Resistance(type), 80);
        }
        oneGap.Set(StatKeys.Resistance("chaos"), 0);

        Assert.Equal(1, BossMetricBenchmarkResolver.ResistanceCoverage(capped), 9);
        Assert.Equal(0.7 * 9 / 10d, BossMetricBenchmarkResolver.ResistanceCoverage(oneGap), 9);
    }
}
