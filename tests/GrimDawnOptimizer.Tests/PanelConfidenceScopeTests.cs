using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PanelConfidenceScopeTests
{
    [Theory]
    [InlineData(CalculationDiagnostics.Survival)]
    [InlineData(CalculationDiagnostics.CombatDps)]
    public void CombatOrSurvivalWarningLowersOverallCoverageWithoutClaimingAPanelGap(string metric)
    {
        var loadout = Loadout(new UnsupportedEffect("fixture", "Unmodeled scoped effect", [metric]));

        Assert.Equal(ConfidenceLevel.High, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void MissingPanelMechanismLowersBothConfidenceMeasures()
    {
        var loadout = Loadout(new UnsupportedEffect("fixture", "Unknown outgoing effect",
            [CalculationDiagnostics.PanelDps, CalculationDiagnostics.CombatDps]));

        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void ExplicitModeledStateInformationDoesNotCountAsAnUnsupportedMechanism()
    {
        var loadout = Loadout(new UnsupportedEffect("fixture", "Buff uses explicitly selected activation state",
            [CalculationDiagnostics.PanelDps]) { Severity = DiagnosticSeverity.Information });

        Assert.Equal(ConfidenceLevel.High, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.High, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void SkillFallbackCannotReceiveHighPanelConfidence()
    {
        var loadout = Loadout();
        var rotation = new SkillRotationDefinition { IsFallback = true };

        Assert.Equal(ConfidenceLevel.Medium, CalculationDiagnostics.PanelConfidence(loadout, rotation));
        Assert.Equal(ConfidenceLevel.Medium, CalculationDiagnostics.OverallConfidence(loadout, rotation));
    }

    [Fact]
    public void UnknownSeedRollCannotReceiveHighConfidenceEvenIfItsWarningWasNotCopied()
    {
        var item = new ItemInstance
        {
            Id = "fixture", BaseRecord = "fixture", Name = "fixture", Slot = EquipmentSlot.Ring,
            Seed = 42, SeedStatsResolved = false, Source = new ItemSourceLocation(ItemSourceKind.Equipped, "fixture.gdc")
        };
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.RingLeft] = new(item, null, null) },
            Stats = new StatBlock()
        };

        Assert.Equal(ConfidenceLevel.Medium, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.Medium, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void InvalidEquipmentCannotReceiveHighConfidenceEvenWithNoUnsupportedFields()
    {
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = new StatBlock(),
            ValidationErrors = ["Required equipment source is missing"]
        };

        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void UnknownFieldIsConservativeWhileKnownDefensiveDurationIsScopedToSurvival()
    {
        Assert.Contains(CalculationDiagnostics.PanelDps,
            CalculationDiagnostics.AffectedMetrics(["offensiveUnknownFutureField"]));
        Assert.Equal([CalculationDiagnostics.Survival],
            CalculationDiagnostics.AffectedMetrics(["defensiveFireDuration"]));
    }

    [Theory]
    [InlineData("offensiveSlowPhysicalDurationModifier")]
    [InlineData("offensiveSlowBleedingDurationModifier")]
    [InlineData("offensiveSlowFireDurationModifier")]
    [InlineData("offensiveSlowColdDurationModifier")]
    [InlineData("offensiveSlowLightningDurationModifier")]
    [InlineData("offensiveSlowPoisonDurationModifier")]
    [InlineData("offensiveSlowLifeDurationModifier")]
    public void KnownOutgoingDotDurationKeepsItsCombatWarningWithoutClaimingMissingPanelDamage(string field)
    {
        var metrics = CalculationDiagnostics.AffectedMetrics([field]);
        var warning = new UnsupportedEffect("fixture", $"Unmodeled duration: {field}", metrics);
        var loadout = Loadout(warning);

        Assert.Equal([CalculationDiagnostics.CombatDps], metrics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(ConfidenceLevel.High, CalculationDiagnostics.PanelConfidence(loadout, ExactRotation()));
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.OverallConfidence(loadout, ExactRotation()));
    }

    [Fact]
    public void UnknownDurationFieldAndMixedUnknownDamageRemainConservative()
    {
        Assert.Contains(CalculationDiagnostics.PanelDps,
            CalculationDiagnostics.AffectedMetrics(["offensiveSlowFutureDurationModifier"]));
        Assert.Contains(CalculationDiagnostics.PanelDps,
            CalculationDiagnostics.AffectedMetrics(["offensiveSlowFireDurationModifier", "offensiveFutureDamage"]));
    }

    private static CalculatedLoadout Loadout(params UnsupportedEffect[] diagnostics) => new()
    {
        Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = new StatBlock(), UnsupportedEffects = diagnostics
    };
    private static SkillRotationDefinition ExactRotation() => new() { IsFallback = false };
}
