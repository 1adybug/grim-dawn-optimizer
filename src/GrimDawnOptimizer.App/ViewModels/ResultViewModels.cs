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
                    string.Join(" · ", new[] { variant.Component?.Name, variant.Augment?.Name }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    variant.Item.Source.DisplayName, currentItem != variant.Item.Name);

            var candidate = slot == EquipmentSlot.OffHand && usesTwoHands ? "由双手武器占用" : "—（无可用装备）";
            return new SlotResultViewModel(SlotName(slot), currentItem, candidate, string.Empty, "—", currentItem != "—");
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

    private static string FormatNumber(double value) => double.IsPositiveInfinity(value) ? "∞" : value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.00}m",
        >= 1_000 => $"{value / 1_000:0.0}k",
        _ => $"{value:0}"
    };

    private static string SlotName(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "头盔", EquipmentSlot.Shoulders => "护肩", EquipmentSlot.Chest => "胸甲",
        EquipmentSlot.Hands => "手套", EquipmentSlot.Legs => "护腿", EquipmentSlot.Feet => "靴子",
        EquipmentSlot.Waist => "腰带", EquipmentSlot.Amulet => "项链", EquipmentSlot.Medal => "勋章",
        EquipmentSlot.Relic => "遗物", EquipmentSlot.RingLeft => "戒指 I", EquipmentSlot.RingRight => "戒指 II",
        EquipmentSlot.MainHand => "主手", EquipmentSlot.OffHand => "副手", _ => slot.ToString()
    };
}

public sealed record SlotResultViewModel(string Slot, string CurrentItem, string CandidateItem, string Attachments, string Source, bool Changed);
