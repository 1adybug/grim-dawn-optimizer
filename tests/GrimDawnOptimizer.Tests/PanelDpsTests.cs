using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PanelDpsTests
{
    [Fact]
    public void SecondaryWeaponDamageIsOnePayloadAfterTheDualWieldMainAttack()
    {
        var stats = Stats((StatKeys.WeaponDamage("pierce"), 100), ("averageWeaponStrikesPerAction", 1.5));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1, WeaponDamagePercent = 160,
            SecondaryAttacks = [new() { WeaponDamagePercent = 35 }]
        };
        // Measured Fire Strike relation: primary per-action pierce * 160%
        // plus the single weapon payload * 35%, not primary pierce * 195%.
        Assert.Equal(275, Run(stats, rotation).UnmitigatedDps, 8);
    }

    [Fact]
    public void SecondaryDotDoesNotUseTheMainAttacksDualWieldWeight()
    {
        var stats = Stats((StatKeys.DamageDot("fire"), 10), ("averageWeaponStrikesPerAction", 1.5));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1, WeaponDamagePercent = 160,
            SecondaryAttacks = [new() { WeaponDamagePercent = 35, FlatDamage = Stats((StatKeys.DamageDot("fire"), 102)) }]
        };
        Assert.Equal(117.5, Run(stats, rotation).UnmitigatedDps, 8);
    }

    [Fact]
    public void DualWieldDpsFiltersDuplicateDurationSourcesBeforeHandWeighting()
    {
        var stats = Stats((StatKeys.DamageDot("fire"), 10), ("averageWeaponStrikesPerAction", 1.5));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1, WeaponDamagePercent = 195,
            FlatDamage = Stats((StatKeys.DamageDot("fire"), 102))
        };
        Assert.Equal(91.125, Run(stats, rotation).UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void BurnUsesItsOwnAndAllDamageBonuses(double fireBonus)
    {
        var stats = Stats(
            (StatKeys.DamageDot("fire"), 20),
            (StatKeys.DamagePercent("fire"), fireBonus),
            (StatKeys.DamageDotPercent("fire"), 50),
            ("damage.total.percent", 25));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1,
            FlatDamage = Stats(
                (StatKeys.DamagePercent("fire"), 200),
                (StatKeys.DamageDotPercent("fire"), 75))
        };

        Assert.Equal(50, Run(stats, rotation).UnmitigatedDps, 8);
    }

    [Fact]
    public void PanelMultipliesDotContributionByAttackRateWhileSustainedDotDoesNotStack()
    {
        var stats = Stats((StatKeys.DamageDot("fire"), 100));
        var slow = Run(stats, new SkillRotationDefinition { BaseAttacksPerSecond = 1 });
        var fast = Run(stats, new SkillRotationDefinition { BaseAttacksPerSecond = 2 });

        Assert.Equal(100, slow.UnmitigatedDps, 8);
        Assert.Equal(200, fast.UnmitigatedDps, 8);
        Assert.Equal(slow.ExpectedDps, fast.ExpectedDps, 8);
    }

    [Fact]
    public async Task TargetCountChangesCombatDamageButNotTheCharacterPanel()
    {
        var stats = Stats((StatKeys.WeaponDamage("fire"), 100));
        var rotation = new SkillRotationDefinition { BaseAttacksPerSecond = 1 };
        var single = Run(stats, rotation, 1);
        var multiple = Run(stats, rotation, 3);

        Assert.Equal(100, single.UnmitigatedDps, 8);
        Assert.Equal(single.UnmitigatedDps, multiple.UnmitigatedDps, 8);
        Assert.Equal(single.ExpectedDps * 3, multiple.ExpectedDps, 8);

        var sampled = await new CombatSimulator().RunMonteCarloAsync(
            Character(), Loadout(stats), Scenario(3), rotation, 100, 42);
        Assert.Equal(single.UnmitigatedDps, sampled.UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(50, 25)]
    [InlineData(100, 35)]
    [InlineData(200, 55)]
    public void EquipmentDotUsesWeaponPercentageWhileSkillDotIsPreserved(
        double weaponPercentage, double expectedPanelDps)
    {
        var stats = Stats((StatKeys.DamageDot("acid"), 20));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1,
            WeaponDamagePercent = weaponPercentage,
            FlatDamage = Stats((StatKeys.DamageDot("acid"), 15))
        };

        var report = Run(stats, rotation);

        Assert.Equal(expectedPanelDps, report.UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PureSpellDoesNotInheritWeaponDamageOrDualWieldStrikes(double weaponStrikes)
    {
        var stats = Stats(
            (StatKeys.WeaponDamage("fire"), 1000),
            (StatKeys.DamageFlat("fire"), 700),
            (StatKeys.DamageDot("fire"), 40),
            ("averageWeaponStrikesPerAction", weaponStrikes));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1,
            WeaponDamagePercent = 0,
            UsesAttackSpeed = false,
            FlatDamage = Stats((StatKeys.DamageFlat("fire"), 50), (StatKeys.DamageDot("fire"), 7))
        };

        var report = Run(stats, rotation);

        Assert.Equal(57, report.UnmitigatedDps, 8);
        var noWeapon = Run(new StatBlock(), rotation);
        Assert.Equal(noWeapon.ExpectedDps, report.ExpectedDps, 8);
    }

    [Fact]
    public void WeaponAttackRetainsDualWieldStrikes()
    {
        var stats = Stats((StatKeys.WeaponDamage("fire"), 100), ("averageWeaponStrikesPerAction", 1.5));

        var report = Run(stats, new SkillRotationDefinition { BaseAttacksPerSecond = 1 });

        Assert.Equal(150, report.UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeaponPoolDotScalesEquipmentDamageWithoutChangingThePanel(bool legacy)
    {
        var stats = Stats((StatKeys.DamageDot("acid"), 20));
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1,
            WeaponDamagePercent = 200,
            FlatDamage = Stats((StatKeys.DamageDot("acid"), 15))
        };
        var withoutPool = Run(stats, rotation);
        if (legacy)
        {
            rotation.WeaponPoolDamageMultiplier = 1.5;
            rotation.WeaponPoolFlatDamage = Stats((StatKeys.DamageDot("acid"), 10));
        }
        else
        {
            rotation.WeaponPoolAttacks =
            [
                new WeaponPoolAttackDefinition
                {
                    Chance = 100,
                    WeaponDamageMultiplier = 1.5,
                    FlatDamage = Stats((StatKeys.DamageDot("acid"), 10))
                }
            ];
        }

        var withPool = Run(stats, rotation);

        Assert.Equal(55, withoutPool.UnmitigatedDps, 8);
        Assert.Equal(withoutPool.UnmitigatedDps, withPool.UnmitigatedDps, 8);
        // One guaranteed single-strike replacement: 20 * 200% * 150% + 15 + 10.
        // With the same target and no direct hit, only the DoT amount changes.
        Assert.Equal(withoutPool.ExpectedDps * 85 / 55, withPool.ExpectedDps, 8);
    }

    private static SimulationReport Run(StatBlock stats, SkillRotationDefinition rotation, int targets = 1) =>
        new CombatSimulator().Simulate(Character(), Loadout(stats), Scenario(targets), rotation);

    private static CharacterSnapshot Character() => new() { Name = "panel", SaveFilePath = "test.gdc" };

    private static CalculatedLoadout Loadout(StatBlock stats) => new()
    {
        Stats = stats,
        Slots = new Dictionary<EquipmentSlot, LoadoutVariant>()
    };

    private static CombatScenario Scenario(int targets) => new()
    {
        TargetCount = targets,
        EnemyArmor = 0,
        EnemyResistances = new Dictionary<string, double>()
    };

    private static StatBlock Stats(params (string Key, double Value)[] values)
    {
        var stats = new StatBlock();
        foreach (var (key, value) in values) stats.Set(key, value);
        return stats;
    }
}
