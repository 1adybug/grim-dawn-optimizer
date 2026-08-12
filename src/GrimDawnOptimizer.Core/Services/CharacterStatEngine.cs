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
            if (!IsCompatible(targetSlot, variant.Item.Slot))
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

        var finalPhysique = character.Physique + stats["physique"];
        var finalCunning = character.Cunning + stats["cunning"];
        var finalSpirit = character.Spirit + stats["spirit"];

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

    public static bool IsCompatible(EquipmentSlot target, EquipmentSlot itemSlot) => target switch
    {
        EquipmentSlot.RingLeft or EquipmentSlot.RingRight => itemSlot == EquipmentSlot.Ring,
        _ => target == itemSlot
    };
}
