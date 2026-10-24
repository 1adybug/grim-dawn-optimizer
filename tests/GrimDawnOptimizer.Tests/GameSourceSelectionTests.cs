using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class GameSourceSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CurrentRunOverridesOldHistoryButPreservesAnExplicitSelection()
    {
        var oldGame = Path.Combine(_root, "Grim.Dawn");
        var currentGame = Path.Combine(_root, "Grim Dawn");
        var available = new[] { oldGame, currentGame };

        Assert.Equal(currentGame, GameSourceSelection.ChooseRoot(available, null, currentGame, oldGame));
        Assert.Equal(oldGame, GameSourceSelection.ChooseRoot(available, oldGame, currentGame, oldGame));
        Assert.Equal(oldGame, GameSourceSelection.ChooseRoot(available, null, null, oldGame));
    }

    [Fact]
    public void RestartCanChangeTheAutomaticallySelectedInstallation()
    {
        var gameA = Path.Combine(_root, "A");
        var gameB = Path.Combine(_root, "B");
        var available = new[] { gameA, gameB };
        var firstRun = GameSourceSelection.ChooseRoot(available, null, gameA, gameB);

        Assert.Equal(gameA, firstRun);
        Assert.Equal(gameB, GameSourceSelection.ChooseRoot(available, null, gameB, firstRun));
    }

    [Fact]
    public void SelectionDoesNotReturnARootThatWasRemovedOrIsUnavailable()
    {
        var available = Path.Combine(_root, "Available");
        var removed = Path.Combine(_root, "Removed");

        Assert.Equal(available, GameSourceSelection.ChooseRoot([available], removed, removed, removed));
        Assert.Null(GameSourceSelection.ChooseRoot([], null, removed, removed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningExecutableResolvesTheSharedDatabaseForBothArchitectures(bool x64)
    {
        var game = Path.Combine(_root, "Grim Dawn");
        Directory.CreateDirectory(Path.Combine(game, "database"));
        File.WriteAllText(Path.Combine(game, "database", "database.arz"), "fixture");
        var exe = x64 ? Path.Combine(game, "x64", "Grim Dawn.exe") : Path.Combine(game, "Grim Dawn.exe");

        Assert.Equal(game, GameSourceSelection.FindInstallationRoot(exe));
        Assert.Null(GameSourceSelection.FindInstallationRoot(Path.Combine(game, "Another Game.exe")));
        Assert.Null(GameSourceSelection.FindInstallationRoot(Path.Combine(_root, "Missing", "Grim Dawn.exe")));
    }

    [Theory]
    [InlineData("cloudSaving               = false\r\n", false)]
    [InlineData("cloudSaving = true\n", true)]
    [InlineData("other = true\n", null)]
    [InlineData("cloudSaving = maybe\n", null)]
    public void OnlyAnExplicitCloudSavingSettingProvidesSaveSourceEvidence(string options, bool? expected)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "options.txt");
        File.WriteAllText(path, options);

        Assert.Equal(expected, GameSourceSelection.ReadCloudSavingSetting(path));
        Assert.Null(GameSourceSelection.ReadCloudSavingSetting(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void DisabledCloudSavingSelectsLocalDocumentsInsteadOfHistoricSaveCopy()
    {
        var local = new SaveLocation(Path.Combine(_root, "Documents", "save"), "本地文档", [], null, null);
        var old = new SaveLocation(Path.Combine(_root, "OldSteamCopy", "save"), "历史目录", [], null, null);
        var preferred = GameSourceSelection.FindPreferredSaveRoot([old, local], false, local.RootPath);

        Assert.Equal(local.RootPath, preferred);
        Assert.Equal(local.RootPath, GameSourceSelection.ChooseRoot(
            [old.RootPath, local.RootPath], null, preferred, old.RootPath));
    }

    [Fact]
    public void AmbiguousSteamAccountsDoNotGuessTheCurrentAccountFromFileDates()
    {
        var first = new SaveLocation(Path.Combine(_root, "User1"), "Steam Cloud", [], null, null);
        var second = new SaveLocation(Path.Combine(_root, "User2"), "Steam Cloud", [], null, null);

        Assert.Null(GameSourceSelection.FindPreferredSaveRoot([first, second], true, "local"));
        Assert.Null(GameSourceSelection.FindPreferredSaveRoot([first], null, "local"));
        Assert.Equal(first.RootPath, GameSourceSelection.FindPreferredSaveRoot([first], true, "local"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
