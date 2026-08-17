using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public static class StatKeys
{
    public static readonly string[] DamageTypes =
    [
        "physical", "pierce", "fire", "cold", "lightning", "acid", "vitality", "aether", "chaos"
    ];

    public static string Resistance(string damageType) => $"{damageType}Resistance";
    public static string MaxResistance(string damageType) => $"{damageType}MaxResistance";
    public static string DamageFlat(string damageType) => $"damage.{damageType}.flat";
    public static string DamagePercent(string damageType) => $"damage.{damageType}.percent";
    public static string DamageDot(string damageType) => $"damage.{damageType}.dot";
    public static string DamageDotPercent(string damageType) => $"damage.{damageType}.dotPercent";
    public static string WeaponDamage(string damageType) => $"weaponDamage.{damageType}";
}

public sealed class CharacterStatEngine : ICharacterStatEngine
{
    public CalculatedLoadout Calculate(
        CharacterSnapshot character,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog)
    {
        var stats = character.BaselineStats.Clone();
        var effects = new List<TriggeredEffect>();
        var unsupported = new List<UnsupportedEffect>();
        var errors = new List<string>();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var setCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (targetSlot, variant) in slots)
        {
            if (!IsCompatible(targetSlot, variant.Item.Slot, character.CanDualWield) ||
                targetSlot == EquipmentSlot.OffHand && variant.Item.IsTwoHanded)
                errors.Add($"{variant.Item.Name} 不能装备在 {targetSlot}");

            if (!itemIds.Add(variant.Item.Id))
                errors.Add($"同一实物被重复装备：{variant.Item.Name}");

            stats.Add(variant.CombinedStats);
            effects.AddRange(variant.Effects);
            unsupported.AddRange(variant.UnsupportedEffects);

            if (!string.IsNullOrWhiteSpace(variant.Item.SetId))
                setCounts[variant.Item.SetId] = setCounts.GetValueOrDefault(variant.Item.SetId) + 1;
        }

        foreach (var (setId, count) in setCounts)
        {
            if (!catalog.SetBonuses.TryGetValue(setId, out var bonuses)) continue;
            foreach (var (requiredPieces, bonus) in bonuses)
                if (count >= requiredPieces) stats.Add(bonus);
        }

        ApplyPercentModifier(stats, "physique");
        ApplyPercentModifier(stats, "cunning");
        ApplyPercentModifier(stats, "spirit");
        ApplyPercentModifier(stats, "health");
        ApplyPercentModifier(stats, "energy");
        if (stats["usePlayerAbilityFormula"] > 0)
            ApplyPlayerAbilityFormula(stats);
        else
        {
            ApplyPercentModifier(stats, "offensiveAbility");
            ApplyPercentModifier(stats, "defensiveAbility");
        }

        ResolveEquippedWeaponDamage(stats, slots);

        // Character attributes contribute directly to damage bonuses. The ARZ
        // item/skill modifiers intentionally do not include these sheet bonuses.
        var sheetCunning = stats["cunning"];
        var sheetSpirit = stats["spirit"];
        stats.Add(StatKeys.DamagePercent("physical"), sheetCunning * 0.41);
        stats.Add(StatKeys.DamagePercent("pierce"), sheetCunning * 0.41);
        foreach (var damageType in new[] { "fire", "cold", "lightning", "acid", "vitality", "aether", "chaos" })
            stats.Add(StatKeys.DamagePercent(damageType), sheetSpirit * 0.47);

        var finalPhysique = stats["physique"] > 0 ? stats["physique"] : character.Physique;
        var finalCunning = stats["cunning"] > 0 ? stats["cunning"] : character.Cunning;
        var finalSpirit = stats["spirit"] > 0 ? stats["spirit"] : character.Spirit;

        foreach (var variant in slots.Values)
        {
            var item = variant.Item;
            if (item.LevelRequirement > character.Level)
                errors.Add($"{item.Name} 需要等级 {item.LevelRequirement}");
            if (item.PhysiqueRequirement > finalPhysique)
                errors.Add($"{item.Name} 需要体格 {item.PhysiqueRequirement}");
            if (item.CunningRequirement > finalCunning)
                errors.Add($"{item.Name} 需要灵巧 {item.CunningRequirement}");
            if (item.SpiritRequirement > finalSpirit)
                errors.Add($"{item.Name} 需要精神 {item.SpiritRequirement}");
        }

