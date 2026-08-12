using System.Text.Json;
using GrimDawnOptimizer.Core.Contracts;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public JsonProfileStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrimDawnOptimizer", "profiles.json");

    public async Task<IReadOnlyList<OptimizationProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [new OptimizationProfile()];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
            var profiles = await JsonSerializer.DeserializeAsync<List<OptimizationProfile>>(stream, _options, cancellationToken);
            return profiles is { Count: > 0 } ? profiles.Where(x => x.SchemaVersion == 1).ToArray() : [new OptimizationProfile()];
        }
        catch (JsonException)
        {
            return [new OptimizationProfile()];
        }
        catch (InvalidOperationException)
        {
            return [new OptimizationProfile()];
        }
    }

    public async Task SaveAsync(IEnumerable<OptimizationProfile> profiles, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            await JsonSerializer.SerializeAsync(stream, profiles.ToArray(), _options, cancellationToken);
        File.Move(temp, _path, overwrite: true);
    }
}
