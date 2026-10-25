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
            SourceHistorySettings? settings;
            await using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true))
                settings = await JsonSerializer.DeserializeAsync<SourceHistorySettings>(stream, _options, cancellationToken);
            if (settings is not { SchemaVersion: 1 or 2 }) return new SourceHistorySettings();
            var needsMigration = settings.SchemaVersion == 1 || settings.GameDirectories?.Count > 1 ||
                settings.SaveDirectories?.Count > 1 || settings.RemovedGameDirectories?.Count > 0 ||
                settings.RemovedSaveDirectories?.Count > 0;
            settings = Normalize(settings);
            if (needsMigration)
            {
                // Compact existing files too, rather than only hiding historical
                // entries in memory. A read-only file still supplies its selection.
                try { await SaveAsync(settings, cancellationToken); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return settings;
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
        settings.LastGameDirectory = LatestPath(settings.LastGameDirectory, settings.GameDirectories);
        settings.LastSaveDirectory = LatestPath(settings.LastSaveDirectory, settings.SaveDirectories);
        settings.GameDirectories = string.IsNullOrWhiteSpace(settings.LastGameDirectory) ? [] : [settings.LastGameDirectory];
        settings.SaveDirectories = string.IsNullOrWhiteSpace(settings.LastSaveDirectory) ? [] : [settings.LastSaveDirectory];
        // Deletion remains available to legacy clients for the current session;
        // no history of previously removed directories is retained on disk.
        settings.RemovedGameDirectories = [];
        settings.RemovedSaveDirectories = [];
        settings.SchemaVersion = 2;
        return settings;
    }

    private static string LatestPath(string? selected, IEnumerable<string>? legacyPaths)
    {
        var path = NormalizePath(selected);
        return !string.IsNullOrWhiteSpace(path) ? path :
            (legacyPaths ?? []).Select(NormalizePath).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

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
