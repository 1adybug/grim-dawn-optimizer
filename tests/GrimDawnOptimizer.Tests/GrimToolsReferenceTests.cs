using System.Text.Json;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class GrimToolsReferenceTests
{
    private static readonly JsonElement Reference = LoadReference();

    [Fact]
    public void FlatAndPercentageAttributesMatchReferenceResourceAndAbilityEquations()
    {
        var catalog = ReferenceCatalog();
        var raw = new RawCharacter("fixture.gdc", "reference", "class", 20, false)
        { Health = 250, Energy = 250, Physique = 50, Cunning = 50, Spirit = 50 };
        var character = Character(SaveReader.BuildBaseline(raw, catalog), level: 20);
        var gear = Item("attributes", EquipmentSlot.Head, GameStatTranslator.Translate([Record("attributes",
            Stats(("characterStrength", 20), ("characterDexterity", 30), ("characterIntelligence", 40),
                ("characterStrengthModifier", 10), ("characterDexterityModifier", 20), ("characterIntelligenceModifier", 30),
                ("characterLifeModifier", 20), ("characterManaModifier", 10),
                ("characterOffensiveAbilityModifier", 10), ("characterDefensiveAbilityModifier", 15)))]));

        var stats = new CharacterStatEngine().Calculate(character, Slots((EquipmentSlot.Head, gear)), catalog).Stats;

        Assert.Equal(77, stats["physique"], 8);
        Assert.Equal(96, stats["cunning"], 8);
        Assert.Equal(117, stats["spirit"], 8);
        Assert.Equal(556.8, stats["health"], 8);
        Assert.Equal(422.4, stats["energy"], 8);
        Assert.Equal(441.3, stats["offensiveAbility"], 8);
        Assert.Equal(448.025, stats["defensiveAbility"], 8);
        Assert.Equal(96d / 245 * 100, stats[StatKeys.DamagePercent("physical")], 8);
        Assert.Equal(96d / 215 * 100, stats[StatKeys.DamageDotPercent("bleeding")], 8);
        Assert.Equal(117d / 215 * 100, stats[StatKeys.DamagePercent("fire")], 8);
        Assert.Equal(58.5, stats[StatKeys.DamageDotPercent("fire")], 8);
    }

    [Fact]
    public void ArmorUsesSixRegionWeightsWithBeltAndLocalComponentArmor()
    {
        var slots = Slots(
            (EquipmentSlot.Head, Item("head", EquipmentSlot.Head, Stats(("armor", 100)))),
            (EquipmentSlot.Shoulders, Item("shoulders", EquipmentSlot.Shoulders, Stats(("armor", 200)))),
            (EquipmentSlot.Chest, Item("chest", EquipmentSlot.Chest, Stats(("armor", 300)))),
            (EquipmentSlot.Hands, Item("hands", EquipmentSlot.Hands, Stats(("armor", 400)))),
            (EquipmentSlot.Legs, Item("legs", EquipmentSlot.Legs, Stats(("armor", 500)))),
            (EquipmentSlot.Feet, Item("feet", EquipmentSlot.Feet, Stats(("armor", 600)))),
            (EquipmentSlot.Waist, Item("belt", EquipmentSlot.Waist, Stats(("armor", 50)))));
        var component = new AttachmentDefinition
        {
            Id = "plate", Name = "local armor", Kind = AttachmentKind.Component,
            Stats = GameStatTranslator.Translate([Record("plate", Stats(("defensiveBonusProtection", 40)))])
        };
        slots[EquipmentSlot.Head] = new(slots[EquipmentSlot.Head].Item, component, null);
        var character = Character(Stats(("armor", 20), ("armor.percent", 20), ("armorAbsorption", 70),
            ("armorAbsorption.percent", 20)));

        var stats = new CharacterStatEngine().Calculate(character, slots, ReferenceCatalog()).Stats;

        // Weighted body armor 349, plus belt/global armor 70, then +20%.
        Assert.Equal(502.8, stats["armor"], 8);
        Assert.Equal(252, stats["armor.region.Head"], 8);
        Assert.Equal(444, stats["armor.region.Chest"], 8);
        Assert.Equal(84, stats["armorAbsorption"], 8);
        Assert.Equal(0.26, stats["armor.chance.Chest"], 8);
    }

    [Fact]
    public void GlobalArmorOnAnAttachmentIsNotMistakenForLocalBonusProtection()
    {
        var head = Item("head", EquipmentSlot.Head, Stats(("armor", 100)));
        var slots = Slots((EquipmentSlot.Head, head));
        slots[EquipmentSlot.Head] = new(head, new AttachmentDefinition
        {
            Id = "global", Name = "global armor", Kind = AttachmentKind.Component,
            Stats = GameStatTranslator.Translate([Record("global", Stats(("defensiveProtection", 10)))])
        }, null);
        var stats = new CharacterStatEngine().Calculate(Character(), slots, ReferenceCatalog()).Stats;
        Assert.Equal(25, stats["armor"], 8); // 100*15% + 10 on every region.
    }

    [Fact]
    public void RegionWeightsComeFromLoadedDatabaseAndAbsorptionIsCapped()
    {
        var formulas = Record("records/game/combatformulas.dbr", Stats(
            ("combatRegionHeadChance", 0), ("combatRegionShouldersChance", 0), ("combatRegionTorsoChance", 100),
            ("combatRegionArmsChance", 0), ("combatRegionLegsChance", 0), ("combatRegionFeetChance", 0)));
        var stats = new CharacterStatEngine().Calculate(
            Character(Stats(("armorAbsorption", 70), ("armorAbsorption.percent", 100))),
            Slots((EquipmentSlot.Chest, Item("chest", EquipmentSlot.Chest, Stats(("armor", 200))))), Catalog(formulas)).Stats;
        Assert.Equal(200, stats["armor"]);
        Assert.Equal(100, stats["armorAbsorption"]);
    }

    [Theory]
    [InlineData(100, 1000, 70, 30)]
    [InlineData(1000, 100, 70, 930)]
    [InlineData(100, 1000, 100, 0)]
    public void ArmorCannotAbsorbMoreThanItsAbsorptionFractionOfTheHit(double hit, double armor, double absorption, double expected)
    {
        Assert.Equal(expected, ArmorRules.MitigatePhysicalHit(Stats(("armor", armor), ("armorAbsorption", absorption)), hit), 8);
    }

    [Fact]
    public void IncomingPhysicalDamageIsAveragedAfterEachRegionsArmor()
    {
        var slots = Slots((EquipmentSlot.Head, Item("head", EquipmentSlot.Head, Stats(("armor", 1000)))));
        var stats = new CharacterStatEngine().Calculate(Character(Stats(("armorAbsorption", 70))), slots, ReferenceCatalog()).Stats;
        Assert.Equal(150, stats["armor"], 8);
        Assert.Equal(447.5, ArmorRules.MitigatePhysicalHit(stats, 500), 8);
    }

    [Fact]
    public void BlockChanceModifierAddsPercentagePointsWhileBlockAmountMultiplies()
    {
        var shield = Item("shield", EquipmentSlot.OffHand, GameStatTranslator.Translate([Record("shield",
            Stats(("defensiveBlockChance", 35), ("defensiveBlock", 1000),
                ("defensiveBlockModifier", 20), ("defensiveBlockAmountModifier", 25)))]));
        var stats = new CharacterStatEngine().Calculate(Character(), Slots((EquipmentSlot.OffHand, shield)), ReferenceCatalog()).Stats;
        Assert.Equal(55, stats["blockChance"]);
        Assert.Equal(1250, stats["blockAmount"]);
    }

    [Fact]
    public void BlockBonusesRequireAnEquippedShield()
    {
        var character = Character(Stats(("blockChance.bonus", 20), ("blockAmount", 100)));
        var stats = new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), ReferenceCatalog()).Stats;
        Assert.Equal(0, stats["blockChance"]);
        Assert.Equal(0, stats["blockAmount"]);
    }

    [Fact]
    public void ElementalInputConversionConvertsEachOfTheThreeTypesAtFullPercentage()
    {
        var conversion = Conversion("Elemental", "Poison", 60);
        var stats = GameStatTranslator.Translate([conversion]);
        var damage = new Dictionary<string, double> { ["fire"] = 90, ["cold"] = 60, ["lightning"] = 30 };
        DamageConversion.Apply(damage, new StatBlock(), stats);
        Assert.Equal(108, damage["acid"], 8);
        Assert.Equal(36, damage["fire"], 8);
        Assert.Equal(24, damage["cold"], 8);
        Assert.Equal(12, damage["lightning"], 8);
        Assert.Empty(GameStatTranslator.FindUnsupportedCombatFields(conversion));
    }

    [Fact]
    public void ElementalOutputIsSplitBeforeOverallocatedConversionsAreNormalized()
    {
        var elemental = GameStatTranslator.Translate([Conversion("Physical", "Elemental", 60)]);
        var fire = GameStatTranslator.Translate([Conversion("Physical", "Fire", 60)]);
        elemental.Add(fire);
        var damage = new Dictionary<string, double> { ["physical"] = 90 };
        DamageConversion.Apply(damage, new StatBlock(), elemental);
        Assert.Equal(60, damage["fire"], 8);
        Assert.Equal(15, damage["cold"], 8);
        Assert.Equal(15, damage["lightning"], 8);
        Assert.Equal(0, damage["physical"], 8);
        Assert.Equal(90, damage.Values.Sum(), 8);
    }

    [Fact]
    public void UnknownConversionTypesAndAdditionalSlotsAreDiagnosed()
    {
        var unknown = Conversion("Physical", "FutureType", 50);
        Assert.Contains("conversionPercentage", GameStatTranslator.FindUnsupportedCombatFields(unknown));
        var extra = Record("extra", Stats(("conversionPercentage3", 50)),
            new Dictionary<string, IReadOnlyList<string>> { ["conversionInType3"] = ["Fire"], ["conversionOutType3"] = ["Cold"] });
        Assert.Contains("conversionPercentage3", GameStatTranslator.FindUnsupportedCombatFields(extra));
    }

    [Fact]
    public void UnverifiedLifeMultiplierIsNotSilentlyMergedWithOrdinaryHealthPercent()
    {
        var record = Record("new-content", Stats(("characterLifeModifier", 20), ("characterLifeMultModifier", -33)));
        Assert.Equal(20, GameStatTranslator.Translate([record])["health.percent"]);
        Assert.Contains("characterLifeMultModifier", GameStatTranslator.FindUnsupportedCombatFields(record));
    }

    [Fact]
    public void ElementalResistanceAndMaximumBonusesApplyToEachElementWithoutBeingDivided()
    {
        var record = Record("elemental defense", Stats(("defensiveElementalResistance", 30),
            ("defensiveElementalMaxResist", 3), ("defensiveAllMaxResist", 2)));
        var stats = GameStatTranslator.Translate([record]);
        foreach (var type in new[] { "fire", "cold", "lightning" })
        {
            Assert.Equal(30, stats[StatKeys.Resistance(type)]);
            Assert.Equal(5, stats[StatKeys.MaxResistance(type)]);
        }
        Assert.Equal(0, stats[StatKeys.Resistance("acid")]);
        Assert.Equal(2, stats[StatKeys.MaxResistance("acid")]);
        Assert.Empty(GameStatTranslator.FindUnsupportedCombatFields(record));
    }

    [Theory]
    [InlineData("offensiveFire", "damage.fire.flat")]
    [InlineData("offensiveElemental", "damage.fire.flat")]
    [InlineData("offensiveBaseElemental", "weaponDamage.fire")]
    public void ZeroMinimumRangesRetainTheirMean(string prefix, string canonical)
    {
        var stats = GameStatTranslator.Translate([Record("range", Stats(($"{prefix}Min", 0), ($"{prefix}Max", 90)))]);
        Assert.Equal(prefix.Contains("Elemental") ? 15 : 45, stats[canonical], 8);
    }

    [Fact]
    public void SkillLevelSelectionKeepsScalarStatsAndCapsScarsOfBattleAtItsUltimateLevel()
    {
        const string skillName = "records/skills/playerclass01/reference-passive.dbr";
        var reference = Reference.GetProperty("scarsOfBattle");
        var skill = new GameRecord
        {
            RecordName = skillName, RecordType = "Skill_Passive",
            NumericValues = Stats(("skillMaxLevel", reference.GetProperty("skillMaxLevel").GetInt32()),
                ("skillUltimateLevel", reference.GetProperty("skillUltimateLevel").GetInt32()), ("characterLife", 30)),
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["defensiveAbsorptionModifier"] = reference.GetProperty("defensiveAbsorptionModifier").EnumerateArray().Select(x => x.GetDouble()).ToArray() }
        };
        var owner = Record("bonus", Stats(("augmentAllLevel", 20)));
        var catalog = Catalog(skill, owner);
        var learned = new CharacterSkill(skillName, "reference", 8, 0, true, "", "");
        var raw = new RawCharacter("fixture.gdc", "reference", "class", 20, false)
        { Health = 250, Energy = 250, Physique = 50, Cunning = 50, Spirit = 50 };
        raw.Skills.Add(new RawSkill(skillName, 8, true, false, 0, 0, 0, true, false, "", ""));
        var character = Character(SaveReader.BuildBaseline(raw, catalog), [learned]);
        var slots = Slots((EquipmentSlot.Head, Item("bonus", EquipmentSlot.Head)));
        var result = new CharacterStatEngine().Calculate(character, slots, catalog);

        Assert.Equal(10, SkillRotationFactory.ResolveCurrentSkillBonus(character, catalog, skillName, slots));
        Assert.Equal(30, GameStatTranslator.TranslateAtLevel(skill, 18)["health"]);
        Assert.Equal(35, result.Stats["armorAbsorption.percent"]);
        Assert.Equal(94.5, result.Stats["armorAbsorption"], 8);
    }

    [Theory]
    [InlineData("Skill_Mastery", "records/skills/playerclass01/mastery.dbr")]
    [InlineData("Skill_Transmuter", "records/skills/playerclass01/transmuter.dbr")]
    [InlineData("Skill_ProjectileTransmuter", "records/skills/playerclass01/projectile.dbr")]
    [InlineData("Skill_SpawnPetTransmuter", "records/skills/playerclass01/pet.dbr")]
    [InlineData("Skill_Passive", "records/skills/devotion/star.dbr")]
    public void AllSkillEquipmentDoesNotRaiseMasteriesTransmutersOrDevotions(string skillClass, string skillName)
    {
        var skill = new GameRecord { RecordName = skillName, RecordType = skillClass };
        var owner = Record("bonus", Stats(("augmentAllLevel", 3)));
        var character = Character(skills: [new(skillName, "reference", 1, 0, true, "", "")]);
        Assert.Equal(0, SkillRotationFactory.ResolveCurrentSkillBonus(character, Catalog(skill, owner), skillName,
            Slots((EquipmentSlot.Head, Item("bonus", EquipmentSlot.Head)))));
    }

    [Fact]
    public void DuplicateSetRingsKeepOrdinaryStatsButDoNotUnlockSecondPieceBonuses()
    {
        const string set = "set";
        var bonus = Record("set-bonus", Stats(("augmentAllLevel", 2)));
        var setRecord = Record(set, new(), new Dictionary<string, IReadOnlyList<string>> { ["setBonus2"] = [bonus.RecordName] });
        var catalog = new GameDataCatalog
        {
            Records = new[] { setRecord, bonus }.ToDictionary(x => x.RecordName),
            SetBonuses = new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>
            { [set] = new Dictionary<int, StatBlock> { [2] = Stats(("health", 1000)) } }
        };
        var slots = Slots((EquipmentSlot.RingLeft, Item("ring", EquipmentSlot.Ring, Stats(("health", 100)), "left", set)),
            (EquipmentSlot.RingRight, Item("ring", EquipmentSlot.Ring, Stats(("health", 100)), "right", set)));
        var character = Character(Stats(("health", 1000)));
        Assert.Equal(1200, new CharacterStatEngine().Calculate(character, slots, catalog).Stats["health"]);
        Assert.Equal(0, SkillRotationFactory.ResolveCurrentSkillBonus(character, catalog, "skill", slots));
    }

    [Fact]
    public void SeparateCopiesOfSkillModifierItemsBothContributeToAttacksAndPassives()
    {
        const string attackName = "records/skills/playerclass01/attack.dbr";
        const string passiveName = "records/skills/playerclass01/passive.dbr";
        var attack = new GameRecord { RecordName = attackName, RecordType = "Skill_AttackSpell", NumericValues = Stats(("offensiveFireMin", 10)) };
        var passive = new GameRecord { RecordName = passiveName, RecordType = "Skill_Passive" };
        var owner = Record("ring", new(), new Dictionary<string, IReadOnlyList<string>>
        {
            ["modifiedSkillName1"] = [attackName], ["modifierSkillName1"] = ["attack-modifier"],
            ["modifiedSkillName2"] = [passiveName], ["modifierSkillName2"] = ["passive-modifier"]
        });
        var catalog = Catalog(attack, passive, owner, Record("attack-modifier", Stats(("offensiveFireMin", 30))),
            Record("passive-modifier", Stats(("characterStrength", 10))));
        var selected = new CharacterSkill(attackName, "reference attack", 1, 0, true, "", "");
        var character = Character(Stats(("physique", 50)), [selected, new(passiveName, "reference passive", 1, 0, true, "", "")]);
        var slots = Slots((EquipmentSlot.RingLeft, Item("ring", EquipmentSlot.Ring, id: "left")),
            (EquipmentSlot.RingRight, Item("ring", EquipmentSlot.Ring, id: "right")));
        Assert.Equal(70, SkillRotationFactory.Build(character, catalog, selected, slots).FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(70, new CharacterStatEngine().Calculate(character, slots, catalog).Stats["physique"]);
        slots.Remove(EquipmentSlot.RingRight);
        Assert.Equal(40, SkillRotationFactory.Build(character, catalog, selected, slots).FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(60, new CharacterStatEngine().Calculate(character, slots, catalog).Stats["physique"]);
    }

    [Fact]
    public void SkillModifiersAddWeaponDamageAndSignedCooldownAndCostAtTheirOwnLevels()
    {
        const string name = "records/skills/playerclass01/attack1.dbr";
        var attack = new GameRecord
        {
            RecordName = name, RecordType = "Skill_WeaponPool_BasicAttack",
            NumericValues = Stats(("weaponDamagePct", 150), ("skillCooldownTime", 5), ("skillManaCost", 30))
        };
        var child = new GameRecord
        {
            RecordName = "records/skills/playerclass01/attack2.dbr", RecordType = "Skill_Modifier",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["weaponDamagePct"] = [10, 20], ["skillCooldownTime"] = [-0.5, -1], ["skillManaCost"] = [2, 4] }
        };
        var equipmentModifier = new GameRecord
        {
            RecordName = "modifier", NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["weaponDamagePct"] = [25, 999], ["skillCooldownTime"] = [-1, -99], ["skillManaCost"] = [-5, -99] }
        };
        var owner = Record("ring", new(), new Dictionary<string, IReadOnlyList<string>>
        { ["modifiedSkillName1"] = [name], ["modifierSkillName1"] = [equipmentModifier.RecordName] });
        var selected = new CharacterSkill(name, "reference", 1, 0, true, "", "");
        var character = Character(skills: [selected, new(child.RecordName, "child", 2, 0, true, "", "")]);
        var slots = Slots((EquipmentSlot.RingLeft, Item("ring", EquipmentSlot.Ring, id: "left")),
            (EquipmentSlot.RingRight, Item("ring", EquipmentSlot.Ring, id: "right")));
        var rotation = SkillRotationFactory.Build(character, Catalog(attack, child, owner, equipmentModifier), selected, slots);
        Assert.Equal(220, rotation.WeaponDamagePercent); // 150 + child(20) + 2*25.
        Assert.Equal(2, rotation.CooldownSeconds); // 5 - child(1) - 2*1.
        Assert.Equal(24, rotation.EnergyPerUse); // 30 + child(4) - 2*5.
    }

    private static GameDataCatalog ReferenceCatalog()
    {
        var reference = Reference;
        var formulas = new GameRecord
        {
            RecordName = "records/game/combatformulas.dbr",
            NumericValues = new(reference.GetProperty("armorChances").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble())),
            TextValues = reference.GetProperty("formulas").EnumerateObject().ToDictionary(x => x.Name, x => (IReadOnlyList<string>)new[] { x.Value.GetString()! })
        };
        var increments = reference.GetProperty("attributeIncrements");
        var levels = Record("records/creatures/pc/playerlevels.dbr", Stats(
            ("strengthIncrement", increments.GetProperty("strengthIncrement").GetDouble()),
            ("dexterityIncrement", increments.GetProperty("cunningIncrement").GetDouble()),
            ("intelligenceIncrement", increments.GetProperty("spiritIncrement").GetDouble()),
            ("lifeIncrement", increments.GetProperty("healthIncrement").GetDouble()),
            ("lifeIncrementDexterity", increments.GetProperty("healthIncrementCunning").GetDouble()),
            ("lifeIncrementIntelligence", increments.GetProperty("healthIncrementSpirit").GetDouble()),
            ("manaIncrement", increments.GetProperty("energyIncrement").GetDouble())));
        return Catalog(formulas, levels, Record("records/game/gameengine.dbr", Stats(("armorDefensiveAbsorption", reference.GetProperty("armorAbsorption").GetDouble()))));
    }

    private static GameRecord Conversion(string input, string output, double percent) => Record("conversion", Stats(("conversionPercentage", percent)),
        new Dictionary<string, IReadOnlyList<string>> { ["conversionInType"] = [input], ["conversionOutType"] = [output] });
    private static CharacterSnapshot Character(StatBlock? stats = null, IReadOnlyList<CharacterSkill>? skills = null, int level = 100) => new()
    { Name = "reference", SaveFilePath = "fixture.gdc", Level = level, BaselineStats = stats ?? new(), Skills = skills ?? [] };
    private static ItemInstance Item(string record, EquipmentSlot slot, StatBlock? stats = null, string? id = null, string set = "") => new()
    { Id = id ?? record, Name = record, BaseRecord = record, Slot = slot, Stats = stats ?? new(), SetId = set, Source = new(ItemSourceKind.Equipped, "fixture.gdc") };
    private static Dictionary<EquipmentSlot, LoadoutVariant> Slots(params (EquipmentSlot Slot, ItemInstance Item)[] entries) =>
        entries.ToDictionary(x => x.Slot, x => new LoadoutVariant(x.Item, null, null));
    private static StatBlock Stats(params (string Key, double Value)[] values) => new(values.ToDictionary(x => x.Key, x => x.Value));
    private static GameRecord Record(string name, StatBlock stats, IReadOnlyDictionary<string, IReadOnlyList<string>>? text = null) => new()
    { RecordName = name, NumericValues = stats, TextValues = text ?? new Dictionary<string, IReadOnlyList<string>>() };
    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    { Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase) };
    private static JsonElement LoadReference()
    {
        using var stream = typeof(GrimToolsReferenceTests).Assembly.GetManifestResourceStream("GrimDawnOptimizer.Tests.Fixtures.grimtools-reference.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
