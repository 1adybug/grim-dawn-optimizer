using System.Security.Cryptography;

namespace GrimDawnOptimizer.Bridge.Trainer;

internal static class TrainerBridgeCache
{
    // The game keeps the DLL mapped after disconnect. Never inject a file that
    // the installer or portable launcher may need to replace.
    internal static async Task<string> PrepareAsync(string bundledDll, CancellationToken cancellationToken,
        string? cacheDirectory = null)
    {
        var bytes = await File.ReadAllBytesAsync(bundledDll, cancellationToken);
        var hash = SHA256.HashData(bytes);
        cacheDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrimDawnOptimizer", "trainer-native");
        var directory = Path.Combine(cacheDirectory, Convert.ToHexString(hash).ToLowerInvariant());
        var cachedDll = Path.Combine(directory, Path.GetFileName(bundledDll));
        Directory.CreateDirectory(directory);
        if (Matches(cachedDll, hash)) return cachedDll;

        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, cachedDll, overwrite: true); }
            // Another instance may have published (and loaded) the same bytes.
            catch (IOException) when (Matches(cachedDll, hash)) { }
        }
        finally { File.Delete(temporary); }
        return cachedDll;
    }

    internal static void RequireMatchingLoadedBridge(string loadedDll, string bundledDll)
    {
        using var loaded = File.OpenRead(loadedDll);
        using var bundled = File.OpenRead(bundledDll);
        if (!SHA256.HashData(loaded).AsSpan().SequenceEqual(SHA256.HashData(bundled)))
            throw new InvalidOperationException("游戏中仍有旧版组件，请保存并重启游戏后再连接新版工具。");
    }

    private static bool Matches(string path, byte[] hash)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return SHA256.HashData(file).AsSpan().SequenceEqual(hash);
        }
        catch (FileNotFoundException) { return false; }
    }
}
