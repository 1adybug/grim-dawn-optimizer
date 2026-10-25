namespace GrimDawnOptimizer.Bridge.Trainer;

public sealed record TrainerFeatureDefinition(string Id, int Flag, string Title, string Description, string Shortcut);

public static class TrainerFeatures
{
    public const int Mask = 1951;
    public static readonly int[] MovementMultipliers = [2, 4, 8, 16];
    public static readonly TrainerFeatureDefinition[] All =
    [
        new("life", 1, "无限生命", "锁定可用生命，并同时启用护甲与抗性防护；仅作用于当前游戏状态。", "Ctrl+Alt+F1"),
        new("mana", 2, "无限蓝量", "锁定可用蓝量；保留光环等技能的预留。", "Ctrl+Alt+F2"),
        new("kill", 4, "一击必杀", "当前角色命中敌人时造成致命伤害。", "Ctrl+Alt+F3"),
        new("moveSpeed", 8, "移动速度", "按所选倍率提高当前角色的移动速度；选择倍率不会自动开启。", "Ctrl+Alt+F4"),
        new("attackSpeed", 128, "最大攻击速度", "仅将攻击速度提升至游戏允许的上限。", "Ctrl+Alt+F5"),
        new("castSpeed", 256, "最大施法速度", "仅将施法速度提升至游戏允许的上限。", "Ctrl+Alt+F6"),
        new("cooldown", 16, "技能无冷却", "清除当前角色的技能冷却。", "Ctrl+Alt+F7"),
        new("autoHealth", 512, "自动加血", "缺失生命达到血药回复值且药水就绪时，自动使用 R 对应的血药技能。", "Ctrl+Alt+F8"),
        new("autoMana", 1024, "自动回蓝", "扣除技能预留后，缺失可用蓝量达到蓝药回复值且药水就绪时，自动使用 E 对应的蓝药技能。", "Ctrl+Alt+F9")
    ];
}

public sealed record TrainerFeatureState(string Id, string Title, string Description, string Shortcut, bool Requested, bool Applied, bool Pending);

public sealed record TrainerState(
    string SessionId, long Revision, string? ConfiguredExecutable,
    bool Connected, bool Connecting, bool Available, string Status, string StatusText,
    string? Error, string? HotkeyWarning, int? Pid, string? Architecture, int MovementMultiplier,
    IReadOnlyList<TrainerFeatureState> Features,
    double? Health, double? MaxHealth, double? Mana, double? MaxMana,
    double? HealthPotionRecovery, double? ManaPotionRecovery,
    int? HealthPotionCooldown, int? ManaPotionCooldown);

public sealed record TrainerRuntimeSnapshot(
    int Status, int Applied, uint Fault, ulong Tick,
    double Health, float MaxHealth, float Mana, float MaxMana,
    float HealthPotionRecovery, float ManaPotionRecovery,
    int HealthPotionCooldown, int ManaPotionCooldown, string Message = "");

public interface ITrainerSession : IDisposable
{
    bool Connected { get; }
    int ProcessId { get; }
    string Architecture { get; }
    int Requested { get; }
    Task ConnectAsync(string executable, CancellationToken cancellationToken);
    void SetFlags(int flags);
    void SetMovementMultiplier(int value);
    TrainerRuntimeSnapshot? Read();
}

public interface ITrainerHotkeys : IDisposable
{
    string? Start(Action<int> onHotkey);
    void Stop();
}
