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
        ["characterDefensiveAbilityModifier"] = "defensiveAbility.percent",
        ["characterStrength"] = "physique",
        ["characterStrengthModifier"] = "physique.percent",
        ["characterDexterity"] = "cunning",
        ["characterDexterityModifier"] = "cunning.percent",
        ["characterIntelligence"] = "spirit",
        ["characterIntelligenceModifier"] = "spirit.percent",
        ["characterGlobalReqReduction"] = "requirementReduction.global",
        ["characterArmorStrengthReqReduction"] = "requirementReduction.armorPhysique",
        ["characterHuntingDexterityReqReduction"] = "requirementReduction.rangedCunning",
        ["characterJewelryIntelligenceReqReduction"] = "requirementReduction.jewelrySpirit",
        ["characterMeleeStrengthReqReduction"] = "requirementReduction.meleePhysique",
        ["characterMeleeDexterityReqReduction"] = "requirementReduction.meleeCunning",
        ["characterMeleeIntelligenceReqReduction"] = "requirementReduction.meleeSpirit",
        ["characterShieldStrengthReqReduction"] = "requirementReduction.shieldPhysique",
        ["characterWeaponStrengthReqReduction"] = "requirementReduction.weaponPhysique",
        ["characterWeaponIntelligenceReqReduction"] = "requirementReduction.weaponSpirit",
        ["characterLifeModifier"] = "health.percent",
        ["characterManaModifier"] = "energy.percent",
        ["characterOffensiveAbilityModifier"] = "offensiveAbility.percent",
        ["characterAttackSpeed"] = "attackSpeed",
        ["characterAttackSpeedModifier"] = "attackSpeed",
        ["characterBaseAttackSpeed"] = "weaponBaseAttackSpeed",
        ["characterSpellCastSpeed"] = "castSpeed",
        ["characterSpellCastSpeedModifier"] = "castSpeed",
        ["defensiveProtection"] = "armor",
        ["defensiveAbsorption"] = "armorAbsorption",
        ["defensiveBlockChance"] = "blockChance",
        ["defensiveBlockAmount"] = "blockAmount",
        ["characterDodgePercent"] = "dodgeChance",
        ["characterDeflectProjectile"] = "projectileAvoidance",
        ["offensiveLifeLeechMin"] = "lifeSteal",
        ["offensiveCritDamage"] = "criticalDamage",
        ["offensiveCritDamageModifier"] = "criticalDamage",
        ["offensiveCriticalModifier"] = "criticalDamage",
        ["augmentAllLevel"] = "allSkills"
    };

    private static readonly Dictionary<string, string> DamageTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Physical"] = "physical", ["Pierce"] = "pierce", ["Fire"] = "fire", ["Cold"] = "cold",
        ["Lightning"] = "lightning", ["Acid"] = "acid", ["Poison"] = "acid", ["Life"] = "vitality", ["Aether"] = "aether",
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

    public static double NumericAtLevel(GameRecord record, string key, int level)
    {
        if (record.NumericSeries.TryGetValue(key, out var values) && values.Count > 0)
            return values[Math.Clamp(level - 1, 0, values.Count - 1)];
        return record.NumericValues[key];
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
                if (key.Equals($"offensive{gameType}Modifier", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.DamagePercent(type), rawValue);
                else if (key.Equals($"defensive{gameType}", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals($"defensive{gameType}Resistance", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.Resistance(type), rawValue);
                else if (key.Equals($"defensive{gameType}MaxResist", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.MaxResistance(type), rawValue);
            }
        }

        foreach (var (gameType, type) in DamageTypeMap)
        {
            var chance = ChanceFactor(record, $"offensive{gameType}Chance");
            result.Add(StatKeys.DamageFlat(type), Average(record, $"offensive{gameType}") * chance);
            result.Add(StatKeys.WeaponDamage(type), Average(record, $"offensiveBase{gameType}") * chance);

            var duration = Average(record, $"offensiveSlow{gameType}Duration");
            var dotChance = ChanceFactor(record, $"offensiveSlow{gameType}Chance");
            result.Add(StatKeys.DamageDot(type), Average(record, $"offensiveSlow{gameType}") * dotChance / Math.Max(1, duration));
            result.Add(StatKeys.DamageDotPercent(type), record.NumericValues[$"offensiveSlow{gameType}Modifier"]);
        }

        var elementalMinimum = record.NumericValues["offensiveElementalMin"];
        var elementalMaximum = record.NumericValues["offensiveElementalMax"];
        var elementalAverage = Math.Abs(elementalMinimum) > 1e-9 && Math.Abs(elementalMaximum) > 1e-9
            ? (elementalMinimum + elementalMaximum) / 2d
            : elementalMinimum + elementalMaximum;
        elementalAverage *= ChanceFactor(record, "offensiveElementalChance");
        var elementalModifier = record.NumericValues["offensiveElementalModifier"];
        var elementalResistance = record.NumericValues["defensiveElementalResistance"];
        foreach (var type in new[] { "fire", "cold", "lightning" })
        {
            result.Add(StatKeys.DamageFlat(type), elementalAverage / 3d);
            result.Add(StatKeys.DamagePercent(type), elementalModifier);
            result.Add(StatKeys.Resistance(type), elementalResistance);
        }

        if (record.NumericValues["defensiveAllMaxResist"] is var allMax && Math.Abs(allMax) > 1e-9)
            foreach (var type in StatKeys.DamageTypes) result.Add(StatKeys.MaxResistance(type), allMax);

        AddConversion(record, result, string.Empty);
        AddConversion(record, result, "2");

        for (var index = 1; index <= 32; index++)
        {
            var skillName = record.Text($"augmentSkillName{index}");
            if (!string.IsNullOrWhiteSpace(skillName))
                result.Add($"skillLevel.{skillName}", record.NumericValues[$"augmentSkillLevel{index}"]);
            var masteryName = record.Text($"augmentMasteryName{index}");
            if (!string.IsNullOrWhiteSpace(masteryName))
                result.Add($"masteryLevel.{masteryName}", record.NumericValues[$"augmentMasteryLevel{index}"]);
            var modifiedSkillName = record.Text($"modifiedSkillName{index}");
            if (!string.IsNullOrWhiteSpace(modifiedSkillName))
                result.Set($"skillModifier.{modifiedSkillName}", 1);
        }
    }

    private static double Average(GameRecord record, string prefix)
    {
        var minimum = record.NumericValues[prefix + "Min"];
        var maximum = record.NumericValues[prefix + "Max"];
        return Math.Abs(minimum) > 1e-9 && Math.Abs(maximum) > 1e-9
            ? (minimum + maximum) / 2d
            : minimum + maximum;
    }

    private static double ChanceFactor(GameRecord record, string key)
    {
        var chance = record.NumericValues[key];
        return chance > 0 ? Math.Clamp(chance / 100d, 0, 1) : 1;
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

    public static string ResolveAttachmentQualifier(GameRecord record)
    {
        var @class = record.Text("Class");
        if (string.IsNullOrWhiteSpace(@class)) @class = record.RecordType;
        var lower = @class.ToLowerInvariant();
        if (lower.Contains("head")) return "head";
        if (lower.Contains("shoulder")) return "shoulders";
        if (lower.Contains("hands")) return "hands";
        if (lower.Contains("chest")) return "chest";
        if (lower.Contains("waist")) return "waist";
        if (lower.Contains("legs")) return "legs";
        if (lower.Contains("feet")) return "feet";
        if (lower.Contains("amulet")) return "amulet";
        if (lower.Contains("medal")) return "medal";
        if (lower.Contains("ring")) return "ring";
        if (lower.Contains("shield")) return "shield";
        if (lower.Contains("offhand")) return "offhand";
        if (lower.Contains("ranged2h")) return "ranged2h";
        if (lower.Contains("ranged1h")) return "ranged1h";
        if (lower.Contains("spear2h")) return "spear2h";
        if (lower.Contains("axe2h")) return "axe2h";
        if (lower.Contains("mace2h") || lower.Contains("blunt2h")) return "mace2h";
        if (lower.Contains("sword2h")) return "sword2h";
        if (lower.Contains("spear")) return "spear";
        if (lower.Contains("staff")) return "staff";
        if (lower.Contains("axe")) return "axe";
        if (lower.Contains("mace") || lower.Contains("blunt")) return "mace";
        if (lower.Contains("sword")) return "sword";
        if (lower.Contains("dagger")) return "dagger";
        if (lower.Contains("scepter")) return "scepter";
        return string.Empty;
    }
}
