using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace GrimDawnOptimizer.Core.Domain;

public enum EquipmentSlot
{
    Head,
    Shoulders,
    Chest,
    Hands,
    Legs,
    Feet,
    Waist,
    Amulet,
    Medal,
    Relic,
    Ring,
    RingLeft,
    RingRight,
    MainHand,
    OffHand
}

public enum ItemSourceKind { Equipped, Backpack, PersonalStash, SharedStash }
public enum AttachmentKind { Component, Augment }
public enum MetricDirection { Maximize, Minimize }
public enum ConfidenceLevel { High, Medium, Low }

public sealed record ItemSourceLocation(
    ItemSourceKind Kind,
    string FilePath,
    int ContainerIndex = 0,
    int X = -1,
    int Y = -1)
{
    public string DisplayName => Kind switch
    {
        ItemSourceKind.Equipped => "当前穿戴",
        ItemSourceKind.Backpack => $"背包 {ContainerIndex + 1} ({X},{Y})",
        ItemSourceKind.PersonalStash => $"个人仓库 {ContainerIndex + 1} ({X},{Y})",
        ItemSourceKind.SharedStash => $"共享仓库 {ContainerIndex + 1} ({X},{Y})",
        _ => Kind.ToString()
    };
}

public sealed class StatBlock
{
    private readonly Dictionary<string, double> _values;

    [JsonConstructor]
    public StatBlock(IReadOnlyDictionary<string, double>? values = null) =>
        _values = values is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(values, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, double> Values => _values;
    public double this[string key] => _values.TryGetValue(key, out var value) ? value : 0d;

    public void Set(string key, double value) => _values[key] = value;
    public void Add(string key, double value)
    {
        if (Math.Abs(value) < 1e-12) return;
        _values[key] = this[key] + value;
    }

    public void Add(StatBlock other, double scale = 1d)
    {
        foreach (var (key, value) in other.Values) Add(key, value * scale);
    }

    public StatBlock Clone() => new(_values);
    public static StatBlock operator +(StatBlock left, StatBlock right)
    {
        var result = left.Clone();
        result.Add(right);
        return result;
    }
}

public sealed record UnsupportedEffect(string Record, string Description, IReadOnlyList<string> AffectedMetrics);

public sealed record TriggeredEffect(
    string Id,
    string Name,
    double Chance,
    double CooldownSeconds,
    StatBlock Damage,
    string Trigger = "OnAttack",
    double DurationSeconds = 0,
    bool IsDefensive = false);

public sealed class ItemInstance
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required EquipmentSlot Slot { get; init; }
    public required string BaseRecord { get; init; }
    public string PrefixRecord { get; init; } = string.Empty;
    public string SuffixRecord { get; init; } = string.Empty;
    public string ModifierRecord { get; init; } = string.Empty;
    public string TransmuteRecord { get; init; } = string.Empty;
    public uint Seed { get; init; }
    public string ComponentRecord { get; init; } = string.Empty;
    public string CompletionBonusRecord { get; init; } = string.Empty;
    public uint ComponentSeed { get; init; }
    public string AugmentRecord { get; init; } = string.Empty;
    public uint AugmentSeed { get; init; }
    public int LevelRequirement { get; init; }
    public int PhysiqueRequirement { get; init; }
    public int CunningRequirement { get; init; }
    public int SpiritRequirement { get; init; }
    public bool IsTwoHanded { get; init; }
    public string SetId { get; init; } = string.Empty;
    public StatBlock Stats { get; init; } = new();
    public IReadOnlyList<TriggeredEffect> Effects { get; init; } = [];
    public IReadOnlyList<UnsupportedEffect> UnsupportedEffects { get; init; } = [];
    public required ItemSourceLocation Source { get; init; }

    public static string CreateStableId(ItemSourceLocation source, string baseRecord, uint seed)
    {
        var raw = $"{Path.GetFullPath(source.FilePath)}|{source.Kind}|{source.ContainerIndex}|{source.X}|{source.Y}|{baseRecord}|{seed}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
    }
}

public sealed class AttachmentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AttachmentKind Kind { get; init; }
    public IReadOnlySet<EquipmentSlot> CompatibleSlots { get; init; } = new HashSet<EquipmentSlot>();
    public int LevelRequirement { get; init; }
    public string RequiredFormula { get; init; } = string.Empty;
    public string RequiredFaction { get; init; } = string.Empty;
    public double RequiredReputation { get; init; }
    public StatBlock Stats { get; init; } = new();
    public IReadOnlyList<TriggeredEffect> Effects { get; init; } = [];
    public IReadOnlyList<UnsupportedEffect> UnsupportedEffects { get; init; } = [];
}

