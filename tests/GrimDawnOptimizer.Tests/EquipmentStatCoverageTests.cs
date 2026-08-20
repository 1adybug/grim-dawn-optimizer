using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class EquipmentStatCoverageTests
{
    [Fact]
    public void TranslatorCoversGlobalAndDefensiveEquipmentStats()
    {
        var record = Record("coverage", new Dictionary<string, double>
        {
            ["offensiveTotalDamageModifier"] = 25,
            ["characterTotalSpeedModifier"] = 6,
            ["characterLifeRegenModifier"] = 20,
            ["characterManaRegenModifier"] = 30,
            ["defensiveProtectionModifier"] = 12,
            ["defensiveBlockAmountModifier"] = 14,
            ["characterDefensiveBlockRecoveryReduction"] = 18,
            ["skillCooldownReduction"] = 10,
            ["skillManaCostReduction"] = 8,
            ["defensiveBleeding"] = 22
        });

        var stats = GameStatTranslator.Translate([record]);

        Assert.Equal(25, stats["damage.total.percent"]);
        Assert.Equal(6, stats["attackSpeed"]);
        Assert.Equal(6, stats["castSpeed"]);
        Assert.Equal(20, stats["healthRegen.percent"]);
        Assert.Equal(30, stats["energyRegen.percent"]);
        Assert.Equal(12, stats["armor.percent"]);
        Assert.Equal(14, stats["blockAmount.percent"]);
        Assert.Equal(18, stats["blockRecoveryReduction"]);
        Assert.Equal(10, stats["cooldownReduction"]);
        Assert.Equal(8, stats["energyCostReduction"]);
        Assert.Equal(22, stats["bleedingResistance"]);
        Assert.Empty(GameStatTranslator.FindUnsupportedCombatFields(record));
    }

    [Fact]
    public void NumericArraySummariesAreNotAddedAsIndependentStats()
    {
        var record = Record("array", new Dictionary<string, double>
        {
            ["characterLife"] = 100,
            ["characterLife.min"] = 80,
            ["characterLife.max"] = 120
        });

        Assert.Equal(100, GameStatTranslator.Translate([record])["health"]);
    }

    [Fact]
    public void ChanceBasedModifierUsesItsExpectedContribution()
    {
        var record = Record("chance", new Dictionary<string, double>
        {
            ["offensiveFireModifier"] = 250,
            ["offensiveFireModifierChance"] = 20,
            ["offensiveLifeLeechMin"] = 10,
            ["offensiveLifeLeechChance"] = 30
        });

        var stats = GameStatTranslator.Translate([record]);

        Assert.Equal(50, stats[StatKeys.DamagePercent("fire")]);
        Assert.Equal(3, stats["lifeSteal"]);
    }

    [Fact]
    public void DotFieldIsPerTickAndAlsoRetainsProcTotal()
    {
        var record = Record("burn", new Dictionary<string, double>
        {
            ["offensiveSlowFireMin"] = 10,
            ["offensiveSlowFireDurationMin"] = 3
        });

        var stats = GameStatTranslator.Translate([record]);

        Assert.Equal(10, stats[StatKeys.DamageDot("fire")]);
        Assert.Equal(30, stats["damage.fire.dotTotal"]);
    }

    [Fact]
    public void UnknownCombatFieldIsNeverSilentlyAccepted()
    {
        var record = Record("future", new Dictionary<string, double>
        {
            ["offensiveUnknownFutureDamage"] = 42
        });

        Assert.Contains("offensiveUnknownFutureDamage",
            GameStatTranslator.FindUnsupportedCombatFields(record));
    }

    [Fact]
    public void ApothecaryTotalDamageGloveBeatsNinePercentSpeedAtThreeToOneWeight()
    {
        var character = new CharacterSnapshot
        {
            Name = "glove-regression", SaveFilePath = "test.gdc", Level = 41,
            BaselineStats = Stats(
                ("health", 10_000), ("defensiveAbility", 2_800), ("offensiveAbility", 3_000),
                (StatKeys.WeaponDamage("physical"), 1_000))
        };
        var apothecary = Item("药剂师的触摸", Stats(("damage.total.percent", 25), ("health", 248)));
        var swiftRuthless = Item("轻快之 无情的 手套", Stats(("attackSpeed", 9)));
        var engine = new CharacterStatEngine();
        var catalog = new GameDataCatalog();
        var apothecaryLoadout = engine.Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.Hands] = new(apothecary, null, null)
            }, catalog);
        var candidateLoadout = engine.Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.Hands] = new(swiftRuthless, null, null)
            }, catalog);
        var scenario = new CombatScenario
        {
            EnemyArmor = 0, EnemyDefensiveAbility = 3_000, EnemyOffensiveAbility = 2_800,
            IncomingDamage = new IncomingDamageProfile(
                HitsPerSecond: 1, PhysicalPerHit: 1_000, FirePerHit: 0, ColdPerHit: 0,
                LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0, VitalityPerHit: 0,
                AetherPerHit: 0, ChaosPerHit: 0, BleedingPerHit: 0)
        };
        var rotation = new SkillRotationDefinition { BaseAttacksPerSecond = 1, WeaponDamagePercent = 100 };
        var simulator = new CombatSimulator();
        var current = simulator.Simulate(character, apothecaryLoadout, scenario, rotation);
        var candidate = simulator.Simulate(character, candidateLoadout, scenario, rotation);
        var rules = new[]
        {
            new MetricRule { Key = "dps", Weight = 3 },
            new MetricRule { Key = "effectiveHealth", Weight = 1 }
        };
        var scoring = new ScoringEngine(rules, BossMetricBenchmarkResolver.Resolve(rules, scenario));
        var currentScore = scoring.Score(new Dictionary<string, double>
        {
            ["dps"] = current.ExpectedDps, ["effectiveHealth"] = current.EffectiveHealth
        }).TierScores[0];
        var candidateScore = scoring.Score(new Dictionary<string, double>
        {
            ["dps"] = candidate.ExpectedDps, ["effectiveHealth"] = candidate.EffectiveHealth
        }).TierScores[0];

        Assert.True(current.ExpectedDps > candidate.ExpectedDps);
        Assert.True(current.EffectiveHealth > candidate.EffectiveHealth);
        Assert.True(currentScore > candidateScore);
    }

    [Fact]
    public void UnsupportedSetBonusWarningReachesCalculatedLoadout()
    {
        var warning = new UnsupportedEffect("set-bonus", "未建模套装词条", ["dps"]);
        var setStats = new StatBlock();
        var catalog = new GameDataCatalog
        {
            SetBonuses = new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>
            {
                ["set-a"] = new Dictionary<int, StatBlock> { [2] = setStats }
            },
            SetBonusUnsupportedEffects =
                new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<UnsupportedEffect>>>
                {
                    ["set-a"] = new Dictionary<int, IReadOnlyList<UnsupportedEffect>> { [2] = [warning] }
                }
        };
        var character = new CharacterSnapshot
        {
            Name = "set", SaveFilePath = "test.gdc", Level = 100,
            BaselineStats = Stats(("health", 10_000))
        };
        var result = new CharacterStatEngine().Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.Head] = new(Item("head", new StatBlock(), EquipmentSlot.Head, "set-a"), null, null),
                [EquipmentSlot.Chest] = new(Item("chest", new StatBlock(), EquipmentSlot.Chest, "set-a"), null, null)
            }, catalog);

        Assert.Contains(warning, result.UnsupportedEffects);
    }

    [Fact]
    public void CandidateSkillLevelBonusUpdatesPersistentSkillStats()
    {
        const string skillName = "records/skills/test/passive.dbr";
        const string itemName = "records/items/test/skill-glove.dbr";
        var skill = new GameRecord
        {
            RecordName = skillName,
            RecordType = "Skill_Passive",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            {
                ["characterLife"] = [100, 200]
            },
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_Passive"]
            }
        };
        var itemRecord = new GameRecord
        {
            RecordName = itemName,
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["augmentSkillLevel1"] = 1
            }),
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["augmentSkillName1"] = [skillName]
            }
        };
        var character = new CharacterSnapshot
        {
            Name = "passive", SaveFilePath = "test.gdc", Level = 100,
            BaselineStats = Stats(("health", 1_100)),
            Skills = [new CharacterSkill(skillName, "被动", 1, 0, true, "", "")]
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [skillName] = skill, [itemName] = itemRecord
            }
        };

        var result = new CharacterStatEngine().Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.Hands] = new(Item("技能手套", new StatBlock(), baseRecord: itemName), null, null)
            }, catalog);

        Assert.Equal(1_200, result.Stats["health"]);
    }

    [Fact]
    public void TriggeredSkillIncludesLinkedDirectDotAndWeaponDamage()
    {
        const string ownerName = "records/items/test/proc.dbr";
        const string skillName = "records/skills/item/proc.dbr";
        const string projectileName = "records/skills/item/projectile.dbr";
        const string controllerName = "records/controllers/onattack.dbr";
        var owner = new GameRecord
        {
            RecordName = ownerName,
            NumericValues = new StatBlock(new Dictionary<string, double> { ["itemSkillLevelEq"] = 1 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["itemSkillName"] = [skillName], ["itemSkillAutoController"] = [controllerName]
            }
        };
        var skill = new GameRecord
        {
            RecordName = skillName,
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["weaponDamagePct"] = 50,
                ["offensiveSlowFireMin"] = 10,
                ["offensiveSlowFireDurationMin"] = 3
            }),
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["skillProjectileName"] = [projectileName]
            }
        };
        var projectile = Record(projectileName, new Dictionary<string, double>
        {
            ["offensiveFireMin"] = 20
        });
        var controller = new GameRecord
        {
            RecordName = controllerName,
            NumericValues = new StatBlock(new Dictionary<string, double> { ["triggerEventChance"] = 100 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["triggerType"] = ["AttackEnemy"] }
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [skillName] = skill, [projectileName] = projectile, [controllerName] = controller
            }
        };

        var effect = Assert.Single(SaveReader.BuildTriggeredEffects([owner], catalog));

        Assert.Equal(50, effect.WeaponDamagePercent);
        Assert.Equal(50, effect.Damage["proc.weaponDamagePercent"]);
        Assert.Equal(20, effect.Damage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(30, effect.Damage["damage.fire.dotTotal"]);
    }

    private static GameRecord Record(string name, IReadOnlyDictionary<string, double> values) => new()
    {
        RecordName = name,
        NumericValues = new StatBlock(values)
    };

    private static ItemInstance Item(
        string name,
        StatBlock stats,
        EquipmentSlot slot = EquipmentSlot.Hands,
        string setId = "",
        string? baseRecord = null) => new()
    {
        Id = name, Name = name, Slot = slot, BaseRecord = baseRecord ?? name, Stats = stats, SetId = setId,
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };

    private static StatBlock Stats(params (string Key, double Value)[] values)
    {
        var result = new StatBlock();
        foreach (var (key, value) in values) result.Set(key, value);
        return result;
    }
}
