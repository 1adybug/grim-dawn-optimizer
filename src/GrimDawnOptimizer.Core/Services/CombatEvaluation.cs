using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

// Combat packets are deliberately separate from the game's target-free panel
// collector. Armor, crits, ADCTH and RATA must retain their per-hit provenance.
internal sealed class CombatEvaluation
{
    private sealed record Packet(string Type, double Damage, bool IsDot = false, double LifeSteal = 0, bool LeechEligible = false);
    private sealed record Attack(double Rate, HitDistribution Hit, IReadOnlyList<Packet> Packets);
    private sealed record Proc(double Chance, double Cooldown, bool OnIncoming, bool OnCrit, IReadOnlyList<Packet> Packets);
    private readonly List<Attack> _attacks = [];
    private readonly List<Proc> _procs = [];
    private readonly List<Packet> _retaliation = [];
    private readonly IncomingCombatModel _incoming;
    private readonly ResistanceRules _resistance;
    private readonly double _duration;
    private readonly double _aps;
    private readonly int _targets;
    private readonly double _health;
    private readonly double _regen;
    private readonly double _leechResistanceFactor;
    private double _dotDps;
    private double _retaliationDotDps;
    internal List<UnsupportedEffect> Diagnostics { get; } = [];
    internal double ExpectedDps { get; }
    internal double RetaliationDps { get; }
    internal double IncomingDps => _incoming.IncomingDps;
    internal double RawIncomingDps => _incoming.RawIncomingDps;
    internal double LifeStealEligibleDps { get; }
    internal double LifeStealPerSecond { get; }
    internal double EffectiveLifeSteal => LifeStealEligibleDps > 0 ? LifeStealPerSecond / LifeStealEligibleDps : 0;
    internal HitDistribution MainHit { get; }

