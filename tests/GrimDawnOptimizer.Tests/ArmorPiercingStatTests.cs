using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ArmorPiercingStatTests
{
    [Fact]
    public void ActualCrossbowSeedKeepsFixedArmorPiercingAndPhysicalBase()
    {
        // a11_gun2h003.dbr, owned seed 917980966; observed tooltip 126–263
        // physical, 100% armor piercing. The fixed ratio consumes no RNG draw.
        var weapon = new GameRecord
        {
            RecordName = "records/items/gearweapons/guns2h/a11_gun2h003.dbr", RecordType = "WeaponHunting_Ranged2h",
            NumericValues = new StatBlock(new Dictionary<string, double>
            { ["offensivePhysicalMin"] = 126, ["offensivePhysicalMax"] = 263, ["offensivePierceRatioMin"] = 100, ["attributeScalePercent"] = 52 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = ["WeaponHunting_Ranged2h"] }
        };

        var seeded = SeededItemStatResolver.Resolve(weapon, null, null, 917980966);
        Assert.True(seeded.IsExact);
        Assert.Equal(100, seeded.Stats[StatKeys.WeaponArmorPiercing]);
        Assert.Equal(194.5, seeded.Stats[StatKeys.WeaponDamage("physical")]);
        Assert.Equal(0, seeded.Stats[StatKeys.DamageFlat("pierce")]);
    }

    [Fact]
    public void ArmorPiercingRangeAndGlobalModifierHaveDifferentCanonicalStats()
    {
        var record = new GameRecord { RecordName = "test.dbr", NumericValues = new StatBlock(new Dictionary<string, double>
            { ["offensivePierceRatioMin"] = 25, ["offensivePierceRatioMax"] = 35, ["offensivePierceRatioModifier"] = 100 }) };
        var result = GameStatTranslator.Translate([record]);

        Assert.Equal(30, result[StatKeys.WeaponArmorPiercing]);
        Assert.Equal(100, result["armorPiercingModifier"]);
        Assert.Empty(GameStatTranslator.FindUnsupportedCombatFields(record));
    }
}
