using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal sealed record ActiveResistanceReduction(CombatModifier Modifier, double Uptime, string Emitter = "");
internal sealed record ResistanceOutcome(double Resistance, double Probability);

internal sealed class ResistanceRules
{
    private readonly Dictionary<string, ResistanceOutcome[]> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CombatScenario _scenario;

    internal ResistanceRules(CombatScenario scenario, StatBlock stats, IEnumerable<ActiveResistanceReduction> reductions)
    {
        _scenario = scenario;
        var resolved = reductions.Where(x => x.Modifier.Value > 0 && x.Uptime > 0).ToArray();
        foreach (var type in StatKeys.DamageTypes)
        {
            // Equal named debuffs do not stack; separate proc owners must not
            // turn one skill into two independently stackable -res debuffs.
            var sources = resolved.Where(x => x.Modifier.DamageType.Equals(type, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Modifier.Source, StringComparer.OrdinalIgnoreCase).Select(SourceOutcomes).ToArray();
            var states = new List<(double Flat, double Percent, double Stacking, double Probability)>
            {
                (Math.Max(0, stats[$"rr.{type}.flat"]), Math.Clamp(stats[$"rr.{type}.percent"] / 100, 0, 1),
                    Math.Max(0, stats[$"rr.{type}.stacking"]), 1)
            };
            foreach (var source in sources)
            {
                var next = new List<(double, double, double, double)>();
                foreach (var state in states)
                foreach (var outcome in source)
                {
                    next.Add((Math.Max(state.Flat, outcome.Flat), Math.Max(state.Percent, outcome.Percent),
                        state.Stacking + outcome.Stacking, state.Probability * outcome.Probability));
                }
                // Merge identical states rather than approximating the strongest
                // proc by its average magnitude. This also bounds common flat-RR sets.
                states = next.GroupBy(x => (x.Item1, x.Item2, x.Item3))
                    .Select(g => (g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Sum(x => x.Item4))).ToList();
            }
            _outcomes[type] = states.Select(x => new ResistanceOutcome(Apply(
                scenario.EnemyResistances.GetValueOrDefault(type), x.Flat, x.Percent, x.Stacking), x.Probability)).ToArray();
        }
    }

    private static IReadOnlyList<(double Flat, double Percent, double Stacking, double Probability)> SourceOutcomes(
        IEnumerable<ActiveResistanceReduction> source)
    {
        // Emitters retain independent proc chances/cooldowns. Their same named
        // debuff still uses the strongest active value, including mixed levels.
        var states = new List<(Dictionary<(CombatModifierKind Kind, string Field), double> Values, double Probability)>
            { (new(), 1) };
        foreach (var emitter in source.GroupBy(x => x.Emitter, StringComparer.OrdinalIgnoreCase))
        {
            var uptime = emitter.Max(x => Math.Clamp(x.Uptime, 0, 1));
            var next = new List<(Dictionary<(CombatModifierKind, string), double>, double)>();
            foreach (var state in states)
            {
                if (uptime < 1) next.Add((state.Values, state.Probability * (1 - uptime)));
                if (uptime <= 0) continue;
                var values = new Dictionary<(CombatModifierKind, string), double>(state.Values);
                foreach (var effect in emitter)
                {
                    var key = (effect.Modifier.Kind, effect.Modifier.Field.ToLowerInvariant());
                    values[key] = Math.Max(values.GetValueOrDefault(key), effect.Modifier.Value);
                }
                next.Add((values, state.Probability * uptime));
            }
            states = next;
        }
        return states.Select(state => (
            Flat: state.Values.Where(x => x.Key.Kind == CombatModifierKind.FlatResistanceReduction).Select(x => x.Value).DefaultIfEmpty(0).Max(),
            Percent: state.Values.Where(x => x.Key.Kind == CombatModifierKind.PercentResistanceReduction).Select(x => x.Value / 100).DefaultIfEmpty(0).Max(),
            Stacking: state.Values.Where(x => x.Key.Kind == CombatModifierKind.StackingResistanceReduction).Sum(x => x.Value),
            state.Probability)).GroupBy(x => (x.Flat, x.Percent, x.Stacking))
            .Select(g => (g.Key.Flat, Math.Clamp(g.Key.Percent, 0, 1), g.Key.Stacking, g.Sum(x => x.Probability))).ToArray();
    }

    // -X% resistance, strongest X% Reduced, strongest X Reduced, in that order.
    // Percentage reduction magnifies a negative resistance instead of raising it.
    internal static double Apply(double resistance, double flat, double percent, double stacking)
    {
        var reduced = resistance - Math.Max(0, stacking);
        reduced *= reduced < 0 ? 1 + Math.Clamp(percent, 0, 1) : 1 - Math.Clamp(percent, 0, 1);
        return reduced - Math.Max(0, flat);
    }

    internal double Mitigate(string type, double damage, bool isDot = false) =>
        _outcomes[type].Sum(x => x.Probability * AfterResistance(type, damage, isDot, x.Resistance));

    internal double Sample(string type, double damage, bool isDot, Random rng)
    {
        var roll = rng.NextDouble();
        foreach (var outcome in _outcomes[type])
        {
            roll -= outcome.Probability;
            if (roll < 0) return AfterResistance(type, damage, isDot, outcome.Resistance);
        }
        return AfterResistance(type, damage, isDot, _outcomes[type][^1].Resistance);
    }

    private double AfterResistance(string type, double damage, bool isDot, double resistance)
    {
        var result = Math.Max(0, damage) * Math.Max(0, 1 - resistance / 100);
        if (!isDot && type.Equals("physical", StringComparison.OrdinalIgnoreCase))
            result -= Math.Min(result, Math.Max(0, _scenario.EnemyArmor)) * Math.Clamp(_scenario.EnemyArmorAbsorption / 100, 0, 1);
        return result;
    }

}