    internal CombatEvaluation(CalculatedLoadout loadout, CombatScenario scenario, SkillRotationDefinition rotation, double aps)
    {
        var stats = loadout.Stats;
        _duration = Math.Max(1, scenario.DurationSeconds);
        _aps = aps;
        _targets = Math.Max(1, scenario.TargetCount);
        _health = Math.Max(1, stats["health"]);
        _regen = Math.Max(0, stats["healthRegen"]);
        _leechResistanceFactor = Math.Max(0, 1 - scenario.EnemyLifeLeechResistance / 100);
        _incoming = new(stats, scenario);
        var enemyDa = scenario.EnemyDefensiveAbility > 0 ? scenario.EnemyDefensiveAbility : 2400 + scenario.EnemyLevel * 5;
        MainHit = HitRules.Resolve(stats["offensiveAbility"], enemyDa, stats["criticalDamage"] + rotation.FlatDamage["criticalDamage"]);
        var reductionSources = new List<ActiveResistanceReduction>();
        var attackOpportunities = ProcTiming.Regular(aps, _duration, MainHit.HitChance);
        var criticalOpportunities = ProcTiming.Regular(aps, _duration, MainHit.HitChance * MainHit.CritChance);
        AddReductions(stats, attackOpportunities,
            magnitudeScale: Math.Min(1, Math.Max(0, rotation.WeaponDamagePercent) / 100));
        var selectedOpportunities = ProcTiming.Regular(aps, _duration, MainHit.HitChance * Math.Clamp(rotation.ActivationRate, 0, 1));
        AddReductions(rotation.FlatDamage, selectedOpportunities);
        foreach (var secondary in rotation.SecondaryAttacks) AddReductions(secondary.FlatDamage, selectedOpportunities);

        var effects = loadout.Effects.Where(x => !x.IsDefensive && x.TriggerResolved).ToArray();
        var procRates = new List<(TriggeredEffect Effect, double Rate)>();
        var emitterIndex = 0;
        foreach (var effect in effects)
        {
            var opportunities = effect.Trigger.ToLowerInvariant() switch
            {
                "onattack" => attackOpportunities,
                "oncrit" => criticalOpportunities,
                "onhit" => _incoming.ProcOpportunities,
                _ => Array.Empty<ProcOpportunity>()
            };
            var rate = ProcTiming.ExpectedTriggers(opportunities, effect.Chance, effect.CooldownSeconds) / _duration;
            procRates.Add((effect, rate));
            AddReductions(effect.Damage, opportunities, effect.DurationSeconds, effect.Chance, effect.CooldownSeconds,
                emitter: "proc-instance-" + emitterIndex++);
        }
        var rrStats = stats.Clone();
        foreach (var (key, value) in rotation.FlatDamage.Values.Where(x => x.Key.StartsWith("rr.", StringComparison.OrdinalIgnoreCase)))
            rrStats.Add(key, value);
        _resistance = new(scenario, rrStats, reductionSources);

        var activation = Math.Clamp(rotation.ActivationRate, 0, 1);
        var wps = rotation.WeaponPoolAttacks.Where(x => x.Chance > 0).ToArray();
        var wpsDenominator = Math.Max(100, wps.Sum(x => x.Chance));
        var wpsChance = wps.Sum(x => x.Chance) / wpsDenominator;
        var normalStrikes = rotation.WeaponDamagePercent > 0 ? Math.Max(1, stats["averageWeaponStrikesPerAction"]) : 1;
        AddAttack(rotation.FlatDamage, rotation.WeaponDamagePercent * (wps.Length == 0 ? Math.Max(0, rotation.WeaponPoolDamageMultiplier) : 1),
            activation * (1 - wpsChance), normalStrikes,
            wps.Length == 0 ? rotation.WeaponPoolFlatDamage : null);
        foreach (var w in wps)
            AddAttack(rotation.FlatDamage, rotation.WeaponDamagePercent * Math.Max(0, w.WeaponDamageMultiplier),
                activation * w.Chance / wpsDenominator, Math.Max(1, w.WeaponStrikes), w.FlatDamage);
        if (activation < 1) AddAttack(new(), 100, 1 - activation, normalStrikes);
        foreach (var secondary in rotation.SecondaryAttacks)
            AddAttack(secondary.FlatDamage, secondary.WeaponDamagePercent, activation, 1, secondary: true);

        var directDps = 0d;
        var leechDps = 0d;
        var eligibleDps = 0d;
        foreach (var attack in _attacks)
            foreach (var packet in attack.Packets)
            {
                var dealt = attack.Hit.Expected(m => _resistance.Mitigate(packet.Type, packet.Damage * m, packet.IsDot)) * attack.Rate;
                directDps += dealt;
                if (packet.LeechEligible) eligibleDps += dealt;
                leechDps += dealt * packet.LifeSteal;
            }
        foreach (var (effect, rate) in procRates)
        {
            if (rate <= 0) continue;
            var packets = BuildPackets(stats, effect.Damage, effect.WeaponDamagePercent, extraRaw: true);
            var proc = new Proc(effect.Chance, effect.CooldownSeconds, effect.Trigger.Equals("OnHit", StringComparison.OrdinalIgnoreCase),
                effect.Trigger.Equals("OnCrit", StringComparison.OrdinalIgnoreCase), packets);
            _procs.Add(proc);
            foreach (var packet in packets)
            {
                var dealt = _resistance.Mitigate(packet.Type, packet.Damage, packet.IsDot) * rate;
                directDps += dealt;
                if (packet.LeechEligible) eligibleDps += dealt;
                leechDps += dealt * packet.LifeSteal;
            }
        }

        // Passive retaliation is a response to melee contact, with no OA roll
        // and no ordinary damage bonuses, crit multiplier or global ADCTH.
        var retaliationPackets = BuildRetaliationPackets(stats, new());
        foreach (var packet in retaliationPackets)
        {
            if (!packet.IsDot) _retaliation.Add(packet);
            else
            {
                var duration = stats[StatKeys.RetaliationDot(packet.Type)] > 0
                    ? Math.Max(1, stats[StatKeys.RetaliationDotTotal(packet.Type)] / stats[StatKeys.RetaliationDot(packet.Type)]) : 1;
                _retaliationDotDps += _resistance.Mitigate(packet.Type, packet.Damage, true) * Math.Min(1, _incoming.MeleeHitsPerSecond * duration);
            }
        }
        RetaliationDps = _retaliation.Sum(x => _resistance.Mitigate(x.Type, x.Damage)) * _incoming.MeleeHitsPerSecond + _retaliationDotDps;
        ExpectedDps = (directDps + _dotDps) * _targets + RetaliationDps;
        LifeStealEligibleDps = eligibleDps * _targets;
        LifeStealPerSecond = leechDps * _targets * _leechResistanceFactor;

        void AddReductions(StatBlock block, IReadOnlyList<ProcOpportunity> opportunities, double defaultDuration = 0,
            double procChance = 1, double cooldown = 0, double magnitudeScale = 1, string emitter = "")
        {
            foreach (var modifier in block.CombatModifiers.Where(x => x.Value > 0 &&
                         x.Kind is CombatModifierKind.FlatResistanceReduction or CombatModifierKind.PercentResistanceReduction or CombatModifierKind.StackingResistanceReduction))
            {
                var duration = modifier.DurationSeconds > 0 ? modifier.DurationSeconds : defaultDuration;
                var uptime = modifier.IsPersistent ? Math.Clamp(modifier.Chance, 0, 1)
                    : ProcTiming.DebuffUptime(opportunities, procChance, cooldown, modifier.Chance, duration, _duration);
                if (!modifier.IsPersistent && duration <= 0)
                    Diagnostics.Add(new(modifier.Source, "降抗持续时间缺失，未假定为全程生效。", [CalculationDiagnostics.CombatDps]));
                var magnitude = modifier.Value * (modifier.IsPersistent ? 1 : magnitudeScale);
                if (magnitude > 0) reductionSources.Add(new(modifier with { Value = magnitude }, uptime, emitter));
            }
        }

        void AddAttack(StatBlock skill, double weaponPercent, double chance, double strikes, StatBlock? extra = null, bool secondary = false)
        {
            if (chance <= 0) return;
            var modifiers = skill.Clone();
            if (extra is not null) modifiers.Add(extra);
            var packets = BuildPackets(stats, modifiers, weaponPercent, loadout: loadout, secondary: secondary);
            var distribution = HitRules.Resolve(stats["offensiveAbility"], enemyDa, stats["criticalDamage"] + modifiers["criticalDamage"]);
            var direct = packets.Where(x => !x.IsDot).ToArray();
            _attacks.Add(new(aps * chance * strikes, distribution, direct));
            foreach (var dot in packets.Where(x => x.IsDot))
                _dotDps += distribution.Expected(m => _resistance.Mitigate(dot.Type, dot.Damage * m, true)) * chance;
        }
    }

