using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Parsing;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class LegacyWeaponBaseDamageTests
{
    [Theory]
    [InlineData(52, 70, 10, 15, 1778881840u)]
    [InlineData(20, 40, 100, 150, 1u)]
    [InlineData(120, 180, 2, 8, 42u)]
    public void SeededBonusPhysicalDoesNotAlsoBecomeFixedWeaponBaseDamage(
        double minimum, double maximum, double bonusMin, double bonusMax, uint seed)
    {
        var record = Record(minimum, maximum, bonusMin, bonusMax);

        var result = SeededItemStatResolver.Resolve(record, null, null, seed);

        Assert.True(result.IsExact);
        Assert.Equal((minimum + maximum) / 2, result.Stats[StatKeys.WeaponDamage("physical")]);
        Assert.True(result.Stats[StatKeys.DamageFlat("physical")] > 0);
    }

    [Fact]
    public void FallbackKeepsWeaponBaseFixedAndScalesOnlyTheSeparatePhysicalBonus()
    {
        var record = Record(52, 70, 10, 15);
        var catalog = new GameDataCatalog
        {
            Records = new Dictionary<string, GameRecord> { [record.RecordName] = record }
        };

        var item = SaveReader.ConvertItem(new RawItem { BaseRecord = record.RecordName, Seed = 0 },
            "fixture.gdc", catalog, []);

        Assert.NotNull(item);
        Assert.False(item.SeedStatsResolved);
        Assert.Equal(61, item.Stats[StatKeys.WeaponDamage("physical")]);
        Assert.Equal(16.25, item.Stats[StatKeys.DamageFlat("physical")]);
    }

    internal static GameRecord Record(double minimum, double maximum, double bonusMin, double bonusMax) => new()
    {
        RecordName = "records/items/test-gun.dbr",
        RecordType = "WeaponHunting_Ranged1h",
        NumericValues = new StatBlock(new Dictionary<string, double>
        {
            ["attributeScalePercent"] = 30,
            ["offensivePhysicalMin"] = minimum,
            ["offensivePhysicalMax"] = maximum,
            ["offensiveBonusPhysicalMin"] = bonusMin,
            ["offensiveBonusPhysicalMax"] = bonusMax
        }),
        TextValues = new Dictionary<string, IReadOnlyList<string>> { ["Class"] = ["WeaponHunting_Ranged1h"] }
    };
}
