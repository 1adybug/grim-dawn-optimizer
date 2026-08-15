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
            var requiredSlots = Enum.GetValues<EquipmentSlot>()
                .Where(x => x is not (EquipmentSlot.Ring or EquipmentSlot.OffHand));
            Assert.All(requiredSlots, slot =>
                Assert.True(result.Loadout.Slots.ContainsKey(slot), $"候选不应缺少 {slot}"));
        });
    }

    [Fact]
    public async Task ResultsUseDifferentEquipmentCombinationsInsteadOfAttachmentOnlyVariants()
    {
        var itemA = Item("weapon-a", 100);
        var itemB = Item("weapon-b", 90);
        var attachments = new[]
        {
            Augment("augment-a", 20),
            Augment("augment-b", 19),
            Augment("augment-c", 18)
        };
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 100,
            Physique = 1000, Cunning = 1000, Spirit = 1000,
            Items = [itemA, itemB],
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 10_000, ["offensiveAbility"] = 3000, ["defensiveAbility"] = 2800
            })
        };
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1, ResultCount = 3, MonteCarloTrials = 500,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "持续 DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, attachments, new GameDataCatalog(), profile);

        Assert.Equal(2, run.Results.Count);
        Assert.Equal(run.Results.Count, run.Results
            .Select(x => string.Join(';', x.Loadout.Slots.OrderBy(s => s.Key).Select(s => s.Value.Item.Id)))
            .Distinct(StringComparer.Ordinal)
            .Count());
        Assert.Contains(run.Results, x => x.Loadout.Slots.Values.Any(v => v.Item.Id == itemB.Id));
    }

    [Fact]
    public async Task CurrentPanelDpsCalibratesReportedCandidateDps()
    {
        var item = Item("equipped-weapon", 100);
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 10,
            Physique = 100, Cunning = 100, Spirit = 100,
            Items = [item],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = item.Id },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 1000, ["offensiveAbility"] = 1000, ["defensiveAbility"] = 1000
            })
        };
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1, ResultCount = 1, MonteCarloTrials = 500,
            CurrentPanelDps = 300,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [], new GameDataCatalog(), profile);

        Assert.Equal(300, Assert.Single(run.Results).Simulation.UnmitigatedDps, 6);
        Assert.NotEqual(1, profile.RuntimeDpsCalibrationFactor);
    }

    [Fact]
    public async Task ResultReportsTargetFreeEquipmentOnlyDpsWithoutAttachments()
    {
        var item = Item("weapon", 100);
        var attachment = Augment("damage-augment", 100);
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 10,
            Physique = 100, Cunning = 100, Spirit = 100,
            Items = [item],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = item.Id },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 1000, ["offensiveAbility"] = 1000, ["defensiveAbility"] = 1000
            })
        };
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1, ResultCount = 1, MonteCarloTrials = 500,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [attachment], new GameDataCatalog(), profile);

        var result = Assert.Single(run.Results);
        Assert.True(result.EquipmentOnlyUnmitigatedDps > 0);
        Assert.True(result.Simulation.UnmitigatedDps > result.EquipmentOnlyUnmitigatedDps);
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

    [Fact]
    public void AttachmentDatabaseQualifiersRestrictAmmoToSupportedWeaponClasses()
    {
        var record = new GameRecord
        {
            RecordName = "records/items/materia/ammo.dbr",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["ranged1h"] = 1, ["ranged2h"] = 1, ["offhand"] = 1
            })
        };

        var qualifiers = GameDataSource.ResolveAttachmentQualifiers(record);
        var slots = GameDataSource.ResolveAttachmentSlots(qualifiers);

        Assert.Contains("ranged1h", qualifiers);
        Assert.Contains(EquipmentSlot.MainHand, slots);
        Assert.Contains(EquipmentSlot.OffHand, slots);
        Assert.DoesNotContain(EquipmentSlot.Hands, slots);
        Assert.DoesNotContain(EquipmentSlot.Amulet, slots);
    }

    [Fact]
    public void AttachmentCompatibilityChecksExactWeaponQualifier()
    {
        var ammo = new AttachmentDefinition
        {
            Id = "ammo", Name = "弹药", Kind = AttachmentKind.Component,
            CompatibleSlots = new HashSet<EquipmentSlot> { EquipmentSlot.MainHand },
            CompatibleItemQualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ranged1h" }
        };
        var gun = Item("gun", 10);
        var rangedGun = CopyWithQualifier(gun, "ranged1h");
        var sword = CopyWithQualifier(gun, "sword");
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", Level = 100 };
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(BeamSearchOptimizer.IsAttachmentCompatible(ammo, rangedGun, character, owned));
        Assert.False(BeamSearchOptimizer.IsAttachmentCompatible(ammo, sword, character, owned));
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

    private static ItemInstance Item(string id, double fireDamage) => new()
    {
        Id = id, Name = id, Slot = EquipmentSlot.MainHand, BaseRecord = id,
        Seed = 1, Stats = new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.DamageFlat("fire")] = fireDamage
        }),
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };

    private static AttachmentDefinition Augment(string id, double fireDamage) => new()
    {
        Id = id, Name = id, Kind = AttachmentKind.Augment,
        CompatibleSlots = new HashSet<EquipmentSlot> { EquipmentSlot.MainHand },
        Stats = new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.DamageFlat("fire")] = fireDamage
        })
    };

    private static ItemInstance CopyWithQualifier(ItemInstance source, string qualifier) => new()
    {
        Id = source.Id + qualifier, Name = source.Name, Slot = source.Slot, BaseRecord = source.BaseRecord,
        Seed = source.Seed, Stats = source.Stats, Source = source.Source, AttachmentQualifier = qualifier
    };
}
