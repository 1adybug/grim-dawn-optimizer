using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class DifficultyRulesTests
{
    [Theory]
    [InlineData(GameDifficulty.Normal, 0, 0)]
    [InlineData(GameDifficulty.Veteran, 0, 0)]
    [InlineData(GameDifficulty.Elite, -25, 0)]
    [InlineData(GameDifficulty.Ultimate, -50, -25)]
    public void StandardPenaltiesDistinguishResistanceGroups(GameDifficulty difficulty, double primary, double secondary)
    {
        var scenario = new CombatScenario { Difficulty = difficulty };
        foreach (var type in new[] { "fire", "cold", "lightning", "acid", "pierce" })
            Assert.Equal(primary, DifficultyRules.PlayerPenalty(scenario, type));
        foreach (var type in new[] { "vitality", "aether", "chaos", "bleeding" })
            Assert.Equal(secondary, DifficultyRules.PlayerPenalty(scenario, type));
        Assert.Equal(0, DifficultyRules.PlayerPenalty(scenario, "physical"));
    }

    [Fact]
    public void ScreenshotPresetsReplaceDatabaseHealthAndDamageWithoutAccumulation()
    {
        var catalog = Catalog();
        var scenario = new CombatScenario();
        foreach (var (difficulty, life, damage, penalty) in new[]
        {
            (GameDifficulty.Normal, 0d, 0d, 0d),
            (GameDifficulty.Veteran, 140d, 15d, 0d),
            (GameDifficulty.Elite, 270d, 21d, -25d),
            (GameDifficulty.Ultimate, 530d, 30d, -50d),
            (GameDifficulty.Normal, 0d, 0d, 0d)
        })
        {
            scenario.Difficulty = difficulty;
            DifficultyRules.Apply(catalog, scenario);
            Assert.Equal(life, scenario.DifficultyEnemyModifiers["health.percent"]);
            Assert.Equal(damage, scenario.DifficultyEnemyModifiers["damage.total.percent"]);
            Assert.Equal(penalty, DifficultyRules.PlayerPenalty(scenario, "fire"));
            Assert.Equal(750, scenario.IncomingDamage.PhysicalPerHit);
        }
    }

    [Fact]
    public void OvercapAbsorbsDebuffBeforeResistanceIsCapped()
    {
        var stats = new StatBlock(new Dictionary<string, double> { ["fireResistance"] = 150 });
        var scenario = new CombatScenario { Difficulty = GameDifficulty.Ultimate };
        scenario.PlayerResistanceReduction["fire"] = 20;
        Assert.Equal(80, DifficultyRules.EffectiveResistance(stats, scenario, "fire"));
        scenario.PlayerResistanceReduction["fire"] = 35;
        Assert.Equal(65, DifficultyRules.EffectiveResistance(stats, scenario, "fire"));
        stats.Set("fireResistance", 30);
        Assert.Equal(-55, DifficultyRules.EffectiveResistance(stats, scenario, "fire"));
        Assert.Equal(30, stats["fireResistance"]);
    }

    [Fact]
    public async Task DifficultyAndDebuffsAffectCombatAndMonteCarloButNotSheetDps()
    {
        var character = new CharacterSnapshot { Name = "difficulty", SaveFilePath = "test.gdc" };
        var stats = new StatBlock(new Dictionary<string, double>
        {
            ["health"] = 10000, ["defensiveAbility"] = 2800, ["offensiveAbility"] = 2800,
            ["fireResistance"] = 80, ["weaponDamage.fire"] = 100
        });
        var loadout = new CalculatedLoadout { Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = stats };
        var scenario = new CombatScenario
        {
            EnemyOffensiveAbility = 2800,
            IncomingDamage = new IncomingDamageProfile(1, 0, 1000, 0, 0, 0, 0, 0, 0, 0, 0)
        };
        var simulator = new CombatSimulator();
        var rotation = new SkillRotationDefinition();
        var normal = simulator.Simulate(character, loadout, scenario, rotation);
        scenario.Difficulty = GameDifficulty.Ultimate;
        scenario.PlayerResistanceReduction["fire"] = 20;
        scenario.EnemyDamageIncreasePercent = 50;
        var ultimate = simulator.Simulate(character, loadout, scenario, rotation);
        Assert.Equal(normal.UnmitigatedDps, ultimate.UnmitigatedDps, 8);
        Assert.Equal(8.775, ultimate.IncomingDps / normal.IncomingDps, 8);
        Assert.True(ultimate.EffectiveHealth < normal.EffectiveHealth);
        var sampled = await simulator.RunMonteCarloAsync(character, loadout, scenario, rotation, 500, 17);
        Assert.Equal(ultimate.IncomingDps, sampled.IncomingDps, 8);
        Assert.Equal(1950, BossMetricBenchmarkResolver.RawIncomingDps(scenario), 8);
        Assert.Equal(10, BeamSearchOptimizer.ExtractMetrics(loadout, ultimate, scenario)["fireResistance"]);
    }

    [Fact]
    public void BossRefreshIsIdempotentAndSwitchingBackRestoresStats()
    {
        var character = new CharacterSnapshot { Name = "difficulty", SaveFilePath = "test.gdc", Level = 56 };
        var scenario = new CombatScenario();
        var catalog = Catalog();
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        var normal = scenario.EnemyHealth;
        scenario.Difficulty = GameDifficulty.Veteran;
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        var veteran = scenario.EnemyHealth;
        Assert.True(veteran > normal);
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        Assert.Equal(veteran, scenario.EnemyHealth);
        scenario.Difficulty = GameDifficulty.Normal;
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        Assert.Equal(normal, scenario.EnemyHealth);
    }

    [Fact]
    public async Task ProfilePersistsDifficultyAndDebuffsWithoutDerivedMultipliers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"difficulty-{Guid.NewGuid():N}.json");
        try
        {
            var profile = new OptimizationProfile();
            profile.Scenario.Difficulty = GameDifficulty.Ultimate;
            profile.Scenario.EnemyDamageIncreasePercent = 40;
            profile.Scenario.PlayerResistanceReduction["fire"] = 30;
            DifficultyRules.Apply(Catalog(), profile.Scenario);
            var store = new JsonProfileStore(path);
            await store.SaveAsync([profile]);
            var restored = Assert.Single(await store.LoadAsync());
            Assert.Equal(7, restored.SchemaVersion);
            Assert.Equal(GameDifficulty.Ultimate, restored.Scenario.Difficulty);
            Assert.Equal(40, restored.Scenario.EnemyDamageIncreasePercent);
            Assert.Equal(30, restored.Scenario.PlayerResistanceReduction["fire"]);
            Assert.Empty(restored.Scenario.DifficultyEnemyModifiers.Values);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(GameDifficulty.Normal, 1, 1)]
    [InlineData(GameDifficulty.Veteran, 2.4, 1.15)]
    [InlineData(GameDifficulty.Elite, 3.7, 1.21)]
    [InlineData(GameDifficulty.Ultimate, 6.3, 1.3)]
    public void PresetsScaleBossOwnHealthAndIncomingTemplateOnce(GameDifficulty difficulty, double lifeFactor, double damageFactor)
    {
        var boss = new GameRecord
        {
            RecordName = "records/creatures/enemies/boss/example.dbr", RecordType = "Monster",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["characterLife"] = new double[] { 1000 }, ["characterLifeModifier"] = new double[] { 100 } }
        };
        var records = new Dictionary<string, GameRecord>(Catalog().Records) { [boss.RecordName] = boss };
        var catalog = new GameDataCatalog { Records = records };
        var character = new CharacterSnapshot { Name = "preset", SaveFilePath = "test.gdc", Level = 56 };
        var scenario = new CombatScenario { Difficulty = difficulty };
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        Assert.Equal(2000 * lifeFactor, scenario.EnemyHealth, 8);
        Assert.Equal(1, scenario.BossSampleCount);
        var expected = scenario.EnemyHealth;
        BossScenarioFactory.ApplyAverageBoss(character, catalog, scenario);
        Assert.Equal(expected, scenario.EnemyHealth, 8);
        var incoming = DifficultyRules.Incoming(scenario);
        Assert.Equal(scenario.IncomingDamage.PhysicalPerHit * damageFactor, incoming.PhysicalPerHit, 8);
        Assert.Equal(scenario.IncomingDamage.HitsPerSecond, incoming.HitsPerSecond, 8);
        Assert.Equal(scenario.EnemyHealth / scenario.DurationSeconds, BossMetricBenchmarkResolver.Resolve("dps", scenario), 8);
    }

    private static GameDataCatalog Catalog()
    {
        GameRecord Record(string name, Dictionary<string, IReadOnlyList<double>> values) => new()
        {
            RecordName = name, NumericSeries = values
        };
        // Deliberately different P2..P4 values catch indexing/averaging errors.
        var enemy = Record("records/game/balancingadjustment_mp+difficulty_enemies01.dbr", new()
        {
            ["characterLifeModifier"] = [50, 51, 52, 53, 320, 321, 322, 323, 580, 581, 582, 583],
            ["offensiveTotalDamageModifier"] = [-25, -24, -23, -22, 25, 26, 27, 28, 40, 41, 42, 43]
        });
        var player = Record("records/game/balancingadjustment_mp+difficulty_players01.dbr", new()
        {
            ["defensiveFire"] = [0, 0, 0, 0, -25, -25, -25, -25, -50, -50, -50, -50]
        });
        var veteran = Record("records/game/balancingadjustment_challengemode_enemies01.dbr", new()
        {
            ["characterLifeModifier"] = [140], ["offensiveTotalDamageModifier"] = [40]
        });
        return new GameDataCatalog { Records = new[] { enemy, player, veteran }.ToDictionary(x => x.RecordName) };
    }
}
