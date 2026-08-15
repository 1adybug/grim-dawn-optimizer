using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IGameDataSource _gameDataSource;
    private readonly ISaveReader _saveReader;
    private readonly ILoadoutOptimizer _optimizer;
    private readonly IProfileStore _profileStore;
    private readonly ISourceHistoryStore _sourceHistoryStore;
    private SourceHistorySettings _sourceHistory = new();
    private bool _restoringSources;
    private readonly SemaphoreSlim _sourceHistorySaveLock = new(1, 1);
    private GameDataCatalog? _catalog;
    private OptimizationProfile _profile = new();
    private GameInstallation? _selectedInstallation;
    private SaveLocation? _selectedSaveLocation;
    private CharacterSnapshot? _selectedCharacter;
    private CharacterSkill? _selectedSkill;
    private ResultViewModel? _selectedResult;
    private string _status = "正在发现游戏与存档…";
    private string _searchStats = "尚未运行";
    private bool _isBusy;
    private double _progressValue;
    private CancellationTokenSource? _cancellation;

    public MainViewModel()
    {
        _gameDataSource = new GameDataSource();
        _saveReader = new SaveReader();
        var statEngine = new CharacterStatEngine();
        var simulator = new CombatSimulator();
        _optimizer = new BeamSearchOptimizer(statEngine, simulator);
        _profileStore = new JsonProfileStore();
        _sourceHistoryStore = new JsonSourceHistoryStore();

        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync, () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => !IsBusy && SelectedInstallation is not null && SelectedSaveLocation is not null);
        OptimizeCommand = new AsyncRelayCommand(OptimizeAsync, () => !IsBusy && SelectedCharacter is not null && SelectedCharacter.Items.Count > 0);
        DemoCommand = new RelayCommand(_ => LoadDemo(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsBusy);
        DeleteGameDirectoryCommand = new RelayCommand(DeleteGameDirectory, x => x is GameInstallation);
        DeleteSaveDirectoryCommand = new RelayCommand(DeleteSaveDirectory, x => x is SaveLocation);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
    }

    public ObservableCollection<GameInstallation> Installations { get; } = [];
    public ObservableCollection<SaveLocation> SaveLocations { get; } = [];
    public ObservableCollection<CharacterSnapshot> Characters { get; } = [];
    public ObservableCollection<CharacterSkill> ActiveSkills { get; } = [];
    public ObservableCollection<MetricRule> Metrics { get; } = [];
    public ObservableCollection<ResultViewModel> Results { get; } = [];

    public AsyncRelayCommand DiscoverCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand OptimizeCommand { get; }
    public RelayCommand DemoCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DeleteGameDirectoryCommand { get; }
    public RelayCommand DeleteSaveDirectoryCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }

    public GameInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (!SetProperty(ref _selectedInstallation, value)) return;
            if (!_restoringSources && value is not null)
            {
                RememberGameDirectory(value.RootPath);
                ScheduleSourceHistorySave();
            }
            RefreshCommands();
        }
    }

    public SaveLocation? SelectedSaveLocation
    {
        get => _selectedSaveLocation;
        set
        {
            if (!SetProperty(ref _selectedSaveLocation, value)) return;
            if (!_restoringSources && value is not null)
            {
                RememberSaveDirectory(value.RootPath);
                ScheduleSourceHistorySave();
            }
            RefreshCommands();
        }
    }

    public CharacterSnapshot? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!SetProperty(ref _selectedCharacter, value)) return;
            ActiveSkills.Clear();
            if (value is not null && _catalog is not null)
            {
                foreach (var skill in value.Skills
                             .Where(x => x.Level > 0 && SkillRotationFactory.IsDirectAttackSkill(x, _catalog))
                             .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    ActiveSkills.Add(skill);

                if (!string.IsNullOrWhiteSpace(value.PrimarySkillRecord) &&
                    ActiveSkills.All(x => !x.Record.Equals(value.PrimarySkillRecord, StringComparison.OrdinalIgnoreCase)) &&
                    _catalog.Records.TryGetValue(value.PrimarySkillRecord, out var primaryRecord))
                {
                    var tag = primaryRecord.Text("skillDisplayName");
                    var name = _catalog.Tags.GetValueOrDefault(tag, Path.GetFileNameWithoutExtension(value.PrimarySkillRecord));
                    var level = ResolveGrantedSkillLevel(value, value.PrimarySkillRecord, _catalog);
                    ActiveSkills.Insert(0, new CharacterSkill(value.PrimarySkillRecord, name, level, 0, true, string.Empty, string.Empty));
                }
            }
            SelectedSkill = ActiveSkills.FirstOrDefault(x =>
                                value is not null && x.Record.Equals(value.PrimarySkillRecord, StringComparison.OrdinalIgnoreCase))
                            ?? ActiveSkills.FirstOrDefault();
            ApplyAverageBossScenario();
            OnPropertyChanged(nameof(CharacterSummary));
            OnPropertyChanged(nameof(BuildFoundationSummary));
            RefreshCommands();
        }
    }

    public CharacterSkill? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (!SetProperty(ref _selectedSkill, value)) return;
            ApplySelectedSkillRotation();
            OnPropertyChanged(nameof(BuildFoundationSummary));
        }
    }

    public ResultViewModel? SelectedResult
    {
        get => _selectedResult;
        set => SetProperty(ref _selectedResult, value);
    }

    public OptimizationProfile Profile
    {
        get => _profile;
        private set
        {
            if (!SetProperty(ref _profile, value)) return;
            Metrics.Clear();
            foreach (var metric in value.Metrics) Metrics.Add(metric);
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SearchStats { get => _searchStats; private set => SetProperty(ref _searchStats, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }
    public bool IsIdle => !IsBusy;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string CharacterSummary => SelectedCharacter is null
        ? "选择角色后读取装备池"
        : $"Lv.{SelectedCharacter.Level} · {(SelectedCharacter.IsHardcore ? "专家" : "普通")} · {SelectedCharacter.Items.Count:N0} 件装备 · {SelectedCharacter.OwnedAttachments.Count:N0} 件附件";
    public string BuildFoundationSummary => SelectedCharacter is null
        ? "尚未读取技能与信仰"
        : $"左键：{(SelectedSkill?.Name ?? "默认武器攻击")} · {(SelectedCharacter.CanDualWield ? "可双持" : "常规副手规则")} · {Profile.Rotation.CalculationSummary}";
    public string EnemyProfileSummary
    {
        get
        {
            var scenario = Profile.Scenario;
            var source = scenario.BossSampleCount > 0 ? $"{scenario.BossSampleCount} 类 BOSS" : "内置估算";
            return $"{source} · 生命 {scenario.EnemyHealth:N0} · 护甲 {scenario.EnemyArmor:N0} · OA/DA {scenario.EnemyOffensiveAbility:N0}/{scenario.EnemyDefensiveAbility:N0}";
        }
    }
    public string EnemyResistanceSummary => string.Join("  ", new[]
    {
        ("物", "physical"), ("穿", "pierce"), ("火", "fire"), ("冰", "cold"), ("电", "lightning"),
        ("酸", "acid"), ("活", "vitality"), ("虚", "aether"), ("混", "chaos")
    }.Select(x => $"{x.Item1}{Profile.Scenario.EnemyResistances.GetValueOrDefault(x.Item2):0.#}%"));
    public string ScoreBenchmarkSummary
    {
        get
        {
            var scenario = Profile.Scenario;
            var requiredDps = BossMetricBenchmarkResolver.Resolve("dps", scenario);
            var incomingWindow = BossMetricBenchmarkResolver.Resolve("effectiveHealth", scenario);
            return $"评分基准 · {scenario.DurationSeconds:0.#} 秒击杀需 {requiredDps:N0} DPS · 同期原始承伤 {incomingWindow:N0}";
        }
    }

    public async Task InitializeAsync()
    {
        var profiles = await _profileStore.LoadAsync();
        Profile = profiles.FirstOrDefault() ?? new OptimizationProfile();
        _sourceHistory = await _sourceHistoryStore.LoadAsync();
        await DiscoverAsync();
    }

    public Task FlushSourceHistoryAsync() => PersistSourceHistoryAsync();

    public async Task UseGameFolderAsync(string path)
    {
        path = JsonSourceHistoryStore.NormalizePath(path);
        RemovePath(_sourceHistory.RemovedGameDirectories, path);
        var installation = FindInstallation(path) ?? GameDataSource.CreateInstallation(path);
        if (!Installations.Contains(installation)) Installations.Insert(0, installation);
        SelectedInstallation = installation;
        Status = installation.DataFiles.Count == 0 ? "目录中没有发现 database.arz" : "已添加手动游戏目录";
        await PersistSourceHistoryAsync();
    }

    public async Task UseSaveFolderAsync(string path)
    {
        path = JsonSourceHistoryStore.NormalizePath(path);
        RemovePath(_sourceHistory.RemovedSaveDirectories, path);
        var save = FindSaveLocation(path) ?? CreateSaveLocation(path, "手动目录");
        if (!SaveLocations.Contains(save)) SaveLocations.Insert(0, save);
        SelectedSaveLocation = save;
        Status = save.CharacterFiles.Count == 0 ? "目录中没有发现 player.gdc" : $"已发现 {save.CharacterFiles.Count} 个角色";
        await PersistSourceHistoryAsync();
    }

    private async Task DiscoverAsync()
    {
        IsBusy = true;
        Status = "正在发现 Steam、GOG 与存档目录…";
        try
        {
            var previousGame = SelectedInstallation?.RootPath;
            var previousSave = SelectedSaveLocation?.RootPath;
            var discovery = await _gameDataSource.DiscoverAsync();
            var removedGames = _sourceHistory.RemovedGameDirectories
                .Select(JsonSourceHistoryStore.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removedSaves = _sourceHistory.RemovedSaveDirectories
                .Select(JsonSourceHistoryStore.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discoveredGames = discovery.Installations
                .GroupBy(x => JsonSourceHistoryStore.NormalizePath(x.RootPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var discoveredSaves = discovery.SaveLocations
                .GroupBy(x => JsonSourceHistoryStore.NormalizePath(x.RootPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            Installations.Clear();
            SaveLocations.Clear();
            foreach (var path in _sourceHistory.GameDirectories
                         .Concat(discovery.Installations.Select(x => x.RootPath))
                         .Select(JsonSourceHistoryStore.NormalizePath)
                         .Where(x => !string.IsNullOrWhiteSpace(x) && !removedGames.Contains(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Installations.Add(discoveredGames.GetValueOrDefault(path) ?? GameDataSource.CreateInstallation(path));
            foreach (var path in _sourceHistory.SaveDirectories
                         .Concat(discovery.SaveLocations.Select(x => x.RootPath))
                         .Select(JsonSourceHistoryStore.NormalizePath)
                         .Where(x => !string.IsNullOrWhiteSpace(x) && !removedSaves.Contains(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                SaveLocations.Add(discoveredSaves.GetValueOrDefault(path) ?? CreateSaveLocation(path, "历史目录"));

            var preferredGame = FirstNonEmpty(_sourceHistory.LastGameDirectory, previousGame);
            var preferredSave = FirstNonEmpty(_sourceHistory.LastSaveDirectory, previousSave);
            _restoringSources = true;
            try
            {
                SelectedInstallation = FindInstallation(preferredGame) ?? Installations.FirstOrDefault();
                SelectedSaveLocation = FindSaveLocation(preferredSave) ?? SaveLocations.FirstOrDefault();
            }
            finally
            {
                _restoringSources = false;
            }
            if (SelectedInstallation is not null) RememberGameDirectory(SelectedInstallation.RootPath);
            if (SelectedSaveLocation is not null) RememberSaveDirectory(SelectedSaveLocation.RootPath);
            await PersistSourceHistoryAsync();
            Status = Installations.Count == 0
                ? "未发现游戏安装；可手动选择目录或加载演示数据"
                : $"发现 {Installations.Count} 个游戏目录、{SaveLocations.Count} 个存档目录";
        }
        catch (Exception ex) { Status = $"路径发现失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void DeleteGameDirectory(object? value)
    {
        if (value is not GameInstallation installation) return;
        var path = JsonSourceHistoryStore.NormalizePath(installation.RootPath);
        var wasSelected = PathEquals(SelectedInstallation?.RootPath, path);
        Installations.Remove(installation);
        RemovePath(_sourceHistory.GameDirectories, path);
        AddUnique(_sourceHistory.RemovedGameDirectories, path);
        if (PathEquals(_sourceHistory.LastGameDirectory, path)) _sourceHistory.LastGameDirectory = string.Empty;
        if (wasSelected)
        {
            _restoringSources = true;
            try { SelectedInstallation = Installations.FirstOrDefault(); }
            finally { _restoringSources = false; }
            if (SelectedInstallation is not null) RememberGameDirectory(SelectedInstallation.RootPath);
        }
        Status = "已从游戏目录列表删除；磁盘上的目录和文件未被修改";
        ScheduleSourceHistorySave();
    }

    private void DeleteSaveDirectory(object? value)
    {
        if (value is not SaveLocation save) return;
        var path = JsonSourceHistoryStore.NormalizePath(save.RootPath);
        var wasSelected = PathEquals(SelectedSaveLocation?.RootPath, path);
        SaveLocations.Remove(save);
        RemovePath(_sourceHistory.SaveDirectories, path);
        AddUnique(_sourceHistory.RemovedSaveDirectories, path);
        if (PathEquals(_sourceHistory.LastSaveDirectory, path)) _sourceHistory.LastSaveDirectory = string.Empty;
        if (wasSelected)
        {
            _restoringSources = true;
            try { SelectedSaveLocation = SaveLocations.FirstOrDefault(); }
            finally { _restoringSources = false; }
            if (SelectedSaveLocation is not null) RememberSaveDirectory(SelectedSaveLocation.RootPath);
        }
        Status = "已从存档目录列表删除；磁盘上的目录和文件未被修改";
        ScheduleSourceHistorySave();
    }

    private GameInstallation? FindInstallation(string? path) =>
        Installations.FirstOrDefault(x => PathEquals(x.RootPath, path));

    private SaveLocation? FindSaveLocation(string? path) =>
        SaveLocations.FirstOrDefault(x => PathEquals(x.RootPath, path));

    private static SaveLocation CreateSaveLocation(string path, string source)
    {
        IReadOnlyList<string> characters = [];
        try
        {
            if (Directory.Exists(path))
                characters = Directory.EnumerateFiles(path, "player.gdc", SearchOption.AllDirectories).ToArray();
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        var transferGst = Path.Combine(path, "transfer.gst");
        var transferGsh = Path.Combine(path, "transfer.gsh");
        var formulas = Path.Combine(path, "formulas.gst");
        return new SaveLocation(
            path, source, characters,
            File.Exists(transferGst) ? transferGst : File.Exists(transferGsh) ? transferGsh : null,
            File.Exists(formulas) ? formulas : null);
    }

    private void RememberGameDirectory(string path)
    {
        path = JsonSourceHistoryStore.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path)) return;
        RemovePath(_sourceHistory.RemovedGameDirectories, path);
        MoveToFront(_sourceHistory.GameDirectories, path);
        _sourceHistory.LastGameDirectory = path;
    }

    private void RememberSaveDirectory(string path)
    {
        path = JsonSourceHistoryStore.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path)) return;
        RemovePath(_sourceHistory.RemovedSaveDirectories, path);
        MoveToFront(_sourceHistory.SaveDirectories, path);
        _sourceHistory.LastSaveDirectory = path;
    }

    private void ScheduleSourceHistorySave() => _ = PersistSourceHistoryAsync();

    private async Task PersistSourceHistoryAsync()
    {
        await _sourceHistorySaveLock.WaitAsync();
        try
        {
            var snapshot = new SourceHistorySettings
            {
                SchemaVersion = _sourceHistory.SchemaVersion,
                GameDirectories = _sourceHistory.GameDirectories.ToList(),
                SaveDirectories = _sourceHistory.SaveDirectories.ToList(),
                RemovedGameDirectories = _sourceHistory.RemovedGameDirectories.ToList(),
                RemovedSaveDirectories = _sourceHistory.RemovedSaveDirectories.ToList(),
                LastGameDirectory = _sourceHistory.LastGameDirectory,
                LastSaveDirectory = _sourceHistory.LastSaveDirectory
            };
            try { await _sourceHistoryStore.SaveAsync(snapshot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally
        {
            _sourceHistorySaveLock.Release();
        }
    }

    private static void MoveToFront(List<string> paths, string path)
    {
        RemovePath(paths, path);
        paths.Insert(0, path);
    }

    private static void AddUnique(List<string> paths, string path)
    {
        if (!paths.Any(x => PathEquals(x, path))) paths.Add(path);
    }

    private static void RemovePath(List<string> paths, string path) =>
        paths.RemoveAll(x => PathEquals(x, path));

    private static bool PathEquals(string? left, string? right) =>
        JsonSourceHistoryStore.NormalizePath(left)
            .Equals(JsonSourceHistoryStore.NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private async Task LoadSelectedAsync()
    {
        if (SelectedInstallation is null || SelectedSaveLocation is null) return;
        IsBusy = true;
        ProgressValue = 0;
        Status = "正在解析游戏数据库；首次运行会建立本地缓存…";
        _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(x => ProgressValue = x * 100);
            _catalog = await _gameDataSource.LoadCatalogAsync(SelectedInstallation, progress, _cancellation.Token);
            Status = "正在只读导入角色与仓库…";
            var characters = await _saveReader.ReadCharactersAsync(SelectedSaveLocation, _catalog, _cancellation.Token);
            Characters.Clear();
            foreach (var character in characters) Characters.Add(character);
            SelectedCharacter = Characters.FirstOrDefault(x => x.Items.Count > 0) ?? Characters.FirstOrDefault();
            Status = SelectedCharacter is null
                ? "没有找到可读取的角色"
                : $"已载入 {Characters.Count} 个角色；{SelectedCharacter.ImportWarnings.Count} 条导入提示";
        }
        catch (OperationCanceledException) { Status = "导入已取消"; }
        catch (Exception ex) { Status = $"导入失败：{ex.Message}"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }

    private void LoadDemo()
    {
        var (character, catalog) = DemoDataFactory.Create();
        _catalog = catalog;
        Characters.Clear();
        Characters.Add(character);
        SelectedCharacter = character;
        Status = "已载入脱敏演示装备池；可直接运行优化";
    }

    private async Task OptimizeAsync()
    {
        if (SelectedCharacter is null || _catalog is null) return;
        IsBusy = true;
        Results.Clear();
        SelectedResult = null;
        ProgressValue = 0;
        Status = "正在生成装备与附件变体…";
        _cancellation = new CancellationTokenSource();
        ApplyAverageBossScenario();
        SyncProfile();
        try
        {
            await SaveProfileAsync();
            var beforeHashes = await ComputeSourceHashesAsync(SelectedCharacter, _cancellation.Token);
            var availableAttachments = _gameDataSource.GetUnlockedAttachments(SelectedCharacter, _catalog);
            var attachments = Profile.UseUnlockedAttachments
                ? availableAttachments
                : availableAttachments.Where(x => x.IsOwned).ToArray();
            var progress = new Progress<SearchProgress>(x =>
            {
                Status = x.Message;
                SearchStats = $"{x.EvaluatedCandidates:N0} 次评估 · {x.Elapsed.TotalSeconds:0.0}s · {x.Phase}";
                ProgressValue = Math.Min(100, x.Elapsed.TotalSeconds / Math.Max(1, Profile.SearchBudgetSeconds) * 100);
            });
            var run = await Task.Run(() => _optimizer.OptimizeAsync(
                SelectedCharacter, SelectedCharacter.Items, attachments, _catalog, Profile, progress, _cancellation.Token),
                _cancellation.Token);
            var afterHashes = await ComputeSourceHashesAsync(SelectedCharacter, _cancellation.Token);
            if (beforeHashes.Any(x => !afterHashes.TryGetValue(x.Key, out var hash) || hash != x.Value))
                throw new IOException("只读保护校验失败：源存档哈希发生变化");

            foreach (var result in run.Results) Results.Add(new ResultViewModel(result, SelectedCharacter, Profile.Metrics));
            SelectedResult = Results.FirstOrDefault();
            SearchStats = $"{run.EvaluatedCandidates:N0} 次评估 · {run.Elapsed.TotalSeconds:0.0}s · {run.Iterations} 轮";
            var calibration = Math.Abs(Profile.RuntimeDpsCalibrationFactor - 1) > 0.0005
                ? $"；面板 DPS 校准 ×{Profile.RuntimeDpsCalibrationFactor:0.000}"
                : string.Empty;
            Status = $"{run.StopReason}{calibration}；存档哈希保持不变";
            ProgressValue = 100;
        }
        catch (OperationCanceledException) { Status = "优化已取消"; }
        catch (Exception ex) { Status = $"优化失败：{ex.Message}"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }

    private void SyncProfile()
    {
        Profile.Metrics = Metrics.ToList();
        Profile.SchemaVersion = 5;
        ApplySelectedSkillRotation();
        Profile.SearchBudgetSeconds = Math.Clamp(Profile.SearchBudgetSeconds, 1, 300);
        Profile.ResultCount = Math.Clamp(Profile.ResultCount, 1, 50);
        Profile.MonteCarloTrials = Math.Clamp(Profile.MonteCarloTrials, 500, 10_000);
        Profile.CurrentPanelDps = Math.Clamp(Profile.CurrentPanelDps, 0, 10_000_000);
    }

    private void ApplySelectedSkillRotation()
    {
        if (SelectedCharacter is null || _catalog is null) return;
        Profile.Rotation = SkillRotationFactory.Build(SelectedCharacter, _catalog, SelectedSkill);
        OnPropertyChanged(nameof(Profile));
    }

    private static int ResolveGrantedSkillLevel(
        CharacterSnapshot character,
        string skillRecord,
        GameDataCatalog catalog)
    {
        var level = 1;
        foreach (var itemId in character.CurrentLoadout.Values)
        {
            var item = character.Items.FirstOrDefault(x => x.Id == itemId);
            if (item is null) continue;
            foreach (var recordName in new[]
                     {
                         item.BaseRecord, item.PrefixRecord, item.SuffixRecord, item.ModifierRecord,
                         item.TransmuteRecord, item.ComponentRecord, item.CompletionBonusRecord, item.AugmentRecord
                     }.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!catalog.Records.TryGetValue(recordName, out var record) ||
                    !record.Text("itemSkillName").Equals(skillRecord, StringComparison.OrdinalIgnoreCase))
                    continue;
                level = Math.Max(level, (int)Math.Round(record.NumericValues["itemSkillLevelEq"]));
            }
        }
        return level;
    }

    private void ApplyAverageBossScenario()
    {
        if (SelectedCharacter is null || _catalog is null) return;
        BossScenarioFactory.ApplyAverageBoss(SelectedCharacter, _catalog, Profile.Scenario);
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(EnemyProfileSummary));
        OnPropertyChanged(nameof(EnemyResistanceSummary));
        OnPropertyChanged(nameof(ScoreBenchmarkSummary));
    }

    private async Task SaveProfileAsync()
    {
        SyncProfile();
        await _profileStore.SaveAsync([Profile]);
        if (!IsBusy) Status = "方案已保存到本地";
    }

    private static async Task<Dictionary<string, string>> ComputeSourceHashesAsync(CharacterSnapshot character, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = character.Items.Select(x => x.Source.FilePath).Append(character.SaveFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists);
        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            result[file] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return result;
    }

    private void RefreshCommands()
    {
        DiscoverCommand.RaiseCanExecuteChanged();
        LoadCommand.RaiseCanExecuteChanged();
        OptimizeCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
