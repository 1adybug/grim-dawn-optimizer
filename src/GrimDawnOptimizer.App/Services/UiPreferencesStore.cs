using System.IO;
using System.Text.Json;

namespace GrimDawnOptimizer.App.Services;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class UiPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
}

public sealed class UiPreferencesStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public UiPreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrimDawnOptimizer",
            "ui-settings.json");
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new UiPreferences();

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                useAsync: true);
            var preferences = await JsonSerializer.DeserializeAsync<UiPreferences>(stream, _options, cancellationToken);
            return preferences is { SchemaVersion: 1 } ? preferences : new UiPreferences();
        }
        catch (JsonException)
        {
            return new UiPreferences();
        }
        catch (InvalidOperationException)
        {
            return new UiPreferences();
        }
        catch (IOException)
        {
            return new UiPreferences();
        }
        catch (UnauthorizedAccessException)
        {
            return new UiPreferences();
        }
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(
                             temp,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             useAsync: true))
            {
                preferences.SchemaVersion = 1;
                await JsonSerializer.SerializeAsync(stream, preferences, _options, cancellationToken);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
