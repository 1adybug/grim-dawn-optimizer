using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public async Task ProfileRoundTripsWithStatBlocksAndSchemaVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            var profile = new OptimizationProfile { Name = "火焰测试", SearchBudgetSeconds = 7 };
            profile.Rotation.FlatDamage.Add(StatKeys.DamageFlat("fire"), 42);
            var store = new JsonProfileStore(path);

            await store.SaveAsync([profile]);
            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            Assert.Equal(4, loaded[0].SchemaVersion);
            Assert.Equal("火焰测试", loaded[0].Name);
            Assert.Equal(42, loaded[0].Rotation.FlatDamage[StatKeys.DamageFlat("fire")]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyTierProfileMigratesToWeightedResistanceCoverage()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            var legacy = new OptimizationProfile
            {
                SchemaVersion = 1,
                Scenario = new CombatScenario
                {
                    IncomingDamage = new IncomingDamageProfile(
                        HitsPerSecond: 1.2, PhysicalPerHit: 1200, FirePerHit: 450,
                        ColdPerHit: 0, LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0,
                        VitalityPerHit: 0, AetherPerHit: 0, ChaosPerHit: 0)
                },
                Metrics =
                [
                    new MetricRule { Key = "dps", DisplayName = "DPS", PriorityTier = 1, Weight = 1 },
                    new MetricRule { Key = "fireResistance", DisplayName = "火抗", PriorityTier = 0, Weight = 1, HardMinimum = 80 }
                ]
            };
            var store = new JsonProfileStore(path);
            await store.SaveAsync([legacy]);

            var migrated = Assert.Single(await store.LoadAsync());

            Assert.Equal(4, migrated.SchemaVersion);
            Assert.DoesNotContain(migrated.Metrics, x => x.Key == "fireResistance");
            Assert.DoesNotContain(migrated.Metrics, x => x.Key == "resistanceCoverage");
            Assert.Equal(SupportedMetricCatalog.Keys.Count, migrated.Metrics.Count);
            Assert.Equal(750, migrated.Scenario.IncomingDamage.PhysicalPerHit);
            Assert.Equal(112.5, migrated.Scenario.IncomingDamage.ChaosPerHit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SupportedMetricCatalogIsFixedUniqueAndPreservesOnlyEditableSettings()
    {
        Assert.Equal(new[] { "dps", "effectiveHealth" }, SupportedMetricCatalog.Keys);
        var customized = SupportedMetricCatalog.Merge(
        [
            new MetricRule
            {
                Key = "dps", DisplayName = "可篡改名称", Description = "可篡改说明",
                Weight = 2.5, HardMinimum = 50, Direction = MetricDirection.Minimize
            },
            new MetricRule { Key = "unknownMetric", Weight = 999 }
        ]);

        Assert.Equal(SupportedMetricCatalog.Keys.Count, customized.Count);
        Assert.Equal(customized.Count, customized.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(customized, x => x.Key == "unknownMetric");
        var dps = Assert.Single(customized, x => x.Key == "dps");
        Assert.Equal("对 BOSS 输出完成度", dps.DisplayName);
        Assert.Equal(MetricDirection.Maximize, dps.Direction);
        Assert.Equal(2.5, dps.Weight);
        Assert.Equal(50, dps.HardMinimum);
    }

    [Fact]
    public void DefaultIncomingBenchmarkCoversEveryDamageTypeWithoutChangingRawTotal()
    {
        var incoming = new IncomingDamageProfile();
        var values = new[]
        {
            incoming.PhysicalPerHit, incoming.PiercePerHit, incoming.FirePerHit,
            incoming.ColdPerHit, incoming.LightningPerHit, incoming.AcidPerHit,
            incoming.VitalityPerHit, incoming.AetherPerHit, incoming.ChaosPerHit
        };

        Assert.All(values, value => Assert.True(value > 0));
        Assert.Equal(1650, values.Sum());
        Assert.Equal(1980, BossMetricBenchmarkResolver.RawIncomingDps(new CombatScenario()));
    }

    [Fact]
    public async Task InvalidJsonFallsBackToDefaultWithoutOverwritingInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            await File.WriteAllTextAsync(path, "{ invalid");
            var original = await File.ReadAllBytesAsync(path);

            var loaded = await new JsonProfileStore(path).LoadAsync();

            Assert.Single(loaded);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
