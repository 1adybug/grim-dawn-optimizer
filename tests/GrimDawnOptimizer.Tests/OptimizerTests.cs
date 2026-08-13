using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;
using System.Diagnostics;

namespace GrimDawnOptimizer.Tests;

public sealed class OptimizerTests
{
    [Fact]
    public async Task DemoSearchReturnsValidDeterministicCandidates()
    {
        var (character, catalog) = DemoDataFactory.Create();
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1,
            ResultCount = 3,
            MonteCarloTrials = 500
        };
        var optimizer = new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator());

        var run = await optimizer.OptimizeAsync(character, character.Items, catalog.Attachments, catalog, profile);

        Assert.NotEmpty(run.Results);
        Assert.True(run.EvaluatedCandidates > 0);
        Assert.All(run.Results, result =>
        {
            Assert.True(result.Loadout.IsValid, string.Join("; ", result.Loadout.ValidationErrors));
            Assert.Equal(result.Loadout.Slots.Count, result.Loadout.Slots.Values.Select(x => x.Item.Id).Distinct().Count());
            if (result.Loadout.Slots.TryGetValue(EquipmentSlot.MainHand, out var main) && main.Item.IsTwoHanded)
                Assert.False(result.Loadout.Slots.ContainsKey(EquipmentSlot.OffHand));
            else
                Assert.True(result.Loadout.Slots.ContainsKey(EquipmentSlot.OffHand), "单手主手候选应保留副手槽位");
        });
    }

    [Fact]
    public async Task ImpossibleHardConstraintReturnsMarkedLeastViolationCandidates()
    {
        var (character, catalog) = DemoDataFactory.Create();
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            Metrics =
            [
                new MetricRule { Key = "fireResistance", DisplayName = "火焰抗性", PriorityTier = 0, HardMinimum = 999, NormalizationStep = 1 },
                new MetricRule { Key = "dps", DisplayName = "持续 DPS", PriorityTier = 1, Weight = 1 }
            ]
        };
        var optimizer = new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator());

        var run = await optimizer.OptimizeAsync(character, character.Items, catalog.Attachments, catalog, profile);

        Assert.NotEmpty(run.Results);
        Assert.False(run.Results[0].IsFeasible);
        Assert.True(run.Results[0].ConstraintViolations["fireResistance"] > 0);
    }

    [Fact]
    public async Task LargeAttachmentPoolIsBoundedAndReturnsWithinBudgetMargin()
    {
        var (character, catalog) = DemoDataFactory.Create();
        var allSlots = Enum.GetValues<EquipmentSlot>().ToHashSet();
        var attachments = Enumerable.Range(0, 100).Select(i => new AttachmentDefinition
        {
            Id = $"stress/component/{i}",
            Name = $"组件 {i}",
            Kind = AttachmentKind.Component,
            CompatibleSlots = allSlots,
            Stats = BuildAttachmentStats(i)
        }).Concat(Enumerable.Range(0, 80).Select(i => new AttachmentDefinition
        {
            Id = $"stress/augment/{i}",
            Name = $"增幅 {i}",
            Kind = AttachmentKind.Augment,
            CompatibleSlots = allSlots,
            Stats = BuildAttachmentStats(i + 100)
        })).ToArray();
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, attachments, catalog, profile, cancellationToken: timeout.Token);

        watch.Stop();
        Assert.NotEmpty(run.Results);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"耗时 {watch.Elapsed}");
    }

    [Fact]
    public void InstalledComponentIsAvailableIndependentlyWithItsCompletionBonus()
    {
        const string componentRecord = "records/items/components/test.dbr";
        const string completionRecord = "records/items/components/completion_test.dbr";
        var componentStats = new StatBlock();
        componentStats.Set("fireResistance", 8);
        var completionStats = new StatBlock();
        completionStats.Set("health", 120);
        var component = new AttachmentDefinition
        {
            Id = componentRecord,
            RecordId = componentRecord,
            Name = "已镶嵌组件",
            Kind = AttachmentKind.Component,
            LevelRequirement = 100,
            RequiredFormula = "missing-formula",
            CompatibleSlots = new HashSet<EquipmentSlot> { EquipmentSlot.Head },
            Stats = componentStats
        };
        var item = new ItemInstance
        {
            Id = "item", Name = "原装备", Slot = EquipmentSlot.Head, BaseRecord = "item.dbr",
            ComponentRecord = componentRecord, CompletionBonusRecord = completionRecord,
            Stats = new StatBlock(), Source = new ItemSourceLocation(ItemSourceKind.Equipped, "test.gdc")
        };
        var completion = new GameRecord
        {
            RecordName = completionRecord,
            NumericValues = new StatBlock(new Dictionary<string, double> { ["characterLife"] = 120 })
        };
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 1, Items = [item]
        };
        var catalog = new GameDataCatalog
        {
            Attachments = [component],
            Records = new Dictionary<string, GameRecord> { [completionRecord] = completion }
        };

        var available = new GameDataSource().GetUnlockedAttachments(character, catalog);
        var owned = Assert.Single(available, x => x.IsOwned);

        Assert.Equal(componentRecord, owned.RecordId);
        Assert.Equal(8, owned.Stats["fireResistance"]);
        Assert.Equal(120, owned.Stats["health"]);
    }

    private static StatBlock BuildAttachmentStats(int index)
    {
        var stats = new StatBlock();
        stats.Set("health", index % 7 * 13);
        stats.Set("offensiveAbility", index % 11 * 3);
        stats.Set("defensiveAbility", index % 13 * 2);
        stats.Set("fireResistance", index % 17);
        stats.Set("coldResistance", index % 19);
        stats.Set("lightningResistance", index % 23);
        stats.Set(StatKeys.DamageFlat("fire"), index % 29);
        return stats;
    }
}
