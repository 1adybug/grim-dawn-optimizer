using System.Text.Json;
using GrimDawnOptimizer.App.Services;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Bridge;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;
using Xunit;

namespace GrimDawnOptimizer.Bridge.Tests;

public sealed class BridgeControllerTests
{
    [Fact]
    public Task SnapshotsHaveStableSessionIdentityAndIncreasingRevisions() => InContext(async (bridge, _, _) =>
    {
        var initial = State(bridge);
        await bridge.ExecuteAsync("demo", default);
        var next = State(bridge);
        var repeated = State(bridge);
        Assert.True(Guid.TryParseExact(initial.GetProperty("sessionId").GetString(), "N", out _));
        Assert.Equal(initial.GetProperty("sessionId").GetString(), next.GetProperty("sessionId").GetString());
        Assert.True(next.GetProperty("revision").GetInt64() > initial.GetProperty("revision").GetInt64());
        Assert.True(repeated.GetProperty("revision").GetInt64() > next.GetProperty("revision").GetInt64());
    });

    [Fact]
    public Task DemoUsesRealPreviewAndAcceptsExplicitDefaultAttack() => InContext(async (bridge, model, _) =>
    {
        await bridge.ExecuteAsync("demo", default);
        await bridge.ExecuteAsync("configure", Payload(new { skillRecord = "", goalIndex = 1, difficultyIndex = 3 }));
        var state = State(bridge);
        Assert.True(state.GetProperty("ready").GetBoolean());
        Assert.True(state.GetProperty("canOptimize").GetBoolean());
        Assert.False(state.GetProperty("canRefresh").GetBoolean());
        Assert.Equal("", state.GetProperty("selectedSkillRecord").GetString());
        Assert.NotEqual("—", state.GetProperty("panel").GetProperty("dps").GetString());
        Assert.Equal(14, state.GetProperty("equipment").GetArrayLength());
        Assert.Equal(3, model.SelectedDifficulty);
    });

