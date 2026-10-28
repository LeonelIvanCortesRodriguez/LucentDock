using Strata.Core;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class MonitorWindowGeometryTests
{
    private static readonly PixelBounds LeftMonitor = new(-2560, -300, 0, 1140);

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    [InlineData(2.50)]
    public void PillPlacementUsesPerMonitorScaleAndNegativeDesktopCoordinates(double scale)
    {
        DockPlacement bottom = DockPlacementPlanner.Calculate(LeftMonitor, scale, 48, 800, 6,
            DockShape.Pill, DockEdge.Bottom);
        int widthPx = (int)Math.Round(800 * scale);
        int heightPx = (int)Math.Round(48 * scale);
        int gapPx = (int)Math.Round(6 * scale);

        Assert.Null(bottom.WidthDip);
        Assert.Equal(2560 / scale - 24, bottom.MaxWidthDip!.Value, 8);
        Assert.Equal(-2560 + (2560 - widthPx) / 2, bottom.X);
        Assert.Equal(1140 - heightPx - gapPx, bottom.Y);
        Assert.Equal(heightPx, bottom.HeightPx);

        DockPlacement top = DockPlacementPlanner.Calculate(LeftMonitor, scale, 48, 800, 6,
            DockShape.Pill, DockEdge.Top);
        Assert.Equal(-300 + gapPx, top.Y);
        Assert.Equal((int)Math.Ceiling(60 * scale),
            DockPlacementPlanner.AppBarHeight(48, 6, DockShape.Pill, scale));
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    [InlineData(2.50)]
    public void FullWidthDockUsesPhysicalMonitorWidthWithoutCrossMonitorDrift(double scale)
    {
        DockPlacement result = DockPlacementPlanner.Calculate(LeftMonitor, scale, 52, 123, 6,
            DockShape.FullWidth, DockEdge.Bottom);

        Assert.Equal(2560 / scale, result.WidthDip!.Value, 8);
        Assert.Null(result.MaxWidthDip);
        Assert.Equal(-2560, result.X);
        Assert.Equal(1140 - (int)Math.Round(52 * scale), result.Y);
        Assert.Equal((int)Math.Ceiling(52 * scale),
            DockPlacementPlanner.AppBarHeight(52, 6, DockShape.FullWidth, scale));
    }

    [Fact]
    public void InvalidScaleOrMonitorFailsClosedBeforeNativePlacement()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DockPlacementPlanner.Calculate(
            LeftMonitor, 0, 48, 800, 6, DockShape.Pill, DockEdge.Bottom));
        Assert.Throws<ArgumentOutOfRangeException>(() => DockPlacementPlanner.Calculate(
            new PixelBounds(0, 0, 0, 1080), 1, 48, 800, 6, DockShape.Pill, DockEdge.Bottom));
    }

    [Fact]
    public void PillAndFullWidthTransitionsResetBothWpfDimensionConstraints()
    {
        DockPlacement pill = DockPlacementPlanner.Calculate(LeftMonitor, 1.5, 48, 800, 6,
            DockShape.Pill, DockEdge.Bottom);
        DockPlacement full = DockPlacementPlanner.Calculate(LeftMonitor, 1.5, 48, 800, 6,
            DockShape.FullWidth, DockEdge.Bottom);

        DockDimensionContract pillDimensions = DockPlacementPlanner.Dimensions(pill);
        Assert.True(double.IsNaN(pillDimensions.WidthDip));
        Assert.True(double.IsFinite(pillDimensions.MaxWidthDip));

        DockDimensionContract fullDimensions = DockPlacementPlanner.Dimensions(full);
        Assert.Equal(2560 / 1.5, fullDimensions.WidthDip, 8);
        Assert.True(double.IsPositiveInfinity(fullDimensions.MaxWidthDip));
    }
}
