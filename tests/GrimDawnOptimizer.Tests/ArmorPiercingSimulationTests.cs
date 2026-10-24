using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ArmorPiercingSimulationTests
{
    [Fact]
    public void PiercingUsesItsOwnLocalBonusWithoutFractionScalingPhysicalOrPierceBonuses()
    {
        var damage = Damage(("physical", 100));
        var skill = Stats((StatKeys.DamagePercent("physical"), 100), (StatKeys.DamagePercent("pierce"), 50));

        var local = DamageConversion.Apply(damage, skill, new(), armorPiercingPercent: 50);

        Assert.Equal(50, damage["physical"]);
        Assert.Equal(50, damage["pierce"]);
        Assert.Equal(50, local["physical"]);
        Assert.Equal(25, local["pierce"]);
    }

    [Fact]
    public void PiercingRunsAfterOrdinaryConversionAndCannotReconvertLockedPhysical()
    {
        var damage = Damage(("physical", 100), ("cold", 20));
        var global = Stats(("conversion.physical.fire", 50), ("conversion.cold.physical", 50));
        var skill = Stats((StatKeys.DamagePercent("physical"), 80), (StatKeys.DamagePercent("pierce"), 40));

        var local = DamageConversion.Apply(damage, skill, global, armorPiercingPercent: 100);

        Assert.Equal(50, damage["fire"]);
        Assert.Equal(50, damage["pierce"]);
        Assert.Equal(10, damage["physical"]); // Converted from cold and already locked.
        Assert.Equal(10, damage["cold"]);
        Assert.Equal(20, local["pierce"]); // Original pierce bonus, not the reduced physical bonus.
    }

    [Fact]
    public void NativeAndSharedPhysicalJoinTheSingleWeaponsPiercingCarrier()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 100), (StatKeys.DamageFlat("physical"), 20),
            (StatKeys.DamagePercent("pierce"), 100));
        var rotation = Rotation();
        rotation.FlatDamage.Set(StatKeys.DamageFlat("physical"), 40);
        rotation.FlatDamage.Set(StatKeys.DamagePercent("physical"), 89);

        var report = Run(stats, rotation, Weapon(100, 100));

        // (100 weapon + 20 shared + 40 native) becomes pierce; the skill's
        // +89% physical does not become a pierce modifier.
        Assert.Equal(320, report.UnmitigatedDps, 8);
    }

    [Fact]
    public void DualWieldPrimaryPreservesEachHandsDamageAndPiercingRatio()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 200), (StatKeys.DamageFlat("physical"), 20),
            ("averageWeaponStrikesPerAction", 1.5), (StatKeys.DamagePercent("pierce"), 100));
        var rotation = Rotation();
        rotation.FlatDamage.Set(StatKeys.DamageFlat("physical"), 40);

        var report = Run(stats, rotation, Weapon(100, 100), Weapon(300, 0));

        // Main: (100+20+40)*.75*2 = 240; off: (300+20+40)*.75 = 270.
        // Averaging the ratios first would incorrectly pierce the larger offhand.
        Assert.Equal(510, report.UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(50, 100, 690)]
    [InlineData(0, 100, 920)]
    public void SecondaryMergesAllPhysicalIntoTheFirstPositivePiercingCarrier(
        double mainPiercing, double offPiercing, double expected)
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 200), (StatKeys.DamageFlat("physical"), 20),
            ("secondaryWeaponDamageCorrection.physical", 200), (StatKeys.DamagePercent("pierce"), 100));
        var rotation = Rotation();
        rotation.WeaponDamagePercent = 0;
        rotation.SecondaryAttacks = [new() { WeaponDamagePercent = 100, FlatDamage = Stats((StatKeys.DamageFlat("physical"), 40)) }];

        var report = Run(stats, rotation, Weapon(100, mainPiercing), Weapon(300, offPiercing));

        // The secondary's one collector has 400 local weapon + 20 shared + 40 native.
        Assert.Equal(expected, report.UnmitigatedDps, 8);
    }

    [Fact]
    public void PurePhysicalSpellDoesNotAcquireTheEquippedWeaponsPiercingCarrier()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 100), (StatKeys.DamagePercent("physical"), 100));
        var rotation = Rotation();
        rotation.WeaponDamagePercent = 0;
        rotation.UsesAttackSpeed = false;
        rotation.FlatDamage.Set(StatKeys.DamageFlat("physical"), 50);

        Assert.Equal(100, Run(stats, rotation, Weapon(100, 100)).UnmitigatedDps, 8);
    }

    [Fact]
    public void PiercingModifierScalesTheWeaponsRatioAndCapsAtOneHundredPercent()
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 100), (StatKeys.DamagePercent("pierce"), 100),
            ("armorPiercingModifier", 150));

        Assert.Equal(200, Run(stats, Rotation(), Weapon(100, 50)).UnmitigatedDps, 8);
    }

    [Theory]
    [InlineData(100, 0, 100, 25, true)]
    [InlineData(100, 100, 100, 25, true)]
    [InlineData(0, 0, 100, 25, false)]
    [InlineData(100, 0, 0, 25, false)]
    [InlineData(100, 0, 100, 0, false)]
    public async Task DualWieldWpsPiercingIsAnExplicitCombatOnlyLimitation(
        double mainPiercing, double offPiercing, double weaponPercent, double chance, bool warns)
    {
        var stats = Stats((StatKeys.WeaponDamage("physical"), 100), ("averageWeaponStrikesPerAction", 1.5));
        var loadout = new CalculatedLoadout
        {
            Stats = stats,
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>
            {
                [EquipmentSlot.MainHand] = new(Weapon(100, mainPiercing), null, null),
                [EquipmentSlot.OffHand] = new(Weapon(100, offPiercing), null, null)
            }
        };
        var rotation = Rotation();
        rotation.WeaponDamagePercent = weaponPercent;
        rotation.WeaponPoolAttacks = [new() { Chance = chance, WeaponStrikes = 2 }];
        var character = new CharacterSnapshot { Name = "fixture", SaveFilePath = "fixture.gdc" };
        var simulator = new CombatSimulator();
        var report = simulator.Simulate(character, loadout, new(), rotation);
        var monteCarlo = await simulator.RunMonteCarloAsync(character, loadout, new(), rotation, 100, 123);

        Assert.Equal(warns ? ConfidenceLevel.Low : ConfidenceLevel.High,
            CalculationDiagnostics.OverallConfidence(loadout, rotation));
        Assert.Equal(ConfidenceLevel.High, CalculationDiagnostics.PanelConfidence(loadout, rotation));
        Assert.Equal(warns ? 1 : 0, report.UnsupportedEffects.Count);
        Assert.Equal(report.UnsupportedEffects.Count, monteCarlo.UnsupportedEffects.Count);
        foreach (var warning in report.UnsupportedEffects)
        {
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains(CalculationDiagnostics.CombatDps, warning.AffectedMetrics);
            Assert.DoesNotContain(CalculationDiagnostics.PanelDps, warning.AffectedMetrics);
        }
    }

    [Fact]
    public void SimulationReportRetainsUnsupportedMainSkillDiagnostics()
    {
        var rotation = Rotation();
        rotation.UnsupportedEffects = [new("fixture", "Unmodeled native skill damage", [CalculationDiagnostics.PanelDps])];

        Assert.Single(Run(Stats((StatKeys.WeaponDamage("physical"), 100)), rotation, Weapon(100, 0)).UnsupportedEffects);
    }

    private static SimulationReport Run(StatBlock stats, SkillRotationDefinition rotation,
        ItemInstance main, ItemInstance? off = null)
    {
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.MainHand] = new(main, null, null) };
        if (off is not null) slots[EquipmentSlot.OffHand] = new(off, null, null);
        var loadout = new CalculatedLoadout { Slots = slots, Stats = stats };
        return new CombatSimulator().Simulate(new CharacterSnapshot { Name = "fixture", SaveFilePath = "fixture.gdc" },
            loadout, new CombatScenario { EnemyResistances = new(), EnemyArmor = 0 }, rotation);
    }

    private static ItemInstance Weapon(double physical, double piercing) => new()
    {
        Id = "fixture", Name = "fixture", BaseRecord = "fixture", Slot = EquipmentSlot.MainHand,
        Stats = Stats((StatKeys.WeaponDamage("physical"), physical), (StatKeys.WeaponArmorPiercing, piercing)),
        Source = new ItemSourceLocation(ItemSourceKind.Equipped, "fixture.gdc")
    };
    private static SkillRotationDefinition Rotation() => new() { BaseAttacksPerSecond = 1, IsFallback = false };
    private static StatBlock Stats(params (string Key, double Value)[] values) => new(values.ToDictionary(x => x.Key, x => x.Value));
    private static Dictionary<string, double> Damage(params (string Key, double Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
}
