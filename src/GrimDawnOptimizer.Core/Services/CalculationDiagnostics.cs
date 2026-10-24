using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class CalculationDiagnostics
{
    public const string PanelDps = "panelDps";
    public const string CombatDps = "dps";
    public const string Survival = "effectiveHealth";

    private static readonly HashSet<string> OutgoingDotDurationModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "offensiveSlowPhysicalDurationModifier", "offensiveSlowBleedingDurationModifier",
        "offensiveSlowFireDurationModifier", "offensiveSlowColdDurationModifier",
        "offensiveSlowLightningDurationModifier", "offensiveSlowPoisonDurationModifier",
        "offensiveSlowLifeDurationModifier"
    };

    // Missing source data is conservative. Known defensive/control mechanics
    // must not be described as missing outgoing character-sheet damage.
    public static IReadOnlyList<string> AffectedMetrics(IEnumerable<string> fields)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            // CombatAttributeDurDamage::Process updates duration separately
            // (0xE322F), while the sheet getter (0xE32E0) reads only damage rate.
            // Keep the combat warning: duration/refresh coverage is not simulated.
            if (OutgoingDotDurationModifiers.Contains(field))
                result.Add(CombatDps);
            else if (field.StartsWith("defensive", StringComparison.OrdinalIgnoreCase) ||
                field.StartsWith("block", StringComparison.OrdinalIgnoreCase) ||
                field.StartsWith("characterLife", StringComparison.OrdinalIgnoreCase) ||
                field.StartsWith("skillLife", StringComparison.OrdinalIgnoreCase))
                result.Add(Survival);
            else if (field.StartsWith("retaliation", StringComparison.OrdinalIgnoreCase))
                result.Add(CombatDps);
            else if (field.StartsWith("characterManaLimitReserve", StringComparison.OrdinalIgnoreCase))
                result.Add(CombatDps);
            else if (field.StartsWith("offensiveStun", StringComparison.OrdinalIgnoreCase) ||
                     field.StartsWith("offensiveKnockdown", StringComparison.OrdinalIgnoreCase) ||
                     field.StartsWith("offensiveTotalDamageReduction", StringComparison.OrdinalIgnoreCase))
                result.Add(Survival);
            else if (field.Contains("ResistanceReduction", StringComparison.OrdinalIgnoreCase))
                result.Add(CombatDps);
            else
                result.UnionWith([PanelDps, CombatDps, Survival]);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public static bool AffectsPanel(UnsupportedEffect effect) =>
        effect.Severity == DiagnosticSeverity.Warning &&
        effect.AffectedMetrics.Contains(PanelDps, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<UnsupportedEffect> RotationDiagnostics(
        CalculatedLoadout loadout, SkillRotationDefinition rotation)
    {
        var diagnostics = rotation.UnsupportedEffects.ToList();
        var weapons = new[] { EquipmentSlot.MainHand, EquipmentSlot.OffHand }
            .Where(loadout.Slots.ContainsKey)
            .Select(slot => loadout.Slots[slot])
            .Where(variant => variant.Item.Slot == EquipmentSlot.MainHand)
            .ToArray();
        var hasWeaponPool = rotation.WeaponPoolAttacks.Any(attack => attack.Chance > 0) ||
                            rotation.WeaponPoolChance > 0;
        var piercingModifier = 1 + (loadout.Stats["armorPiercingModifier"] +
                                   rotation.FlatDamage["armorPiercingModifier"]) / 100d;
        if (rotation.WeaponDamagePercent > 0 && weapons.Length == 2 && hasWeaponPool &&
            piercingModifier > 0 && weapons.Any(weapon =>
                weapon.CombinedStats[StatKeys.WeaponDamage("physical")] > 0 &&
                weapon.CombinedStats[StatKeys.WeaponArmorPiercing] > 0))
        {
            // The sheet excludes WPS. Combat still approximates their hand choice:
            // WeaponStrikes alone does not identify a one-hand or both-hand collector.
            diagnostics.Add(new(rotation.MainSkillRecord,
                "双持武器池技能的命中回调与护甲穿刺归属尚未完整模拟；这不影响不含武器池技能的面板 DPS。",
                [CombatDps]));
        }
        return diagnostics;
    }

    public static ConfidenceLevel PanelConfidence(CalculatedLoadout loadout, SkillRotationDefinition rotation)
    {
        if (!loadout.IsValid || loadout.UnsupportedEffects.Concat(rotation.UnsupportedEffects).Any(AffectsPanel)) return ConfidenceLevel.Low;
        if (rotation.IsFallback || loadout.Slots.Values.Any(x => x.Item.Seed != 0 && !x.Item.SeedStatsResolved))
            return ConfidenceLevel.Medium;
        // Coverage is not a numerical accuracy guarantee or an empirical certificate.
        return ConfidenceLevel.High;
    }

    public static ConfidenceLevel OverallConfidence(CalculatedLoadout loadout, SkillRotationDefinition rotation)
    {
        if (!loadout.IsValid || loadout.UnsupportedEffects.Concat(RotationDiagnostics(loadout, rotation)).Any(x => x.Severity == DiagnosticSeverity.Warning))
            return ConfidenceLevel.Low;
        return PanelConfidence(loadout, rotation);
    }

    public static string ScopeLabel(UnsupportedEffect effect)
    {
        var labels = new List<string>();
        if (effect.AffectedMetrics.Contains(PanelDps, StringComparer.OrdinalIgnoreCase)) labels.Add("面板 DPS");
        if (effect.AffectedMetrics.Contains(CombatDps, StringComparer.OrdinalIgnoreCase)) labels.Add("实战输出");
        if (effect.AffectedMetrics.Contains(Survival, StringComparer.OrdinalIgnoreCase)) labels.Add("生存");
        return labels.Count == 0 ? "状态说明" : string.Join("、", labels);
    }
}
