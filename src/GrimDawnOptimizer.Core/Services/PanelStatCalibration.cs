using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public sealed record PanelStatField(string Key, string Label, string Hint, double Minimum, double Maximum);

/// <summary>Calibrate missing base contributions, retaining each candidate's percentage modifiers.</summary>
public static class PanelStatCalibration
{
    public static IReadOnlyList<PanelStatField> Fields { get; }
    private static readonly HashSet<string> PercentScaled = new(StringComparer.OrdinalIgnoreCase)
    { "health", "energy", "offensiveAbility", "defensiveAbility", "armor", "healthRegen", "energyRegen" };

    private static IReadOnlyList<PanelStatField> BuildFields()
    {
        var result = new List<PanelStatField>
        {
            new("health", "最大生命", "最大值，不是当前剩余生命", 1, 10000000),
            new("energy", "最大能量", "最大值，不是当前剩余能量", 1, 10000000),
            new("offensiveAbility", "攻击能力 OA", "开启常驻光环后的面板值", 1, 100000),
            new("defensiveAbility", "防御能力 DA", "开启常驻光环后的面板值", 1, 100000),
            new("attackSpeed", "攻击速度（%）", "例如 145；已封顶时填未封顶值，不明则留空", 1, 1000),
            new("castSpeed", "施法速度（%）", "例如 130；已封顶时填未封顶值，不明则留空", 1, 1000),
            new("armor", "护甲值", "角色面板显示的护甲", 0, 100000),
            new("armorAbsorption", "护甲吸收率（%）", "例如 70，不是护甲值", 0, 100),
            new("healthRegen", "每秒生命回复", "无临时回血效果时的数值", 0, 1000000),
            new("energyRegen", "每秒能量回复", "无临时药水效果时的数值", 0, 1000000),
            new("lifeSteal", "攻击伤害转化为生命（%）", "全局吸血，不填特定技能独有的吸血", 0, 100)
        };
        foreach (var (type, label) in DamageLabels)
        {
            result.Add(new(StatKeys.Resistance(type), $"{label}抗性（含超抗）", "当前难度的面板抗性 + 超出上限的部分；不含临时降抗", -100, 1000));
            result.Add(new(StatKeys.MaxResistance(type), $"{label}抗性上限（%）", "通常为 80；有提高上限的效果时填写实际值", 0, 100));
        }
        return result;
    }

    public static readonly (string Type, string Label)[] DamageLabels =
    [ ("physical", "物理"), ("pierce", "穿刺"), ("fire", "火焰"), ("cold", "冰冷"),
      ("lightning", "闪电"), ("acid", "酸毒"), ("vitality", "活力"), ("aether", "虚化"),
      ("chaos", "混乱"), ("bleeding", "流血") ];

    static PanelStatCalibration() => Fields = BuildFields();

    public static IReadOnlyDictionary<string, double> Create(StatBlock current, OptimizationProfile profile)
    {
        var corrections = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (!profile.CurrentPanelStats.TryGetValue(field.Key, out var measured)) continue;
            if (!double.IsFinite(measured) || measured < field.Minimum || measured > field.Maximum)
                throw new InvalidOperationException($"{field.Label}必须在 {field.Minimum}–{field.Maximum} 之间。");
            var key = field.Key;
            if (key is "attackSpeed" or "castSpeed")
            {
                var weaponFactor = key == "attackSpeed" && current["weaponAttackSpeedFactor"] > 0
                    ? current["weaponAttackSpeedFactor"] : 1;
                if (key == "attackSpeed" && current["weaponAttackAnimationRatio"] > 0)
                    weaponFactor *= current["weaponAttackAnimationRatio"];
                corrections[key] = measured / weaponFactor - 100 - current[key];
            }
            else if (key.EndsWith("MaxResistance", StringComparison.OrdinalIgnoreCase))
                corrections[key] = measured - 80 - current[key];
            else if (key.EndsWith("Resistance", StringComparison.OrdinalIgnoreCase))
            {
                var type = key[..^"Resistance".Length];
                corrections[key] = measured - DifficultyRules.PlayerPenalty(profile.Scenario, type) - current[key];
            }
            else
                corrections[key] = (measured - current[key]) / Factor(current, key);
        }
        return corrections;
    }

    public static CalculatedLoadout Apply(CalculatedLoadout loadout, OptimizationProfile profile)
    {
        if (profile.RuntimePanelCorrections.Count == 0) return loadout;
        var stats = loadout.Stats.Clone();
        foreach (var (key, correction) in profile.RuntimePanelCorrections)
            stats.Add(key, correction * Factor(stats, key));
        foreach (var key in new[] { "health", "energy", "offensiveAbility", "defensiveAbility" })
            stats.Set(key, Math.Max(1, stats[key]));
        foreach (var key in new[] { "armor", "healthRegen", "energyRegen", "lifeSteal" })
            stats.Set(key, Math.Max(0, stats[key]));
        return new CalculatedLoadout
        {
            Slots = loadout.Slots, Stats = stats, Effects = loadout.Effects,
            UnsupportedEffects = loadout.UnsupportedEffects, ValidationErrors = loadout.ValidationErrors
        };
    }

    private static double Factor(StatBlock stats, string key) => PercentScaled.Contains(key)
        ? Math.Max(0.01, 1 + stats[key + ".percent"] / 100d) : 1;

    public static IReadOnlyDictionary<string, double> SheetValues(CalculatedLoadout loadout, CombatScenario scenario, double dps)
    {
        var stats = loadout.Stats;
        var result = Fields.ToDictionary(x => x.Key, x => stats[x.Key], StringComparer.OrdinalIgnoreCase);
        result["dps"] = dps;
        foreach (var key in new[] { "attackSpeed", "castSpeed" })
        {
            var factor = key == "attackSpeed" && stats["weaponAttackSpeedFactor"] > 0
                ? stats["weaponAttackSpeedFactor"] : 1;
            result[key] = Math.Clamp(factor * (100 + stats[key]), 20, 200 + Math.Max(0, stats[key + ".maxBonus"]));
            if (key == "attackSpeed" && stats["weaponAttackAnimationRatio"] > 0)
                result[key] *= stats["weaponAttackAnimationRatio"];
        }
        foreach (var (type, _) in DamageLabels)
        {
            var cap = Math.Clamp(80 + stats[StatKeys.MaxResistance(type)], 0, 100);
            result[StatKeys.MaxResistance(type)] = cap;
            // Compare an un-debuffed in-game sheet, not the configured stress scenario.
            result[StatKeys.Resistance(type)] = Math.Clamp(stats[StatKeys.Resistance(type)] + DifficultyRules.PlayerPenalty(scenario, type), -100, cap);
        }
        return result;
    }
}
