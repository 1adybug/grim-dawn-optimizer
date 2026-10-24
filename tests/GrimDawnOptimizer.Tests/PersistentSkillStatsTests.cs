using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PersistentSkillStatsTests
{
    [Theory]
    [InlineData("Skill_PassiveOnCritBuffSelf")]
    [InlineData("Skill_PassiveOnLifeBuffSelf")]
    [InlineData("Skill_PassiveOnHitBuffSelf")]
    public void ConditionalPassivesAreNotActiveJustBecauseTheyAreLearnedAndEnabled(string skillClass)
    {
        var skill = Record("skills/conditional.dbr", skillClass,
            ("characterOffensiveAbilityModifier", new double[] { 6, 8 }),
            ("offensiveCritDamageModifier", new double[] { 5, 6 }),
            ("offensiveTotalDamageModifier", new double[] { 25, 35 }));
        var modifier = Record("skills/conditional_modifier.dbr", "Skill_Modifier",
            ("characterAttackSpeedModifier", new double[] { 25 }));
        var item = Item();
        var equipment = EquipmentRecord(item.BaseRecord, skill.RecordName, modifier.RecordName);
        var catalog = Catalog(skill, modifier, equipment);
        var character = Character(skill, catalog);
        var calculated = new CharacterStatEngine().Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) }, catalog);

        Assert.Equal(0, character.BaselineStats["offensiveAbility.percent"]);
        Assert.Equal(0, calculated.Stats["offensiveAbility.percent"]);
        Assert.Equal(0, calculated.Stats["criticalDamage"]);
        Assert.Equal(0, calculated.Stats["damage.total.percent"]);
        Assert.Equal(0, calculated.Stats["attackSpeed"]);
    }

    [Fact]
    public void ReactiveAuraKeepsCharacterBonusesButNotItsAttackPayloadAtBaseAndEquipmentLevels()
    {
        var aura = Record("skills/reactive_aura.dbr", "Skill_OnHitAttackRadius",
            ("characterLifeRegen", new double[] { 100, 182 }),
            ("characterTotalSpeedModifier", new double[] { 12, 16 }),
            ("offensiveFireMin", new double[] { 80, 127 }),
            ("retaliationFireMin", new double[] { 100, 202 }));
        var modifier = Record("skills/aura_modifier.dbr", "Skill_Modifier",
            ("characterLifeRegen", new double[] { 10 }),
            ("offensiveFireMin", new double[] { 999 }));
        var item = Item();
        var equipment = EquipmentRecord(item.BaseRecord, aura.RecordName, modifier.RecordName);
        var catalog = Catalog(aura, modifier, equipment);
        var character = Character(aura, catalog);
        var engine = new CharacterStatEngine();
        var withoutEquipment = engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);
        var equipped = engine.Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) }, catalog);

        Assert.Equal(100, withoutEquipment.Stats["healthRegen"]);
        Assert.Equal(12, withoutEquipment.Stats["attackSpeed"]);
        Assert.Equal(12, withoutEquipment.Stats["castSpeed"]);
        Assert.Equal(192, equipped.Stats["healthRegen"]);
        Assert.Equal(16, equipped.Stats["attackSpeed"]);
        Assert.Equal(16, equipped.Stats["castSpeed"]);
        Assert.Equal(0, equipped.Stats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(0, equipped.Stats[StatKeys.RetaliationFlat("fire")]);
    }

    [Fact]
    public void OrdinaryPassivesKeepTheirPersistentOffensiveDamage()
    {
        var passive = Record("skills/passive.dbr", "Skill_Passive",
            ("offensiveFireMin", new double[] { 20 }),
            ("offensiveFireModifier", new double[] { 50 }));
        var character = Character(passive, Catalog(passive));

        Assert.Equal(20, character.BaselineStats[StatKeys.DamageFlat("fire")]);
        Assert.Equal(50, character.BaselineStats[StatKeys.DamagePercent("fire")]);
    }

    [Theory]
    [InlineData("Skill_BuffRadiusToggled", true)]
    [InlineData("Skill_WeaponPool_BasicAttack", false)]
    [InlineData("Skill_PassiveOnCritBuffSelf", false)]
    public void AllocatedModifierInheritsPersistenceOnlyFromItsLearnedPersistentRoot(string rootClass, bool persistent)
    {
        var root = Record("skills/aura1.dbr", rootClass);
        var modifier = Record("skills/aura2.dbr", "Skill_Modifier",
            ("characterDefensiveAbility", new double[] { 20, 40 }),
            ("offensivePhysicalMin", new double[] { 6, 10 }),
            ("offensivePierceModifier", new double[] { 12, 18 }));
        var item = Item();
        var equipment = EquipmentRecord(item.BaseRecord, modifier.RecordName, "");
        var catalog = Catalog(root, modifier, equipment);
        var character = Character(catalog, root, modifier);
        var engine = new CharacterStatEngine();
        var baseLoadout = engine.Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);
        var equipped = engine.Calculate(character,
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) }, catalog);

        Assert.Equal(persistent ? 6 : 0, character.BaselineStats[StatKeys.DamageFlat("physical")]);
        Assert.Equal(persistent ? 10 : 0, equipped.Stats[StatKeys.DamageFlat("physical")]);
        Assert.Equal(persistent ? 18 : 0, equipped.Stats[StatKeys.DamagePercent("pierce")]);
        Assert.Equal(persistent ? 20 : 0, equipped.Stats["defensiveAbility"] - baseLoadout.Stats["defensiveAbility"], 8);
    }

    [Fact]
    public void ModifierWithoutLearnedRootDoesNotBecomePersistent()
    {
        var root = Record("skills/aura1.dbr", "Skill_BuffRadiusToggled");
        var modifier = Record("skills/aura2.dbr", "Skill_Modifier", ("offensiveFireMin", new double[] { 999 }));
        var character = Character(Catalog(root, modifier), modifier);

        Assert.Equal(0, character.BaselineStats[StatKeys.DamageFlat("fire")]);
    }

    [Fact]
    public void LearnedReactiveAuraModifierKeepsOnlyItsCharacterBonuses()
    {
        var root = Record("skills/aura1.dbr", "Skill_OnHitAttackRadius");
        var modifier = Record("skills/aura2.dbr", "Skill_Modifier",
            ("characterLifeRegen", new double[] { 25 }), ("offensiveFireMin", new double[] { 999 }));
        var character = Character(Catalog(root, modifier), root, modifier);

        Assert.Equal(25, character.BaselineStats["healthRegen"]);
        Assert.Equal(0, character.BaselineStats[StatKeys.DamageFlat("fire")]);
    }

    private static CharacterSnapshot Character(GameRecord skill, GameDataCatalog catalog) => Character(catalog, skill);

    private static CharacterSnapshot Character(GameDataCatalog catalog, params GameRecord[] skills)
    {
        var raw = new RawCharacter("test.gdc", "test", "class", 1, false) { Health = 100, Energy = 100 };
        foreach (var skill in skills)
            raw.Skills.Add(new RawSkill(skill.RecordName, 1, true, false, 0, 0, 0, true, false, "", ""));
        return new CharacterSnapshot
        {
            Name = raw.Name, SaveFilePath = raw.FilePath, Level = raw.Level,
            BaselineStats = SaveReader.BuildBaseline(raw, catalog),
            Skills = skills.Select(skill => new CharacterSkill(skill.RecordName, "test skill", 1, 0, true, "", "")).ToArray()
        };
    }

    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    {
        Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase)
    };

    private static ItemInstance Item() => new()
    {
        Id = "helmet", Name = "helmet", Slot = EquipmentSlot.Head, BaseRecord = "items/helmet.dbr",
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };

    private static GameRecord EquipmentRecord(string name, string skill, string modifier) => new()
    {
        RecordName = name,
        NumericValues = new StatBlock(new Dictionary<string, double> { ["augmentSkillLevel1"] = 1 }),
        TextValues = new Dictionary<string, IReadOnlyList<string>>
        {
            ["augmentSkillName1"] = [skill], ["modifiedSkillName1"] = [skill], ["modifierSkillName1"] = [modifier]
        }
    };

    private static GameRecord Record(string name, string skillClass, params (string Key, double[] Values)[] fields) => new()
    {
        RecordName = name, RecordType = skillClass,
        NumericValues = new StatBlock(fields.ToDictionary(x => x.Key, x => x.Values[0])),
        NumericSeries = fields.ToDictionary(x => x.Key, x => (IReadOnlyList<double>)x.Values),
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = [skillClass] }
    };
}
