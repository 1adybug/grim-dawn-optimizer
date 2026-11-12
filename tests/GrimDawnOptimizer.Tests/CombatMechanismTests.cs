using System.Text.Json;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class CombatMechanismTests
{
    [Fact]
    public void AbsorptionFollowsResistanceAndArmorWithIndependentPercentLayers()
    {
        var stats = Player(("armor", 200), ("armorAbsorption", 70), ("physicalResistance", 20));
        stats.Add(GameStatTranslator.Translate([
            Record("sphere", "Skill_BuffSelfToggled", ("damageAbsorptionPercent", 20)),
            Record("bulwark", "Skill_BuffSelfToggled", ("damageAbsorptionPercent", 25)),
            Record("seal", "Skill_BuffSelfToggled", ("damageAbsorption", 30)),
            Record("ascension", "Skill_BuffSelfDuration", ("damageAbsorption", 10))]));
        var scenario = Scenario(Physical(1000));
        var report = Run(stats, scenario);

        // 1000 * .8 resistance = 800; armor absorbs 140; .8 * .75; -40.
        Assert.Equal(356, report.IncomingDps, 8);
        Assert.Equal(10_000d / 356, report.TimeToDeathSeconds, 8);
        Assert.Equal(10_000d * 1000 / 356, report.EffectiveHealth, 8);
    }

    [Fact]
    public void AbsorptionModifiersOfOneBuffAddBeforeIndependentBuffsMultiply()
    {
        var root = Record("sphere", "Skill_BuffSelfToggled", ("damageAbsorptionPercent", 20));
        var child = Record("sphere-modifier", "Skill_Modifier", ("damageAbsorptionPercent", 10));
        var stats = Player();
        stats.Add(SkillRotationFactory.TranslatePersistentStats(root, 1, root));
        stats.Add(SkillRotationFactory.TranslatePersistentStats(child, 1, root));
        Assert.Equal(700, Run(stats, Scenario(Fire(1000))).IncomingDps, 8);
    }

    [Fact]
    public void ChanceFlatAbsorptionRetainsMagnitudeAndClampsEachOutcome()
    {
        var stats = Player();
        stats.AddCombatModifier(new("absorb", CombatModifierKind.FlatAbsorption, 200, Chance: .5));
        // Half the 100-point hits are fully absorbed; not all hits reduced by 100.
        Assert.Equal(50, Run(stats, Scenario(Fire(100))).IncomingDps, 8);
    }

    [Fact]
    public void FlatAbsorptionIsAppliedPerDamageTypeAndNeverCreatesHealing()
    {
        var stats = Player(("damageAbsorption", 60));
        var report = Run(stats, Scenario(Fire(100) with { ColdPerHit = 40 }));
        Assert.Equal(40, report.IncomingDps, 8);
        Assert.Equal(0, report.SustainPerSecond);
    }

    [Fact]
    public void FiniteShieldAndUnknownAbsorptionAreNotMistakenForPermanentFlatAbsorption()
    {
        var shield = Record("turtle", "Skill_BuffSelfShield", ("damageAbsorption", 5000));
        var unknown = Record("future", "Skill_BuffSelfToggled", ("damageAbsorptionUnknown", 20));
        Assert.Empty(GameStatTranslator.Translate([shield]).CombatModifiers);
        Assert.Contains("damageAbsorption", GameStatTranslator.FindUnsupportedCombatFields(shield));
        var missing = GameStatTranslator.FindUnsupportedCombatFields(unknown);
        Assert.Contains("damageAbsorptionUnknown", missing);
        Assert.Equal([CalculationDiagnostics.Survival], CalculationDiagnostics.AffectedMetrics(missing));
    }

    [Theory]
    [InlineData(40, .55, .43214285714285716, 0)]
    [InlineData(55, .55, .43214285714285716, 0)]
    [InlineData(70, .7, .7, 0)]
    [InlineData(90, .9, .9, 0)]
    [InlineData(95, .95, .955, .05263157894736842)]
    [InlineData(100, 1, 1.01, .1)]
    [InlineData(105, 1, 1.0142857142857142, .14285714285714285)]
    [InlineData(120, 1, 1.0375, .25)]
    [InlineData(130, 1, 1.0576923076923077, .3076923076923077)]
    [InlineData(135, 1, 1.0703703703703704, .3333333333333333)]
    [InlineData(150, 1, 1.1133333333333333, .4)]
    public void PthUsesCurrentCriticalIntervalsAndLowPthDamagePenalty(double pth, double hit, double damageFactor, double crit)
    {
        var distribution = HitRules.FromProbabilityToHit(pth);
        Assert.Equal(hit, distribution.HitChance, 10);
        Assert.Equal(damageFactor, distribution.MeanMultiplier, 10);
        Assert.Equal(crit, distribution.CritChance, 10);
        Assert.Equal(1, distribution.Outcomes.Sum(x => x.Probability), 10);
    }

    [Fact]
    public void GlobalAndSkillCriticalDamageAddWithoutChangingPanelDps()
    {
        var stats = Player(("weaponDamage.fire", 100));
        stats.Set("offensiveAbility", 2000);
        var scenario = Scenario();
        scenario.EnemyDefensiveAbility = 500; // PTH 138.125; crit roll width 48.125.
        var baseline = Run(stats, scenario);
        stats.Set("criticalDamage", 60);
        var rotation = Rotation();
        rotation.FlatDamage.Set("criticalDamage", 40);
        var modified = Run(stats, scenario, rotation);

        Assert.Equal(100 * 48.125 / 138.125, modified.ExpectedDps - baseline.ExpectedDps, 8);
        Assert.Equal(baseline.UnmitigatedDps, modified.UnmitigatedDps);
        Assert.Equal(48.125 / 138.125, modified.CritChance, 10);
    }

    [Fact]
    public void CriticalDamageIsRolledBeforeNonlinearEnemyArmor()
    {
        var stats = Player(("weaponDamage.physical", 100));
        stats.Set("offensiveAbility", 2000);
        var scenario = Scenario();
        scenario.EnemyDefensiveAbility = 500;
        scenario.EnemyArmor = 105;
        scenario.EnemyArmorAbsorption = 100;
        // Normal hits are stopped. Crit buckets deal 5,15,25,35,45.
        Assert.Equal(865.625 / 138.125, Run(stats, scenario).ExpectedDps, 8);
    }

    [Fact]
    public void EnemyCriticalsAndTheirDamageBonusAffectSurvival()
    {
        var stats = Player();
        stats.Set("defensiveAbility", 500);
        var scenario = Scenario(Fire(100) with { UsesOffensiveAbility = true });
        scenario.EnemyOffensiveAbility = 2000;
        var exposed = Run(stats, scenario);
        scenario.IncomingDamage = scenario.IncomingDamage with { CriticalDamagePercent = 100 };
        var critical = Run(stats, scenario);
        stats.Set("defensiveAbility", 2000);
        var defensive = Run(stats, scenario);
        Assert.Equal(100 * 48.125 / 138.125, critical.IncomingDps - exposed.IncomingDps, 8);
        Assert.Equal(90, defensive.IncomingDps, 8);
        Assert.True(defensive.TimeToDeathSeconds > critical.TimeToDeathSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, .05)]
    [InlineData(50, .1)]
    [InlineData(100, .2)]
    [InlineData(200, .2)]
    public void EquipmentAdcthRequiresAndScalesWithWeaponDamage(double weaponPercent, double leech)
    {
        var stats = Player(("weaponDamage.fire", 100), ("lifeSteal", 20));
        var rotation = Rotation(weaponPercent, Stats(("damage.fire.flat", 100)));
        var report = Run(stats, Scenario(), rotation);
        Assert.Equal(report.ExpectedDps * leech, report.SustainPerSecond, 8);
        Assert.Equal(weaponPercent == 0 ? 0 : report.ExpectedDps, report.LifeStealEligibleDps, 8);
    }

    [Fact]
    public void SpellLocalAdcthWorksButDoesNotLeechDotDamage()
    {
        var rotation = Rotation(0, Stats(("damage.fire.flat", 100), ("damage.fire.dot", 100), ("lifeSteal", 20)));
        var scenario = Scenario();
        scenario.EnemyLifeLeechResistance = 50;
        var report = Run(Player(("lifeSteal", 100)), scenario, rotation);
        Assert.Equal(180, report.ExpectedDps, 8);
        Assert.Equal(90, report.LifeStealEligibleDps, 8);
        Assert.Equal(9, report.SustainPerSecond, 8);
    }

    [Fact]
    public void SecondarySpellDoesNotInheritMainAttackWeaponDamageForAdcth()
    {
        var rotation = Rotation();
        rotation.SecondaryAttacks.Add(new() { WeaponDamagePercent = 0, FlatDamage = Stats(("damage.fire.flat", 900)) });
        var report = Run(Player(("weaponDamage.fire", 100), ("lifeSteal", 20)), Scenario(), rotation);
        Assert.Equal(900, report.ExpectedDps, 8);
        Assert.Equal(90, report.LifeStealEligibleDps, 8);
        Assert.Equal(18, report.SustainPerSecond, 8);
    }

    [Fact]
    public void ProcLocalAdcthIsUsedAndProcSpellDoesNotInheritGlobalAdcth()
    {
        var stats = Player(("lifeSteal", 100));
        var effect = new TriggeredEffect("proc", "proc", 1, 0,
            Stats(("damage.fire.flat", 100), ("damage.fire.dotTotal", 100), ("lifeSteal", 20)));
        var report = Run(stats, Scenario(), effects: [effect]);
        Assert.Equal(180, report.ExpectedDps, 8);
        Assert.Equal(18, report.SustainPerSecond, 8);
        Assert.Equal(90, report.LifeStealEligibleDps, 8);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(50, 58.5)]
    [InlineData(1000, 27)]
    public void EnemyArmorCannotAbsorbMoreThanItsAbsorptionFraction(double armor, double damage)
    {
        var scenario = Scenario();
        scenario.EnemyArmor = armor;
        Assert.Equal(damage, Run(Player(("weaponDamage.physical", 100)), scenario).ExpectedDps, 8);
    }

    [Fact]
    public void PhysicalDotBypassesArmorAndHasNoAdcth()
    {
        var scenario = Scenario();
        scenario.EnemyArmor = 10000;
        scenario.EnemyArmorAbsorption = 100;
        var report = Run(Player(("lifeSteal", 100)), scenario,
            Rotation(0, Stats(("damage.physical.dot", 100), ("lifeSteal", 100))));
        Assert.Equal(90, report.ExpectedDps, 8);
        Assert.Equal(0, report.SustainPerSecond);
    }

    [Fact]
    public void EachWeaponPoolStrikeGetsItsOwnArmorCheck()
    {
        var scenario = Scenario();
        scenario.EnemyArmor = 100;
        scenario.EnemyArmorAbsorption = 100;
        var rotation = Rotation();
        rotation.WeaponPoolAttacks.Add(new() { Chance = 100, WeaponStrikes = 2 });
        // Two 100-point hits cannot become a single 200-point hit piercing armor.
        Assert.Equal(0, Run(Player(("weaponDamage.physical", 100)), scenario, rotation).ExpectedDps);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(50, 150)]
    [InlineData(100, 100)]
    public void BlockPrecedesResistanceAndAllowsOneHundredPercent(double chance, double expected)
    {
        var stats = Player(("fireResistance", 80), ("blockChance", chance), ("blockAmount", 500));
        Assert.Equal(expected, Run(stats, Scenario(Fire(1000))).IncomingDps, 8);
    }

    [Fact]
    public void OneBlockChecksEachTypeBeforeArmorAndAbsorption()
    {
        var stats = Player(("blockChance", 100), ("blockAmount", 500), ("fireResistance", 80), ("coldResistance", 80));
        var scenario = Scenario(Fire(1000) with { ColdPerHit = 1000 });
        Assert.Equal(200, Run(stats, scenario).IncomingDps, 8);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void BlockRecoveryRespectsTimelineAndFullRecoveryReduction(double recoveryReduction, double expected)
    {
        var stats = Player(("blockChance", 100), ("blockAmount", 100),
            ("blockRecoverySeconds", 1), ("blockRecoveryReduction", recoveryReduction));
        var scenario = Scenario(Fire(100) with { HitsPerSecond = 2 });
        scenario.DurationSeconds = 2;
        Assert.Equal(expected, Run(stats, scenario).IncomingDps, 8);
    }

    [Fact]
    public void IncomingDotBypassesDodgeDaBlockAndArmorButNotAbsorption()
    {
        var stats = Player(("dodgeChance", 100), ("blockChance", 100), ("blockAmount", 10000),
            ("armor", 10000), ("armorAbsorption", 100), ("damageAbsorption", 20));
        var scenario = Scenario(Physical(100) with { IsDamageOverTime = true });
        Assert.Equal(80, Run(stats, scenario).IncomingDps, 8);
    }

    [Theory]
    [InlineData(50, 10, .2, 60, -22)]
    [InlineData(80, 10, .25, 20, 35)]
    [InlineData(-20, 10, .5, 10, -55)]
    public void ResistanceReductionUsesCategoryOrderIncludingNegativeResistance(double resistance, double flat,
        double percent, double stacking, double expected)
        => Assert.Equal(expected, ResistanceRules.Apply(resistance, flat, percent, stacking), 8);

    [Fact]
    public void FlatAndPercentReductionChooseStrongestWhileDistinctSpecificDebuffsStack()
    {
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 80;
        var rules = new ResistanceRules(scenario, new(), [
            Reduction("a", CombatModifierKind.FlatResistanceReduction, 10),
            Reduction("b", CombatModifierKind.FlatResistanceReduction, 20),
            Reduction("c", CombatModifierKind.PercentResistanceReduction, 10),
            Reduction("d", CombatModifierKind.PercentResistanceReduction, 25),
            Reduction("e", CombatModifierKind.StackingResistanceReduction, 10),
            Reduction("f", CombatModifierKind.StackingResistanceReduction, 10)]);
        // (80 - 10 - 10) * .75 - max(10,20) = 25 resistance.
        Assert.Equal(75, rules.Mitigate("fire", 100), 8);
    }

    [Fact]
    public void ProbabilisticStrongestReductionDoesNotAverageMagnitudesBeforeSelecting()
    {
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 50;
        var rules = new ResistanceRules(scenario, new(), [
            Reduction("weak", CombatModifierKind.FlatResistanceReduction, 20, .5),
            Reduction("strong", CombatModifierKind.FlatResistanceReduction, 30, .5)]);
        // None:25%, weak only:25%, strong (with/without weak):50%.
        Assert.Equal(70, rules.Mitigate("fire", 100), 8);
    }

    [Fact]
    public void SameNamedSpecificDebuffDoesNotStackWithItself()
    {
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 50;
        var rules = new ResistanceRules(scenario, new(), [
            Reduction("same", CombatModifierKind.StackingResistanceReduction, 20),
            Reduction("same", CombatModifierKind.StackingResistanceReduction, 30)]);
        Assert.Equal(80, rules.Mitigate("fire", 100), 8);
    }

    [Fact]
    public void SameNamedDebuffsFromDifferentEmittersUseStrongestActiveLevel()
    {
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 50;
        var modifier = new CombatModifier("same-buff", CombatModifierKind.StackingResistanceReduction, 20, "fire");
        var rules = new ResistanceRules(scenario, new(), [new(modifier, 1, "low-level-ring"),
            new(modifier with { Value = 30 }, .5, "high-level-ring")]);
        // Always -20; only half the time replaced by -30. Never -50.
        Assert.Equal(75, rules.Mitigate("fire", 100), 8);
    }

    [Fact]
    public async Task DuplicateDamageProcsHaveIndependentCooldowns()
    {
        var stats = Player();
        var scenario = Scenario(Fire(100));
        scenario.DurationSeconds = 4;
        var proc = new TriggeredEffect("same", "same", .5, 2, Stats(("damage.fire.flat", 100)), "OnHit");
        var report = await new CombatSimulator().RunMonteCarloAsync(Character(), Loadout(stats, [proc, proc]), scenario, Rotation(), 10000, 313);
        Assert.Equal(71.875, report.ExpectedDps, 8);
        Assert.InRange(report.MonteCarloDpsMean, 71, 72.75);
    }

    [Fact]
    public void EnemyDebuffFieldsDoNotReduceThePlayersOwnResistance()
    {
        var debuff = Record("enemy", "Skill_BuffEnemy", ("defensiveFire", -30),
            ("defensiveElementalResistance", -10), ("skillActiveDuration", 4));
        var translated = GameStatTranslator.TranslateAtLevel(debuff, 1);
        Assert.Equal(0, translated["fireResistance"]);
        Assert.Equal(0, translated["coldResistance"]);
        Assert.Equal(40, translated.CombatModifiers.Where(x => x.DamageType == "fire").Sum(x => x.Value));
        Assert.All(translated.CombatModifiers, x => Assert.Equal(4, x.DurationSeconds));
        var rules = new ResistanceRules(Scenario(), new(), translated.CombatModifiers.Select(x => new ActiveResistanceReduction(x, 1)));
        Assert.Equal(140, rules.Mitigate("fire", 100), 8);
        Assert.Equal(-30, GameStatTranslator.Translate([Record("item", "Armor", ("defensiveFire", -30))])["fireResistance"]);
    }

    [Fact]
    public void DebuffUptimeIncludesApplicationChanceExpiryAndInitialRamp()
    {
        var events = ProcTiming.Regular(1, 4, 1);
        // Initially inactive. Probabilities in seconds 1..4: 0,.5,.75,.75.
        Assert.Equal(.5, ProcTiming.DebuffUptime(events, .5, 0, 1, 2, 4), 10);
        Assert.Equal(0, ProcTiming.DebuffUptime(events, 0, 0, 1, 2, 4));
        Assert.Equal(0, ProcTiming.DebuffUptime(events, 1, 0, 1, 0, 4));
        Assert.Equal(.46875, ProcTiming.DebuffUptime(events, .5, 2, 1, 2, 4), 10);
    }

    [Fact]
    public void SameRecordFlatCategoriesDoNotBecomeAnAdditiveReduction()
    {
        var raw = Record("same", "Item", ("offensiveTotalResistanceReductionAbsoluteMin", 20),
            ("offensiveElementalResistanceReductionAbsoluteMin", 30));
        var translated = GameStatTranslator.Translate([raw]);
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 50;
        var rules = new ResistanceRules(scenario, new(), translated.CombatModifiers.Select(x => new ActiveResistanceReduction(x, 1)));
        Assert.Equal(80, rules.Mitigate("fire", 100), 8);
    }

    [Fact]
    public void EquipmentReductionUsesWeaponHitsChanceDurationAndEncounterStartup()
    {
        var stats = Player(("weaponDamage.fire", 100));
        stats.Add(GameStatTranslator.Translate([Record("rr-gear", "Item",
            ("offensiveTotalResistanceReductionAbsoluteMin", 20),
            ("offensiveTotalResistanceReductionAbsoluteChance", 50),
            ("offensiveTotalResistanceReductionAbsoluteDurationMin", 2))]));
        var scenario = Scenario();
        scenario.DurationSeconds = 4;
        scenario.EnemyResistances["fire"] = 50;
        // Land+apply=.9*.5=.45; uptime=(0+.45+.6975+.6975)/4=.46125.
        Assert.Equal(53.3025, Run(stats, scenario).ExpectedDps, 8);
        Assert.Equal(73.726875, Run(stats, scenario, Rotation(50, Stats(("damage.fire.flat", 100)))).ExpectedDps, 8);
        Assert.Equal(106.605, Run(stats, scenario, Rotation(200)).ExpectedDps, 8);
        Assert.Equal(45, Run(stats, scenario, Rotation(0, Stats(("damage.fire.flat", 100)))).ExpectedDps, 8);
    }

    [Fact]
    public void AdcthPercentIsNotCappedAsIfItWereAProcChance()
    {
        var rotation = Rotation(100, Stats(("damage.fire.flat", 100), ("lifeSteal", 100)));
        var report = Run(Player(("lifeSteal", 50)), Scenario(), rotation);
        Assert.Equal(90, report.ExpectedDps, 8);
        Assert.Equal(135, report.SustainPerSecond, 8);
    }

    [Fact]
    public async Task ProcCooldownUsesSuccessfulTriggersInExpectationAndMonteCarlo()
    {
        var opportunities = ProcTiming.Regular(1, 4, 1);
        Assert.Equal(1.4375, ProcTiming.ExpectedTriggers(opportunities, .5, 2), 10);
        var stats = Player();
        var scenario = Scenario(Fire(100));
        scenario.DurationSeconds = 4;
        var effect = new TriggeredEffect("cooldown", "cooldown", .5, 2, Stats(("damage.fire.flat", 100)), "OnHit");
        var report = await new CombatSimulator().RunMonteCarloAsync(Character(), Loadout(stats, [effect]), scenario, Rotation(), 10000, 313);
        Assert.Equal(35.9375, report.ExpectedDps, 8);
        Assert.InRange(report.MonteCarloDpsMean, 35.5, 36.4);
    }

    [Fact]
    public void IndependentItemInstancesKeepSeparateAbsorptionSources()
    {
        var absorption = GameStatTranslator.Translate([Record("item-buff", "Item", ("damageAbsorptionPercent", 20))]);
        LoadoutVariant Item(string id) => new(new ItemInstance { Id = id, Name = id, Slot = EquipmentSlot.Ring,
            BaseRecord = "same-item", Stats = absorption, Source = new(ItemSourceKind.Equipped, "fixture.gdc") }, null, null);
        var stats = Player();
        stats.Add(Item("left").CombinedStats);
        stats.Add(Item("right").CombinedStats);
        Assert.Equal(640, Run(stats, Scenario(Fire(1000))).IncomingDps, 8);
    }

    [Fact]
    public void OnCritReductionCannotHaveUptimeAtNinetyPthAndMissingDurationIsDiagnosed()
    {
        var stats = Player(("weaponDamage.fire", 100));
        var scenario = Scenario();
        scenario.EnemyResistances["fire"] = 50;
        var debuff = new StatBlock(combatModifiers: [new("rr", CombatModifierKind.StackingResistanceReduction,
            30, "fire", DurationSeconds: 4)]);
        var crit = new TriggeredEffect("rr", "rr", 1, 0, debuff, Trigger: "OnCrit");
        Assert.Equal(45, Run(stats, scenario, effects: [crit]).ExpectedDps, 8);
        var missing = new StatBlock(combatModifiers: [new("rr", CombatModifierKind.FlatResistanceReduction, 30, "fire")]);
        var report = Run(stats, scenario, effects: [crit with { Damage = missing, Trigger = "OnAttack" }]);
        Assert.Equal(45, report.ExpectedDps, 8);
        Assert.Contains(report.UnsupportedEffects, x => x.Description.Contains("持续时间缺失") && !x.AffectedMetrics.Contains("panelDps"));
    }

    [Theory]
    [InlineData(IncomingAttackKind.Melee, 0, 100)]
    [InlineData(IncomingAttackKind.Projectile, 100, 0)]
    [InlineData(IncomingAttackKind.Spell, 100, 100)]
    public void DodgeAndDeflectionUseTheIncomingAttackKind(IncomingAttackKind kind, double withDodge, double withDeflect)
    {
        var scenario = Scenario(Fire(100) with { Kind = kind });
        Assert.Equal(withDodge, Run(Player(("dodgeChance", 100)), scenario).IncomingDps, 8);
        Assert.Equal(withDeflect, Run(Player(("projectileAvoidance", 100)), scenario).IncomingDps, 8);
    }

    [Fact]
    public void SpellsDefaultToNoOaRollButAnExplicitWeaponSpellCanOptIn()
    {
        var incoming = Fire(100) with { Kind = IncomingAttackKind.Spell, UsesOffensiveAbility = null };
        Assert.Equal(100, Run(Player(), Scenario(incoming)).IncomingDps, 8);
        Assert.Equal(90, Run(Player(), Scenario(incoming with { UsesOffensiveAbility = true })).IncomingDps, 8);
    }

    [Fact]
    public void MixedIncomingProfilesTriggerRetaliationOnlyOnMeleeContact()
    {
        var stats = Player(("retaliation.fire.flat", 100));
        var scenario = Scenario(Fire(100) with { UsesOffensiveAbility = true });
        scenario.AdditionalIncomingDamage.Add(Fire(100) with { HitsPerSecond = 2, Kind = IncomingAttackKind.Projectile });
        scenario.AdditionalIncomingDamage.Add(Fire(100) with { HitsPerSecond = 3, Kind = IncomingAttackKind.Spell });
        var report = Run(stats, scenario);
        Assert.Equal(590, report.IncomingDps, 8);
        Assert.Equal(90, report.RetaliationDps, 8);
        stats.Set("projectileAvoidance", 100);
        Assert.Equal(390, Run(stats, scenario).IncomingDps, 8);
        stats.Set("dodgeChance", 100);
        var avoided = Run(stats, scenario);
        Assert.Equal(300, avoided.IncomingDps, 8);
        Assert.Equal(0, avoided.RetaliationDps);
    }

    [Fact]
    public void RataUsesRetaliationBonusesWithoutOrdinaryDamageOrGlobalAdcth()
    {
        var stats = Player(("retaliation.fire.flat", 100), ("retaliation.fire.percent", 200),
            ("retaliation.total.percent", 100), ("damage.fire.percent", 900),
            ("damage.total.multiplierPercent", 200), ("lifeSteal", 100));
        var rata = GameStatTranslator.Translate([Record("rata", "Skill_AttackSpell", ("retaliationDamagePct", 25))]);
        var rotation = Rotation(0, rata);
        var scenario = Scenario(Fire(100) with { Kind = IncomingAttackKind.Projectile });
        var report = Run(stats, scenario, rotation);
        Assert.Equal(90, report.ExpectedDps, 8);
        Assert.Equal(0, report.RetaliationDps);
        Assert.Equal(0, report.SustainPerSecond);
        Assert.Equal(ConfidenceLevel.Low, CalculationDiagnostics.PanelConfidence(Loadout(stats), rotation));
        Assert.Contains(report.UnsupportedEffects, CalculationDiagnostics.AffectsPanel);
        rata.Set("lifeSteal", 10);
        Assert.Equal(9, Run(stats, scenario, rotation).SustainPerSecond, 8);
        Assert.Empty(GameStatTranslator.FindUnsupportedCombatFields(Record("rata", "Skill_AttackSpell", ("retaliationDamagePct", 25))));
    }

    [Fact]
    public void RataCritsAndSkillConversionUsesDestinationRetaliationBonuses()
    {
        var stats = Player(("retaliation.physical.flat", 100), ("retaliation.fire.percent", 100));
        stats.Set("offensiveAbility", 2000);
        var scenario = Scenario(Fire(0) with { Kind = IncomingAttackKind.Spell });
        scenario.EnemyDefensiveAbility = 500;
        scenario.EnemyArmor = 10000;
        scenario.EnemyArmorAbsorption = 100;
        var rotation = Rotation(0, Stats(("retaliation.addedToAttackPercent", 50), ("conversion.physical.fire", 100)));
        var withoutCritBonus = Run(stats, scenario, rotation);
        stats.Set("criticalDamage", 100);
        Assert.Equal(100 * 48.125 / 138.125, Run(stats, scenario, rotation).ExpectedDps - withoutCritBonus.ExpectedDps, 8);
        Assert.True(withoutCritBonus.ExpectedDps > 100);
    }

    [Fact]
    public void RataDotDoesNotStackWithAttackSpeedOrLeech()
    {
        var stats = Player(("retaliation.fire.dot", 100), ("retaliation.fire.dotPercent", 100));
        var rotation = Rotation(0, Stats(("retaliation.addedToAttackPercent", 50), ("lifeSteal", 100)));
        var scenario = Scenario(Fire(0) with { Kind = IncomingAttackKind.Spell });
        var slow = Run(stats, scenario, rotation);
        rotation.BaseAttacksPerSecond = 2;
        var fast = Run(stats, scenario, rotation);
        Assert.Equal(90, slow.ExpectedDps, 8);
        Assert.Equal(slow.ExpectedDps, fast.ExpectedDps);
        Assert.Equal(0, fast.SustainPerSecond);
    }

    [Fact]
    public void CombatModifierSourcesSurviveJsonCloneAndSkillLevelReplacement()
    {
        var record = new GameRecord
        {
            RecordName = "sphere", RecordType = "Skill_BuffSelfToggled",
            NumericSeries = new Dictionary<string, IReadOnlyList<double>> { ["damageAbsorptionPercent"] = [10, 20] }
        };
        var baseline = GameStatTranslator.TranslateAtLevel(record, 1);
        var delta = GameStatTranslator.TranslateAtLevel(record, 2);
        delta.Add(baseline, -1);
        var final = baseline.Clone();
        final.Add(delta);
        var restored = JsonSerializer.Deserialize<StatBlock>(JsonSerializer.Serialize(final))!;
        var stats = Player();
        stats.Add(restored);
        Assert.Equal(800, Run(stats, Scenario(Fire(1000))).IncomingDps, 8);
        Assert.Equal(20, Assert.Single(restored.CombatModifiers).Value);
    }

    [Fact]
    public void RecursiveSkillPayloadPreservesEnemyDebuffAndRataWithoutCycles()
    {
        const string rootName = "records/skills/attack.dbr";
        var root = Link(Record(rootName, "Skill_AttackSpell", ("retaliationDamagePct", 25)), "skillProjectileName", "projectile");
        var projectile = Link(Record("projectile", "Projectile", ("offensiveFireMin", 100)), "buffSkillName", "debuff");
        var debuff = Link(Record("debuff", "Skill_BuffEnemy", ("defensiveFire", -30), ("skillActiveDuration", 4)), "buffSkillName", "projectile");
        var catalog = new GameDataCatalog { Records = new[] { root, projectile, debuff }.ToDictionary(x => x.RecordName) };
        var rotation = SkillRotationFactory.Build(Character(), catalog, new(rootName, "attack", 1, 0, true, "", ""));
        Assert.False(rotation.IsFallback);
        Assert.Equal(100, rotation.FlatDamage["damage.fire.flat"]);
        Assert.Equal(25, rotation.FlatDamage["retaliation.addedToAttackPercent"]);
        Assert.Equal(30, Assert.Single(rotation.FlatDamage.CombatModifiers).Value);
        Assert.Equal(0, rotation.FlatDamage["fireResistance"]);
    }

    [Fact]
    public async Task MonteCarloAgreesWithCriticalArmorRataAndProcAdcthCombatExpectation()
    {
        var stats = Player(("weaponDamage.physical", 100), ("criticalDamage", 30), ("lifeSteal", 20),
            ("retaliation.fire.flat", 100), ("projectileAvoidance", 60));
        stats.Set("offensiveAbility", 2000);
        var scenario = Scenario(Fire(100) with { HitsPerSecond = 3, Kind = IncomingAttackKind.Projectile });
        scenario.EnemyDefensiveAbility = 500;
        scenario.EnemyArmor = 105;
        scenario.EnemyArmorAbsorption = 100;
        var rotation = Rotation(50, Stats(("retaliation.addedToAttackPercent", 25), ("lifeSteal", 10)));
        var effect = new TriggeredEffect("heal-proc", "heal-proc", .5, 0,
            Stats(("damage.fire.flat", 50), ("lifeSteal", 20)), Trigger: "OnHit");
        var loadout = Loadout(stats, [effect]);
        var report = await new CombatSimulator().RunMonteCarloAsync(Character(), loadout, scenario, rotation, 10000, 25134);
        Assert.InRange(report.MonteCarloDpsMean, report.ExpectedDps * .99, report.ExpectedDps * 1.01);
        Assert.InRange(report.ExpectedDps, report.MonteCarloDpsLow95, report.MonteCarloDpsHigh95);
    }

    [Fact]
    public void MonteCarloIncomingMeanMatchesCritBlockRecoveryArmorAndAbsorptionExpectation()
    {
        var stats = Player(("blockChance", 60), ("blockAmount", 150), ("blockRecoverySeconds", .7),
            ("armor", 100), ("armorAbsorption", 70), ("projectileAvoidance", 30), ("damageAbsorption", 20));
        stats.Set("defensiveAbility", 500);
        stats.AddCombatModifier(new("sphere", CombatModifierKind.PercentAbsorption, 20));
        var scenario = Scenario(Physical(250) with { HitsPerSecond = 3, UsesOffensiveAbility = true });
        scenario.EnemyOffensiveAbility = 2000;
        scenario.AdditionalIncomingDamage.Add(Fire(100) with { HitsPerSecond = 2, Kind = IncomingAttackKind.Projectile });
        var model = new IncomingCombatModel(stats, scenario);
        var rng = new Random(912);
        var mean = Enumerable.Range(0, 20000).Average(_ => model.Sample(rng, (_, _) => { }) / scenario.DurationSeconds);
        Assert.InRange(mean, model.IncomingDps * .99, model.IncomingDps * 1.01);
    }

    private static ActiveResistanceReduction Reduction(string source, CombatModifierKind kind, double value, double uptime = 1)
        => new(new(source, kind, value, "fire"), uptime);
    private static IncomingDamageProfile Fire(double damage) => new(HitsPerSecond: 1, PhysicalPerHit: 0, FirePerHit: damage,
        ColdPerHit: 0, LightningPerHit: 0, AcidPerHit: 0, PiercePerHit: 0, VitalityPerHit: 0, AetherPerHit: 0,
        ChaosPerHit: 0, BleedingPerHit: 0, UsesOffensiveAbility: false);
    private static IncomingDamageProfile Physical(double damage) => Fire(0) with { PhysicalPerHit = damage };
    private static CombatScenario Scenario(IncomingDamageProfile? incoming = null) => new()
    {
        EnemyArmor = 0, EnemyOffensiveAbility = 1000, EnemyDefensiveAbility = 1000,
        EnemyLifeLeechResistance = 0, EnemyResistances = new(StringComparer.OrdinalIgnoreCase),
        IncomingDamage = incoming ?? Fire(0), DurationSeconds = 10
    };
    private static SkillRotationDefinition Rotation(double weaponPercent = 100, StatBlock? damage = null) => new()
    { BaseAttacksPerSecond = 1, WeaponDamagePercent = weaponPercent, FlatDamage = damage ?? new() };
    private static StatBlock Player(params (string Key, double Value)[] values)
    {
        var result = Stats(("health", 10000), ("offensiveAbility", 1000), ("defensiveAbility", 1000));
        foreach (var (key, value) in values) result.Set(key, value);
        return result;
    }
    private static StatBlock Stats(params (string Key, double Value)[] values) => new(values.ToDictionary(x => x.Key, x => x.Value));
    private static GameRecord Record(string name, string type, params (string Key, double Value)[] values)
        => new() { RecordName = name, RecordType = type, NumericValues = Stats(values) };
    private static GameRecord Link(GameRecord record, string field, string target) => new()
    {
        RecordName = record.RecordName, RecordType = record.RecordType, NumericValues = record.NumericValues,
        TextValues = new Dictionary<string, IReadOnlyList<string>> { [field] = [target] }
    };
    private static CharacterSnapshot Character() => new()
    { Name = "mechanisms", SaveFilePath = "fixture.gdc", Level = 100, Physique = 0, Cunning = 0, Spirit = 0 };
    private static CalculatedLoadout Loadout(StatBlock stats, IReadOnlyList<TriggeredEffect>? effects = null) => new()
    { Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = stats, Effects = effects ?? [] };
    private static SimulationReport Run(StatBlock stats, CombatScenario scenario, SkillRotationDefinition? rotation = null,
        IReadOnlyList<TriggeredEffect>? effects = null) => new CombatSimulator().Simulate(Character(), Loadout(stats, effects), scenario, rotation ?? Rotation());
}