public sealed record CharacterSkill(
    string Record,
    string Name,
    int Level,
    int DevotionLevel,
    bool Enabled,
    string AutoCastSkill,
    string AutoCastController);

public sealed class CharacterSnapshot
{
    public required string Name { get; init; }
    public required string SaveFilePath { get; init; }
    public int Level { get; init; }
    public bool IsHardcore { get; init; }
    public int Physique { get; init; }
    public int Cunning { get; init; }
    public int Spirit { get; init; }
    public StatBlock BaselineStats { get; init; } = new();
    public IReadOnlyList<CharacterSkill> Skills { get; init; } = [];
    public IReadOnlyDictionary<string, double> FactionReputation { get; init; } = new Dictionary<string, double>();
    public IReadOnlySet<string> KnownFormulas { get; init; } = new HashSet<string>();
    public IReadOnlyList<ItemInstance> Items { get; init; } = [];
    public IReadOnlyDictionary<EquipmentSlot, string> CurrentLoadout { get; init; } = new Dictionary<EquipmentSlot, string>();
    public IReadOnlyList<string> ImportWarnings { get; init; } = [];
}

public sealed record IncomingDamageProfile(
    double HitsPerSecond = 1.2,
    double PhysicalPerHit = 1200,
    double FirePerHit = 450,
    double ColdPerHit = 0,
    double LightningPerHit = 0,
    double AcidPerHit = 0,
    double PiercePerHit = 0,
    double VitalityPerHit = 0,
    double AetherPerHit = 0,
    double ChaosPerHit = 0);

public sealed class CombatScenario
{
    public string Name { get; set; } = "单体 Boss · 10 秒";
    public int EnemyLevel { get; set; } = 100;
    public double EnemyArmor { get; set; } = 2200;
    public double EnemyOffensiveAbility { get; set; } = 2850;
    public double EnemyLifeLeechResistance { get; set; } = 60;
    public double DurationSeconds { get; set; } = 10;
    public int TargetCount { get; set; } = 1;
    public Dictionary<string, double> EnemyResistances { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["physical"] = 18, ["pierce"] = 18, ["fire"] = 25, ["cold"] = 25,
        ["lightning"] = 25, ["acid"] = 25, ["vitality"] = 25, ["aether"] = 25, ["chaos"] = 25
    };
    public IncomingDamageProfile IncomingDamage { get; set; } = new();
}

public sealed class SkillRotationDefinition
{
    public string MainSkillRecord { get; set; } = string.Empty;
    public string MainSkillName { get; set; } = "默认武器攻击";
    public double WeaponDamagePercent { get; set; } = 100;
    public double BaseAttacksPerSecond { get; set; } = 1.5;
    public double EnergyPerUse { get; set; }
    public StatBlock FlatDamage { get; set; } = new();
    public List<string> EnabledCooldownSkills { get; set; } = [];
}

public sealed class MetricRule
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PriorityTier { get; set; } = 1;
    public double Weight { get; set; } = 1;
    public MetricDirection Direction { get; set; } = MetricDirection.Maximize;
    public double? HardMinimum { get; set; }
    public double? HardMaximum { get; set; }
    public double NormalizationStep { get; set; }
}

public sealed class OptimizationProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "默认构筑";
    public CombatScenario Scenario { get; set; } = new();
    public SkillRotationDefinition Rotation { get; set; } = new();
    public List<MetricRule> Metrics { get; set; } =
    [
        new() { Key = "dps", DisplayName = "持续 DPS", PriorityTier = 1, Weight = 1 },
        new() { Key = "effectiveHealth", DisplayName = "有效生命", PriorityTier = 2, Weight = 1 },
        new() { Key = "fireResistance", DisplayName = "火焰抗性", PriorityTier = 0, HardMinimum = 80, NormalizationStep = 1 },
        new() { Key = "coldResistance", DisplayName = "冰冷抗性", PriorityTier = 0, HardMinimum = 80, NormalizationStep = 1 },
        new() { Key = "lightningResistance", DisplayName = "闪电抗性", PriorityTier = 0, HardMinimum = 80, NormalizationStep = 1 }
    ];
    public int SearchBudgetSeconds { get; set; } = 5;
    public int ResultCount { get; set; } = 10;
    public int MonteCarloTrials { get; set; } = 2000;
    public bool UseUnlockedAttachments { get; set; } = true;
}

