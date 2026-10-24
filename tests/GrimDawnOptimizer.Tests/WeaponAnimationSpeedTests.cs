using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class WeaponAnimationSpeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DualPistolsUseBothBaseSpeedModifiersAndResourceAnimationDurations(bool female)
    {
        var unarmed = (double)(19f / 30);
        var equipped = (double)(((19f / 30) + (19f / 30) + (21f / 30 / 1.1f)) / 3);
        var catalog = Catalog(Profile("male", unarmed, equipped), Profile("female", unarmed, equipped));
        var character = Character(female, 57);
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = Weapon("right", "ranged1h", -.07),
            [EquipmentSlot.OffHand] = Weapon("left", "ranged1h", -.07)
        };

        var loadout = new CharacterStatEngine().Calculate(character, slots, catalog);
        var rotation = SkillRotationFactory.Build(character, catalog, null, slots);
        var report = new CombatSimulator().Simulate(character, loadout, new CombatScenario(), rotation);

        Assert.Equal(.86, loadout.Stats["weaponAttackSpeedFactor"], 10);
        Assert.Equal(unarmed / equipped, loadout.Stats["weaponAttackAnimationRatio"], 10);
        Assert.Equal(134.804996, 100 * 1.57 * .86 * loadout.Stats["weaponAttackAnimationRatio"], 5);
        // Two equal 100-damage weapons produce 150 average direct damage per action.
        Assert.InRange(report.UnmitigatedDps / 150, 2.022075 - 1e-7, 2.022075 + 1e-7);
    }

    [Theory]
    [InlineData(false, .5, .8)]
    [InlineData(true, .75, .9)]
    public void UsesAnimationTableReferencedByTheCharactersSex(bool female, double unarmed, double equipped)
    {
        var catalog = Catalog(Profile("male", .5, .8), Profile("female", .75, .9));
        var character = Character(female, 0);
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = Weapon("right", "ranged1h", 0)
        };

        var loadout = new CharacterStatEngine().Calculate(character, slots, catalog);
        var rotation = SkillRotationFactory.Build(character, catalog, null, slots);

        Assert.Equal(.95 / unarmed, rotation.BaseAttacksPerSecond, 10);
        Assert.Equal(unarmed / equipped, loadout.Stats["weaponAttackAnimationRatio"], 10);
    }

    [Fact]
    public void AttackSpeedCapAppliesBeforeTheAnimationDurationRatio()
    {
        var catalog = Catalog(Profile("male", .5, .25), Profile("female", .5, .25));
        var character = Character(false, 250);
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = Weapon("right", "ranged1h", 0)
        };
        var loadout = new CharacterStatEngine().Calculate(character, slots, catalog);
        var rotation = SkillRotationFactory.Build(character, catalog, null, slots);

        var report = new CombatSimulator().Simulate(character, loadout, new CombatScenario(), rotation);

        // Clamp 3.5 to 2, then multiply U/W=2 and base frequency=.95/.5.
        Assert.Equal(100 * 2 * 2 * 1.9, report.UnmitigatedDps, 8);
    }

    [Fact]
    public void OnlyDualMeleeAppliesTheDatabaseBaseSpeedAveragingFactor()
    {
        var catalog = Catalog(Profile("male", .5, .5), Profile("female", .5, .5));
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant>
        {
            [EquipmentSlot.MainHand] = Weapon("right", "sword", -.1),
            [EquipmentSlot.OffHand] = Weapon("left", "sword", -.2)
        };

        var loadout = new CharacterStatEngine().Calculate(Character(false, 0), slots, catalog);

        // Fixture deliberately uses .25 to prove this comes from the DBR.
        Assert.Equal(1 + (-.1 - .2) * .25, loadout.Stats["weaponAttackSpeedFactor"], 10);
    }

    private static CharacterSnapshot Character(bool female, double bonus) => new()
    {
        Name = "animation", SaveFilePath = "fixture.gdc", IsFemale = female, CanDualWield = true,
        BaselineStats = new(new Dictionary<string, double> { ["attackSpeed"] = bonus })
    };

    private static LoadoutVariant Weapon(string id, string qualifier, double baseSpeed) => new(new ItemInstance
    {
        Id = id, Name = id, BaseRecord = id, Slot = EquipmentSlot.MainHand, AttachmentQualifier = qualifier,
        Source = new(ItemSourceKind.Equipped, "fixture.gdc"),
        Stats = new(new Dictionary<string, double>
        {
            ["weaponBaseAttackSpeed"] = baseSpeed,
            [StatKeys.WeaponDamage("physical")] = 100
        })
    }, null, null);

    private static AnimationSpeedProfile Profile(string name, double unarmed, double equipped) => new()
    {
        TableRecordName = name,
        AttackGroups = new Dictionary<string, AnimationTimingGroup>
        {
            ["unarmed"] = new("unarmed", unarmed, []),
            ["ranged1h"] = new("ranged1h", equipped, []),
            ["dualRanged"] = new("dualRanged", equipped, []),
            ["dHanded"] = new("dHanded", equipped, [])
        }
    };

    private static GameDataCatalog Catalog(AnimationSpeedProfile male, AnimationSpeedProfile female) => new()
    {
        AnimationProfiles = new Dictionary<string, AnimationSpeedProfile>
        {
            [male.TableRecordName] = male,
            [female.TableRecordName] = female
        },
        Records = new Dictionary<string, GameRecord>
        {
            ["records/creatures/pc/malepc01.dbr"] = Pc("records/creatures/pc/malepc01.dbr", male.TableRecordName),
            ["records/creatures/pc/femalepc01.dbr"] = Pc("records/creatures/pc/femalepc01.dbr", female.TableRecordName),
            ["records/game/gameengine.dbr"] = new()
            {
                RecordName = "records/game/gameengine.dbr",
                NumericValues = new(new Dictionary<string, double> { ["dwWeaponSpeedFactor"] = .25 })
            }
        }
    };

    private static GameRecord Pc(string name, string table) => new()
    {
        RecordName = name,
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["charAnimationTableName"] = [table] }
    };
}
