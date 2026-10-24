using System.Text.Json;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PanelDpsGameGoldenTests
{
    [Theory]
    [InlineData("full-gear-fire-strike", 23478)]
    [InlineData("remove-left-ring-fire-strike", 21821)]
    [InlineData("full-gear-default-attack", 4374)]
    [InlineData("remove-right-ring-fire-strike", 22157)]
    [InlineData("remove-head-fire-strike", 19441)]
    public void UncalibratedPanelDpsMatchesIndependentlyObservedGameDisplay(string name, int observedDps)
    {
        using var stream = typeof(PanelDpsGameGoldenTests).Assembly.GetManifestResourceStream(
            "GrimDawnOptimizer.Tests.Fixtures.panel-dps-game-observations.json");
        Assert.NotNull(stream);
        var document = JsonSerializer.Deserialize<GoldenDocument>(stream)!;
        var sample = Assert.Single(document.Cases, x => x.Name == name);
        Assert.Equal(observedDps, sample.GamePanelDps);
        var character = new CharacterSnapshot { Name = "panel-fixture", SaveFilePath = "fixture.gdc", Level = 64 };
        var loadout = new CalculatedLoadout
        {
            Slots = new Dictionary<EquipmentSlot, LoadoutVariant>(),
            Stats = new StatBlock(sample.Stats)
        };

        var result = new CombatSimulator().Simulate(character, loadout, new CombatScenario(), sample.Rotation);

        // No runtime calibration or target mitigation is used. The game rounds
        // this display to the nearest integer; its five UI observations are
        // independent assertions, including changes to stats and skill levels.
        Assert.Equal(observedDps, (int)Math.Floor(result.UnmitigatedDps + 0.5));
    }

    private sealed class GoldenDocument
    {
        public GoldenDocument() { }
        public List<GoldenSample> Cases { get; init; } = [];
    }

    private sealed class GoldenSample
    {
        public GoldenSample() { }
        public string Name { get; init; } = string.Empty;
        public int GamePanelDps { get; init; }
        public Dictionary<string, double> Stats { get; init; } = [];
        public SkillRotationDefinition Rotation { get; init; } = new();
    }
}
