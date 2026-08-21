using System.Text.Json;
using GrimDawnOptimizer.App.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class UiPreferencesStoreTests
{
    [Fact]
    public async Task MissingOrCorruptFileFallsBackToSystem()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "ui-settings.json");
            var store = new UiPreferencesStore(path);

            Assert.Equal(ThemePreference.System, (await store.LoadAsync()).Theme);

            await File.WriteAllTextAsync(path, "{not-json");
            Assert.Equal(ThemePreference.System, (await store.LoadAsync()).Theme);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTripUsesVersionedCamelCaseJsonAndLeavesNoTempFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "ui-settings.json");
            var store = new UiPreferencesStore(path);

            await store.SaveAsync(new UiPreferences { Theme = ThemePreference.Dark });

            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(ThemePreference.Dark, (await store.LoadAsync()).Theme);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "grim-dawn-ui-preferences-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
