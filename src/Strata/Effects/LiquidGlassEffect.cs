using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Strata.Effects;

/// <summary>
/// The liquid-glass shader for the dock's wallpaper layer: a signed-distance lens bends the glass toward the
/// rim, a seven-tap dispersion adds the chromatic fringe at denser glass, and directional plus ambient rim
/// highlights are computed from the edge normal (LiquidGlass.fx; math adapted from AndroidLiquidGlass,
/// Apache-2.0). The effect is attached to the wallpaper layer only, never to content. ps_3_0 renders in
/// hardware only, so callers gate on <see cref="IsSupported"/>; without it the flat frost remains the look.
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Strata;component/Effects/LiquidGlass.ps"),
    };

    public LiquidGlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SizeProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(RefractionHeightProperty);
        UpdateShaderValue(RefractionAmountProperty);
        UpdateShaderValue(LightAngleProperty);
        UpdateShaderValue(FalloffProperty);
        UpdateShaderValue(HighlightAlphaProperty);
        UpdateShaderValue(AmbientAlphaProperty);
        UpdateShaderValue(DispersionProperty);
        UpdateShaderValue(RimOnlyProperty);
    }

    /// <summary>ps_3_0 has no software fallback in WPF: full hardware rendering or the brushes carry the look.</summary>
    public static bool IsSupported =>
        RenderCapability.Tier >> 16 == 2 && RenderCapability.IsPixelShaderVersionSupported(3, 0);

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(Point), typeof(LiquidGlassEffect), new UIPropertyMetadata(new Point(1, 1), PixelShaderConstantCallback(0)));
    /// <summary>The layer's size in device-independent pixels; every other length shares that unit.</summary>
    public Point Size { get => (Point)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public static readonly DependencyProperty RefractionHeightProperty = DependencyProperty.Register(
        nameof(RefractionHeight), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(10.0, PixelShaderConstantCallback(2)));
    public double RefractionHeight { get => (double)GetValue(RefractionHeightProperty); set => SetValue(RefractionHeightProperty, value); }

    public static readonly DependencyProperty RefractionAmountProperty = DependencyProperty.Register(
        nameof(RefractionAmount), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(16.0, PixelShaderConstantCallback(3)));
    public double RefractionAmount { get => (double)GetValue(RefractionAmountProperty); set => SetValue(RefractionAmountProperty, value); }

    public static readonly DependencyProperty LightAngleProperty = DependencyProperty.Register(
        nameof(LightAngle), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(-Math.PI / 2, PixelShaderConstantCallback(4)));
    public double LightAngle { get => (double)GetValue(LightAngleProperty); set => SetValue(LightAngleProperty, value); }

    public static readonly DependencyProperty FalloffProperty = DependencyProperty.Register(
        nameof(Falloff), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(2.5, PixelShaderConstantCallback(5)));
    public double Falloff { get => (double)GetValue(FalloffProperty); set => SetValue(FalloffProperty, value); }

    public static readonly DependencyProperty HighlightAlphaProperty = DependencyProperty.Register(
        nameof(HighlightAlpha), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.3, PixelShaderConstantCallback(6)));
    public double HighlightAlpha { get => (double)GetValue(HighlightAlphaProperty); set => SetValue(HighlightAlphaProperty, value); }

    public static readonly DependencyProperty AmbientAlphaProperty = DependencyProperty.Register(
        nameof(AmbientAlpha), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.14, PixelShaderConstantCallback(7)));
    public double AmbientAlpha { get => (double)GetValue(AmbientAlphaProperty); set => SetValue(AmbientAlphaProperty, value); }

    public static readonly DependencyProperty DispersionProperty = DependencyProperty.Register(
        nameof(Dispersion), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(8)));
    public double Dispersion { get => (double)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }

    public static readonly DependencyProperty RimOnlyProperty = DependencyProperty.Register(
        nameof(RimOnly), typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(9)));
    /// <summary>1 renders the interior transparent and adds no highlight: the wallpaper-refraction layer shows
    /// only the bent rim band, while the real blur owns the middle and the specular layer owns the light.</summary>
    public double RimOnly { get => (double)GetValue(RimOnlyProperty); set => SetValue(RimOnlyProperty, value); }

    /// <summary>Apply <paramref name="p"/> for a layer currently measured at <paramref name="width"/> × <paramref name="height"/> DIPs.</summary>
    public void Apply(in LiquidGlassParameters p, double width, double height, double cornerRadius)
    {
        Size = new Point(Math.Max(1, width), Math.Max(1, height));
        Radius = cornerRadius;
        RefractionHeight = p.RefractionHeight;
        RefractionAmount = p.RefractionAmount;
        LightAngle = p.LightAngleRadians;
        Falloff = p.Falloff;
        HighlightAlpha = p.HighlightAlpha;
        AmbientAlpha = p.AmbientAlpha;
        Dispersion = p.Dispersion ? 1.0 : 0.0;
    }
}

/// <summary>
/// The rim parameters for a dock of a given height and glass opacity, derived from the values the source
/// material uses on pill-shaped bars (lens height ≈ a fifth of the bar, displacement ≈ a third, highlight up to
/// 38% white) and bounded so a very tall or tiny dock stays glass rather than caricature. Pure, so the mapping
/// is testable without rendering.
/// </summary>
public readonly record struct LiquidGlassParameters(
    double RefractionHeight, double RefractionAmount, double LightAngleRadians,
    double Falloff, double HighlightAlpha, double AmbientAlpha, bool Dispersion)
{
    public static LiquidGlassParameters For(double dockHeightDip, int glassOpacity)
    {
        int opacity = Math.Clamp(glassOpacity, 0, 100);
        return new(
            RefractionHeight: Math.Clamp(dockHeightDip * 0.20, 6, 14),
            RefractionAmount: Math.Clamp(dockHeightDip * 0.33, 10, 24),
            // The light sits above the dock: the edge normal that faces it is (0,-1) in WPF's y-down space.
            LightAngleRadians: -Math.PI / 2,
            Falloff: 2.5,
            // 22% white on airy glass up to the source's 38% on dense glass.
            HighlightAlpha: 0.22 + 0.16 * (opacity / 100.0),
            AmbientAlpha: 0.14,
            // The chromatic fringe reads as dirt on near-transparent glass; it needs body behind it.
            Dispersion: opacity >= 40);
    }
}
