using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SelectedGrantedAttackTests
{
    private const string Attack = "skills/granted_attack.dbr";

    [Fact]
    public void ExplicitGrantNotInLearnedSkillsUsesEachCandidatesOwnerLevel()
    {
        var low = Item("low", EquipmentSlot.Head);
        var high = Item("high", EquipmentSlot.Head);
        var catalog = Catalog(Owner(low, 1), Owner(high, 2), AttackRecord());
        var character = Character(low, high);

        var first = Selected(character, catalog, Slots(low));
        var second = Selected(character, catalog, Slots(high));

        Assert.Empty(character.Skills);
        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(Attack, first.MainSkillRecord);
        Assert.Equal(100, first.FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.Equal(300, second.FlatDamage[StatKeys.DamageFlat("fire")]);
    }

    [Fact]
    public void RemovingGrantRetainsSelectedIdentityAndExplicitlyDisablesTheAttack()
    {
        var source = Item("source", EquipmentSlot.Head);
        var catalog = Catalog(Owner(source, 1), AttackRecord());
        var character = Character(source);

        var unavailable = Selected(character, catalog, Slots());

        Assert.False(unavailable.IsAvailable);
        Assert.Equal(Attack, unavailable.MainSkillRecord);
        Assert.NotEmpty(unavailable.UnavailableReason);
        Assert.Equal(0, unavailable.WeaponDamagePercent);
        Assert.Empty(unavailable.WeaponPoolAttacks);
        Assert.Contains(unavailable.UnsupportedEffects, x => x.AffectedMetrics.Contains("panelDps"));
        var stats = new StatBlock(new Dictionary<string, double> { [StatKeys.WeaponDamage("physical")] = 1000 });
        var report = new CombatSimulator().Simulate(character,
            new() { Stats = stats, Slots = Slots() }, new(), unavailable);
        Assert.Equal(0, report.UnmitigatedDps);
    }

    [Fact]
    public void CandidateWeaponsMustMeetGrantedAttackRequirements()
    {
        var source = Item("source", EquipmentSlot.Head);
        var pistol = Item("pistol", EquipmentSlot.MainHand, "ranged1h");
        var rifle = Item("rifle", EquipmentSlot.MainHand, "ranged2h");
        var catalog = Catalog(Owner(source, 1), AttackRecord(requiredWeapon: "Ranged2h"));
        var character = Character(source, pistol, rifle);

        Assert.True(Selected(character, catalog, Slots(source, rifle)).IsAvailable);
        var unavailable = Selected(character, catalog, Slots(source, pistol));
        Assert.False(unavailable.IsAvailable);
        Assert.Contains("武器", unavailable.UnavailableReason);
    }

    [Fact]
    public void CandidateComponentCanSupplyTheSelectedGrant()
    {
        var weapon = Item("weapon", EquipmentSlot.MainHand, "ranged1h");
        var component = new AttachmentDefinition
        {
            Id = "component.dbr", Name = "component", Kind = AttachmentKind.Component
        };
        var owner = new GameRecord
        {
            RecordName = component.Id,
            TextValues = new Dictionary<string, IReadOnlyList<string>>
            { ["itemSkillName"] = [Attack], ["itemSkillLevelEq"] = ["2"] }
        };
        var catalog = Catalog(owner, AttackRecord());
        var character = Character(weapon);
        var slots = Slots(weapon);
        slots[EquipmentSlot.MainHand] = new(weapon, component, null);

        Assert.Equal(300, Selected(character, catalog, slots).FlatDamage[StatKeys.DamageFlat("fire")]);
        Assert.False(Selected(character, catalog, Slots(weapon)).IsAvailable);
    }

    [Fact]
    public void MissingExplicitRecordDoesNotBecomeDefaultAttack()
    {
        var character = Character();
        var unavailable = Selected(character, new(), Slots());
        var defaultAttack = SkillRotationFactory.BuildSelectedRecord(character, new(), "", "", Slots());

        Assert.False(unavailable.IsAvailable);
        Assert.Equal(Attack, unavailable.MainSkillRecord);
        Assert.True(defaultAttack.IsAvailable);
        Assert.Equal(100, defaultAttack.WeaponDamagePercent);
        Assert.Empty(defaultAttack.MainSkillRecord);
    }

    [Fact]
    public async Task OptimizerPreservesSelectedGrantAcrossCandidatesAndRejectsPlansWithoutIt()
    {
        var low = Item("low", EquipmentSlot.Head);
        var high = Item("high", EquipmentSlot.Head);
        var noGrant = Item("no-grant", EquipmentSlot.Head);
        var weapon = Item("weapon", EquipmentSlot.MainHand, "ranged1h");
        var catalog = Catalog(Owner(low, 1), Owner(high, 2), AttackRecord());
        var character = Character(low, high, noGrant, weapon);
        var profile = new OptimizationProfile
        {
            Rotation = new() { MainSkillRecord = Attack, MainSkillName = "Granted attack" },
            SearchBudgetSeconds = 2, ResultCount = 10, MonteCarloTrials = 500,
            Metrics = [new() { Key = "dps", DisplayName = "DPS", Weight = 1 }]
        };
        var simulator = new CombatSimulator();
        var run = await new BeamSearchOptimizer(new CharacterStatEngine(), simulator, 1)
            .OptimizeAsync(character, character.Items, [], catalog, profile);

        Assert.Equal(2, run.Results.Count);
        Assert.Contains(run.Results, result => result.Loadout.Slots[EquipmentSlot.Head].Item.Id == low.Id);
        Assert.Contains(run.Results, result => result.Loadout.Slots[EquipmentSlot.Head].Item.Id == high.Id);
        Assert.DoesNotContain(run.Results, result => result.Loadout.Slots[EquipmentSlot.Head].Item.Id == noGrant.Id);
        foreach (var result in run.Results)
        {
            Assert.True(result.Loadout.IsValid);
            var rotation = Selected(character, catalog, result.Loadout.Slots);
            var expected = simulator.Simulate(character, result.Loadout, profile.Scenario, rotation);
            Assert.Equal(expected.UnmitigatedDps, result.Simulation.UnmitigatedDps, 8);
            Assert.True(result.Simulation.UnmitigatedDps > 0);
        }
    }

    private static SkillRotationDefinition Selected(CharacterSnapshot character, GameDataCatalog catalog,
        IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> slots) =>
        SkillRotationFactory.BuildSelectedRecord(character, catalog, Attack, "Granted attack", slots);

    private static ItemInstance Item(string id, EquipmentSlot slot, string qualifier = "") => new()
    {
        Id = id, Name = id, Slot = slot, BaseRecord = $"items/{id}.dbr", AttachmentQualifier = qualifier,
        IsTwoHanded = qualifier.EndsWith("2h", StringComparison.Ordinal),
        Stats = new StatBlock(new Dictionary<string, double>
        { [StatKeys.WeaponDamage("physical")] = slot == EquipmentSlot.MainHand ? 20 : 0 }),
        Source = new(ItemSourceKind.Backpack, "fixture.gdc")
    };

    private static CharacterSnapshot Character(params ItemInstance[] items) => new()
    {
        Name = "fixture", SaveFilePath = "fixture.gdc", Level = 100, Items = items,
        BaselineStats = new StatBlock(new Dictionary<string, double>
        { ["health"] = 10000, ["offensiveAbility"] = 3000, ["defensiveAbility"] = 2800 }),
        CurrentLoadout = items.GroupBy(item => item.Slot).ToDictionary(group => group.Key, group => group.First().Id)
    };

    private static Dictionary<EquipmentSlot, LoadoutVariant> Slots(params ItemInstance[] items) =>
        items.ToDictionary(item => item.Slot, item => new LoadoutVariant(item, null, null));
    private static GameDataCatalog Catalog(params GameRecord[] records) => new()
    { Records = records.ToDictionary(record => record.RecordName, StringComparer.OrdinalIgnoreCase) };
    private static GameRecord Owner(ItemInstance item, int level) => new()
    {
        RecordName = item.BaseRecord,
        TextValues = new Dictionary<string, IReadOnlyList<string>>
        { ["itemSkillName"] = [Attack], ["itemSkillLevelEq"] = [level.ToString()] }
    };
    private static GameRecord AttackRecord(string requiredWeapon = "") => new()
    {
        RecordName = Attack, RecordType = "Skill_AttackWave",
        NumericValues = new StatBlock(new Dictionary<string, double>
        { ["offensiveFireMin"] = 100, [requiredWeapon] = string.IsNullOrEmpty(requiredWeapon) ? 0 : 1 }),
        NumericSeries = new Dictionary<string, IReadOnlyList<double>> { ["offensiveFireMin"] = [100, 300] }
    };
}
