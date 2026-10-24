using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>Applies skill conversion before equipment conversion, locking converted packets.</summary>
internal static class DamageConversion
{
    /// <summary>
    /// Replaces damage with converted raw amounts and returns the contribution of
    /// local skill bonuses. Global bonuses are applied to the final type by the caller.
    /// </summary>
    internal static Dictionary<string, double> Apply(
        Dictionary<string, double> damage, StatBlock skillModifiers, StatBlock globalStats,
        double armorPiercingPercent = 0)
    {
        var original = new Dictionary<string, double>(damage, StringComparer.OrdinalIgnoreCase);
        var converted = new Dictionary<string, double>(damage, StringComparer.OrdinalIgnoreCase);
        var localBonus = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in StatKeys.DamageTypes)
        {
            converted[type] = 0;
            localBonus[type] = 0;
        }

        foreach (var source in StatKeys.DamageTypes)
        {
            var amount = original.GetValueOrDefault(source);
            if (amount == 0) continue;
            var percent = skillModifiers[StatKeys.DamagePercent(source)] + skillModifiers["damage.total.percent"];
            var remaining = ApplyLayer(source, amount, percent, skillModifiers);
            remaining = ApplyLayer(source, remaining.Amount, remaining.Percent, globalStats);
            if (source.Equals("physical", StringComparison.OrdinalIgnoreCase) && armorPiercingPercent > 0)
            {
                // BasePhysical::Process performs armor piercing after ordinary
                // conversion. Already-converted packets are locked above. Unlike
                // conversion, piercing uses its own local modifier and does not
                // scale the physical/pierce modifier by the piercing fraction.
                var pierced = remaining.Amount * Math.Clamp(armorPiercingPercent / 100d, 0, 1);
                var piercePercent = skillModifiers[StatKeys.DamagePercent("pierce")] + skillModifiers["damage.total.percent"];
                Accumulate("pierce", pierced, piercePercent);
                Accumulate(source, remaining.Amount - pierced, remaining.Percent);
            }
            else Accumulate(source, remaining.Amount, remaining.Percent);
        }

        damage.Clear();
        foreach (var (type, value) in converted) damage[type] = value;
        return localBonus;

        void Accumulate(string target, double amount, double percent)
        {
            converted[target] += amount;
            localBonus[target] += amount * percent / 100d;
        }

        (double Amount, double Percent) ApplyLayer(string source, double amount, double percent, StatBlock conversionStats)
        {
            var routes = StatKeys.DamageTypes
                .Where(target => !source.Equals(target, StringComparison.OrdinalIgnoreCase))
                .Select(target => (Target: target, Percent: Math.Max(0, conversionStats[$"conversion.{source}.{target}"])))
                .Where(route => route.Percent > 0)
                .ToArray();
            var total = routes.Sum(route => route.Percent);
            if (total == 0) return (amount, percent);

            var denominator = Math.Max(100, total);
            foreach (var route in routes)
            {
                var fraction = route.Percent / denominator;
                // Accumulating directly locks it against every subsequent conversion.
                // The engine copies the source's local modifier, then scales
                // both its raw damage and modifier by the converted fraction.
                Accumulate(route.Target, amount * fraction, percent * fraction);
            }
            var remainingFraction = 1 - Math.Min(1, total / 100);
            return (amount * remainingFraction, percent * remainingFraction);
        }
    }
}
