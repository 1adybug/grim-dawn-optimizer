using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class OptimizerDualWieldTests
{
    private const string DualSkill = "records/skills/itemskills/granted/test_dual_ranged.dbr";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoOrdinaryWeaponsStillProduceBothSingleHandPlans(bool hasCatalog)
    {
        var first = Item("first", EquipmentSlot.MainHand, 100);
        var second = Item("second", EquipmentSlot.MainHand, 90);
        var character = Character(first, second);
        var catalog = hasCatalog ? Catalog() : new GameDataCatalog();

        var run = await Search(character, catalog);

        Assert.Equal(2, run.Results.Count);
        Assert.Equal(new[] { first.Id, second.Id }, run.Results
            .Select(x => x.Loadout.Slots[EquipmentSlot.MainHand].Item.Id).Order().ToArray());
        Assert.All(run.Results, result =>
        {
            Assert.True(result.Loadout.IsValid, string.Join("; ", result.Loadout.ValidationErrors));
            Assert.False(result.Loadout.Slots.ContainsKey(EquipmentSlot.OffHand));
        });
    }

    [Theory]
    [InlineData(EquipmentSlot.Chest)]
    [InlineData(EquipmentSlot.MainHand)]
    public async Task SearchCanGainDualWieldFromACandidateIncludingTheSecondWeapon(EquipmentSlot grantSlot)
    {
        var first = Item("first", EquipmentSlot.MainHand, 100);
        var second = Item("second", EquipmentSlot.MainHand, 90);
        var grant = grantSlot == EquipmentSlot.MainHand ? second : Item("grant", grantSlot, 0);
        var items = grantSlot == EquipmentSlot.MainHand ? new[] { first, second } : new[] { first, second, grant };
        var character = Character(items);
        var catalog = Catalog(grant);

        var run = await Search(character, catalog);

        Assert.False(character.CanDualWield);
        Assert.Contains(run.Results, result =>
            result.Loadout.Slots[EquipmentSlot.MainHand].Item.Id == first.Id &&
            result.Loadout.Slots.TryGetValue(EquipmentSlot.OffHand, out var offHand) && offHand.Item.Id == second.Id);
        Assert.All(run.Results, result => Assert.True(result.Loadout.IsValid,
            string.Join("; ", result.Loadout.ValidationErrors)));
    }

    [Fact]
    public async Task ReplacingTheGrantKeepsLegalSingleHandPlansAndUsesAvailableShields()
    {
        var first = Item("first", EquipmentSlot.MainHand, 100);
        var second = Item("second", EquipmentSlot.MainHand, 90);
        var grant = Item("grant", EquipmentSlot.Chest, 0);
        var armor = Item("armor", EquipmentSlot.Chest, 0);
        var shield = Item("shield", EquipmentSlot.OffHand, 0);
        var character = Character(first, second, grant, armor, shield);
        var catalog = Catalog(grant);

        var run = await Search(character, catalog);

        Assert.Contains(run.Results, result =>
            result.Loadout.Slots[EquipmentSlot.Chest].Item.Id == armor.Id &&
            result.Loadout.Slots[EquipmentSlot.OffHand].Item.Id == shield.Id);
        Assert.Contains(run.Results, result =>
            result.Loadout.Slots[EquipmentSlot.Chest].Item.Id == grant.Id &&
            result.Loadout.Slots[EquipmentSlot.OffHand].Item.Slot == EquipmentSlot.MainHand);
        Assert.All(run.Results, result =>
        {
            Assert.True(result.Loadout.IsValid, string.Join("; ", result.Loadout.ValidationErrors));
            if (result.Loadout.Slots[EquipmentSlot.Chest].Item.Id == armor.Id)
                Assert.Equal(shield.Id, result.Loadout.Slots[EquipmentSlot.OffHand].Item.Id);
        });
    }

    private static Task<OptimizationRun> Search(CharacterSnapshot character, GameDataCatalog catalog) =>
        new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator(), maxDegreeOfParallelism: 2)
            .OptimizeAsync(character, character.Items, [], catalog, new OptimizationProfile
            {
                SearchBudgetSeconds = 1,
                ResultCount = 20,
                MonteCarloTrials = 500,
                Metrics = [new MetricRule { Key = "dps", DisplayName = "DPS", Weight = 1 }]
            });

    private static CharacterSnapshot Character(params ItemInstance[] items) => new()
    {
        Name = "candidate-dual", SaveFilePath = "test.gdc", Level = 100,
        Physique = 1000, Cunning = 1000, Spirit = 1000,
        Items = items,
        CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = items[0].Id },
        BaselineStats = new StatBlock(new Dictionary<string, double>
        {
            ["health"] = 10_000, ["offensiveAbility"] = 3000, ["defensiveAbility"] = 2800
        })
    };

    private static ItemInstance Item(string id, EquipmentSlot slot, double damage) => new()
    {
        Id = id, Name = id, Slot = slot, BaseRecord = $"records/items/test/{id}.dbr", Seed = 1,
        AttachmentQualifier = slot == EquipmentSlot.MainHand ? "ranged1h" : string.Empty,
        Stats = new StatBlock(new Dictionary<string, double> { [StatKeys.WeaponDamage("physical")] = damage }),
        Source = new ItemSourceLocation(ItemSourceKind.Backpack, "test.gdc")
    };

    private static GameDataCatalog Catalog(ItemInstance? grant = null)
    {
        var records = new Dictionary<string, GameRecord>
        {
            [DualSkill] = new()
            {
                RecordName = DualSkill, RecordType = "Skill_PassiveDualWieldWeapon",
                NumericValues = new StatBlock(new Dictionary<string, double> { ["dualRangedOnly"] = 1 })
            }
        };
        if (grant is not null)
            records[grant.BaseRecord] = new GameRecord
            {
                RecordName = grant.BaseRecord,
                TextValues = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["itemSkillName"] = [DualSkill], ["itemSkillLevelEq"] = ["1"]
                }
            };
        return new GameDataCatalog { Records = records };
    }
}
