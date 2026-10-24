using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SkillInputDiagnosticsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LearnedExclusiveSkillNeedsAnEstablishedActiveChoice(bool? active, bool expectedWarning)
    {
        const string record = "skills/exclusive.dbr";
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord>
        {
            [record] = new() { RecordName = record, RecordType = "Skill_BuffSelfToggled",
                NumericValues = new StatBlock(new Dictionary<string, double> { ["exclusiveSkill"] = 1 }) }
        } };
        var character = new CharacterSnapshot { Name = "test", SaveFilePath = "test.gdc",
            Skills = [new(record, "exclusive", 1, 0, true, "", "", Active: active)] };
        var result = new CharacterStatEngine().Calculate(character, new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);

        Assert.Equal(expectedWarning, result.UnsupportedEffects.Any(x => x.Record == record && x.AffectedMetrics.Contains("panelDps")));
        Assert.Equal(expectedWarning ? ConfidenceLevel.Low : ConfidenceLevel.High,
            CalculationDiagnostics.PanelConfidence(result, new() { IsFallback = false }));
    }

    [Fact]
    public void MissingCurrentWeaponAnimationCannotReportHighPanelCoverage()
    {
        const string player = "records/creatures/pc/malepc01.dbr";
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord>
        {
            [player] = new() { RecordName = player, TextValues = new Dictionary<string, IReadOnlyList<string>> { ["charAnimationTableName"] = ["animations.dbr"] } }
        } };
        var result = new CharacterStatEngine().Calculate(Character(), new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);

        Assert.Contains(result.UnsupportedEffects, x => x.Record == player && x.AffectedMetrics.Contains("panelDps"));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(result, new() { IsFallback = false }));
    }

    [Fact]
    public void UnrelatedMissingAnimationDoesNotInvalidateCompleteCurrentWeaponGroup()
    {
        const string player = "records/creatures/pc/malepc01.dbr";
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord>
        {
            [player] = new() { RecordName = player, TextValues = new Dictionary<string, IReadOnlyList<string>> { ["charAnimationTableName"] = ["animations.dbr"] } }
        }, AnimationProfiles = new Dictionary<string, AnimationSpeedProfile>
        {
            ["animations.dbr"] = new() { AttackGroups = new Dictionary<string, AnimationTimingGroup>
                { ["unarmed"] = new("unarmed", 1, []) }, UnresolvedResources = ["dualRangedAttackAnim1: unavailable"] }
        } };
        var result = new CharacterStatEngine().Calculate(Character(), new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);

        Assert.DoesNotContain(result.UnsupportedEffects, x => x.Record == player);
    }

    [Theory]
    [InlineData("Skill_Passive", "offensiveUnknownBoost")]
    [InlineData("Skill_UnknownAura", "offensiveFireModifier")]
    public void UnknownLearnedPersistentClassOrFieldCannotReportHighCoverage(string type, string field)
    {
        var record = new GameRecord { RecordName = "skills/unknown.dbr", RecordType = type,
            NumericValues = new StatBlock(new Dictionary<string, double> { [field] = 10 }) };
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord> { [record.RecordName] = record } };
        var result = new CharacterStatEngine().Calculate(Character(record.RecordName), new Dictionary<EquipmentSlot, LoadoutVariant>(), catalog);

        Assert.Contains(result.UnsupportedEffects, x => x.AffectedMetrics.Contains("panelDps"));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(result, new() { IsFallback = false }));
    }

    [Fact]
    public void SelectedSkillChecksUnsupportedFieldsAtItsActualLevel()
    {
        var record = new GameRecord { RecordName = "skills/attack.dbr", RecordType = "Skill_AttackWave",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>>
            { ["offensivePhysicalMin"] = [10, 20], ["offensiveUnknownBoost"] = [0, 5] } };
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord> { [record.RecordName] = record } };
        var skill = new CharacterSkill(record.RecordName, "attack", 2, 0, true, "", "");
        var rotation = SkillRotationFactory.Build(Character(), catalog, skill);

        Assert.False(rotation.IsFallback);
        Assert.Equal(20, rotation.FlatDamage[StatKeys.DamageFlat("physical")]);
        Assert.Contains(rotation.UnsupportedEffects, x => x.AffectedMetrics.Contains("panelDps"));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(new() { Stats = new(), Slots = new Dictionary<EquipmentSlot, LoadoutVariant>() }, rotation));
    }

    [Fact]
    public void SelectedUnknownAttackClassAndMissingLinkedRecordAreExplicitDiagnostics()
    {
        var record = new GameRecord { RecordName = "skills/attack.dbr", RecordType = "Skill_AttackUnmodeled",
            NumericValues = new StatBlock(new Dictionary<string, double> { ["offensivePhysicalMin"] = 20 }),
            TextValues = new Dictionary<string, IReadOnlyList<string>> { ["skillProjectileName"] = ["missing.dbr"] } };
        var catalog = new GameDataCatalog { Records = new Dictionary<string, GameRecord> { [record.RecordName] = record } };
        var rotation = SkillRotationFactory.Build(Character(), catalog, new(record.RecordName, "attack", 1, 0, true, "", ""));

        Assert.Contains(rotation.UnsupportedEffects, x => x.Record == record.RecordName);
        Assert.Contains(rotation.UnsupportedEffects, x => x.Record == "missing.dbr");
        Assert.Equal(20, rotation.FlatDamage[StatKeys.DamageFlat("physical")]);
    }

    private static CharacterSnapshot Character(string skill = "") => new()
    {
        Name = "test", SaveFilePath = "test.gdc",
        Skills = string.IsNullOrWhiteSpace(skill) ? [] : [new(skill, "test", 1, 0, true, "", "")]
    };
}
