namespace GrimDawnOptimizer.App.ViewModels;

public sealed class ItemBuffStateViewModel(string record, string name, string description, Action changed) : ObservableObject
{
    public string Record { get; } = record;
    public string Name { get; } = name;
    public string Description { get; } = description;
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (SetProperty(ref _isActive, value)) changed(); }
    }
}
