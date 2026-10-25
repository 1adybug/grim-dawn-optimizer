using System.Text.Json;
using System.Windows.Input;
using GrimDawnOptimizer.App.Services;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Bridge;

/// <summary>The allowlisted desktop API. Call only from the owning synchronization context.</summary>
public sealed class BridgeController
{
    private readonly MainViewModel _model;
    private bool _pendingMutation;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _snapshotRevision;
    public bool Ready { get; private set; }
    public string? LastError { get; private set; }
    public bool Dirty { get; set; } = true;
    public bool IsBusy => _model.IsBusy || _pendingMutation || !Ready;

    public BridgeController(MainViewModel model)
    {
        _model = model;
        _model.PropertyChanged += (_, _) => Dirty = true;
        _model.Results.CollectionChanged += (_, _) => Dirty = true;
        _model.Characters.CollectionChanged += (_, _) => Dirty = true;
        _model.Installations.CollectionChanged += (_, _) => Dirty = true;
        _model.SaveLocations.CollectionChanged += (_, _) => Dirty = true;
    }

    public async Task InitializeAsync()
    {
        try { await _model.InitializeAsync(); }
        catch (Exception error) { ReportError(error); }
        finally { Ready = true; Dirty = true; }
    }

    public void ReportError(Exception error) { LastError = error.Message; Dirty = true; }

