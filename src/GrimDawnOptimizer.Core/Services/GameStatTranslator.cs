using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal static class GameStatTranslator
{
    private static readonly Dictionary<string, string> Simple = new(StringComparer.OrdinalIgnoreCase)
    {
        ["characterLife"] = "health",
        ["characterMana"] = "energy",
        ["characterLifeRegen"] = "healthRegen",
        ["characterLifeRegenModifier"] = "healthRegen.percent",
        ["characterManaRegen"] = "energyRegen",
        ["characterManaRegenModifier"] = "energyRegen.percent",
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
        ["characterLifeMultModifier"] = "health.percent",
        ["characterManaModifier"] = "energy.percent",
        ["characterOffensiveAbilityModifier"] = "offensiveAbility.percent",
        ["characterAttackSpeed"] = "attackSpeed",
        ["characterAttackSpeedModifier"] = "attackSpeed",
        ["characterAttackSpeedMaxModifier"] = "attackSpeed.maxBonus",
        ["characterBaseAttackSpeed"] = "weaponBaseAttackSpeed",
        ["characterSpellCastSpeed"] = "castSpeed",
        ["characterSpellCastSpeedModifier"] = "castSpeed",
        ["characterSpellCastSpeedMaxModifier"] = "castSpeed.maxBonus",
        ["defensiveProtection"] = "armor",
        ["defensiveBonusProtection"] = "armor",
        ["defensiveProtectionModifier"] = "armor.percent",
        ["defensiveAbsorption"] = "armorAbsorption",
        ["defensiveAbsorptionModifier"] = "armorAbsorption",
        ["defensiveBlock"] = "blockAmount",
        ["defensiveBlockChance"] = "blockChance",
        ["defensiveBlockAmount"] = "blockAmount",
        ["defensiveBlockAmountModifier"] = "blockAmount.percent",
        ["defensiveBlockModifier"] = "blockChance.percent",
        ["blockAbsorption"] = "blockAbsorption",
        ["blockRecoveryTime"] = "blockRecoverySeconds",
        ["characterDefensiveBlockRecoveryReduction"] = "blockRecoveryReduction",
        ["characterDodgePercent"] = "dodgeChance",
        ["characterDeflectProjectile"] = "projectileAvoidance",
        ["offensiveLifeLeechMin"] = "lifeSteal",
        ["offensiveCritDamage"] = "criticalDamage",
        ["offensiveCritDamageModifier"] = "criticalDamage",
        ["offensiveCriticalModifier"] = "criticalDamage",
        ["offensiveDamageMultModifier"] = "damage.total.multiplierPercent",
        ["skillCooldownReduction"] = "cooldownReduction",
        ["skillCooldownReductionModifier"] = "cooldownReduction",
        ["skillManaCostReduction"] = "energyCostReduction",
        ["skillManaCostReductionModifier"] = "energyCostReduction",
        ["augmentAllLevel"] = "allSkills"
    };

    private static readonly Dictionary<string, string> DamageTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Physical"] = "physical", ["Pierce"] = "pierce", ["Fire"] = "fire", ["Cold"] = "cold",
        ["Lightning"] = "lightning", ["Acid"] = "acid", ["Poison"] = "acid", ["Life"] = "vitality", ["Aether"] = "aether",
        ["Chaos"] = "chaos", ["Bleeding"] = "bleeding"
    };

    private static readonly HashSet<string> NonScoringFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "characterRunSpeed", "characterRunSpeedModifier", "characterRunSpeedMaxModifier",
        "characterIncreasedExperience", "characterIncreasedGold", "characterLightRadius",
        "characterConstitution", "characterConstitutionModifier", "characterLevelReqReduction",
        "characterModifierPoints", "offensivePierceRatioMin", "offensivePierceRatioMax",
        "skillCooldownTime", "skillManaCost", "skillActiveManaCost", "skillChance",
        "skillChanceWeight", "skillChargeLevel", "skillTargetNumber", "skillTargetRadius",
        "skillMaxLevel", "skillMasteryLevelRequired", "skillUltimateLevel", "skillTier",
        "skillExperienceLevels", "skillLocked", "skillSet", "skillAllowsWarmUp",
        "skillWeaponTintRed", "skillWeaponTintGreen", "skillWeaponTintBlue",
        "characterRunSpeedJitter"
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
            // ArzArchiveReader stores these two keys only as summaries of a DBR
            // numeric array. They are not independent stats and must never be added
            // on top of the array's selected/average value.
            if (rawKey.EndsWith(".min", StringComparison.OrdinalIgnoreCase) ||
                rawKey.EndsWith(".max", StringComparison.OrdinalIgnoreCase)) continue;
            var key = rawKey;
            if (Simple.TryGetValue(key, out var canonical))
            {
                var chance = ChanceFactor(record, ChanceKeyFor(key));
                result.Add(canonical, rawValue * chance);
            }

            foreach (var (gameType, type) in DamageTypeMap)
            {
                if (key.Equals($"offensive{gameType}Modifier", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.DamagePercent(type), rawValue * ChanceFactor(record, key + "Chance"));
                else if (key.Equals($"defensive{gameType}", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals($"defensive{gameType}Resistance", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.Resistance(type), rawValue * ChanceFactor(record, key + "Chance"));
                else if (key.Equals($"defensive{gameType}MaxResist", StringComparison.OrdinalIgnoreCase))
                    result.Add(StatKeys.MaxResistance(type), rawValue);
            }
        }

        foreach (var (gameType, type) in DamageTypeMap)
        {
            var chance = ChanceFactor(record, $"offensive{gameType}Chance");
            result.Add(StatKeys.DamageFlat(type), Average(record, $"offensive{gameType}") * chance);
            if (gameType.Equals("Physical", StringComparison.OrdinalIgnoreCase))
                result.Add(StatKeys.DamageFlat(type), Average(record, "offensiveBonusPhysical") *
                    ChanceFactor(record, "offensiveBonusPhysicalChance"));
            result.Add(StatKeys.WeaponDamage(type), Average(record, $"offensiveBase{gameType}") * chance);

            var dotChance = ChanceFactor(record, $"offensiveSlow{gameType}Chance");
            var dotPerTick = Average(record, $"offensiveSlow{gameType}") * dotChance;
            var dotDuration = Math.Max(1, Average(record, $"offensiveSlow{gameType}Duration"));
            // offensiveSlow* is the per-tick value; DurationMin is only used by the
            // game to display the total "over N seconds" amount.
            result.Add(StatKeys.DamageDot(type), dotPerTick);
            result.Add($"damage.{type}.dotTotal", dotPerTick * dotDuration);
            result.Add(StatKeys.DamageDotPercent(type),
                record.NumericValues[$"offensiveSlow{gameType}Modifier"] *
                ChanceFactor(record, $"offensiveSlow{gameType}ModifierChance"));

            var retaliationChance = RetaliationChanceFactor(record, $"retaliation{gameType}");
            var retaliation = Average(record, $"retaliation{gameType}") * retaliationChance;
            var retaliationModifier = record.NumericValues[$"retaliation{gameType}Modifier"] *
                                      RetaliationChanceFactor(record, $"retaliation{gameType}Modifier");
            result.Add(StatKeys.RetaliationFlat(type), retaliation);
            result.Add(StatKeys.RetaliationPercent(type), retaliationModifier);
            result.Add("retaliation.flatProxy", retaliation);
            result.Add("retaliation.percentProxy", retaliationModifier);

            var retaliationDotChance = RetaliationChanceFactor(record, $"retaliationSlow{gameType}");
            var retaliationDot = Average(record, $"retaliationSlow{gameType}") * retaliationDotChance;
            var retaliationDotDuration = Math.Max(1, Average(record, $"retaliationSlow{gameType}Duration"));
            result.Add(StatKeys.RetaliationDot(type), retaliationDot);
            result.Add(StatKeys.RetaliationDotTotal(type), retaliationDot * retaliationDotDuration);
            var retaliationDotModifier = record.NumericValues[$"retaliationSlow{gameType}Modifier"] *
                                         RetaliationChanceFactor(record, $"retaliationSlow{gameType}Modifier");
            result.Add(StatKeys.RetaliationDotPercent(type), retaliationDotModifier);
            result.Add("retaliation.flatProxy", retaliationDot * retaliationDotDuration);
            result.Add("retaliation.percentProxy", retaliationDotModifier);
        }

        var elementalMinimum = record.NumericValues["offensiveElementalMin"];
        var elementalMaximum = record.NumericValues["offensiveElementalMax"];
        var elementalAverage = Math.Abs(elementalMinimum) > 1e-9 && Math.Abs(elementalMaximum) > 1e-9
            ? (elementalMinimum + elementalMaximum) / 2d
            : elementalMinimum + elementalMaximum;
        elementalAverage *= ChanceFactor(record, "offensiveElementalChance");
        var elementalModifier = record.NumericValues["offensiveElementalModifier"] *
                                ChanceFactor(record, "offensiveElementalModifierChance");
        var elementalResistance = record.NumericValues["defensiveElementalResistance"];
        var elementalResistanceChance = ChanceFactor(record, "defensiveElementalResistanceChance");
        foreach (var type in new[] { "fire", "cold", "lightning" })
        {
            result.Add(StatKeys.DamageFlat(type), elementalAverage / 3d);
            result.Add(StatKeys.DamagePercent(type), elementalModifier);
            result.Add(StatKeys.Resistance(type), elementalResistance * elementalResistanceChance);
        }

        var retaliationElemental = Average(record, "retaliationElemental") *
                                   RetaliationChanceFactor(record, "retaliationElemental");
        var retaliationElementalModifier = record.NumericValues["retaliationElementalModifier"] *
                                           RetaliationChanceFactor(record, "retaliationElementalModifier");
        foreach (var type in new[] { "fire", "cold", "lightning" })
        {
            result.Add(StatKeys.RetaliationFlat(type), retaliationElemental / 3d);
            result.Add(StatKeys.RetaliationPercent(type), retaliationElementalModifier);
        }
        result.Add("retaliation.flatProxy", retaliationElemental);
        result.Add("retaliation.percentProxy", retaliationElementalModifier);

        var totalDamage = record.NumericValues["offensiveTotalDamageModifier"] *
                          ChanceFactor(record, "offensiveTotalDamageModifierChance");
        result.Add("damage.total.percent", totalDamage);
        var retaliationTotalModifier = record.NumericValues["retaliationTotalDamageModifier"] *
                                       RetaliationChanceFactor(record, "retaliationTotalDamageModifier");
        var retaliationDamageMultiplier = record.NumericValues["retaliationDamageMultModifier"] *
                                          RetaliationChanceFactor(record, "retaliationDamageMultModifier");
        result.Add("retaliation.total.percent", retaliationTotalModifier);
        result.Add("retaliation.total.multiplierPercent", retaliationDamageMultiplier);
        result.Add("retaliation.percentProxy", retaliationTotalModifier + retaliationDamageMultiplier);

        var totalSpeed = record.NumericValues["characterTotalSpeedModifier"] *
                         ChanceFactor(record, "characterTotalSpeedModifierChance");
        result.Add("attackSpeed", totalSpeed);
        result.Add("castSpeed", totalSpeed);

        var allResistance = record.NumericValues["defensiveAllResistance"] *
                            ChanceFactor(record, "defensiveAllResistanceChance");
        if (Math.Abs(allResistance) > 1e-9)
            foreach (var type in StatKeys.DamageTypes) result.Add(StatKeys.Resistance(type), allResistance);

        AddResistanceReduction(record, result, "Total", StatKeys.DamageTypes);
        AddResistanceReduction(record, result, "Physical", ["physical"]);
        AddResistanceReduction(record, result, "Elemental", ["fire", "cold", "lightning"]);

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

    internal static double LegacyPhysicalWeaponDamage(GameRecord record) => Average(record, "offensivePhysical");

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

    private static double RetaliationChanceFactor(GameRecord record, string fieldPrefix) =>
        ChanceFactor(record, fieldPrefix + "Chance") * ChanceFactor(record, "retaliationGlobalChance");

    private static string ChanceKeyFor(string key) =>
        key.EndsWith("Min", StringComparison.OrdinalIgnoreCase) ? key[..^3] + "Chance" : key + "Chance";

    private static void AddConversion(GameRecord record, StatBlock result, string suffix)
    {
        var input = NormalizeDamageType(record.Text("conversionInType" + suffix));
        var output = NormalizeDamageType(record.Text("conversionOutType" + suffix));
        var percentage = record.NumericValues["conversionPercentage" + suffix];
        if (!string.IsNullOrWhiteSpace(input) && !string.IsNullOrWhiteSpace(output) && percentage > 0)
            result.Add($"conversion.{input}.{output}", percentage);
    }

    private static void AddResistanceReduction(
        GameRecord record,
        StatBlock result,
        string gameType,
        IEnumerable<string> damageTypes)
    {
        var flatKey = $"offensive{gameType}ResistanceReductionAbsolute";
        var percentKey = $"offensive{gameType}ResistanceReductionPercent";
        var flat = Average(record, flatKey) * ChanceFactor(record, flatKey + "Chance");
        var percent = Average(record, percentKey) * ChanceFactor(record, percentKey + "Chance");
        foreach (var type in damageTypes)
        {
            result.Add($"rr.{type}.flat", flat);
            result.Add($"rr.{type}.percent", percent);
        }
    }

    public static IReadOnlyList<string> FindUnsupportedCombatFields(GameRecord record)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in record.NumericValues.Values)
        {
            if (Math.Abs(rawValue) < 1e-9 || rawKey.EndsWith(".min", StringComparison.OrdinalIgnoreCase) ||
                rawKey.EndsWith(".max", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsCombatField(rawKey) || IsHandledCombatField(rawKey) || NonScoringFields.Contains(rawKey)) continue;
            result.Add(rawKey);
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsCombatField(string key) =>
        key.StartsWith("character", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("offensive", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("defensive", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("retaliation", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("skill", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("conversion", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("blockAbsorption", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("blockRecovery", StringComparison.OrdinalIgnoreCase);

    private static bool IsHandledCombatField(string key)
    {
        if (Simple.ContainsKey(key) || key is "characterTotalSpeedModifier" or "defensiveAllResistance" or
            "defensiveAllMaxResist" or "offensiveTotalDamageModifier" or
            "offensiveTotalDamageModifierChance" or "defensiveElementalResistance" or
            "defensiveElementalResistanceChance" or "offensiveBonusPhysicalMin" or
            "offensiveBonusPhysicalMax" or "offensiveBonusPhysicalChance") return true;
        if (Simple.Keys.Any(simple => ChanceKeyFor(simple).Equals(key, StringComparison.OrdinalIgnoreCase))) return true;
        if (key is "characterTotalSpeedModifierChance" or "defensiveAllResistanceChance") return true;
        if (key.StartsWith("conversion", StringComparison.OrdinalIgnoreCase)) return true;
        if (key.Equals("retaliationGlobalChance", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("retaliationTotalDamageModifier", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("retaliationTotalDamageModifierChance", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("retaliationDamageMultModifier", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("retaliationDamageMultModifierChance", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var gameType in new[] { "Total", "Physical", "Elemental" })
        foreach (var reductionType in new[] { "Absolute", "Percent" })
        {
            var prefix = $"offensive{gameType}ResistanceReduction{reductionType}";
            if (key.Equals(prefix + "Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals(prefix + "Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals(prefix + "Chance", StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var gameType in DamageTypeMap.Keys.Append("Elemental"))
        {
            if (key.Equals($"offensive{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensive{gameType}Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensive{gameType}Chance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensive{gameType}Modifier", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensive{gameType}ModifierChance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveBase{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveBase{gameType}Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}Chance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}DurationMin", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}DurationMax", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}Modifier", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"offensiveSlow{gameType}ModifierChance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"defensive{gameType}", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"defensive{gameType}Chance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"defensive{gameType}Resistance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"defensive{gameType}ResistanceChance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"defensive{gameType}MaxResist", StringComparison.OrdinalIgnoreCase)) return true;

            if (key.Equals($"retaliation{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliation{gameType}Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliation{gameType}Chance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliation{gameType}Modifier", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliation{gameType}ModifierChance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}Min", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}Max", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}Chance", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}DurationMin", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}DurationMax", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}Modifier", StringComparison.OrdinalIgnoreCase) ||
                key.Equals($"retaliationSlow{gameType}ModifierChance", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
