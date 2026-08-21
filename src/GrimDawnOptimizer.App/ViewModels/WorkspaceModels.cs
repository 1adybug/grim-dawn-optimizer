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
