using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.App.ViewModels;

public sealed class ResultViewModel
{
    private static readonly EquipmentSlot[] DisplaySlots =
    [
        EquipmentSlot.Head, EquipmentSlot.Shoulders, EquipmentSlot.Chest, EquipmentSlot.Hands,
        EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Waist, EquipmentSlot.Amulet,
        EquipmentSlot.Medal, EquipmentSlot.Relic, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
        EquipmentSlot.MainHand, EquipmentSlot.OffHand
    ];

    public ResultViewModel(LoadoutResult result, CharacterSnapshot character, IReadOnlyList<MetricRule> metricRules)
    {
        Model = result;
        var currentItems = character.CurrentLoadout.ToDictionary(
            x => x.Key,
            x => character.Items.FirstOrDefault(i => i.Id == x.Value)?.Name ?? "—");
        var usesTwoHands = result.Loadout.Slots.TryGetValue(EquipmentSlot.MainHand, out var mainHand) && mainHand.Item.IsTwoHanded;
        SlotRows = DisplaySlots.Select(slot =>
        {
            var currentItem = currentItems.GetValueOrDefault(slot, "—");
            if (result.Loadout.Slots.TryGetValue(slot, out var variant))
                return new SlotResultViewModel(
                    SlotName(slot), currentItem, variant.Item.Name,
                    FormatAttachment(variant.Component),
                    FormatAttachment(variant.Augment),
                    variant.Item.Source.DisplayName, currentItem != variant.Item.Name);

            var candidate = slot == EquipmentSlot.OffHand && usesTwoHands ? "由双手武器占用" : "—（无可用装备）";
            return new SlotResultViewModel(SlotName(slot), currentItem, candidate, "—", "—", "—", currentItem != "—");
        }).ToArray();
        WarningRows = result.Loadout.UnsupportedEffects.Select(x => $"{x.Description} · {x.Record}")
            .Concat(result.Loadout.ValidationErrors)
            .Concat(character.ImportWarnings)
            .Distinct().ToArray();
        var labels = metricRules.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        ScoreBreakdown = string.Join(" · ", result.ScoreContributions.Select(x =>
            $"{labels.GetValueOrDefault(x.Key, x.Key)} {x.Value:+0.000;-0.000;0.000}"));
    }

    public LoadoutResult Model { get; }
    public int Rank => Model.Rank;
    public string Title => $"候选 {Rank:00}";
    public string Dps => FormatNumber(Model.Simulation.ExpectedDps);
    public string RetaliationDps => FormatNumber(Model.Simulation.RetaliationDps);
    public string UnmitigatedDps => FormatNumber(Model.Simulation.UnmitigatedDps);
    public string EquipmentOnlyUnmitigatedDps => FormatNumber(Model.EquipmentOnlyUnmitigatedDps);
    public string TimeToKill => double.IsPositiveInfinity(Model.Simulation.TimeToKillSeconds) ? "∞" : $"{Model.Simulation.TimeToKillSeconds:0.0}s";
    public string EffectiveHealth => FormatNumber(Model.Simulation.EffectiveHealth);
    public string TimeToDeath => double.IsPositiveInfinity(Model.Simulation.TimeToDeathSeconds) ? "∞" : $"{Model.Simulation.TimeToDeathSeconds:0.0}s";
    public string Confidence => Model.Confidence switch { ConfidenceLevel.High => "高置信度", ConfidenceLevel.Medium => "中置信度", _ => "低置信度" };
    public string Validity => Model.Loadout.IsValid ? "配装合法" : "配装存在兼容性问题";
    public string MonteCarloRange => Model.Simulation.MonteCarloDpsHigh95 > 0
        ? $"95% · {FormatNumber(Model.Simulation.MonteCarloDpsLow95)} – {FormatNumber(Model.Simulation.MonteCarloDpsHigh95)}"
        : "等待抽样复核";
    public string ScoreSummary => $"最终权重得分：{Model.TierScores.FirstOrDefault():0.000}";
    public string ScoreBreakdown { get; }
    public IReadOnlyList<SlotResultViewModel> SlotRows { get; }
    public IReadOnlyList<string> WarningRows { get; }
    public bool HasWarnings => WarningRows.Count > 0;
    public string WarningSummary => string.Join(Environment.NewLine, WarningRows);

