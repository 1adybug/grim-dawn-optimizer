using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GrimDawnOptimizer.App.Services;
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
    private readonly UiPreferencesStore _uiPreferencesStore;
    private SourceHistorySettings _sourceHistory = new();
    private bool _restoringSources;
    private bool _gameSourceChosenByUser;
    private bool _saveSourceChosenByUser;
    private readonly SemaphoreSlim _sourceHistorySaveLock = new(1, 1);
    private GameDataCatalog? _catalog;
    private OptimizationProfile _profile = new();
    private GameInstallation? _selectedInstallation;
    private SaveLocation? _selectedSaveLocation;
    private CharacterSnapshot? _selectedCharacter;
    private CharacterSkill? _selectedSkill;
    private ResultViewModel? _selectedResult;
    private WorkspaceSection _currentWorkspace = WorkspaceSection.Sources;
    private ThemePreference _selectedThemePreference = ThemePreference.System;
    private bool _restoringUiPreferences;
    private string _status = "正在发现游戏与存档…";
    private string _searchStats = "尚未运行";
    private string _currentWeaponAttackDpsInput = string.Empty;
    private string _currentPanelDpsInput = string.Empty;
    private bool _isBusy;
    private double _progressValue;
    private CancellationTokenSource? _cancellation;
    private string? _loadedGameRoot;
    private string? _loadedSaveRoot;
    private string? _lastCharacterPath;
    private Dictionary<string, string> _importedSourceHashes = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDemo;
    private bool _lastImportSucceeded;
    private bool _updatingCharacter;
    private int _selectedGoalIndex;
    private string _currentPanelDpsDisplay = "—";
    private string _currentDefaultDpsDisplay = "—";
    private string _currentHealthDisplay = "—";
    private string _currentPanelCoverage = "载入角色后自动检查计算覆盖情况。";
    private string _currentBuildSummary = "载入角色后自动计算当前配装。";
    private string _requiredStatus = "请载入并选择角色。";
    private bool _hasCurrentPanelWarning;
    private bool _canAnalyzeCurrentBuild;
    private long _analysisRevision;

    public MainViewModel() : this(new GameDataSource(), new SaveReader(),
        new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator()),
        new JsonProfileStore(), new JsonSourceHistoryStore()) { }

    public MainViewModel(IGameDataSource gameDataSource, ISaveReader saveReader,
        ILoadoutOptimizer optimizer, IProfileStore profileStore, ISourceHistoryStore sourceHistoryStore,
        UiPreferencesStore? uiPreferencesStore = null)
    {
        _gameDataSource = gameDataSource;
        _saveReader = saveReader;
        _optimizer = optimizer;
        _profileStore = profileStore;
        _sourceHistoryStore = sourceHistoryStore;
        _uiPreferencesStore = uiPreferencesStore ?? new UiPreferencesStore();

        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync, () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => !IsBusy && SelectedInstallation is not null && SelectedSaveLocation is not null);
        RefreshCharacterCommand = new AsyncRelayCommand(LoadSelectedAsync, () =>
            !IsBusy && !_isDemo && SelectedCharacter is not null && SelectedInstallation is not null && SelectedSaveLocation is not null);
        OptimizeCommand = new AsyncRelayCommand(OptimizeAsync, () =>
            !IsBusy &&
            SelectedCharacter is not null &&
            SelectedCharacter.Items.Count > 0 &&
            _canAnalyzeCurrentBuild);
        ClearPanelStatsCommand = new RelayCommand(_ => ResetPanelStats(), _ => !IsBusy);
        DemoCommand = new RelayCommand(_ => LoadDemo(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsBusy);
        DeleteGameDirectoryCommand = new RelayCommand(DeleteGameDirectory, x => x is GameInstallation);
        DeleteSaveDirectoryCommand = new RelayCommand(DeleteSaveDirectory, x => x is SaveLocation);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        NavigateCommand = new RelayCommand(Navigate, CanNavigate);
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanOpenResults));
            NavigateCommand.RaiseCanExecuteChanged();
        };
        Characters.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCharacters));
            OnPropertyChanged(nameof(HasNoCharacters));
        };
        foreach (var metric in _profile.Metrics) Metrics.Add(metric);
    }

    public ObservableCollection<GameInstallation> Installations { get; } = [];
    public ObservableCollection<SaveLocation> SaveLocations { get; } = [];
    public ObservableCollection<CharacterSnapshot> Characters { get; } = [];
    public ObservableCollection<CharacterSkill> ActiveSkills { get; } = [];
    public ObservableCollection<ItemBuffStateViewModel> ItemBuffStates { get; } = [];
    public bool HasItemBuffStates => ItemBuffStates.Count > 0;
    private void ItemBuffStateChanged()
    {
        if (SelectedCharacter is null) return;
        SelectedCharacter.ActiveItemSkillRecords = ItemBuffStates.Where(x => x.IsActive)
            .Select(x => x.Record).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Results.Clear();
        SelectedResult = null;
        ResetCurrentPanelDps();
        ResetPanelStats();
        ApplySelectedSkillRotation();
    }
    public ObservableCollection<MetricRule> Metrics { get; } = [];
    public ObservableCollection<ResultViewModel> Results { get; } = [];

    public AsyncRelayCommand DiscoverCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand RefreshCharacterCommand { get; }
    public AsyncRelayCommand OptimizeCommand { get; }
    public RelayCommand DemoCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DeleteGameDirectoryCommand { get; }
    public RelayCommand DeleteSaveDirectoryCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand ClearPanelStatsCommand { get; }

    private IReadOnlyList<PanelStatInputViewModel>? _panelStats;
    public IReadOnlyList<PanelStatInputViewModel> PanelStats => _panelStats ??=
        PanelStatCalibration.Fields.Select(x => new PanelStatInputViewModel(x, PanelStatsChanged)).ToArray();
    public IEnumerable<PanelStatInputViewModel> CorePanelStats => PanelStats.Where(x => !x.Key.EndsWith("Resistance", StringComparison.Ordinal));
    public IEnumerable<PanelStatInputViewModel> ResistancePanelStats => PanelStats.Where(x => x.Key.EndsWith("Resistance", StringComparison.Ordinal));
    public bool IsPanelStatsValid => _panelStats?.All(x => !x.IsInvalid) ?? true;
    public string PanelStatsSummary => IsPanelStatsValid
        ? $"已填写 {Profile.CurrentPanelStats.Count} 项；留空的属性继续使用自动计算。"
        : "有无效的属性输入，请修正或清空后运行。";

    private void PanelStatsChanged()
    {
        Profile.CurrentPanelStats = PanelStats.Where(x => !x.IsInvalid && x.Value.HasValue)
            .ToDictionary(x => x.Key, x => x.Value!.Value, StringComparer.OrdinalIgnoreCase);
        ClearStaleResults();
        OnPropertyChanged(nameof(PanelStatsSummary));
        OnPropertyChanged(nameof(IsPanelStatsValid));
        OptimizeCommand.RaiseCanExecuteChanged();
    }

    private void ResetPanelStats()
    {
        Profile.CurrentPanelStats.Clear();
        Profile.RuntimePanelCorrections = new Dictionary<string, double>();
        if (_panelStats is not null)
            foreach (var row in _panelStats) row.Input = string.Empty;
        OnPropertyChanged(nameof(PanelStatsSummary));
        ClearStaleResults();
        OptimizeCommand.RaiseCanExecuteChanged();
    }

    public IReadOnlyList<ThemePreferenceOption> ThemeOptions { get; } =
    [
        new(ThemePreference.System, "跟随系统"),
        new(ThemePreference.Light, "浅色"),
        new(ThemePreference.Dark, "深色")
    ];

    public WorkspaceSection CurrentWorkspace
    {
        get => _currentWorkspace;
        private set
        {
            if (!SetProperty(ref _currentWorkspace, value)) return;
            OnPropertyChanged(nameof(IsSourcesWorkspace));
            OnPropertyChanged(nameof(IsBuildWorkspace));
            OnPropertyChanged(nameof(IsSearchWorkspace));
            OnPropertyChanged(nameof(IsResultsWorkspace));
            OnPropertyChanged(nameof(WorkspaceTitle));
            OnPropertyChanged(nameof(WorkspaceDescription));
        }
    }

    public bool IsSourcesWorkspace => CurrentWorkspace == WorkspaceSection.Sources;
    public bool IsBuildWorkspace => CurrentWorkspace == WorkspaceSection.Build;
    public bool IsSearchWorkspace => CurrentWorkspace == WorkspaceSection.Search;
    public bool IsResultsWorkspace => CurrentWorkspace == WorkspaceSection.Results;
    public bool CanOpenBuild => SelectedCharacter is not null;
    public bool CanOpenSearch => SelectedCharacter is not null;
    public bool CanOpenResults => Results.Count > 0;
    public bool HasCharacters => Characters.Count > 0;
    public bool HasNoCharacters => Characters.Count == 0;
    public string AppVersion { get; } = FormatVersion(typeof(MainViewModel).Assembly.GetName().Version);
    public string WorkspaceTitle => CurrentWorkspace switch
    {
        WorkspaceSection.Sources => "角色",
        WorkspaceSection.Build or WorkspaceSection.Search => "配装分析",
        WorkspaceSection.Results => "方案对比",
        _ => string.Empty
    };
    public string WorkspaceDescription => CurrentWorkspace switch
    {
        WorkspaceSection.Sources => "连接游戏与存档，选择要分析的角色。",
        WorkspaceSection.Build or WorkspaceSection.Search => "查看当前配装，选择目标后开始分析。",
        WorkspaceSection.Results => "比较候选方案、装备替换与来源位置。",
        _ => string.Empty
    };

    public ThemePreference SelectedThemePreference
    {
        get => _selectedThemePreference;
        set
        {
            if (!SetProperty(ref _selectedThemePreference, value)) return;
            if (!_restoringUiPreferences) _ = PersistUiPreferencesAsync();
        }
    }

    public GameInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (!SetProperty(ref _selectedInstallation, value)) return;
            if (!_restoringSources && value is not null)
            {
                _gameSourceChosenByUser = true;
                RememberGameDirectory(value.RootPath);
                ScheduleSourceHistorySave();
            }
            if (!_restoringSources) InvalidateMismatchedSources();
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
                _saveSourceChosenByUser = true;
                RememberSaveDirectory(value.RootPath);
                ScheduleSourceHistorySave();
            }
            if (!_restoringSources) InvalidateMismatchedSources();
            RefreshCommands();
        }
    }

    public CharacterSnapshot? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!SetProperty(ref _selectedCharacter, value)) return;
            Results.Clear();
            SelectedResult = null;
            ResetCurrentPanelDps();
            ResetPanelStats();
            ActiveSkills.Clear();
            ItemBuffStates.Clear();
            _updatingCharacter = true;
            if (value is not null) _lastCharacterPath = value.SaveFilePath;
            if (value is not null && _catalog is not null)
            {
                ActiveSkills.Add(DefaultAttackSkill());
                value.ActiveItemSkillRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var buff in SkillRotationFactory.ResolveAvailableGrantedSkills(value, _catalog)
                             .Concat(SkillRotationFactory.ResolveTemporaryLearnedSkills(value, _catalog))
                             .Where(x => x.CanAffectPanelStats && !x.IsPassive && !x.IsToggle)
                             .GroupBy(x => x.Record, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                             .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    ItemBuffStates.Add(new ItemBuffStateViewModel(buff.Record, buff.Name,
                        buff.DurationSeconds > 0
                            ? $"持续 {buff.DurationSeconds:0.##} 秒；按激活时的面板计算，候选须保留技能来源并满足武器条件。"
                            : "按激活时的面板计算，候选须保留技能来源并满足武器条件。", ItemBuffStateChanged));
                foreach (var skill in value.Skills
                             .Where(x => x.Level > 0 && SkillRotationFactory.IsDirectAttackSkill(x, _catalog))
                             .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    ActiveSkills.Add(skill);

                foreach (var grant in SkillRotationFactory.ResolveGrantedSkills(value, _catalog)
                             .Where(x => string.IsNullOrWhiteSpace(x.AutoControllerRecord))
                             .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var skill = new CharacterSkill(grant.Record, grant.Name, grant.Level, 0, true, "", "");
                    if (SkillRotationFactory.IsDirectAttackSkill(skill, _catalog) &&
                        ActiveSkills.All(x => !x.Record.Equals(skill.Record, StringComparison.OrdinalIgnoreCase)))
                        ActiveSkills.Add(skill);
                }

                if (!string.IsNullOrWhiteSpace(value.PrimarySkillRecord) &&
                    ActiveSkills.All(x => !x.Record.Equals(value.PrimarySkillRecord, StringComparison.OrdinalIgnoreCase)))
                {
                    _catalog.Records.TryGetValue(value.PrimarySkillRecord, out var primaryRecord);
                    var tag = primaryRecord?.Text("skillDisplayName") ?? "";
                    var name = _catalog.Tags.GetValueOrDefault(tag, Path.GetFileNameWithoutExtension(value.PrimarySkillRecord));
                    var level = ResolveGrantedSkillLevel(value, value.PrimarySkillRecord, _catalog);
                    ActiveSkills.Add(new CharacterSkill(value.PrimarySkillRecord, name, level, 0, true, string.Empty, string.Empty));
                }
            }
            SelectedSkill = ActiveSkills.FirstOrDefault(x =>
                                value is not null && x.Record.Equals(value.PrimarySkillRecord, StringComparison.OrdinalIgnoreCase))
                            ?? ActiveSkills.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Record));
            _updatingCharacter = false;
            ApplyAverageBossScenario();
            ApplySelectedSkillRotation();
            OnPropertyChanged(nameof(HasItemBuffStates));
            OnPropertyChanged(nameof(CharacterSummary));
            OnPropertyChanged(nameof(BuildFoundationSummary));
            OnPropertyChanged(nameof(CanOpenBuild));
            OnPropertyChanged(nameof(CanOpenSearch));
            NavigateCommand.RaiseCanExecuteChanged();
            if (value is null && CurrentWorkspace != WorkspaceSection.Sources)
                CurrentWorkspace = WorkspaceSection.Sources;
            else if (value is not null)
                CurrentWorkspace = WorkspaceSection.Build;
            RefreshCommands();
        }
    }

    public CharacterSkill? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (!SetProperty(ref _selectedSkill, value)) return;
            ResetCurrentPanelDps();
            ClearStaleResults();
            if (_updatingCharacter) return;
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
            NormalizeAutomaticProfileDefaults();
            ResetCurrentPanelDps();
            ResetPanelStats();
            Metrics.Clear();
            foreach (var metric in value.Metrics) Metrics.Add(metric);
            DetectSelectedGoal();
            OnPropertyChanged(nameof(SelectedDifficulty));
            OnPropertyChanged(nameof(EnemyDamageIncreasePercent));
            OnPropertyChanged(nameof(ScenarioDurationSeconds));
            OnPropertyChanged(nameof(ScenarioTargetCount));
            foreach (var row in ResistanceDebuffs) row.Refresh();
        }
    }

    public IReadOnlyList<string> DifficultyOptions { get; } = ["普通（新手）", "老兵", "精英", "终极"];
    public ObservableCollection<string> GoalOptions { get; } = ["均衡", "输出优先", "生存优先"];
    public int SelectedGoalIndex
    {
        get => _selectedGoalIndex;
        set
        {
            if (value < 0 || value >= GoalOptions.Count || !SetProperty(ref _selectedGoalIndex, value)) return;
            if (value < 3)
            {
                var weights = GoalWeights(value);
                Metrics.Clear();
                foreach (var metric in SupportedMetricCatalog.CreateDefaults())
                {
                    metric.Weight = metric.Key == "dps" ? weights.Output : weights.Survival;
                    Metrics.Add(metric);
                }
                Profile.Metrics = Metrics.ToList();
                if (GoalOptions.Count > 3) GoalOptions.RemoveAt(3);
            }
            ClearStaleResults();
        }
    }

    private static (double Output, double Survival) GoalWeights(int index) => index switch
    {
        1 => (2, 1), 2 => (1, 2), _ => (1, 1)
    };

    private void DetectSelectedGoal()
    {
        var output = Metrics.FirstOrDefault(x => x.Key == "dps")?.Weight ?? 0;
        var survival = Metrics.FirstOrDefault(x => x.Key == "effectiveHealth")?.Weight ?? 0;
        _selectedGoalIndex = Enumerable.Range(0, 3).FirstOrDefault(index =>
        {
            var weights = GoalWeights(index);
            return Math.Abs(output - weights.Output) < 1e-9 && Math.Abs(survival - weights.Survival) < 1e-9;
        }, -1);
        if (_selectedGoalIndex < 0)
        {
            if (GoalOptions.Count == 3) GoalOptions.Add("自定义（已保存）");
            _selectedGoalIndex = 3;
        }
        else if (GoalOptions.Count > 3) GoalOptions.RemoveAt(3);
        OnPropertyChanged(nameof(SelectedGoalIndex));
    }

    public string CurrentPanelDpsDisplay { get => _currentPanelDpsDisplay; private set => SetProperty(ref _currentPanelDpsDisplay, value); }
    public string CurrentDefaultDpsDisplay { get => _currentDefaultDpsDisplay; private set => SetProperty(ref _currentDefaultDpsDisplay, value); }
    public string CurrentHealthDisplay { get => _currentHealthDisplay; private set => SetProperty(ref _currentHealthDisplay, value); }
    public string CurrentPanelCoverage { get => _currentPanelCoverage; private set => SetProperty(ref _currentPanelCoverage, value); }
    public string CurrentBuildSummary { get => _currentBuildSummary; private set => SetProperty(ref _currentBuildSummary, value); }
    public string RequiredStatus { get => _requiredStatus; private set => SetProperty(ref _requiredStatus, value); }
    public bool HasCurrentPanelWarning { get => _hasCurrentPanelWarning; private set => SetProperty(ref _hasCurrentPanelWarning, value); }
    public int SelectedDifficulty
    {
        get => (int)Profile.Scenario.Difficulty;
        set
        {
            var selected = (GameDifficulty)Math.Clamp(value, 0, 3);
            if (selected == Profile.Scenario.Difficulty) return;
            Profile.Scenario.Difficulty = selected;
            ResetPanelStats();
            ApplyAverageBossScenario();
            foreach (var row in ResistanceDebuffs) row.Refresh();
            ClearStaleResults();
            RefreshCurrentBuildPreview();
            OnPropertyChanged();
        }
    }
    public double EnemyDamageIncreasePercent
    {
        get => Profile.Scenario.EnemyDamageIncreasePercent;
        set
        {
            if (!double.IsFinite(value)) return;
            Profile.Scenario.EnemyDamageIncreasePercent = Math.Clamp(value, 0, 1000);
            ClearStaleResults();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnemyProfileSummary));
            OnPropertyChanged(nameof(ScoreBenchmarkSummary));
        }
    }
    public double ScenarioDurationSeconds
    {
        get => Profile.Scenario.DurationSeconds;
        set
        {
            if (!double.IsFinite(value)) return;
            var seconds = Math.Clamp(value, 1, 300);
            if (Math.Abs(Profile.Scenario.DurationSeconds - seconds) < 1e-9) return;
            Profile.Scenario.DurationSeconds = seconds;
            ClearStaleResults();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScoreBenchmarkSummary));
        }
    }
    public int ScenarioTargetCount
    {
        get => Profile.Scenario.TargetCount;
        set
        {
            var count = Math.Clamp(value, 1, 20);
            if (Profile.Scenario.TargetCount == count) return;
            Profile.Scenario.TargetCount = count;
            ClearStaleResults();
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<ResistanceDebuffViewModel>? _resistanceDebuffs;
    public IReadOnlyList<ResistanceDebuffViewModel> ResistanceDebuffs => _resistanceDebuffs ??=
        new[] { ("physical", "物理"), ("pierce", "穿刺"), ("fire", "火焰"), ("cold", "冰冷"),
            ("lightning", "闪电"), ("acid", "酸毒"), ("vitality", "活力"), ("aether", "虚化"),
            ("chaos", "混乱"), ("bleeding", "流血") }
        .Select(x => new ResistanceDebuffViewModel(x.Item1, x.Item2,
            () => Profile.Scenario.PlayerResistanceReduction.GetValueOrDefault(x.Item1),
            v => { Profile.Scenario.PlayerResistanceReduction[x.Item1] = v; ClearStaleResults(); },
            () => DifficultyRules.PlayerPenalty(Profile.Scenario, x.Item1))).ToArray();
    private void ClearStaleResults()
    {
        var hadResults = Results.Count > 0;
        _analysisRevision++;
        Results.Clear();
        SelectedResult = null;
        SearchStats = "尚未分析当前设置";
        if (hadResults && !IsBusy) Status = "配装或设置已改变，请重新分析。";
        if (CurrentWorkspace == WorkspaceSection.Results)
            CurrentWorkspace = SelectedCharacter is null ? WorkspaceSection.Sources : WorkspaceSection.Build;
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SearchStats { get => _searchStats; private set => SetProperty(ref _searchStats, value); }
    public string CurrentWeaponAttackDpsInput
    {
        get => _currentWeaponAttackDpsInput;
        set
        {
            if (!SetProperty(ref _currentWeaponAttackDpsInput, value)) return;
            ClearStaleResults();
            Profile.CurrentWeaponAttackDps = TryParseRequiredDps(value, out var dps) ? dps : 0;
            OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsValid));
            OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsInvalid));
            OnPropertyChanged(nameof(CurrentWeaponAttackDpsValidationMessage));
            RefreshCommands();
        }
    }
    public bool UsePanelDpsCalibration
    {
        get => Profile.UsePanelDpsCalibration;
        set
        {
            if (Profile.UsePanelDpsCalibration == value) return;
            Profile.UsePanelDpsCalibration = value;
            ClearStaleResults();
            NotifyDpsCalibrationProperties();
            RefreshCommands();
        }
    }
    public bool UsesSeparateWeaponDpsCalibration =>
        UsePanelDpsCalibration && BeamSearchOptimizer.RequiresWeaponDpsCalibration(Profile.Rotation);
    public string DpsCalibrationInstructions => !UsePanelDpsCalibration
        ? "默认从本机游戏数据、存档装备和所选技能直接计算 DPS。需要手动校准时，再开启下方选项。"
        : UsesSeparateWeaponDpsCalibration
        ? "当前技能包含可可靠拆分的武器伤害：先输入主攻技能 DPS，再输入默认武器攻击 DPS，用两个锚点校准。"
        : "当前技能使用单点校准，只需输入游戏技能栏显示的 DPS；切换角色或主攻技能后需要重新填写。";
    public string CurrentPanelDpsLabel => SelectedSkill is null
        ? "当前默认攻击 DPS"
        : "当前主攻技能 DPS";
    public bool IsCurrentWeaponAttackDpsValid =>
        !UsesSeparateWeaponDpsCalibration || TryParseRequiredDps(CurrentWeaponAttackDpsInput, out _);
    public bool IsCurrentWeaponAttackDpsInvalid =>
        UsesSeparateWeaponDpsCalibration && !TryParseRequiredDps(CurrentWeaponAttackDpsInput, out _);
    public string CurrentWeaponAttackDpsValidationMessage => !UsesSeparateWeaponDpsCalibration ? string.Empty : RequiredDpsValidationMessage(
        CurrentWeaponAttackDpsInput,
        "该技能需要输入游戏面板中的默认武器攻击 DPS。");
    public string CurrentPanelDpsInput
    {
        get => _currentPanelDpsInput;
        set
        {
            if (!SetProperty(ref _currentPanelDpsInput, value)) return;
            ClearStaleResults();
            Profile.CurrentPanelDps = TryParseRequiredDps(value, out var dps) ? dps : 0;
            OnPropertyChanged(nameof(IsCurrentPanelDpsValid));
            OnPropertyChanged(nameof(IsCurrentPanelDpsInvalid));
            OnPropertyChanged(nameof(CurrentPanelDpsValidationMessage));
            RefreshCommands();
        }
    }
    public bool IsCurrentPanelDpsValid => !UsePanelDpsCalibration || TryParseRequiredDps(CurrentPanelDpsInput, out _);
    public bool IsCurrentPanelDpsInvalid => !IsCurrentPanelDpsValid;
    public string CurrentPanelDpsValidationMessage => !UsePanelDpsCalibration ? string.Empty : RequiredDpsValidationMessage(
        CurrentPanelDpsInput,
        "必须输入当前所选主攻技能的游戏面板 DPS。");
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            ClearPanelStatsCommand.RaiseCanExecuteChanged();
            RefreshCommands();
        }
    }
    public bool IsIdle => !IsBusy;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string CharacterSummary => SelectedCharacter is null
        ? "选择角色后读取装备池"
        : $"Lv.{SelectedCharacter.Level} · {(SelectedCharacter.IsHardcore ? "专家角色（永久死亡）" : "非专家角色")} · {SelectedCharacter.Items.Count:N0} 件装备 · {SelectedCharacter.OwnedAttachments.Count:N0} 件附件";
    public string BuildFoundationSummary => SelectedCharacter is null
        ? "尚未读取技能与信仰"
        : $"左键：{(SelectedSkill?.Name ?? "默认武器攻击")} · {(SelectedCharacter.CanDualWield ? "可双持" : "常规副手规则")} · {Profile.Rotation.CalculationSummary}";
    public string EnemyProfileSummary
    {
        get
        {
            var scenario = Profile.Scenario;
            var source = scenario.BossSampleCount > 0 ? $"{scenario.BossSampleCount} 类 BOSS" : "内置估算";
            return $"{DifficultyRules.Name(scenario.Difficulty)} · {source} · 生命 {scenario.EnemyHealth:N0} · 护甲 {scenario.EnemyArmor:N0} · OA/DA {scenario.EnemyOffensiveAbility:N0}/{scenario.EnemyDefensiveAbility:N0} · 来袭 {DifficultyRules.Incoming(scenario).HitsPerSecond:0.##}/s · 吸血抗 {scenario.EnemyLifeLeechResistance:0.#}%";
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
        _restoringUiPreferences = true;
        try
        {
            var preferences = await _uiPreferencesStore.LoadAsync();
            SelectedThemePreference = preferences.Theme;
        }
        finally
        {
            _restoringUiPreferences = false;
        }
        var profiles = await _profileStore.LoadAsync();
        Profile = profiles.FirstOrDefault() ?? new OptimizationProfile();
        _sourceHistory = await _sourceHistoryStore.LoadAsync();
        await DiscoverAsync();
    }

    public Task FlushSourceHistoryAsync() => PersistSourceHistoryAsync();

    public Task FlushUiPreferencesAsync() => PersistUiPreferencesAsync();

    private void Navigate(object? value)
    {
        if (value is WorkspaceSection section && CanNavigate(section))
            CurrentWorkspace = section == WorkspaceSection.Search ? WorkspaceSection.Build : section;
    }

    private bool CanNavigate(object? value) => value switch
    {
        WorkspaceSection.Sources => true,
        WorkspaceSection.Build => CanOpenBuild,
        WorkspaceSection.Search => CanOpenSearch,
        WorkspaceSection.Results => CanOpenResults,
        _ => false
    };

    public async Task UseGameFolderAsync(string path)
    {
        _gameSourceChosenByUser = true;
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
        _saveSourceChosenByUser = true;
        path = JsonSourceHistoryStore.NormalizePath(path);
        RemovePath(_sourceHistory.RemovedSaveDirectories, path);
        var save = FindSaveLocation(path) ?? CreateSaveLocation(path, "手动目录");
        if (!SaveLocations.Contains(save)) SaveLocations.Insert(0, save);
        SelectedSaveLocation = save;
        Status = save.CharacterFiles.Count == 0 ? "目录中没有发现 player.gdc" : $"已发现 {save.CharacterFiles.Count} 个角色";
        await PersistSourceHistoryAsync();
    }

    private async Task<bool> DiscoverAsync()
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

            _restoringSources = true;
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

            var preferredGame = GameSourceSelection.ChooseRoot(
                Installations.Select(x => x.RootPath), _gameSourceChosenByUser ? previousGame : null,
                discovery.RunningGameRoot, _sourceHistory.LastGameDirectory);
            var preferredSave = GameSourceSelection.ChooseRoot(
                SaveLocations.Select(x => x.RootPath), _saveSourceChosenByUser ? previousSave : null,
                discovery.PreferredSaveRoot, _sourceHistory.LastSaveDirectory);
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
            InvalidateMismatchedSources();
            if (SelectedInstallation is not null) RememberGameDirectory(SelectedInstallation.RootPath);
            if (SelectedSaveLocation is not null) RememberSaveDirectory(SelectedSaveLocation.RootPath);
            await PersistSourceHistoryAsync();
            Status = Installations.Count == 0
                ? "未发现游戏安装；可手动选择目录或加载演示数据"
                : $"发现 {Installations.Count} 个游戏目录、{SaveLocations.Count} 个存档目录";
            return true;
        }
        catch (Exception ex) { Status = $"路径发现失败：{ex.Message}"; return false; }
        finally { _restoringSources = false; IsBusy = false; }
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
            _gameSourceChosenByUser = false;
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
            _saveSourceChosenByUser = false;
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
        _sourceHistory.GameDirectories.Clear();
        _sourceHistory.GameDirectories.Add(path);
        _sourceHistory.LastGameDirectory = path;
    }

    private void RememberSaveDirectory(string path)
    {
        path = JsonSourceHistoryStore.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path)) return;
        RemovePath(_sourceHistory.RemovedSaveDirectories, path);
        _sourceHistory.SaveDirectories.Clear();
        _sourceHistory.SaveDirectories.Add(path);
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
                RemovedGameDirectories = [],
                RemovedSaveDirectories = [],
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

    private async Task PersistUiPreferencesAsync()
    {
        try
        {
            await _uiPreferencesStore.SaveAsync(new UiPreferences { Theme = SelectedThemePreference });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private void InvalidateMismatchedSources()
    {
        if (_catalog is null || !_isDemo &&
            PathEquals(_loadedGameRoot, SelectedInstallation?.RootPath) &&
            PathEquals(_loadedSaveRoot, SelectedSaveLocation?.RootPath)) return;
        _catalog = null;
        _isDemo = false;
        _importedSourceHashes.Clear();
        Characters.Clear();
        SelectedCharacter = null;
        ClearStaleResults();
        RequiredStatus = "数据源已改变，请载入角色。";
        CurrentWorkspace = WorkspaceSection.Sources;
    }

    private async Task LoadSelectedAsync()
    {
        _lastImportSucceeded = false;
        var previousPath = SelectedCharacter?.SaveFilePath ?? _lastCharacterPath;
        var previousSkill = SelectedSkill;
        // The game may have been restarted from a different installation, or a new
        // character may have been saved since this app opened. Refresh before import.
        if (!await DiscoverAsync()) return;
        if (SelectedInstallation is null || SelectedSaveLocation is null) return;
        IsBusy = true;
        ProgressValue = 0;
        Status = "正在解析游戏数据库；首次运行会建立本地缓存…";
        _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(x => ProgressValue = x * 100);
            var installation = SelectedInstallation;
            var saveLocation = SelectedSaveLocation;
            var token = _cancellation.Token;
            // Cache JSON, ARZ records, and animation resources all perform CPU work
            // after I/O awaits. Keep that continuation off the UI dispatcher.
            var catalog = await Task.Run(
                () => _gameDataSource.LoadCatalogAsync(installation, progress, token), token);
            Status = "正在只读导入角色与仓库…";
            var beforeHashes = await ComputeLocationHashesAsync(saveLocation, token);
            var characters = await _saveReader.ReadCharactersAsync(saveLocation, catalog, token);
            var afterHashes = await ComputeLocationHashesAsync(saveLocation, token);
            if (!HashesMatch(beforeHashes, afterHashes))
                throw new IOException("导入期间游戏存档发生变化，请在游戏保存完成后刷新。");
            if (!PathEquals(installation.RootPath, SelectedInstallation?.RootPath) ||
                !PathEquals(saveLocation.RootPath, SelectedSaveLocation?.RootPath))
                throw new IOException("数据源已改变，请载入当前选择的目录。");

            // Commit the database and its matching snapshots together. Failed or
            // cancelled imports cannot pair an old character with a new database.
            var sameSource = PathEquals(_loadedSaveRoot, saveLocation.RootPath);
            _catalog = catalog;
            _loadedGameRoot = installation.RootPath;
            _loadedSaveRoot = saveLocation.RootPath;
            _isDemo = false;
            _importedSourceHashes = afterHashes;
            _lastImportSucceeded = true;
            Characters.Clear();
            foreach (var character in characters) Characters.Add(character);
            var retainedCharacter = Characters.FirstOrDefault(x => PathEquals(x.SaveFilePath, previousPath));
            SelectedCharacter = retainedCharacter ??
                (Characters.Count == 1 && (!sameSource || string.IsNullOrWhiteSpace(previousPath)) ? Characters[0] : null);
            if (retainedCharacter is not null && previousSkill is not null)
            {
                var retainedSkill = ActiveSkills.FirstOrDefault(x => x.Record.Equals(previousSkill.Record, StringComparison.OrdinalIgnoreCase));
                if (retainedSkill is null)
                {
                    // Keep an unavailable former selection visible in the picker;
                    // selecting an item outside ItemsSource can otherwise clear it.
                    ActiveSkills.Add(previousSkill);
                    retainedSkill = previousSkill;
                }
                SelectedSkill = retainedSkill;
            }
            if (SelectedCharacter is null) RefreshCurrentBuildPreview();
            Status = SelectedCharacter is null
                ? Characters.Count == 0 ? "没有找到可读取的角色" : "已载入角色列表，请选择要分析的角色。"
                : $"已载入 {Characters.Count} 个角色；{SelectedCharacter.ImportWarnings.Count} 条导入提示";
            if (SelectedCharacter is not null) CurrentWorkspace = WorkspaceSection.Build;
        }
        catch (OperationCanceledException) { Status = "导入已取消"; }
        catch (Exception ex) { Status = $"导入失败：{ex.Message}"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }

    private void LoadDemo()
    {
        var (character, catalog) = DemoDataFactory.Create();
        _catalog = catalog;
        _isDemo = true;
        _importedSourceHashes.Clear();
        Characters.Clear();
        Characters.Add(character);
        SelectedCharacter = character;
        Status = "已载入演示装备池，可直接开始配装分析。";
        CurrentWorkspace = WorkspaceSection.Build;
    }

    private async Task OptimizeAsync()
    {
        if (SelectedCharacter is null || _catalog is null) return;
        ResetCurrentPanelDps();
        ResetPanelStats();
        ApplySelectedSkillRotation();
        if (!_canAnalyzeCurrentBuild) { Status = RequiredStatus; return; }
        IsBusy = true;
        ClearStaleResults();
        ProgressValue = 0;
        Status = "正在生成装备与附件变体…";
        _cancellation = new CancellationTokenSource();
        ApplyAverageBossScenario();
        SyncProfile();
        try
        {
            var character = SelectedCharacter;
            var catalog = _catalog;
            var saveLocation = _isDemo ? null : SelectedSaveLocation;
            var beforeHashes = saveLocation is null
                ? await ComputeSourceHashesAsync(character, _cancellation.Token)
                : await ComputeLocationHashesAsync(saveLocation, _cancellation.Token);
            if (!_isDemo && !HashesMatch(_importedSourceHashes, beforeHashes))
            {
                // A save made after import is a new snapshot, not a safe baseline
                // for this run. Reload it and let the user see the changed build.
                _cancellation.Dispose();
                _cancellation = null;
                IsBusy = false;
                await LoadSelectedAsync();
                if (_lastImportSucceeded && SelectedCharacter is not null && !HashesMatch(_importedSourceHashes, beforeHashes))
                    Status = "存档已发生变化，请确认刷新后的角色状态再开始分析。";
                else if (_lastImportSucceeded && SelectedCharacter is not null)
                    Status = "存档已更新并重新载入，请确认当前配装后再次开始分析。";
                return;
            }
            await SaveProfileAsync();
            var runProfile = JsonSerializer.Deserialize<OptimizationProfile>(JsonSerializer.Serialize(Profile))!;
            var revision = _analysisRevision;
            var availableAttachments = _gameDataSource.GetUnlockedAttachments(character, catalog);
            var attachments = runProfile.UseUnlockedAttachments
                ? availableAttachments
                : availableAttachments.Where(x => x.IsOwned).ToArray();
            var progress = new Progress<SearchProgress>(x =>
            {
                if (!IsBusy || _analysisRevision != revision) return;
                UpdateSearchProgress(x, runProfile.SearchBudgetSeconds);
            });
            var run = await Task.Run(() => _optimizer.OptimizeAsync(
                character, character.Items, attachments, catalog, runProfile, progress, _cancellation.Token),
                _cancellation.Token);
            var afterHashes = saveLocation is null
                ? await ComputeSourceHashesAsync(character, _cancellation.Token)
                : await ComputeLocationHashesAsync(saveLocation, _cancellation.Token);
            if (!HashesMatch(beforeHashes, afterHashes))
                throw new IOException("分析期间源存档发生变化，结果已舍弃；请刷新角色后重试。");
            if (_analysisRevision != revision || !ReferenceEquals(character, SelectedCharacter) || !ReferenceEquals(catalog, _catalog))
            {
                Status = "分析设置已改变，旧结果已舍弃，请重新开始分析。";
                return;
            }

            foreach (var result in run.Results) Results.Add(new ResultViewModel(result, character, runProfile.Metrics));
            SelectedResult = Results.FirstOrDefault();
            SearchStats = $"{run.EvaluatedCandidates:N0} 次评估 · {run.Elapsed.TotalSeconds:0.0}s · {run.Iterations} 轮";
            Status = Results.Count > 0
                ? $"分析完成，得到 {Results.Count} 套候选；分析期间存档未变化"
                : "未找到可用候选，请检查主攻要求与装备来源。";
            ProgressValue = 100;
            if (Results.Count > 0) CurrentWorkspace = WorkspaceSection.Results;
        }
        catch (OperationCanceledException) { Status = "优化已取消"; }
        catch (Exception ex) { Status = $"优化失败：{ex.Message}"; }
        finally { IsBusy = false; _cancellation?.Dispose(); _cancellation = null; }
    }

    private void SyncProfile()
    {
        NormalizeAutomaticProfileDefaults();
        Profile.Metrics = Metrics.ToList();
        Profile.SchemaVersion = 7;
        ApplySelectedSkillRotation();
        Profile.SearchBudgetSeconds = 180;
        Profile.ResultCount = 10;
        Profile.MonteCarloTrials = 4000;
        Profile.CurrentWeaponAttackDps = UsesSeparateWeaponDpsCalibration &&
                                         TryParseRequiredDps(CurrentWeaponAttackDpsInput, out var weaponAttackDps)
            ? weaponAttackDps
            : 0;
        Profile.CurrentPanelDps = UsePanelDpsCalibration && TryParseRequiredDps(CurrentPanelDpsInput, out var panelDps) ? panelDps : 0;
    }

    private void NormalizeAutomaticProfileDefaults()
    {
        // These inputs are no longer user choices in the automatic workflow.
        // A saved legacy value must not silently change its advertised scope.
        Profile.UseUnlockedAttachments = true;
        Profile.Scenario.IncomingDamage = new IncomingDamageProfile();
        Profile.Scenario.EnemyArmorAbsorption = 70;
        OnPropertyChanged(nameof(EnemyProfileSummary));
        OnPropertyChanged(nameof(ScoreBenchmarkSummary));
    }

    private void UpdateSearchProgress(SearchProgress progress, int searchBudgetSeconds)
    {
        Status = progress.Phase switch
        {
            "LocalSearch" or "MultiStart" => "正在细化候选…",
            "MonteCarlo" => "正在复核结果…",
            _ => "正在比较配装…"
        };
        SearchStats = $"已评估 {progress.EvaluatedCandidates:N0} 个方案 · 已用 {progress.Elapsed.TotalSeconds:0.0} 秒";
        ProgressValue = Math.Min(100, progress.Elapsed.TotalSeconds / Math.Max(1, searchBudgetSeconds) * 100);
    }

    private void ApplySelectedSkillRotation()
    {
        if (SelectedCharacter is not null && _catalog is not null)
            Profile.Rotation = SkillRotationFactory.BuildSelectedRecord(SelectedCharacter, _catalog,
                SelectedSkill?.Record ?? "", SelectedSkill?.Name ?? "");
        OnPropertyChanged(nameof(Profile));
        NotifyDpsCalibrationProperties();
        RefreshCurrentBuildPreview();
        RefreshCommands();
    }

    private static CharacterSkill DefaultAttackSkill() =>
        new("", "默认武器攻击", 1, 0, true, "", "");

    private void RefreshCurrentBuildPreview()
    {
        _canAnalyzeCurrentBuild = false;
        CurrentPanelDpsDisplay = CurrentDefaultDpsDisplay = CurrentHealthDisplay = "—";
        HasCurrentPanelWarning = false;
        if (SelectedCharacter is null || _catalog is null)
        {
            RequiredStatus = HasCharacters ? "请选择要分析的角色。" : "请载入并选择角色。";
            CurrentBuildSummary = "载入角色后自动计算当前配装。";
            CurrentPanelCoverage = "载入角色后自动检查计算覆盖情况。";
            RefreshCommands();
            return;
        }
        try
        {
            var character = SelectedCharacter;
            var attachments = _gameDataSource.GetUnlockedAttachments(character, _catalog);
            var slots = new Dictionary<EquipmentSlot, LoadoutVariant>();
            var missing = new List<string>();
            foreach (var (slot, itemId) in character.CurrentLoadout)
            {
                var item = character.Items.FirstOrDefault(x => x.Id == itemId);
                if (item is null)
                {
                    missing.Add($"当前 {slot} 装备来源缺失，数值不完整。");
                    continue;
                }
                AttachmentDefinition? FindInstalled(string record, string completion, AttachmentKind kind)
                {
                    if (string.IsNullOrWhiteSpace(record)) return null;
                    var attachment = attachments.FirstOrDefault(x => x.Kind == kind &&
                        (string.IsNullOrWhiteSpace(x.RecordId) ? x.Id : x.RecordId).Equals(record, StringComparison.OrdinalIgnoreCase) &&
                        x.CompletionBonusRecord.Equals(completion, StringComparison.OrdinalIgnoreCase));
                    if (attachment is null) missing.Add($"{item.Name} 已安装附件无法还原：{record}");
                    return attachment;
                }
                slots[slot] = new(item,
                    FindInstalled(item.ComponentRecord, item.CompletionBonusRecord, AttachmentKind.Component),
                    FindInstalled(item.AugmentRecord, "", AttachmentKind.Augment));
            }

            // Preview uses the same engine and installed attachments as optimization,
            // without persisted/manual DPS anchors or attribute corrections.
            var loadout = new CharacterStatEngine().Calculate(character, slots, _catalog);
            var rotation = SkillRotationFactory.BuildSelectedRecord(character, _catalog,
                SelectedSkill?.Record ?? "", SelectedSkill?.Name ?? "", slots);
            var defaultRotation = SkillRotationFactory.Build(character, _catalog, null, slots);
            Profile.Rotation = rotation;
            var simulator = new CombatSimulator();
            CurrentDefaultDpsDisplay = PanelDpsDisplay.Format(
                simulator.Simulate(character, loadout, Profile.Scenario, defaultRotation).UnmitigatedDps);
            CurrentHealthDisplay = loadout.Stats["health"].ToString("N0");
            CurrentPanelDpsDisplay = rotation.IsAvailable
                ? PanelDpsDisplay.Format(simulator.Simulate(character, loadout, Profile.Scenario, rotation).UnmitigatedDps)
                : "不可用";
            _canAnalyzeCurrentBuild = rotation.IsAvailable && loadout.IsValid && character.Items.Count > 0;
            RequiredStatus = !rotation.IsAvailable ? rotation.UnavailableReason
                : !loadout.IsValid ? "当前装备状态存在问题，请查看计算说明。"
                : character.Items.Count == 0 ? "角色没有可用于分析的装备。"
                : "已按存档与游戏数据计算，可直接开始分析。";
            CurrentBuildSummary = $"{character.Name} · Lv.{character.Level} · {SelectedSkill?.Name ?? "默认武器攻击"}" +
                (character.ActiveItemSkillRecords.Count > 0 ? $" · {character.ActiveItemSkillRecords.Count} 项临时增益已激活" : " · 常驻状态") +
                (_isDemo ? " · 演示数据" : "");
            var diagnostics = loadout.UnsupportedEffects.Concat(CalculationDiagnostics.RotationDiagnostics(loadout, rotation)).Distinct().ToArray();
            var warnings = diagnostics.Where(x => x.Severity == DiagnosticSeverity.Warning)
                .Select(x => $"[{CalculationDiagnostics.ScopeLabel(x)}] {x.Description}")
                .Concat(loadout.ValidationErrors).Concat(missing)
                .Concat(character.ImportWarnings.Where(x => x.Contains("失败", StringComparison.Ordinal) ||
                    x.Contains("缺失", StringComparison.Ordinal) || x.Contains("无法", StringComparison.Ordinal)))
                .Distinct().ToArray();
            var coverage = CalculationDiagnostics.PanelConfidence(loadout, rotation) switch
            {
                ConfidenceLevel.High when missing.Count == 0 => "面板未发现已知缺项。",
                ConfidenceLevel.Medium when missing.Count == 0 => "面板使用部分回退估算。",
                _ => "面板存在计算缺口。"
            };
            HasCurrentPanelWarning = warnings.Length > 0 || CalculationDiagnostics.PanelConfidence(loadout, rotation) != ConfidenceLevel.High;
            CurrentPanelCoverage = string.Join(Environment.NewLine,
                new[] { coverage, "覆盖检查不等于所有配装的实测精度保证。" }.Concat(warnings));
        }
        catch (Exception ex)
        {
            CurrentPanelDpsDisplay = CurrentDefaultDpsDisplay = CurrentHealthDisplay = "—";
            RequiredStatus = "当前配装无法计算，请刷新角色或检查数据源。";
            CurrentBuildSummary = SelectedCharacter.Name;
            CurrentPanelCoverage = $"预览失败：{ex.Message}";
            HasCurrentPanelWarning = true;
        }
        RefreshCommands();
    }

    private void ResetCurrentPanelDps()
    {
        Profile.UsePanelDpsCalibration = false;
        Profile.CurrentWeaponAttackDps = 0;
        Profile.CurrentPanelDps = 0;
        Profile.RuntimeDpsCalibrationFactor = 1;
        Profile.RuntimeWeaponDpsCalibrationFactor = 1;
        Profile.RuntimeSkillAddedDpsCalibrationFactor = 1;
        Profile.RuntimeDpsCalibrationMode = DpsCalibrationMode.None;
        _currentWeaponAttackDpsInput = string.Empty;
        _currentPanelDpsInput = string.Empty;
        OnPropertyChanged(nameof(CurrentWeaponAttackDpsInput));
        OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsValid));
        OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsInvalid));
        OnPropertyChanged(nameof(CurrentWeaponAttackDpsValidationMessage));
        OnPropertyChanged(nameof(CurrentPanelDpsInput));
        OnPropertyChanged(nameof(IsCurrentPanelDpsValid));
        OnPropertyChanged(nameof(IsCurrentPanelDpsInvalid));
        OnPropertyChanged(nameof(CurrentPanelDpsValidationMessage));
        NotifyDpsCalibrationProperties();
        OptimizeCommand.RaiseCanExecuteChanged();
    }

    private void NotifyDpsCalibrationProperties()
    {
        OnPropertyChanged(nameof(UsePanelDpsCalibration));
        OnPropertyChanged(nameof(UsesSeparateWeaponDpsCalibration));
        OnPropertyChanged(nameof(DpsCalibrationInstructions));
        OnPropertyChanged(nameof(CurrentPanelDpsLabel));
        OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsValid));
        OnPropertyChanged(nameof(IsCurrentWeaponAttackDpsInvalid));
        OnPropertyChanged(nameof(CurrentWeaponAttackDpsValidationMessage));
        OnPropertyChanged(nameof(IsCurrentPanelDpsValid));
        OnPropertyChanged(nameof(IsCurrentPanelDpsInvalid));
        OnPropertyChanged(nameof(CurrentPanelDpsValidationMessage));
    }

    private static bool TryParseRequiredDps(string? input, out double dps) =>
        double.TryParse(
            input,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture,
            out dps) &&
        double.IsFinite(dps) &&
        dps is > 0 and <= 10_000_000;

    private static string RequiredDpsValidationMessage(string? input, string missingMessage) =>
        string.IsNullOrWhiteSpace(input)
            ? missingMessage
            : "请输入大于 0 且不超过 10,000,000 的有效数值。";

    private static string FormatVersion(Version? version) => version is null
        ? "版本未知"
        : $"版本 {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

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

    private static Task<Dictionary<string, string>> ComputeSourceHashesAsync(CharacterSnapshot character, CancellationToken cancellationToken) =>
        ComputeFileHashesAsync(character.Items.Select(x => x.Source.FilePath).Append(character.SaveFilePath), cancellationToken);

    private static Task<Dictionary<string, string>> ComputeLocationHashesAsync(SaveLocation location, CancellationToken cancellationToken) =>
        ComputeFileHashesAsync(location.CharacterFiles.Concat(new[]
        {
            location.SharedStashFile, location.FormulaFile,
            Path.Combine(location.RootPath, "transfer.gst"), Path.Combine(location.RootPath, "transfer.gsh"),
            Path.Combine(location.RootPath, "formulas.gst")
        }.OfType<string>()), cancellationToken);

    private static bool HashesMatch(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var hash) && hash == x.Value);

    private static async Task<Dictionary<string, string>> ComputeFileHashesAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) { result[file] = "<missing>"; continue; }
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            result[file] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return result;
    }

    private void RefreshCommands()
    {
        DiscoverCommand.RaiseCanExecuteChanged();
        LoadCommand.RaiseCanExecuteChanged();
        RefreshCharacterCommand.RaiseCanExecuteChanged();
        OptimizeCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        NavigateCommand.RaiseCanExecuteChanged();
    }
}
