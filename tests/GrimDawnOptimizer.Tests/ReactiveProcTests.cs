using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ReactiveProcTests
{
    private readonly CombatSimulator _simulator = new();

    [Fact]
    public async Task NoIncomingAttacksMeansNoOnHitDamageEvenWhenThePlayerKeepsAttacking()
    {
        var scenario = Scenario(incomingRate: 0);
        var rotation = Rotation(attacksPerSecond: 3);
        var loadout = Loadout(proc: Proc());
        var expected = _simulator.Simulate(Character(), loadout, scenario, rotation);
        var sampled = await _simulator.RunMonteCarloAsync(Character(), loadout, scenario, rotation, 200, 42);

        Assert.Equal(0, expected.ExpectedDps);
        Assert.Equal(0, sampled.MonteCarloDpsMean);
        Assert.Equal(0, expected.UnmitigatedDps);
    }

    [Fact]
    public void ChangingOwnAttackSpeedDoesNotChangeTheReactiveProcIncrement()
    {
        var scenario = Scenario(incomingRate: 4);
        double Increment(double attacksPerSecond)
        {
            var rotation = Rotation(attacksPerSecond);
            var baseline = _simulator.Simulate(Character(), Loadout(weaponDamage: 80), scenario, rotation);
            var withProc = _simulator.Simulate(Character(), Loadout(proc: Proc(), weaponDamage: 80), scenario, rotation);
            Assert.Equal(baseline.UnmitigatedDps, withProc.UnmitigatedDps);
            return withProc.ExpectedDps - baseline.ExpectedDps;
        }

        var slowIncrement = Increment(.5);
        var fastIncrement = Increment(3);

        // Four incoming attempts, 90% hit chance, 25% proc chance, 100 damage.
        Assert.Equal(90, slowIncrement, 8);
        Assert.Equal(slowIncrement, fastIncrement, 8);
    }

    [Theory]
    [InlineData(1000, 1000, 0, 90)]
    [InlineData(1000, 1000, 25, 67.5)]
    [InlineData(2000, 500, 0, 100)]
    [InlineData(2000, 500, 50, 50)]
    public void ReactiveProcUsesEnemyHitChanceAndPlayerDodge(
        double enemyOffense, double playerDefense, double dodge, double expectedDps)
    {
        var scenario = Scenario(incomingRate: 4, enemyOffense);
        var loadout = Loadout(proc: Proc(), playerDefense: playerDefense, dodge: dodge);

        var result = _simulator.Simulate(Character(), loadout, scenario, Rotation());

        Assert.Equal(expectedDps, result.ExpectedDps, 8);
        Assert.Equal(0, result.UnmitigatedDps);
    }

    [Fact]
    public void IncomingFrequencyChangesReactiveOutputWithoutChangingPlayerAttacks()
    {
        var loadout = Loadout(proc: Proc());
        var low = _simulator.Simulate(Character(), loadout, Scenario(1), Rotation());
        var high = _simulator.Simulate(Character(), loadout, Scenario(4), Rotation());

        Assert.Equal(22.5, low.ExpectedDps, 8);
        Assert.Equal(4 * low.ExpectedDps, high.ExpectedDps, 8);
    }

    [Fact]
    public async Task UnresolvedTriggerCannotCauseDamageInEitherSimulationPath()
    {
        var unresolved = Proc() with { TriggerResolved = false };
        var loadout = Loadout(proc: unresolved);
        var scenario = Scenario(4);
        var expected = _simulator.Simulate(Character(), loadout, scenario, Rotation());
        var sampled = await _simulator.RunMonteCarloAsync(Character(), loadout, scenario, Rotation(), 200, 12);

        Assert.Equal(0, expected.ExpectedDps);
        Assert.Equal(0, sampled.MonteCarloDpsMean);
    }

    [Fact]
    public async Task ReactiveProcMonteCarloMeanAgreesWithIncomingEventExpectation()
    {
        var scenario = Scenario(4);
        var loadout = Loadout(proc: Proc(), dodge: 25);
        var result = await _simulator.RunMonteCarloAsync(Character(), loadout, scenario, Rotation(), 2500, 154);

        Assert.Equal(67.5, result.ExpectedDps, 8);
        Assert.InRange(result.MonteCarloDpsMean, result.ExpectedDps * .985, result.ExpectedDps * 1.015);
        Assert.True(result.MonteCarloDpsLow95 < result.ExpectedDps);
        Assert.True(result.MonteCarloDpsHigh95 > result.ExpectedDps);
        Assert.Equal(0, result.UnmitigatedDps);
    }

    [Fact]
    public async Task MultiTargetMonteCarloScalesReactiveAttackDamageButNotPerHitRetaliation()
    {
        var scenario = Scenario(4);
        scenario.TargetCount = 3;
        var loadout = Loadout(proc: Proc(), dodge: 25);
        loadout.Stats.Set(StatKeys.RetaliationFlat("fire"), 40);
        loadout.Stats.Set("retaliation.flatProxy", 40);

        var result = await _simulator.RunMonteCarloAsync(Character(), loadout, scenario, Rotation(), 2500, 154);

        // 2.7 incoming landed hits/s: 67.5 reactive DPS per target, plus
        // 108 retaliation DPS from incoming hits, counted once across all targets.
        Assert.Equal(108, result.RetaliationDps, 8);
        Assert.Equal(310.5, result.ExpectedDps, 8);
        Assert.InRange(result.MonteCarloDpsMean, result.ExpectedDps * .985, result.ExpectedDps * 1.015);
        Assert.Equal(0, result.UnmitigatedDps);
    }

    private static TriggeredEffect Proc() => new("reactive", "Reactive damage", .25, 0,
        new StatBlock(new Dictionary<string, double> { [StatKeys.DamageFlat("fire")] = 100 }), "OnHit");

    private static CalculatedLoadout Loadout(TriggeredEffect? proc = null, double weaponDamage = 0,
        double playerDefense = 1000, double dodge = 0) => new()
    {
        Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(),
        Stats = new StatBlock(new Dictionary<string, double>
        {
            ["health"] = 10000, ["offensiveAbility"] = 1000,
            ["defensiveAbility"] = playerDefense, ["dodgeChance"] = dodge,
            [StatKeys.WeaponDamage("fire")] = weaponDamage
        }),
        Effects = proc is null ? [] : [proc]
    };

    private static CharacterSnapshot Character() => new() { Name = "fixture", SaveFilePath = "fixture.gdc" };
    private static SkillRotationDefinition Rotation(double attacksPerSecond = 1) => new()
    {
        BaseAttacksPerSecond = attacksPerSecond, IsFallback = false, WeaponDamagePercent = 100
    };
    private static CombatScenario Scenario(double incomingRate, double enemyOffense = 1000) => new()
    {
        DurationSeconds = 60, EnemyOffensiveAbility = enemyOffense, EnemyDefensiveAbility = 1000,
        EnemyArmor = 0, EnemyResistances = new Dictionary<string, double>(), TargetCount = 1,
        IncomingDamage = new IncomingDamageProfile(HitsPerSecond: incomingRate, PhysicalPerHit: 20)
    };
}
