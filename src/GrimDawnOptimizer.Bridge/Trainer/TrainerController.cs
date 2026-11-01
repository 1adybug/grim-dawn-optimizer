using System.Text.Json;

namespace GrimDawnOptimizer.Bridge.Trainer;

/// <summary>Owns trainer state on the sidecar's synchronization context, entirely
/// separate from optimizer state and its mutation gate.</summary>
public sealed class TrainerController : IDisposable
{
    private readonly TrainerSettings _settings;
    private readonly Func<ITrainerSession> _sessionFactory;
    private readonly ITrainerHotkeys _hotkeys;
    private readonly Action<string> _launchGame;
    private readonly Action<Action> _dispatch;
    private readonly Func<ulong> _clock;
    private readonly Func<string, int?> _runningProcess;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private ITrainerSession? _session;
    private CancellationTokenSource? _connectionCancellation;
    private TrainerRuntimeSnapshot? _runtime;
    private long _revision, _connectionGeneration;
    private int _movementMultiplier = 2;
    private int _enabledFeatures;
    private bool _connecting, _available, _disposed;
    private bool _autoConnectPaused;
    private ulong _nextAutoConnectCheck;
    private int? _failedAutoConnectProcess;
    private string _status = "disconnected", _statusText = "未连接，所有效果关闭";
    private string? _error, _hotkeyWarning;
    public bool Dirty { get; set; } = true;

    public TrainerController(TrainerSettings settings, Action<Action> dispatch,
        Func<ITrainerSession>? sessionFactory = null, ITrainerHotkeys? hotkeys = null,
        Action<string>? launchGame = null, Func<ulong>? clock = null, Func<string, int?>? runningProcess = null)
    {
        _settings = settings;
        _dispatch = dispatch;
        _sessionFactory = sessionFactory ?? (() => new NativeTrainerSession());
        _hotkeys = hotkeys ?? new TrainerHotkeys();
        _launchGame = launchGame ?? NativeTrainerSession.LaunchGame;
        _clock = clock ?? (() => (ulong)Environment.TickCount64);
        _runningProcess = runningProcess ?? NativeTrainerSession.FindRunningProcess;
        _movementMultiplier = settings.MovementMultiplier;
        _enabledFeatures = settings.EnabledFeatures;
        _error = settings.LoadWarning;
    }

    public static bool IsTrainerCommand(string command) => command.StartsWith("trainer", StringComparison.Ordinal);

    public async Task ExecuteAsync(string command, JsonElement payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Off/disconnect remain available during a pending connection and any
        // optimizer activity. Cancellation uses a generation fence below.
        switch (command)
        {
            case "trainerState": Poll(); return;
            case "trainerDisableAll":
                DisableAll();
                return;
            case "trainerDisconnect":
                _autoConnectPaused = true;
                Disconnect("已断开，自动连接已暂停；功能偏好已保留"); return;
            case "trainerSetAutoConnect":
                _settings.SaveAutoConnect(RequiredBoolean(payload, "enabled"));
                _autoConnectPaused = false; _failedAutoConnectProcess = null; _nextAutoConnectCheck = 0;
                if (!_settings.AutoConnect && _connecting) Disconnect("自动连接已关闭，连接已取消");
                ClearError();
                break;
            case "trainerSelectExecutable":
                RequireDisconnected();
                _settings.SaveExecutable(RequiredString(payload, "path"));
                _autoConnectPaused = false; _failedAutoConnectProcess = null; _nextAutoConnectCheck = 0;
                _error = null; _status = "disconnected"; _statusText = "游戏路径与功能配置已记住";
                break;
            case "trainerConnect": _autoConnectPaused = false; await ConnectAsync(); break;
            case "trainerSetMovementMultiplier":
                var multiplier = RequiredInt(payload, "value");
                if (!TrainerFeatures.MovementMultipliers.Contains(multiplier)) throw new ArgumentException("移动倍率只能为 2、4、8 或 16。");
                _settings.SaveMovementMultiplier(multiplier);
                _session?.SetMovementMultiplier(multiplier);
                _movementMultiplier = multiplier;
                ClearError();
                break;
            case "trainerSetFeature":
                var feature = TrainerFeatures.All.FirstOrDefault(x => x.Id == RequiredString(payload, "id"))
                    ?? throw new ArgumentException("未知的实战功能。");
                var enabled = RequiredBoolean(payload, "enabled");
                Poll();
                _settings.SaveFeatures(enabled ? _enabledFeatures | feature.Flag : _enabledFeatures & ~feature.Flag);
                _enabledFeatures = _settings.EnabledFeatures;
                if (_available) _session?.SetFlags(_enabledFeatures);
                _error = null;
                break;
            case "trainerLaunch":
                RequireDisconnected();
                _launchGame(TrainerSettings.Validate(_settings.Executable));
                _autoConnectPaused = false; _nextAutoConnectCheck = 0;
                _error = null; _status = "disconnected"; _statusText = _settings.AutoConnect ? "已启动游戏，等待自动连接" : "已启动游戏，进入单人角色后点击连接";
                break;
            default: throw new ArgumentException($"不支持的实战操作：{command}");
        }
        Dirty = true;
    }

