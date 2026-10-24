using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class CompatibilityDiagnosticAuditTests
{
    [Fact]
    public void EquippedItemWithMissingBaseRecordCannotDisappearWithoutADiagnostic()
    {
        var warnings = new List<string>();
        var item = SaveReader.ConvertItem(new RawItem
        {
            BaseRecord = "records/items/missing.dbr", Seed = 1,
            SourceKind = ItemSourceKind.Equipped, EquippedSlot = EquipmentSlot.MainHand
        }, "fixture.gdc", new GameDataCatalog(), warnings);

        Assert.True(warnings.Count > 0 || item?.UnsupportedEffects.Any(CalculationDiagnostics.AffectsPanel) == true);
    }

    [Fact]
    public void ControllerWithChanceButMissingTriggerTypeIsUnresolved()
    {
        var owner = Owner("skills/damage.dbr", "controllers/missing-type.dbr");
        var catalog = Catalog(owner,
            Record("skills/damage.dbr", "Skill_AttackRadius", new() { ["offensiveFireMin"] = 100 }),
            Record("controllers/missing-type.dbr", "Controller", new() { ["chanceToRun"] = 25 }));

        var effect = Assert.Single(SaveReader.BuildTriggeredEffects([owner], catalog));

        Assert.False(effect.TriggerResolved);
        Assert.Equal(0, effect.Chance);
    }

    [Fact]
    public void GrantedWeaponPoolAttackRetainsUnknownCombatDamageDiagnosticsWithoutAffectingPanelScope()
    {
        var owner = Owner("skills/wps.dbr");
        var catalog = Catalog(owner, Record("skills/wps.dbr", "Skill_WPAttack_Basic", new()
        {
            ["skillChanceWeight"] = 20, ["weaponDamagePct"] = 120,
            ["offensiveUnknownFutureModifier"] = 10
        }));

        var warnings = SaveReader.FindUnsupportedItemSkillEffects([owner], catalog, "fixture");

        var diagnostic = Assert.Single(warnings,
            x => x.Description.Contains("offensiveUnknownFutureModifier", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(CalculationDiagnostics.CombatDps, diagnostic.AffectedMetrics);
        Assert.DoesNotContain(CalculationDiagnostics.PanelDps, diagnostic.AffectedMetrics);
    }

    [Fact]
    public void MissingLinkedBuffPayloadRetainsAPanelWarning()
    {
        var owner = Owner("skills/buff.dbr");
        var root = Record("skills/buff.dbr", "Skill_BuffSelfDuration",
            new() { ["skillActiveDuration"] = 5 }, new() { ["buffSkillName"] = "skills/missing-payload.dbr" });
        var catalog = Catalog(owner, root);

        var warnings = SaveReader.FindUnsupportedItemSkillEffects([owner], catalog, "fixture");

        Assert.Contains(warnings, CalculationDiagnostics.AffectsPanel);
    }

    [Fact]
    public void TriggeredHealingRemainsUnsupportedUntilItsSustainIsActuallySimulated()
    {
        var owner = Owner("skills/heal.dbr", "controllers/on-hit.dbr");
        var catalog = Catalog(owner,
            Record("skills/heal.dbr", "Skill_Heal", new() { ["characterLife"] = 100 }),
            Record("controllers/on-hit.dbr", "Controller", new() { ["chanceToRun"] = 25 },
                new() { ["triggerType"] = "OnHit" }));

        var warnings = SaveReader.FindUnsupportedItemSkillEffects([owner], catalog, "fixture");

        Assert.Contains(warnings, x => x.Severity == DiagnosticSeverity.Warning &&
            x.AffectedMetrics.Contains(CalculationDiagnostics.Survival));
    }

    [Fact]
    public void ZeroSeedExpectedValueFallbackCannotReceiveHighPanelConfidence()
    {
        var record = Record("records/items/unseeded-ring.dbr", "ArmorJewelry_Ring",
            new() { ["offensiveFireModifier"] = 100 });
        var item = SaveReader.ConvertItem(new RawItem { BaseRecord = record.RecordName, Seed = 0 },
            "fixture.gdc", Catalog(record), [])!;
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.RingLeft] = new(item, null, null)
            },
            Stats = item.Stats,
            UnsupportedEffects = item.UnsupportedEffects
        };

        Assert.False(item.SeedStatsResolved);
        Assert.NotEqual(ConfidenceLevel.High,
            CalculationDiagnostics.PanelConfidence(loadout, new SkillRotationDefinition { IsFallback = false }));
    }

    private static GameRecord Owner(string skill, string controller = "") =>
        Record("records/items/owner.dbr", "Item", new(),
            new() { ["itemSkillName"] = skill, ["itemSkillAutoController"] = controller });

    private static GameRecord Record(string name, string type, Dictionary<string, double> numeric,
        Dictionary<string, string>? text = null) => new()
    {
        RecordName = name, RecordType = type, NumericValues = new StatBlock(numeric),
        TextValues = (text ?? []).Append(new KeyValuePair<string, string>("Class", type))
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)[x.Value], StringComparer.OrdinalIgnoreCase)
    };

    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    {
        Records = records.ToDictionary(x => x.RecordName, StringComparer.OrdinalIgnoreCase)
    };
}
