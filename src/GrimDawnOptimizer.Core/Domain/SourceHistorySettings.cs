namespace GrimDawnOptimizer.Core.Domain;

public sealed class SourceHistorySettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> GameDirectories { get; set; } = [];
    public List<string> SaveDirectories { get; set; } = [];
    public List<string> RemovedGameDirectories { get; set; } = [];
    public List<string> RemovedSaveDirectories { get; set; } = [];
    public string LastGameDirectory { get; set; } = string.Empty;
    public string LastSaveDirectory { get; set; } = string.Empty;
}
