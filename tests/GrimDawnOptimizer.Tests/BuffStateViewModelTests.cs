using System.Reflection;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Tests;

public sealed class BuffStateViewModelTests
{
    [Fact]
    public void CandidateAndLearnedBuffsAreAvailableAndReloadClearsActivation()
    {
        const string granted = "records/skills/itemskills/timed.dbr";
        const string learned = "records/skills/playerclass02/timed.dbr";
        var owner = Record("records/items/unused.dbr", "Armor", new());
        ((Dictionary<string, IReadOnlyList<string>>)owner.TextValues)["itemSkillName"] = [granted];
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord>
            {
                [owner.RecordName] = owner,
                [granted] = Record(granted, "Skill_BuffSelfDuration", new() { ["skillActiveDuration"] = 3, ["characterAttackSpeedModifier"] = 10 }),
                [learned] = Record(learned, "Skill_BuffSelfDuration", new() { ["skillActiveDuration"] = 5, ["characterAttackSpeedModifier"] = 15 })
            }
        };
        var item = new ItemInstance
        {
            Id = "unused", Name = "Inventory item", BaseRecord = owner.RecordName, Slot = EquipmentSlot.Chest,
            Source = new ItemSourceLocation(ItemSourceKind.Backpack, "fixture.gdc")
        };
        var character = new CharacterSnapshot
        {
            Name = "fixture", SaveFilePath = "fixture.gdc", Level = 64, Items = [item],
            Skills = [new(learned, "Learned buff", 1, 0, true, "", "")]
        };
        var viewModel = new MainViewModel();
        typeof(MainViewModel).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, catalog);
        viewModel.SelectedCharacter = character;

        Assert.True(viewModel.HasItemBuffStates);
        Assert.Contains(viewModel.ItemBuffStates, x => x.Record == granted);
        Assert.Contains(viewModel.ItemBuffStates, x => x.Record == learned);
        Assert.All(viewModel.ItemBuffStates, x => Assert.False(x.IsActive));
        viewModel.UsePanelDpsCalibration = true;
        viewModel.CurrentPanelDpsInput = "1000";
        viewModel.ItemBuffStates.Single(x => x.Record == granted).IsActive = true;
        Assert.Contains(granted, character.ActiveItemSkillRecords);
        Assert.False(viewModel.UsePanelDpsCalibration);
        Assert.Empty(viewModel.CurrentPanelDpsInput);

        viewModel.SelectedCharacter = null;
        viewModel.SelectedCharacter = character;
        Assert.Empty(character.ActiveItemSkillRecords);
        Assert.All(viewModel.ItemBuffStates, x => Assert.False(x.IsActive));
    }

    private static GameRecord Record(string name, string type, Dictionary<string, double> stats) => new()
    {
        RecordName = name, RecordType = type, NumericValues = new StatBlock(stats),
        TextValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Class"] = [type] }
    };
}