    public async Task ExecuteAsync(string command, JsonElement payload)
    {
        if (command == "state") return;
        if (command == "cancel") { Execute(_model.CancelCommand); Dirty = true; return; }
        if (!Ready) throw new InvalidOperationException("正在初始化，请稍候。 ");
        if (command != "navigate" && command != "selectResult" && IsBusy)
            throw new InvalidOperationException("当前操作尚未完成，请等待完成或取消后再修改。 ");
        LastError = null;
        switch (command)
        {
            case "discover": Execute(_model.DiscoverCommand); break;
            case "load": Execute(_model.LoadCommand); break;
            case "refresh": Execute(_model.RefreshCharacterCommand); break;
            case "demo": Execute(_model.DemoCommand); break;
            case "optimize": Execute(_model.OptimizeCommand); break;
            case "saveProfile":
                await MutateAsync(async () =>
                {
                    Execute(_model.SaveProfileCommand);
                    while (!_model.SaveProfileCommand.CanExecute(null)) await Task.Delay(10);
                });
                break;
            case "selectCharacter":
                _model.SelectedCharacter = _model.Characters.FirstOrDefault(x => x.SaveFilePath == RequiredString(payload, "id"))
                    ?? throw new ArgumentException("角色已不在当前存档中，请刷新角色列表。");
                break;
            case "selectResult":
                _model.SelectedResult = _model.Results.FirstOrDefault(x => x.Rank == RequiredInt(payload, "rank"))
                    ?? throw new ArgumentException("候选方案已失效，请重新分析。");
                break;
            case "navigate":
                var workspace = RequiredString(payload, "workspace") switch
                {
                    "sources" => WorkspaceSection.Sources,
                    "build" => WorkspaceSection.Build,
                    "results" => WorkspaceSection.Results,
                    _ => throw new ArgumentException("未知的工作区。")
                };
                Execute(_model.NavigateCommand, workspace);
                break;
            case "selectGameDirectory":
                await MutateAsync(() => _model.UseGameFolderAsync(RequiredPath(payload)));
                break;
            case "selectSaveDirectory":
                await MutateAsync(() => _model.UseSaveFolderAsync(RequiredPath(payload)));
                break;
            case "deleteGameDirectory":
                var installation = _model.Installations.FirstOrDefault(x => x.RootPath.Equals(RequiredPath(payload, requireExists: false), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("游戏目录已不在历史记录中。");
                var wasGameSelected = ReferenceEquals(installation, _model.SelectedInstallation);
                Execute(_model.DeleteGameDirectoryCommand, installation);
                // The legacy deletion deliberately suppresses source setters while
                // selecting its fallback. Re-run invalidation at the API boundary.
                if (wasGameSelected)
                {
                    var next = _model.SelectedInstallation;
                    _model.SelectedInstallation = null;
                    _model.SelectedInstallation = next;
                }
                break;
            case "deleteSaveDirectory":
                var save = _model.SaveLocations.FirstOrDefault(x => x.RootPath.Equals(RequiredPath(payload, requireExists: false), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("存档目录已不在历史记录中。");
                var wasSaveSelected = ReferenceEquals(save, _model.SelectedSaveLocation);
                Execute(_model.DeleteSaveDirectoryCommand, save);
                if (wasSaveSelected)
                {
                    var next = _model.SelectedSaveLocation;
                    _model.SelectedSaveLocation = null;
                    _model.SelectedSaveLocation = next;
                }
                break;
            case "configure": Configure(payload); break;
            case "configureBuff":
                var buff = _model.ItemBuffStates.FirstOrDefault(x => x.Record == RequiredString(payload, "record"))
                    ?? throw new ArgumentException("该临时增益不适用于当前角色。");
                buff.IsActive = RequiredBoolean(payload, "isActive");
                break;
            case "compareItem": CompareItem(payload); break;
            default: throw new ArgumentException($"不支持的操作：{command}");
        }
        Dirty = true;
    }

    private async Task MutateAsync(Func<Task> action)
    {
        _pendingMutation = true;
        try { await action(); }
        finally { _pendingMutation = false; }
    }

    private void Configure(JsonElement payload)
    {
        // Validate the complete patch before changing any part of the current build.
        var skill = Has(payload, "skillRecord")
            ? _model.ActiveSkills.FirstOrDefault(x => x.Record == RequiredString(payload, "skillRecord"))
                ?? throw new ArgumentException("主攻技能已不可用，请重新选择。")
            : null;
        var goal = OptionalInt(payload, "goalIndex", 0, _model.GoalOptions.Count - 1);
        var difficulty = OptionalInt(payload, "difficultyIndex", 0, 3);
        var duration = OptionalNumber(payload, "durationSeconds", 1, 300);
        var targets = OptionalInt(payload, "targetCount", 1, 20);
        var enemyDamage = OptionalNumber(payload, "enemyDamageIncreasePercent", 0, 1000);
        ThemePreference? theme = Has(payload, "theme") ? RequiredString(payload, "theme") switch
        {
            "system" => ThemePreference.System, "light" => ThemePreference.Light, "dark" => ThemePreference.Dark,
            _ => throw new ArgumentException("未知的外观设置。")
        } : null;
        var debuffs = new List<(ResistanceDebuffViewModel Row, double Value)>();
        if (Has(payload, "resistanceDebuffs"))
        {
            var reductions = payload.GetProperty("resistanceDebuffs");
            if (reductions.ValueKind != JsonValueKind.Object) throw new ArgumentException("降抗参数必须是对象。");
            foreach (var property in reductions.EnumerateObject())
            {
                var row = _model.ResistanceDebuffs.FirstOrDefault(x => x.Type == property.Name)
                    ?? throw new ArgumentException("未知的抗性类型。");
                debuffs.Add((row, RequiredNumber(property.Value, property.Name, 0, 100)));
            }
        }
        if (skill is not null) _model.SelectedSkill = skill;
        if (goal.HasValue) _model.SelectedGoalIndex = goal.Value;
        if (difficulty.HasValue) _model.SelectedDifficulty = difficulty.Value;
        if (duration.HasValue) _model.ScenarioDurationSeconds = duration.Value;
        if (targets.HasValue) _model.ScenarioTargetCount = targets.Value;
        if (enemyDamage.HasValue) _model.EnemyDamageIncreasePercent = enemyDamage.Value;
        if (theme.HasValue) _model.SelectedThemePreference = theme.Value;
        foreach (var (row, value) in debuffs) row.Reduction = value;
    }

    private void CompareItem(JsonElement payload)
    {
        var result = _model.SelectedResult ?? throw new ArgumentException("请先选择候选方案。");
        var index = RequiredInt(payload, "index");
        if (index < 0 || index >= result.SingleSwaps.Count) throw new ArgumentException("该单件对照不存在。");
        var swap = result.SingleSwaps[index];
        if (Has(payload, "metricKey"))
        {
            var row = swap.Rows.FirstOrDefault(x => x.Key == RequiredString(payload, "metricKey"))
                ?? throw new ArgumentException("该对照指标不存在。");
            row.ActualInput = RequiredString(payload, "actualInput");
        }
        result.SelectedSwap = swap;
    }

    public async Task ShutdownAsync()
    {
        if (_model.CancelCommand.CanExecute(null)) _model.CancelCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_model.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
        await _model.FlushSourceHistoryAsync();
        await _model.FlushUiPreferencesAsync();
    }

    private static void Execute(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter)) throw new InvalidOperationException("当前状态无法执行此操作，请检查角色与输入设置。");
        command.Execute(parameter);
    }

    private static bool Has(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out _);
    private static string RequiredString(JsonElement payload, string key) =>
        Has(payload, key) && payload.GetProperty(key).ValueKind == JsonValueKind.String
            ? payload.GetProperty(key).GetString()! : throw new ArgumentException($"缺少有效的 {key} 参数。");
    private static int RequiredInt(JsonElement payload, string key) =>
        Has(payload, key) && payload.GetProperty(key).TryGetInt32(out var value)
            ? value : throw new ArgumentException($"缺少有效的 {key} 整数参数。");
    private static bool RequiredBoolean(JsonElement payload, string key) =>
        Has(payload, key) && payload.GetProperty(key).ValueKind is JsonValueKind.True or JsonValueKind.False
            ? payload.GetProperty(key).GetBoolean() : throw new ArgumentException($"缺少有效的 {key} 开关参数。");
    private static string RequiredPath(JsonElement payload, bool requireExists = true)
    {
        var path = RequiredString(payload, "path");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请选择完整的本地目录路径。");
        if (requireExists && !Directory.Exists(path)) throw new ArgumentException("所选目录不存在或无法访问。");
        return Path.GetFullPath(path);
    }
    private static int? OptionalInt(JsonElement payload, string key, int minimum, int maximum)
    {
        if (!Has(payload, key)) return null;
        var value = RequiredInt(payload, key);
        if (value < minimum || value > maximum) throw new ArgumentException($"{key} 必须介于 {minimum} 与 {maximum} 之间。");
        return value;
    }
    private static double? OptionalNumber(JsonElement payload, string key, double minimum, double maximum) =>
        Has(payload, key) ? RequiredNumber(payload.GetProperty(key), key, minimum, maximum) : null;
    private static double RequiredNumber(JsonElement element, string key, double minimum, double maximum) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) && value >= minimum && value <= maximum
            ? value : throw new ArgumentException($"{key} 必须介于 {minimum} 与 {maximum} 之间。");

    public object Snapshot() => new
    {
        protocolVersion = 1,
        sessionId = _sessionId,
        revision = ++_snapshotRevision,
        version = _model.AppVersion,
        ready = Ready,
        isBusy = IsBusy,
        lastError = LastError,
        status = _model.Status,
        progress = double.IsFinite(_model.ProgressValue) ? Math.Clamp(_model.ProgressValue, 0, 100) : 0,
        workspace = _model.CurrentWorkspace switch { WorkspaceSection.Sources => "sources", WorkspaceSection.Results => "results", _ => "build" },
        canLoad = Ready && !IsBusy && _model.LoadCommand.CanExecute(null),
        canRefresh = Ready && !IsBusy && _model.RefreshCharacterCommand.CanExecute(null),
        canOptimize = Ready && !IsBusy && _model.OptimizeCommand.CanExecute(null),
        canCancel = _model.CancelCommand.CanExecute(null),
        canSaveProfile = Ready && !IsBusy && _model.SaveProfileCommand.CanExecute(null),
        theme = _model.SelectedThemePreference.ToString().ToLowerInvariant(),
        installations = _model.Installations.Select(x => new { path = x.RootPath, x.Source, x.Version, fileCount = x.DataFiles.Count }).ToArray(),
        saveLocations = _model.SaveLocations.Select(x => new { path = x.RootPath, x.Source, characterCount = x.CharacterFiles.Count }).ToArray(),
        selectedGamePath = _model.SelectedInstallation?.RootPath,
        selectedSavePath = _model.SelectedSaveLocation?.RootPath,
        characters = _model.Characters.Select(x => new { id = x.SaveFilePath, x.Name, x.Level, hardcore = x.IsHardcore, itemCount = x.Items.Count, attachmentCount = x.OwnedAttachments.Count }).ToArray(),
        selectedCharacterId = _model.SelectedCharacter?.SaveFilePath,
        characterSummary = _model.CharacterSummary,
        skills = _model.ActiveSkills.Select(x => new { x.Record, x.Name, x.Level }).ToArray(),
        selectedSkillRecord = _model.SelectedSkill?.Record,
        goals = _model.GoalOptions.ToArray(),
        goalIndex = _model.SelectedGoalIndex,
        difficulties = _model.DifficultyOptions,
        difficultyIndex = _model.SelectedDifficulty,
        panel = new { dps = _model.CurrentPanelDpsDisplay, defaultDps = _model.CurrentDefaultDpsDisplay, health = _model.CurrentHealthDisplay,
            coverage = _model.CurrentPanelCoverage, summary = _model.CurrentBuildSummary, requiredStatus = _model.RequiredStatus,
            hasWarning = _model.HasCurrentPanelWarning, foundationSummary = _model.BuildFoundationSummary },
        scenario = new { durationSeconds = _model.ScenarioDurationSeconds, targetCount = _model.ScenarioTargetCount,
            enemyDamageIncreasePercent = _model.EnemyDamageIncreasePercent, enemySummary = _model.EnemyProfileSummary,
            enemyResistances = _model.EnemyResistanceSummary, scoreSummary = _model.ScoreBenchmarkSummary,
            resistanceDebuffs = _model.ResistanceDebuffs.Select(x => new { x.Type, x.Label, x.Reduction, x.Penalty }).ToArray() },
        buffs = _model.ItemBuffStates.Select(x => new { x.Record, x.Name, x.Description, x.IsActive }).ToArray(),
        equipment = CurrentEquipment(),
        importWarnings = _model.SelectedCharacter?.ImportWarnings ?? [],
        searchStats = _model.SearchStats,
        results = _model.Results.Select(ResultSnapshot).ToArray(),
        selectedResultRank = _model.SelectedResult?.Rank
    };

    private object[] CurrentEquipment()
    {
        if (_model.SelectedCharacter is not { } character) return [];
        return Enum.GetValues<EquipmentSlot>().Where(x => x != EquipmentSlot.Ring).Select(slot =>
        {
            var item = character.Items.FirstOrDefault(x => x.Id == character.CurrentLoadout.GetValueOrDefault(slot));
            return (object)new { slot = ResultViewModel.SlotName(slot), slotKey = slot.ToString(), name = item?.Name ?? "—", source = item?.Source.DisplayName ?? "—" };
        }).ToArray();
    }

    private static object ResultSnapshot(ResultViewModel result) => new
    {
        result.Rank, result.Title, result.ChangeSummary, result.DiagnosticSummary,
        dps = result.Dps, panelDps = result.UnmitigatedDpsFull, equipmentOnlyDps = result.EquipmentOnlyUnmitigatedDpsFull,
        result.RetaliationDps, result.CombatResistances, result.TimeToKill, result.EffectiveHealth,
        result.TimeToDeath, result.Confidence, result.CoverageExplanation, result.Validity,
        result.MonteCarloRange, result.ScoreSummary, result.ScoreBreakdown, result.SlotRows,
        result.WarningRows, result.InformationRows, result.HasWarnings,
        selectedSwapIndex = result.SelectedSwap is null ? (int?)null : result.SingleSwaps.ToList().IndexOf(result.SelectedSwap),
        singleSwaps = result.SingleSwaps.Select((swap, index) => new
        {
            index, swap.Title, swap.Explanation,
            rows = swap.Rows.Select(row => new { row.Key, row.Label, row.Before, row.Predicted,
                delta = double.IsFinite(row.Delta) ? (double?)row.Delta : null, row.Change, row.ActualInput, row.Deviation }).ToArray()
        }).ToArray()
    };
}