    private static IReadOnlyList<Packet> BuildPackets(StatBlock owner, StatBlock skill, double weaponPercent,
        CalculatedLoadout? loadout = null, bool secondary = false, bool extraRaw = false)
    {
        var scale = Math.Max(0, weaponPercent) / 100;
        var raw = StatKeys.DamageTypes.ToDictionary(type => type, type =>
            (owner[StatKeys.WeaponDamage(type)] + owner[StatKeys.DamageFlat(type)] +
             (secondary ? owner[$"secondaryWeaponDamageCorrection.{type}"] : 0)) * scale +
            skill[StatKeys.DamageFlat(type)] + (extraRaw ? skill[type] : 0), StringComparer.OrdinalIgnoreCase);
        var piercing = loadout?.Slots.Values.Where(x => x.Item.Slot == EquipmentSlot.MainHand)
            .Select(x => x.CombinedStats[StatKeys.WeaponArmorPiercing]).DefaultIfEmpty(0).Average() ?? 0;
        var local = DamageConversion.Apply(raw, skill, owner,
            scale > 0 ? Math.Clamp(piercing * (1 + (owner["armorPiercingModifier"] + skill["armorPiercingModifier"]) / 100), 0, 100) : 0);
        var total = Math.Max(0, 1 + (owner["damage.total.multiplierPercent"] + skill["damage.total.multiplierPercent"]) / 100);
        var globalLeech = Math.Max(0, owner["lifeSteal"]) / 100 * Math.Min(1, scale);
        var localLeech = Math.Max(0, skill["lifeSteal"]) / 100;
        var leech = globalLeech + localLeech;
        var result = new List<Packet>();
        foreach (var type in StatKeys.DamageTypes)
        {
            var direct = Math.Max(0, raw[type] * Math.Max(0, 1 +
                (owner[StatKeys.DamagePercent(type)] + owner["damage.total.percent"]) / 100) + local[type]) * total;
            if (direct > 0) result.Add(new(type, direct, LifeSteal: leech, LeechEligible: scale > 0 || localLeech > 0));
            var dot = extraRaw ? skill[$"damage.{type}.dotTotal"]
                : owner[StatKeys.DamageDot(type)] * scale + skill[StatKeys.DamageDot(type)];
            dot *= Math.Max(0, 1 + (owner[StatKeys.DamageDotPercent(type)] + skill[StatKeys.DamageDotPercent(type)] +
                owner["damage.total.percent"] + skill["damage.total.percent"]) / 100) * total;
            if (dot > 0) result.Add(new(type, dot, true));
        }
        // RATA is a separate retaliation-scaled packet. Neither ordinary
        // +damage nor total-damage multipliers or global ADCTH boost it.
        var rataScale = Math.Max(0, skill["retaliation.addedToAttackPercent"]) / 100;
        if (rataScale > 0)
            result.AddRange(BuildRetaliationPackets(owner, skill).Select(x => x with
            { Damage = x.Damage * rataScale, LifeSteal = x.IsDot ? 0 : localLeech, LeechEligible = !x.IsDot && localLeech > 0 }));
        return result;
    }

