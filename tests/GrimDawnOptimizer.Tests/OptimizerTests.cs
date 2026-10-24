using GrimDawnOptimizer.Core.Contracts;
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
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 3,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 1_000,
            CurrentPanelDps = 1_000
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
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = itemA.Id },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 10_000, ["offensiveAbility"] = 3000, ["defensiveAbility"] = 2800
            })
        };
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1, ResultCount = 3, MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 100,
            CurrentPanelDps = 100,
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
        Assert.All(run.Results, x => Assert.Equal("augment-a", x.Loadout.Slots[EquipmentSlot.MainHand].Augment?.Id));
    }

    [Fact]
    public async Task SearchNeverReturnsEquipmentTheCharacterCannotWear()
    {
        var wearable = Item("wearable-weapon", 100);
        var impossible = new ItemInstance
        {
            Id = "impossible-weapon", Name = "impossible-weapon", Slot = EquipmentSlot.MainHand,
            BaseRecord = "impossible-weapon", Seed = 1, CunningRequirement = 1000,
            Stats = new StatBlock(new Dictionary<string, double>
            {
                [StatKeys.DamageFlat("fire")] = 10_000
            }),
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
        };
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 100,
            Physique = 100, Cunning = 100, Spirit = 100,
            Items = [wearable, impossible],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = wearable.Id },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 1000, ["cunning"] = 100,
                ["offensiveAbility"] = 1000, ["defensiveAbility"] = 1000
            })
        };
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1, ResultCount = 3, MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 100, CurrentPanelDps = 100,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [], new GameDataCatalog(), profile);

        var result = Assert.Single(run.Results);
        Assert.True(result.Loadout.IsValid, string.Join("; ", result.Loadout.ValidationErrors));
        Assert.Equal(wearable.Id, result.Loadout.Slots[EquipmentSlot.MainHand].Item.Id);
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
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1, ResultCount = 1, MonteCarloTrials = 500,
            // The raw model is intentionally far above this input. Calibration
            // must still use the user's exact current sheet value rather than
            // silently clipping the factor to the former 0.5 lower bound.
            CurrentWeaponAttackDps = 0,
            CurrentPanelDps = 30,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [], new GameDataCatalog(), profile);

        Assert.Equal(30, Assert.Single(run.Results).Simulation.UnmitigatedDps, 6);
        Assert.NotEqual(1, profile.RuntimeDpsCalibrationFactor);
        Assert.Equal(DpsCalibrationMode.SingleAnchor, profile.RuntimeDpsCalibrationMode);
        Assert.Equal(profile.RuntimeDpsCalibrationFactor, profile.RuntimeWeaponDpsCalibrationFactor, 10);
        Assert.Equal(profile.RuntimeDpsCalibrationFactor, profile.RuntimeSkillAddedDpsCalibrationFactor, 10);
    }

    [Fact]
    public async Task WeaponAndSkillPanelDpsCalibrateSeparateDamageParts()
    {
        const string skillRecord = "records/skills/test/two-anchor-attack.dbr";
        var item = Item("equipped-weapon", 100);
        var skill = new CharacterSkill(skillRecord, "双锚点攻击", 1, 0, true, "", "");
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 10,
            Physique = 100, Cunning = 100, Spirit = 100,
            Skills = [skill],
            Items = [item],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = item.Id },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 1000, ["offensiveAbility"] = 1000, ["defensiveAbility"] = 1000
            })
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [skillRecord] = new GameRecord
                {
                    RecordName = skillRecord,
                    RecordType = "Skill_WeaponPool_BasicAttack",
                    TextValues = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["Class"] = ["Skill_WeaponPool_BasicAttack"]
                    },
                    NumericSeries = new Dictionary<string, IReadOnlyList<double>>
                    {
                        ["weaponDamagePct"] = [200]
                    }
                }
            }
        };
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(item, null, null)
        };
        var engine = new CharacterStatEngine();
        var simulator = new CombatSimulator();
        var loadout = engine.Calculate(character, slots, catalog);
        var rawWeaponDps = simulator.Simulate(
            character, loadout, new CombatScenario(), SkillRotationFactory.Build(character, catalog, null, slots)).UnmitigatedDps;
        var rawSkillDps = simulator.Simulate(
            character, loadout, new CombatScenario(), SkillRotationFactory.Build(character, catalog, skill, slots)).UnmitigatedDps;
        Assert.True(rawSkillDps > rawWeaponDps);
        var panelWeaponDps = rawWeaponDps * 1.1;
        var panelSkillDps = panelWeaponDps + (rawSkillDps - rawWeaponDps) * 1.2;
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = panelWeaponDps,
            CurrentPanelDps = panelSkillDps,
            Rotation = new SkillRotationDefinition { MainSkillRecord = skillRecord },
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(engine, simulator)
            .OptimizeAsync(character, character.Items, [], catalog, profile);

        Assert.Equal(panelSkillDps, Assert.Single(run.Results).Simulation.UnmitigatedDps, 6);
        Assert.Equal(DpsCalibrationMode.DualAnchor, profile.RuntimeDpsCalibrationMode);
        Assert.Equal(1.1, profile.RuntimeWeaponDpsCalibrationFactor, 6);
        Assert.Equal(1.2, profile.RuntimeSkillAddedDpsCalibrationFactor, 6);
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
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1, ResultCount = 1, MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 100,
            CurrentPanelDps = 100,
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
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 1_000,
            CurrentPanelDps = 1_000
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
    public async Task CandidateEvaluationUsesConfiguredParallelism()
    {
        var weapons = Enumerable.Range(0, 8)
            .Select(i => ItemAt($"parallel-weapon-{i}", EquipmentSlot.MainHand, 100 + i))
            .ToArray();
        var helmets = Enumerable.Range(0, 8)
            .Select(i => ItemAt($"parallel-helmet-{i}", EquipmentSlot.Head, 10 + i))
            .ToArray();
        var character = new CharacterSnapshot
        {
            Name = "parallel-test",
            SaveFilePath = "parallel-test.gdc",
            Level = 100,
            Physique = 1_000,
            Cunning = 1_000,
            Spirit = 1_000,
            Items = weapons.Concat(helmets).ToArray(),
            CurrentLoadout = new Dictionary<EquipmentSlot, string>
            {
                [EquipmentSlot.MainHand] = weapons[0].Id,
                [EquipmentSlot.Head] = helmets[0].Id
            },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 10_000,
                ["offensiveAbility"] = 3_000,
                ["defensiveAbility"] = 2_800
            })
        };
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 100,
            CurrentPanelDps = 100,
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };
        var simulator = new ConcurrentCallTrackingSimulator();

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), simulator, maxDegreeOfParallelism: 4)
            .OptimizeAsync(character, character.Items, [], new GameDataCatalog(), profile);

        Assert.NotEmpty(run.Results);
        Assert.True(simulator.MaxConcurrentCalls > 1,
            $"预期并行模拟，实际最大并发为 {simulator.MaxConcurrentCalls}");
    }

    [Fact]
    public async Task SingleAnchorSearchDoesNotRequireWeaponDps()
    {
        var (character, catalog) = DemoDataFactory.Create();
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 0,
            CurrentPanelDps = 100
        };
        var optimizer = new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator());

        var run = await optimizer.OptimizeAsync(
            character, character.Items, catalog.Attachments, catalog, profile);

        Assert.NotEmpty(run.Results);
        Assert.Equal(DpsCalibrationMode.SingleAnchor, profile.RuntimeDpsCalibrationMode);
    }

    [Fact]
    public async Task ExplicitCalibrationRequiresMainSkillPanelDps()
    {
        var (character, catalog) = DemoDataFactory.Create();
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentPanelDps = 0
        };
        var optimizer = new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            optimizer.OptimizeAsync(character, character.Items, catalog.Attachments, catalog, profile));

        Assert.Contains("主攻技能", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(double.NaN)]
    public async Task DirectCalculationIgnoresMissingOrStalePanelDpsByDefault(double stalePanelDps)
    {
        var (character, catalog, skill) = SkillCalibrationFixture(
            "direct-weapon-skill", "Skill_WeaponPool_BasicAttack", weaponDamagePercent: 200);
        var profile = new OptimizationProfile
        {
            SearchBudgetSeconds = 1, ResultCount = 1, MonteCarloTrials = 500,
            CurrentPanelDps = stalePanelDps, CurrentWeaponAttackDps = 0,
            RuntimeDpsCalibrationFactor = 7, RuntimeWeaponDpsCalibrationFactor = 8,
            RuntimeSkillAddedDpsCalibrationFactor = 9, RuntimeDpsCalibrationMode = DpsCalibrationMode.DualAnchor,
            Rotation = new SkillRotationDefinition { MainSkillRecord = skill.Record }
        };
        var engine = new CharacterStatEngine();
        var simulator = new CombatSimulator();
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(character.Items[0], null, null)
        };
        var expected = simulator.Simulate(character, engine.Calculate(character, slots, catalog),
            profile.Scenario, SkillRotationFactory.Build(character, catalog, skill, slots)).UnmitigatedDps;

        var run = await new BeamSearchOptimizer(engine, simulator)
            .OptimizeAsync(character, character.Items, [], catalog, profile);

        Assert.False(profile.UsePanelDpsCalibration);
        Assert.Equal(expected, Assert.Single(run.Results).Simulation.UnmitigatedDps, 8);
        Assert.Equal(DpsCalibrationMode.None, profile.RuntimeDpsCalibrationMode);
        Assert.Equal(1, profile.RuntimeDpsCalibrationFactor);
        Assert.Equal(1, profile.RuntimeWeaponDpsCalibrationFactor);
        Assert.Equal(1, profile.RuntimeSkillAddedDpsCalibrationFactor);
    }

    [Fact]
    public async Task PureSpellUsesOnlyMainSkillPanelDps()
    {
        var (character, catalog, skill) = SkillCalibrationFixture(
            "pure-spell", "Skill_AttackProjectile", weaponDamagePercent: 0, flatFireDamage: 50);
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 0,
            CurrentPanelDps = 500,
            Rotation = new SkillRotationDefinition { MainSkillRecord = skill.Record },
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [], catalog, profile);

        Assert.Equal(500, Assert.Single(run.Results).Simulation.UnmitigatedDps, 6);
        Assert.Equal(DpsCalibrationMode.SingleAnchor, profile.RuntimeDpsCalibrationMode);
    }

    [Fact]
    public async Task ReliableWeaponDamageSkillRequiresWeaponAttackDps()
    {
        var (character, catalog, skill) = SkillCalibrationFixture(
            "weapon-skill", "Skill_WeaponPool_BasicAttack", weaponDamagePercent: 200);
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 0,
            CurrentPanelDps = 500,
            Rotation = new SkillRotationDefinition { MainSkillRecord = skill.Record }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
                .OptimizeAsync(character, character.Items, [], catalog, profile));

        Assert.Contains("武器攻击", exception.Message);
    }

    [Fact]
    public async Task IllConditionedDualAnchorAutomaticallyFallsBackToSingleAnchor()
    {
        var (character, catalog, skill) = SkillCalibrationFixture(
            "near-weapon-skill", "Skill_WeaponPool_BasicAttack", weaponDamagePercent: 101);
        var profile = new OptimizationProfile
        {
            UsePanelDpsCalibration = true,
            SearchBudgetSeconds = 1,
            ResultCount = 1,
            MonteCarloTrials = 500,
            CurrentWeaponAttackDps = 50,
            CurrentPanelDps = 500,
            Rotation = new SkillRotationDefinition { MainSkillRecord = skill.Record },
            Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };

        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator())
            .OptimizeAsync(character, character.Items, [], catalog, profile);

        Assert.Equal(500, Assert.Single(run.Results).Simulation.UnmitigatedDps, 6);
        Assert.Equal(DpsCalibrationMode.SingleAnchorFallback, profile.RuntimeDpsCalibrationMode);
        Assert.Equal(profile.RuntimeDpsCalibrationFactor, profile.RuntimeWeaponDpsCalibrationFactor, 10);
        Assert.Equal(profile.RuntimeDpsCalibrationFactor, profile.RuntimeSkillAddedDpsCalibrationFactor, 10);
    }

    [Fact]
    public void WeaponDpsRequirementUsesRotationReliabilityAndWeaponDamage()
    {
        Assert.False(BeamSearchOptimizer.RequiresWeaponDpsCalibration(new SkillRotationDefinition
        {
            IsFallback = true,
            WeaponDamagePercent = 100
        }));
        Assert.False(BeamSearchOptimizer.RequiresWeaponDpsCalibration(new SkillRotationDefinition
        {
            IsFallback = false,
            WeaponDamagePercent = 0
        }));
        Assert.True(BeamSearchOptimizer.RequiresWeaponDpsCalibration(new SkillRotationDefinition
        {
            IsFallback = false,
            WeaponDamagePercent = 150
        }));
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

    private static (CharacterSnapshot Character, GameDataCatalog Catalog, CharacterSkill Skill)
        SkillCalibrationFixture(
            string id,
            string recordClass,
            double weaponDamagePercent,
            double flatFireDamage = 0)
    {
        var skillRecord = $"records/skills/test/{id}.dbr";
        var skill = new CharacterSkill(skillRecord, id, 1, 0, true, "", "");
        var weapon = Item($"{id}-weapon", 100);
        var character = new CharacterSnapshot
        {
            Name = id,
            SaveFilePath = $"{id}.gdc",
            Level = 100,
            Physique = 1000,
            Cunning = 1000,
            Spirit = 1000,
            Skills = [skill],
            Items = [weapon],
            CurrentLoadout = new Dictionary<EquipmentSlot, string>
            {
                [EquipmentSlot.MainHand] = weapon.Id
            },
            BaselineStats = new StatBlock(new Dictionary<string, double>
            {
                ["health"] = 10_000,
                ["offensiveAbility"] = 3000,
                ["defensiveAbility"] = 2800
            })
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [skillRecord] = new GameRecord
                {
                    RecordName = skillRecord,
                    RecordType = recordClass,
                    TextValues = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["Class"] = [recordClass]
                    },
                    NumericSeries = new Dictionary<string, IReadOnlyList<double>>
                    {
                        ["weaponDamagePct"] = [weaponDamagePercent],
                        ["offensiveFireMin"] = [flatFireDamage],
                        ["offensiveFireMax"] = [flatFireDamage]
                    }
                }
            }
        };
        return (character, catalog, skill);
    }

    private static ItemInstance ItemAt(
        string id,
        EquipmentSlot targetSlot,
        double fireDamage = 0) => new()
    {
        Id = id,
        Name = id,
        Slot = targetSlot is EquipmentSlot.RingLeft or EquipmentSlot.RingRight ? EquipmentSlot.Ring : targetSlot,
        BaseRecord = id,
        Seed = 1,
        Stats = new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.DamageFlat("fire")] = fireDamage
        }),
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };

    private static ItemInstance CopyWithQualifier(ItemInstance source, string qualifier) => new()
    {
        Id = source.Id + qualifier, Name = source.Name, Slot = source.Slot, BaseRecord = source.BaseRecord,
        Seed = source.Seed, Stats = source.Stats, Source = source.Source, AttachmentQualifier = qualifier
    };

    private sealed class ConcurrentCallTrackingSimulator : ICombatSimulator
    {
        private readonly CombatSimulator _inner = new();
        private int _activeCalls;
        private int _maxConcurrentCalls;

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public SimulationReport Simulate(
            CharacterSnapshot character,
            CalculatedLoadout loadout,
            CombatScenario scenario,
            SkillRotationDefinition rotation)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            var observed = Volatile.Read(ref _maxConcurrentCalls);
            while (active > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maxConcurrentCalls, active, observed);
                if (prior == observed) break;
                observed = prior;
            }

            try
            {
                Thread.Sleep(3);
                return _inner.Simulate(character, loadout, scenario, rotation);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task<SimulationReport> RunMonteCarloAsync(
            CharacterSnapshot character,
            CalculatedLoadout loadout,
            CombatScenario scenario,
            SkillRotationDefinition rotation,
            int trials,
            int seed,
            CancellationToken cancellationToken = default) =>
            _inner.RunMonteCarloAsync(character, loadout, scenario, rotation, trials, seed, cancellationToken);
    }
}
