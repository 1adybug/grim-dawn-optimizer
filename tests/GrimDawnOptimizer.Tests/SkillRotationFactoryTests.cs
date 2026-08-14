using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SkillRotationFactoryTests
{
    [Fact]
    public void SelectedWeaponAttackUsesItsLevelScaledDamageAndCost()
    {
        const string recordName = "records/skills/playerclass/test/attack.dbr";
        var record = new GameRecord
        {
            RecordName = recordName,
            RecordType = "Skill_WeaponPool_BasicAttack",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            {
                ["weaponDamagePct"] = [100, 125],
                ["skillManaCost"] = [1, 3],
                ["offensiveFireMin"] = [10, 20],
                ["offensiveFireMax"] = [20, 40]
            },
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_WeaponPool_BasicAttack"]
            }
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [recordName] = record }
        };
        var skill = new CharacterSkill(recordName, "测试攻击", 2, 0, true, "", "");

        var rotation = SkillRotationFactory.Build(Character(), catalog, skill);

        Assert.Equal(125, rotation.WeaponDamagePercent);
        Assert.Equal(3, rotation.EnergyPerUse);
        Assert.Equal(30, rotation.FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.True(rotation.UsesAttackSpeed);
        Assert.False(rotation.IsFallback);
    }

    [Fact]
    public void SelectedSkillRaisesDpsAndReportsPreMitigationComparisonValue()
    {
        var character = Character();
        var stats = new StatBlock(new Dictionary<string, double>
        {
            ["health"] = 10_000,
            ["energy"] = 10_000,
            ["energyRegen"] = 100,
            ["offensiveAbility"] = 3000,
            ["defensiveAbility"] = 2800,
            [StatKeys.WeaponDamage("physical")] = 100
        });
        var loadout = new CalculatedLoadout { Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = stats };
        var scenario = new CombatScenario
        {
            EnemyArmor = 0,
            EnemyDefensiveAbility = 3000,
            EnemyResistances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["physical"] = 50, ["fire"] = 50
            }
        };
        var defaultAttack = new SkillRotationDefinition { WeaponDamagePercent = 100, BaseAttacksPerSecond = 1.5 };
        var skillAttack = new SkillRotationDefinition
        {
            WeaponDamagePercent = 150,
            BaseAttacksPerSecond = 1.5,
            FlatDamage = new StatBlock(new Dictionary<string, double> { [StatKeys.DamageFlat("fire")] = 50 })
        };
        var simulator = new CombatSimulator();

        var baseline = simulator.Simulate(character, loadout, scenario, defaultAttack);
        var selected = simulator.Simulate(character, loadout, scenario, skillAttack);

        Assert.True(selected.ExpectedDps > baseline.ExpectedDps);
        Assert.True(selected.UnmitigatedDps > selected.ExpectedDps);
    }

    [Fact]
    public void ChargedFinaleUsesChargeCycleActivationRate()
    {
        const string recordName = "records/skills/playerclass/test/charged-finale.dbr";
        var record = new GameRecord
        {
            RecordName = recordName,
            RecordType = "Skill_WeaponPool_ChargedFinale",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            {
                ["weaponDamagePct"] = [200],
                ["skillChargeLevel"] = [2]
            },
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_WeaponPool_ChargedFinale"]
            }
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [recordName] = record }
        };

        var rotation = SkillRotationFactory.Build(
            Character(), catalog, new CharacterSkill(recordName, "测试终结技", 1, 0, true, "", ""));

        Assert.Equal(1d / 3d, rotation.ActivationRate, 8);
        Assert.Contains("每 3 击触发", rotation.CalculationSummary);
    }

    [Fact]
    public void FixedSkillDamageWithNoMaximumIsNotCutInHalf()
    {
        var record = new GameRecord
        {
            RecordName = "fixed-damage.dbr",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["offensiveFireMin"] = 40
            })
        };

        var translated = GameStatTranslator.Translate([record]);

        Assert.Equal(40, translated[StatKeys.DamageFlat("fire")]);
    }

    [Fact]
    public void ElementalDamageIsDistributedAcrossFireColdAndLightning()
    {
        var record = new GameRecord
        {
            RecordName = "elemental.dbr",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["offensiveElementalMin"] = 90,
                ["offensiveElementalModifier"] = 25
            })
        };

        var translated = GameStatTranslator.Translate([record]);

        foreach (var type in new[] { "fire", "cold", "lightning" })
        {
            Assert.Equal(30, translated[StatKeys.DamageFlat(type)]);
            Assert.Equal(25, translated[StatKeys.DamagePercent(type)]);
        }
    }

    [Fact]
    public void EquipmentSkillCriticalModifierUsesSheetDamageKey()
    {
        var record = new GameRecord
        {
            RecordName = "skill-modifier.dbr",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["offensiveCritDamageModifier"] = 18
            })
        };

        var translated = GameStatTranslator.Translate([record]);

        Assert.Equal(18, translated["criticalDamage"]);
    }

    [Fact]
    public void DefaultAttackPlaceholderIsNotCountedAsWeaponPoolSkill()
    {
        const string placeholder = "records/skills/default/defaultwpbasicattack.dbr";
        const string bursting = "records/skills/playerclass07/wpattack01.dbr";
        GameRecord Wps(string name, double chance) => new()
        {
            RecordName = name,
            RecordType = "Skill_WPAttack_BasicAttack",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["skillChanceWeight"] = chance, ["weaponDamagePct"] = 100
            }),
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_WPAttack_BasicAttack"]
            }
        };
        var character = CharacterWithSkills(
            new CharacterSkill(placeholder, "占位攻击", 1, 0, true, "", ""),
            new CharacterSkill(bursting, "爆裂弹药", 10, 0, true, "", ""));
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [placeholder] = Wps(placeholder, 20), [bursting] = Wps(bursting, 22)
            }
        };

        var rotation = SkillRotationFactory.Build(character, catalog, null);

        Assert.Equal(22, rotation.WeaponPoolChance);
        Assert.Single(rotation.WeaponPoolAttacks);
    }

    [Fact]
    public void CandidateEquipmentSkillLevelIsUsedDuringEvaluation()
    {
        const string skillName = "records/skills/test/main.dbr";
        const string itemName = "records/items/test/bonus.dbr";
        var skillRecord = new GameRecord
        {
            RecordName = skillName,
            RecordType = "Skill_WeaponPool_BasicAttack",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            {
                ["weaponDamagePct"] = [100, 150]
            },
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Skill_WeaponPool_BasicAttack"]
            }
        };
        var itemRecord = new GameRecord
        {
            RecordName = itemName,
            NumericValues = new StatBlock(new Dictionary<string, double> { ["augmentSkillLevel1"] = 1 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["augmentSkillName1"] = [skillName] }
        };
        var item = new ItemInstance
        {
            Id = "item", Name = "技能装备", Slot = EquipmentSlot.Head, BaseRecord = itemName,
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
        };
        var character = CharacterWithSkills(new CharacterSkill(skillName, "主攻技能", 1, 0, true, "", ""));
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [skillName] = skillRecord, [itemName] = itemRecord }
        };

        var rotation = SkillRotationFactory.Build(character, catalog, character.Skills[0],
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) });

        Assert.Equal(150, rotation.WeaponDamagePercent);
        Assert.Contains("基础 1 + 装备 1", rotation.CalculationSummary);
    }

    [Fact]
    public void TriggerControllerFilenameSuppliesChanceAndGrantedSkillIsNotAProc()
    {
        const string procSkill = "records/skills/item/proc.dbr";
        const string controllerName = "records/controllers/itemskills/cast_@enemyonattack_15%.dbr";
        var procOwner = new GameRecord
        {
            RecordName = "proc-owner.dbr",
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["itemSkillName"] = [procSkill], ["itemSkillAutoController"] = [controllerName]
            }
        };
        var grantedOwner = new GameRecord
        {
            RecordName = "granted-owner.dbr",
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["itemSkillName"] = [procSkill] }
        };
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [procSkill] = new GameRecord
                {
                    RecordName = procSkill,
                    NumericValues = new StatBlock(new Dictionary<string, double>
                    {
                        ["skillCooldownTime"] = 3, ["offensiveFireMin"] = 100
                    })
                },
                [controllerName] = new GameRecord
                {
                    RecordName = controllerName,
                    TextValues = new Dictionary<string, IReadOnlyList<string>> { ["triggerType"] = ["AttackEnemy"] }
                }
            }
        };

        var proc = Assert.Single(SaveReader.BuildTriggeredEffects([procOwner], catalog));

        Assert.Equal(0.15, proc.Chance, 8);
        Assert.Equal(3, proc.CooldownSeconds);
        Assert.Empty(SaveReader.BuildTriggeredEffects([grantedOwner], catalog));
    }

    [Fact]
    public void ExpectedItemScaleAppliesToDamageButNotFixedUtilityStats()
    {
        var stats = new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.WeaponDamage("fire")] = 100,
            [StatKeys.DamagePercent("fire")] = 50,
            ["health"] = 1000
        });

        SaveReader.ApplyExpectedItemScale(stats, 20);

        Assert.Equal(120, stats[StatKeys.WeaponDamage("fire")], 8);
        Assert.Equal(60, stats[StatKeys.DamagePercent("fire")], 8);
        Assert.Equal(1000, stats["health"]);
    }

    private static CharacterSnapshot Character() => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000
    };

    private static CharacterSnapshot CharacterWithSkills(params CharacterSkill[] skills) => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000, Skills = skills
    };
}
