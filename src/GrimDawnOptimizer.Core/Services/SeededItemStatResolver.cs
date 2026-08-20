using GrimDawnItemStats;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>
/// Adapts the raw ARZ records used by this project to Item Assistant's seed-stat engine.
/// The vendored engine is kept independent so its draw order can be compared with upstream.
/// </summary>
internal static class SeededItemStatResolver
{
    private static readonly string[] SeedStatPrefixes =
    [
        "offensive", "defensive", "retaliation", "character", "skill",
        "conversion", "blockAbsorption", "blockRecovery"
    ];

    public static SeededItemStatResolution Resolve(
        GameRecord baseRecord,
        GameRecord? prefixRecord,
        GameRecord? suffixRecord,
        uint seed)
    {
        if (seed == 0)
            return SeededItemStatResolution.Fallback(["装备 Seed 为 0，无法重放实际浮动值"]);

        var result = ItemStatEngine.Compute(
            ToInputStats(baseRecord),
            seed,
            prefixStats: prefixRecord is null ? null : ToInputStats(prefixRecord),
            suffixStats: suffixRecord is null ? null : ToInputStats(suffixRecord));

        if (result.UnmodeledFields.Count > 0)
            return SeededItemStatResolution.Fallback(result.UnmodeledFields);

        var stats = new StatBlock();
        foreach (var record in Enumerate(baseRecord, prefixRecord, suffixRecord))
            stats.Add(GameStatTranslator.Translate([WithoutSeedControlledValues(record)]));

        var rolledValues = new StatBlock(result.Stats);
        // Chance-based modifier lines are represented by their expectation. Folding the
        // full rolled value into a permanent modifier overstates e.g. "20% chance of
        // +250% Fire Damage" by five times.
        foreach (var proc in result.ProcLines ?? [])
        {
            if (proc.Min is not { } minimum) continue;
            var chance = Math.Clamp(proc.Chance / 100d, 0, 1);
            rolledValues.Add(proc.Field, minimum * chance);
        }

        var rolledRecord = new GameRecord
        {
            RecordName = baseRecord.RecordName + "#seed",
            RecordType = baseRecord.RecordType,
            NumericValues = rolledValues,
            TextValues = MergeTextValues(baseRecord, prefixRecord, suffixRecord)
        };
        stats.Add(GameStatTranslator.Translate([rolledRecord]));

        // Legacy physical weapons store their fixed base damage in offensivePhysical.
        // The seed engine correctly consumes no random draw for it and therefore does
        // not emit it; map that fixed base range to weapon damage explicitly.
        var itemClass = baseRecord.Text("Class");
        if (itemClass.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase))
        {
            var baseStats = GameStatTranslator.Translate([baseRecord]);
            var legacyPhysical = baseStats[StatKeys.DamageFlat("physical")];
            if (Math.Abs(legacyPhysical) > 1e-9)
                stats.Add(StatKeys.WeaponDamage("physical"), legacyPhysical);
        }

        return SeededItemStatResolution.Exact(stats);
    }

    private static IEnumerable<ItemStatEngine.InputStat> ToInputStats(GameRecord record)
    {
        foreach (var (key, value) in record.NumericValues.Values)
        {
            // ArzArchiveReader adds these summaries for numeric arrays. They are
            // metadata for level-series consumers, not real DBR field names.
            if (key.EndsWith(".min", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".max", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new ItemStatEngine.InputStat(key, record.Text(key), value);
        }

        foreach (var (key, values) in record.TextValues)
        {
            if (record.NumericValues.Values.ContainsKey(key)) continue;
            var value = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
                yield return new ItemStatEngine.InputStat(key, value, 0);
        }
    }

    private static GameRecord WithoutSeedControlledValues(GameRecord record)
    {
        var fixedValues = new StatBlock(record.NumericValues.Values
            .Where(x => !SeedStatPrefixes.Any(prefix => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        return new GameRecord
        {
            RecordName = record.RecordName,
            RecordType = record.RecordType,
            NumericValues = fixedValues,
            NumericSeries = record.NumericSeries,
            TextValues = record.TextValues
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MergeTextValues(
        GameRecord baseRecord,
        GameRecord? prefixRecord,
        GameRecord? suffixRecord)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in Enumerate(baseRecord, prefixRecord, suffixRecord))
            foreach (var (key, values) in record.TextValues)
                if (!result.ContainsKey(key)) result[key] = values;
        return result;
    }

    private static IEnumerable<GameRecord> Enumerate(
        GameRecord baseRecord,
        GameRecord? prefixRecord,
        GameRecord? suffixRecord)
    {
        yield return baseRecord;
        if (prefixRecord is not null) yield return prefixRecord;
        if (suffixRecord is not null) yield return suffixRecord;
    }
}

internal sealed record SeededItemStatResolution(
    StatBlock Stats,
    bool IsExact,
    IReadOnlyList<string> UnmodeledFields)
{
    public static SeededItemStatResolution Exact(StatBlock stats) => new(stats, true, []);
    public static SeededItemStatResolution Fallback(IReadOnlyList<string> fields) => new(new StatBlock(), false, fields);
}
