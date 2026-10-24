using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class GrantedSkillRecalculationTests
{
    private const string Sharpshooter = "records/skills/itemskills/granted/item_sharpshooter_02.dbr";
    private const string Havoc = "records/skills/itemskills/legendary/item_havoc.dbr";

    [Fact]
    public void ActualSharpshootersCoatPassiveTracksCandidateWeaponAndRemoval()
    {
        // Correct local DB, inventory seed 550418065: this passive is restricted
        // to Ranged2h and gives 8 pierce / +45% pierce at granted level 1.
        var passive = Record(Sharpshooter, "Skill_Passive",
            ("Ranged2h", [1]), ("offensivePierceMin", [8, 14]), ("offensivePierceModifier", [45, 75]));
        var coat = Item("records/items/geartorso/c020_torso.dbr", EquipmentSlot.Chest, seed: 550418065);
        var owner = Owner(coat.BaseRecord, Sharpshooter);
        var rifle = Item("records/items/gearweapons/guns2h/d301_gun2h.dbr", EquipmentSlot.MainHand, "ranged2h");
        var pistol = Item("records/items/gearweapons/guns1h/d002_gun1h.dbr", EquipmentSlot.MainHand, "ranged1h");
        var catalog = Catalog(owner, passive);
        var character = Character();
        var engine = new CharacterStatEngine();

        var withRifle = engine.Calculate(character, Slots(coat, rifle), catalog);
        var withPistol = engine.Calculate(character, Slots(coat, pistol), catalog);
        var removed = engine.Calculate(character, Slots(rifle), catalog);

        Assert.Equal(8, withRifle.Stats[StatKeys.DamageFlat("pierce")]);
        Assert.Equal(45, withRifle.Stats[StatKeys.DamagePercent("pierce")]);
        Assert.Equal(0, withPistol.Stats[StatKeys.DamageFlat("pierce")]);
        Assert.Equal(0, removed.Stats[StatKeys.DamagePercent("pierce")]);
        Assert.Equal(0, character.BaselineStats[StatKeys.DamageFlat("pierce")]);
    }

    [Fact]
    public void HavocTemporaryBuffNeedsExplicitStateAndStillEquippedSource()
    {
        var weapon = Item("records/items/gearweapons/guns1h/d002_gun1h.dbr", EquipmentSlot.MainHand, "ranged1h", 1778881840);
        var root = Record(Havoc, "Skill_BuffSelfDuration", ("skillActiveDuration", [1]),
            ("characterAttackSpeedModifier", [100]), ("characterOffensiveAbilityModifier", [15, 18]));
        var owner = Owner(weapon.BaseRecord, Havoc, controller: "records/controllers/itemskills/cast_@selfonattack_10%.dbr");
        var catalog = Catalog(owner, root);
        var character = Character();
        var engine = new CharacterStatEngine();
        var slots = Slots(weapon);

        Assert.Equal(0, engine.Calculate(character, slots, catalog).Stats["attackSpeed"]);
        character.ActiveItemSkillRecords = new HashSet<string> { Havoc };
        var active = engine.Calculate(character, slots, catalog);
        Assert.Equal(100, active.Stats["attackSpeed"]);
        Assert.Equal(15, active.Stats["offensiveAbility.percent"]);
        Assert.Equal(0, engine.Calculate(character, Slots(), catalog).Stats["attackSpeed"]);
        Assert.Equal(0, engine.Calculate(Character(), slots, catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void ApothecaryDurationAndStatsComeFromLinkedBuff()
    {
        const string skill = "records/skills/itemskills/granted/item_apothecarytouch_01.dbr";
        const string buff = "records/skills/itemskills/granted/item_apothecarytouch_01_buff.dbr";
        var hands = Item("records/items/gearhands/c013_hands.dbr", EquipmentSlot.Hands, seed: 44050384);
        var catalog = Catalog(Owner(hands.BaseRecord, skill),
            WithText(Record(skill, "Skill_BuffRadius"), ("buffSkillName", buff)),
            Record(buff, "SkillBuff_Passive", ("skillActiveDuration", [3]), ("characterLifeRegen", [25, 50, 150])));
        var character = Character();
        var slots = Slots(hands);
        var grant = Assert.Single(SkillRotationFactory.ResolveGrantedSkills(character, catalog, slots));

        Assert.Equal(3, grant.DurationSeconds);
        Assert.False(grant.IsPassive);
        Assert.Contains(buff, SkillRotationFactory.CollectGrantedRecordNames(catalog));
        Assert.Equal(0, new CharacterStatEngine().Calculate(character, slots, catalog).Stats["healthRegen"]);
        character.ActiveItemSkillRecords = new HashSet<string> { skill };
        Assert.Equal(25, new CharacterStatEngine().Calculate(character, slots, catalog).Stats["healthRegen"]);
    }

    [Fact]
    public void GrantedAttackUsesCandidateOwnerLevelAndCannotSurviveRemovingItsItem()
    {
        const string skill = "records/skills/itemskills/granted/item_groundripper.dbr";
        var weapon = Item("records/items/gearweapons/melee2h/c026_blunt2h.dbr", EquipmentSlot.MainHand, "mace2h", 945372809);
        var owner = WithText(Owner(weapon.BaseRecord, skill), ("itemSkillLevelEq", "2"));
        var catalog = Catalog(owner, Record(skill, "Skill_AttackWave", ("offensivePhysicalMin", [50, 100])));
        var selected = new CharacterSkill(skill, "Tremor", 1, 0, true, "", "");
        var character = Character();

        var equipped = SkillRotationFactory.Build(character, catalog, selected, Slots(weapon));
        var removed = SkillRotationFactory.Build(character, catalog, selected, Slots());

        Assert.Equal(2, SkillRotationFactory.ResolveGrantedSkillLevel(owner));
        Assert.Equal(100, equipped.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(skill, equipped.MainSkillRecord);
        Assert.True(removed.IsFallback);
        Assert.False(removed.IsAvailable);
        Assert.Equal(skill, removed.MainSkillRecord);
        Assert.Contains(removed.UnsupportedEffects, x => x.AffectedMetrics.Contains("panelDps"));
        Assert.Equal(0, removed.FlatDamage[StatKeys.DamageFlat("physical")]);
    }

    [Fact]
    public void AGrantAddedBySetBonusExistsOnlyAtItsPieceThreshold()
    {
        const string set = "records/items/itemsets/test.dbr";
        const string bonus = "records/items/itemsets/test_bonus.dbr";
        var one = Item("one", EquipmentSlot.Head, setId: set);
        var two = Item("two", EquipmentSlot.Chest, setId: set);
        var catalog = Catalog(WithText(Record(set, "ItemSet"), ("setBonus2", bonus)), Owner(bonus, Sharpshooter),
            Record(Sharpshooter, "Skill_Passive", ("offensivePierceMin", [8])));

        Assert.Empty(SkillRotationFactory.ResolveGrantedSkills(Character(), catalog, Slots(one)));
        Assert.Single(SkillRotationFactory.ResolveGrantedSkills(Character(), catalog, Slots(one, two)));
    }

    [Fact]
    public void AvailableGrantChoicesIncludeOwnedCandidatesButExcludeUnownedDatabaseItems()
    {
        var owned = Item("owned.dbr", EquipmentSlot.Amulet);
        var catalog = Catalog(Owner(owned.BaseRecord, Havoc), Owner("unowned.dbr", Sharpshooter),
            Record(Havoc, "Skill_BuffSelfDuration", ("skillActiveDuration", [1])),
            Record(Sharpshooter, "Skill_Passive", ("offensivePierceMin", [8])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", Items = [owned] };

        Assert.Empty(SkillRotationFactory.ResolveGrantedSkills(character, catalog));
        Assert.Equal(Havoc, Assert.Single(SkillRotationFactory.ResolveAvailableGrantedSkills(character, catalog)).Record);
    }

    [Fact]
    public void UninstalledOwnedComponentOffersItsGrantWithoutApplyingItToCurrentLoadout()
    {
        var catalog = Catalog(Owner("component.dbr", Havoc), Record(Havoc, "Skill_BuffSelfDuration", ("skillActiveDuration", [1])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            OwnedAttachments = [new("component.dbr", AttachmentKind.Component)] };
        Assert.Equal(Havoc, Assert.Single(SkillRotationFactory.ResolveAvailableGrantedSkills(character, catalog)).Record);
        Assert.Empty(SkillRotationFactory.ResolveGrantedSkills(character, catalog));
    }

    [Theory]
    [InlineData("records/skills/itemskills/granted/item_apothecarytouch_01.dbr", "tagItemSkillC126Name", "药剂师之触")]
    [InlineData("records/skills/itemskills/item_warcry.dbr", "tagItemSkillB008Name", "战争怒吼")]
    public void GrantedBuffNameUsesTheActualLocalizedChildName(string rootName, string tag, string displayName)
    {
        var item = Item("source.dbr", EquipmentSlot.Amulet);
        var records = new[] { Owner(item.BaseRecord, rootName),
            WithText(Record(rootName, "Skill_BuffRadius"), ("buffSkillName", rootName + "_buff")),
            WithText(Record(rootName + "_buff", "SkillBuff_Passive"), ("skillDisplayName", tag)) };
        var catalog = new GameDataCatalog { Records = records.ToDictionary(x => x.RecordName),
            Tags = new Dictionary<string, string> { [tag] = displayName } };
        Assert.Equal(displayName, Assert.Single(SkillRotationFactory.ResolveGrantedSkills(Character(), catalog, Slots(item))).Name);
    }

    [Fact]
    public void StaleSavedGrantedWeaponPoolSkillCannotSurviveRemovingItsSource()
    {
        const string skill = "skills/item_wpattack.dbr";
        var item = Item("item_wpattack.dbr", EquipmentSlot.Amulet);
        var catalog = Catalog(Owner(item.BaseRecord, skill), Record(skill, "Skill_WPAttack_BasicAttack", ("skillChanceWeight", [20])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            Skills = [new(skill, "item weapon pool", 1, 0, true, "", "")] };

        Assert.Equal(20, SkillRotationFactory.Build(character, catalog, null, Slots(item)).WeaponPoolChance);
        Assert.Equal(0, SkillRotationFactory.Build(character, catalog, null, Slots()).WeaponPoolChance);
    }

    [Theory]
    [InlineData("Skill_BuffSelfToggled")]
    [InlineData("Skill_BuffRadiusToggled")]
    [InlineData("Skill_BuffAttackRadiusToggled")]
    public void ActualToggleClassesAutoActivateWithoutReusingATemporaryBuffSelection(string type)
    {
        const string skill = "skills/automatic_toggle.dbr";
        var item = Item("items/toggle.dbr", EquipmentSlot.Amulet);
        var catalog = Catalog(Owner(item.BaseRecord, skill), Record(skill, type, ("characterAttackSpeedModifier", [30])));
        var character = Character();

        Assert.Empty(character.ActiveItemSkillRecords);
        Assert.True(Assert.Single(SkillRotationFactory.ResolveGrantedSkills(character, catalog, Slots(item))).IsToggle);
        Assert.Equal(30, new CharacterStatEngine().Calculate(character, Slots(item), catalog).Stats["attackSpeed"]);
        Assert.Equal(0, new CharacterStatEngine().Calculate(character, Slots(), catalog).Stats["attackSpeed"]);
    }

    [Fact]
    public void AttackRadiusToggleCollectsPlayerBonusesButNotItsAuraAttackPayload()
    {
        const string skill = "skills/ringofflame_toggled.dbr";
        const string buff = "skills/player_buff.dbr";
        var item = Item("items/ringofflame.dbr", EquipmentSlot.Amulet);
        var catalog = Catalog(Owner(item.BaseRecord, skill),
            WithText(Record(skill, "Skill_BuffAttackRadiusToggled", ("offensiveFireMin", [15]),
                ("characterAttackSpeedModifier", [5]), ("retaliationFireMin", [3]), ("defensiveFire", [10])),
                ("buffSkillName", buff)),
            Record(buff, "SkillBuff_Passive", ("offensiveColdMin", [9])));
        var stats = new CharacterStatEngine().Calculate(Character(), Slots(item), catalog).Stats;

        Assert.Equal(0, stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(9, stats[StatKeys.DamageFlat("cold")]);
        Assert.Equal(5, stats["attackSpeed"]);
        Assert.Equal(10, stats["fireResistance"]);
        Assert.Equal(3, stats[StatKeys.RetaliationFlat("fire")]);
    }

    [Fact]
    public void NativeLearnedSkillIsNotRemovedBecauseAnUnownedItemAlsoGrantsIt()
    {
        const string skill = "records/skills/playerclass02/native1.dbr";
        const string mastery = "records/skills/playerclass02/_classtraining_class02.dbr";
        var selected = new CharacterSkill(skill, "native", 1, 0, true, "", "");
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            Skills = [selected, new CharacterSkill(mastery, "mastery", 1, 0, true, "", "")] };
        var catalog = Catalog(Owner("unowned.dbr", skill), Record(skill, "Skill_AttackWave", ("offensivePhysicalMin", [50])));

        Assert.True(SkillRotationFactory.IsIntrinsicLearnedSkill(character, skill));
        Assert.True(SkillRotationFactory.IsIntrinsicLearnedSkill(character.Skills.Select(x => x.Record), skill));
        Assert.Equal(50, SkillRotationFactory.Build(character, catalog, selected, Slots()).FlatDamage[StatKeys.DamageFlat("physical")]);
    }

    [Fact]
    public void RemovingTheOnlyDualWieldSourceInvalidatesTheCandidateDespiteImportFlag()
    {
        const string grant = "records/skills/itemskills/granted/item_gunslinger.dbr";
        var belt = Item("belt.dbr", EquipmentSlot.Waist);
        var main = Item("main.dbr", EquipmentSlot.MainHand, "ranged1h");
        var off = Item("off.dbr", EquipmentSlot.MainHand, "ranged1h");
        var catalog = Catalog(Owner(belt.BaseRecord, grant), Record(grant, "Skill_PassiveDualWieldWeapon", ("Ranged1h", [1]), ("dualRangedOnly", [1])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc", CanDualWield = true };
        var slots = Slots(belt, main);
        slots[EquipmentSlot.OffHand] = new(off, null, null);
        Assert.True(new CharacterStatEngine().Calculate(character, slots, catalog).IsValid);
        slots.Remove(EquipmentSlot.Waist);
        Assert.False(SkillRotationFactory.ResolveCanDualWield(character, catalog, slots));
        Assert.False(new CharacterStatEngine().Calculate(character, slots, catalog).IsValid);
    }

    [Theory]
    [InlineData("dualRangedOnly", "ranged1h", true)]
    [InlineData("dualRangedOnly", "sword1h", false)]
    [InlineData("dualWieldOnly", "sword1h", true)]
    [InlineData("dualWieldOnly", "ranged1h", false)]
    [InlineData("dualRangedOrRanged2hOnly", "ranged1h", true)]
    [InlineData("dualRangedOrAllRangedOnly", "ranged1h", true)]
    public void ExplicitDualQualifiersAuthorizeOnlyTheirWeaponTypeEvenOnWeaponPoolSkills(string flag, string weaponType, bool allowed)
    {
        const string skill = "skills/ordinary_wpattack.dbr";
        var main = Item("main.dbr", EquipmentSlot.MainHand, weaponType);
        var off = Item("off.dbr", EquipmentSlot.MainHand, weaponType);
        var catalog = Catalog(Record(skill, "Skill_WPAttack_BasicAttack", (flag, [1])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            Skills = [new CharacterSkill(skill, "weapon pool", 1, 0, true, "", "")] };
        var slots = Slots(main);
        slots[EquipmentSlot.OffHand] = new(off, null, null);

        Assert.Equal(allowed, SkillRotationFactory.ResolveCanDualWield(character, catalog, slots));
        Assert.Equal(allowed, new CharacterStatEngine().Calculate(character, slots, catalog).IsValid);
    }

    [Fact]
    public void ConflictingExclusiveGrantIsNotAddedOnTopOfSavedExclusiveSkill()
    {
        const string learned = "skills/learned_exclusive.dbr";
        const string grant = "skills/item_exclusive.dbr";
        var item = Item("exclusive_item.dbr", EquipmentSlot.Amulet);
        var catalog = Catalog(Owner(item.BaseRecord, grant),
            Record(learned, "Skill_BuffSelfToggled", ("exclusiveSkill", [1])),
            Record(grant, "Skill_BuffSelfToggled", ("exclusiveSkill", [1]), ("offensiveFireModifier", [30])));
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            Skills = [new CharacterSkill(learned, "exclusive", 1, 0, true, "", "", Active: true)] };
        var result = new CharacterStatEngine().Calculate(character, Slots(item), catalog);
        Assert.Equal(0, result.Stats[StatKeys.DamagePercent("fire")]);
        Assert.Contains(result.UnsupportedEffects, x => x.Record == grant && x.AffectedMetrics.Contains("panelDps"));
    }

    [Fact]
    public void SetBonusSkillLevelAndModifierRecalculateWhenThresholdIsLost()
    {
        const string skill = "skills/attack.dbr";
        const string set = "set.dbr";
        const string bonus = "set_bonus.dbr";
        const string modifier = "set_modifier.dbr";
        var one = Item("one.dbr", EquipmentSlot.Head, setId: set);
        var two = Item("two.dbr", EquipmentSlot.Chest, setId: set);
        var catalog = Catalog(WithText(Record(set, "ItemSet"), ("setBonus2", bonus)),
            WithText(Record(bonus, "ItemSetBonus", ("augmentSkillLevel1", [1])),
                ("augmentSkillName1", skill), ("modifiedSkillName1", skill), ("modifierSkillName1", modifier)),
            Record(skill, "Skill_AttackWave", ("offensivePhysicalMin", [50, 100])),
            Record(modifier, "Skill_Modifier", ("offensiveFireMin", [20])));
        var character = Character();
        var selected = new CharacterSkill(skill, "attack", 1, 0, true, "", "");
        var complete = SkillRotationFactory.Build(character, catalog, selected, Slots(one, two));
        var removed = SkillRotationFactory.Build(character, catalog, selected, Slots(one));
        Assert.Equal(100, complete.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(20, complete.FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(50, removed.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(0, removed.FlatDamage[StatKeys.DamageFlat("fire")]);
    }

    private static CharacterSnapshot Character() => new() { Name = "test", SaveFilePath = "test.gdc", Level = 64 };
    private static ItemInstance Item(string record, EquipmentSlot slot, string qualifier = "", uint seed = 1, string setId = "") => new()
    {
        Id = record, BaseRecord = record, Name = record, Slot = slot, Seed = seed, AttachmentQualifier = qualifier,
        SetId = setId, IsTwoHanded = qualifier.EndsWith("2h", StringComparison.Ordinal),
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "fixture.gdc")
    };
    private static Dictionary<EquipmentSlot, LoadoutVariant> Slots(params ItemInstance[] items) =>
        items.ToDictionary(x => x.Slot, x => new LoadoutVariant(x, null, null));
    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    {
        Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase)
    };
    private static GameRecord Owner(string record, string grant, string controller = "") =>
        WithText(Record(record, "Item"), ("itemSkillName", grant), ("itemSkillAutoController", controller));
    private static GameRecord WithText(GameRecord record, params (string Key, string Value)[] fields) => new()
    {
        RecordName = record.RecordName, RecordType = record.RecordType, NumericValues = record.NumericValues,
        NumericSeries = record.NumericSeries,
        TextValues = record.TextValues.Concat(fields.Select(x =>
                new KeyValuePair<string, IReadOnlyList<string>>(x.Key, new[] { x.Value })))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Value, StringComparer.OrdinalIgnoreCase)
    };
    private static GameRecord Record(string name, string type, params (string Key, double[] Values)[] fields) => new()
    {
        RecordName = name, RecordType = type,
        NumericValues = new StatBlock(fields.ToDictionary(x => x.Key, x => x.Values[0])),
        NumericSeries = fields.ToDictionary(x => x.Key, x => (IReadOnlyList<double>)x.Values)
    };
}
