using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Tests;

public sealed class OptionalDpsCalibrationViewModelTests
{
    [Fact]
    public void DpsInputsAreRequiredOnlyAfterExplicitOptIn()
    {
        var viewModel = new MainViewModel();
        viewModel.DemoCommand.Execute(null);

        Assert.False(viewModel.UsePanelDpsCalibration);
        Assert.True(viewModel.OptimizeCommand.CanExecute(null));
        Assert.False(viewModel.IsCurrentPanelDpsInvalid);

        viewModel.UsePanelDpsCalibration = true;
        Assert.False(viewModel.OptimizeCommand.CanExecute(null));
        Assert.True(viewModel.IsCurrentPanelDpsInvalid);
        viewModel.CurrentPanelDpsInput = "1000";
        viewModel.CurrentWeaponAttackDpsInput = "500";
        Assert.True(viewModel.OptimizeCommand.CanExecute(null));

        viewModel.CurrentPanelDpsInput = "invalid";
        viewModel.CurrentWeaponAttackDpsInput = "invalid";
        Assert.False(viewModel.OptimizeCommand.CanExecute(null));
        viewModel.UsePanelDpsCalibration = false;
        Assert.True(viewModel.OptimizeCommand.CanExecute(null));
        Assert.False(viewModel.IsCurrentPanelDpsInvalid);
        Assert.False(viewModel.IsCurrentWeaponAttackDpsInvalid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingCharacterOrSkillResetsCalibrationConsentAndValues(bool changeSkill)
    {
        var viewModel = new MainViewModel();
        viewModel.DemoCommand.Execute(null);
        viewModel.UsePanelDpsCalibration = true;
        viewModel.CurrentPanelDpsInput = "1000";
        viewModel.CurrentWeaponAttackDpsInput = "500";

        if (changeSkill)
            viewModel.SelectedSkill = new CharacterSkill("records/skills/test/new.dbr", "New skill", 1, 0, true, "", "");
        else
            viewModel.DemoCommand.Execute(null);

        Assert.False(viewModel.UsePanelDpsCalibration);
        Assert.Empty(viewModel.CurrentPanelDpsInput);
        Assert.Empty(viewModel.CurrentWeaponAttackDpsInput);
        Assert.Equal(0, viewModel.Profile.CurrentPanelDps);
        Assert.Equal(0, viewModel.Profile.CurrentWeaponAttackDps);
        Assert.True(viewModel.OptimizeCommand.CanExecute(null));
    }
}
