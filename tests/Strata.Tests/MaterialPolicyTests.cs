using System.Windows;
using System.Windows.Media;
using Strata.Core;
using Strata.Controls;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class MaterialPolicyTests
{
    private static readonly MaterialEnvironment Capable = new(
        TransparencyEffects: true,
        HighContrast: false,
        ReducedMotion: false,
        BatterySaver: false,
        RemoteSession: false,
        CompositionAvailable: true,
        SystemBackdropSupported: true);

    [Fact]
    public void Opacity_endpoints_preserve_the_exact_contract()
    {
        var transparent = MaterialPolicy.Decide(DockStyle.Clear, 0, Capable);
        Assert.Equal(MaterialMode.Transparent, transparent.Mode);
        Assert.False(transparent.UseSystemBackdrop);
        Assert.False(transparent.UseTexture);
        Assert.True(Theme.LiquidGlass(0).DockFill.A <= 1,
            "the transparent endpoint may keep one alpha step so the layered window stays hit-testable (audit F2)");
        Assert.Equal(0, Theme.LiquidGlass(0).DockBorder.A);

        var solid = MaterialPolicy.Decide(DockStyle.Clear, 100, Capable);
        Assert.Equal(MaterialMode.Solid, solid.Mode);
        Assert.False(solid.UseSystemBackdrop);
        Assert.Equal(255, Theme.LiquidGlass(100).DockFill.A);
    }

    [Fact]
    public void Ordinary_translucency_uses_documented_native_acrylic()
    {
        var decision = MaterialPolicy.Decide(DockStyle.Clear, 42, Capable);
        Assert.Equal(MaterialMode.NativeAcrylic, decision.Mode);
        Assert.True(decision.UseSystemBackdrop);
        Assert.True(decision.UseTexture);
        Assert.Equal(MaterialFallbackReason.None, decision.Reason);
    }

    [Theory]
    [InlineData("transparency", MaterialFallbackReason.TransparencyDisabled)]
    [InlineData("contrast", MaterialFallbackReason.HighContrast)]
    [InlineData("battery", MaterialFallbackReason.BatterySaver)]
    [InlineData("remote", MaterialFallbackReason.RemoteSession)]
    [InlineData("composition", MaterialFallbackReason.CompositionUnavailable)]
    [InlineData("version", MaterialFallbackReason.BackdropUnsupported)]
    public void Constrained_environments_fail_closed_to_a_solid_surface(string constraint, MaterialFallbackReason reason)
    {
        var environment = Capable with
        {
            TransparencyEffects = constraint != "transparency",
            HighContrast = constraint == "contrast",
            BatterySaver = constraint == "battery",
            RemoteSession = constraint == "remote",
            CompositionAvailable = constraint != "composition",
            SystemBackdropSupported = constraint != "version",
        };

        var decision = MaterialPolicy.Decide(DockStyle.Clear, 42, environment);
        Assert.Equal(MaterialMode.SolidFallback, decision.Mode);
        Assert.Equal(reason, decision.Reason);
        Assert.False(decision.UseSystemBackdrop);
        Assert.False(decision.UseTexture);
        Assert.False(decision.UseMotion);
    }

    [Fact]
    public void Reduced_motion_keeps_static_material_but_suppresses_transitions()
    {
        var decision = MaterialPolicy.Decide(DockStyle.Clear, 42, Capable with { ReducedMotion = true });
        Assert.Equal(MaterialMode.NativeAcrylic, decision.Mode);
        Assert.True(decision.UseSystemBackdrop);
        Assert.True(decision.UseTexture);
        Assert.False(decision.UseMotion);
    }

    [Fact]
    public void High_contrast_palette_covers_shell_flyouts_inputs_and_interaction_states()
    {
        Palette palette = Theme.HighContrastPalette();

        Assert.Equal(SystemColors.WindowColor, palette.DockFill);
        Assert.Equal(SystemColors.WindowTextColor, palette.DockBorder);
        Assert.Equal(SystemColors.WindowTextColor, palette.Foreground);
        Assert.Equal(SystemColors.WindowColor, palette.Chip);
        Assert.Equal(SystemColors.WindowTextColor, palette.ChipBorder);
        Assert.Equal(SystemColors.WindowColor, palette.FlyoutBackground);
        Assert.Equal(SystemColors.WindowTextColor, palette.FlyoutBorder);
        Assert.Equal(SystemColors.WindowTextColor, palette.FlyoutForeground);
        Assert.Equal(SystemColors.WindowTextColor, palette.FlyoutSubtle);
        Assert.Equal(SystemColors.WindowColor, palette.InputBackground);
        Assert.Equal(SystemColors.HighlightColor, palette.Hover);
        Assert.Equal(SystemColors.HighlightColor, palette.Pressed);
        Assert.Equal(SystemColors.HighlightColor, palette.Accent);
        Assert.Equal(SystemColors.HighlightTextColor, palette.AccentForeground);
        Assert.False(palette.Liquid);
        Assert.Equal(0u, palette.AcrylicTint);
    }

    [Fact]
    public void High_contrast_interaction_overrides_descendant_text_resources_with_highlight_text()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new System.Windows.Controls.Button();
                HighContrastInteraction.Apply(host, highContrast: true, active: true, SystemColors.HighlightTextBrush);
                Assert.Same(SystemColors.HighlightTextBrush, host.Resources["Fg"]);
                Assert.Same(SystemColors.HighlightTextBrush, host.Resources["FgSubtle"]);
                Assert.Same(SystemColors.HighlightTextBrush, host.Foreground);

                HighContrastInteraction.Apply(host, highContrast: true, active: false, SystemColors.HighlightTextBrush);
                Assert.False(host.Resources.Contains("Fg"));
                Assert.False(host.Resources.Contains("FgSubtle"));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA material test timed out.");
        if (failure is not null) throw failure;
    }

    [Fact]
    public void White_text_on_liquid_chips_meets_normal_text_contrast_over_white()
    {
        // Below 10% there is no glass to carry readability, so the dark chip must hold the full 4.5:1 contract
        // over a white wallpaper, exactly as 1.3.0 shipped.
        for (int opacity = 0; opacity < 10; opacity++)
        {
            var palette = Theme.LiquidGlass(opacity);
            Color surface = Composite(palette.DockFill, Colors.White);
            Color behindText = Composite(palette.Chip, surface);
            double contrast = Contrast(Colors.White, behindText);
            Assert.True(contrast >= 4.5, $"{opacity}% produced only {contrast:F2}:1");
        }
        // From 10% up the owner chose the reference look (2026-10-27): one glass, no plates, readability riding
        // the blur -- the same trade the reference makes, with a soft text shadow as the planned follow-up. The
        // contract here is the absence itself, so a dark plate cannot quietly return.
        for (int opacity = 10; opacity <= 100; opacity++)
            Assert.Equal(0, Theme.LiquidGlass(opacity).Chip.A);
        // The solid endpoint is the one wallpaper-independent case, so it must stand on its own: dark ink on
        // the light fill, full normal-text contrast, whatever is behind the dock.
        var solid = Theme.LiquidGlass(100);
        Assert.True(Contrast(solid.Foreground, solid.DockFill) >= 4.5,
            $"solid liquid glass carries only {Contrast(solid.Foreground, solid.DockFill):F2}:1");
    }

    private static Color Composite(Color front, Color back)
    {
        double a = front.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(front.R, back.R), Mix(front.G, back.G), Mix(front.B, back.B));
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
