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
        Assert.True(armored.TimeToKillSeconds > exposed.TimeToKillSeconds);
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
        IncomingDamage = new IncomingDamageProfile(1, 1200, 800)
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
