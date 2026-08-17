using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class CharacterStatEngineTests
{
    private readonly CharacterStatEngine _engine = new();

    [Fact]
    public void AllowsOneHandedWeaponInOffhandWhenCurrentBuildCanDualWield()
    {
        var weapon = Item("second-weapon", EquipmentSlot.MainHand);
        var character = new CharacterSnapshot
        {
            Name = "dual-wielder", SaveFilePath = "test.gdc", Level = 100,
            Physique = 1000, Cunning = 1000, Spirit = 1000, CanDualWield = true,
            BaselineStats = new StatBlock(new Dictionary<string, double> { ["health"] = 10_000 })
        };

        var result = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.OffHand] = new(weapon, null, null)
        }, new GameDataCatalog());

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.Slots.ContainsKey(EquipmentSlot.OffHand));
    }

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

    [Fact]
    public void DualWieldAlternatesWeaponBaseDamageInsteadOfAddingBothGunsPerShot()
    {
        var main = Item("main-gun", EquipmentSlot.MainHand, weaponDamage: 100);
        var off = Item("off-gun", EquipmentSlot.MainHand, weaponDamage: 200);
        var result = _engine.Calculate(new CharacterSnapshot
        {
            Name = "gunner", SaveFilePath = "test.gdc", Level = 100,
            Physique = 1000, Cunning = 1000, Spirit = 1000, CanDualWield = true
        }, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null),
            [EquipmentSlot.OffHand] = new(off, null, null)
        }, new GameDataCatalog());

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.Equal(150, result.Stats[StatKeys.WeaponDamage("physical")]);
    }

    [Fact]
    public void DualRangedWeaponsUseInternalAndAverageWeaponSpeedModifiers()
    {
        var main = Item("main-gun", EquipmentSlot.MainHand, qualifier: "ranged1h", weaponBaseSpeed: -0.09);
        var off = Item("off-gun", EquipmentSlot.MainHand, qualifier: "ranged1h", weaponBaseSpeed: -0.06);
        var result = _engine.Calculate(new CharacterSnapshot
        {
            Name = "gunner", SaveFilePath = "test.gdc", Level = 100,
            Physique = 1000, Cunning = 1000, Spirit = 1000, CanDualWield = true
        }, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(main, null, null),
            [EquipmentSlot.OffHand] = new(off, null, null)
        }, new GameDataCatalog());

        Assert.Equal(-0.075, result.Stats["weaponBaseAttackSpeed"], 8);
        Assert.Equal(0.795, result.Stats["weaponAttackSpeedFactor"], 8);
        Assert.Equal(1.5, result.Stats["averageWeaponStrikesPerAction"], 8);
    }

    [Fact]
    public void PercentageAttributesAreAppliedAfterFlatEquipmentBonuses()
    {
        var item = Item("attribute-item", EquipmentSlot.Head);
        item.Stats.Set("physique", 20);
        item.Stats.Set("physique.percent", 10);
        var character = Character();
        character.BaselineStats.Set("physique", 100);

        var result = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Head] = new(item, null, null)
        }, new GameDataCatalog());

        Assert.Equal(132, result.Stats["physique"]);
    }

    [Fact]
    public void AppliesPlayerAbilityEquationAfterFlatAndPercentBonuses()
    {
        var item = Item("ability-item", EquipmentSlot.Head);
        item.Stats.Set("offensiveAbility", 100);
        item.Stats.Set("offensiveAbility.percent", 10);
        item.Stats.Set("defensiveAbility", 50);
        var character = Character();
        character.BaselineStats.Set("cunning", 400);
        character.BaselineStats.Set("physique", 300);
        character.BaselineStats.Set("offensiveAbility", 65);
        character.BaselineStats.Set("defensiveAbility", 65);
        character.BaselineStats.Set("characterLevel", 38);
        character.BaselineStats.Set("usePlayerAbilityFormula", 1);

        var result = _engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Head] = new(item, null, null)
        }, new GameDataCatalog());

        Assert.Equal((65 + 100 + 38 * 12 + 400 * 0.5) * 1.1 + 53, result.Stats["offensiveAbility"], 6);
        Assert.Equal(65 + 50 + 38 * 12 + 300 * 0.5 + 53, result.Stats["defensiveAbility"], 6);
    }

    private static CharacterSnapshot Character() => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000,
        BaselineStats = new StatBlock(new Dictionary<string, double> { ["health"] = 10_000 })
    };

    private static ItemInstance Item(
        string id,
        EquipmentSlot slot,
        string set = "",
        bool twoHanded = false,
        double weaponDamage = 0,
        string qualifier = "",
        double weaponBaseSpeed = 0) => new()
    {
        Id = id, Name = id, Slot = slot, BaseRecord = id, Seed = 1,
        SetId = set, IsTwoHanded = twoHanded, AttachmentQualifier = qualifier,
        Stats = new StatBlock(new Dictionary<string, double>
        {
            [StatKeys.WeaponDamage("physical")] = weaponDamage,
            ["weaponBaseAttackSpeed"] = weaponBaseSpeed
        }),
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc", 0, 0, 0)
    };
}
