using System.Diagnostics;
using System.Text.RegularExpressions;
using GrimDawnOptimizer.Core.Domain;

namespace GrimDawnOptimizer.Core.Services;

/// <summary>Chooses a source from evidence about this run, without treating saved history as an explicit choice.</summary>
public static class GameSourceSelection
{
    public static string? ChooseRoot(
        IEnumerable<string> availableRoots,
        string? explicitlySelectedRoot,
        string? currentGameRoot,
        string? lastUsedRoot)
    {
        var roots = availableRoots.ToArray();
        foreach (var preferred in new[] { explicitlySelectedRoot, currentGameRoot, lastUsedRoot })
        {
            if (string.IsNullOrWhiteSpace(preferred)) continue;
            var match = roots.FirstOrDefault(root => PathEquals(root, preferred));
            if (match is not null) return match;
        }
        return roots.FirstOrDefault();
    }

    internal static IReadOnlyList<string> GetRunningInstallationRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("Grim Dawn"))
        {
            using (process)
            {
                try
                {
                    var root = FindInstallationRoot(process.MainModule?.FileName);
                    if (root is not null) roots.Add(root);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { } // The game may exit while being discovered.
            }
        }
        return roots.ToArray();
    }

    internal static string? FindInstallationRoot(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.GetFileName(executablePath).Equals("Grim Dawn.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (directory is null) return null;
        if (File.Exists(Path.Combine(directory, "database", "database.arz"))) return directory;
        // The 64-bit executable is distributed one level below the shared database.
        if (!Path.GetFileName(directory).Equals("x64", StringComparison.OrdinalIgnoreCase)) return null;
        var parent = Path.GetDirectoryName(directory);
        return parent is not null && File.Exists(Path.Combine(parent, "database", "database.arz")) ? parent : null;
    }

    internal static bool? ReadCloudSavingSetting(string optionsPath)
    {
        try
        {
            if (!File.Exists(optionsPath)) return null;
            var match = Regex.Match(File.ReadAllText(optionsPath),
                @"^\s*cloudSaving\s*=\s*(true|false)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? bool.Parse(match.Groups[1].Value) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string? FindPreferredSaveRoot(
        IReadOnlyList<SaveLocation> locations, bool? cloudSaving, string localSaveRoot)
    {
        if (cloudSaving == false)
            return locations.FirstOrDefault(x => PathEquals(x.RootPath, localSaveRoot))?.RootPath;
        if (cloudSaving != true) return null;

        // A Steam account cannot be inferred from whichever character happens to have
        // the latest timestamp. Keep the user's selection if several accounts exist.
        var cloudRoots = locations.Where(x => x.Source == "Steam Cloud").ToArray();
        return cloudRoots.Length == 1 ? cloudRoots[0].RootPath : null;
    }

    private static bool PathEquals(string left, string right) =>
        JsonSourceHistoryStore.NormalizePath(left).Equals(
            JsonSourceHistoryStore.NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}
