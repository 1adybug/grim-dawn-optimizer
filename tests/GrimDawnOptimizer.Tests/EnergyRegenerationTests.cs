using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class EnergyRegenerationTests
{
    private const string ResourceRecord = "records/game/playerresourcebehavior.dbr";
    private const string ManaEquation = "(((manaRegen*((intelligence/384)+1))+((intelligence-50)/100))+6.5) * elapsedTime";
    private const string LifeEquation = "(lifeRegen + ((physique-50)/25))  * elapsedTime";

    [Theory]
    [InlineData(415.84, 237, 14, 15.57, 293.54)]
    [InlineData(382.72, 237, 0, 15.02, 260.36)]
    [InlineData(415.84, 192, 14, 15.57, 242.24)]
    public void ActualResourceEquationsMatchObservedOriginalAndRingRemovalStates(
        double spirit, double healthRegen, double healthRegenPercent, double observedEnergy, double observedHealth)
    {
        var character = Character(spirit, 2.5999999046325684, healthRegen);
        character.BaselineStats.Set("healthRegen.percent", healthRegenPercent);

        var result = Calculate(character, Catalog());

        Assert.Equal(observedEnergy, Math.Round(result.Stats["energyRegen"], 2));
        Assert.Equal(observedHealth, Math.Round(result.Stats["healthRegen"], 2));
        Assert.DoesNotContain(result.UnsupportedEffects, effect => effect.Record == ResourceRecord);
    }

    [Fact]
    public void EquipmentPercentIsAppliedBeforeTheSpiritEquationAndDoesNotScaleItsFlatTerm()
    {
        var character = Character(50, 2, 10);
        var ring = Item("ring", EquipmentSlot.Ring, new Dictionary<string, double>
        {
            ["spirit"] = 50, ["spirit.percent"] = 100, ["energyRegen"] = 3,
            ["energyRegen.percent"] = 50, ["healthRegen.percent"] = 100
        });
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        { [EquipmentSlot.RingLeft] = new(ring, null, null) };

        var result = Calculate(character, Catalog(), slots);

        // Final spirit 200; only (2 + 3) regen is increased by 50% before
        // the spirit multiplier and the independent spirit/constant terms.
        Assert.Equal(19.40625, result.Stats["energyRegen"], 8);
        Assert.Equal(43.36, result.Stats["healthRegen"], 8);
    }

    [Fact]
    public void NewVoidstormHelmetHoldoutMatchesGameEnergyRegeneration()
    {
        // Independent game swap: the helmet adds 3/s and 13% regeneration,
        // keeps spirit at 415.84, and the game displays 23.34/s.
        var character = Character(415.84, 2.5999999046325684, 237);
        character.BaselineStats.Set("healthRegen.percent", 14);
        var helmet = Item("voidstorm", EquipmentSlot.Head, new Dictionary<string, double>
        { ["energyRegen"] = 3, ["energyRegen.percent"] = 13 });
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(helmet, null, null) };

        var result = Calculate(character, Catalog(), slots);

        Assert.Equal(23.34, Math.Round(result.Stats["energyRegen"], 2));
        Assert.Equal(293.54, Math.Round(result.Stats["healthRegen"], 2));
    }

    [Fact]
    public void ReplacingAndRemovingSourcesRecalculatesFromTheUnmodifiedBaseline()
    {
        var character = Character(100, 0, 0);
        var catalog = Catalog();
        var item = Item("regen", EquipmentSlot.Head, new Dictionary<string, double> { ["energyRegen"] = 4 });
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) };

        var before = Calculate(character, catalog);
        var equipped = Calculate(character, catalog, slots);
        var restored = Calculate(character, catalog);
        var repeated = Calculate(character, catalog, slots);

        Assert.Equal(7, before.Stats["energyRegen"], 8);
        Assert.Equal(12.041666666666666, equipped.Stats["energyRegen"], 8);
        Assert.Equal(before.Stats["energyRegen"], restored.Stats["energyRegen"]);
        Assert.Equal(equipped.Stats["energyRegen"], repeated.Stats["energyRegen"]);
        Assert.Equal(0, character.BaselineStats["energyRegen"]);
    }

    [Fact]
    public void EquationsComeFromTheLoadedDatabaseRatherThanObservedConstants()
    {
        var character = Character(100, 2, 3);
        var result = Calculate(character, Catalog(
            "(manaRegen + intelligence / 50 + manaTotal / 1000) * elapsedTime",
            "(lifeRegen + physique / 100) * elapsedTime"));

        Assert.Equal(6, result.Stats["energyRegen"], 8);
        Assert.Equal(9.34, result.Stats["healthRegen"], 8);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1200, 4.8)]
    [InlineData(3000, 4)]
    public void ManaTotalVariableIsUsableEnergyAfterReservation(double reserved, double expected)
    {
        var character = Character(100, 2, 3);
        character.BaselineStats.Set("energyReserved", reserved);

        var result = Calculate(character, Catalog("(manaRegen + intelligence / 50 + manaTotal / 1000) * elapsedTime"));

        Assert.Equal(expected, result.Stats["energyRegen"], 8);
        Assert.Equal(2000, result.Stats["energy"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("manaRegen + unknownNewField")]
    [InlineData("manaRegen / 0")]
    [InlineData("(manaRegen")]
    public void MissingOrUnsupportedManaFormulaRetainsKnownContributionAndWarnsOnlyCombat(string equation)
    {
        var result = Calculate(Character(100, 2, 3), Catalog(equation));

        Assert.Equal(2, result.Stats["energyRegen"]);
        var warning = Assert.Single(result.UnsupportedEffects, effect => effect.Record == ResourceRecord);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal([CalculationDiagnostics.CombatDps], warning.AffectedMetrics);
        Assert.False(CalculationDiagnostics.AffectsPanel(warning));
    }

    [Fact]
    public void MissingResourceRecordRetainsContributionsAndWarnsForBothAffectedMetrics()
    {
        var result = Calculate(Character(100, 2, 3), new());

        Assert.Equal(2, result.Stats["energyRegen"]);
        Assert.Equal(3, result.Stats["healthRegen"]);
        Assert.Contains(result.UnsupportedEffects, effect => effect.AffectedMetrics.Contains(CalculationDiagnostics.CombatDps));
        Assert.Contains(result.UnsupportedEffects, effect => effect.AffectedMetrics.Contains(CalculationDiagnostics.Survival));
        Assert.DoesNotContain(result.UnsupportedEffects.Where(effect => effect.Record == ResourceRecord), CalculationDiagnostics.AffectsPanel);
    }

    [Fact]
    public void NormalizedSyntheticStatsAreNotInterpretedAsImportedPlayerInputs()
    {
        var character = Character(100, 2, 3);
        character.BaselineStats.Set("usePlayerAttributeFormula", 0);

        var result = Calculate(character, Catalog());

        Assert.Equal(2, result.Stats["energyRegen"]);
        Assert.Equal(3, result.Stats["healthRegen"]);
    }

    private static CharacterSnapshot Character(double spirit, double energyRegen, double healthRegen) => new()
    {
        Name = "fixture", SaveFilePath = "fixture.gdc", Level = 64,
        BaselineStats = new(new Dictionary<string, double>
        {
            ["usePlayerAttributeFormula"] = 1, ["characterLevel"] = 64,
            ["spirit"] = spirit, ["savedSpirit"] = spirit,
            ["physique"] = 634, ["savedPhysique"] = 634,
            ["cunning"] = 50, ["savedCunning"] = 50,
            ["health"] = 4800, ["energy"] = 2000,
            ["healthRegen"] = healthRegen, ["energyRegen"] = energyRegen
        })
    };

    private static ItemInstance Item(string id, EquipmentSlot slot, Dictionary<string, double> stats) => new()
    {
        Id = id, Name = id, BaseRecord = $"items/{id}.dbr", Slot = slot, Stats = new(stats),
        Source = new(ItemSourceKind.Backpack, "fixture.gdc")
    };

    private static CalculatedLoadout Calculate(CharacterSnapshot character, GameDataCatalog catalog,
        Dictionary<EquipmentSlot, LoadoutVariant>? slots = null) => new CharacterStatEngine().Calculate(character, slots ?? [], catalog);

    private static GameDataCatalog Catalog(string mana = ManaEquation, string life = LifeEquation) => new()
    {
        Records = new Dictionary<string, GameRecord>
        {
            [ResourceRecord] = new()
            {
                RecordName = ResourceRecord,
                TextValues = new Dictionary<string, IReadOnlyList<string>>
                { ["manaRegenEquation"] = [mana], ["lifeRegenEquation"] = [life] }
            }
        }
    };
}
