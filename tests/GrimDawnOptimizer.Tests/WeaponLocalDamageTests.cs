using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class WeaponLocalDamageTests
{
    [Theory]
    [InlineData(100, 0, 70)]
    [InlineData(10, 90, 70)]
    [InlineData(40, 80, 80)]
    public void DualWieldUsesEachWeaponsOwnFlatDamageAndSharesNonWeaponFlatDamage(
        double mainFlat, double offFlat, double expectedFlat)
    {
        var main = Weapon("main", Stats((StatKeys.DamageFlat("fire"), mainFlat), (StatKeys.WeaponDamage("fire"), 50)));
        var off = Weapon("off", Stats((StatKeys.DamageFlat("fire"), offFlat), (StatKeys.WeaponDamage("fire"), 150)));
        var character = Character(Stats((StatKeys.DamageFlat("fire"), 20)));

        var result = Calculate(character, main, off);

        Assert.Equal(expectedFlat, result.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(100, result.Stats[StatKeys.WeaponDamage("fire")]);
        // Keep both original per-hand contributions available for later weapon-specific rules.
        Assert.Equal(mainFlat, result.Slots[EquipmentSlot.MainHand].CombinedStats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(offFlat, result.Slots[EquipmentSlot.OffHand].CombinedStats[StatKeys.DamageFlat("fire")]);
    }

    [Fact]
    public void FlatDamageOnWeaponComponentsAndAugmentsBelongsToTheirHostWeapon()
    {
        var main = Weapon("main", Stats((StatKeys.DamageFlat("fire"), 10)));
        var off = Weapon("off", Stats((StatKeys.DamageFlat("fire"), 4)));
        var component = Attachment("component", AttachmentKind.Component, 6);
        var augment = Attachment("augment", AttachmentKind.Augment, 8);
        var result = new CharacterStatEngine().Calculate(Character(), new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, component, augment),
            [EquipmentSlot.OffHand] = new(off, null, null)
        }, new GameDataCatalog());

        Assert.Equal(14, result.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(24, result.Slots[EquipmentSlot.MainHand].CombinedStats[StatKeys.DamageFlat("fire")]);
    }

    [Fact]
    public void WeaponPercentDamageAndGlobalConversionAreNotAveragedWithFlatDamage()
    {
        var main = Weapon("main", Stats((StatKeys.DamageFlat("fire"), 20),
            (StatKeys.DamagePercent("fire"), 40), ("conversion.physical.fire", 15), ("attackSpeed", 9)));
        var off = Weapon("off", Stats((StatKeys.DamageFlat("fire"), 30),
            (StatKeys.DamagePercent("fire"), 60), ("conversion.physical.fire", 25), ("attackSpeed", 11)));

        var result = Calculate(Character(), main, off);

        Assert.Equal(25, result.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(100, result.Stats[StatKeys.DamagePercent("fire")]);
        Assert.Equal(40, result.Stats["conversion.physical.fire"]);
        Assert.Equal(20, result.Stats["attackSpeed"]);
    }

    [Fact]
    public void SingleWeaponWithFocusKeepsItsFlatDamageAndGlobalFocusDamage()
    {
        var main = Weapon("main", Stats((StatKeys.DamageFlat("fire"), 40)));
        var focus = Item("focus", EquipmentSlot.OffHand, Stats((StatKeys.DamageFlat("fire"), 30)));

        var result = Calculate(Character(Stats((StatKeys.DamageFlat("fire"), 20))), main, focus);

        Assert.Equal(90, result.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(1, result.Stats["averageWeaponStrikesPerAction"]);
    }

    private static CalculatedLoadout Calculate(CharacterSnapshot character, ItemInstance main, ItemInstance off) =>
        new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null),
            [EquipmentSlot.OffHand] = new(off, null, null)
        }, new GameDataCatalog());

    private static CharacterSnapshot Character(StatBlock? baseline = null) => new()
    {
        Name = "dual", SaveFilePath = "fixture.gdc", Level = 100, CanDualWield = true,
        BaselineStats = baseline ?? new StatBlock()
    };

    private static ItemInstance Weapon(string id, StatBlock stats) => Item(id, EquipmentSlot.MainHand, stats);

    private static ItemInstance Item(string id, EquipmentSlot slot, StatBlock stats) => new()
    {
        Id = id, Name = id, Slot = slot, BaseRecord = id, Stats = stats,
        Source = new ItemSourceLocation(ItemSourceKind.Equipped, "fixture.gdc")
    };

    private static AttachmentDefinition Attachment(string id, AttachmentKind kind, double flat) => new()
    {
        Id = id, Name = id, Kind = kind, Stats = Stats((StatKeys.DamageFlat("fire"), flat))
    };

    private static StatBlock Stats(params (string Key, double Value)[] values) =>
        new(values.ToDictionary(x => x.Key, x => x.Value));
}
