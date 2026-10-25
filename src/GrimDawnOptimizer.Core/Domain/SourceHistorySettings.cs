namespace GrimDawnOptimizer.Core.Domain;

public sealed class SourceHistorySettings
{
    public int SchemaVersion { get; set; } = 2;
    // Retain collection properties for shared view-model consumers; each contains at most
    // the latest selected path. Version 1's multi-entry histories are migrated.
    public List<string> GameDirectories { get; set; } = [];
    public List<string> SaveDirectories { get; set; } = [];
    public List<string> RemovedGameDirectories { get; set; } = [];
    public List<string> RemovedSaveDirectories { get; set; } = [];
    public string LastGameDirectory { get; set; } = string.Empty;
    public string LastSaveDirectory { get; set; } = string.Empty;
}
