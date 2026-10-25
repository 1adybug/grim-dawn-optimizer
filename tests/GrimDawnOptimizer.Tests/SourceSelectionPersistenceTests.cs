using System.Reflection;
using GrimDawnOptimizer.App.Services;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SourceSelectionPersistenceTests
{
    [Fact]
    public async Task ApplyingDirectoriesReplacesEachRememberedPathAndRestoresOnlyTheLatest()
    {
        using var fixture = new Fixture();
        var model = fixture.CreateModel();
        await model.InitializeAsync();
        await model.UseGameFolderAsync(fixture.GameA);
        await model.UseSaveFolderAsync(fixture.SaveA);
        await model.UseGameFolderAsync(fixture.GameB);
        await model.UseSaveFolderAsync(fixture.SaveB);
        await model.FlushSourceHistoryAsync();
        var stored = await fixture.Store.LoadAsync();
        Assert.Equal([fixture.GameB], stored.GameDirectories);
        Assert.Equal([fixture.SaveB], stored.SaveDirectories);
        Assert.Equal(fixture.GameB, stored.LastGameDirectory);
        Assert.Equal(fixture.SaveB, stored.LastSaveDirectory);
        // Existing empty-directory application rules remain unchanged.
        Assert.Empty(model.SelectedInstallation!.DataFiles);
        Assert.Empty(model.SelectedSaveLocation!.CharacterFiles);

        var restarted = fixture.CreateModel();
        await restarted.InitializeAsync();
        Assert.Equal(fixture.GameB, Assert.Single(restarted.Installations).RootPath);
        Assert.Equal(fixture.SaveB, Assert.Single(restarted.SaveLocations).RootPath);
    }

    [Fact]
    public async Task DiscoveryRetainsManualChoiceButKeepsExistingRunningAndCloudPriorityAfterRestart()
    {
        using var fixture = new Fixture();
        fixture.Source.Result = new DiscoveryResult(
            [GameDataSource.CreateInstallation(fixture.GameA), GameDataSource.CreateInstallation(fixture.GameB)],
            [new(fixture.SaveA, "fixture", [], null, null), new(fixture.SaveB, "fixture", [], null, null)])
        {
            RunningGameRoot = fixture.GameB,
            PreferredSaveRoot = fixture.SaveB
        };
        var model = fixture.CreateModel();
        await model.InitializeAsync();
        Assert.Equal(fixture.GameB, model.SelectedInstallation!.RootPath);
        await model.UseGameFolderAsync(fixture.GameA);
        await model.UseSaveFolderAsync(fixture.SaveA);
        await Discover(model);
        Assert.Equal(fixture.GameA, model.SelectedInstallation!.RootPath);
        Assert.Equal(fixture.SaveA, model.SelectedSaveLocation!.RootPath);
        Assert.Equal([fixture.GameA], (await fixture.Store.LoadAsync()).GameDirectories);

        // This change does not promote a previous run's history to an explicit
        // selection: current running/cloud evidence still has its original priority.
        var restarted = fixture.CreateModel();
        await restarted.InitializeAsync();
        Assert.Equal(fixture.GameB, restarted.SelectedInstallation!.RootPath);
        Assert.Equal(fixture.SaveB, restarted.SelectedSaveLocation!.RootPath);
        Assert.Equal([fixture.GameB], (await fixture.Store.LoadAsync()).GameDirectories);
        Assert.Equal([fixture.SaveB], (await fixture.Store.LoadAsync()).SaveDirectories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledDiscoveryPreservesLastAppliedPaths(bool cancel)
    {
        using var fixture = new Fixture();
        var model = fixture.CreateModel();
        await model.InitializeAsync();
        await model.UseGameFolderAsync(fixture.GameA);
        await model.UseSaveFolderAsync(fixture.SaveA);
        var before = await File.ReadAllBytesAsync(fixture.HistoryPath);
        fixture.Source.Failure = cancel ? new OperationCanceledException() : new IOException("fixture discovery failure");
        await Discover(model);
        Assert.Equal(fixture.GameA, model.SelectedInstallation!.RootPath);
        Assert.Equal(fixture.SaveA, model.SelectedSaveLocation!.RootPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.HistoryPath));
    }

    [Fact]
    public async Task LegacyDeleteCanSelectItsFallbackWithoutRetainingRemovedHistoryOrDeletingFiles()
    {
        using var fixture = new Fixture();
        var model = fixture.CreateModel();
        await model.InitializeAsync();
        await model.UseGameFolderAsync(fixture.GameA);
        await model.UseGameFolderAsync(fixture.GameB);
        await model.UseSaveFolderAsync(fixture.SaveA);
        await model.UseSaveFolderAsync(fixture.SaveB);
        model.DeleteGameDirectoryCommand.Execute(model.SelectedInstallation);
        model.DeleteSaveDirectoryCommand.Execute(model.SelectedSaveLocation);
        await model.FlushSourceHistoryAsync();
        var stored = await fixture.Store.LoadAsync();
        Assert.Equal([fixture.GameA], stored.GameDirectories);
        Assert.Equal([fixture.SaveA], stored.SaveDirectories);
        Assert.Empty(stored.RemovedGameDirectories);
        Assert.Empty(stored.RemovedSaveDirectories);
        Assert.True(Directory.Exists(fixture.GameB));
        Assert.True(Directory.Exists(fixture.SaveB));
    }

    private static Task Discover(MainViewModel model) =>
        (Task)typeof(MainViewModel).GetMethod("DiscoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gd-source-selection-" + Guid.NewGuid().ToString("N"));
        public string GameA => Path.Combine(_root, "game-a");
        public string GameB => Path.Combine(_root, "game-b");
        public string SaveA => Path.Combine(_root, "save-a");
        public string SaveB => Path.Combine(_root, "save-b");
        public string HistoryPath => Path.Combine(_root, "source-history.json");
        public TestSource Source { get; } = new();
        public JsonSourceHistoryStore Store { get; }

        public Fixture()
        {
            foreach (var directory in new[] { GameA, GameB, SaveA, SaveB }) Directory.CreateDirectory(directory);
            Store = new(HistoryPath);
        }

        public MainViewModel CreateModel() => new(Source, new SaveReader(),
            new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator()),
            new JsonProfileStore(Path.Combine(_root, "profiles.json")), Store,
            new UiPreferencesStore(Path.Combine(_root, "ui.json")));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class TestSource : IGameDataSource
    {
        public DiscoveryResult Result { get; set; } = new([], []);
        public Exception? Failure { get; set; }
        public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(Result) : Task.FromException<DiscoveryResult>(Failure);
        public Task<GameDataCatalog> LoadCatalogAsync(GameInstallation installation, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No import expected in path selection tests.");
        public IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog) => [];
    }
}
