using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

internal sealed class IncomingCombatModel
{
    private sealed record Profile(IncomingDamageProfile Definition, HitDistribution Hit, double Avoidance,
        IReadOnlyList<(string Type, double Damage)> Damage);
    private sealed record IncomingEvent(double Time, double Probability, Profile Profile);
    private sealed record Absorption(double Factor, double Flat, double Probability);
    private sealed record DamageOutcome(double Probability, double Damage);
    private readonly StatBlock _stats;
    private readonly CombatScenario _scenario;
    private readonly IncomingEvent[] _events;
    private readonly Absorption[] _absorption;
    private readonly Dictionary<(Profile Profile, double Multiplier, bool Blocked), DamageOutcome[]> _damage = [];
    private readonly double _blockChance;
    private readonly double _blockAmount;
    private readonly double _recovery;
    private readonly double _duration;

    internal double IncomingDps { get; }
    internal double RawIncomingDps { get; }
    internal double LandedHitsPerSecond { get; }
    internal double MeleeHitsPerSecond { get; }
    internal IReadOnlyList<ProcOpportunity> ProcOpportunities { get; }

    internal IncomingCombatModel(StatBlock stats, CombatScenario scenario)
    {
        _stats = stats;
        _scenario = scenario;
        _duration = Math.Max(1, scenario.DurationSeconds);
        _blockChance = Math.Clamp(stats["blockChance"] / 100, 0, 1);
        _blockAmount = Math.Max(0, stats["blockAmount"]) *
            (stats["blockAbsorption"] > 0 ? Math.Clamp(stats["blockAbsorption"] / 100, 0, 1) : 1);
        _recovery = Math.Max(0, stats["blockRecoverySeconds"]) * (1 - Math.Clamp(stats["blockRecoveryReduction"] / 100, 0, 1));
        _absorption = Absorptions(stats);
        var events = new List<IncomingEvent>();
        foreach (var incoming in DifficultyRules.IncomingProfiles(scenario))
        {
            if (incoming.HitsPerSecond <= 0) continue;
            var usesOa = !incoming.IsDamageOverTime && (incoming.UsesOffensiveAbility ?? incoming.Kind != IncomingAttackKind.Spell);
            var hit = usesOa ? HitRules.Resolve(scenario.EnemyOffensiveAbility, stats["defensiveAbility"], incoming.CriticalDamagePercent)
                : HitDistribution.Guaranteed;
            var avoidance = incoming.IsDamageOverTime ? 0 : incoming.Kind switch
            {
                IncomingAttackKind.Melee => Math.Clamp(stats["dodgeChance"] / 100, 0, 1),
                IncomingAttackKind.Projectile => Math.Clamp(stats["projectileAvoidance"] / 100, 0, 1),
                _ => 0
            };
            var damage = new (string, double)[]
            {
                ("physical", incoming.PhysicalPerHit), ("pierce", incoming.PiercePerHit), ("fire", incoming.FirePerHit),
                ("cold", incoming.ColdPerHit), ("lightning", incoming.LightningPerHit), ("acid", incoming.AcidPerHit),
                ("vitality", incoming.VitalityPerHit), ("aether", incoming.AetherPerHit), ("chaos", incoming.ChaosPerHit),
                ("bleeding", incoming.BleedingPerHit)
            }.Where(x => x.Item2 > 0).ToArray();
            var profile = new Profile(incoming, hit, avoidance, damage);
            var expected = incoming.HitsPerSecond * _duration;
            var whole = (int)Math.Floor(expected);
            for (var i = 0; i < whole; i++) events.Add(new((i + 1) / incoming.HitsPerSecond, 1, profile));
            if (expected > whole) events.Add(new(_duration, expected - whole, profile));
            RawIncomingDps += damage.Sum(x => x.Item2) * incoming.HitsPerSecond;
            if (!incoming.IsDamageOverTime)
            {
                var rate = incoming.HitsPerSecond * hit.HitChance * (1 - avoidance);
                LandedHitsPerSecond += rate;
                if (incoming.Kind == IncomingAttackKind.Melee) MeleeHitsPerSecond += rate;
            }
        }
        _events = events.OrderBy(x => x.Time).ToArray();
        foreach (var profile in _events.Select(x => x.Profile).Distinct())
        foreach (var hit in profile.Hit.Outcomes.Where(x => x.Multiplier > 0))
        foreach (var blocked in new[] { false, true })
            _damage[(profile, hit.Multiplier, blocked)] = DamageOutcomes(profile, hit.Multiplier, blocked);
        ProcOpportunities = _events.Where(x => !x.Profile.Definition.IsDamageOverTime)
            .Select(x => new ProcOpportunity(x.Time, x.Probability * x.Profile.Hit.HitChance * (1 - x.Profile.Avoidance))).ToArray();
        IncomingDps = ExpectedDamage() / _duration;
    }

