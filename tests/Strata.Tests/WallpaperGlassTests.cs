using System.Windows;
using Strata.Effects;
using static Strata.Effects.WallpaperMapping;

namespace Strata.Tests;

/// <summary>The wallpaper placement math behind the refraction layer: Windows' five modes, pinned.</summary>
[Trait("Category", "Unit")]
public class WallpaperGlassTests
{
    // A 3840x2160 monitor at the virtual origin; the dock strip sits at the bottom centre.
    private static readonly Rect Monitor = new(0, 0, 3840, 2160);
    private static readonly Rect Virtual = new(0, 0, 3840, 2160);
    private static readonly Rect DockStrip = new(720, 2080, 2400, 80);

    [Fact]
    public void Fill_with_a_matching_image_maps_proportionally()
    {
        var src = SourceRect(DockStrip, Monitor, Virtual, new Size(1920, 1080), Placement.Fill);
        Assert.Equal(360, src.X, 1); Assert.Equal(1040, src.Y, 1);
        Assert.Equal(1200, src.Width, 1); Assert.Equal(40, src.Height, 1);
    }

    [Fact]
    public void Fill_with_a_wider_image_crops_the_sides_centred()
    {
        // 4096x1080 over a 3840x2160 monitor: height dictates scale 2, so the displayed image is 8192 wide and
        // (8192-3840)/2 = 2176 screen px hang off each side; screen x=0 is image x = 2176/2 = 1088.
        var src = SourceRect(new Rect(0, 2080, 100, 80), Monitor, Virtual, new Size(4096, 1080), Placement.Fill);
        Assert.Equal(1088, src.X, 1);
        Assert.Equal(1040, src.Y, 1);
    }

    [Fact]
    public void Stretch_maps_each_axis_independently()
    {
        var src = SourceRect(DockStrip, Monitor, Virtual, new Size(1000, 1000), Placement.Stretch);
        Assert.Equal(720 * 1000.0 / 3840, src.X, 1);
        Assert.Equal(2080 * 1000.0 / 2160, src.Y, 1);
    }

    [Fact]
    public void Fit_letterboxes_and_the_strip_lands_inside_the_image()
    {
        // A square image fit on a wide monitor: pillarboxed, x offset (3840-2160)/2 = 840.
        var src = SourceRect(new Rect(1900, 2080, 100, 80), Monitor, Virtual, new Size(1000, 1000), Placement.Fit);
        Assert.Equal((1900 - 840) / 2.16, src.X, 1);
        Assert.InRange(src.Y, 0, 999);
    }

    [Fact]
    public void Span_measures_against_the_virtual_screen()
    {
        var wideVirtual = new Rect(0, 0, 7680, 2160); // two monitors side by side
        var onSecond = new Rect(4000, 2080, 100, 80);
        var src = SourceRect(onSecond, new Rect(3840, 0, 3840, 2160), wideVirtual, new Size(7680, 2160), Placement.Span);
        Assert.Equal(4000, src.X, 1); // 1:1 against the virtual surface
    }

    [Fact]
    public void Tile_wraps_with_modulo()
    {
        var src = SourceRect(new Rect(2500, 2080, 100, 40), Monitor, Virtual, new Size(1000, 1000), Placement.Tile);
        Assert.Equal(500, src.X, 1); // 2500 mod 1000
        Assert.Equal(80, src.Y, 1);  // 2080 mod 1000
    }

    [Theory]
    [InlineData("10", "0", Placement.Fill)]
    [InlineData("2", "0", Placement.Stretch)]
    [InlineData("6", "0", Placement.Fit)]
    [InlineData("22", "0", Placement.Span)]
    [InlineData("0", "0", Placement.Center)]
    [InlineData("0", "1", Placement.Tile)]
    [InlineData(null, null, Placement.Fill)]
    public void Registry_values_map_to_the_documented_placements(string? style, string? tile, Placement expected) =>
        Assert.Equal(expected, FromRegistryValues(style, tile));
}
