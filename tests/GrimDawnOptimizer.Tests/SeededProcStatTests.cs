using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SeededProcStatTests
{
    [Fact]
    public void ChanceFlatDamagePreservesItsRangeWithoutReducingOrdinaryDamageOfSameType()
    {
        var prefix = Affix(("offensiveFireMin", 10), ("offensiveFireMax", 30));
        var suffix = Affix(("offensiveFireMin", 100), ("offensiveFireMax", 200), ("offensiveFireChance", 20));

        var result = SeededItemStatResolver.Resolve(Weapon(), prefix, suffix, 1);

        Assert.True(result.IsExact);
        Assert.Equal(50, result.Stats[StatKeys.DamageFlat("fire")]); // 20 ordinary + 150 * 20%.
    }

    [Fact]
    public void ChanceDotPreservesItsOwnDurationAndOrdinaryDotOfSameType()
    {
        var prefix = Affix(("offensiveSlowFireMin", 5), ("offensiveSlowFireDurationMin", 2));
        var suffix = Affix(("offensiveSlowFireMin", 100), ("offensiveSlowFireMax", 200),
            ("offensiveSlowFireDurationMin", 3), ("offensiveSlowFireChance", 10));

        var result = SeededItemStatResolver.Resolve(Weapon(), prefix, suffix, 1);

        Assert.True(result.IsExact);
        Assert.Equal(20, result.Stats[StatKeys.DamageDot("fire")]); // 5 ordinary + 150 * 10%.
        Assert.Equal(55, result.Stats["damage.fire.dotTotal"]); // 5*2 + 150*3*10%.
    }

    [Fact]
    public void ChancePercentDamageDoesNotApplyChanceTwiceOrReduceAnOrdinaryModifier()
    {
        var prefix = Affix(("offensiveFireModifier", 50));
        var suffix = Affix(("offensiveFireModifier", 100), ("offensiveFireModifierChance", 20));

        var result = SeededItemStatResolver.Resolve(Weapon(), prefix, suffix, 1);

        Assert.True(result.IsExact);
        Assert.Equal(70, result.Stats[StatKeys.DamagePercent("fire")]);
    }

    [Fact]
    public void CurrentExpansionPistolMatchesObservedSeededStatsIncludingChanceBurn()
    {
        // Frozen UI observation, seed 1315855443: 7-12 fire, 2-22 lightning,
        // 10% chance of 300 burn over 3 seconds, +65% fire, +54% burn, 14% conversion.
        var weapon = Record("WeaponHunting_Ranged1h", ("attributeScalePercent", 30),
            ("offensivePhysicalMin", 29), ("offensivePhysicalMax", 109));
        var prefix = Record("LootRandomizer", ("lootRandomizerJitter", 10),
            ("offensiveLightningMin", 3), ("offensiveLightningMax", 23), ("conversionPercentage", 15));
        prefix = new GameRecord
        {
            RecordName = prefix.RecordName, RecordType = prefix.RecordType, NumericValues = prefix.NumericValues,
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Class"] = ["LootRandomizer"], ["conversionInType"] = ["Physical"], ["conversionOutType"] = ["Lightning"]
            }
        };
        var suffix = Record("LootRandomizer", ("lootRandomizerJitter", 15),
            ("offensiveFireMin", 6), ("offensiveFireMax", 10), ("offensiveFireModifier", 45),
            ("offensiveSlowFireMin", 82), ("offensiveSlowFireDurationMin", 3),
            ("offensiveSlowFireChance", 10), ("offensiveSlowFireModifier", 45));

        var result = SeededItemStatResolver.Resolve(weapon, prefix, suffix, 1315855443);

        Assert.True(result.IsExact);
        Assert.Equal(69, result.Stats[StatKeys.WeaponDamage("physical")]);
        Assert.Equal(9.5, result.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(12, result.Stats[StatKeys.DamageFlat("lightning")]);
        Assert.Equal(10, result.Stats[StatKeys.DamageDot("fire")]);
        Assert.Equal(30, result.Stats["damage.fire.dotTotal"]);
        Assert.Equal(65, result.Stats[StatKeys.DamagePercent("fire")]);
        Assert.Equal(54, result.Stats[StatKeys.DamageDotPercent("fire")]);
        Assert.Equal(13.591328859329224, result.Stats["conversion.physical.lightning"], 6);
        Assert.Equal(0, result.Stats["criticalDamage"]);
    }

    private static GameRecord Weapon() => Record("WeaponHunting_Ranged1h");
    private static GameRecord Affix(params (string Key, double Value)[] values) => Record("LootRandomizer", values);
    private static GameRecord Record(string type, params (string Key, double Value)[] values) => new()
    {
        RecordName = "records/items/fixture.dbr", RecordType = type,
        NumericValues = new StatBlock(values.ToDictionary(x => x.Key, x => x.Value)),
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = [type] }
    };
}