    private static IReadOnlyList<Packet> BuildRetaliationPackets(StatBlock owner, StatBlock skill)
    {
        var result = new List<Packet>();
        var conversions = new StatBlock(skill.Values.Where(x => x.Key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        foreach (var dot in new[] { false, true })
        {
            var raw = StatKeys.DamageTypes.ToDictionary(type => type, type =>
                owner[dot ? StatKeys.RetaliationDot(type) : StatKeys.RetaliationFlat(type)], StringComparer.OrdinalIgnoreCase);
            DamageConversion.Apply(raw, conversions, owner);
            foreach (var type in StatKeys.DamageTypes)
            {
                if (dot && type is "pierce" or "aether" or "chaos") continue;
                var bonus = owner[dot ? StatKeys.RetaliationDotPercent(type) : StatKeys.RetaliationPercent(type)] + owner["retaliation.total.percent"];
                var value = Math.Max(0, raw[type]) * Math.Max(0, 1 + bonus / 100) *
                    Math.Max(0, 1 + owner["retaliation.total.multiplierPercent"] / 100);
                if (value > 0) result.Add(new(type, value, dot));
            }
        }
        return result;
    }

    internal (double Dps, double Survival) Sample(Random rng)
    {
        var procReady = new Dictionary<Proc, double>();
        var dealt = _dotDps * _duration;
        var leech = 0d;
        foreach (var attack in _attacks)
        {
            var count = SampleCount(rng, attack.Rate * _duration);
            for (var i = 0; i < count; i++)
            {
                var hit = attack.Hit.Sample(rng);
                if (hit.Multiplier <= 0) continue;
                foreach (var packet in attack.Packets)
                {
                    var value = _resistance.Sample(packet.Type, packet.Damage * hit.Multiplier, packet.IsDot, rng);
                    dealt += value;
                    leech += value * packet.LifeSteal;
                }
            }
        }
        var outgoingEvents = SampleCount(rng, _aps * _duration);
        for (var i = 0; i < outgoingEvents; i++)
        {
            var hit = MainHit.Sample(rng);
            if (hit.Multiplier <= 0) continue;
            foreach (var proc in _procs.Where(x => !x.OnIncoming && (!x.OnCrit || hit.IsCritical)))
                Trigger(proc, Math.Min(_duration, (i + 1) / _aps));
        }
        var retaliation = _retaliationDotDps * _duration;
        var received = _incoming.Sample(rng, (kind, time) =>
        {
            foreach (var proc in _procs.Where(x => x.OnIncoming)) Trigger(proc, time);
            if (kind == IncomingAttackKind.Melee)
                retaliation += _retaliation.Sum(x => _resistance.Sample(x.Type, x.Damage, false, rng));
        });
        var sustain = _regen + leech / _duration * _targets * _leechResistanceFactor;
        var net = received / _duration - sustain;
        return (dealt / _duration * _targets + retaliation / _duration,
            net <= 1e-9 ? double.PositiveInfinity : _health / net);

        void Trigger(Proc proc, double time)
        {
            if (procReady.GetValueOrDefault(proc) > time + 1e-9 || rng.NextDouble() >= proc.Chance) return;
            procReady[proc] = time + Math.Max(0, proc.Cooldown);
            foreach (var packet in proc.Packets)
            {
                var value = _resistance.Sample(packet.Type, packet.Damage, packet.IsDot, rng);
                dealt += value;
                leech += value * packet.LifeSteal;
            }
        }
    }

    private static int SampleCount(Random rng, double expected)
    {
        var whole = (int)Math.Floor(Math.Max(0, expected));
        return whole + (rng.NextDouble() < expected - whole ? 1 : 0);
    }
}
