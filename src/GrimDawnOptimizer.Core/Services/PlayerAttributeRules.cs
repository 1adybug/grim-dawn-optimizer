using GrimDawnOptimizer.Core.Domain;
using System.Text.RegularExpressions;

namespace GrimDawnOptimizer.Core.Services;

internal static class PlayerAttributeRules
{
    internal static void ApplyRegeneration(
        StatBlock stats, GameDataCatalog catalog, ICollection<UnsupportedEffect> diagnostics)
    {
        if (stats["usePlayerAttributeFormula"] <= 0) return;
        const string source = "records/game/playerresourcebehavior.dbr";
        catalog.Records.TryGetValue(source, out var behavior);
        // CharacterBio::Update passes GetAttribute's already percentage-adjusted
        // regeneration into these equations. The attribute contribution belongs
        // inside the database expression, not in the global regen multiplier.
        var variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifeRegen"] = stats["healthRegen"], ["manaRegen"] = stats["energyRegen"],
            ["lifeRegenMod"] = stats["healthRegen.percent"], ["manaRegenMod"] = stats["energyRegen.percent"],
            ["lifeTotal"] = stats["health"],
            ["manaTotal"] = Math.Max(0, stats["energy"] - Math.Max(0, stats["energyReserved"])),
            ["physique"] = stats["physique"], ["intelligence"] = stats["spirit"],
            ["charLevel"] = stats["characterLevel"], ["elapsedTime"] = 1
        };
        Apply("lifeRegenEquation", "healthRegen", CalculationDiagnostics.Survival);
        Apply("manaRegenEquation", "energyRegen", CalculationDiagnostics.CombatDps);

        void Apply(string field, string stat, string metric)
        {
            var equation = behavior?.Text(field);
            // FormulaEvaluator's legacy callers allow absent identifiers as 0;
            // resource equations must instead expose an unrecognized variable.
            if (!string.IsNullOrWhiteSpace(equation) &&
                Regex.Matches(equation, @"[A-Za-z_]\w*").Cast<Match>().All(match => variables.ContainsKey(match.Value)) &&
                FormulaEvaluator.TryEvaluate(equation, variables, out var value))
            {
                stats.Set(stat, value);
                return;
            }
            diagnostics.Add(new(source,
                $"玩家回复公式 {field} 缺失或无法解析，暂只计入已解析的回复词条；回复预算尚不完整。",
                [metric]));
        }
    }

    internal static void ApplyResourceBonuses(StatBlock stats, GameDataCatalog catalog)
    {
        if (stats["usePlayerAttributeFormula"] <= 0) return;
        catalog.Records.TryGetValue("records/creatures/pc/playerlevels.dbr", out var levels);
        double Value(string key, double fallback) => levels?.NumericValues.Values.TryGetValue(key, out var value) == true
            ? value : fallback;
        var physiqueDelta = stats["physique"] - stats["savedPhysique"];
        var cunningDelta = stats["cunning"] - stats["savedCunning"];
        var spiritDelta = stats["spirit"] - stats["savedSpirit"];
        var health = physiqueDelta * Value("lifeIncrement", 20) / Math.Max(1, Value("strengthIncrement", 8)) +
                     cunningDelta * Value("lifeIncrementDexterity", 8) / Math.Max(1, Value("dexterityIncrement", 8)) +
                     spiritDelta * Value("lifeIncrementIntelligence", 12) / Math.Max(1, Value("intelligenceIncrement", 8));
        var energy = spiritDelta * Value("manaIncrement", 16) / Math.Max(1, Value("intelligenceIncrement", 8));
        stats.Add("health", health);
        stats.Add("energy", energy);
    }

    internal static void ApplyDamageBonuses(StatBlock stats, GameDataCatalog catalog)
    {
        catalog.Records.TryGetValue("records/game/combatformulas.dbr", out var formulas);
        // The damage equations use the complete attribute, including its initial
        // 50 points. Direct and duration damage have distinct divisors.
        var variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["dexterityDV"] = stats["cunning"], ["intelligenceDV"] = stats["spirit"],
            ["physicalDamageDV"] = 1, ["pierceDamageDV"] = 1, ["magicalDamageDV"] = 1
        };
        double Bonus(string field, double fallback)
        {
            var equation = formulas?.Text(field);
            return !string.IsNullOrWhiteSpace(equation) && FormulaEvaluator.TryEvaluate(equation, variables, out var factor)
                ? (factor - 1) * 100 : fallback;
        }
        var physical = Bonus("physicalDamageEquation", stats["cunning"] * 100 / 245d);
        var pierce = Bonus("pierceDamageEquation", stats["cunning"] * 100 / 245d);
        var physicalDot = Bonus("physicalDurationDamageEquation", stats["cunning"] * 100 / 215d);
        var magical = Bonus("magicalDamageEquation", stats["spirit"] * 100 / 215d);
        var magicalDot = Bonus("magicalDurationDamageEquation", stats["spirit"] * 100 / 200d);
        stats.Add(StatKeys.DamagePercent("physical"), physical);
        stats.Add(StatKeys.DamagePercent("pierce"), pierce);
        stats.Add(StatKeys.DamagePercent("bleeding"), physicalDot);
        stats.Add(StatKeys.DamageDotPercent("physical"), physicalDot);
        stats.Add(StatKeys.DamageDotPercent("bleeding"), physicalDot);
        foreach (var type in new[] { "fire", "cold", "lightning", "acid", "vitality", "aether", "chaos" })
            stats.Add(StatKeys.DamagePercent(type), magical);
        foreach (var type in new[] { "fire", "cold", "lightning", "acid", "vitality" })
            stats.Add(StatKeys.DamageDotPercent(type), magicalDot);
    }
}
