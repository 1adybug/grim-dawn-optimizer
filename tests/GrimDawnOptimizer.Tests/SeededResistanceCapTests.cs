using System.Text.Json;
using GrimDawnItemStats;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SeededResistanceCapTests
{
    [Theory]
    [InlineData("defensiveBleedingMaxResist", "bleeding", 6)]
    [InlineData("defensivePoisonMaxResist", "acid", 5)]
    [InlineData("defensiveLifeMaxResist", "vitality", 3)]
    public void FixedResistanceCapIsPreservedWithoutScalingOrChangingRandomDraws(
        string field, string damageType, double cap)
    {
        ItemStatEngine.InputStat[] ordinary =
        [
            new("Class", "ArmorJewelry_Ring", 0),
            new("attributeScalePercent", "", 300),
            new("defensivePoison", "", 22),
            new("defensiveReflect", "", 15),
            new("conversionPercentage", "", 30),
            new("skillCooldownReduction", "", 10)
        ];
        var before = ItemStatEngine.Compute(ordinary, 1234251313);
        var after = ItemStatEngine.Compute([.. ordinary, new(field, "", cap)], 1234251313);

        Assert.Empty(after.UnmodeledFields);
        Assert.Equal(cap, after.Stats[field]);
        foreach (var (key, value) in before.Stats)
            Assert.Equal(value, after.Stats[key]);
        var canonical = SeededItemStatResolver.Resolve(Record(new() { [field] = cap }), null, null, 1234251313);
        Assert.True(canonical.IsExact);
        Assert.Equal(cap, canonical.Stats[StatKeys.MaxResistance(damageType)]);
    }

    [Fact]
    public void UnknownResistanceCapStillFallsBack()
    {
        var result = SeededItemStatResolver.Resolve(
            Record(new() { ["defensiveFutureMaxResist"] = 6 }), null, null, 1);

        Assert.False(result.IsExact);
        Assert.Contains("defensiveFutureMaxResist", result.UnmodeledFields);
    }

    [Fact]
    public void AffixResistanceCapsJitterAndAddAtTheirDefenseStorePosition()
    {
        var result = ItemStatEngine.Compute(
            [new("defensiveLifeMaxResist", "", 3), new("defensiveReflect", "", 100),
             new("attributeScalePercent", "", 300), new("skillCooldownReduction", "", 10)],
            seed: 1,
            prefixStats: [new("defensiveLifeMaxResist", "", 20), new("lootRandomizerJitter", "", 50)],
            suffixStats: [new("defensiveLifeMaxResist", "", 10), new("lootRandomizerJitter", "", 50)]);

        Assert.Empty(result.UnmodeledFields);
        // Defense slots 71-90: base is fixed, prefix and suffix take the first
        // two Park-Miller draws, before reflection (91) and deferred skill draws.
        Assert.Equal(25, result.Stats["defensiveLifeMaxResist"]);
        Assert.Equal(82, result.Stats["defensiveReflect"]);
        Assert.Equal(8, result.Stats["skillCooldownReduction"]);
    }

    [Fact]
    public void NonJitteredAffixCapsAreAddedWithoutConsumingRandomDraws()
    {
        var result = ItemStatEngine.Compute(
            [new("defensiveLifeMaxResist", "", 3), new("defensiveReflect", "", 100)],
            seed: 1,
            prefixStats: [new("defensiveLifeMaxResist", "", 2)],
            suffixStats: [new("defensiveLifeMaxResist", "", 4)]);
        var withoutCaps = ItemStatEngine.Compute([new("defensiveReflect", "", 100)], seed: 1);

        Assert.Empty(result.UnmodeledFields);
        Assert.Equal(9, result.Stats["defensiveLifeMaxResist"]);
        Assert.Equal(withoutCaps.Stats["defensiveReflect"], result.Stats["defensiveReflect"]);
    }

    [Theory]
    [InlineData(1958839992u, 79, 18)]
    [InlineData(738868812u, 83, 23)]
    public void ResistanceCapDoesNotDiscardSeedSpecificBeltStats(uint seed, double defense, double bleedResist)
    {
        // Complete nonzero combat fields of c025_waist, effective expansion record.
        var record = Record(new()
        {
            ["attributeScalePercent"] = 30,
            ["characterDefensiveAbility"] = 70,
            ["characterDefensiveAbilityModifier"] = 4,
            ["defensiveAether"] = 20,
            ["defensiveBleeding"] = 20,
            ["defensiveBleedingMaxResist"] = 6,
            ["defensiveProtection"] = 64,
            ["defensiveStun"] = 50
        }, "ArmorProtective_Waist");

        var result = ResolveAndReload(record, seed);

        Assert.Equal(defense, result.Stats["defensiveAbility"]);
        Assert.Equal(bleedResist, result.Stats[StatKeys.Resistance("bleeding")]);
        Assert.Equal(6, result.Stats[StatKeys.MaxResistance("bleeding")]);
    }

    [Fact]
    public void PoisonCapBeltReplaysDamageAndHealthInsteadOfExpectedValues()
    {
        // Complete nonzero combat fields of d001_waist.
        var record = Record(new()
        {
            ["attributeScalePercent"] = 30,
            ["characterDefensiveAbilityModifier"] = 3,
            ["characterLifeModifier"] = 5,
            ["defensivePoison"] = 20,
            ["defensivePoisonDuration"] = 30,
            ["defensivePoisonMaxResist"] = 5,
            ["defensiveProtection"] = 52,
            ["offensiveSlowBleedingModifier"] = 45,
            ["retaliationPoisonMin"] = 280,
            ["retaliationTotalDamageModifier"] = 60
        }, "ArmorProtective_Waist");

        var result = ResolveAndReload(record, 1307173940);

        Assert.Equal(6, result.Stats["health.percent"]);
        Assert.Equal(50, result.Stats[StatKeys.DamageDotPercent("bleeding")]);
        Assert.Equal(5, result.Stats[StatKeys.MaxResistance("acid")]);
    }

    [Fact]
    public void VitalityCapRingReplaysDifferentPhysicalAndTraumaRolls()
    {
        // Complete nonzero combat fields of d201_ring.
        var record = Record(new()
        {
            ["attributeScalePercent"] = 30,
            ["characterHealIncreasePercent"] = 10,
            ["characterStrength"] = 28,
            ["defensiveAether"] = 16,
            ["defensiveLife"] = 12,
            ["defensiveLifeMaxResist"] = 3,
            ["defensivePoison"] = 22,
            ["defensiveProtectionModifier"] = 6,
            ["offensivePhysicalModifier"] = 44,
            ["offensiveSlowPhysicalModifier"] = 44
        });

        var result = ResolveAndReload(record, 1234251313);

        Assert.Equal(49, result.Stats[StatKeys.DamagePercent("physical")]);
        Assert.Equal(65, result.Stats[StatKeys.DamageDotPercent("physical")]);
        Assert.Equal(3, result.Stats[StatKeys.MaxResistance("vitality")]);
    }

    private static SeededItemStatResolution ResolveAndReload(GameRecord record, uint seed)
    {
        var cold = SeededItemStatResolver.Resolve(record, null, null, seed);
        var hot = SeededItemStatResolver.Resolve(record, null, null, seed);
        var reloadedRecord = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(record))!;
        var reloaded = SeededItemStatResolver.Resolve(reloadedRecord, null, null, seed);

        Assert.True(cold.IsExact);
        Assert.Empty(cold.UnmodeledFields);
        Assert.True(hot.IsExact);
        Assert.True(reloaded.IsExact);
        Assert.Equal(cold.Stats.Values.OrderBy(x => x.Key), hot.Stats.Values.OrderBy(x => x.Key));
        Assert.Equal(cold.Stats.Values.OrderBy(x => x.Key), reloaded.Stats.Values.OrderBy(x => x.Key));
        return cold;
    }

    private static GameRecord Record(Dictionary<string, double> numeric, string itemClass = "ArmorJewelry_Ring") => new()
    {
        RecordName = "records/items/seed-cap-fixture.dbr",
        RecordType = itemClass,
        NumericValues = new StatBlock(numeric),
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = [itemClass] }
    };
}
