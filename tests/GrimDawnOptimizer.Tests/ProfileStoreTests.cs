using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public async Task ProfileRoundTripsWithStatBlocksAndSchemaVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            var profile = new OptimizationProfile { Name = "火焰测试", SearchBudgetSeconds = 7 };
            profile.Rotation.FlatDamage.Add(StatKeys.DamageFlat("fire"), 42);
            var store = new JsonProfileStore(path);

            await store.SaveAsync([profile]);
            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            Assert.Equal(1, loaded[0].SchemaVersion);
            Assert.Equal("火焰测试", loaded[0].Name);
            Assert.Equal(42, loaded[0].Rotation.FlatDamage[StatKeys.DamageFlat("fire")]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidJsonFallsBackToDefaultWithoutOverwritingInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "profiles.json");
            await File.WriteAllTextAsync(path, "{ invalid");
            var original = await File.ReadAllBytesAsync(path);

            var loaded = await new JsonProfileStore(path).LoadAsync();

            Assert.Single(loaded);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
