using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class WeaponQualifiedSkillTests
{
    private const string Expertise = "records/skills/playerclass07/passive01.dbr";

    [Fact]
    public void RangedExpertiseRecalculatesAcrossDualRangedTwoHandedMeleeAndReload()
    {
        var passive = new GameRecord
        {
            RecordName = Expertise, RecordType = "Skill_Passive",
            NumericValues = new StatBlock(new Dictionary<string, double> { ["dualRangedOrAllRangedOnly"] = 1 }),
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["characterAttackSpeedModifier"] = [5, 8], ["offensivePierceMin"] = [5, 10] }
        };
        var booster = new GameRecord { RecordName = "bonus.dbr", NumericValues = new StatBlock(new Dictionary<string, double> { ["augmentSkillLevel1"] = 1 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["augmentSkillName1"] = [Expertise] } };
        var catalog = new GameDataCatalog { Records = new[] { passive, booster }.ToDictionary(x => x.RecordName) };
        var character = Character(catalog);
        var engine = new CharacterStatEngine();
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = new(Weapon("main", "ranged1h"), null, null),
            [EquipmentSlot.OffHand] = new(Weapon("off", "ranged1h"), null, null)
        };

        Assert.Equal(0, character.BaselineStats["attackSpeed"]);
        Assert.Equal(5, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots.Remove(EquipmentSlot.OffHand);
        slots[EquipmentSlot.MainHand] = new(Weapon("rifle", "ranged2h"), null, null);
        Assert.Equal(5, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots[EquipmentSlot.Head] = new(new ItemInstance { Id = "bonus", BaseRecord = "bonus.dbr", Name = "bonus", Slot = EquipmentSlot.Head,
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc") }, null, null);
        Assert.Equal(8, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots[EquipmentSlot.MainHand] = new(Weapon("sword", "sword1h"), null, null);
        var melee = engine.Calculate(character, slots, catalog);
        Assert.Equal(0, melee.Stats["attackSpeed"]);
        Assert.Equal(0, melee.Stats[StatKeys.DamageFlat("pierce")]);
        slots.Remove(EquipmentSlot.Head);
        slots[EquipmentSlot.MainHand] = new(Weapon("pistol", "ranged1h"), null, null);
        Assert.Equal(5, engine.Calculate(Character(catalog), slots, catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void DualOnlyGrantedPassiveRequiresBothEligibleHandsAndStillOwnedSource()
    {
        const string granted = "skills/dual_only.dbr";
        var belt = new ItemInstance { Id = "belt", BaseRecord = "belt.dbr", Name = "belt", Slot = EquipmentSlot.Waist,
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc") };
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord>
        {
            [belt.BaseRecord] = new() { RecordName = belt.BaseRecord,
                TextValues = new Dictionary<string, IReadOnlyList<string>> { ["itemSkillName"] = [granted] } },
            [granted] = new() { RecordName = granted, RecordType = "Skill_PassiveDualWieldWeapon",
                NumericValues = new StatBlock(new Dictionary<string, double> { ["dualRangedOnly"] = 1, ["characterAttackSpeedModifier"] = 20 }) }
        } };
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc" };
        var engine = new CharacterStatEngine();
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.Waist] = new(belt, null, null),
            [EquipmentSlot.MainHand] = new(Weapon("main", "ranged1h"), null, null)
        };
        Assert.Equal(0, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots[EquipmentSlot.OffHand] = new(Weapon("off", "ranged1h"), null, null);
        Assert.Equal(20, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots.Remove(EquipmentSlot.Waist);
        var removed = engine.Calculate(character, slots, catalog);
        Assert.Equal(0, removed.Stats["attackSpeed"]);
        Assert.False(removed.IsValid);
    }

    private static CharacterSnapshot Character(GameDataCatalog catalog)
    {
        var raw = new RawCharacter("test.gdc", "test", "class", 64, false) { Health = 100, Energy = 100 };
        raw.Skills.Add(new RawSkill(Expertise, 1, true, false, 0, 0, 0, true, false, "", ""));
        return new() { Name = raw.Name, SaveFilePath = raw.FilePath, Level = raw.Level,
            BaselineStats = SaveReader.BuildBaseline(raw, catalog), Skills = [new(Expertise, "Ranged Expertise", 1, 0, true, "", "")] };
    }

    private static ItemInstance Weapon(string name, string qualifier) => new()
    {
        Id = name, Name = name, BaseRecord = name, Slot = EquipmentSlot.MainHand, AttachmentQualifier = qualifier,
        IsTwoHanded = qualifier.EndsWith("2h", StringComparison.Ordinal), Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };
}
