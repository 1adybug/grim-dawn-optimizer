using GrimDawnOptimizer.App.Services;

namespace GrimDawnOptimizer.App.ViewModels;

public enum WorkspaceSection
{
    Sources,
    Build,
    Search,
    Results
}

public sealed record ThemePreferenceOption(ThemePreference Value, string Label);

public sealed class ResistanceDebuffViewModel(
    string type, string label, Func<double> getReduction, Action<double> setReduction,
    Func<double> getPenalty) : ObservableObject
{
    public string Type { get; } = type;
    public string Label { get; } = label;
    public double Reduction { get => getReduction(); set { setReduction(Math.Clamp(value, 0, 100)); OnPropertyChanged(); } }
    public string Penalty => $"{getPenalty():0} 个百分点";
    public void Refresh() { OnPropertyChanged(nameof(Penalty)); OnPropertyChanged(nameof(Reduction)); }
}
