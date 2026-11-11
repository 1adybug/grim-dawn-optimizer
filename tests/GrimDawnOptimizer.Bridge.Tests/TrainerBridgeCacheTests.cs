using System.Runtime.InteropServices;
using GrimDawnOptimizer.Bridge.Trainer;
using Xunit;

namespace GrimDawnOptimizer.Bridge.Tests;

public sealed class TrainerBridgeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gdo-trainer-cache-{Guid.NewGuid():N}");
    private string Cache => Path.Combine(_root, "cache");
    private string Bundled => Path.Combine(_root, "installation", "CairnBridge.v7.x64.dll");

    public TrainerBridgeCacheTests() => Directory.CreateDirectory(Path.GetDirectoryName(Bundled)!);

    [Fact]
    public async Task BundledDllCanBeReplacedWhileCachedCopyIsLoaded()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Bundled);
        var cached = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
        var original = await File.ReadAllBytesAsync(Bundled);
        var module = NativeLibrary.Load(cached);
        try
        {
            // Reproduce the Windows image lock without attaching to the game.
            Assert.Throws<IOException>(() => File.Open(cached, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            Assert.Equal(cached, await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache));
            await File.WriteAllBytesAsync(Bundled, [1, 2, 3]);
            var updated = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
            Assert.NotEqual(cached, updated);
            Assert.Equal(original, await File.ReadAllBytesAsync(cached));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(updated));
            var error = Assert.Throws<InvalidOperationException>(() =>
                TrainerBridgeCache.RequireMatchingLoadedBridge(cached, Bundled));
            Assert.Contains("重启游戏", error.Message);
        }
        finally { NativeLibrary.Free(module); }
    }

    [Fact]
    public async Task CorruptedCacheIsRepairedAndUnchangedLoadedBytesAreAccepted()
    {
        await File.WriteAllBytesAsync(Bundled, [4, 5, 6]);
        var cached = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
        await File.WriteAllBytesAsync(cached, [0]);
        Assert.Equal(cached, await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache));
        TrainerBridgeCache.RequireMatchingLoadedBridge(cached, Bundled);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(cached));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cached)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPreparationsPublishOneCompleteDll(bool corrupted)
    {
        // Fresh hashes exercise publication/repair rather than merely reusing
        // the valid file from the first iteration.
        for (var round = 0; round < 10; round++)
        {
            var bytes = new byte[512 * 1024];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(Bundled, bytes);
            if (corrupted)
            {
                var cached = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
                await File.WriteAllBytesAsync(cached, [0]);
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => TrainerBridgeCache.PrepareAsync(Bundled, default, Cache)));
            Assert.Single(results.Distinct());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(results[0]));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(results[0])!));
        }
    }

    [Fact]
    public async Task ConcurrentPreparationsReuseLoadedDll()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Bundled);
        var cached = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
        var expected = await File.ReadAllBytesAsync(Bundled);
        var module = NativeLibrary.Load(cached);
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => TrainerBridgeCache.PrepareAsync(Bundled, default, Cache)));
            Assert.All(results, result => Assert.Equal(cached, result));
            Assert.Equal(expected, await File.ReadAllBytesAsync(cached));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cached)!));
        }
        finally { NativeLibrary.Free(module); }
    }

    [Fact]
    public async Task LockedCorruptedCacheReportsFailureAndCleansTemporaryFiles()
    {
        await File.WriteAllBytesAsync(Bundled, [4, 5, 6]);
        var cached = await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache);
        await File.WriteAllBytesAsync(cached, [0]);
        using (File.Open(cached, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => TrainerBridgeCache.PrepareAsync(Bundled, default, Cache));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file access failure, got {error}");
            Assert.Equal(new byte[] { 0 }, await File.ReadAllBytesAsync(cached));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cached)!));
        }

        // Once the lock is gone, the same entry must still be repairable.
        Assert.Equal(cached, await TrainerBridgeCache.PrepareAsync(Bundled, default, Cache));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(cached));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
