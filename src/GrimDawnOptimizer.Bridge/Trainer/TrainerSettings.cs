using System.Diagnostics;
using System.Text.Json;

namespace GrimDawnOptimizer.Bridge.Trainer;

/// <summary>Owns only the current application's trainer preference file.</summary>
public sealed class TrainerSettings
{
    private readonly string _storage;
    public string? Executable { get; private set; }
    public bool AutoConnect { get; private set; } = true;
    public int EnabledFeatures { get; private set; }
    public int MovementMultiplier { get; private set; } = 2;
    public string LifeMode { get; private set; } = "infinite";
    public string? LoadWarning { get; private set; }

    public TrainerSettings(string? storage = null, bool importLegacyDefault = true, string? legacyStorage = null)
    {
        _storage = storage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrimDawnOptimizer", "combat-settings.json");
        var source = File.Exists(_storage) ? _storage : importLegacyDefault
            ? legacyStorage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CairnCompanion", "settings.json")
            : null;
        if (source is null || !File.Exists(source)) return;
        try
        {
            var preference = JsonSerializer.Deserialize<Preference>(File.ReadAllText(source),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Executable = string.IsNullOrWhiteSpace(preference?.GameExecutable) ? null : preference.GameExecutable;
            // The old tool contributes only its path, never its runtime effects.
            if (source == _storage && preference is not null)
            {
                if (preference.SchemaVersion > 1) throw new JsonException("Unsupported combat settings version.");
                AutoConnect = preference.AutoConnect;
                EnabledFeatures = preference.EnabledFeatures & TrainerFeatures.Mask;
                MovementMultiplier = TrainerFeatures.MovementMultipliers.Contains(preference.MovementMultiplier)
                    ? preference.MovementMultiplier : 2;
                LifeMode = TrainerFeatures.LifeModes.Contains(preference.LifeMode) ? preference.LifeMode : "infinite";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Executable = null;
            LoadWarning = "实战工具设置未能读取，当前使用默认配置；请重新选择游戏程序。";
        }
    }

    public void SaveExecutable(string path)
    {
        var fullPath = Validate(path);
        Save(new(fullPath, AutoConnect, EnabledFeatures, MovementMultiplier, LifeMode: LifeMode));
    }

    public void SaveAutoConnect(bool enabled) => Save(new(Executable, enabled, EnabledFeatures, MovementMultiplier, LifeMode: LifeMode));
    public void SaveFeatures(int flags) => Save(new(Executable, AutoConnect, flags & TrainerFeatures.Mask, MovementMultiplier, LifeMode: LifeMode));
    public void SaveLifeMode(string value)
    {
        if (!TrainerFeatures.LifeModes.Contains(value)) throw new ArgumentException("生命模式只能为 infinite 或 lock。");
        Save(new(Executable, AutoConnect, EnabledFeatures, MovementMultiplier, LifeMode: value));
    }
    public void SaveMovementMultiplier(int value)
    {
        if (!TrainerFeatures.MovementMultipliers.Contains(value)) throw new ArgumentException("移动倍率只能为 2、4、8 或 16。");
        Save(new(Executable, AutoConnect, EnabledFeatures, value, LifeMode: LifeMode));
    }

    private void Save(Preference preference)
    {
        var directory = Path.GetDirectoryName(_storage)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"combat-settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preference,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, _storage, overwrite: true);
            Executable = preference.GameExecutable;
            AutoConnect = preference.AutoConnect;
            EnabledFeatures = preference.EnabledFeatures;
            MovementMultiplier = preference.MovementMultiplier;
            LifeMode = preference.LifeMode;
            LoadWarning = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请先选择 Grim Dawn.exe 的完整路径。");
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetFileName(fullPath).Equals("Grim Dawn.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择游戏目录中的 Grim Dawn.exe。");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("游戏程序不存在，请重新选择路径。");
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!File.Exists(Path.Combine(directory, "Game.dll")) || !File.Exists(Path.Combine(directory, "Engine.dll")))
            throw new ArgumentException("程序旁缺少 Game.dll 或 Engine.dll，请选择完整的游戏安装目录。");
        return fullPath;
    }

    public static string RootFor(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        return Path.GetFileName(directory).Equals("x64", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(directory)!.FullName : directory;
    }

    public static ProcessStartInfo LaunchInfo(string executable)
    {
        var path = Validate(executable);
        return new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = RootFor(path) };
    }

    private sealed record Preference(string? GameExecutable = null, bool AutoConnect = true,
        int EnabledFeatures = 0, int MovementMultiplier = 2, int SchemaVersion = 1, string LifeMode = "infinite");
}
