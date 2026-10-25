using System.Reflection;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class AutomaticAnalysisViewModelTests
{
    [Fact]
    public async Task SavedDefaultAttackNeverSilentlySelectsAnotherLearnedSkill()
    {
        using var fixture = new Fixture();
        fixture.Reader.Characters = [fixture.Character(primary: "")];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");

        Assert.Equal("", fixture.ViewModel.SelectedSkill!.Record);
        Assert.Contains(fixture.ViewModel.ActiveSkills, x => x.Record == Fixture.Attack);
        Assert.Equal("", fixture.ViewModel.Profile.Rotation.MainSkillRecord);
        Assert.Equal(fixture.ViewModel.CurrentDefaultDpsDisplay, fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.True(fixture.ViewModel.OptimizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingSavedSkillStaysVisibleAndUnavailable()
    {
        using var fixture = new Fixture();
        fixture.Reader.Characters = [fixture.Character(primary: "records/missing.dbr")];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");

        Assert.Equal("records/missing.dbr", fixture.ViewModel.SelectedSkill!.Record);
        Assert.False(fixture.ViewModel.Profile.Rotation.IsAvailable);
        Assert.Equal("不可用", fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.True(fixture.ViewModel.HasCurrentPanelWarning);
        Assert.False(fixture.ViewModel.OptimizeCommand.CanExecute(null));
        Assert.Contains("缺失", fixture.ViewModel.RequiredStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshCannotReuseTheOldLevelOfAnUnlearnedAttack(bool zeroLevelEntryRemains)
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        Assert.Equal(Fixture.Attack, fixture.ViewModel.SelectedSkill!.Record);
        fixture.Reader.Characters = [fixture.Character(primary: "", skills: zeroLevelEntryRemains
            ? [new(Fixture.Attack, "Forgotten attack", 0, 0, true, "", "")] : [])];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");

        Assert.Equal(Fixture.Attack, fixture.ViewModel.SelectedSkill!.Record);
        Assert.Contains(fixture.ViewModel.SelectedSkill, fixture.ViewModel.ActiveSkills);
        Assert.False(fixture.ViewModel.Profile.Rotation.IsAvailable);
        Assert.Equal("不可用", fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.Contains("未学习", fixture.ViewModel.RequiredStatus);
        Assert.False(fixture.ViewModel.OptimizeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("goal")]
    [InlineData("duration")]
    [InlineData("targets")]
    public async Task ChangingAnalysisInputsClearsOldResults(string change)
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        Assert.Single(fixture.ViewModel.Results);
        Assert.True(fixture.ViewModel.IsResultsWorkspace);

        switch (change)
        {
            case "skill": fixture.ViewModel.SelectedSkill = fixture.ViewModel.ActiveSkills.Single(x => x.Record == ""); break;
            case "goal": fixture.ViewModel.SelectedGoalIndex = 1; break;
            case "duration": fixture.ViewModel.ScenarioDurationSeconds = 30; break;
            case "targets": fixture.ViewModel.ScenarioTargetCount = 2; break;
        }
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Contains("请重新分析", fixture.ViewModel.Status);
        Assert.Null(fixture.ViewModel.SelectedResult);
        Assert.True(fixture.ViewModel.IsBuildWorkspace);
    }

    [Fact]
    public async Task AutomaticAnalysisClearsHiddenCalibrationAndFreezesReliableDefaults()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        var viewModel = fixture.ViewModel;
        var preview = viewModel.CurrentPanelDpsDisplay;
        viewModel.UsePanelDpsCalibration = true;
        viewModel.CurrentPanelDpsInput = "invalid";
        viewModel.CurrentWeaponAttackDpsInput = "999999";
        viewModel.PanelStats[0].Input = "123456";
        viewModel.Profile.RuntimeDpsCalibrationFactor = 99;
        viewModel.Profile.SearchBudgetSeconds = 1;
        viewModel.Profile.MonteCarloTrials = 500;
        viewModel.Profile.ResultCount = 1;
        Assert.True(viewModel.OptimizeCommand.CanExecute(null));
        await InvokeAsync(viewModel, "OptimizeAsync");

        var captured = Assert.IsType<OptimizationProfile>(fixture.Optimizer.LastProfile);
        Assert.NotSame(viewModel.Profile, captured);
        Assert.False(captured.UsePanelDpsCalibration);
        Assert.Equal(0, captured.CurrentPanelDps);
        Assert.Equal(0, captured.CurrentWeaponAttackDps);
        Assert.Empty(captured.CurrentPanelStats);
        Assert.Empty(captured.RuntimePanelCorrections);
        Assert.Equal(1, captured.RuntimeDpsCalibrationFactor);
        Assert.Equal(180, captured.SearchBudgetSeconds);
        Assert.Equal(4000, captured.MonteCarloTrials);
        Assert.Equal(10, captured.ResultCount);
        Assert.Equal(preview, viewModel.CurrentPanelDpsDisplay);
        Assert.DoesNotContain(viewModel.PanelStats, x => x.Value.HasValue);
    }

    [Fact]
    public async Task MultipleCharactersRequireSelectionAndRefreshKeepsThatIdentity()
    {
        using var fixture = new Fixture();
        var second = fixture.Character("Second", path: fixture.SecondFile);
        fixture.Reader.Characters = [fixture.Character(), second];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        Assert.Null(fixture.ViewModel.SelectedCharacter);
        Assert.True(fixture.ViewModel.IsSourcesWorkspace);
        fixture.ViewModel.SelectedCharacter = second;
        Assert.True(fixture.ViewModel.IsBuildWorkspace);
        var refreshed = fixture.Character("Second updated", path: fixture.SecondFile);
        fixture.Reader.Characters = [fixture.Character(), refreshed];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        Assert.Same(refreshed, fixture.ViewModel.SelectedCharacter);
        Assert.True(fixture.ViewModel.IsBuildWorkspace);
    }

    [Fact]
    public async Task FailedReloadNeverPairsOldCharacterWithNewCatalog()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        var original = fixture.ViewModel.SelectedCharacter;
        var catalog = fixture.Source.Catalog;
        fixture.Source.Catalog = new GameDataCatalog();
        fixture.Reader.Fail = true;
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");

        Assert.Same(original, fixture.ViewModel.SelectedCharacter);
        Assert.Same(catalog, typeof(MainViewModel).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.ViewModel));
        Assert.Contains("导入失败", fixture.ViewModel.Status);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    [Fact]
    public async Task SourceChangeInvalidatesOldCharacterAndPreview()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        fixture.ViewModel.SelectedInstallation = new GameInstallation(Path.Combine(fixture.Root, "other-game"), "fixture", "", []);
        Assert.Null(fixture.ViewModel.SelectedCharacter);
        Assert.Empty(fixture.ViewModel.Characters);
        Assert.Equal("—", fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.False(fixture.ViewModel.OptimizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveChangedSinceImportReloadsAndDoesNotAnalyzeOldSnapshot()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        var updated = fixture.Character("Updated");
        fixture.Reader.Characters = [updated];
        await File.WriteAllTextAsync(fixture.CharacterFile, "new save state");
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");

        Assert.Equal(0, fixture.Optimizer.Calls);
        Assert.Same(updated, fixture.ViewModel.SelectedCharacter);
        Assert.True(fixture.ViewModel.IsBuildWorkspace);
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Contains("重新载入", fixture.ViewModel.Status);
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        Assert.Equal(1, fixture.Optimizer.Calls);
    }

    [Fact]
    public async Task PreviewIncludesActuallyInstalledAttachments()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        var character = fixture.ViewModel.SelectedCharacter!;
        var weapon = character.Items.Single();
        var component = fixture.Source.Catalog.Attachments.Single();
        var slots = new Dictionary<EquipmentSlot, LoadoutVariant> { [EquipmentSlot.MainHand] = new(weapon, component, null) };
        var calculated = new CharacterStatEngine().Calculate(character, slots, fixture.Source.Catalog);
        var rotation = SkillRotationFactory.Build(character, fixture.Source.Catalog, fixture.ViewModel.SelectedSkill, slots);
        var expected = new CombatSimulator().Simulate(character, calculated, fixture.ViewModel.Profile.Scenario, rotation);
        Assert.Equal(Math.Floor(expected.UnmitigatedDps + .5).ToString("N0"), fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.Equal(calculated.Stats["health"].ToString("N0"), fixture.ViewModel.CurrentHealthDisplay);
    }

    [Fact]
    public async Task SaveChangedDuringAnalysisDiscardsResults()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        fixture.Optimizer.BeforeReturn = () => File.WriteAllText(fixture.CharacterFile, "game saved while analyzing");
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Contains("源存档发生变化", fixture.ViewModel.Status);
        Assert.Contains("舍弃", fixture.ViewModel.Status);
    }

    [Fact]
    public async Task SettingsChangedDuringAnalysisCannotPublishOldResults()
    {
        using var fixture = new Fixture();
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        fixture.Optimizer.BeforeReturn = () => fixture.ViewModel.ScenarioTargetCount = 3;
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        Assert.Equal(1, fixture.Optimizer.LastProfile!.Scenario.TargetCount);
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Contains("设置已改变", fixture.ViewModel.Status);
    }

    [Fact]
    public async Task TemporaryBuffUpdatesPreviewAndClearsResultsImmediately()
    {
        using var fixture = new Fixture();
        const string buff = "records/skills/playerclass01/temp-buff.dbr";
        ((Dictionary<string, GameRecord>)fixture.Source.Catalog.Records)[buff] = new()
        {
            RecordName = buff, RecordType = "Skill_BuffSelfDuration",
            NumericValues = new(new Dictionary<string, double> { ["skillActiveDuration"] = 5, ["characterAttackSpeedModifier"] = 20 })
        };
        fixture.Reader.Characters = [fixture.Character(skills:
            [new(Fixture.Attack, "Attack", 1, 0, true, "", ""), new(buff, "Temporary buff", 1, 0, true, "", "")])];
        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        var inactive = fixture.ViewModel.CurrentPanelDpsDisplay;
        fixture.ViewModel.ItemBuffStates.Single(x => x.Record == buff).IsActive = true;
        Assert.NotEqual(inactive, fixture.ViewModel.CurrentPanelDpsDisplay);
        Assert.Empty(fixture.ViewModel.Results);
        Assert.Contains("临时增益已激活", fixture.ViewModel.CurrentBuildSummary);
    }

    [Fact]
    public async Task SavedCustomGoalIsPreservedUntilUserSelectsPreset()
    {
        using var fixture = new Fixture();
        var custom = new OptimizationProfile();
        custom.Metrics.Single(x => x.Key == "dps").Weight = 7;
        fixture.Profiles.Profile = custom;
        await fixture.ViewModel.InitializeAsync();
        Assert.Equal(3, fixture.ViewModel.SelectedGoalIndex);
        Assert.Equal(7, fixture.ViewModel.Metrics.Single(x => x.Key == "dps").Weight);
        fixture.ViewModel.SelectedGoalIndex = 2;
        Assert.Equal(1, fixture.ViewModel.Metrics.Single(x => x.Key == "dps").Weight);
        Assert.Equal(2, fixture.ViewModel.Metrics.Single(x => x.Key == "effectiveHealth").Weight);
        Assert.Equal(3, fixture.ViewModel.GoalOptions.Count);
    }

    [Fact]
    public async Task HiddenLegacyInputsAreNormalizedOnLoadAndAgainBeforeAnalysis()
    {
        using var fixture = new Fixture();
        var saved = fixture.Profiles.Profile;
        saved.UseUnlockedAttachments = false;
        saved.Scenario.IncomingDamage = new IncomingDamageProfile(HitsPerSecond: 9, PhysicalPerHit: 9000);
        saved.Scenario.EnemyArmorAbsorption = 1;
        saved.Scenario.DurationSeconds = 30;
        saved.Scenario.TargetCount = 3;
        await fixture.ViewModel.InitializeAsync();
        Assert.True(fixture.ViewModel.Profile.UseUnlockedAttachments);
        Assert.Equal(new IncomingDamageProfile(), fixture.ViewModel.Profile.Scenario.IncomingDamage);
        Assert.Equal(70, fixture.ViewModel.Profile.Scenario.EnemyArmorAbsorption);
        Assert.Equal(30, fixture.ViewModel.ScenarioDurationSeconds);
        Assert.Equal(3, fixture.ViewModel.ScenarioTargetCount);

        await InvokeAsync(fixture.ViewModel, "LoadSelectedAsync");
        fixture.ViewModel.Profile.UseUnlockedAttachments = false;
        fixture.ViewModel.Profile.Scenario.IncomingDamage = new IncomingDamageProfile(HitsPerSecond: 0);
        fixture.ViewModel.Profile.Scenario.EnemyArmorAbsorption = 99;
        await InvokeAsync(fixture.ViewModel, "OptimizeAsync");
        var captured = Assert.IsType<OptimizationProfile>(fixture.Optimizer.LastProfile);
        Assert.True(captured.UseUnlockedAttachments);
        Assert.Equal(new IncomingDamageProfile(), captured.Scenario.IncomingDamage);
        Assert.Equal(70, captured.Scenario.EnemyArmorAbsorption);
        Assert.Equal(30, captured.Scenario.DurationSeconds);
        Assert.Equal(3, captured.Scenario.TargetCount);
    }

    [Theory]
    [InlineData("BeamSearch", "正在比较配装…")]
    [InlineData("LocalSearch", "正在细化候选…")]
    [InlineData("MultiStart", "正在细化候选…")]
    [InlineData("MonteCarlo", "正在复核结果…")]
    public void SearchProgressDoesNotExposeInternalSlotsOrThreadDetails(string phase, string expectedStatus)
    {
        var viewModel = new MainViewModel();
        var progress = new SearchProgress(phase, 1234, 2, .5, TimeSpan.FromSeconds(12), "已完成 OffHand · 16 线程");
        typeof(MainViewModel).GetMethod("UpdateSearchProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [progress, 180]);
        Assert.Equal(expectedStatus, viewModel.Status);
        Assert.Contains(1234.ToString("N0"), viewModel.SearchStats);
        Assert.DoesNotContain(phase, viewModel.SearchStats);
        Assert.DoesNotContain("OffHand", viewModel.Status + viewModel.SearchStats);
        Assert.DoesNotContain("线程", viewModel.Status + viewModel.SearchStats);
    }

    private static Task InvokeAsync(MainViewModel viewModel, string method) =>
        (Task)typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null)!;

    private sealed class Fixture : IDisposable
    {
        public const string Attack = "records/skills/playerclass01/attack.dbr";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gd-flow-test-" + Guid.NewGuid().ToString("N"));
        public string CharacterFile => Path.Combine(Root, "first.gdc");
        public string SecondFile => Path.Combine(Root, "second.gdc");
        public TestSource Source { get; }
        public TestReader Reader { get; } = new();
        public TestOptimizer Optimizer { get; } = new();
        public TestProfiles Profiles { get; } = new();
        public MainViewModel ViewModel { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(CharacterFile, "saved state");
            File.WriteAllText(SecondFile, "other state");
            Source = new TestSource(new GameInstallation(Path.Combine(Root, "game"), "fixture", "1", []),
                new SaveLocation(Root, "fixture", [CharacterFile, SecondFile], null, null));
            Reader.Characters = [Character()];
            ViewModel = new(Source, Reader, Optimizer, Profiles, new TestHistory());
        }

        public CharacterSnapshot Character(string name = "First", string primary = Attack, string? path = null,
            IReadOnlyList<CharacterSkill>? skills = null) => new()
        {
            Name = name, SaveFilePath = path ?? CharacterFile, Level = 64, Physique = 100, Cunning = 100, Spirit = 100,
            BaselineStats = new StatBlock(new Dictionary<string, double> { ["health"] = 1000 }),
            PrimarySkillRecord = primary,
            Skills = skills ?? [new(Attack, "Known attack", 1, 0, true, "", "")],
            Items = [new ItemInstance
            {
                Id = "weapon", Name = "Weapon", Slot = EquipmentSlot.MainHand, BaseRecord = "weapon.dbr", ComponentRecord = "component.dbr",
                Stats = new StatBlock(new Dictionary<string, double> { ["weaponDamage.physical"] = 100 }),
                Source = new(ItemSourceKind.Equipped, path ?? CharacterFile)
            }],
            CurrentLoadout = new Dictionary<EquipmentSlot, string> { [EquipmentSlot.MainHand] = "weapon" }
        };

        public void Dispose()
        {
            File.Delete(CharacterFile);
            File.Delete(SecondFile);
            Directory.Delete(Root);
        }
    }

    private sealed class TestSource(GameInstallation installation, SaveLocation save) : IGameDataSource
    {
        public GameDataCatalog Catalog { get; set; } = new()
        {
            Records = new Dictionary<string, GameRecord>
            {
                [Fixture.Attack] = new GameRecord { RecordName = Fixture.Attack, RecordType = "Skill_WeaponPool_BasicAttack", NumericValues = new(new Dictionary<string, double> { ["weaponDamagePct"] = 150 }) }
            },
            Attachments = [new AttachmentDefinition
            {
                Id = "component.dbr", RecordId = "component.dbr", Name = "Installed component", Kind = AttachmentKind.Component, IsOwned = true,
                Stats = new(new Dictionary<string, double> { ["damage.fire.flat"] = 50, ["health"] = 250 })
            }]
        };
        public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiscoveryResult([installation], [save]));
        public Task<GameDataCatalog> LoadCatalogAsync(GameInstallation value, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Catalog);
        public IReadOnlyList<AttachmentDefinition> GetUnlockedAttachments(CharacterSnapshot character, GameDataCatalog catalog) => catalog.Attachments;
    }

    private sealed class TestReader : ISaveReader
    {
        public IReadOnlyList<CharacterSnapshot> Characters { get; set; } = [];
        public bool Fail { get; set; }
        public Task<IReadOnlyList<CharacterSnapshot>> ReadCharactersAsync(SaveLocation location, GameDataCatalog catalog, CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<IReadOnlyList<CharacterSnapshot>>(new IOException("fixture read failed")) : Task.FromResult(Characters);
    }

    private sealed class TestOptimizer : ILoadoutOptimizer
    {
        public int Calls { get; private set; }
        public OptimizationProfile? LastProfile { get; private set; }
        public Action? BeforeReturn { get; set; }
        public Task<OptimizationRun> OptimizeAsync(CharacterSnapshot character, IReadOnlyList<ItemInstance> itemPool,
            IReadOnlyList<AttachmentDefinition> attachments, GameDataCatalog catalog, OptimizationProfile profile,
            IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastProfile = profile;
            BeforeReturn?.Invoke();
            return Task.FromResult(new OptimizationRun
            {
                Results = [new LoadoutResult
                {
                    Rank = 1, Loadout = new CalculatedLoadout { Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(), Stats = new() },
                    Simulation = new(), Metrics = new Dictionary<string, double>(), TierScores = [1], Confidence = ConfidenceLevel.High
                }],
                EvaluatedCandidates = 1, Iterations = 1, Elapsed = TimeSpan.Zero, StopReason = "fixture complete"
            });
        }
    }

    private sealed class TestProfiles : IProfileStore
    {
        public OptimizationProfile Profile { get; set; } = new();
        public Task<IReadOnlyList<OptimizationProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OptimizationProfile>>([Profile]);
        public Task SaveAsync(IEnumerable<OptimizationProfile> profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHistory : ISourceHistoryStore
    {
        public Task<SourceHistorySettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SourceHistorySettings());
        public Task SaveAsync(SourceHistorySettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
