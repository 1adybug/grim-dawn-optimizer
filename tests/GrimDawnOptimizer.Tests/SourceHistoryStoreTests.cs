using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SourceHistoryStoreTests
{
    [Fact]
    public async Task RoundTripPreservesAllDirectoriesLastSelectionAndRemovedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source-history.json");
            var gameA = Path.Combine(root, "GameA");
            var gameB = Path.Combine(root, "GameB");
            var saveA = Path.Combine(root, "SaveA");
            var store = new JsonSourceHistoryStore(path);
            await store.SaveAsync(new SourceHistorySettings
            {
                GameDirectories = [gameA + Path.DirectorySeparatorChar, gameB, gameA],
                SaveDirectories = [saveA],
                RemovedGameDirectories = [gameB],
                LastGameDirectory = gameA + Path.DirectorySeparatorChar,
                LastSaveDirectory = saveA
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.GameDirectories.Count);
            Assert.Equal(JsonSourceHistoryStore.NormalizePath(gameA), loaded.LastGameDirectory);
            Assert.Equal(JsonSourceHistoryStore.NormalizePath(saveA), loaded.LastSaveDirectory);
            Assert.Contains(JsonSourceHistoryStore.NormalizePath(gameB), loaded.RemovedGameDirectories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidHistoryFallsBackWithoutOverwritingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source-history.json");
            await File.WriteAllTextAsync(path, "{ invalid");
            var original = await File.ReadAllBytesAsync(path);

            var loaded = await new JsonSourceHistoryStore(path).LoadAsync();

            Assert.Empty(loaded.GameDirectories);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
