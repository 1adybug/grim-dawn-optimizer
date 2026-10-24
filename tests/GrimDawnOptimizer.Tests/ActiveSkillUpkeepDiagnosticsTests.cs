using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ActiveSkillUpkeepDiagnosticsTests
{
    private const string Aura = "skills/aura.dbr";
    private const string Buff = "skills/aura_buff.dbr";

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void LearnedToggleWarnsOnlyWhileEnabled(bool enabled, bool expectedWarning)
    {
        var catalog = Catalog("Skill_BuffSelfToggled", [4]);
        var character = Character(new CharacterSkill(Aura, "aura", 1, 0, enabled, "", ""));

        var warnings = Upkeep(Calculate(character, catalog));

        Assert.Equal(expectedWarning ? 1 : 0, warnings.Length);
        foreach (var warning in warnings)
        {
            Assert.Equal([CalculationDiagnostics.CombatDps], warning.AffectedMetrics);
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.False(CalculationDiagnostics.AffectsPanel(warning));
        }
    }

    [Fact]
    public void UpkeepDiagnosticUsesTheCandidateAdjustedLevel()
    {
        var catalog = Catalog("Skill_BuffSelfToggled", [0, 4]);
        var character = Character(new CharacterSkill(Aura, "aura", 1, 0, true, "", ""));
        var item = Item(new Dictionary<string, double> { ["allSkills"] = 1 });

        Assert.Empty(Upkeep(Calculate(character, catalog)));
        Assert.Single(Upkeep(Calculate(character, catalog, item)));
    }

    [Fact]
    public void TemporaryGrantWarnsOnlyWhenExplicitlyActiveAndStillOwned()
    {
        var catalog = Catalog("Skill_BuffSelfDuration", [3], granted: true);
        var character = Character();
        var item = Item([]);

        Assert.Empty(Upkeep(Calculate(character, catalog, item)));
        character.ActiveItemSkillRecords = new HashSet<string> { Aura };
        Assert.Single(Upkeep(Calculate(character, catalog, item)));
        Assert.Empty(Upkeep(Calculate(character, catalog)));
        character.ActiveItemSkillRecords = new HashSet<string>();
        Assert.Empty(Upkeep(Calculate(character, catalog, item)));
    }

    [Fact]
    public void OrdinaryAttackCostIsNotReportedAsAnActiveBuffUpkeep()
    {
        var catalog = Catalog("Skill_AttackRadius", [4]);
        var character = Character(new CharacterSkill(Aura, "attack", 1, 0, true, "", ""));

        Assert.Empty(Upkeep(Calculate(character, catalog)));
    }

    private static UnsupportedEffect[] Upkeep(CalculatedLoadout result) => result.UnsupportedEffects
        .Where(effect => effect.Description.Contains("skillActiveManaCost", StringComparison.Ordinal)).ToArray();

    private static CalculatedLoadout Calculate(CharacterSnapshot character, GameDataCatalog catalog, ItemInstance? item = null) =>
        new CharacterStatEngine().Calculate(character, item is null ? [] :
            new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.Head] = new(item, null, null) }, catalog);

    private static CharacterSnapshot Character(params CharacterSkill[] skills) => new()
    { Name = "fixture", SaveFilePath = "fixture.gdc", Level = 64, Skills = skills };

    private static ItemInstance Item(Dictionary<string, double> stats) => new()
    {
        Id = "item", Name = "item", BaseRecord = "items/grant.dbr", Slot = EquipmentSlot.Head,
        Stats = new(stats), Source = new(ItemSourceKind.Backpack, "fixture.gdc")
    };

    private static GameDataCatalog Catalog(string type, double[] costs, bool granted = false) => new()
    {
        Records = new Dictionary<string, GameRecord>
        {
            [Aura] = new()
            {
                RecordName = Aura, RecordType = type,
                NumericValues = new(new Dictionary<string, double> { ["skillActiveDuration"] = type.Contains("Duration") ? 5 : 0 }),
                TextValues = new Dictionary<string, IReadOnlyList<string>> { ["buffSkillName"] = [Buff] }
            },
            [Buff] = new()
            {
                RecordName = Buff, RecordType = "SkillBuff_Passive",
                NumericValues = new(new Dictionary<string, double> { ["skillActiveManaCost"] = costs[0] }),
                NumericSeries = new Dictionary<string, IReadOnlyList<double>> { ["skillActiveManaCost"] = costs }
            },
            ["items/grant.dbr"] = new()
            {
                RecordName = "items/grant.dbr",
                NumericValues = new(new Dictionary<string, double> { ["augmentAllLevel"] = 1 }),
                TextValues = granted ? new Dictionary<string, IReadOnlyList<string>> { ["itemSkillName"] = [Aura] } : []
            }
        }
    };
}
