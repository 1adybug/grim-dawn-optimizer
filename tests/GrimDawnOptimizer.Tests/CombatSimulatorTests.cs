using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class CombatSimulatorTests
{
    private readonly CombatSimulator _simulator = new();
    private readonly CharacterStatEngine _engine = new();

    [Fact]
    public void ConversionUsesDestinationResistance()
    {
        var withoutConversion = Stats((StatKeys.WeaponDamage("physical"), 100), ("offensiveAbility", 3000));
        var withConversion = withoutConversion.Clone();
        withConversion.Add("conversion.physical.fire", 100);
        var scenario = Scenario();
        scenario.EnemyResistances["physical"] = 80;
        scenario.EnemyResistances["fire"] = 0;

        var physical = Simulate(withoutConversion, scenario);
        var fire = Simulate(withConversion, scenario);

        Assert.True(fire.ExpectedDps > physical.ExpectedDps * 4.5);
    }

    [Fact]
    public void ResistanceAndArmorReduceIncomingDamage()
    {
        var exposed = Simulate(Stats(("health", 10_000), ("defensiveAbility", 2800)), Scenario());
        var protectedStats = Stats(
            ("health", 10_000), ("defensiveAbility", 2800), ("fireResistance", 80),
            ("physicalResistance", 20), ("armor", 1500), ("armorAbsorption", 100));
        var mitigated = Simulate(protectedStats, Scenario());

        Assert.True(mitigated.IncomingDps < exposed.IncomingDps);
        Assert.True(mitigated.EffectiveHealth > exposed.EffectiveHealth);
    }

    [Fact]
    public void BossArmorReducesPhysicalDamageAndIncreasesKillTime()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 3000), ("offensiveAbility", 3000));
        var exposedScenario = Scenario();
        exposedScenario.EnemyHealth = 100_000;
        exposedScenario.EnemyArmor = 0;
        exposedScenario.EnemyArmorAbsorption = 100;
        var armoredScenario = Scenario();
        armoredScenario.EnemyHealth = 100_000;
        armoredScenario.EnemyArmor = 1000;
        armoredScenario.EnemyArmorAbsorption = 100;

        var exposed = Simulate(stats, exposedScenario);
        var armored = Simulate(stats, armoredScenario);

        Assert.True(armored.ExpectedDps < exposed.ExpectedDps);
        Assert.Equal(exposed.UnmitigatedDps, armored.UnmitigatedDps, 8);
        Assert.True(armored.TimeToKillSeconds > exposed.TimeToKillSeconds);
    }

    [Fact]
    public void SkillCriticalModifierAffectsProbabilisticCritsNotTargetFreeSheetDps()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 100), ("offensiveAbility", 20_000));
        var character = Character(stats);
        var loadout = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), new GameDataCatalog());
        var baseline = Rotation();
        var modified = Rotation();
        modified.FlatDamage.Set("criticalDamage", 18);

        var withoutModifier = _simulator.Simulate(character, loadout, Scenario(), baseline);
        var withModifier = _simulator.Simulate(character, loadout, Scenario(), modified);

        Assert.Equal(withoutModifier.UnmitigatedDps, withModifier.UnmitigatedDps, 8);
        Assert.True(withModifier.ExpectedDps > withoutModifier.ExpectedDps);
    }

    [Fact]
    public void WeaponPoolAffectsCombatDpsButNotTargetFreeSheetDps()
    {
        var character = Character(new StatBlock());
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(),
            Stats = Stats((StatKeys.WeaponDamage("physical"), 100), ("offensiveAbility", 3000))
        };
        SkillRotationDefinition RotationWithStrikes(double strikes) => new()
        {
            BaseAttacksPerSecond = 1,
            WeaponDamagePercent = 100,
            WeaponPoolAttacks =
            [
                new WeaponPoolAttackDefinition
                {
                    Chance = 100, WeaponDamageMultiplier = 1, WeaponStrikes = strikes
                }
            ]
        };

        var oneWeapon = _simulator.Simulate(character, loadout, Scenario(), RotationWithStrikes(1));
        var bothWeapons = _simulator.Simulate(character, loadout, Scenario(), RotationWithStrikes(2));

        Assert.Equal(oneWeapon.UnmitigatedDps, bothWeapons.UnmitigatedDps, 8);
        Assert.Equal(oneWeapon.ExpectedDps * 2, bothWeapons.ExpectedDps, 8);
    }

    [Fact]
    public void WeaponSpeedFactorIsAppliedBeforeTheFinalSpeedCap()
    {
        var character = Character(new StatBlock());
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(),
            Stats = Stats(
                (StatKeys.WeaponDamage("physical"), 100),
                ("offensiveAbility", 3000),
                ("attackSpeed", 27),
                ("weaponAttackSpeedFactor", 0.795))
        };

        var report = _simulator.Simulate(character, loadout, Scenario(), new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1.5,
            WeaponDamagePercent = 100
        });

        Assert.Equal(100 * 1.5 * 0.795 * 1.27, report.UnmitigatedDps, 8);
    }

    [Fact]
    public void CooldownAndEnergyCostReductionIncreaseLimitedSkillDps()
    {
        var baseline = Stats(
            (StatKeys.WeaponDamage("physical"), 100), ("offensiveAbility", 3000),
            ("energy", 100), ("energyRegen", 0));
        var improved = baseline.Clone();
        improved.Set("cooldownReduction", 50);
        improved.Set("energyCostReduction", 50);
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 10, WeaponDamagePercent = 100,
            CooldownSeconds = 2, EnergyPerUse = 20
        };

        var withoutReduction = Simulate(baseline, Scenario(), rotation);
        var withReduction = Simulate(improved, Scenario(), rotation);

        Assert.True(withReduction.ExpectedDps > withoutReduction.ExpectedDps * 1.9);
    }

    [Fact]
    public void BleedingResistanceAndBlockAbsorptionAffectEffectiveHealth()
    {
        var scenario = Scenario();
        scenario.IncomingDamage = new IncomingDamageProfile(
            HitsPerSecond: 1, PhysicalPerHit: 500, FirePerHit: 0, ColdPerHit: 0,
            LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0, VitalityPerHit: 0,
            AetherPerHit: 0, ChaosPerHit: 0, BleedingPerHit: 500);
        var partial = Stats(
            ("health", 10_000), ("defensiveAbility", 2_800), ("blockChance", 90),
            ("blockAmount", 500), ("blockAbsorption", 50));
        var protectedStats = partial.Clone();
        protectedStats.Set("bleedingResistance", 80);
        protectedStats.Set("blockAbsorption", 100);

        var partialReport = Simulate(partial, scenario);
        var protectedReport = Simulate(protectedStats, scenario);

        Assert.True(protectedReport.IncomingDps < partialReport.IncomingDps);
        Assert.True(protectedReport.EffectiveHealth > partialReport.EffectiveHealth);
    }

    [Fact]
    public void RetaliationContributesToBossDpsUsingBossHitRateAndResistance()
    {
        var stats = Stats(
            ("health", 10_000), ("defensiveAbility", 2_800),
            (StatKeys.RetaliationFlat("fire"), 100),
            ("retaliation.flatProxy", 100),
            ("retaliation.total.percent", 100),
            ("lifeSteal", 100));
        var scenario = Scenario();
        scenario.IncomingDamage = scenario.IncomingDamage with { HitsPerSecond = 2 };
        scenario.EnemyResistances["fire"] = 50;

        var report = Simulate(stats, scenario);

        // Equal OA/DA gives the boss 90% hit chance: 2 * 0.9 triggers/s,
        // with 200 retaliation reduced to 100 by the boss's fire resistance.
        Assert.Equal(180, report.RetaliationDps, 8);
        Assert.Equal(report.RetaliationDps, report.ExpectedDps, 8);
        Assert.Equal(0, report.UnmitigatedDps, 8);
        Assert.Equal(0, report.SustainPerSecond, 8);
    }

    [Fact]
    public void BossAdjustedLifeStealContributesToEffectiveHealthCompletion()
    {
        var baseline = Stats(
            ("health", 10_000), ("defensiveAbility", 2_800), ("offensiveAbility", 3_000),
            (StatKeys.WeaponDamage("physical"), 1_000));
        var withLifeSteal = baseline.Clone();
        withLifeSteal.Set("lifeSteal", 10);
        var scenario = Scenario();
        scenario.EnemyLifeLeechResistance = 50;

        var without = Simulate(baseline, scenario);
        var with = Simulate(withLifeSteal, scenario);

        Assert.Equal(with.ExpectedDps * 0.05, with.SustainPerSecond, 8);
        Assert.True(with.EffectiveHealth > without.EffectiveHealth);
        Assert.True(with.TimeToDeathSeconds > without.TimeToDeathSeconds);

        scenario.EnemyLifeLeechResistance = 100;
        var immuneBoss = Simulate(withLifeSteal, scenario);
        Assert.Equal(without.EffectiveHealth, immuneBoss.EffectiveHealth, 8);
    }

    [Fact]
    public async Task MonteCarloIsReproducibleForFixedSeed()
    {
        var character = Character(Stats(
            ("health", 10_000), ("defensiveAbility", 2700), ("offensiveAbility", 3000),
            (StatKeys.WeaponDamage("physical"), 140)));
        var loadout = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), new GameDataCatalog());

        var first = await _simulator.RunMonteCarloAsync(character, loadout, Scenario(), Rotation(), 500, 9421);
        var second = await _simulator.RunMonteCarloAsync(character, loadout, Scenario(), Rotation(), 500, 9421);

        Assert.Equal(first.MonteCarloDpsMean, second.MonteCarloDpsMean);
        Assert.Equal(first.MonteCarloDpsLow95, second.MonteCarloDpsLow95);
        Assert.InRange(first.ExpectedDps, first.MonteCarloDpsLow95, first.MonteCarloDpsHigh95);
    }

    private SimulationReport Simulate(StatBlock stats, CombatScenario scenario)
    {
        var character = Character(stats);
        var loadout = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), new GameDataCatalog());
        return _simulator.Simulate(character, loadout, scenario, Rotation());
    }

    private SimulationReport Simulate(
        StatBlock stats,
        CombatScenario scenario,
        SkillRotationDefinition rotation)
    {
        var character = Character(stats);
        var loadout = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), new GameDataCatalog());
        return _simulator.Simulate(character, loadout, scenario, rotation);
    }

    private static CharacterSnapshot Character(StatBlock baseline) => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000, BaselineStats = baseline
    };

    private static CombatScenario Scenario() => new()
    {
        EnemyLevel = 100,
        EnemyArmor = 0,
        EnemyOffensiveAbility = 2800,
        EnemyLifeLeechResistance = 0,
        DurationSeconds = 10,
        IncomingDamage = new IncomingDamageProfile(
            HitsPerSecond: 1, PhysicalPerHit: 1200, FirePerHit: 800,
            ColdPerHit: 0, LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0,
            VitalityPerHit: 0, AetherPerHit: 0, ChaosPerHit: 0)
    };

    private static SkillRotationDefinition Rotation() => new()
    {
        BaseAttacksPerSecond = 1,
        WeaponDamagePercent = 100
    };

    private static StatBlock Stats(params (string Key, double Value)[] values)
    {
        var result = new StatBlock();
        foreach (var (key, value) in values) result.Add(key, value);
        return result;
    }
}
