using Strata.Effects;

namespace Strata.Tests;

/// <summary>The liquid-glass rim's parameter mapping: pure, so the tuning is pinned without rendering.</summary>
[Trait("Category", "Unit")]
public class LiquidGlassTests
{
    [Theory]
    [InlineData(48, 9.6, 15.84)] // the default dock height: a fifth and a third of the bar
    [InlineData(20, 6.0, 10.0)]  // a tiny dock stops at the floors instead of vanishing
    [InlineData(100, 14.0, 24.0)] // a tall dock stops at the source's pill values instead of caricature
    public void The_rim_scales_with_the_dock_but_stays_glass(double dockHeight, double height, double amount)
    {
        var p = LiquidGlassParameters.For(dockHeight, 40);
        Assert.Equal(height, p.RefractionHeight, 3);
        Assert.Equal(amount, p.RefractionAmount, 3);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(39, false)]
    [InlineData(40, true)]
    [InlineData(100, true)]
    public void The_chromatic_fringe_needs_body_behind_the_glass(int opacity, bool dispersion) =>
        Assert.Equal(dispersion, LiquidGlassParameters.For(48, opacity).Dispersion);

    [Fact]
    public void The_highlight_grows_from_airy_glass_to_the_sources_dense_value()
    {
        Assert.Equal(0.22, LiquidGlassParameters.For(48, 0).HighlightAlpha, 3);
        Assert.Equal(0.38, LiquidGlassParameters.For(48, 100).HighlightAlpha, 3);
    }

    [Fact]
    public void The_light_always_sits_above_the_dock()
    {
        // In WPF's y-down space the edge normal facing a light above is (0,-1): angle -pi/2, at every opacity.
        Assert.Equal(-Math.PI / 2, LiquidGlassParameters.For(48, 0).LightAngleRadians, 6);
        Assert.Equal(-Math.PI / 2, LiquidGlassParameters.For(48, 100).LightAngleRadians, 6);
        Assert.Equal(2.5, LiquidGlassParameters.For(48, 50).Falloff, 3);
        Assert.Equal(0.14, LiquidGlassParameters.For(48, 50).AmbientAlpha, 3);
    }
}
