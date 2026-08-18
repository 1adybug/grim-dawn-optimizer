using GrimDawnItemStats;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SeededItemStatResolverTests
{
    [Fact]
    public void MinstdStreamMatchesParkMillerReferenceSequence()
    {
        var random = new MinstdRandom(1);

        Assert.Equal(16807, random.State);
        Assert.Equal(282475249, random.Next());
        Assert.Equal(1622650073, random.Next());
    }

    [Fact]
    public void SeedEngineReplaysJitterAndFloatScaling()
    {
        var result = ItemStatEngine.Compute(
            [
                new ItemStatEngine.InputStat("Class", "ArmorJewelry_Ring", 0),
                new ItemStatEngine.InputStat("attributeScalePercent", "", 30),
                new ItemStatEngine.InputStat("offensiveFireModifier", "", 100)
            ],
            seed: 1);

        Assert.Empty(result.UnmodeledFields);
        Assert.Equal(115, result.Stats["offensiveFireModifier"]);
    }

    [Fact]
    public void ResolverTranslatesSeedAppliedValuesIntoCanonicalStats()
    {
        var record = ItemRecord(new Dictionary<string, double>
        {
            ["attributeScalePercent"] = 30,
            ["offensiveFireModifier"] = 100
        });

        var result = SeededItemStatResolver.Resolve(record, null, null, seed: 1);

        Assert.True(result.IsExact);
        Assert.Empty(result.UnmodeledFields);
        Assert.Equal(115, result.Stats[StatKeys.DamagePercent("fire")]);
    }

    [Fact]
    public void ResolverFallsBackWhenASeedControlledFieldIsNotModeled()
    {
        var record = ItemRecord(new Dictionary<string, double>
        {
            ["offensiveUnknownFutureStat"] = 10
        });

        var result = SeededItemStatResolver.Resolve(record, null, null, seed: 1);

        Assert.False(result.IsExact);
        Assert.Contains("offensiveUnknownFutureStat", result.UnmodeledFields);
    }

    [Fact]
    public void FixedLegacyPhysicalWeaponDamageIsPreservedWithoutAverageScaling()
    {
        var record = ItemRecord(
            new Dictionary<string, double>
            {
                ["attributeScalePercent"] = 30,
                ["offensivePhysicalMin"] = 90,
                ["offensivePhysicalMax"] = 110
            },
            itemClass: "WeaponMelee_Sword");

        var result = SeededItemStatResolver.Resolve(record, null, null, seed: 1);

        Assert.True(result.IsExact);
        Assert.Equal(100, result.Stats[StatKeys.WeaponDamage("physical")]);
        Assert.Equal(0, result.Stats[StatKeys.DamageFlat("physical")]);
    }

    private static GameRecord ItemRecord(
        IReadOnlyDictionary<string, double> values,
        string itemClass = "ArmorJewelry_Ring") => new()
    {
        RecordName = "records/items/test.dbr",
        RecordType = itemClass,
        NumericValues = new StatBlock(values),
        TextValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Class"] = [itemClass]
        }
    };
}
