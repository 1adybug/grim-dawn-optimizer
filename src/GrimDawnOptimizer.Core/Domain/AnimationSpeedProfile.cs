namespace GrimDawnOptimizer.Core.Domain;

/// <summary>Animation timing read from an installation's DBR and ANM resources.</summary>
public sealed class AnimationSpeedProfile
{
    public string TableRecordName { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, AnimationTimingGroup> AttackGroups { get; init; } =
        new Dictionary<string, AnimationTimingGroup>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> UnresolvedResources { get; init; } = [];

    // Player::CalculateSkillDps and the attacks-per-second UI both apply this
    // adjustment for the ordinary, non-cooldown frequency branch.
    public const double PanelFrequencyFactor = 0.95;

    public double? UnarmedAttackSeconds =>
        AttackGroups.TryGetValue("unarmed", out var group) ? group.MeanSeconds : null;

    public double? UnarmedPanelAttacksPerSecond => UnarmedAttackSeconds is > 0 and var seconds
        ? PanelFrequencyFactor / seconds : null;

    public double? GetAttackAnimationRatio(string prefix) =>
        UnarmedAttackSeconds is > 0 and var unarmed &&
        AttackGroups.TryGetValue(prefix, out var group) && group.MeanSeconds > 0
            ? unarmed / group.MeanSeconds : null;
}

public sealed record AnimationTimingGroup(string Prefix, double MeanSeconds, IReadOnlyList<AnimationTimingEntry> Entries);

public sealed record AnimationTimingEntry(
    string ResourcePath, string ArchivePath, int Version, uint Frames, int FramesPerSecond,
    double SpeedMultiplier, double AdjustedSeconds);
