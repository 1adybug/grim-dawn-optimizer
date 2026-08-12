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

        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync, () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => !IsBusy && SelectedInstallation is not null && SelectedSaveLocation is not null);
        OptimizeCommand = new AsyncRelayCommand(OptimizeAsync, () => !IsBusy && SelectedCharacter is not null && SelectedCharacter.Items.Count > 0);
        DemoCommand = new RelayCommand(_ => LoadDemo(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsBusy);
        AddMetricCommand = new RelayCommand(_ => AddMetric());
        RemoveMetricCommand = new RelayCommand(x => RemoveMetric(x as MetricRule), x => x is MetricRule);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
    }

    public ObservableCollection<GameInstallation> Installations { get; } = [];
    public ObservableCollection<SaveLocation> SaveLocations { get; } = [];
    public ObservableCollection<CharacterSnapshot> Characters { get; } = [];
    public ObservableCollection<MetricRule> Metrics { get; } = [];
    public ObservableCollection<ResultViewModel> Results { get; } = [];

    public AsyncRelayCommand DiscoverCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand OptimizeCommand { get; }
    public RelayCommand DemoCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddMetricCommand { get; }
    public RelayCommand RemoveMetricCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }

    public GameInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set { if (SetProperty(ref _selectedInstallation, value)) RefreshCommands(); }
    }

    public SaveLocation? SelectedSaveLocation
    {
        get => _selectedSaveLocation;
        set { if (SetProperty(ref _selectedSaveLocation, value)) RefreshCommands(); }
    }

    public CharacterSnapshot? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!SetProperty(ref _selectedCharacter, value)) return;
            SelectedSkill = value?.Skills.FirstOrDefault(x => x.Enabled);
            OnPropertyChanged(nameof(CharacterSummary));
            RefreshCommands();
        }
    }

    public CharacterSkill? SelectedSkill
    {
        get => _selectedSkill;
        set => SetProperty(ref _selectedSkill, value);
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
        : $"Lv.{SelectedCharacter.Level} · {(SelectedCharacter.IsHardcore ? "专家" : "普通")} · {SelectedCharacter.Items.Count:N0} 件可用装备";

    public async Task InitializeAsync()
    {
        var profiles = await _profileStore.LoadAsync();
        Profile = profiles.FirstOrDefault() ?? new OptimizationProfile();
        await DiscoverAsync();
    }

    public async Task UseGameFolderAsync(string path)
    {
        var installation = GameDataSource.CreateInstallation(path);
        Installations.Add(installation);
        SelectedInstallation = installation;
        Status = installation.DataFiles.Count == 0 ? "目录中没有发现 database.arz" : "已添加手动游戏目录";
        await Task.CompletedTask;
    }

    public async Task UseSaveFolderAsync(string path)
    {
        var chars = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "player.gdc", SearchOption.AllDirectories).ToArray()
            : [];
        var save = new SaveLocation(
            path, "手动目录", chars,
            File.Exists(Path.Combine(path, "transfer.gst")) ? Path.Combine(path, "transfer.gst") : File.Exists(Path.Combine(path, "transfer.gsh")) ? Path.Combine(path, "transfer.gsh") : null,
            File.Exists(Path.Combine(path, "formulas.gst")) ? Path.Combine(path, "formulas.gst") : null);
        SaveLocations.Add(save);
        SelectedSaveLocation = save;
        Status = chars.Length == 0 ? "目录中没有发现 player.gdc" : $"已发现 {chars.Length} 个角色";
        await Task.CompletedTask;
    }

    private async Task DiscoverAsync()
    {
        IsBusy = true;
        Status = "正在发现 Steam、GOG 与存档目录…";
        try
        {
            var discovery = await _gameDataSource.DiscoverAsync();
            Installations.Clear();
            SaveLocations.Clear();
            foreach (var installation in discovery.Installations) Installations.Add(installation);
            foreach (var save in discovery.SaveLocations) SaveLocations.Add(save);
            SelectedInstallation = Installations.FirstOrDefault();
            SelectedSaveLocation = SaveLocations.FirstOrDefault();
            Status = Installations.Count == 0
                ? "未发现游戏安装；可手动选择目录或加载演示数据"
                : $"发现 {Installations.Count} 个游戏目录、{SaveLocations.Count} 个存档目录";
        }
        catch (Exception ex) { Status = $"路径发现失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

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
        SyncProfile();
        try
        {
            await SaveProfileAsync();
            var beforeHashes = await ComputeSourceHashesAsync(SelectedCharacter, _cancellation.Token);
            var attachments = Profile.UseUnlockedAttachments
                ? _gameDataSource.GetUnlockedAttachments(SelectedCharacter, _catalog)
                : _catalog.Attachments.Where(x => SelectedCharacter.Items.Any(item =>
                    string.Equals(item.ComponentRecord, x.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.AugmentRecord, x.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
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

            foreach (var result in run.Results) Results.Add(new ResultViewModel(result, SelectedCharacter));
            SelectedResult = Results.FirstOrDefault();
            SearchStats = $"{run.EvaluatedCandidates:N0} 次评估 · {run.Elapsed.TotalSeconds:0.0}s · {run.Iterations} 轮";
            Status = $"{run.StopReason}；存档哈希保持不变";
            ProgressValue = 100;
        }
        catch (OperationCanceledException) { Status = "优化已取消"; }
        catch (Exception ex) { Status = $"优化失败：{ex.Message}"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }

    private void SyncProfile()
    {
        Profile.Metrics = Metrics.ToList();
        if (SelectedSkill is not null)
        {
            Profile.Rotation.MainSkillRecord = SelectedSkill.Record;
            Profile.Rotation.MainSkillName = SelectedSkill.Name;
        }
        Profile.SearchBudgetSeconds = Math.Clamp(Profile.SearchBudgetSeconds, 1, 60);
        Profile.ResultCount = Math.Clamp(Profile.ResultCount, 1, 50);
        Profile.MonteCarloTrials = Math.Clamp(Profile.MonteCarloTrials, 500, 10_000);
    }

    private async Task SaveProfileAsync()
    {
        SyncProfile();
        await _profileStore.SaveAsync([Profile]);
        if (!IsBusy) Status = "方案已保存到本地";
    }

    private void AddMetric() => Metrics.Add(new MetricRule
    {
        Key = "health", DisplayName = "生命", PriorityTier = Metrics.Count == 0 ? 1 : Metrics.Max(x => x.PriorityTier), Weight = 1
    });

    private void RemoveMetric(MetricRule? rule)
    {
        if (rule is not null) Metrics.Remove(rule);
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
