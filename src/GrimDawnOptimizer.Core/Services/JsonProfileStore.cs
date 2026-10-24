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
            if (profiles is not { Count: > 0 }) return [new OptimizationProfile()];
            var supported = profiles.Where(x => x.SchemaVersion is >= 1 and <= 7).Select(Migrate).ToArray();
            return supported.Length > 0 ? supported : [new OptimizationProfile()];
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

    private static OptimizationProfile Migrate(OptimizationProfile profile)
    {
        var legacyNarrow = profile.SchemaVersion < 4 && IsLegacyNarrowIncoming(profile.Scenario.IncomingDamage);
        if (legacyNarrow)
            profile.Scenario.IncomingDamage = new IncomingDamageProfile();
        else if (profile.SchemaVersion < 6)
            profile.Scenario.IncomingDamage = IsLegacyBalancedIncoming(profile.Scenario.IncomingDamage)
                ? new IncomingDamageProfile()
                : profile.Scenario.IncomingDamage with { BleedingPerHit = 0 };
        profile.Metrics = SupportedMetricCatalog.Merge(profile.Metrics);
        profile.SchemaVersion = 7;
        return profile;
    }

    private static bool IsLegacyBalancedIncoming(IncomingDamageProfile incoming) =>
        Math.Abs(incoming.HitsPerSecond - 1.2) < 1e-9 &&
        Math.Abs(incoming.PhysicalPerHit - 750) < 1e-9 &&
        new[]
        {
            incoming.PiercePerHit, incoming.FirePerHit, incoming.ColdPerHit,
            incoming.LightningPerHit, incoming.AcidPerHit, incoming.VitalityPerHit,
            incoming.AetherPerHit, incoming.ChaosPerHit
        }.All(x => Math.Abs(x - 112.5) < 1e-9);

    private static bool IsLegacyNarrowIncoming(IncomingDamageProfile incoming) =>
        Math.Abs(incoming.HitsPerSecond - 1.2) < 1e-9 &&
        Math.Abs(incoming.PhysicalPerHit - 1200) < 1e-9 &&
        Math.Abs(incoming.FirePerHit - 450) < 1e-9 &&
        Math.Abs(incoming.ColdPerHit) < 1e-9 &&
        Math.Abs(incoming.LightningPerHit) < 1e-9 &&
        Math.Abs(incoming.AcidPerHit) < 1e-9 &&
        Math.Abs(incoming.PiercePerHit) < 1e-9 &&
        Math.Abs(incoming.VitalityPerHit) < 1e-9 &&
        Math.Abs(incoming.AetherPerHit) < 1e-9 &&
        Math.Abs(incoming.ChaosPerHit) < 1e-9;
}
