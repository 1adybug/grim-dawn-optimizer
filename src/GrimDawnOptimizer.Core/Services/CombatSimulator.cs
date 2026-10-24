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
                var lifeStealEligibleDamage = 0d;
                for (var attack = 0; attack < attacks; attack++)
                {
                    if (rng.NextDouble() > breakdown.HitChance) continue;
                    var variance = 0.9 + rng.NextDouble() * 0.2;
                    var critical = rng.NextDouble() < breakdown.CritChance;
                    var directDamage = breakdown.DamagePerLandedHit * variance * (critical ? breakdown.CritMultiplier : 1);
                    totalDamage += directDamage;
                    lifeStealEligibleDamage += directDamage;

                    foreach (var proc in breakdown.Procs)
                        if (rng.NextDouble() < proc.ChancePerAttack)
                            totalDamage += proc.Damage;
                }

                var incomingHits = SampleEventCount(rng, DifficultyRules.Incoming(scenario).HitsPerSecond * duration);
                var received = 0d;
                var retaliationDamage = 0d;
                for (var hit = 0; hit < incomingHits; hit++)
                {
                    if (rng.NextDouble() > breakdown.EnemyHitChance) continue;
                    if (rng.NextDouble() < breakdown.AvoidanceChance) continue;
                    retaliationDamage += breakdown.RetaliationDamagePerTrigger;
                    var damage = breakdown.DamageTakenPerLandedHit * (0.9 + rng.NextDouble() * 0.2);
                    if (rng.NextDouble() < breakdown.BlockChance)
                        damage = Math.Max(0, damage - breakdown.BlockAmount);
                    received += damage;
                }

                var activeDamagePerSecond = totalDamage / duration;
                damageSamples[trial] = activeDamagePerSecond + retaliationDamage / duration;
                var incomingPerSecond = received / duration;
                var sustain = breakdown.HealthRegen +
                              lifeStealEligibleDamage / duration * breakdown.EffectiveLifeSteal;
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
                RetaliationDps = baseReport.RetaliationDps,
                UnmitigatedDps = baseReport.UnmitigatedDps,
                TimeToKillSeconds = baseReport.TimeToKillSeconds,
                EffectiveHealth = baseReport.EffectiveHealth,
                IncomingDps = baseReport.IncomingDps,
                SustainPerSecond = baseReport.SustainPerSecond,
                Health = baseReport.Health,
                RawIncomingDps = baseReport.RawIncomingDps,
                HealthRegenPerSecond = baseReport.HealthRegenPerSecond,
                EffectiveLifeSteal = baseReport.EffectiveLifeSteal,
                LifeStealEligibleDps = baseReport.LifeStealEligibleDps,
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
            if ((key.StartsWith("damage.", StringComparison.OrdinalIgnoreCase) &&
                 (key.EndsWith(".percent", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".dotPercent", StringComparison.OrdinalIgnoreCase))) ||
                key.Equals("damage.total.multiplierPercent", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("rr.", StringComparison.OrdinalIgnoreCase))
                offensiveStats.Add(key, value);
        var damage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var dotDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sheetDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sheetDotDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var activationRate = Math.Clamp(rotation.ActivationRate, 0, 1);
        var weaponDamageScale = Math.Max(0, rotation.WeaponDamagePercent) / 100d;
        // Dual-wield weapon actions do not add strikes to a pure spell.
        var defaultStrikes = weaponDamageScale > 0 ? Math.Max(1, stats["averageWeaponStrikesPerAction"]) : 1;
        var weaponPool = NormalizeWeaponPool(rotation.WeaponPoolAttacks);
        var weaponPoolChance = weaponPool.Sum(x => x.ChanceFraction);
        foreach (var type in StatKeys.DamageTypes)
        {
            var normalWeaponHit = stats[StatKeys.WeaponDamage(type)] + stats[StatKeys.DamageFlat(type)];
            var legacyPoolMultiplier = weaponPool.Count == 0 ? Math.Max(0, rotation.WeaponPoolDamageMultiplier) : 1;
            var sheetSkillHit = normalWeaponHit * weaponDamageScale +
                                rotation.FlatDamage[StatKeys.DamageFlat(type)];
            var selectedSkillHit = sheetSkillHit +
                                   normalWeaponHit * weaponDamageScale * (legacyPoolMultiplier - 1) +
                                   (weaponPool.Count == 0 ? rotation.WeaponPoolFlatDamage[StatKeys.DamageFlat(type)] : 0);
            var selectedPerStrike = normalWeaponHit + (selectedSkillHit - normalWeaponHit) * activationRate;
            // The character-sheet comparison value describes the selected attack
            // itself. Weapon-pool skills replace attacks in combat but are not
            // folded into the target-free sheet DPS shown by the game.
            var sheetPerStrike = normalWeaponHit + (sheetSkillHit - normalWeaponHit) * activationRate;
            sheetDamage[type] = sheetPerStrike * defaultStrikes;
            var directPerAction = selectedPerStrike * defaultStrikes * (1 - weaponPoolChance);
            foreach (var entry in weaponPool)
            {
                var wpsHit = normalWeaponHit * weaponDamageScale *
                             Math.Max(0, entry.Attack.WeaponDamageMultiplier) +
                             rotation.FlatDamage[StatKeys.DamageFlat(type)] +
                             entry.Attack.FlatDamage[StatKeys.DamageFlat(type)];
                directPerAction += entry.ChanceFraction * wpsHit * Math.Max(1, entry.Attack.WeaponStrikes);
            }
            damage[type] = directPerAction;

            var normalDot = stats[StatKeys.DamageDot(type)];
            // Equipment DoT belongs to the weapon component, just like flat
            // direct damage. A 0% weapon spell inherits none of it; the skill's
            // own DoT is independent of its weapon-damage percentage.
            var sheetSelectedDot = normalDot * weaponDamageScale + rotation.FlatDamage[StatKeys.DamageDot(type)];
            var selectedDot = sheetSelectedDot +
                              normalDot * weaponDamageScale * (legacyPoolMultiplier - 1) +
                              (weaponPool.Count == 0 ? rotation.WeaponPoolFlatDamage[StatKeys.DamageDot(type)] : 0);
            // CalculateDps filters duplicate duration damage sources between
            // hands before applying its dual-wield 0.75-per-hand weighting.
            // The damage tooltip does not apply this filter, so multiplying
            // that tooltip's mean by APS does not reproduce the DPS field.
            var sheetDotWeight = defaultStrikes > 1 ? defaultStrikes / 2d : 1;
            sheetDotDamage[type] = sheetSelectedDot * sheetDotWeight;
            var dotPerAction = selectedDot * (1 - weaponPoolChance);
            foreach (var entry in weaponPool)
                dotPerAction += entry.ChanceFraction *
                    (normalDot * weaponDamageScale * Math.Max(0, entry.Attack.WeaponDamageMultiplier) +
                     rotation.FlatDamage[StatKeys.DamageDot(type)] +
                     entry.Attack.FlatDamage[StatKeys.DamageDot(type)]);
            dotDamage[type] = dotPerAction;
        }

        var localDamageBonus = DamageConversion.Apply(damage, rotation.FlatDamage, stats);
        var localSheetDamageBonus = DamageConversion.Apply(sheetDamage, rotation.FlatDamage, stats);

        var rawDamagePerLandedHit = 0d;
        var sheetDamagePerHit = 0d;
        var rawDotDps = 0d;
        var sheetDotDps = 0d;
        // offensiveTotalDamageModifier is the item's "+% All Damage": it joins
        // each type's additive bonus. Only offensiveDamageMultModifier is a
        // separate multiplier. Multiplying by All Damage overvalues such items
        // and can reverse the predicted direction of an equipment swap.
        var allDamageBonus = offensiveStats["damage.total.percent"];
        foreach (var type in StatKeys.DamageTypes)
        {
            var typeBonus = stats[StatKeys.DamagePercent(type)] + stats["damage.total.percent"];
            var damageMultiplier = Math.Max(0, 1 + typeBonus / 100d);
            var boosted = Math.Max(0, damage[type] * damageMultiplier + localDamageBonus[type]);
            var sheetBoosted = Math.Max(0, sheetDamage[type] * damageMultiplier + localSheetDamageBonus[type]);
            sheetDamagePerHit += sheetBoosted;
            rawDamagePerLandedHit += ApplyEnemyMitigation(type, boosted, scenario, offensiveStats);
            // Burn/poison/etc. have their own damage bonuses. Their associated
            // direct type (fire/acid/etc.) does not also boost the DoT.
            var dotMultiplier = Math.Max(0, 1 +
                (allDamageBonus + offensiveStats[StatKeys.DamageDotPercent(type)]) / 100d);
            var boostedDot = dotDamage[type] * dotMultiplier;
            var sheetBoostedDot = sheetDotDamage[type] * dotMultiplier;
            sheetDotDps += sheetBoostedDot;
            rawDotDps += ApplyEnemyMitigation(type, boostedDot, scenario, offensiveStats);
        }

        var totalDamageFactor = Math.Max(0, 1 + offensiveStats["damage.total.multiplierPercent"] / 100d);
        rawDamagePerLandedHit *= totalDamageFactor;
        sheetDamagePerHit *= totalDamageFactor;
        rawDotDps *= totalDamageFactor;
        sheetDotDps *= totalDamageFactor;

        // A secondary explosion carries a separate payload. The collector uses
        // both weapon IDs at full weapon-damage percentage, with shared flat
        // damage once, rather than the parent's averaged dual-strike action.
        foreach (var secondary in rotation.SecondaryAttacks)
        {
            var secondaryStats = stats.Clone();
            foreach (var (key, value) in secondary.FlatDamage.Values)
                if ((key.StartsWith("damage.", StringComparison.OrdinalIgnoreCase) &&
                     (key.EndsWith(".percent", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".dotPercent", StringComparison.OrdinalIgnoreCase))) ||
                    key.Equals("damage.total.multiplierPercent", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("conversion.", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("rr.", StringComparison.OrdinalIgnoreCase))
                    secondaryStats.Add(key, value);
            var secondaryScale = Math.Max(0, secondary.WeaponDamagePercent) / 100d;
            var secondaryDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in StatKeys.DamageTypes)
                secondaryDamage[type] = ((stats[StatKeys.WeaponDamage(type)] + stats[StatKeys.DamageFlat(type)] +
                                          stats[$"secondaryWeaponDamageCorrection.{type}"]) * secondaryScale +
                                         secondary.FlatDamage[StatKeys.DamageFlat(type)]) * activationRate;
            var secondaryLocalBonus = DamageConversion.Apply(secondaryDamage, secondary.FlatDamage, stats);
            var secondaryTotal = Math.Max(0, 1 + secondaryStats["damage.total.multiplierPercent"] / 100d);
            foreach (var type in StatKeys.DamageTypes)
            {
                var direct = Math.Max(0, secondaryDamage[type] * Math.Max(0, 1 +
                    (stats[StatKeys.DamagePercent(type)] + stats["damage.total.percent"]) / 100d) +
                    secondaryLocalBonus[type]) * secondaryTotal;
                sheetDamagePerHit += direct;
                rawDamagePerLandedHit += ApplyEnemyMitigation(type, direct, scenario, secondaryStats);
                var dot = (stats[StatKeys.DamageDot(type)] * secondaryScale + secondary.FlatDamage[StatKeys.DamageDot(type)]) *
                          activationRate * Math.Max(0, 1 + (secondaryStats[StatKeys.DamageDotPercent(type)] +
                          secondaryStats["damage.total.percent"]) / 100d) * secondaryTotal;
                sheetDotDps += dot;
                rawDotDps += ApplyEnemyMitigation(type, dot, scenario, secondaryStats);
            }
        }

        var speedBonus = rotation.UsesAttackSpeed ? stats["attackSpeed"] : stats["castSpeed"];
        var weaponSpeedFactor = rotation.UsesAttackSpeed && stats["weaponAttackSpeedFactor"] > 0
            ? stats["weaponAttackSpeedFactor"]
            : 1;
        // Grim Dawn applies the weapon/animation base before the global speed
        // bonus and caps the resulting character-sheet speed, not the bonus in
        // isolation. This matters most for ranged dual wielding.
        var speedCap = 2.0 + Math.Max(0, stats[rotation.UsesAttackSpeed ? "attackSpeed.maxBonus" : "castSpeed.maxBonus"]) / 100d;
        var actionSpeed = Math.Clamp(weaponSpeedFactor * (1 + speedBonus / 100d), 0.2, speedCap);
        // The engine caps Character::GetAttackSpeed before applying the ratio
        // between unarmed and equipped-weapon animation durations.
        if (rotation.UsesAttackSpeed && stats["weaponAttackAnimationRatio"] > 0)
            actionSpeed *= stats["weaponAttackAnimationRatio"];
        var attacksPerSecond = Math.Max(0.05, rotation.BaseAttacksPerSecond * actionSpeed);
        var cooldown = rotation.CooldownSeconds *
                       (1 - Math.Clamp(stats["cooldownReduction"] / 100d, 0, 0.8));
        if (cooldown > Epsilon)
            attacksPerSecond = Math.Min(attacksPerSecond, 1d / cooldown);
        // The sheet describes the attack at its actual speed. Resource limits
        // over a chosen fight duration affect sustained combat DPS only.
        var sheetAttacksPerSecond = attacksPerSecond;
        var energyPerUse = rotation.EnergyPerUse *
                           (1 - Math.Clamp(stats["energyCostReduction"] / 100d, 0, 0.8));
        var energyAvailable = Math.Max(0, stats["energyRegen"] + stats["energy"] / Math.Max(1, scenario.DurationSeconds));
        var energyNeeded = energyPerUse * attacksPerSecond;
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
        // The game includes the DoT rate in its displayed per-hit range, then
        // multiplies that entire range by attack frequency. Sustained combat
        // above keeps the separate, non-stacking DoT rate instead.
        var unmitigatedWeaponDps = (sheetDamagePerHit + sheetDotDps) * sheetAttacksPerSecond;

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

            var procDamage = ResolveEffectDamage(effect, stats, scenario);
            procDps += triggerRate * procDamage;
            procModels.Add(new ProcModel(Math.Clamp(triggerRate / attacksPerSecond, 0, 1), procDamage));
        }

        var targetCount = Math.Max(1, scenario.TargetCount);
        var activeExpectedDps = (weaponDps + procDps) * targetCount;
        var lifeStealEligibleDps = rawDamagePerLandedHit * attacksPerSecond * hitAndCritFactor * targetCount;
        // Grim Dawn's target-free sheet value contains direct skill/weapon damage,
        // but no enemy DA exists to roll a crit against and random item procs are
        // not treated as guaranteed DPS. They remain in the active combat DPS.
        // The character panel describes this attack, independently of how many
        // enemies a combat scenario assumes it will hit.
        var unmitigatedDps = unmitigatedWeaponDps;
        var health = Math.Max(1, stats["health"]);
        var enemyHit = ResolveHitAndCrit(scenario.EnemyOffensiveAbility, stats["defensiveAbility"]).HitChance;
        var avoidance = Math.Clamp(stats["dodgeChance"] / 100d, 0, 0.8);
        var blockChance = Math.Clamp(stats["blockChance"] / 100d, 0, 0.9);
        var recovery = Math.Max(0, stats["blockRecoverySeconds"]) *
                       (1 - Math.Clamp(stats["blockRecoveryReduction"] / 100d, 0, 0.9));
        if (recovery > Epsilon && DifficultyRules.Incoming(scenario).HitsPerSecond > Epsilon)
            blockChance = Math.Min(blockChance, 1d / (recovery * DifficultyRules.Incoming(scenario).HitsPerSecond));
        var blockAbsorption = stats["blockAbsorption"] > 0
            ? Math.Clamp(stats["blockAbsorption"] / 100d, 0, 1)
            : 1d;
        var blockAmount = Math.Max(0, stats["blockAmount"]) * blockAbsorption;
        var retaliationTriggerRate = Math.Max(0, DifficultyRules.Incoming(scenario).HitsPerSecond) * enemyHit * (1 - avoidance);
        var (retaliationDps, retaliationDamagePerTrigger) = ResolveRetaliation(
            stats, scenario, retaliationTriggerRate);
        var expectedDps = activeExpectedDps + retaliationDps;
        var timeToKill = expectedDps <= Epsilon
            ? double.PositiveInfinity
            : Math.Max(1, scenario.EnemyHealth) * Math.Max(1, scenario.TargetCount) / expectedDps;
        var damageTakenPerHit = ResolveIncomingDamagePerHit(stats, scenario);
        var afterAvoidance = damageTakenPerHit * enemyHit * (1 - avoidance);
        var afterBlock = Math.Max(0, afterAvoidance - blockChance * Math.Min(blockAmount, afterAvoidance));
        var incomingDps = afterBlock * Math.Max(0, DifficultyRules.Incoming(scenario).HitsPerSecond);
        var lifeSteal = Math.Clamp(stats["lifeSteal"] / 100d, 0, 1) * Math.Clamp(1 - scenario.EnemyLifeLeechResistance / 100d, 0, 1);
        var healthRegen = Math.Max(0, stats["healthRegen"]);
        // Passive retaliation is not attack damage and therefore cannot produce
        // health through global attack-damage conversion.
        var sustain = healthRegen + lifeStealEligibleDps * lifeSteal;
        var rawIncoming = RawIncomingPerHit(scenario) * Math.Max(0, DifficultyRules.Incoming(scenario).HitsPerSecond);
        var netIncomingDps = incomingDps - sustain;
        var effectiveHealth = netIncomingDps <= Epsilon ? double.PositiveInfinity : health * rawIncoming / netIncomingDps;
        var timeToDeath = netIncomingDps <= Epsilon ? double.PositiveInfinity : health / netIncomingDps;

        return new CombatBreakdown(
            expectedDps, retaliationDps, unmitigatedDps, timeToKill, effectiveHealth, incomingDps, sustain, timeToDeath,
            attacksPerSecond, rawDamagePerLandedHit, hitChance, critChance, selectedSkillCritMultiplier,
            enemyHit, avoidance, blockChance, blockAmount, damageTakenPerHit, retaliationDamagePerTrigger,
            health, rawIncoming, healthRegen, lifeSteal, lifeStealEligibleDps, procModels);
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
        TriggeredEffect triggeredEffect,
        StatBlock owner,
        CombatScenario scenario,
        bool applyEnemyMitigation = true)
    {
        var effect = triggeredEffect.Damage;
        var total = 0d;
        foreach (var type in StatKeys.DamageTypes)
        {
            var weapon = (owner[StatKeys.WeaponDamage(type)] + owner[StatKeys.DamageFlat(type)]) *
                         Math.Max(0, triggeredEffect.WeaponDamagePercent) / 100d;
            var raw = weapon + effect[StatKeys.DamageFlat(type)] + effect[type];
            var typeBonus = owner[StatKeys.DamagePercent(type)] + effect[StatKeys.DamagePercent(type)] +
                            owner["damage.total.percent"] + effect["damage.total.percent"];
            var boosted = raw * Math.Max(0, 1 + typeBonus / 100d);
            var dot = effect[$"damage.{type}.dotTotal"] * Math.Max(0, 1 +
                (owner["damage.total.percent"] + effect["damage.total.percent"] +
                 owner[StatKeys.DamageDotPercent(type)] + effect[StatKeys.DamageDotPercent(type)]) / 100d);
            var combined = boosted + dot;
            total += applyEnemyMitigation ? ApplyEnemyMitigation(type, combined, scenario, owner) : combined;
        }
        var totalMultiplier = owner["damage.total.multiplierPercent"] + effect["damage.total.multiplierPercent"];
        return total * Math.Max(0, 1 + totalMultiplier / 100d);
    }

    private static (double Dps, double DamagePerTrigger) ResolveRetaliation(
        StatBlock stats,
        CombatScenario scenario,
        double triggerRate)
    {
        if (triggerRate <= Epsilon || stats["retaliation.flatProxy"] <= Epsilon) return (0, 0);

        var directPerTrigger = 0d;
        var sustainedDotDps = 0d;
        var totalFactor = Math.Max(0, 1 + stats["retaliation.total.percent"] / 100d) *
                          Math.Max(0, 1 + stats["retaliation.total.multiplierPercent"] / 100d);
        foreach (var type in StatKeys.DamageTypes)
        {
            var typeFactor = Math.Max(0, 1 + stats[StatKeys.RetaliationPercent(type)] / 100d);
            var direct = stats[StatKeys.RetaliationFlat(type)] * typeFactor * totalFactor;
            directPerTrigger += ApplyEnemyMitigation(type, direct, scenario, stats);

            var rawDot = stats[StatKeys.RetaliationDot(type)];
            if (rawDot <= Epsilon) continue;
            var dotFactor = Math.Max(0, 1 +
                (stats[StatKeys.RetaliationPercent(type)] + stats[StatKeys.RetaliationDotPercent(type)]) / 100d);
            var dot = ApplyEnemyMitigation(type, rawDot * dotFactor * totalFactor, scenario, stats);
            var duration = Math.Max(1, stats[StatKeys.RetaliationDotTotal(type)] / rawDot);
            var uptime = Math.Clamp(triggerRate * duration, 0, 1);
            sustainedDotDps += dot * uptime;
        }

        var dps = directPerTrigger * triggerRate + sustainedDotDps;
        return (dps, dps / triggerRate);
    }

    private static double ResolveIncomingDamagePerHit(StatBlock stats, CombatScenario scenario)
    {
        var incoming = DifficultyRules.Incoming(scenario);
        var total = 0d;
        foreach (var type in StatKeys.IncomingDamageTypes)
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
                "bleeding" => incoming.BleedingPerHit,
                _ => 0
            };
            var cap = 80 + stats[StatKeys.MaxResistance(type)];
            var resistance = DifficultyRules.EffectiveResistance(stats, scenario, type);
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
        var x = DifficultyRules.Incoming(scenario);
        return x.PhysicalPerHit + x.PiercePerHit + x.FirePerHit + x.ColdPerHit + x.LightningPerHit +
               x.AcidPerHit + x.VitalityPerHit + x.AetherPerHit + x.ChaosPerHit + x.BleedingPerHit;
    }

    private static SimulationReport ToReport(CalculatedLoadout loadout, CombatBreakdown x) => new()
    {
        ExpectedDps = x.ExpectedDps,
        RetaliationDps = x.RetaliationDps,
        UnmitigatedDps = x.UnmitigatedDps,
        TimeToKillSeconds = x.TimeToKillSeconds,
        EffectiveHealth = x.EffectiveHealth,
        IncomingDps = x.IncomingDps,
        SustainPerSecond = x.SustainPerSecond,
        Health = x.Health,
        RawIncomingDps = x.RawIncomingDps,
        HealthRegenPerSecond = x.HealthRegen,
        EffectiveLifeSteal = x.EffectiveLifeSteal,
        LifeStealEligibleDps = x.LifeStealEligibleDps,
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
        double RetaliationDps,
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
        double RetaliationDamagePerTrigger,
        double Health,
        double RawIncomingDps,
        double HealthRegen,
        double EffectiveLifeSteal,
        double LifeStealEligibleDps,
        IReadOnlyList<ProcModel> Procs);
}
