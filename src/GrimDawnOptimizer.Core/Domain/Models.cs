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

public enum ItemSourceKind { Equipped, AlternateWeaponSet, Backpack, PersonalStash, SharedStash }
public enum AttachmentKind { Component, Augment }
public enum AttachmentAcquisitionKind { Loose, Installed, Blacksmith, FactionVendor }
public enum MetricDirection { Maximize, Minimize }
public enum ConfidenceLevel { High, Medium, Low }
public enum DpsCalibrationMode { None, SingleAnchor, DualAnchor, SingleAnchorFallback }
public enum GameDifficulty { Normal, Veteran, Elite, Ultimate }

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
        ItemSourceKind.AlternateWeaponSet => $"备用武器组 {ContainerIndex + 1}",
        ItemSourceKind.Backpack => $"背包第 {ContainerIndex + 1} 页 · 左上格 ({X + 1},{Y + 1})",
        ItemSourceKind.PersonalStash => $"个人仓库第 {ContainerIndex + 1} 页 · 左上格 ({X + 1},{Y + 1})",
        ItemSourceKind.SharedStash => $"共享仓库第 {ContainerIndex + 1} 页 · 左上格 ({X + 1},{Y + 1})",
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
    bool IsDefensive = false,
    double WeaponDamagePercent = 0);

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
    public bool SeedStatsResolved { get; init; }
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
    public string AttachmentQualifier { get; init; } = string.Empty;
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
    public string RecordId { get; init; } = string.Empty;
    public string CompletionBonusRecord { get; init; } = string.Empty;
    public bool IsOwned { get; init; }
    public required string Name { get; init; }
    public required AttachmentKind Kind { get; init; }
    public IReadOnlySet<EquipmentSlot> CompatibleSlots { get; init; } = new HashSet<EquipmentSlot>();
    public IReadOnlySet<string> CompatibleItemQualifiers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int LevelRequirement { get; init; }
    public string RequiredFormula { get; init; } = string.Empty;
    public string RequiredFaction { get; init; } = string.Empty;
    public double RequiredReputation { get; init; }
    public IReadOnlyList<AttachmentAcquisitionSource> AcquisitionSources { get; init; } = [];
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

public sealed record OwnedAttachmentInstance(
    string RecordId,
    AttachmentKind Kind,
    string CompletionBonusRecord = "",
    AttachmentAcquisitionSource? Source = null);

public sealed record AttachmentAcquisitionSource(
    AttachmentAcquisitionKind Kind,
    ItemSourceLocation? Location = null,
    string HostItemId = "",
    string HostItemName = "",
    EquipmentSlot? HostSlot = null,
    string FormulaRecord = "",
    string FactionRecord = "",
    string FactionName = "",
    string FactionReputationKey = "",
    string ReputationName = "",
    double RequiredReputation = 0,
    bool IsAlwaysAvailable = false);

public sealed class CharacterSnapshot
{
    public required string Name { get; init; }
    public required string SaveFilePath { get; init; }
    public int Level { get; init; }
    public bool IsHardcore { get; init; }
    public bool IsFemale { get; init; }
    public int Physique { get; init; }
    public int Cunning { get; init; }
    public int Spirit { get; init; }
    public StatBlock BaselineStats { get; init; } = new();
    public IReadOnlyList<CharacterSkill> Skills { get; init; } = [];
    public IReadOnlyDictionary<string, double> FactionReputation { get; init; } = new Dictionary<string, double>();
    public IReadOnlySet<string> KnownFormulas { get; init; } = new HashSet<string>();
    public IReadOnlyList<ItemInstance> Items { get; init; } = [];
    public IReadOnlyList<OwnedAttachmentInstance> OwnedAttachments { get; init; } = [];
    public IReadOnlyDictionary<EquipmentSlot, string> CurrentLoadout { get; init; } = new Dictionary<EquipmentSlot, string>();
    public string PrimarySkillRecord { get; init; } = string.Empty;
    public string SecondarySkillRecord { get; init; } = string.Empty;
    public bool CanDualWield { get; init; }
    public IReadOnlyList<string> ImportWarnings { get; init; } = [];
}

public sealed record IncomingDamageProfile(
    double HitsPerSecond = 1.2,
    double PhysicalPerHit = 750,
    double FirePerHit = 100,
    double ColdPerHit = 100,
    double LightningPerHit = 100,
    double AcidPerHit = 100,
    double PiercePerHit = 100,
    double VitalityPerHit = 100,
    double AetherPerHit = 100,
    double ChaosPerHit = 100,
    double BleedingPerHit = 100);