    private async Task ConnectAsync()
    {
        RequireDisconnected();
        var executable = TrainerSettings.Validate(_settings.Executable);
        var generation = ++_connectionGeneration;
        var session = _sessionFactory();
        var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;
        _session = session;
        _connecting = true;
        _available = false;
        _runtime = null;
        _status = "connecting"; _statusText = "正在连接游戏，所有效果保持关闭";
        _error = null; _hotkeyWarning = null;
        Dirty = true;
        try
        {
            session.SetMovementMultiplier(_movementMultiplier);
            session.SetFlags(0);
            await session.ConnectAsync(executable, cancellation.Token);
            if (_disposed || generation != _connectionGeneration)
            {
                session.Dispose();
                return;
            }
            session.SetFlags(0);
            _connecting = false;
            _hotkeyWarning = _hotkeys.Start(id => _dispatch(() => HandleHotkey(id, generation)));
            Poll();
        }
        catch when (_disposed || generation != _connectionGeneration)
        {
            // A late success/failure from a cancelled attempt cannot revive the
            // connection or replace a newer attempt's status/error.
            session.Dispose();
        }
        catch
        {
            Disconnect("连接失败，所有效果关闭");
            throw;
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _connectionGeneration)
            {
                _connectionCancellation = null;
                _connecting = false;
            }
            Dirty = true;
        }
    }

    private void RequireDisconnected()
    {
        if (_connecting) throw new InvalidOperationException("正在连接游戏，请等待完成或断开连接。");
        if (_session is not null) throw new InvalidOperationException("请先断开实战工具，再修改游戏路径或启动游戏。");
    }

    public void Poll()
    {
        if (_disposed || _connecting) return;
        if (_session is null) { TryAutoConnect(); return; }
        try
        {
            if (!_session.Connected) { Disconnect("游戏已退出，所有效果关闭"); return; }
            var fresh = _session.Read();
            if (fresh is not null) _runtime = fresh;
            var alive = _runtime is not null && _clock() >= _runtime.Tick && _clock() - _runtime.Tick < 3000;
            _available = _runtime is { Status: 2, Fault: 0 } && alive;
            _status = _runtime?.Fault > 0 || _runtime?.Status < 0 ? "fault"
                : !alive ? "stale" : _runtime!.Status == 3 ? "multiplayer" : _available ? "ready" : "waiting";
            _statusText = _status switch
            {
                "ready" => $"已连接 · {_session.Architecture}",
                "multiplayer" => "多人模式，所有效果已关闭",
                "waiting" => "等待进入单人角色，所有效果关闭",
                "stale" => "游戏未刷新，所有效果已关闭",
                _ => "组件检查失败，所有效果已关闭"
            };
            if (!_available && _session.Requested != 0) _session.SetFlags(0);
            if (_available && _session.Requested != _enabledFeatures) _session.SetFlags(_enabledFeatures);
            if (_status == "fault") _error = "运行时检查失败，效果已关闭。请重启游戏后重新连接。";
            // These compact events never include optimizer results. Coalescing
            // and a 250ms poll bound their volume even while the game updates.
            Dirty = true;
        }
        catch (Exception error)
        {
            try { Disconnect("连接中断，所有效果关闭"); }
            catch (Exception cleanupError)
            {
                ReportError(new InvalidOperationException($"{error.Message}（释放连接时：{cleanupError.Message}）"));
                return;
            }
            ReportError(error);
        }
    }

    private async void TryAutoConnect()
    {
        if (!_settings.AutoConnect || _autoConnectPaused || _settings.Executable is null || _clock() < _nextAutoConnectCheck) return;
        _nextAutoConnectCheck = _clock() + 3000;
        try
        {
            var process = _runningProcess(_settings.Executable);
            if (process is null) { _failedAutoConnectProcess = null; return; }
            if (process == _failedAutoConnectProcess) return;
            // A bad build/permission error must not cause repeated injections
            // into the same game. A new process or explicit retry unlocks it.
            _failedAutoConnectProcess = process;
            await ConnectAsync();
            _failedAutoConnectProcess = null;
        }
        catch (Exception error) { if (!_disposed) ReportError(error); }
    }

    private void HandleHotkey(int id, long generation)
    {
        if (_disposed || generation != _connectionGeneration || _session is null) return;
        try
        {
            if (id == 12) { DisableAll(); return; }
            Poll();
            if (!_available || _session is null || id < 1 || id > TrainerFeatures.All.Length) return;
            _settings.SaveFeatures(_enabledFeatures ^ TrainerFeatures.All[id - 1].Flag);
            _enabledFeatures = _settings.EnabledFeatures;
            _session.SetFlags(_enabledFeatures);
            _error = null;
            Dirty = true;
        }
        catch (Exception error) { ReportError(error); }
    }

    private void DisableAll()
    {
        // Emergency off must work even when the preference disk is unavailable.
        _enabledFeatures = 0;
        try { _settings.SaveFeatures(0); ClearError(); }
        finally
        {
            if (_connecting || _session is null) _autoConnectPaused = true;
            if (_connecting) Disconnect("连接已取消，所有效果关闭");
            else _session?.SetFlags(0);
            Dirty = true;
        }
    }

    public void ReportError(Exception error)
    {
        _error = error.Message;
        if (!_connecting && _session is null) { _status = "error"; _statusText = "操作未完成，所有效果关闭"; }
        Dirty = true;
    }

    private void ClearError()
    {
        _error = null;
        if (_session is null && !_connecting && _status == "error")
        {
            _status = "disconnected";
            _statusText = "未连接，所有效果关闭";
        }
    }

    private void Disconnect(string text)
    {
        ++_connectionGeneration;
        _connectionCancellation?.Cancel();
        _connectionCancellation = null;
        var session = _session;
        _session = null;
        try { session?.SetFlags(0); }
        finally
        {
            try { session?.Dispose(); }
            finally
            {
                try { _hotkeys.Stop(); }
                finally
                {
                    _connecting = false; _available = false; _runtime = null;
                    _status = "disconnected"; _statusText = text;
                    _error = null; _hotkeyWarning = null; Dirty = true;
                }
            }
        }
    }

    public TrainerState Snapshot()
    {
        var connected = !_connecting && _session?.Connected == true;
        var available = connected && _available;
        var requested = _session?.Requested ?? 0;
        var applied = available ? _runtime?.Applied ?? 0 : 0;
        // Do not expose previous-character resource values outside a live single-player session.
        var telemetry = available ? _runtime : null;
        return new(_sessionId, ++_revision, _settings.Executable, _settings.AutoConnect, _autoConnectPaused, connected, _connecting, available,
            _status, _statusText, _error, _hotkeyWarning,
            connected ? _session!.ProcessId : null, connected ? _session!.Architecture : null,
            _movementMultiplier, TrainerFeatures.All.Select(feature => new TrainerFeatureState(
                feature.Id, feature.Title, feature.Description, feature.Shortcut,
                (_enabledFeatures & feature.Flag) != 0,
                (requested & feature.Flag) != 0, (applied & feature.Flag) != 0,
                ((requested & feature.Flag) != 0) != ((applied & feature.Flag) != 0))).ToArray(),
            Finite(telemetry?.Health), Finite(telemetry?.MaxHealth), Finite(telemetry?.Mana), Finite(telemetry?.MaxMana),
            Finite(telemetry?.HealthPotionRecovery), Finite(telemetry?.ManaPotionRecovery),
            telemetry?.HealthPotionCooldown, telemetry?.ManaPotionCooldown);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Disconnect("已退出，所有效果关闭"); }
        finally { _disposed = true; _hotkeys.Dispose(); }
    }

    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    private static string RequiredString(JsonElement payload, string key) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException($"缺少有效的 {key} 参数。");
    private static int RequiredInt(JsonElement payload, string key) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"缺少有效的 {key} 整数参数。");
    private static bool RequiredBoolean(JsonElement payload, string key) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new ArgumentException($"缺少有效的 {key} 开关参数。");
}
