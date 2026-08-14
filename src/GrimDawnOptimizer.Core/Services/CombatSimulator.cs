using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public sealed class CombatSimulator : ICombatSimulator
{
    private const double Epsilon = 1e-9;

    public SimulationReport Simulate(
        CharacterSnapshot character,
        CalculatedLoadout loadout,
        CombatScenario scenario,
        SkillRotationDefinition rotation)
    {
        var breakdown = BuildBreakdown(loadout, scenario, rotation);
        return ToReport(loadout, breakdown);
    }

    public async Task<SimulationReport> RunMonteCarloAsync(
        CharacterSnapshot character,
        CalculatedLoadout loadout,
        CombatScenario scenario,
        SkillRotationDefinition rotation,
        int trials,
        int seed,
        CancellationToken cancellationToken = default)
    {
        trials = Math.Clamp(trials, 100, 10_000);
        return await Task.Run(() =>
        {
            var breakdown = BuildBreakdown(loadout, scenario, rotation);
            var rng = new Random(seed);
            var damageSamples = new double[trials];
            var survivalSamples = new double[trials];
            var duration = Math.Max(1, scenario.DurationSeconds);

            for (var trial = 0; trial < trials; trial++)
            {
                if ((trial & 63) == 0) cancellationToken.ThrowIfCancellationRequested();

                var attacks = SampleEventCount(rng, breakdown.AttacksPerSecond * duration);
                var totalDamage = 0d;
                for (var attack = 0; attack < attacks; attack++)
                {
                    if (rng.NextDouble() > breakdown.HitChance) continue;
                    var variance = 0.9 + rng.NextDouble() * 0.2;
                    var critical = rng.NextDouble() < breakdown.CritChance;
                    totalDamage += breakdown.DamagePerLandedHit * variance * (critical ? breakdown.CritMultiplier : 1);

                    foreach (var proc in breakdown.Procs)
                        if (rng.NextDouble() < proc.ChancePerAttack)
                            totalDamage += proc.Damage;
                }

                damageSamples[trial] = totalDamage / duration;

                var incomingHits = SampleEventCount(rng, scenario.IncomingDamage.HitsPerSecond * duration);
                var received = 0d;
                for (var hit = 0; hit < incomingHits; hit++)
                {
                    if (rng.NextDouble() > breakdown.EnemyHitChance) continue;
                    if (rng.NextDouble() < breakdown.AvoidanceChance) continue;
                    var damage = breakdown.DamageTakenPerLandedHit * (0.9 + rng.NextDouble() * 0.2);
                    if (rng.NextDouble() < breakdown.BlockChance)
                        damage = Math.Max(0, damage - breakdown.BlockAmount);
                    received += damage;
                }

                var incomingPerSecond = received / duration;
                var sustain = breakdown.HealthRegen + damageSamples[trial] * breakdown.EffectiveLifeSteal;
                survivalSamples[trial] = incomingPerSecond <= sustain + Epsilon
                    ? double.PositiveInfinity
                    : breakdown.Health / (incomingPerSecond - sustain);
            }

            Array.Sort(damageSamples);
            var finiteSurvival = survivalSamples.Where(double.IsFinite).Order().ToArray();
            var baseReport = ToReport(loadout, breakdown);
            return new SimulationReport
            {
                ExpectedDps = baseReport.ExpectedDps,
                UnmitigatedDps = baseReport.UnmitigatedDps,
                TimeToKillSeconds = baseReport.TimeToKillSeconds,
                EffectiveHealth = baseReport.EffectiveHealth,
                IncomingDps = baseReport.IncomingDps,
                SustainPerSecond = baseReport.SustainPerSecond,
                TimeToDeathSeconds = baseReport.TimeToDeathSeconds,
                HitChance = baseReport.HitChance,
                CritChance = baseReport.CritChance,
                UnsupportedEffects = baseReport.UnsupportedEffects,
                MonteCarloDpsMean = damageSamples.Average(),
                MonteCarloDpsLow95 = Percentile(damageSamples, 0.025),
                MonteCarloDpsHigh95 = Percentile(damageSamples, 0.975),
                MonteCarloSurvivalMean = finiteSurvival.Length == 0 ? double.PositiveInfinity : finiteSurvival.Average()
            };
        }, cancellationToken);
    }

    private static CombatBreakdown BuildBreakdown(CalculatedLoadout loadout, CombatScenario scenario, SkillRotationDefinition rotation)
    {
        var stats = loadout.Stats;
        var offensiveStats = stats.Clone();
        foreach (var (key, value) in rotation.FlatDamage.Values)
            if ((key.StartsWith("damage.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".percent", StringComparison.OrdinalIgnoreCase)) ||
                key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("rr.", StringComparison.OrdinalIgnoreCase))
                offensiveStats.Add(key, value);
        var damage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var dotDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var activationRate = Math.Clamp(rotation.ActivationRate, 0, 1);
        var defaultStrikes = Math.Max(1, stats["averageWeaponStrikesPerAction"]);
        var weaponPool = NormalizeWeaponPool(rotation.WeaponPoolAttacks);
        var weaponPoolChance = weaponPool.Sum(x => x.ChanceFraction);
        foreach (var type in StatKeys.DamageTypes)
        {
            var normalWeaponHit = stats[StatKeys.WeaponDamage(type)] + stats[StatKeys.DamageFlat(type)];
            var legacyPoolMultiplier = weaponPool.Count == 0 ? Math.Max(0, rotation.WeaponPoolDamageMultiplier) : 1;
            var selectedSkillHit = normalWeaponHit * rotation.WeaponDamagePercent / 100d * legacyPoolMultiplier +
                                   rotation.FlatDamage[StatKeys.DamageFlat(type)] +
                                   (weaponPool.Count == 0 ? rotation.WeaponPoolFlatDamage[StatKeys.DamageFlat(type)] : 0);
            var selectedPerStrike = normalWeaponHit + (selectedSkillHit - normalWeaponHit) * activationRate;
            var directPerAction = selectedPerStrike * defaultStrikes * (1 - weaponPoolChance);
            foreach (var entry in weaponPool)
            {
                var wpsHit = normalWeaponHit * rotation.WeaponDamagePercent / 100d *
                             Math.Max(0, entry.Attack.WeaponDamageMultiplier) +
                             rotation.FlatDamage[StatKeys.DamageFlat(type)] +
                             entry.Attack.FlatDamage[StatKeys.DamageFlat(type)];
                directPerAction += entry.ChanceFraction * wpsHit * Math.Max(1, entry.Attack.WeaponStrikes);
            }
            damage[type] = directPerAction;

            var normalDot = stats[StatKeys.DamageDot(type)];
            var selectedDot = normalDot + rotation.FlatDamage[StatKeys.DamageDot(type)] +
                              (weaponPool.Count == 0 ? rotation.WeaponPoolFlatDamage[StatKeys.DamageDot(type)] : 0);
            var dotPerAction = selectedDot * defaultStrikes * (1 - weaponPoolChance);
            foreach (var entry in weaponPool)
                dotPerAction += entry.ChanceFraction *
                    (normalDot + rotation.FlatDamage[StatKeys.DamageDot(type)] +
                     entry.Attack.FlatDamage[StatKeys.DamageDot(type)]) * Math.Max(1, entry.Attack.WeaponStrikes);
            dotDamage[type] = dotPerAction;
        }

        ApplyConversions(damage, offensiveStats);

        var rawDamagePerLandedHit = 0d;
        var sheetDamagePerHit = 0d;
        var rawDotDps = 0d;
        var sheetDotDps = 0d;
        foreach (var type in StatKeys.DamageTypes)
        {
            var boosted = damage[type] * Math.Max(0, 1 + offensiveStats[StatKeys.DamagePercent(type)] / 100d);
            sheetDamagePerHit += boosted;
            rawDamagePerLandedHit += ApplyEnemyMitigation(type, boosted, scenario, offensiveStats);
            var boostedDot = dotDamage[type] * Math.Max(0, 1 +
                (offensiveStats[StatKeys.DamagePercent(type)] + offensiveStats[StatKeys.DamageDotPercent(type)]) / 100d);
            sheetDotDps += boostedDot;
            rawDotDps += ApplyEnemyMitigation(type, boostedDot, scenario, offensiveStats);
        }

        var speedBonus = rotation.UsesAttackSpeed ? stats["attackSpeed"] : stats["castSpeed"];
        var actionSpeed = Math.Clamp(1 + speedBonus / 100d, 0.2, 2.0);
        var attacksPerSecond = Math.Max(0.05, rotation.BaseAttacksPerSecond * actionSpeed);
        if (rotation.CooldownSeconds > Epsilon)
            attacksPerSecond = Math.Min(attacksPerSecond, 1d / rotation.CooldownSeconds);
        var energyAvailable = Math.Max(0, stats["energyRegen"] + stats["energy"] / Math.Max(1, scenario.DurationSeconds));
        var energyNeeded = rotation.EnergyPerUse * attacksPerSecond;
        if (energyNeeded > Epsilon)
            attacksPerSecond *= Math.Min(1, energyAvailable / energyNeeded);

        var enemyDefensiveAbility = scenario.EnemyDefensiveAbility > 0
            ? scenario.EnemyDefensiveAbility
            : 2400 + scenario.EnemyLevel * 5;
        var (hitChance, critChance, critMultiplier) = ResolveHitAndCrit(stats["offensiveAbility"], enemyDefensiveAbility);
        var selectedSkillCritMultiplier = critMultiplier + Math.Max(0, rotation.FlatDamage["criticalDamage"] / 100d);
        var hitAndCritFactor = hitChance * (1 + critChance * (selectedSkillCritMultiplier - 1));
        var weaponDps = rawDamagePerLandedHit * attacksPerSecond * hitAndCritFactor + rawDotDps * hitChance;
        // This value mirrors the game's target-free sheet tooltip: no armor,
        // resistance, hit chance or enemy defensive ability is applied.
        var unmitigatedWeaponDps = sheetDamagePerHit * attacksPerSecond + sheetDotDps;

        var procModels = new List<ProcModel>();
        var procDps = 0d;
        foreach (var effect in loadout.Effects.Where(x => !x.IsDefensive))
        {
            var chance = Math.Clamp(effect.Chance, 0, 1);
            var eventFactor = effect.Trigger.ToLowerInvariant() switch
            {
                "onattack" => 1d,
                "oncrit" => critChance,
                _ => 0d
            };
            if (eventFactor <= 0) continue;
            var triggerRate = attacksPerSecond * chance * eventFactor;
            if (effect.CooldownSeconds > Epsilon)
                triggerRate = Math.Min(triggerRate, 1d / effect.CooldownSeconds);

            var procDamage = ResolveEffectDamage(effect.Damage, stats, scenario);
            procDps += triggerRate * procDamage;
            procModels.Add(new ProcModel(Math.Clamp(triggerRate / attacksPerSecond, 0, 1), procDamage));
        }

        var targetCount = Math.Max(1, scenario.TargetCount);
        var expectedDps = (weaponDps + procDps) * targetCount;
        // Grim Dawn's target-free sheet value contains direct skill/weapon damage,
        // but no enemy DA exists to roll a crit against and random item procs are
        // not treated as guaranteed DPS.  They remain in ExpectedDps above.
        var unmitigatedDps = unmitigatedWeaponDps * targetCount;
        var timeToKill = expectedDps <= Epsilon
            ? double.PositiveInfinity
            : Math.Max(1, scenario.EnemyHealth) * Math.Max(1, scenario.TargetCount) / expectedDps;
        var health = Math.Max(1, stats["health"]);
        var enemyHit = ResolveHitAndCrit(scenario.EnemyOffensiveAbility, stats["defensiveAbility"]).HitChance;
        var avoidance = Math.Clamp(stats["dodgeChance"] / 100d, 0, 0.8);
        var blockChance = Math.Clamp(stats["blockChance"] / 100d, 0, 0.9);
        var blockAmount = Math.Max(0, stats["blockAmount"]);
        var damageTakenPerHit = ResolveIncomingDamagePerHit(stats, scenario);
        var afterAvoidance = damageTakenPerHit * enemyHit * (1 - avoidance);
        var afterBlock = Math.Max(0, afterAvoidance - blockChance * Math.Min(blockAmount, afterAvoidance));
        var incomingDps = afterBlock * Math.Max(0, scenario.IncomingDamage.HitsPerSecond);
        var lifeSteal = Math.Clamp(stats["lifeSteal"] / 100d, 0, 1) * Math.Clamp(1 - scenario.EnemyLifeLeechResistance / 100d, 0, 1);
        var healthRegen = Math.Max(0, stats["healthRegen"]);
        var sustain = healthRegen + expectedDps * lifeSteal;
        var rawIncoming = RawIncomingPerHit(scenario) * Math.Max(0, scenario.IncomingDamage.HitsPerSecond);
        var effectiveHealth = incomingDps <= Epsilon ? double.PositiveInfinity : health * rawIncoming / incomingDps;
        var timeToDeath = incomingDps <= sustain + Epsilon ? double.PositiveInfinity : health / (incomingDps - sustain);

        return new CombatBreakdown(
            expectedDps, unmitigatedDps, timeToKill, effectiveHealth, incomingDps, sustain, timeToDeath,
            attacksPerSecond, rawDamagePerLandedHit, hitChance, critChance, selectedSkillCritMultiplier,
            enemyHit, avoidance, blockChance, blockAmount, damageTakenPerHit, health, healthRegen, lifeSteal, procModels);
    }

    private static IReadOnlyList<NormalizedWeaponPoolAttack> NormalizeWeaponPool(
        IReadOnlyList<WeaponPoolAttackDefinition> attacks)
    {
        var positive = attacks.Where(x => x.Chance > 0).ToArray();
        var total = positive.Sum(x => x.Chance);
        if (total <= Epsilon) return [];
        var scale = total > 100 ? 1d / total : 0.01d;
        return positive.Select(x => new NormalizedWeaponPoolAttack(x, x.Chance * scale)).ToArray();
    }

    private static void ApplyConversions(Dictionary<string, double> damage, StatBlock stats)
    {
        var original = new Dictionary<string, double>(damage, StringComparer.OrdinalIgnoreCase);
        foreach (var from in StatKeys.DamageTypes)
        {
            var conversions = StatKeys.DamageTypes
                .Where(to => !to.Equals(from, StringComparison.OrdinalIgnoreCase))
                .Select(to => (To: to, Percent: Math.Max(0, stats[$"conversion.{from}.{to}"])))
                .Where(x => x.Percent > Epsilon)
                .ToArray();
            var total = conversions.Sum(x => x.Percent);
            if (total <= Epsilon) continue;
            var scale = total > 100 ? 100 / total : 1;
            var convertedFraction = Math.Min(1, total / 100d);
            damage[from] -= original[from] * convertedFraction;
            foreach (var conversion in conversions)
                damage[conversion.To] += original[from] * conversion.Percent * scale / 100d;
        }
    }

    private static double ResolveEnemyResistance(string type, CombatScenario scenario, StatBlock stats)
    {
        var baseResistance = scenario.EnemyResistances.GetValueOrDefault(type);
        var reducedPercent = Math.Clamp(stats[$"rr.{type}.percent"] / 100d, 0, 1);
        var flatReduction = stats[$"rr.{type}.flat"];
        var stackedReduction = stats[$"rr.{type}.stacking"];
        return (baseResistance - flatReduction) * (1 - reducedPercent) - stackedReduction;
    }

    private static double ApplyEnemyMitigation(string type, double rawDamage, CombatScenario scenario, StatBlock stats)
    {
        var resistance = ResolveEnemyResistance(type, scenario, stats);
        var result = rawDamage * Math.Max(0, 1 - resistance / 100d);
        if (type.Equals("physical", StringComparison.OrdinalIgnoreCase))
        {
            var absorption = Math.Clamp(scenario.EnemyArmorAbsorption / 100d, 0, 1);
            result = Math.Max(0, result - Math.Max(0, scenario.EnemyArmor) * absorption);
        }
        return result;
    }

    private static (double HitChance, double CritChance, double CritMultiplier) ResolveHitAndCrit(double offensiveAbility, double defensiveAbility)
    {
        offensiveAbility = Math.Max(1, offensiveAbility);
        defensiveAbility = Math.Max(1, defensiveAbility);
        // records/game/combatformulas.dbr probabilityToHitEquation, simplified.
        var pth = 315 * offensiveAbility / (3.5 * offensiveAbility + defensiveAbility) +
                  0.02275 * (offensiveAbility - defensiveAbility) + 20;
        var hitChance = Math.Clamp(pth / 100d, 0.55, 1);
        var critChance = Math.Clamp((pth - 100) / 100d, 0, 0.35);
        var critMultiplier = 1 + Math.Clamp((pth - 90) / 500d, 0, 0.5);
        return (hitChance, critChance, critMultiplier);
    }

    private static double ResolveEffectDamage(
        StatBlock effect,
        StatBlock owner,
        CombatScenario scenario,
        bool applyEnemyMitigation = true)
    {
        var total = 0d;
        foreach (var type in StatKeys.DamageTypes)
        {
            var raw = effect[StatKeys.DamageFlat(type)] + effect[type];
            var boosted = raw * Math.Max(0, 1 + owner[StatKeys.DamagePercent(type)] / 100d);
            total += applyEnemyMitigation ? ApplyEnemyMitigation(type, boosted, scenario, owner) : boosted;
        }
        return total;
    }

    private static double ResolveIncomingDamagePerHit(StatBlock stats, CombatScenario scenario)
    {
        var incoming = scenario.IncomingDamage;
        var total = 0d;
        foreach (var type in StatKeys.DamageTypes)
        {
            var raw = type switch
            {
                "physical" => incoming.PhysicalPerHit,
                "pierce" => incoming.PiercePerHit,
                "fire" => incoming.FirePerHit,
                "cold" => incoming.ColdPerHit,
                "lightning" => incoming.LightningPerHit,
                "acid" => incoming.AcidPerHit,
                "vitality" => incoming.VitalityPerHit,
                "aether" => incoming.AetherPerHit,
                "chaos" => incoming.ChaosPerHit,
                _ => 0
            };
            var cap = 80 + stats[StatKeys.MaxResistance(type)];
            var resistance = Math.Clamp(stats[StatKeys.Resistance(type)], -100, cap);
            var afterResistance = raw * (1 - resistance / 100d);
            if (type == "physical")
            {
                var absorption = Math.Clamp(stats["armorAbsorption"] / 100d, 0, 1);
                afterResistance = Math.Max(0, afterResistance - stats["armor"] * absorption);
            }
            total += afterResistance;
        }
        return total;
    }

    private static double RawIncomingPerHit(CombatScenario scenario)
    {
        var x = scenario.IncomingDamage;
        return x.PhysicalPerHit + x.PiercePerHit + x.FirePerHit + x.ColdPerHit + x.LightningPerHit +
               x.AcidPerHit + x.VitalityPerHit + x.AetherPerHit + x.ChaosPerHit;
    }

    private static SimulationReport ToReport(CalculatedLoadout loadout, CombatBreakdown x) => new()
    {
        ExpectedDps = x.ExpectedDps,
        UnmitigatedDps = x.UnmitigatedDps,
        TimeToKillSeconds = x.TimeToKillSeconds,
        EffectiveHealth = x.EffectiveHealth,
        IncomingDps = x.IncomingDps,
        SustainPerSecond = x.SustainPerSecond,
        TimeToDeathSeconds = x.TimeToDeathSeconds,
        HitChance = x.HitChance,
        CritChance = x.CritChance,
        UnsupportedEffects = loadout.UnsupportedEffects
    };

    private static int SampleEventCount(Random rng, double expected)
    {
        var whole = (int)Math.Floor(Math.Max(0, expected));
        return whole + (rng.NextDouble() < expected - whole ? 1 : 0);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private sealed record ProcModel(double ChancePerAttack, double Damage);
    private sealed record NormalizedWeaponPoolAttack(WeaponPoolAttackDefinition Attack, double ChanceFraction);
    private sealed record CombatBreakdown(
        double ExpectedDps,
        double UnmitigatedDps,
        double TimeToKillSeconds,
        double EffectiveHealth,
        double IncomingDps,
        double SustainPerSecond,
        double TimeToDeathSeconds,
        double AttacksPerSecond,
        double DamagePerLandedHit,
        double HitChance,
        double CritChance,
        double CritMultiplier,
        double EnemyHitChance,
        double AvoidanceChance,
        double BlockChance,
        double BlockAmount,
        double DamageTakenPerLandedHit,
        double Health,
        double HealthRegen,
        double EffectiveLifeSteal,
        IReadOnlyList<ProcModel> Procs);
}
