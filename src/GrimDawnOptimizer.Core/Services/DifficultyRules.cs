using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>Single-player campaign difficulty; temporary encounter debuffs are separate.</summary>
public static class DifficultyRules
{
    // User-supplied difficulty selection screenshots. Normal is the reference;
    // these are explicit model presets, not a reconstruction of engine formulas.
    public sealed record Preset(double DamagePercent, double HealthPercent, double LootPercent, double ExperiencePercent);
    public static Preset Settings(GameDifficulty difficulty) => difficulty switch
    {
        GameDifficulty.Veteran => new(15, 140, 50, 10),
        GameDifficulty.Elite => new(21, 270, 250, 0),
        GameDifficulty.Ultimate => new(30, 530, 400, 0),
        _ => new(0, 0, 0, 0)
    };
    public static double HealthMultiplier(GameDifficulty difficulty) => 1 + Settings(difficulty).HealthPercent / 100d;

    public static string Name(GameDifficulty difficulty) => difficulty switch
    {
        GameDifficulty.Veteran => "老兵", GameDifficulty.Elite => "精英",
        GameDifficulty.Ultimate => "终极", _ => "普通（新手）"
    };

    public static void Apply(GameDataCatalog catalog, CombatScenario scenario)
    {
        if (!Enum.IsDefined(scenario.Difficulty)) scenario.Difficulty = GameDifficulty.Normal;
        var tier = scenario.Difficulty switch { GameDifficulty.Elite => 1, GameDifficulty.Ultimate => 2, _ => 0 };
        // AttributePak arrays: Normal P1..P4, Elite P1..P4, Ultimate P1..P4.
        var row = tier * 4 + 1;
        var engine = catalog.Records.GetValueOrDefault("records/game/gameengine.dbr");
        GameRecord? Record(string key, string fallback) => catalog.Records.GetValueOrDefault(
            string.IsNullOrWhiteSpace(engine?.Text(key)) ? fallback : engine!.Text(key));
        var enemy = Record("monsterAttributePak", "records/game/balancingadjustment_mp+difficulty_enemies01.dbr");
        var veteran = scenario.Difficulty == GameDifficulty.Veteran
            ? Record("challengeAdjustment", "records/game/balancingadjustment_challengemode_enemies01.dbr") : null;
        scenario.DifficultyPlayerModifiers = null;
        scenario.DifficultyEnemyModifiers = enemy is null ? new() : GameStatTranslator.TranslateAtLevel(enemy, row);
        if (enemy is not null)
            scenario.DifficultyEnemyModifiers.Set("lifeLeechResistance",
                GameStatTranslator.NumericAtLevel(enemy, "defensiveSlowLifeLeach", row));
        if (veteran is not null) scenario.DifficultyEnemyModifiers.Add(GameStatTranslator.TranslateAtLevel(veteran, 1));
        var preset = Settings(scenario.Difficulty);
        // Keep database OA/DA, armor and enemy resistances, but never mix its
        // health, damage or attack-rate adjustments with the UI presets.
        scenario.DifficultyEnemyModifiers.Set("health", 0);
        scenario.DifficultyEnemyModifiers.Set("health.percent", preset.HealthPercent);
        scenario.DifficultyEnemyModifiers.Set("damage.total.percent", preset.DamagePercent);
        scenario.DifficultyEnemyModifiers.Set("attackSpeed", 0);
        foreach (var type in StatKeys.DamageTypes)
        {
            scenario.DifficultyEnemyModifiers.Set(StatKeys.DamagePercent(type), 0);
            scenario.DifficultyEnemyModifiers.Set(StatKeys.DamageDotPercent(type), 0);
        }
        scenario.DifficultySummary = $"{Name(scenario.Difficulty)} · 单人 · " +
            $"按游戏截图固定：怪物伤害 +{preset.DamagePercent:0}%（×{1 + preset.DamagePercent / 100d:0.##}），" +
            $"生命 +{preset.HealthPercent:0}%（×{HealthMultiplier(scenario.Difficulty):0.##}）。" +
            "普通为基准，不叠加数据库中的伤害、生命或攻速难度修正。\n" +
            $"玩家难度降抗：火/冰/电/酸毒/穿刺 {PlayerPenalty(scenario, "fire"):0}；" +
            $"活力/虚化/混乱/流血 {PlayerPenalty(scenario, "vitality"):0}；物理 0（百分点）。\n" +
            $"掉落质量 +{preset.LootPercent:0}%、额外经验 +{preset.ExperiencePercent:0}% 仅展示，不参与配装评分。" +
            (enemy is null ? "未读取到怪物其他难度属性，OA/DA 等不含该项修正。" : "怪物 OA/DA、护甲及抗性仍读取本机数据库。") +
            $"来袭伤害使用{IncomingKindName(scenario.IncomingDamage.Kind)}混合伤害模板" +
            (scenario.AdditionalIncomingDamage.Count > 0 ? $"及 {scenario.AdditionalIncomingDamage.Count} 组独立攻击" : "") +
            "，未还原具体 BOSS 技能循环。";
    }

