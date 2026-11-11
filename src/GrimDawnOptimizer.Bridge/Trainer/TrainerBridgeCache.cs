using System.Security.Cryptography;
using System.Text;

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
        // Serialize both validation and publication across application instances.
        // Concurrent replacements can otherwise fail with ACCESS_DENIED while
        // the old corrupt target is still present, so verifying a winner only
        // in a catch filter is insufficient.
        var identity = Path.GetFullPath(cachedDll).ToUpperInvariant();
        var mutexName = @"Local\GrimDawnOptimizer.TrainerBridgeCache." +
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        using var publication = new Mutex(false, mutexName);
        try
        {
            if (WaitHandle.WaitAny([publication, cancellationToken.WaitHandle]) == 1)
                cancellationToken.ThrowIfCancellationRequested();
        }
        // An exited publisher may have left a temporary or corrupt entry. The
        // abandoned mutex is now owned by us; validate/repair the entry normally.
        catch (AbandonedMutexException) { }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(cachedDll, hash)) return cachedDll;

            var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                // A Mutex is thread-affine: keep this short publication section
                // synchronous, including release, with no await while it is held.
                File.WriteAllBytes(temporary, bytes);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, cachedDll, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return cachedDll;
        }
        finally { publication.ReleaseMutex(); }
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
