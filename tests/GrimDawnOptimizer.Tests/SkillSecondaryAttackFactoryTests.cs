using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SkillSecondaryAttackFactoryTests
{
    [Fact]
    public void FireStrikeSecondaryIsSeparateWhileStaticStrikeAndBrimstoneModifyPrimary()
    {
        var primary = Record("flamestrike1", "Skill_WeaponPool_BasicAttack",
            ("weaponDamagePct", 160), ("offensiveFireMin", 86), ("offensivePhysicalModifier", 89));
        var secondary = Record("flamestrike2", "SkillSecondary_AttackRadius",
            ("weaponDamagePct", 35), ("offensivePhysicalMin", 84),
            ("offensiveSlowFireMin", 102), ("offensiveSlowFireDurationMin", 2));
        var staticStrike = Record("flamestrike3", "Skill_Modifier",
            ("offensiveFireModifier", 62), ("offensiveLightningMin", 144.54));
        var brimstone = Record("flamestrike4", "Skill_ProjectileModifier",
            ("offensiveFireMin", 60), ("offensiveChaosMin", 74.5));
        var character = Character(primary, secondary, staticStrike, brimstone);
        var catalog = Catalog(primary, secondary, staticStrike, brimstone);

        var rotation = SkillRotationFactory.Build(character, catalog, character.Skills[0]);

        Assert.Equal(160, rotation.WeaponDamagePercent);
        Assert.Equal(146, rotation.FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(89, rotation.FlatDamage[StatKeys.DamagePercent("physical")]);
        Assert.Equal(62, rotation.FlatDamage[StatKeys.DamagePercent("fire")]);
        Assert.Equal(144.54, rotation.FlatDamage[StatKeys.DamageFlat("lightning")]);
        Assert.Equal(74.5, rotation.FlatDamage[StatKeys.DamageFlat("chaos")]);
        Assert.Equal(0, rotation.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(0, rotation.FlatDamage[StatKeys.DamageDot("fire")]);
        var attack = Assert.Single(rotation.SecondaryAttacks);
        Assert.Equal(secondary.RecordName, attack.Record);
        Assert.Equal(35, attack.WeaponDamagePercent);
        Assert.Equal(84, attack.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(102, attack.FlatDamage[StatKeys.DamageDot("fire")]);
        Assert.Equal(204, attack.FlatDamage["damage.fire.dotTotal"]);
        Assert.Equal(0, attack.FlatDamage[StatKeys.DamagePercent("physical")]);
        Assert.Equal(secondary.RecordName, Assert.Single(attack.SourceRecords).Record);
    }

    [Fact]
    public void SecondaryLinksUseItsEquipmentAdjustedLevelAndKeepTheirOwnBonusesAndConversion()
    {
        var primary = Record("attack1", "Skill_WeaponPool_BasicAttack", ("weaponDamagePct", 160));
        var secondary = Record("attack2", "SkillSecondary_AttackProjectile");
        Series(secondary, "weaponDamagePct", 10, 20, 35);
        var projectile = Record("payload", "Projectile", ("conversionPercentage", 100));
        Series(projectile, "offensivePhysicalMin", 10, 20, 30);
        Series(projectile, "offensiveFireModifier", 1, 2, 3);
        Text(projectile, "conversionInType", "Physical");
        Text(projectile, "conversionOutType", "Fire");
        Text(secondary, "skillProjectileName", projectile.RecordName);
        Text(secondary, "buffSkillName", projectile.RecordName);
        var itemModifier = Record("itemmodifier", "Skill_Modifier", ("offensivePhysicalMin", 7));
        var itemOwner = Record("itemowner", "Armor", ("augmentSkillLevel1", 1));
        Text(itemOwner, "augmentSkillName1", secondary.RecordName);
        Text(itemOwner, "modifiedSkillName1", secondary.RecordName);
        Text(itemOwner, "modifierSkillName1", itemModifier.RecordName);
        var item = new ItemInstance
        {
            Id = "head", Name = "Secondary bonus", Slot = EquipmentSlot.Head, BaseRecord = itemOwner.RecordName,
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
        };
        var character = new CharacterSnapshot
        {
            Name = "test", SaveFilePath = "test.gdc", Level = 100,
            Skills = [Skill(primary, 1), Skill(secondary, 2)]
        };
        var catalog = Catalog(primary, secondary, projectile, itemOwner, itemModifier);

        var rotation = SkillRotationFactory.Build(character, catalog, character.Skills[0],
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) });

        Assert.Equal(160, rotation.WeaponDamagePercent);
        Assert.Equal(0, rotation.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(0, rotation.FlatDamage["conversion.physical.fire"]);
        Assert.Equal(0, rotation.FlatDamage[StatKeys.DamagePercent("fire")]);
        var attack = Assert.Single(rotation.SecondaryAttacks);
        Assert.Equal(3, attack.Level);
        Assert.Equal(35, attack.WeaponDamagePercent);
        Assert.Equal(37, attack.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Equal(3, attack.FlatDamage[StatKeys.DamagePercent("fire")]);
        Assert.Equal(100, attack.FlatDamage["conversion.physical.fire"]);
        Assert.Equal(3, attack.SourceRecords.Count);
        var linked = Assert.Single(attack.SourceRecords, x => x.Record == projectile.RecordName);
        Assert.Equal(3, linked.Level);
        Assert.Equal(30, linked.TranslatedStats[StatKeys.DamageFlat("physical")]);
        Assert.Equal(1, Assert.Single(attack.SourceRecords, x => x.Record == itemModifier.RecordName).Level);
    }

    private static GameRecord Record(string stem, string recordType, params (string Key, double Value)[] values) => new()
    {
        RecordName = $"records/skills/test/{stem}.dbr",
        RecordType = recordType,
        NumericValues = new StatBlock(values.ToDictionary(x => x.Key, x => x.Value)),
        NumericSeries = values.ToDictionary(x => x.Key, x => (IReadOnlyList<double>)new[] { x.Value }),
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = [recordType] }
    };

    private static CharacterSkill Skill(GameRecord record, int level = 1) =>
        new(record.RecordName, record.RecordName, level, 0, true, "", "");

    private static void Series(GameRecord record, string key, params double[] values) =>
        ((IDictionary<string, IReadOnlyList<double>>)record.NumericSeries)[key] = values;

    private static void Text(GameRecord record, string key, string value) =>
        ((IDictionary<string, IReadOnlyList<string>>)record.TextValues)[key] = [value];

    private static CharacterSnapshot Character(params GameRecord[] records) => new()
    {
        Name = "test", SaveFilePath = "test.gdc", Level = 100,
        Skills = records.Select(x => Skill(x)).ToArray()
    };

    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    {
        Records = records.ToDictionary(x => x.RecordName)
    };
}