    public static double PlayerPenalty(CombatScenario scenario, string type)
    {
        // The menu gives the primary penalty; preserve the known per-type groups.
        var primary = type is "fire" or "cold" or "lightning" or "acid" or "pierce";
        return scenario.Difficulty switch
        {
            GameDifficulty.Elite when primary => -25,
            GameDifficulty.Ultimate when primary => -50,
            GameDifficulty.Ultimate when type is "vitality" or "aether" or "chaos" or "bleeding" => -25,
            _ => 0
        };
    }

    public static double EffectiveResistance(StatBlock stats, CombatScenario scenario, string type) =>
        Math.Clamp(stats[StatKeys.Resistance(type)] + PlayerPenalty(scenario, type) -
                   Math.Clamp(scenario.PlayerResistanceReduction.GetValueOrDefault(type), 0, 100),
            -100, Math.Clamp(80 + stats[StatKeys.MaxResistance(type)], 0, 100));

    public static IncomingDamageProfile Incoming(CombatScenario scenario)
        => ScaleIncoming(scenario, scenario.IncomingDamage);

    internal static IEnumerable<IncomingDamageProfile> IncomingProfiles(CombatScenario scenario) =>
        new[] { scenario.IncomingDamage }.Concat(scenario.AdditionalIncomingDamage).Select(x => ScaleIncoming(scenario, x));

    private static string IncomingKindName(IncomingAttackKind kind) => kind switch
    {
        IncomingAttackKind.Projectile => "投射物", IncomingAttackKind.Spell => "法术", _ => "近战"
    };

    private static IncomingDamageProfile ScaleIncoming(CombatScenario scenario, IncomingDamageProfile baseline)
    {
        var factor = (1 + Settings(scenario.Difficulty).DamagePercent / 100d) *
                     (1 + Math.Clamp(scenario.EnemyDamageIncreasePercent, 0, 1000) / 100d);
        double Damage(double value) => Math.Max(0, value) * factor;
        return baseline with
        {
            HitsPerSecond = Math.Max(0, baseline.HitsPerSecond),
            PhysicalPerHit = Damage(baseline.PhysicalPerHit), FirePerHit = Damage(baseline.FirePerHit),
            ColdPerHit = Damage(baseline.ColdPerHit), LightningPerHit = Damage(baseline.LightningPerHit),
            AcidPerHit = Damage(baseline.AcidPerHit), PiercePerHit = Damage(baseline.PiercePerHit),
            VitalityPerHit = Damage(baseline.VitalityPerHit), AetherPerHit = Damage(baseline.AetherPerHit),
            ChaosPerHit = Damage(baseline.ChaosPerHit), BleedingPerHit = Damage(baseline.BleedingPerHit)
        };
    }
}
