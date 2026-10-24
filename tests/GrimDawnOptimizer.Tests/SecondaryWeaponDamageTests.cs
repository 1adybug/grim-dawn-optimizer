using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SecondaryWeaponDamageTests
{
    [Fact]
    public void DualWeaponSecondarySumsLocalPayloadsAndIncludesSharedFlatOnlyOnce()
    {
        var main = Item("main", EquipmentSlot.MainHand,
            Stats((StatKeys.WeaponDamage("physical"), 100), (StatKeys.DamageFlat("fire"), 20)));
        var off = Item("off", EquipmentSlot.MainHand,
            Stats((StatKeys.WeaponDamage("physical"), 60), (StatKeys.DamageFlat("physical"), 8), (StatKeys.DamageFlat("fire"), 4)));
        var ring = Item("ring", EquipmentSlot.Ring,
            Stats((StatKeys.DamageFlat("physical"), 4), (StatKeys.DamageFlat("fire"), 7)));
        var character = Character(Stats((StatKeys.DamageFlat("physical"), 6), (StatKeys.DamageFlat("fire"), 5)));
        var result = new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, Attachment("component", AttachmentKind.Component, 10), Attachment("augment", AttachmentKind.Augment, 6)),
            [EquipmentSlot.OffHand] = new(off, null, null),
            [EquipmentSlot.RingLeft] = new(ring, null, null)
        }, new GameDataCatalog());

        Assert.Equal(94, PrimaryPayload(result.Stats, "physical")); // (100 + 60 + 8)/2 + shared 10.
        Assert.Equal(32, PrimaryPayload(result.Stats, "fire")); // (20 + 10 + 6 + 4)/2 + shared 12.
        Assert.Equal(178, SecondaryPayload(result.Stats, "physical")); // Both weapons + shared 10 once.
        Assert.Equal(52, SecondaryPayload(result.Stats, "fire")); // Both attached payloads + shared 12 once.
        Assert.Equal(84, result.Stats["secondaryWeaponDamageCorrection.physical"]);
        Assert.Equal(20, result.Stats["secondaryWeaponDamageCorrection.fire"]);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void SimulatorUsesSummedSecondaryWeaponDamageWithoutRepeatingSharedFlatOrPrimaryModifiers()
    {
        var character = Character(Stats((StatKeys.DamageFlat("physical"), 10)));
        var main = Item("main", EquipmentSlot.MainHand, Stats((StatKeys.WeaponDamage("physical"), 100)));
        var off = Item("off", EquipmentSlot.MainHand, Stats((StatKeys.WeaponDamage("physical"), 60)));
        var loadout = new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null), [EquipmentSlot.OffHand] = new(off, null, null)
        }, new GameDataCatalog());
        var rotation = new SkillRotationDefinition
        {
            BaseAttacksPerSecond = 1,
            WeaponDamagePercent = 100,
            FlatDamage = Stats((StatKeys.DamagePercent("physical"), 50)),
            SecondaryAttacks = [new()
            {
                WeaponDamagePercent = 50,
                FlatDamage = Stats((StatKeys.DamageFlat("physical"), 20))
            }]
        };

        var report = new CombatSimulator().Simulate(character, loadout, new CombatScenario(), rotation);

        // Primary: ((100+60)/2 + shared 10) * 1.5 strikes * 1.5 local bonus = 202.5.
        // Secondary: (100+60 + shared 10) * 50% + native 20 = 105, once.
        Assert.Equal(307.5, report.UnmitigatedDps, 8);
    }

    [Fact]
    public void FocusIsNotASecondWeaponAndDoesNotAddASecondaryCorrection()
    {
        var main = Item("main", EquipmentSlot.MainHand,
            Stats((StatKeys.WeaponDamage("physical"), 100), (StatKeys.DamageFlat("fire"), 20)));
        var focus = Item("focus", EquipmentSlot.OffHand, Stats((StatKeys.DamageFlat("fire"), 7)));
        var result = new CharacterStatEngine().Calculate(Character(Stats((StatKeys.DamageFlat("fire"), 5))),
            new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.MainHand] = new(main, null, null), [EquipmentSlot.OffHand] = new(focus, null, null)
            }, new GameDataCatalog());

        Assert.Equal(100, SecondaryPayload(result.Stats, "physical"));
        Assert.Equal(32, SecondaryPayload(result.Stats, "fire"));
        Assert.Equal(0, result.Stats["secondaryWeaponDamageCorrection.physical"]);
        Assert.Equal(0, result.Stats["secondaryWeaponDamageCorrection.fire"]);
    }

    [Fact]
    public void RemovingSecondWeaponDropsTheCorrectionWithoutChangingSharedDamage()
    {
        var main = Item("main", EquipmentSlot.MainHand, Stats((StatKeys.WeaponDamage("physical"), 100)));
        var off = Item("off", EquipmentSlot.MainHand, Stats((StatKeys.WeaponDamage("physical"), 60)));
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null), [EquipmentSlot.OffHand] = new(off, null, null)
        };
        var character = Character(Stats((StatKeys.DamageFlat("physical"), 10)));
        var engine = new CharacterStatEngine();
        var dual = engine.Calculate(character, slots, new GameDataCatalog());
        slots.Remove(EquipmentSlot.OffHand);
        var single = engine.Calculate(character, slots, new GameDataCatalog());

        Assert.Equal(170, SecondaryPayload(dual.Stats, "physical"));
        Assert.Equal(110, SecondaryPayload(single.Stats, "physical"));
        Assert.Equal(0, single.Stats["secondaryWeaponDamageCorrection.physical"]);
        Assert.Equal(10, single.Stats[StatKeys.DamageFlat("physical")]);
    }

    private static double PrimaryPayload(StatBlock stats, string type) =>
        stats[StatKeys.WeaponDamage(type)] + stats[StatKeys.DamageFlat(type)];

    private static double SecondaryPayload(StatBlock stats, string type) =>
        PrimaryPayload(stats, type) + stats[$"secondaryWeaponDamageCorrection.{type}"];

    private static CharacterSnapshot Character(StatBlock baseline) => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100, CanDualWield = true, BaselineStats = baseline
    };

    private static ItemInstance Item(string id, EquipmentSlot slot, StatBlock stats) => new()
    {
        Id = id, Name = id, BaseRecord = id, Slot = slot, Stats = stats,
        Source = new ItemSourceLocation(ItemSourceKind.Equipped, "test.gdc")
    };

    private static AttachmentDefinition Attachment(string id, AttachmentKind kind, double amount) => new()
    {
        Id = id, Name = id, Kind = kind, Stats = Stats((StatKeys.DamageFlat("fire"), amount))
    };

    private static StatBlock Stats(params (string Key, double Value)[] values) =>
        new(values.ToDictionary(x => x.Key, x => x.Value));
}
