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
        return ToReport(loadout, rotation, breakdown);
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
            for (var trial = 0; trial < trials; trial++)
            {
                if ((trial & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sample = breakdown.Combat.Sample(rng);
                damageSamples[trial] = sample.Dps;
                survivalSamples[trial] = sample.Survival;
            }

            Array.Sort(damageSamples);
            var finiteSurvival = survivalSamples.Where(double.IsFinite).Order().ToArray();
            var baseReport = ToReport(loadout, rotation, breakdown);
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
        var sheetDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sheetDotDamage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var activationRate = Math.Clamp(rotation.ActivationRate, 0, 1);
        var weaponDamageScale = Math.Max(0, rotation.WeaponDamagePercent) / 100d;
        var defaultStrikes = weaponDamageScale > 0 ? Math.Max(1, stats["averageWeaponStrikesPerAction"]) : 1;
        var piercingWeapons = ResolveArmorPiercingWeapons(loadout);
        foreach (var type in StatKeys.DamageTypes)
        {
            var normalWeaponHit = stats[StatKeys.WeaponDamage(type)] + stats[StatKeys.DamageFlat(type)];
            var sheetSkillHit = normalWeaponHit * weaponDamageScale + rotation.FlatDamage[StatKeys.DamageFlat(type)];
            // The game's panel describes the selected attack; WPS are combat replacements.
            var sheetPerStrike = normalWeaponHit + (sheetSkillHit - normalWeaponHit) * activationRate;
            sheetDamage[type] = sheetPerStrike * defaultStrikes;
            var sheetSelectedDot = stats[StatKeys.DamageDot(type)] * weaponDamageScale + rotation.FlatDamage[StatKeys.DamageDot(type)];
            // The panel's duplicate-DoT filtering differs from its direct collector.
            var sheetDotWeight = defaultStrikes > 1 ? defaultStrikes / 2d : 1;
            sheetDotDamage[type] = sheetSelectedDot * sheetDotWeight;
        }

        Dictionary<string, double> localSheetDamageBonus;
        if (weaponDamageScale > 0 && piercingWeapons.Any(x => x.ArmorPiercing > 0))
        {
            var sharedPhysical = stats[StatKeys.WeaponDamage("physical")] + stats[StatKeys.DamageFlat("physical")] -
                                 piercingWeapons.Average(x => x.PhysicalDamage);
            var sheetPackets = new List<(double Damage, double ArmorPiercing)>();
            foreach (var weapon in piercingWeapons)
            {
                // Each hand collects shared/native flat before its 0.75 weighting.
                var normal = weapon.PhysicalDamage + sharedPhysical;
                var skill = normal * weaponDamageScale + rotation.FlatDamage[StatKeys.DamageFlat("physical")];
                var sheet = (normal + (skill - normal) * activationRate) * defaultStrikes / piercingWeapons.Count;
                var piercing = ArmorPiercingPercent(weapon.ArmorPiercing, rotation.FlatDamage, stats);
                sheetPackets.Add((sheet, piercing));
            }
            localSheetDamageBonus = ConvertPhysicalPackets(sheetDamage, sheetPackets, rotation.FlatDamage, stats);
        }
        else localSheetDamageBonus = DamageConversion.Apply(sheetDamage, rotation.FlatDamage, stats);

        var sheetDamagePerHit = 0d;
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
            var sheetBoosted = Math.Max(0, sheetDamage[type] * damageMultiplier + localSheetDamageBonus[type]);
            sheetDamagePerHit += sheetBoosted;
            // Burn/poison/etc. have their own damage bonuses. Their associated
            // direct type (fire/acid/etc.) does not also boost the DoT.
            var dotMultiplier = Math.Max(0, 1 +
                (allDamageBonus + offensiveStats[StatKeys.DamageDotPercent(type)]) / 100d);
            var sheetBoostedDot = sheetDotDamage[type] * dotMultiplier;
            sheetDotDps += sheetBoostedDot;
        }

        var totalDamageFactor = Math.Max(0, 1 + offensiveStats["damage.total.multiplierPercent"] / 100d);
        sheetDamagePerHit *= totalDamageFactor;
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
            // Both weapon IDs enter one secondary collector. Its first positive
            // piercing carrier absorbs every same-region physical source, including
            // shared/native flat and the other weapon, before conversion.
            var secondaryPiercing = secondaryScale > 0
                ? piercingWeapons.FirstOrDefault(x => x.ArmorPiercing > 0)?.ArmorPiercing ?? 0
                : 0;
            var secondaryLocalBonus = DamageConversion.Apply(secondaryDamage, secondary.FlatDamage, stats,
                ArmorPiercingPercent(secondaryPiercing, secondary.FlatDamage, stats));
            var secondaryTotal = Math.Max(0, 1 + secondaryStats["damage.total.multiplierPercent"] / 100d);
            foreach (var type in StatKeys.DamageTypes)
            {
                var direct = Math.Max(0, secondaryDamage[type] * Math.Max(0, 1 +
                    (stats[StatKeys.DamagePercent(type)] + stats["damage.total.percent"]) / 100d) +
                    secondaryLocalBonus[type]) * secondaryTotal;
                sheetDamagePerHit += direct;
                var dot = (stats[StatKeys.DamageDot(type)] * secondaryScale + secondary.FlatDamage[StatKeys.DamageDot(type)]) *
                          activationRate * Math.Max(0, 1 + (secondaryStats[StatKeys.DamageDotPercent(type)] +
                          secondaryStats["damage.total.percent"]) / 100d) * secondaryTotal;
                sheetDotDps += dot;
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
        var usableEnergy = Math.Max(0, stats["energy"] - Math.Max(0, stats["energyReserved"]));
        var energyAvailable = Math.Max(0, stats["energyRegen"] + usableEnergy / Math.Max(1, scenario.DurationSeconds));
        var energyNeeded = energyPerUse * attacksPerSecond;
        if (energyNeeded > Epsilon)
            attacksPerSecond *= Math.Min(1, energyAvailable / energyNeeded);

        // Preserve the empirically verified target-free panel collector above.
        // Target mechanics use independent packets, shared by expectation and MC.
        var panelDps = (sheetDamagePerHit + sheetDotDps) * sheetAttacksPerSecond;
        var evaluation = new CombatEvaluation(loadout, scenario, rotation, attacksPerSecond);
        var health = Math.Max(1, stats["health"]);
        var regen = Math.Max(0, stats["healthRegen"]);
        var sustain = regen + evaluation.LifeStealPerSecond;
        var netIncoming = evaluation.IncomingDps - sustain;
        return new CombatBreakdown(evaluation.ExpectedDps, evaluation.RetaliationDps, panelDps,
            evaluation.ExpectedDps <= Epsilon ? double.PositiveInfinity : Math.Max(1, scenario.EnemyHealth) * Math.Max(1, scenario.TargetCount) / evaluation.ExpectedDps,
            netIncoming <= Epsilon ? double.PositiveInfinity : health * evaluation.RawIncomingDps / netIncoming,
            evaluation.IncomingDps, sustain,
            netIncoming <= Epsilon ? double.PositiveInfinity : health / netIncoming,
            evaluation.MainHit.HitChance, evaluation.MainHit.CritChance,
            health, evaluation.RawIncomingDps, regen, evaluation.EffectiveLifeSteal, evaluation.LifeStealEligibleDps, evaluation);
    }

    private static IReadOnlyList<ArmorPiercingWeapon> ResolveArmorPiercingWeapons(CalculatedLoadout loadout)
    {
        var result = new List<ArmorPiercingWeapon>();
        foreach (var slot in new[] { EquipmentSlot.MainHand, EquipmentSlot.OffHand })
        {
            if (!loadout.Slots.TryGetValue(slot, out var variant) || variant.Item.Slot != EquipmentSlot.MainHand) continue;
            var local = variant.CombinedStats;
            var basePhysical = local[StatKeys.WeaponDamage("physical")];
            result.Add(new(basePhysical + local[StatKeys.DamageFlat("physical")],
                basePhysical > 0 ? Math.Max(0, local[StatKeys.WeaponArmorPiercing]) : 0));
        }
        return result;
    }

    private static double ArmorPiercingPercent(double weaponRatio, StatBlock skill, StatBlock global) =>
        Math.Clamp(weaponRatio * (1 + (skill["armorPiercingModifier"] + global["armorPiercingModifier"]) / 100d), 0, 100);

    private static Dictionary<string, double> ConvertPhysicalPackets(
        Dictionary<string, double> damage, IEnumerable<(double Damage, double ArmorPiercing)> physicalPackets,
        StatBlock skillModifiers, StatBlock globalStats)
    {
        damage["physical"] = 0;
        var localBonus = DamageConversion.Apply(damage, skillModifiers, globalStats);
        foreach (var packet in physicalPackets)
        {
            var physical = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["physical"] = packet.Damage };
            var packetBonus = DamageConversion.Apply(physical, skillModifiers, globalStats, packet.ArmorPiercing);
            foreach (var type in StatKeys.DamageTypes)
            {
                damage[type] += physical[type];
                localBonus[type] += packetBonus[type];
            }
        }
        return localBonus;
    }

    private static SimulationReport ToReport(CalculatedLoadout loadout, SkillRotationDefinition rotation, CombatBreakdown x) => new()
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
        UnsupportedEffects = loadout.UnsupportedEffects.Concat(CalculationDiagnostics.RotationDiagnostics(loadout, rotation)).Concat(x.Combat.Diagnostics).Distinct().ToArray()
    };

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private sealed record ArmorPiercingWeapon(double PhysicalDamage, double ArmorPiercing);
    private sealed record CombatBreakdown(
        double ExpectedDps,
        double RetaliationDps,
        double UnmitigatedDps,
        double TimeToKillSeconds,
        double EffectiveHealth,
        double IncomingDps,
        double SustainPerSecond,
        double TimeToDeathSeconds,
        double HitChance,
        double CritChance,
        double Health,
        double RawIncomingDps,
        double HealthRegen,
        double EffectiveLifeSteal,
        double LifeStealEligibleDps,
        CombatEvaluation Combat);
}
