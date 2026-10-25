using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class SourceHistoryStoreTests
{
    [Fact]
    public async Task RoundTripRetainsOnlyLastSelectionAndDropsRemovedHistory()
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

            Assert.Equal([JsonSourceHistoryStore.NormalizePath(gameA)], loaded.GameDirectories);
            Assert.Equal(JsonSourceHistoryStore.NormalizePath(gameA), loaded.LastGameDirectory);
            Assert.Equal(JsonSourceHistoryStore.NormalizePath(saveA), loaded.LastSaveDirectory);
            Assert.Equal([JsonSourceHistoryStore.NormalizePath(saveA)], loaded.SaveDirectories);
            Assert.Empty(loaded.RemovedGameDirectories);
            Assert.Empty(loaded.RemovedSaveDirectories);
            Assert.Equal(2, loaded.SchemaVersion);
            Assert.DoesNotContain("GameB", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldHistoryIsCompactedOnDiskUsingSelectedPathOrFirstFallback(bool hasSelection)
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source-history.json");
            var firstGame = Path.Combine(root, "first-game");
            var firstSave = Path.Combine(root, "first-save");
            // The selected path need not occur in the old array or still exist.
            var selectedGame = Path.Combine(root, "selected-game");
            var selectedSave = Path.Combine(root, "selected-save");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new SourceHistorySettings
            {
                SchemaVersion = 1,
                GameDirectories = [firstGame, Path.Combine(root, "discard-game")],
                SaveDirectories = [firstSave, Path.Combine(root, "discard-save")],
                LastGameDirectory = hasSelection ? selectedGame : "",
                LastSaveDirectory = hasSelection ? selectedSave : "",
                RemovedGameDirectories = [selectedGame],
                RemovedSaveDirectories = [selectedSave]
            }));
            var loaded = await new JsonSourceHistoryStore(path).LoadAsync();
            Assert.Equal([hasSelection ? selectedGame : firstGame], loaded.GameDirectories);
            Assert.Equal([hasSelection ? selectedSave : firstSave], loaded.SaveDirectories);
            var persisted = System.Text.Json.JsonSerializer.Deserialize<SourceHistorySettings>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(2, persisted.SchemaVersion);
            Assert.Equal(loaded.GameDirectories, persisted.GameDirectories);
            Assert.Equal(loaded.SaveDirectories, persisted.SaveDirectories);
            Assert.Empty(persisted.RemovedGameDirectories);
            Assert.Empty(persisted.RemovedSaveDirectories);
            Assert.DoesNotContain("discard-", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BlockedMigrationStillLoadsTheSelectedPathWithoutReplacingTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source-history.json");
            var selected = Path.Combine(root, "selected");
            var json = System.Text.Json.JsonSerializer.Serialize(new SourceHistorySettings
            {
                SchemaVersion = 1,
                GameDirectories = [Path.Combine(root, "old"), selected],
                LastGameDirectory = selected
            });
            await File.WriteAllTextAsync(path, json);
            // Windows permits reading this file but not the migration's atomic
            // replacement. The readable preference remains usable in memory.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var loaded = await new JsonSourceHistoryStore(path).LoadAsync();
                Assert.Equal([selected], loaded.GameDirectories);
                Assert.Equal(selected, loaded.LastGameDirectory);
                Assert.Equal(json, await File.ReadAllTextAsync(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancelledSaveCannotReplacePreviousSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "gd-optimizer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source-history.json");
            var store = new JsonSourceHistoryStore(path);
            var original = Path.Combine(root, "original");
            await store.SaveAsync(new SourceHistorySettings { LastGameDirectory = original });
            var bytes = await File.ReadAllBytesAsync(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
                new SourceHistorySettings { LastGameDirectory = Path.Combine(root, "cancelled") }, cancellation.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(original, (await store.LoadAsync()).LastGameDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
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
