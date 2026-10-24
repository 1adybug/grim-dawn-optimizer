using System.Globalization;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.App.ViewModels;

internal static class PanelDpsDisplay
{
    // The character sheet rounds positive DPS at .5; keep the simulation's raw
    // value intact and apply this only at the display/comparison boundary.
    internal static double Round(double value) => Math.Floor(value + 0.5);
    internal static string Format(double value) => double.IsPositiveInfinity(value) ? "∞" : Round(value).ToString("N0");
}

public sealed class PanelStatInputViewModel(PanelStatField field, Action changed) : ObservableObject
{
    private string _input = string.Empty;
    public PanelStatField Field => field;
    public string Key => field.Key;
    public string Label => field.Label;
    public string Hint => field.Hint;
    public string Input
    {
        get => _input;
        set
        {
            if (!SetProperty(ref _input, value)) return;
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(IsInvalid));
            changed();
        }
    }
    public double? Value => TryValue(Input, out var value) ? value : null;
    public bool IsInvalid => !string.IsNullOrWhiteSpace(Input) &&
        (Value is not { } value || value < field.Minimum || value > field.Maximum);
    public string Error => IsInvalid ? $"请输入 {field.Minimum:N0}～{field.Maximum:N0} 的数值，或留空。" : string.Empty;
    internal static bool TryValue(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
}

public sealed class SwapComparisonViewModel
{
    public SwapComparisonViewModel(ItemSwapComparison comparison)
    {
        Title = $"{ResultViewModel.SlotName(comparison.Slot)} · {comparison.ItemName}";
        Explanation = comparison.Explanation;
        if (!comparison.IsValid) { Rows = []; return; }
        var fields = new[] { new PanelStatField("dps", "主攻面板 DPS", "", 0, 10000000) }
            .Concat(PanelStatCalibration.Fields);
        Rows = fields.Select(field => new SwapMetricViewModel(field,
            comparison.Before.GetValueOrDefault(field.Key), comparison.After.GetValueOrDefault(field.Key))).ToArray();
        var losses = Rows.Where(x => x.Delta < -0.05 &&
            (x.Key == "dps" || x.Key.EndsWith("Resistance", StringComparison.Ordinal) && !x.Key.EndsWith("MaxResistance", StringComparison.Ordinal)))
            .Select(x => $"{x.Label} {x.Change}").ToArray();
        Explanation += losses.Length > 0 ? " 单换预计下降：" + string.Join("；", losses) + "。" : " 单换预计没有 DPS 或抗性下降。";
    }
    public string Title { get; }
    public string Explanation { get; }
    public IReadOnlyList<SwapMetricViewModel> Rows { get; }
}

public sealed class SwapMetricViewModel(PanelStatField field, double before, double after) : ObservableObject
{
    private string _actualInput = string.Empty;
    private bool IsPanelDps => field.Key == "dps";
    private double DisplayBefore => IsPanelDps ? PanelDpsDisplay.Round(before) : before;
    private double DisplayAfter => IsPanelDps ? PanelDpsDisplay.Round(after) : after;
    private string ChangeFormat => IsPanelDps ? "+0;-0;0" : "+0.0;-0.0;0.0";
    public string Key => field.Key;
    public string Label => field.Label.Replace("（含超抗）", "（面板）");
    public string Before => IsPanelDps ? PanelDpsDisplay.Format(before) : before.ToString("N1");
    public string Predicted => IsPanelDps ? PanelDpsDisplay.Format(after) : after.ToString("N1");
    public double Delta => DisplayAfter - DisplayBefore;
    public string Change => Delta.ToString(ChangeFormat);
    public string ActualInput
    {
        get => _actualInput;
        set
        {
            if (!SetProperty(ref _actualInput, value)) return;
            OnPropertyChanged(nameof(Deviation));
        }
    }
    public string Deviation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ActualInput)) return "—";
            if (!PanelStatInputViewModel.TryValue(ActualInput, out var actual) || actual < field.Minimum || actual > field.Maximum)
                return "数值无效";
            if (IsPanelDps) actual = PanelDpsDisplay.Round(actual);
            var actualChange = actual - DisplayBefore;
            var mismatch = Math.Abs(Delta) > 0.1 && Math.Abs(actualChange) > 0.1 && Math.Sign(Delta) != Math.Sign(actualChange);
            return (actual - DisplayAfter).ToString(ChangeFormat) + (mismatch ? " · 增减方向不符" : "");
        }
    }
}