    private double ExpectedDamage()
    {
        var damages = _events.Select(x => x.Profile).Distinct().ToDictionary(x => x, profile =>
            (Unblocked: profile.Hit.Expected(m => MeanDamage(profile, m, false)),
             Blocked: profile.Hit.Expected(m => MeanDamage(profile, m, true))));
        if (_recovery <= 0 || _blockChance <= 0)
            return _events.Sum(ev =>
            {
                var block = ev.Profile.Definition.CanBeBlocked && !ev.Profile.Definition.IsDamageOverTime ? _blockChance : 0;
                var damage = damages[ev.Profile];
                return ev.Probability * (1 - ev.Profile.Avoidance) * ((1 - block) * damage.Unblocked + block * damage.Blocked);
            });
        // Probability distribution of the shield's next ready time. Unlike an
        // average block subtraction, this observes recovery across all attacks.
        var ready = new Dictionary<double, double> { [0] = 1 };
        var total = 0d;
        foreach (var ev in _events)
        {
            var blockable = ev.Profile.Definition.CanBeBlocked && !ev.Profile.Definition.IsDamageOverTime;
            var available = blockable ? ready.Where(x => x.Key <= ev.Time + 1e-9).Sum(x => x.Value) : 0;
            var block = available * _blockChance;
            var damage = damages[ev.Profile];
            total += ev.Probability * (1 - ev.Profile.Avoidance) * ((1 - block) * damage.Unblocked + block * damage.Blocked);
            if (!blockable || _recovery <= 0 || _blockChance <= 0) continue;
            var next = new Dictionary<double, double>();
            foreach (var (time, probability) in ready)
            {
                var chance = time <= ev.Time + 1e-9
                    ? ev.Probability * ev.Profile.Hit.HitChance * (1 - ev.Profile.Avoidance) * _blockChance : 0;
                Add(time <= ev.Time ? 0 : time, probability * (1 - chance));
                Add(ev.Time + _recovery, probability * chance);
            }
            ready = next;
            void Add(double time, double probability)
            { if (probability > 0) next[time] = next.GetValueOrDefault(time) + probability; }
        }
        return total;
    }

    private double MeanDamage(Profile profile, double multiplier, bool blocked) =>
        _damage[(profile, multiplier, blocked)].Sum(x => x.Probability * x.Damage);

    private DamageOutcome[] DamageOutcomes(Profile profile, double multiplier, bool blocked)
    {
        var components = new List<(string Type, double Value)>();
        foreach (var (type, raw) in profile.Damage)
        {
            // Block and flat absorption operate independently on each type in
            // the hit. A DoT tick bypasses block, OA/DA and physical armor.
            var value = Math.Max(0, raw * multiplier - (blocked ? _blockAmount : 0));
            value *= 1 - DifficultyRules.EffectiveResistance(_stats, _scenario, type) / 100;
            components.Add((type, value));
        }
        var physical = components.FirstOrDefault(x => x.Type == "physical").Value;
        IReadOnlyList<(double Probability, double Damage)> regions = profile.Definition.IsDamageOverTime
            ? [(1, physical)] : ArmorRules.PhysicalOutcomes(_stats, physical);
        return regions.SelectMany(region => _absorption.Select(absorption => new DamageOutcome(
            region.Probability * absorption.Probability,
            components.Sum(component => Math.Max(0,
                (component.Type == "physical" ? region.Damage : component.Value) * absorption.Factor - absorption.Flat))))).ToArray();
    }

    internal double Sample(Random rng, Action<IncomingAttackKind, double> onLandedHit)
    {
        var readyAt = 0d;
        var received = 0d;
        foreach (var ev in _events)
        {
            if (ev.Probability < 1 && rng.NextDouble() >= ev.Probability) continue;
            if (rng.NextDouble() < ev.Profile.Avoidance) continue;
            var hit = ev.Profile.Hit.Sample(rng);
            if (hit.Multiplier <= 0) continue;
            if (!ev.Profile.Definition.IsDamageOverTime) onLandedHit(ev.Profile.Definition.Kind, ev.Time);
            var blocked = ev.Profile.Definition.CanBeBlocked && !ev.Profile.Definition.IsDamageOverTime &&
                readyAt <= ev.Time + 1e-9 && rng.NextDouble() < _blockChance;
            if (blocked) readyAt = ev.Time + _recovery;
            var roll = rng.NextDouble();
            var outcomes = _damage[(ev.Profile, hit.Multiplier, blocked)];
            foreach (var outcome in outcomes)
            {
                roll -= outcome.Probability;
                if (roll < 0) { received += outcome.Damage; break; }
            }
        }
        return received;
    }

    private static Absorption[] Absorptions(StatBlock stats)
    {
        var states = new List<Absorption> { new(1 - Math.Clamp(stats["damageAbsorption.percent"] / 100, 0, 1),
            Math.Max(0, stats["damageAbsorption"]), 1) };
        foreach (var source in stats.CombatModifiers.Where(x => x.Value > 0 &&
                     x.Kind is CombatModifierKind.PercentAbsorption or CombatModifierKind.FlatAbsorption)
                     .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
        {
            var chance = Math.Clamp(source.Max(x => x.Chance), 0, 1);
            var factor = 1 - Math.Clamp(source.Where(x => x.Kind == CombatModifierKind.PercentAbsorption).Sum(x => x.Value) / 100, 0, 1);
            var flat = source.Where(x => x.Kind == CombatModifierKind.FlatAbsorption).Sum(x => x.Value);
            states = states.SelectMany(x => chance >= 1
                ? new[] { new Absorption(x.Factor * factor, x.Flat + flat, x.Probability) }
                : new[] { x with { Probability = x.Probability * (1 - chance) },
                    new Absorption(x.Factor * factor, x.Flat + flat, x.Probability * chance) }).ToList();
        }
        return states.ToArray();
    }
}
