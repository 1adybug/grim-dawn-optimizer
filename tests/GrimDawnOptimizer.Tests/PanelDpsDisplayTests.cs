using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Domain;
using GrimDawnOptimizer.Core.Services;

namespace GrimDawnOptimizer.Tests;

public sealed class PanelDpsDisplayTests
{
    [Theory]
    [InlineData(23478.3, 23478)]
    [InlineData(23478.4999, 23478)]
    [InlineData(23478.5, 23479)]
    public void PanelDpsMatchesTheGamesIntegerDisplay(double raw, double displayed)
    {
        var row = Dps(raw, raw);

        Assert.Equal(displayed.ToString("N0"), row.Before);
        Assert.Equal(displayed.ToString("N0"), row.Predicted);
        row.ActualInput = displayed.ToString();
        Assert.Equal("0", row.Deviation);
    }

    [Fact]
    public void ChangeAndMeasuredDeviationCompareDisplayedIntegers()
    {
        var row = Dps(100.49, 100.5);

        Assert.Equal(1, row.Delta);
        Assert.Equal("+1", row.Change);
        row.ActualInput = "99";
        Assert.Equal("-2 · 增减方向不符", row.Deviation);

        var invisibleChange = Dps(100.49, 100.4);
        invisibleChange.ActualInput = "100";
        Assert.Equal("0", invisibleChange.Change);
        Assert.Equal("0", invisibleChange.Deviation);
    }

    [Fact]
    public void OtherMetricsRetainTheirExistingPrecision()
    {
        var row = new SwapMetricViewModel(new PanelStatField("health", "生命", "", 0, 10000000), 100.2, 101.4);
        row.ActualInput = 102d.ToString();

        Assert.Equal(100.2.ToString("N1"), row.Before);
        Assert.Equal(101.4.ToString("N1"), row.Predicted);
        Assert.Equal(1.2, row.Delta, 8);
        Assert.Equal(1.2.ToString("+0.0;-0.0;0.0"), row.Change);
        Assert.Equal(0.6.ToString("+0.0;-0.0;0.0"), row.Deviation);
    }

    private static SwapMetricViewModel Dps(double before, double after) =>
        new(new PanelStatField("dps", "主攻面板 DPS", "", 0, 10000000), before, after);
}
