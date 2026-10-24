using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class TriggeredSkillDiagnosticTests
{
    [Fact]
    public void DeepBuffUnknownFieldsRemainWarningsAlongsideActivationInformation()
    {
        var owner = Owner();
        var root = Record("skill", "Skill_BuffSelfDuration", new() { ["skillActiveDuration"] = 5 }, new() { ["buffSkillName"] = "middle" });
        var middle = Record("middle", "SkillBuff_Passive", text: new() { ["buffSelfSkillName"] = "leaf" });
        var leaf = Record("leaf", "SkillBuff_Passive", new() { ["offensiveUnknownFutureModifier"] = 10 });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, root, middle, leaf), "fixture");

        Assert.Contains(diagnostics, x => x.Severity == DiagnosticSeverity.Information);
        Assert.Contains(diagnostics, x => CalculationDiagnostics.AffectsPanel(x) &&
            x.Description.Contains("offensiveUnknownFutureModifier", StringComparison.Ordinal));
    }

    [Fact]
    public void DeepMissingBuffLinkIsReportedAndCyclesDoNotRepeatDiagnostics()
    {
        var owner = Owner();
        var root = Record("skill", "Skill_BuffSelfDuration", new() { ["skillActiveDuration"] = 5 }, new() { ["buffSkillName"] = "middle" });
        var middle = Record("middle", "SkillBuff_Passive", text: new()
        {
            ["buffSelfSkillName"] = "missing", ["buffSelf2SkillName"] = "skill"
        });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, root, middle), "fixture");

        var missing = Assert.Single(diagnostics, x => x.Record == "missing");
        Assert.True(CalculationDiagnostics.AffectsPanel(missing));
    }

    [Theory]
    [InlineData("characterLife")]
    [InlineData("characterLifeRegen")]
    [InlineData("offensiveLifeLeechMin")]
    public void DamageDoesNotHideUnsimulatedHealingOrLifeSteal(string survivalField)
    {
        var owner = Owner(controller: "controller");
        var skill = Record("skill", "Skill_AttackRadius", new() { ["offensiveFireMin"] = 100, [survivalField] = 20 });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, skill, Controller()), "fixture");

        Assert.Contains(diagnostics, x => x.Severity == DiagnosticSeverity.Warning &&
            x.AffectedMetrics.Contains(CalculationDiagnostics.Survival));
        Assert.DoesNotContain(diagnostics, CalculationDiagnostics.AffectsPanel);
    }

    [Fact]
    public void OrdinaryDamageProcDoesNotReceiveANonDamageWarning()
    {
        var owner = Owner(controller: "controller");
        var skill = Record("skill", "Skill_AttackRadius", new() { ["offensiveFireMin"] = 100 });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, skill, Controller()), "fixture");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DeepProcPayloadNotConsumedByTheDamageTranslatorKeepsACombatWarning()
    {
        var owner = Owner(controller: "controller");
        var skill = Record("skill", "Skill_AttackRadius", new() { ["offensiveFireMin"] = 100 }, new() { ["buffSkillName"] = "middle" });
        var middle = Record("middle", "SkillBuff_Passive", text: new() { ["buffSelfSkillName"] = "leaf" });
        var leaf = Record("leaf", "SkillBuff_Passive", new() { ["offensiveColdMin"] = 50 });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, skill, middle, leaf, Controller()), "fixture");

        Assert.Contains(diagnostics, x => x.Severity == DiagnosticSeverity.Warning &&
            x.AffectedMetrics.Contains(CalculationDiagnostics.CombatDps));
        Assert.DoesNotContain(diagnostics, CalculationDiagnostics.AffectsPanel);
    }

    [Theory]
    [InlineData("Skill_FutureAttack")]
    [InlineData("Skill_FutureEffect")]
    public void UnknownGrantedClassCannotBeAdvertisedAsSupportedDirectAttack(string type)
    {
        var owner = Owner();
        var skill = Record("skill", type, new() { ["offensiveFireMin"] = 100 });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, skill), "fixture");

        Assert.Contains(diagnostics, CalculationDiagnostics.AffectsPanel);
        Assert.DoesNotContain(diagnostics, x => x.Severity == DiagnosticSeverity.Information);
    }

    [Fact]
    public void KnownDirectAttackIsInformationButItsUnknownFieldsRemainWarnings()
    {
        var owner = Owner();
        var skill = Record("skill", "Skill_AttackWave", new()
        {
            ["offensiveFireMin"] = 100, ["offensiveUnknownFutureModifier"] = 10
        });

        var diagnostics = SaveReader.FindUnsupportedItemSkillEffects([owner], Catalog(owner, skill), "fixture");

        Assert.Contains(diagnostics, x => x.Severity == DiagnosticSeverity.Information);
        Assert.Contains(diagnostics, CalculationDiagnostics.AffectsPanel);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    public void TriggerMetadataUsesGrantedLevelAndDeepBuffDuration(bool controllerCooldown, double expectedCooldown)
    {
        var owner = Owner(controller: "controller", level: 2);
        var skill = Record("skill", "Skill_BuffSelfDuration", text: new() { ["buffSkillName"] = "middle" },
            series: new() { ["skillCooldownTime"] = [9, 4, 2] });
        var middle = Record("middle", "SkillBuff_Passive", text: new() { ["buffSkillName"] = "leaf" });
        var leaf = Record("leaf", "SkillBuff_Passive", series: new() { ["skillActiveDuration"] = [1, 3, 8] });
        var controller = controllerCooldown
            ? Record("controller", "Controller", new() { ["chanceToRun"] = 25 },
                new() { ["triggerType"] = "OnHit" }, new() { ["triggerCooldown"] = [7, 5, 1] })
            : Controller();

        var effect = Assert.Single(SaveReader.BuildTriggeredEffects([owner], Catalog(owner, skill, middle, leaf, controller)));

        Assert.Equal(expectedCooldown, effect.CooldownSeconds);
        Assert.Equal(3, effect.DurationSeconds);
    }

    private static GameRecord Owner(string controller = "", int level = 1) => Record("owner", "Item",
        new() { ["itemSkillLevelEq"] = level }, new() { ["itemSkillName"] = "skill", ["itemSkillAutoController"] = controller });
    private static GameRecord Controller() => Record("controller", "Controller", new() { ["chanceToRun"] = 25 },
        new() { ["triggerType"] = "OnHit" });
    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    {
        Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase)
    };
    private static GameRecord Record(string name, string type, Dictionary<string, double>? numeric = null,
        Dictionary<string, string>? text = null, Dictionary<string, IReadOnlyList<double>>? series = null)
    {
        numeric ??= [];
        foreach (var (key, values) in series ?? []) numeric[key] = values.Average();
        return new GameRecord
        {
            RecordName = name, RecordType = type, NumericValues = new StatBlock(numeric), NumericSeries = series ?? [],
            TextValues = (text ?? []).ToDictionary(x => x.Key, x => (IReadOnlyList<string>)[x.Value], StringComparer.OrdinalIgnoreCase)
        };
    }
}
