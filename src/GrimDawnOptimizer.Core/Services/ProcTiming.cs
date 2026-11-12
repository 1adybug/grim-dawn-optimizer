namespace GrimDawnOptimizer.Core.Services;

internal sealed record ProcOpportunity(double Time, double Probability);

/// <summary>Finite encounter timeline; cooldown starts only on a successful proc.</summary>
internal static class ProcTiming
{
    internal static IReadOnlyList<ProcOpportunity> Regular(double rate, double duration, double probability)
    {
        var result = new List<ProcOpportunity>();
        if (rate <= 0 || probability <= 0) return result;
        var count = rate * duration;
        var whole = (int)Math.Floor(count);
        for (var i = 0; i < whole; i++) result.Add(new((i + 1) / rate, probability));
        if (count > whole) result.Add(new(duration, probability * (count - whole)));
        return result;
    }

    internal static double ExpectedTriggers(IReadOnlyList<ProcOpportunity> opportunities, double chance, double cooldown)
    {
        if (cooldown <= 0) return opportunities.Sum(x => Math.Clamp(x.Probability * chance, 0, 1));
        var ready = new Dictionary<double, double> { [0] = 1 };
        var count = 0d;
        foreach (var ev in opportunities)
        {
            var next = new Dictionary<double, double>();
            foreach (var (time, mass) in ready)
            {
                var probability = time <= ev.Time + 1e-9 ? Math.Clamp(ev.Probability * chance, 0, 1) : 0;
                count += mass * probability;
                Add(time <= ev.Time ? 0 : time, mass * (1 - probability));
                Add(ev.Time + Math.Max(0, cooldown), mass * probability);
            }
            ready = next;
            void Add(double time, double mass) { if (mass > 0) next[time] = next.GetValueOrDefault(time) + mass; }
        }
        return count;
    }

    internal static double DebuffUptime(IReadOnlyList<ProcOpportunity> opportunities, double procChance,
        double cooldown, double applicationChance, double debuffDuration, double encounterDuration)
    {
        if (debuffDuration <= 0 || encounterDuration <= 0) return 0;
        var states = new Dictionary<(double Ready, double Expires), double> { [(0, 0)] = 1 };
        var active = 0d;
        var previous = 0d;
        foreach (var ev in opportunities)
        {
            var next = new Dictionary<(double Ready, double Expires), double>();
            foreach (var (state, mass) in states)
            {
                active += mass * Math.Clamp(state.Expires - previous, 0, ev.Time - previous);
                var probability = state.Ready <= ev.Time + 1e-9 ? Math.Clamp(ev.Probability * procChance, 0, 1) : 0;
                var oldReady = state.Ready <= ev.Time ? 0 : state.Ready;
                var oldExpiry = state.Expires <= ev.Time ? 0 : state.Expires;
                Add((oldReady, oldExpiry), mass * (1 - probability));
                // A failed debuff roll still consumes the successful proc's cooldown.
                Add((ev.Time + Math.Max(0, cooldown), oldExpiry), mass * probability * (1 - Math.Clamp(applicationChance, 0, 1)));
                Add((ev.Time + Math.Max(0, cooldown), ev.Time + debuffDuration), mass * probability * Math.Clamp(applicationChance, 0, 1));
            }
            states = next;
            previous = ev.Time;
            void Add((double Ready, double Expires) state, double mass)
            { if (mass > 0) next[state] = next.GetValueOrDefault(state) + mass; }
        }
        foreach (var (state, mass) in states)
            active += mass * Math.Clamp(state.Expires - previous, 0, encounterDuration - previous);
        return Math.Clamp(active / encounterDuration, 0, 1);
    }
}