public sealed class CombatScenario
{
    public GameDifficulty Difficulty { get; set; } = GameDifficulty.Normal;
    public double EnemyDamageIncreasePercent { get; set; }
    public Dictionary<string, double> PlayerResistanceReduction { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public StatBlock DifficultyEnemyModifiers { get; set; } = new();
    [JsonIgnore] public StatBlock? DifficultyPlayerModifiers { get; set; }
    [JsonIgnore] public string DifficultySummary { get; set; } = "请选择目标难度；承伤使用可比较的混合伤害模板。";
    public string Name { get; set; } = "同级 BOSS 平均值 · 10 秒";
    public int EnemyLevel { get; set; } = 100;
    public double EnemyHealth { get; set; } = 300_000;
    public double EnemyArmor { get; set; } = 2200;
    public double EnemyArmorAbsorption { get; set; } = 70;
    public double EnemyOffensiveAbility { get; set; } = 2850;
    public double EnemyDefensiveAbility { get; set; } = 2900;
    public double EnemyLifeLeechResistance { get; set; } = 60;
    public int BossSampleCount { get; set; }
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
    public double CooldownSeconds { get; set; }
    public double ActivationRate { get; set; } = 1;
    public double WeaponPoolChance { get; set; }
    public double WeaponPoolDamageMultiplier { get; set; } = 1;
    public bool UsesAttackSpeed { get; set; } = true;
    public bool IsFallback { get; set; } = true;
    public string CalculationSummary { get; set; } = "默认武器攻击估算";
    public StatBlock FlatDamage { get; set; } = new();
    public StatBlock WeaponPoolFlatDamage { get; set; } = new();
    public List<WeaponPoolAttackDefinition> WeaponPoolAttacks { get; set; } = [];
    public List<SkillSecondaryAttackDefinition> SecondaryAttacks { get; set; } = [];
    public List<string> EnabledCooldownSkills { get; set; } = [];
}

public sealed class SkillSecondaryAttackDefinition
{
    public string Record { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public double WeaponDamagePercent { get; set; }
    public StatBlock FlatDamage { get; set; } = new();
    public List<SkillDamageSourceDefinition> SourceRecords { get; set; } = [];
}

public sealed class SkillDamageSourceDefinition
{
    public string Record { get; set; } = string.Empty;
    public int Level { get; set; }
    public double WeaponDamagePercent { get; set; }
    public StatBlock TranslatedStats { get; set; } = new();
}

public sealed class WeaponPoolAttackDefinition
{
    public string Record { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Chance { get; set; }
    public double WeaponDamageMultiplier { get; set; } = 1;
    public double WeaponStrikes { get; set; } = 1;
    public StatBlock FlatDamage { get; set; } = new();
}

public sealed class MetricRule
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Weight { get; set; } = 1;
    public MetricDirection Direction { get; set; } = MetricDirection.Maximize;
}

public sealed class OptimizationProfile
{
    public int SchemaVersion { get; set; } = 7;
    public string Name { get; set; } = "默认构筑";
    public CombatScenario Scenario { get; set; } = new();
    public SkillRotationDefinition Rotation { get; set; } = new();
    public List<MetricRule> Metrics { get; set; } = SupportedMetricCatalog.CreateDefaults();
    public int SearchBudgetSeconds { get; set; } = 180;
    public int ResultCount { get; set; } = 10;
    public int MonteCarloTrials { get; set; } = 4000;
    public bool UseUnlockedAttachments { get; set; } = true;
    [JsonIgnore]
    public bool UsePanelDpsCalibration { get; set; }
    [JsonIgnore]
    public double CurrentWeaponAttackDps { get; set; }
    [JsonIgnore]
    public double CurrentPanelDps { get; set; }
    [JsonIgnore] public Dictionary<string, double> CurrentPanelStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public IReadOnlyDictionary<string, double> RuntimePanelCorrections { get; set; } = new Dictionary<string, double>();
    [JsonIgnore]
    public double RuntimeDpsCalibrationFactor { get; set; } = 1;
    [JsonIgnore]
    public double RuntimeWeaponDpsCalibrationFactor { get; set; } = 1;
    [JsonIgnore]
    public double RuntimeSkillAddedDpsCalibrationFactor { get; set; } = 1;
    [JsonIgnore]
    public DpsCalibrationMode RuntimeDpsCalibrationMode { get; set; }
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
    public double RetaliationDps { get; init; }
    public double UnmitigatedDps { get; init; }
    public double TimeToKillSeconds { get; init; }
    public double EffectiveHealth { get; init; }
    public double IncomingDps { get; init; }
    public double SustainPerSecond { get; init; }
    public double Health { get; init; }
    public double RawIncomingDps { get; init; }
    public double HealthRegenPerSecond { get; init; }
    public double EffectiveLifeSteal { get; init; }
    public double LifeStealEligibleDps { get; init; }
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
    public IReadOnlyList<ItemSwapComparison> SingleItemComparisons { get; set; } = [];
    public required int Rank { get; set; }
    public required CalculatedLoadout Loadout { get; init; }
    public required SimulationReport Simulation { get; set; }
    public double EquipmentOnlyUnmitigatedDps { get; set; }
    public required IReadOnlyDictionary<string, double> Metrics { get; init; }
    public required IReadOnlyList<double> TierScores { get; init; }
    public IReadOnlyDictionary<string, double> ScoreContributions { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public required ConfidenceLevel Confidence { get; init; }
}

public sealed record ItemSwapComparison(
    EquipmentSlot Slot, string ItemName, bool IsValid, string Explanation,
    IReadOnlyDictionary<string, double> Before, IReadOnlyDictionary<string, double> After);

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
    // Resolve against the current installation on every load. Animation archives
    // can change independently of the cached database and localization files.
    [JsonIgnore]
    public IReadOnlyDictionary<string, AnimationSpeedProfile> AnimationProfiles { get; set; } =
        new Dictionary<string, AnimationSpeedProfile>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AttachmentDefinition> Attachments { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, StatBlock>> SetBonuses { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<int, StatBlock>>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<TriggeredEffect>>> SetBonusEffects { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<TriggeredEffect>>>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<UnsupportedEffect>>> SetBonusUnsupportedEffects { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<UnsupportedEffect>>>();
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
public sealed record DiscoveryResult(IReadOnlyList<GameInstallation> Installations, IReadOnlyList<SaveLocation> SaveLocations)
{
    public string? RunningGameRoot { get; init; }
    public string? PreferredSaveRoot { get; init; }
}
