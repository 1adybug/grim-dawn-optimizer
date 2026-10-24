using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PlayerAttributeRulesTests
{
    [Fact]
    public void DirectAndDurationDamageUseSeparateDatabaseEquationsIncludingInitialAttributes()
    {
        var stats = new StatBlock(new Dictionary<string, double> { ["cunning"] = 50, ["spirit"] = 50 });
        var formulas = new GameRecord
        {
            RecordName = "records/game/combatformulas.dbr",
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["physicalDamageEquation"] = ["physicalDamageDV * (1 + dexterityDV / 100)"],
                ["pierceDamageEquation"] = ["pierceDamageDV * (1 + dexterityDV / 200)"],
                ["physicalDurationDamageEquation"] = ["physicalDamageDV * (1 + dexterityDV / 125)"],
                ["magicalDamageEquation"] = ["magicalDamageDV * (1 + intelligenceDV / 250)"],
                ["magicalDurationDamageEquation"] = ["magicalDamageDV * (1 + intelligenceDV / 50)"]
            }
        };
        PlayerAttributeRules.ApplyDamageBonuses(stats, Catalog(formulas));

        Assert.Equal(50, stats[StatKeys.DamagePercent("physical")], 8);
        Assert.Equal(25, stats[StatKeys.DamagePercent("pierce")], 8);
        Assert.Equal(40, stats[StatKeys.DamagePercent("bleeding")], 8);
        Assert.Equal(40, stats[StatKeys.DamageDotPercent("physical")], 8);
        Assert.Equal(20, stats[StatKeys.DamagePercent("fire")], 8);
        Assert.Equal(100, stats[StatKeys.DamageDotPercent("fire")], 8);
        Assert.Equal(0, stats[StatKeys.DamageDotPercent("aether")]);
    }

    [Fact]
    public void MasteryAndEquipmentAttributesAddResourcesWithoutCountingPurchasedAttributePointsTwice()
    {
        var mastery = new GameRecord
        {
            RecordName = "skills/mastery.dbr", RecordType = "Skill_Mastery",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["characterStrength"] = 20, ["characterDexterity"] = 10, ["characterIntelligence"] = 10,
                ["characterLife"] = 100, ["characterMana"] = 60
            })
        };
        var raw = new RawCharacter("test.gdc", "test", "class", 1, false)
        { Health = 300, Energy = 250, Physique = 70, Cunning = 50, Spirit = 50 };
        raw.Skills.Add(new RawSkill(mastery.RecordName, 1, true, false, 0, 0, 0, true, false, "", ""));
        var catalog = Catalog(mastery);
        var character = new CharacterSnapshot
        {
            Name = raw.Name, SaveFilePath = raw.FilePath, Level = raw.Level,
            BaselineStats = SaveReader.BuildBaseline(raw, catalog),
            Skills = [new CharacterSkill(mastery.RecordName, "mastery", 1, 0, true, "", "")]
        };
        character.BaselineStats.Set("health.percent", 10);
        var result = new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);

        // Saved 300 HP already includes the 20 bought Physique points. Only
        // mastery grants and the extra 20/10/10 attributes are added here.
        Assert.Equal((300 + 100 + 20 * 2.5 + 10 + 10 * 1.5) * 1.1, result.Stats["health"], 8);
        Assert.Equal(250 + 60 + 10 * 2, result.Stats["energy"], 8);
    }

    [Fact]
    public void ResourceIncrementsComeFromTheLoadedDatabase()
    {
        var levels = new GameRecord
        {
            RecordName = "records/creatures/pc/playerlevels.dbr",
            NumericValues = new StatBlock(new Dictionary<string, double>
            {
                ["strengthIncrement"] = 10, ["dexterityIncrement"] = 10, ["intelligenceIncrement"] = 10,
                ["lifeIncrement"] = 30, ["lifeIncrementDexterity"] = 20, ["lifeIncrementIntelligence"] = 10, ["manaIncrement"] = 40
            })
        };
        var stats = new StatBlock(new Dictionary<string, double>
        {
            ["usePlayerAttributeFormula"] = 1, ["savedPhysique"] = 50, ["savedCunning"] = 50, ["savedSpirit"] = 50,
            ["physique"] = 60, ["cunning"] = 70, ["spirit"] = 80, ["health"] = 100, ["energy"] = 100
        });
        PlayerAttributeRules.ApplyResourceBonuses(stats, Catalog(levels));

        Assert.Equal(100 + 30 + 40 + 30, stats["health"]);
        Assert.Equal(100 + 120, stats["energy"]);
    }

    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    { Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase) };
}