public sealed class LoadoutVariant(ItemInstance item, AttachmentDefinition? component, AttachmentDefinition? augment)
{
    private StatBlock? _combinedStats;
    private IReadOnlyList<TriggeredEffect>? _effects;
    private IReadOnlyList<UnsupportedEffect>? _unsupportedEffects;

    public ItemInstance Item { get; } = item;
    public AttachmentDefinition? Component { get; } = component;
    public AttachmentDefinition? Augment { get; } = augment;

    public StatBlock CombinedStats => _combinedStats ??= BuildCombinedStats();

    private StatBlock BuildCombinedStats()
    {
        var result = Item.Stats.Clone();
        if (Component is not null) result.Add(Component.Stats);
        if (Augment is not null) result.Add(Augment.Stats);
        return result;
    }

    public IReadOnlyList<TriggeredEffect> Effects => _effects ??=
        Item.Effects.Concat(Component?.Effects ?? []).Concat(Augment?.Effects ?? []).ToArray();

    public IReadOnlyList<UnsupportedEffect> UnsupportedEffects => _unsupportedEffects ??=
        Item.UnsupportedEffects.Concat(Component?.UnsupportedEffects ?? []).Concat(Augment?.UnsupportedEffects ?? []).ToArray();
}

public sealed class CalculatedLoadout
{
    public required IReadOnlyDictionary<EquipmentSlot, LoadoutVariant> Slots { get; init; }
    public required StatBlock Stats { get; init; }
    public IReadOnlyList<TriggeredEffect> Effects { get; init; } = [];
    public IReadOnlyList<UnsupportedEffect> UnsupportedEffects { get; init; } = [];
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public bool IsValid => ValidationErrors.Count == 0;
}

public sealed class SimulationReport
{
    public double ExpectedDps { get; init; }
    public double EffectiveHealth { get; init; }
    public double IncomingDps { get; init; }
    public double SustainPerSecond { get; init; }
    public double TimeToDeathSeconds { get; init; }
    public double HitChance { get; init; }
    public double CritChance { get; init; }
    public double MonteCarloDpsMean { get; init; }
    public double MonteCarloDpsLow95 { get; init; }
    public double MonteCarloDpsHigh95 { get; init; }
    public double MonteCarloSurvivalMean { get; init; }
    public IReadOnlyList<UnsupportedEffect> UnsupportedEffects { get; init; } = [];
}

public sealed class LoadoutResult
{
    public required int Rank { get; set; }
    public required CalculatedLoadout Loadout { get; init; }
    public required SimulationReport Simulation { get; set; }
    public required IReadOnlyDictionary<string, double> Metrics { get; init; }
    public required IReadOnlyList<double> TierScores { get; init; }
    public required IReadOnlyDictionary<string, double> ConstraintViolations { get; init; }
    public required ConfidenceLevel Confidence { get; init; }
    public bool IsFeasible => ConstraintViolations.Count == 0 && Loadout.IsValid;
}

public sealed record SearchProgress(
    string Phase,
    int EvaluatedCandidates,
    int Iteration,
    double BestScore,
    TimeSpan Elapsed,
    string Message);

public sealed class OptimizationRun
{
    public required IReadOnlyList<LoadoutResult> Results { get; init; }
    public required int EvaluatedCandidates { get; init; }
    public required int Iterations { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required string StopReason { get; init; }
}

public sealed class GameDataCatalog
{
    public string Version { get; init; } = "unknown";
    public IReadOnlyDictionary<string, GameRecord> Records { get; init; } = new Dictionary<string, GameRecord>();
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<AttachmentDefinition> Attachments { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, StatBlock>> SetBonuses { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>();
}

public sealed class GameRecord
{
    public required string RecordName { get; init; }
    public string RecordType { get; init; } = string.Empty;
    public StatBlock NumericValues { get; init; } = new();
    public IReadOnlyDictionary<string, IReadOnlyList<double>> NumericSeries { get; init; } =
        new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TextValues { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public string Text(string key) => TextValues.TryGetValue(key, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
}

public sealed record GameInstallation(string RootPath, string Source, string Version, IReadOnlyList<string> DataFiles)
{
    public override string ToString() => RootPath;
}

public sealed record SaveLocation(string RootPath, string Source, IReadOnlyList<string> CharacterFiles, string? SharedStashFile, string? FormulaFile)
{
    public override string ToString() => RootPath;
}
public sealed record DiscoveryResult(IReadOnlyList<GameInstallation> Installations, IReadOnlyList<SaveLocation> SaveLocations);
