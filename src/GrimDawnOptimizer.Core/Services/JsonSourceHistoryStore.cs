using System.Text.Json;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public sealed class JsonSourceHistoryStore : ISourceHistoryStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSourceHistoryStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrimDawnOptimizer", "source-history.json");

    public async Task<SourceHistorySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new SourceHistorySettings();
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<SourceHistorySettings>(stream, _options, cancellationToken);
            return settings is { SchemaVersion: 1 } ? Normalize(settings) : new SourceHistorySettings();
        }
        catch (JsonException)
        {
            return new SourceHistorySettings();
        }
        catch (InvalidOperationException)
        {
            return new SourceHistorySettings();
        }
    }

    public async Task SaveAsync(SourceHistorySettings settings, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, Normalize(settings), _options, cancellationToken);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static SourceHistorySettings Normalize(SourceHistorySettings settings)
    {
        settings.GameDirectories = NormalizePaths(settings.GameDirectories);
        settings.SaveDirectories = NormalizePaths(settings.SaveDirectories);
        settings.RemovedGameDirectories = NormalizePaths(settings.RemovedGameDirectories);
        settings.RemovedSaveDirectories = NormalizePaths(settings.RemovedSaveDirectories);
        settings.LastGameDirectory = NormalizePath(settings.LastGameDirectory);
        settings.LastSaveDirectory = NormalizePath(settings.LastSaveDirectory);
        settings.SchemaVersion = 1;
        return settings;
    }

    private static List<string> NormalizePaths(IEnumerable<string>? paths) =>
        (paths ?? []).Select(NormalizePath).Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
