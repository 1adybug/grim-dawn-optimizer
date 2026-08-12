using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.App.ViewModels;

public sealed class ResultViewModel
{
    public ResultViewModel(LoadoutResult result, CharacterSnapshot character)
    {
        Model = result;
        var currentItems = character.CurrentLoadout.ToDictionary(
            x => x.Key,
            x => character.Items.FirstOrDefault(i => i.Id == x.Value)?.Name ?? "—");
        SlotRows = result.Loadout.Slots.OrderBy(x => SlotOrder(x.Key)).Select(x => new SlotResultViewModel(
            SlotName(x.Key),
            currentItems.GetValueOrDefault(x.Key, "—"),
            x.Value.Item.Name,
            string.Join(" · ", new[] { x.Value.Component?.Name, x.Value.Augment?.Name }.Where(v => !string.IsNullOrWhiteSpace(v))),
            x.Value.Item.Source.DisplayName,
            currentItems.GetValueOrDefault(x.Key) != x.Value.Item.Name)).ToArray();
        ConstraintRows = result.ConstraintViolations.Select(x => $"{x.Key} 缺口 {x.Value:0.##} 步").ToArray();
        WarningRows = result.Loadout.UnsupportedEffects.Select(x => $"{x.Description} · {x.Record}")
            .Concat(result.Loadout.ValidationErrors).ToArray();
    }

    public LoadoutResult Model { get; }
    public int Rank => Model.Rank;
    public string Title => $"候选 {Rank:00}";
    public string Dps => FormatNumber(Model.Simulation.ExpectedDps);
    public string EffectiveHealth => FormatNumber(Model.Simulation.EffectiveHealth);
    public string TimeToDeath => double.IsPositiveInfinity(Model.Simulation.TimeToDeathSeconds) ? "∞" : $"{Model.Simulation.TimeToDeathSeconds:0.0}s";
    public string Confidence => Model.Confidence switch { ConfidenceLevel.High => "高置信度", ConfidenceLevel.Medium => "中置信度", _ => "低置信度" };
    public string Feasibility => Model.IsFeasible ? "满足全部约束" : $"{Model.ConstraintViolations.Count} 项约束未满足";
    public string MonteCarloRange => Model.Simulation.MonteCarloDpsHigh95 > 0
        ? $"95% · {FormatNumber(Model.Simulation.MonteCarloDpsLow95)} – {FormatNumber(Model.Simulation.MonteCarloDpsHigh95)}"
        : "等待抽样复核";
    public string TierScores => string.Join("  /  ", Model.TierScores.Select((x, i) => $"层 {i + 1}: {x:+0.##;-0.##;0}"));
    public IReadOnlyList<SlotResultViewModel> SlotRows { get; }
    public IReadOnlyList<string> ConstraintRows { get; }
    public IReadOnlyList<string> WarningRows { get; }

    private static string FormatNumber(double value) => double.IsPositiveInfinity(value) ? "∞" : value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.00}m",
        >= 1_000 => $"{value / 1_000:0.0}k",
        _ => $"{value:0}"
    };

    private static int SlotOrder(EquipmentSlot slot) => Array.IndexOf(new[]
    {
        EquipmentSlot.Head, EquipmentSlot.Shoulders, EquipmentSlot.Chest, EquipmentSlot.Hands,
        EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Waist, EquipmentSlot.Amulet,
        EquipmentSlot.Medal, EquipmentSlot.Relic, EquipmentSlot.RingLeft, EquipmentSlot.RingRight,
        EquipmentSlot.MainHand, EquipmentSlot.OffHand
    }, slot);

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
