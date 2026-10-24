using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class EnergyReservationTests
{
    private const string Aura = "records/skills/playerclass02/blastshield1.dbr";
    private const string Buff = "records/skills/playerclass02/blastshield1_buff.dbr";
    private const string Modifier = "records/skills/playerclass02/blastshield2.dbr";
    private const string ReactiveAura = "records/skills/playerclass02/vindictiveflame1.dbr";

    [Fact]
    public void ObservedAuraLevelsReserve325WithoutScalingByMaximumEnergyBonus()
    {
        // Actual DBR values: learned levels 12/1/16, equipment +1 all skills.
        // 195 + 25 + 105 = 325, matching the game's 2071 max / 1746 usable.
        var catalog = Catalog();
        var character = Character(catalog);
        var bonus = new ItemInstance
        {
            Id = "bonus", Name = "bonus", Slot = EquipmentSlot.Head, BaseRecord = "items/bonus.dbr",
            Stats = new(new Dictionary<string, double> { ["energy.percent"] = 100 }),
            Source = new(ItemSourceKind.Backpack, "fixture.gdc")
        };
        var engine = new CharacterStatEngine();
        var without = engine.Calculate(character, Slots(), catalog);
        var with = engine.Calculate(character, Slots(bonus), catalog);

        Assert.Equal(307, without.Stats["energyReserved"]);
        Assert.Equal(325, with.Stats["energyReserved"]);
        Assert.Equal(without.Stats["energy"] * 2, with.Stats["energy"], 8);
        Assert.DoesNotContain(with.UnsupportedEffects, effect => effect.Description.Contains("characterManaLimitReserve", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledAuraAndItsModifierDoNotReserveEnergy()
    {
        var catalog = Catalog();
        var character = Character(catalog, auraEnabled: false);

        var loadout = new CharacterStatEngine().Calculate(character, Slots(), catalog);

        Assert.Equal(100, loadout.Stats["energyReserved"]); // Only Vindictive Flame.
    }

    [Fact]
    public void TemporaryGrantedBuffReservesEnergyOnlyWhileExplicitlyActiveAndEquipped()
    {
        const string temporary = "skills/temporary.dbr";
        var item = new ItemInstance { Id = "item", Name = "item", Slot = EquipmentSlot.Head,
            BaseRecord = "items/temporary.dbr", Source = new(ItemSourceKind.Backpack, "fixture.gdc") };
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord>
        {
            [item.BaseRecord] = new() { RecordName = item.BaseRecord,
                TextValues = new Dictionary<string, IReadOnlyList<string>> { ["itemSkillName"] = [temporary] } },
            [temporary] = Record(temporary, "Skill_BuffSelfDuration",
                ("skillActiveDuration", [3]), ("characterManaLimitReserve", [50]))
        } };
        var character = new CharacterSnapshot { Name = "fixture", SaveFilePath = "fixture.gdc" };
        var engine = new CharacterStatEngine();

        Assert.Equal(0, engine.Calculate(character, Slots(item), catalog).Stats["energyReserved"]);
        character.ActiveItemSkillRecords = new HashSet<string> { temporary };
        Assert.Equal(50, engine.Calculate(character, Slots(item), catalog).Stats["energyReserved"]);
        Assert.Equal(0, engine.Calculate(character, Slots(), catalog).Stats["energyReserved"]);
    }

    [Theory]
    [InlineData(40, 0, .6)]
    [InlineData(100, 0, 0)]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, 3, .3)]
    public void ReservationReducesOnlyTheCombatEnergyBudget(double reserved, double regeneration, double expectedRatio)
    {
        var baseline = Simulate(0, 0);
        var limited = Simulate(reserved, regeneration);

        Assert.Equal(baseline.UnmitigatedDps, limited.UnmitigatedDps, 8);
        Assert.Equal(baseline.ExpectedDps * expectedRatio, limited.ExpectedDps, 8);
    }

    [Fact]
    public void UnsupportedReservePercentageAndReductionStillProduceWarnings()
    {
        var record = Record("fixture", "SkillBuff_Passive", ("characterManaLimitReserve", [25]),
            ("characterManaLimitReserveModifier", [10]), ("characterManaLimitReserveReduction", [15]));
        var unsupported = GameStatTranslator.FindUnsupportedCombatFields(record);

        Assert.DoesNotContain("characterManaLimitReserve", unsupported);
        Assert.Contains("characterManaLimitReserveModifier", unsupported);
        Assert.Contains("characterManaLimitReserveReduction", unsupported);
    }

    private static SimulationReport Simulate(double reserved, double regeneration)
    {
        var stats = new StatBlock(new Dictionary<string, double>
        { ["energy"] = 100, ["energyReserved"] = reserved, ["energyRegen"] = regeneration });
        var rotation = new SkillRotationDefinition { WeaponDamagePercent = 0, BaseAttacksPerSecond = 2,
            EnergyPerUse = 10, IsFallback = false, FlatDamage = new(new Dictionary<string, double> { [StatKeys.DamageFlat("fire")] = 100 }) };
        return new CombatSimulator().Simulate(new() { Name = "fixture", SaveFilePath = "fixture.gdc" },
            new() { Stats = stats, Slots = Slots() }, new() { DurationSeconds = 10 }, rotation);
    }

    private static CharacterSnapshot Character(GameDataCatalog catalog, bool auraEnabled = true)
    {
        var raw = new RawCharacter("fixture.gdc", "fixture", "class", 64, false) { Health = 1000, Energy = 1000 };
        raw.Skills.Add(new(Aura, 12, auraEnabled, false, 0, 0, 0, auraEnabled, false, "", ""));
        raw.Skills.Add(new(Modifier, 1, true, false, 0, 0, 0, true, false, "", ""));
        raw.Skills.Add(new(ReactiveAura, 16, true, false, 0, 0, 0, true, false, "", ""));
        return new() { Name = "fixture", SaveFilePath = "fixture.gdc", Level = 64,
            BaselineStats = SaveReader.BuildBaseline(raw, catalog),
            Skills = raw.Skills.Select(skill => new CharacterSkill(skill.Record, skill.Record, skill.Level, 0,
                skill.Enabled, "", "", Active: skill.Active)).ToArray() };
    }

    private static GameDataCatalog Catalog()
    {
        var records = new[]
        {
            new GameRecord { RecordName = Aura, RecordType = "Skill_BuffRadiusToggled",
                TextValues = new Dictionary<string, IReadOnlyList<string>> { ["buffSkillName"] = [Buff] } },
            Record(Buff, "SkillBuff_Passive", ("characterManaLimitReserve", Enumerable.Range(0, 22).Select(i => 75d + i * 10).ToArray())),
            Record(Modifier, "Skill_Modifier", ("characterManaLimitReserve", [22, 25])),
            Record(ReactiveAura, "Skill_OnHitAttackRadius", ("characterManaLimitReserve", Enumerable.Range(0, 22).Select(i => 25d + i * 5).ToArray())),
            Record("items/bonus.dbr", "ArmorProtective_Head", ("augmentAllLevel", [1]))
        };
        return new() { Records = records.ToDictionary(record => record.RecordName, StringComparer.OrdinalIgnoreCase) };
    }
    private static Dictionary<EquipmentSlot, LoadoutVariant> Slots(params ItemInstance[] items) =>
        items.ToDictionary(item => item.Slot, item => new LoadoutVariant(item, null, null));
    private static GameRecord Record(string name, string type, params (string Key, double[] Values)[] fields) => new()
    {
        RecordName = name, RecordType = type,
        NumericValues = new(fields.ToDictionary(field => field.Key, field => field.Values[0])),
        NumericSeries = fields.ToDictionary(field => field.Key, field => (IReadOnlyList<double>)field.Values)
    };
}
