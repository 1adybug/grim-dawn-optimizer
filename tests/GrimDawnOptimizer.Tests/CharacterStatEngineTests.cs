using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class CharacterStatEngineTests
{
    private readonly CharacterStatEngine _engine = new();

    [Fact]
    public void RejectsSamePhysicalItemInTwoRingSlots()
    {
        var item = Item("ring-1", EquipmentSlot.Ring);
        var variant = new LoadoutVariant(item, null, null);
        var result = _engine.Calculate(Character(), new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.RingLeft] = variant,
            [EquipmentSlot.RingRight] = variant
        }, new GameDataCatalog());

        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors, x => x.Contains("重复装备", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsOffhandWithTwoHandedWeapon()
    {
        var main = Item("two-hand", EquipmentSlot.MainHand, twoHanded: true);
        var offhand = Item("shield", EquipmentSlot.OffHand);
        var result = _engine.Calculate(Character(), new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null),
            [EquipmentSlot.OffHand] = new(offhand, null, null)
        }, new GameDataCatalog());

        Assert.False(result.IsValid);
        Assert.Contains("双手武器不能与副手同时装备", result.ValidationErrors);
    }

    [Fact]
    public void AppliesEveryReachedSetThreshold()
    {
        var bonus2 = new StatBlock();
        bonus2.Add("health", 100);
        var bonus3 = new StatBlock();
        bonus3.Add("health", 250);
        var catalog = new GameDataCatalog
        {
            SetBonuses = new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>
            {
                ["set-a"] = new Dictionary<int, StatBlock> { [2] = bonus2, [3] = bonus3 }
            }
        };
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Head] = new(Item("a", EquipmentSlot.Head, "set-a"), null, null),
            [EquipmentSlot.Chest] = new(Item("b", EquipmentSlot.Chest, "set-a"), null, null),
            [EquipmentSlot.Hands] = new(Item("c", EquipmentSlot.Hands, "set-a"), null, null)
        };

        var result = _engine.Calculate(Character(), slots, catalog);

        Assert.Equal(10_350, result.Stats["health"]);
    }

    private static CharacterSnapshot Character() => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000,
        BaselineStats = new StatBlock(new Dictionary<string, double> { ["health"] = 10_000 })
    };

    private static ItemInstance Item(string id, EquipmentSlot slot, string set = "", bool twoHanded = false) => new()
    {
        Id = id, Name = id, Slot = slot, BaseRecord = id, Seed = 1,
        SetId = set, IsTwoHanded = twoHanded,
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 0, 0)
    };
}
