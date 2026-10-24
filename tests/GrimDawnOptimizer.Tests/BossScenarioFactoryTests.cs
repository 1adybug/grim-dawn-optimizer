using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class BossScenarioFactoryTests
{
    [Fact]
    public void UsesCurrentLevelBossEquationsArmorCurveAndResistances()
    {
        var monster = Record(
            "records/creatures/enemies/boss&quest/test.dbr", "Monster",
            text: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["Monster"],
                ["monsterClassification"] = ["Boss"],
                ["characterAttributeEquations"] = ["records/creatures/enemies/bios/test.dbr"],
                ["skillName1"] = ["records/skills/nonplayerskills/passive/armor_test.dbr"],
                ["skillLevel1"] = ["charLevel*1"]
            },
            numeric: Stats(("minLevel", 1), ("maxLevel", 250), ("defensiveFire", 15)));
        var equations = Record(
            "records/creatures/enemies/bios/test.dbr", string.Empty,
            text: new Dictionary<string, IReadOnlyList<string>>
            {
                ["characterLife"] = ["charLevel*100"],
                ["characterOffensiveAbility"] = ["charLevel*10+100"],
                ["characterDefensiveAbility"] = ["charLevel*12+100"]
            });
        var armor = Record(
            "records/skills/nonplayerskills/passive/armor_test.dbr", "Skill_Passive",
            series: new Dictionary<string, IReadOnlyList<double>>
            {
                ["defensiveProtection"] = [100, 240],
                ["characterLifeModifier"] = [0, 50],
                ["defensiveCold"] = [10, 25],
                ["defensiveSlowLifeLeach"] = [20, 40]
            });
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [monster.RecordName] = monster,
                [equations.RecordName] = equations,
                [armor.RecordName] = armor
            }
        };
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", Level = 2 };

        var scenario = BossScenarioFactory.ApplyAverageBoss(character, catalog, new CombatScenario());

        Assert.Equal(2, scenario.EnemyLevel);
        Assert.Equal(300, scenario.EnemyHealth);
        Assert.Equal(240, scenario.EnemyArmor);
        Assert.Equal(120, scenario.EnemyOffensiveAbility);
        Assert.Equal(124, scenario.EnemyDefensiveAbility);
        Assert.Equal(15, scenario.EnemyResistances["fire"]);
        Assert.Equal(25, scenario.EnemyResistances["cold"]);
        Assert.Equal(40, scenario.EnemyLifeLeechResistance);
        Assert.Equal(1, scenario.BossSampleCount);
    }

    private static GameRecord Record(
        string name,
        string type,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? text = null,
        StatBlock? numeric = null,
        IReadOnlyDictionary<string, IReadOnlyList<double>>? series = null) => new()
    {
        RecordName = name,
        RecordType = type,
        TextValues = text ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        NumericValues = numeric ?? new StatBlock(),
        NumericSeries = series ?? new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase)
    };

    private static StatBlock Stats(params (string Key, double Value)[] values)
    {
        var result = new StatBlock();
        foreach (var (key, value) in values) result.Set(key, value);
        return result;
    }
}