        if (slots.TryGetValue(EquipmentSlot.MainHand, out var mainHand) &&
            mainHand.Item.IsTwoHanded && slots.ContainsKey(EquipmentSlot.OffHand))
            errors.Add("双手武器不能与副手同时装备");

        return new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(slots),
            Stats = stats,
            Effects = effects,
            UnsupportedEffects = unsupported.Distinct().ToArray(),
            ValidationErrors = errors.Distinct().ToArray()
        };
    }

    public static bool IsCompatible(EquipmentSlot target, EquipmentSlot itemSlot, bool canDualWield = false) => target switch
    {
        EquipmentSlot.RingLeft or EquipmentSlot.RingRight => itemSlot == EquipmentSlot.Ring,
        EquipmentSlot.OffHand => itemSlot == EquipmentSlot.OffHand || canDualWield && itemSlot == EquipmentSlot.MainHand,
        _ => target == itemSlot
    };

    private static void ResolveEquippedWeaponDamage(
        StatBlock stats,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots)
    {
        slots.TryGetValue(EquipmentSlot.MainHand, out var mainHand);
        slots.TryGetValue(EquipmentSlot.OffHand, out var offHand);
        var offHandIsWeapon = offHand?.Item.Slot == EquipmentSlot.MainHand;
        var mainBaseSpeed = mainHand?.CombinedStats["weaponBaseAttackSpeed"] ?? 0;
        var offBaseSpeed = offHandIsWeapon ? offHand!.CombinedStats["weaponBaseAttackSpeed"] : 0;
        var weaponBaseSpeed = offHandIsWeapon ? (mainBaseSpeed + offBaseSpeed) / 2d : mainBaseSpeed;
        var internalBaseSpeed = ResolveInternalWeaponSpeed(mainHand?.Item, offHandIsWeapon);
        stats.Set("weaponBaseAttackSpeed", weaponBaseSpeed);
        stats.Set("weaponAttackSpeedFactor", Math.Max(0.2, internalBaseSpeed + weaponBaseSpeed));
        stats.Set("averageWeaponStrikesPerAction", offHandIsWeapon ? 1.5 : 1);
        stats.Set("dualWieldWeapon", offHandIsWeapon ? 1 : 0);
        foreach (var damageType in StatKeys.DamageTypes)
        {
            var key = StatKeys.WeaponDamage(damageType);
            var mainDamage = mainHand?.CombinedStats[key] ?? 0;
            var offDamage = offHandIsWeapon ? offHand!.CombinedStats[key] : 0;
            var nonWeaponContribution = stats[key] - mainDamage - offDamage;
            var equippedDamage = offHandIsWeapon ? (mainDamage + offDamage) / 2d : mainDamage;
            stats.Set(key, nonWeaponContribution + equippedDamage);
        }
    }

    private static double ResolveInternalWeaponSpeed(ItemInstance? mainHand, bool dualWield)
    {
        if (mainHand is null) return 1;
        var ranged = mainHand.AttachmentQualifier.Contains("ranged", StringComparison.OrdinalIgnoreCase);
        if (dualWield) return ranged ? 0.87 : 1;
        return ranged || mainHand.IsTwoHanded ? 0.92 : 1;
    }

    private static void ApplyPercentModifier(StatBlock stats, string key)
    {
        var percent = stats[$"{key}.percent"];
        if (Math.Abs(percent) > 1e-9)
            stats.Set(key, stats[key] * Math.Max(0, 1 + percent / 100d));
    }

    private static void ApplyPlayerAbilityFormula(StatBlock stats)
    {
        // records/game/combatformulas.dbr:
        // (flat + level*12 + attribute*0.5) * (1 + modifier/100) + 53
        var levelTerm = Math.Max(1, stats["characterLevel"]) * 12;
        var oa = (stats["offensiveAbility"] + levelTerm + stats["cunning"] * 0.5) *
                 Math.Max(0, 1 + stats["offensiveAbility.percent"] / 100d) + 53;
        var da = (stats["defensiveAbility"] + levelTerm + stats["physique"] * 0.5) *
                 Math.Max(0, 1 + stats["defensiveAbility.percent"] / 100d) + 53;
        stats.Set("offensiveAbility", oa);
        stats.Set("defensiveAbility", da);
    }
}
