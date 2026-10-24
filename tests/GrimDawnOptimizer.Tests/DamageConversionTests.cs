using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class DamageConversionTests
{
    [Fact]
    public void PartialConversionCarriesAndScalesSourceLocalModifiers()
    {
        var damage = Damage(("physical", 100));
        var skill = Stats(("damage.physical.percent", 100));
        var global = Stats(("conversion.physical.fire", 25), ("damage.fire.percent", 200));

        var bonus = DamageConversion.Apply(damage, skill, global);

        // The converted packet carries 25% of the original local modifier;
        // the remaining packet carries 75%. Global fire bonuses apply afterwards.
        Assert.Equal(131.25, damage["physical"] + bonus["physical"]);
        Assert.Equal(81.25, damage["fire"] * 3 + bonus["fire"]);
    }

    [Fact]
    public void ConversionKeepsLocalSourceBonusSeparateFromDestinationBonus()
    {
        var damage = Damage(("physical", 100), ("fire", 50));
        var skill = Stats(("conversion.physical.fire", 100),
            ("damage.physical.percent", 40), ("damage.fire.percent", 80));

        var bonus = DamageConversion.Apply(damage, skill, new StatBlock());

        Assert.Equal(150, damage["fire"]);
        Assert.Equal(80, bonus["fire"]); // 100 * 40% + 50 * 80%.
        Assert.Equal(0, bonus["physical"]);
    }

    [Fact]
    public void GlobalConversionUsesOnlyTheRemainderAfterSkillConversion()
    {
        var damage = Damage(("physical", 100));
        var skill = Stats(("conversion.physical.fire", 60));
        var global = Stats(("conversion.physical.cold", 50));

        DamageConversion.Apply(damage, skill, global);

        Assert.Equal(60, damage["fire"]);
        Assert.Equal(20, damage["cold"]);
        Assert.Equal(20, damage["physical"]);
        Assert.Equal(100, damage.Values.Sum());
        Assert.Equal(60, skill["conversion.physical.fire"]);
        Assert.Equal(50, global["conversion.physical.cold"]);
    }

    [Fact]
    public void ConvertedPacketsCannotBeConvertedAgainWithinOrAcrossLayers()
    {
        var damage = Damage(("physical", 100), ("fire", 10));
        var skill = Stats(("conversion.physical.fire", 100));
        var global = Stats(("conversion.fire.cold", 100), ("conversion.cold.chaos", 100));

        DamageConversion.Apply(damage, skill, global);

        Assert.Equal(100, damage["fire"]); // Skill-converted physical stays fire.
        Assert.Equal(10, damage["cold"]); // Only original fire reaches cold.
        Assert.Equal(0, damage["chaos"]);
        Assert.Equal(110, damage.Values.Sum());
    }

    [Fact]
    public void EachLayerProportionallyNormalizesOverallocatedConversions()
    {
        var damage = Damage(("physical", 100), ("cold", 80));
        var skill = Stats(("conversion.physical.fire", 150), ("conversion.physical.lightning", 50));
        var global = Stats(("conversion.physical.acid", 100),
            ("conversion.cold.fire", 100), ("conversion.cold.lightning", 300));

        DamageConversion.Apply(damage, skill, global);

        Assert.Equal(95, damage["fire"]); // 100 * 150/200 + 80 * 100/400.
        Assert.Equal(85, damage["lightning"]);
        Assert.Equal(0, damage["acid"]);
        Assert.Equal(0, damage["physical"]);
        Assert.Equal(0, damage["cold"]);
        Assert.Equal(180, damage.Values.Sum());
    }

    private static Dictionary<string, double> Damage(params (string Type, double Amount)[] values) =>
        values.ToDictionary(x => x.Type, x => x.Amount, StringComparer.OrdinalIgnoreCase);

    private static StatBlock Stats(params (string Key, double Value)[] values) =>
        new(values.ToDictionary(x => x.Key, x => x.Value));
}
