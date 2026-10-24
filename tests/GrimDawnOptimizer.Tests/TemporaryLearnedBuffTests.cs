using System.Text.Json;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class TemporaryLearnedBuffTests
{
    private const string Skill = "records/skills/playerclass04/heal1.dbr";
    private const string Buff = "records/skills/playerclass04/heal1_buff.dbr";

    [Fact]
    public void SavedActiveTemporaryBuffIsOffUntilExplicitlySelected()
    {
        var catalog = Catalog();
        var character = Character(catalog);
        var engine = new CharacterStatEngine();
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>();

        Assert.Equal(0, character.BaselineStats["attackSpeed"]);
        Assert.Equal(0, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        var choice = Assert.Single(SkillRotationFactory.ResolveTemporaryLearnedSkills(character, catalog));
        Assert.Equal(3, choice.DurationSeconds);
        Assert.Equal(1, choice.Level);

        character.ActiveItemSkillRecords = new HashSet<string> { Skill };
        Assert.Equal(10, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        character.ActiveItemSkillRecords = new HashSet<string>();
        Assert.Equal(0, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void ActiveTemporaryBuffRecomputesEquipmentLevelsModifiersAndWeaponEligibility()
    {
        var catalog = Catalog(requireRanged: true);
        var character = Character(catalog);
        character.ActiveItemSkillRecords = new HashSet<string> { Skill };
        var engine = new CharacterStatEngine();
        var pistol = Item("pistol", EquipmentSlot.MainHand, "ranged1h");
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.MainHand] = new(pistol, null, null) };
        Assert.Equal(10, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots[EquipmentSlot.Head] = new(Item("bonus.dbr", EquipmentSlot.Head), null, null);
        Assert.Equal(25, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        slots[EquipmentSlot.MainHand] = new(Item("sword", EquipmentSlot.MainHand, "sword1h"), null, null);
        Assert.Equal(0, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void TemporaryBuffStateIsNotPersistedOrReusedForAnotherCharacter()
    {
        var catalog = Catalog();
        var character = Character(catalog);
        character.ActiveItemSkillRecords = new HashSet<string> { Skill };
        var serialized = JsonSerializer.Serialize(character);
        var restored = Character(catalog);

        Assert.DoesNotContain("ActiveItemSkillRecords", serialized);
        Assert.Empty(restored.ActiveItemSkillRecords);
        Assert.Empty(Character(catalog).ActiveItemSkillRecords);
        Assert.Equal(0, new CharacterStatEngine().Calculate(restored, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void AllocatedTemporaryBuffModifierIsExcludedFromBaselineAndAddedOnlyWhileActive()
    {
        const string child = "records/skills/playerclass04/heal2.dbr";
        var baseCatalog = Catalog();
        var records = new Dictionary<string, GameRecord>(baseCatalog.Records)
        {
            [child] = Record(child, "Skill_Modifier", new() { ["offensiveColdModifier"] = 30 })
        };
        var catalog = new GameDataCatalog { Records = records };
        var raw = Raw();
        raw.Skills.Add(new RawSkill(child, 1, true, false, 0, 0, 0, true, false, "", ""));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", BaselineStats = SaveReader.BuildBaseline(raw, catalog),
            Skills = raw.Skills.Select(x => new CharacterSkill(x.Record, "test", x.Level, 0, true, "", "", Active: true)).ToArray() };
        Assert.Equal(0, character.BaselineStats[StatKeys.DamagePercent("cold")]);
        character.ActiveItemSkillRecords = new HashSet<string> { Skill };
        Assert.Equal(30, new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog).Stats[StatKeys.DamagePercent("cold")]);
    }

    private static RawCharacter Raw()
    {
        var raw = new RawCharacter("test.gdc", "test", "class", 64, false) { Health = 100, Energy = 100 };
        raw.Skills.Add(new RawSkill(Skill, 1, true, false, 0, 0, 0, true, false, "", ""));
        return raw;
    }

    private static CharacterSnapshot Character(GameDataCatalog catalog) => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 64,
        BaselineStats = SaveReader.BuildBaseline(Raw(), catalog),
        Skills = [new CharacterSkill(Skill, "temporary heal", 1, 0, true, "", "", Active: true)]
    };

    private static GameDataCatalog Catalog(bool requireRanged = false)
    {
        var root = Record(Skill, "Skill_BuffSelfDuration", requireRanged ? new() { ["Ranged1h"] = 1 } : new(),
            new() { ["buffSkillName"] = [Buff] });
        var buff = new GameRecord { RecordName = Buff, RecordType = "SkillBuff_Passive",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>> { ["skillActiveDuration"] = [3, 4], ["characterAttackSpeedModifier"] = [10, 20] } };
        var item = Record("bonus.dbr", "Item", new() { ["augmentSkillLevel1"] = 1 },
            new() { ["augmentSkillName1"] = [Skill], ["modifiedSkillName1"] = [Skill], ["modifierSkillName1"] = ["item_modifier.dbr"] });
        var modifier = Record("item_modifier.dbr", "Skill_Modifier", new() { ["characterAttackSpeedModifier"] = 5 });
        return new() { Records = new[] { root, buff, item, modifier }.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase) };
    }

    private static GameRecord Record(string name, string type, Dictionary<string, double> stats,
        Dictionary<string, IReadOnlyList<string>>? text = null) => new()
    {
        RecordName = name, RecordType = type, NumericValues = new StatBlock(stats),
        TextValues = text ?? new Dictionary<string, IReadOnlyList<string>>()
    };

    private static ItemInstance Item(string record, EquipmentSlot slot, string qualifier = "") => new()
    {
        Id = record, BaseRecord = record, Name = record, Slot = slot, AttachmentQualifier = qualifier,
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };
}
