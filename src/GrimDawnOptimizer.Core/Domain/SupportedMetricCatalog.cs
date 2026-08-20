namespace GrimDawnOptimizer.Core.Domain;

/// <summary>
/// The closed set of metrics with a defined combat meaning and normalization
/// benchmark. UI users may tune weights, but may not invent keys whose units
/// the optimizer cannot interpret safely.
/// </summary>
public static class SupportedMetricCatalog
{
    private sealed record Definition(
        string Key,
        string DisplayName,
        string Description,
        double DefaultWeight = 0,
        MetricDirection Direction = MetricDirection.Maximize);

    private static readonly Definition[] Definitions =
    [
        new("dps", "对 BOSS 输出完成度",
            "计入武器、技能、伤害词条、OA、命中、暴击、攻速/施法速度、触发，以及目标护甲和抗性后的最终持续输出。", 1),
        new("effectiveHealth", "综合有效生命完成度",
            "计入生命、十类抗性、护甲与吸收、DA、闪避和格挡后的最终承伤能力；使用十种伤害类型均衡的承伤靶子。", 1)
    ];

    public static IReadOnlyList<string> Keys => Definitions.Select(x => x.Key).ToArray();

    public static List<MetricRule> CreateDefaults() => Merge([]);

    public static List<MetricRule> Merge(IEnumerable<MetricRule> existing)
    {
        var prior = existing
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        return Definitions.Select(definition =>
        {
            prior.TryGetValue(definition.Key, out var saved);
            return new MetricRule
            {
                Key = definition.Key,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                Weight = saved?.Weight ?? definition.DefaultWeight,
                Direction = definition.Direction
            };
        }).ToList();
    }
}