    private static string FormatNumber(double value) => double.IsPositiveInfinity(value) ? "∞" : value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.00}m",
        >= 1_000 => $"{value / 1_000:0.0}k",
        _ => $"{value:0}"
    };

    public static string FormatAttachment(AttachmentDefinition? attachment)
    {
        if (attachment is null) return "—";
        var selected = attachment.AcquisitionSources
            .Where(x => SourceCategory(x) < int.MaxValue)
            .GroupBy(SourceCategory)
            .OrderBy(x => x.Key)
            .Select(x => x.OrderBy(InstalledLocationRank)
                .ThenBy(source => source.Location?.ContainerIndex ?? int.MaxValue)
                .ThenBy(source => source.Location?.Y ?? int.MaxValue)
                .ThenBy(source => source.Location?.X ?? int.MaxValue)
                .ThenBy(source => source.HostItemName, StringComparer.CurrentCultureIgnoreCase)
                .First())
            .Take(2)
            .Select(FormatAcquisitionSource)
            .ToArray();
        if (selected.Length == 0) return $"{attachment.Name}{Environment.NewLine}来源未解析";
        var lines = new List<string> { attachment.Name, $"首选：{selected[0]}" };
        if (selected.Length > 1) lines.Add($"备选：{selected[1]}");
        return string.Join(Environment.NewLine, lines);
    }

    private static int SourceCategory(AttachmentAcquisitionSource source) => source.Kind switch
    {
        AttachmentAcquisitionKind.Loose when source.Location?.Kind == ItemSourceKind.Backpack => 0,
        AttachmentAcquisitionKind.Loose when source.Location?.Kind == ItemSourceKind.PersonalStash => 1,
        AttachmentAcquisitionKind.Loose when source.Location?.Kind == ItemSourceKind.SharedStash => 2,
        AttachmentAcquisitionKind.Installed => 3,
        AttachmentAcquisitionKind.Blacksmith => 4,
        AttachmentAcquisitionKind.FactionVendor => 4,
        _ => int.MaxValue
    };

    private static int InstalledLocationRank(AttachmentAcquisitionSource source) => source.Kind != AttachmentAcquisitionKind.Installed
        ? 0
        : source.Location?.Kind switch
        {
            ItemSourceKind.Equipped => 0,
            ItemSourceKind.AlternateWeaponSet => 1,
            ItemSourceKind.Backpack => 2,
            ItemSourceKind.PersonalStash => 3,
            ItemSourceKind.SharedStash => 4,
            _ => 5
        };

    private static string FormatAcquisitionSource(AttachmentAcquisitionSource source) => source.Kind switch
    {
        AttachmentAcquisitionKind.Loose => source.Location?.DisplayName ?? "散装附件（位置未知）",
        AttachmentAcquisitionKind.Installed => FormatInstalledSource(source),
        AttachmentAcquisitionKind.Blacksmith => source.IsAlwaysAvailable
            ? "铁匠制作（基础配方）"
            : "铁匠制作（配方已学会）",
        AttachmentAcquisitionKind.FactionVendor =>
            $"阵营军需官：{Fallback(source.FactionName, "未知阵营")}" +
            (string.IsNullOrWhiteSpace(source.ReputationName) ? string.Empty : $"（{source.ReputationName}）"),
        _ => "来源未解析"
    };

    private static string FormatInstalledSource(AttachmentAcquisitionSource source)
    {
        var itemName = Fallback(source.HostItemName, "未命名装备");
        if (source.Location?.Kind == ItemSourceKind.Equipped)
        {
            var slot = source.HostSlot ??
                       (Enum.IsDefined(typeof(EquipmentSlot), source.Location.ContainerIndex)
                           ? (EquipmentSlot)source.Location.ContainerIndex
                           : (EquipmentSlot?)null);
            return slot is null
                ? $"已镶在当前穿戴：「{itemName}」"
                : $"已镶在当前穿戴：{SlotName(slot.Value)}「{itemName}」";
        }
        if (source.Location?.Kind == ItemSourceKind.AlternateWeaponSet)
            return $"已镶在「{itemName}」—{source.Location.DisplayName}";
        return source.Location is null
            ? $"已镶在「{itemName}」（位置未知）"
            : $"已镶在「{itemName}」—{source.Location.DisplayName}";
    }

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SlotName(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "头盔", EquipmentSlot.Shoulders => "护肩", EquipmentSlot.Chest => "胸甲",
        EquipmentSlot.Hands => "手套", EquipmentSlot.Legs => "护腿", EquipmentSlot.Feet => "靴子",
        EquipmentSlot.Waist => "腰带", EquipmentSlot.Amulet => "项链", EquipmentSlot.Medal => "勋章",
        EquipmentSlot.Relic => "遗物", EquipmentSlot.RingLeft => "戒指 I", EquipmentSlot.RingRight => "戒指 II",
        EquipmentSlot.MainHand => "主手", EquipmentSlot.OffHand => "副手", _ => slot.ToString()
    };
}

public sealed record SlotResultViewModel(
    string Slot,
    string CurrentItem,
    string CandidateItem,
    string Component,
    string Augment,
    string Source,
    bool Changed);
