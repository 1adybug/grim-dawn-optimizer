using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal static class ArmorRules
{
    private static readonly (EquipmentSlot Slot, string Region, double Chance)[] Regions =
    [
        (EquipmentSlot.Head, "Head", 15), (EquipmentSlot.Shoulders, "Shoulders", 15),
        (EquipmentSlot.Chest, "Torso", 26), (EquipmentSlot.Hands, "Arms", 12),
        (EquipmentSlot.Legs, "Legs", 20), (EquipmentSlot.Feet, "Feet", 12)
    ];

    internal static void Apply(StatBlock stats, IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots,
        GameDataCatalog catalog)
    {
        catalog.Records.TryGetValue("records/game/combatformulas.dbr", out var formulas);
        var local = new double[Regions.Length];
        var chances = new double[Regions.Length];
        for (var i = 0; i < Regions.Length; i++)
        {
            var (slot, region, chance) = Regions[i];
            chances[i] = formulas?.NumericValues.Values.GetValueOrDefault($"combatRegion{region}Chance", chance) ?? chance;
            chances[i] = Math.Max(0, chances[i]);
            if (!slots.TryGetValue(slot, out var variant)) continue;
            // The body's base armor and defensiveBonusProtection from its
            // attachments are local. Belt armor and other flat armor apply to
            // every region. Attachment defensiveProtection remains global.
            local[i] = variant.Item.Stats["armor"] +
                       (variant.Component?.Stats["armor.localBonus"] ?? 0) +
                       (variant.Augment?.Stats["armor.localBonus"] ?? 0);
        }

        var global = stats["armor"] - local.Sum();
        var modifier = Math.Max(0, 1 + stats["armor.percent"] / 100d);
        var absorption = Math.Clamp(stats["armorAbsorption"] * (1 + stats["armorAbsorption.percent"] / 100d), 0, 100);
        stats.Set("armorAbsorption", absorption);
        var chanceTotal = chances.Sum();
        var average = 0d;
        for (var i = 0; i < Regions.Length; i++)
        {
            var region = Regions[i].Slot.ToString();
            var chance = chanceTotal > 0 ? chances[i] / chanceTotal : Regions[i].Chance / 100d;
            var armor = Math.Max(0, local[i] + global) * modifier;
            stats.Set($"armor.region.{region}", armor);
            stats.Set($"armor.chance.{region}", chance);
            stats.Set($"armor.absorption.{region}", local[i] > 0 ? absorption : 0);
            average += armor * chance;
        }
        stats.Set("armor", average);
        stats.Set("armor.regionModel", 1);
    }

    internal static double MitigatePhysicalHit(StatBlock stats, double damage)
    {
        if (damage <= 0) return 0;
        if (stats["armor.regionModel"] <= 0)
            return AfterArmor(damage, stats["armor"], stats["armorAbsorption"]);

        // Average the mitigated hits, not the armor: min(hit, armor) is nonlinear.
        return Regions.Sum(region => stats[$"armor.chance.{region.Slot}"] *
            AfterArmor(damage, stats[$"armor.region.{region.Slot}"], stats[$"armor.absorption.{region.Slot}"]));
    }

    private static double AfterArmor(double damage, double armor, double absorption) =>
        damage - Math.Min(damage, Math.Max(0, armor)) * Math.Clamp(absorption / 100d, 0, 1);
}
