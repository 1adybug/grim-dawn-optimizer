using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal static class GameStatTranslator
{
    private static readonly Dictionary<string, string> Simple = new(StringComparer.OrdinalIgnoreCase)
    {
        ["characterLife"] = "health",
        ["characterMana"] = "energy",
        ["characterLifeRegen"] = "healthRegen",
        ["characterManaRegen"] = "energyRegen",
        ["characterOffensiveAbility"] = "offensiveAbility",
        ["characterDefensiveAbility"] = "defensiveAbility",
        ["characterStrength"] = "physique",
        ["characterDexterity"] = "cunning",
        ["characterIntelligence"] = "spirit",
        ["characterAttackSpeed"] = "attackSpeed",
        ["characterAttackSpeedModifier"] = "attackSpeed",
        ["characterSpellCastSpeed"] = "castSpeed",
        ["characterSpellCastSpeedModifier"] = "castSpeed",
        ["defensiveProtection"] = "armor",
        ["defensiveAbsorption"] = "armorAbsorption",
        ["defensiveBlockChance"] = "blockChance",
        ["defensiveBlockAmount"] = "blockAmount",
        ["characterDodgePercent"] = "dodgeChance",
        ["characterDeflectProjectile"] = "projectileAvoidance",
        ["offensiveLifeLeechMin"] = "lifeSteal",
        ["augmentAllLevel"] = "allSkills"
    };

    private static readonly Dictionary<string, string> DamageTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Physical"] = "physical", ["Pierce"] = "pierce", ["Fire"] = "fire", ["Cold"] = "cold",
        ["Lightning"] = "lightning", ["Poison"] = "acid", ["Life"] = "vitality", ["Aether"] = "aether",
        ["Chaos"] = "chaos"
    };

    public static StatBlock Translate(IEnumerable<GameRecord> records)
    {
        var result = new StatBlock();
        foreach (var record in records) TranslateInto(record, result);
        return result;
    }

    public static StatBlock TranslateAtLevel(GameRecord record, int level)
    {
        if (record.NumericSeries.Count == 0) return Translate([record]);
        var selected = new StatBlock();
        foreach (var (key, values) in record.NumericSeries)
        {
            if (values.Count == 0) continue;
            selected.Set(key, values[Math.Clamp(level - 1, 0, values.Count - 1)]);
        }
        var leveled = new GameRecord
        {
            RecordName = record.RecordName,
            RecordType = record.RecordType,
            NumericValues = selected,
            NumericSeries = record.NumericSeries,
            TextValues = record.TextValues
        };
        return Translate([leveled]);
    }

    private static void TranslateInto(GameRecord record, StatBlock result)
    {
        foreach (var (rawKey, rawValue) in record.NumericValues.Values)
        {
            var key = rawKey.EndsWith(".min", StringComparison.OrdinalIgnoreCase) || rawKey.EndsWith(".max", StringComparison.OrdinalIgnoreCase)
                ? rawKey[..^4]
                : rawKey;
            if (Simple.TryGetValue(key, out var canonical)) result.Add(canonical, rawValue);

            foreach (var (gameType, type) in DamageTypeMap)
            {
                if (key.Equals($"offensive{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals($"offensive{gameType}Max", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.DamageFlat(type), rawValue / 2d);
                else if (key.Equals($"offensive{gameType}Modifier", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.DamagePercent(type), rawValue);
                else if (key.Equals($"defensive{gameType}", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals($"defensive{gameType}Resistance", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.Resistance(type), rawValue);
                else if (key.Equals($"defensive{gameType}MaxResist", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.MaxResistance(type), rawValue);
            }
        }

        if (record.NumericValues["defensiveAllMaxResist"] is var allMax && Math.Abs(allMax) > 1e-9)
            foreach (var type in StatKeys.DamageTypes) result.Add(StatKeys.MaxResistance(type), allMax);

        AddConversion(record, result, string.Empty);
        AddConversion(record, result, "2");
    }

    private static void AddConversion(GameRecord record, StatBlock result, string suffix)
    {
        var input = NormalizeDamageType(record.Text("conversionInType" + suffix));
        var output = NormalizeDamageType(record.Text("conversionOutType" + suffix));
        var percentage = record.NumericValues["conversionPercentage" + suffix];
        if (!string.IsNullOrWhiteSpace(input) && !string.IsNullOrWhiteSpace(output) && percentage > 0)
            result.Add($"conversion.{input}.{output}", percentage);
    }

    private static string NormalizeDamageType(string input)
    {
        foreach (var (game, canonical) in DamageTypeMap)
            if (input.Contains(game, StringComparison.OrdinalIgnoreCase)) return canonical;
        return string.Empty;
    }

    public static EquipmentSlot? ResolveSlot(GameRecord record)
    {
        var @class = record.Text("Class");
        if (string.IsNullOrWhiteSpace(@class)) @class = record.RecordType;
        return @class switch
        {
            "ArmorProtective_Head" => EquipmentSlot.Head,
            "ArmorProtective_Hands" => EquipmentSlot.Hands,
            "ArmorProtective_Feet" => EquipmentSlot.Feet,
            "ArmorProtective_Legs" => EquipmentSlot.Legs,
            "ArmorProtective_Chest" => EquipmentSlot.Chest,
            "ArmorProtective_Waist" => EquipmentSlot.Waist,
            "ArmorProtective_Shoulders" => EquipmentSlot.Shoulders,
            "ArmorJewelry_Medal" => EquipmentSlot.Medal,
            "ArmorJewelry_Ring" => EquipmentSlot.Ring,
            "ArmorJewelry_Amulet" => EquipmentSlot.Amulet,
            "ItemArtifact" => EquipmentSlot.Relic,
            "WeaponArmor_Offhand" or "WeaponArmor_Shield" => EquipmentSlot.OffHand,
            _ when @class.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) => EquipmentSlot.MainHand,
            _ => null
        };
    }

    public static bool IsTwoHanded(GameRecord record)
    {
        var @class = record.Text("Class");
        return @class.Contains("2h", StringComparison.OrdinalIgnoreCase) ||
               record.NumericValues["usesTwoHands"] > 0;
    }
}