    [Fact]
    public Task InvalidConfigurationDoesNotPartiallyModifyTheBuild() => InContext(async (bridge, model, _) =>
    {
        await bridge.ExecuteAsync("demo", default);
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.ExecuteAsync("configure",
            Payload(new { goalIndex = 2, durationSeconds = 0 })));
        Assert.Equal(0, model.SelectedGoalIndex);
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.ExecuteAsync("configure",
            Payload(new { goalIndex = 1, resistanceDebuffs = new { unknown = 12 } })));
        Assert.Equal(0, model.SelectedGoalIndex);
    });

    [Fact]
    public Task RunningOptimizationRejectsMutationsAndCanBeCancelled() => InContext(async (bridge, _, optimizer) =>
    {
        await bridge.ExecuteAsync("demo", default);
        await bridge.ExecuteAsync("optimize", default);
        await optimizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(State(bridge).GetProperty("isBusy").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync("configure", Payload(new { goalIndex = 2 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync("demo", default));
        await bridge.ExecuteAsync("cancel", default);
        for (var attempts = 0; bridge.IsBusy && attempts < 100; attempts++) await Task.Delay(20);
        Assert.False(bridge.IsBusy);
        Assert.Equal("优化已取消", State(bridge).GetProperty("status").GetString());
        Assert.Equal(0, State(bridge).GetProperty("results").GetArrayLength());
    });

    [Fact]
    public Task SnapshotSerializesInfinityAndRetainsSingleSwapDiagnostics() => InContext(async (bridge, model, _) =>
    {
        await bridge.ExecuteAsync("demo", default);
        var result = new ResultViewModel(new LoadoutResult
        {
            Rank = 1,
            Loadout = new CalculatedLoadout { Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = new StatBlock() },
            Simulation = new SimulationReport { TimeToDeathSeconds = double.PositiveInfinity, EffectiveHealth = double.PositiveInfinity, UnmitigatedDps = 23478.4 },
            Metrics = new Dictionary<string, double>(), TierScores = [1], Confidence = ConfidenceLevel.High,
            SingleItemComparisons = [new ItemSwapComparison(EquipmentSlot.Head, "测试头盔", true, "单换对照", new Dictionary<string, double> { ["dps"] = 100 }, new Dictionary<string, double> { ["dps"] = 120 })]
        }, model.SelectedCharacter!, model.Metrics.ToArray());
        model.Results.Add(result);
        await bridge.ExecuteAsync("selectResult", Payload(new { rank = 1 }));
        await bridge.ExecuteAsync("compareItem", Payload(new { index = 0, metricKey = "dps", actualInput = "90" }));
        var candidate = State(bridge).GetProperty("results")[0];
        Assert.Equal("∞", candidate.GetProperty("timeToDeath").GetString());
        Assert.Equal("∞", candidate.GetProperty("effectiveHealth").GetString());
        Assert.Contains("增减方向不符", candidate.GetProperty("singleSwaps")[0].GetProperty("rows")[0].GetProperty("deviation").GetString());
        await bridge.ExecuteAsync("configure", Payload(new { difficultyIndex = 1 }));
        Assert.Equal(0, State(bridge).GetProperty("results").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, State(bridge).GetProperty("selectedResultRank").ValueKind);
    });

    [Fact]
    public Task StaleDirectoryHistoryCanBeRemovedWithoutDiskWrites() => InContext(async (bridge, model, _) =>
    {
        var path = Path.Combine(Path.GetTempPath(), "grim-absent-" + Guid.NewGuid());
        var source = new GameInstallation(path, "历史目录", "", []);
        model.Installations.Add(source);
        model.SelectedInstallation = source;
        await bridge.ExecuteAsync("deleteGameDirectory", Payload(new { path }));
        Assert.DoesNotContain(source, model.Installations);
        Assert.False(Directory.Exists(path));
    });

    [Fact]
    public Task UnknownCommandsAndMissingSelectionsAreRejected() => InContext(async (bridge, _, _) =>
    {
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.ExecuteAsync("runShell", Payload(new { command = "anything" })));
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.ExecuteAsync("selectCharacter", Payload(new { id = "missing" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync("optimize", default));
    });

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement State(BridgeController bridge) => JsonSerializer.SerializeToElement(bridge.Snapshot(), Program.JsonOptions);

    private static Task InContext(Func<BridgeController, MainViewModel, BlockingOptimizer, Task> test) => Task.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "grim-bridge-tests-" + Guid.NewGuid());
        using var context = new SingleThreadContext();
        Exception? failure = null;
        context.UnhandledException += error => { failure = error; context.Complete(); };
        context.Post(async _ =>
        {
            var optimizer = new BlockingOptimizer();
            var model = new MainViewModel(new EmptyDataSource(), new SaveReader(), optimizer,
                new JsonProfileStore(Path.Combine(directory, "profiles.json")),
                new JsonSourceHistoryStore(Path.Combine(directory, "history.json")),
                new UiPreferencesStore(Path.Combine(directory, "ui.json")));
            var bridge = new BridgeController(model);
            try
            {
                await bridge.InitializeAsync();
                await test(bridge, model, optimizer);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { await bridge.ShutdownAsync(); }
                catch (Exception error) { failure ??= error; }
                context.Complete();
            }
        }, null);
        context.Run();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });

    private sealed class EmptyDataSource : IGameDataSource
    {
        public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiscoveryResult([], []));
        public Task<GameDataCatalog> LoadCatalogAsync(GameInstallation installation, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new GameDataCatalog());
        public IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog) => [];
    }

    private sealed class BlockingOptimizer : ILoadoutOptimizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OptimizationRun> OptimizeAsync(CharacterSnapshot character, IReadOnlyList<ItemInstance> itemPool,
            IReadOnlyList<AttachmentDefinition> attachments, GameDataCatalog catalog, OptimizationProfile profile,
            IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
