using System.Windows;
using System.Windows.Media;

namespace Strata.Core;

/// <summary>Colors for one dock style. Applied as dynamic resources so every control restyles live.</summary>
public sealed record Palette(
    string Name,
    uint AcrylicTint,      // AABBGGRR for the blur-behind tint; 0 = no blur
    Color DockFill,        // painted by WPF on top of the blur
    Color DockBorder,
    Color Foreground,
    Color Subtle,
    Color Hover,
    Color Pressed,
    Color FlyoutBackground,
    Color FlyoutBorder,
    Color FlyoutForeground,
    Color FlyoutSubtle,
    Color InputBackground,
    Color Accent,
    Color AccentForeground,
    bool IsLight,
    Color Chip = default,         // behind each widget (liquid glass); transparent in the other styles
    Color ChipBorder = default,   // the chip's rim
    bool Liquid = false,          // liquid glass: light-catching rims and a top sheen
    // Historical name: the dock no longer composes any DWM material, so today this only keeps the window's
    // immersive-dark-mode attribute off for the Liquid style. Text colors are unrelated (IsLight governs those).
    bool LightBackdrop = false);

public static class Theme
{
    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Liquid glass opacity when none is set: translucent, blurred glass.</summary>
    public const int DefaultGlassOpacity = 45;

    private static readonly Palette DarkFlyoutBase = new(
        "", 0, default, default, default, default, default, default,
        FlyoutBackground: C(0xFF202024), FlyoutBorder: C(0x33FFFFFF), FlyoutForeground: C(0xFFF4F4F6),
        FlyoutSubtle: C(0xFF9A9AA4), InputBackground: C(0x1FFFFFFF), Accent: C(0xFF5B9DFF),
        AccentForeground: Colors.White, IsLight: false);

    /// <summary>
    /// Glass: dark acrylic with a faint white wash (the default). Liquid glass (stored as <see cref="DockStyle.Clear"/>):
    /// glass whose opacity the user sets, from fully transparent through blurred translucent to a solid fill, with
    /// each widget on its own glass chip. Dark: dark mode, a near-opaque charcoal dock. Flyouts are always opaque.
    /// Older (1.1) styles render as their nearest 1.2 style.
    /// </summary>
    public static Palette For(DockStyle style, int glassOpacity = DefaultGlassOpacity) => DockStyles.Normalize(style) switch
    {
        DockStyle.Clear => LiquidGlass(glassOpacity),
        DockStyle.Dark => DarkFlyoutBase with
        {
            Name = "Dark", AcrylicTint = 0xF0141416, DockFill = C(0xEB161618), DockBorder = C(0x24FFFFFF),
            Foreground = C(0xFFEDEDF0), Subtle = C(0xFF8F8F99), Hover = C(0x1CFFFFFF), Pressed = C(0x30FFFFFF),
            FlyoutBackground = C(0xFF141416), FlyoutBorder = C(0x26FFFFFF), InputBackground = C(0x17FFFFFF),
        },
        _ => DarkFlyoutBase with
        {
            Name = "Glass", AcrylicTint = 0x50181818, DockFill = C(0x18FFFFFF), DockBorder = C(0x40FFFFFF),
            Foreground = C(0xFFFFFFFF), Subtle = C(0xBFFFFFFF), Hover = C(0x24FFFFFF), Pressed = C(0x3DFFFFFF),
        },
    };

    /// <summary>
    /// Liquid glass at <paramref name="opacity"/> (0..100):
    /// - 0: visually untouched wallpaper (one invisible alpha step keeps the layered window hit-testable);
    /// - below 10: the dark widget chips carry the full 4.5:1 text contract, as 1.3.0 did;
    /// - 10..99: the app-painted frosted wallpaper under one milky gradient wash, lensed and lit by the shader;
    /// - 100: a solid light fill written with dark ink.
    /// </summary>
    public static Palette LiquidGlass(int opacity)
    {
        int o = Math.Clamp(opacity, 0, 100);
        // Milky, not smoked (the owner's reference, 2026-10-27): real liquid glass BRIGHTENS what is behind it
        // with a white scrim and lets the wallpaper's own color come through the blur, and nothing sits in a dark
        // plate. Opacity runs from fully transparent through increasingly milky to a solid light fill.
        // One glass, one pass (the owner's reference, 2026-10-27): the dock reserves its screen space, so the
        // only thing ever behind it is wallpaper -- the creamy wallpaper strip IS the glass, under one gradient
        // milk wash, bent by the lens at the rim, lit once. No noise, no sheen, no inner rim, no chips: the
        // reference has none, and nine translucent layers reading as a silver bar is how the first attempt died.
        byte fill = o == 100 ? (byte)255 : (byte)Math.Round(o * 1.1);
        // Below 10% there is effectively no glass, so the dark chip keeps the full 4.5:1 white-text contract
        // over a white wallpaper, exactly as 1.3.0 did. At the solid endpoint the glass is paper -- the one
        // wallpaper-independent case -- so the ink turns dark there (audit F1); between, readability rides the
        // blur, the owner's accepted trade, with a soft text shadow as the planned follow-up.
        byte chipDark = o < 10 ? (byte)0xB0 : (byte)0;
        bool darkInk = o == 100;
        return DarkFlyoutBase with
        {
            Name = "Liquid glass",
            // AABBGGRR for the accent blur's own tint (the fallback floor when something other than wallpaper
            // slides behind); opaque only at the solid endpoint.
            AcrylicTint = o == 0 ? 0u : ((uint)(o == 100 ? 255 : Math.Round(o * 0.8)) << 24) | 0xF7F5F4u,
            // Alpha 1 at the transparent endpoint: invisible, but a layered window hit-tests per pixel, and an
            // alpha-0 shell would let clicks fall through to the desktop (audit F2).
            DockFill = o == 0 ? Color.FromArgb(1, 0, 0, 0) : o == 100 ? C(0xFFF2F3F5) : Color.FromArgb(fill, 0xF4, 0xF5, 0xF7),
            DockBorder = o == 0 ? Colors.Transparent : C(0x47FFFFFF),
            Foreground = darkInk ? C(0xFF1C1C20) : C(0xFFFFFFFF),
            Subtle = darkInk ? C(0xB31C1C20) : C(0xD9FFFFFF),
            Hover = darkInk ? C(0x14000000) : C(0x2EFFFFFF),
            Pressed = darkInk ? C(0x28000000) : C(0x47FFFFFF),
            Chip = chipDark > 0 ? Color.FromArgb(chipDark, 0, 0, 0) : Colors.Transparent,
            ChipBorder = Colors.Transparent,
            Liquid = true,
            LightBackdrop = true,
        };
    }

    public static Palette Current { get; private set; } = For(DockStyle.Glass);

    /// <summary>Every interactive surface uses the active Windows high-contrast color contract.</summary>
    public static Palette HighContrastPalette() => DarkFlyoutBase with
    {
        Name = "High contrast", AcrylicTint = 0, Liquid = false,
        DockFill = SystemColors.WindowColor, DockBorder = SystemColors.WindowTextColor,
        Foreground = SystemColors.WindowTextColor, Subtle = SystemColors.WindowTextColor,
        Hover = SystemColors.HighlightColor, Pressed = SystemColors.HighlightColor,
        FlyoutBackground = SystemColors.WindowColor, FlyoutBorder = SystemColors.WindowTextColor,
        FlyoutForeground = SystemColors.WindowTextColor, FlyoutSubtle = SystemColors.WindowTextColor,
        InputBackground = SystemColors.WindowColor, Accent = SystemColors.HighlightColor,
        AccentForeground = SystemColors.HighlightTextColor,
        Chip = SystemColors.WindowColor, ChipBorder = SystemColors.WindowTextColor,
        IsLight = SystemColors.WindowColor.R + SystemColors.WindowColor.G + SystemColors.WindowColor.B > 382,
    };

    public static void Apply(DockStyle style, int glassOpacity = DefaultGlassOpacity, MaterialDecision? material = null)
    {
        Current = For(style, glassOpacity);
        if (material?.UsesSolidFallback == true)
        {
            Current = material.Reason == MaterialFallbackReason.HighContrast ? HighContrastPalette() : Current with
            {
                AcrylicTint = 0,
                DockFill = C(0xF218181C), DockBorder = C(0x38FFFFFF),
                Foreground = C(0xFFF7F7F8), Subtle = C(0xFFC4C4CA),
                Chip = C(0x18FFFFFF), ChipBorder = C(0x28FFFFFF),
            };
        }
        var r = Application.Current.Resources;
        void Set(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
        void SetBrush(string key, Brush b) { b.Freeze(); r[key] = b; }
        Set("DockFill", Current.DockFill);
        Set("DockBorder", Current.DockBorder);
        Set("Chip", Current.Chip);
        Set("ChipBorder", Current.ChipBorder);
        // The > 1 floor matches the dock's own gating: alpha 1 is the invisible hit-test fill of the transparent
        // endpoint, where nothing may paint. Solid fallbacks never take the wash either -- they are the readable
        // path, not a glass (audit F3, F4).
        if (Current.Liquid && Current.DockFill.A > 1 && Current.DockFill.A < 255 && material?.UsesSolidFallback != true)
        {
            // The one milk wash: stronger at the top where light enters the glass, almost gone at the bottom.
            // The reference card's scrim is a gradient, not a flat veil, and this is the only wash there is.
            var top = Current.DockFill;
            var crest = Color.FromArgb((byte)Math.Min(255, top.A * 2), top.R, top.G, top.B);
            var foot = Color.FromArgb((byte)(top.A / 2), top.R, top.G, top.B);
            SetBrush("DockFill", new LinearGradientBrush(crest, foot, 90));
            SetBrush("DockBorder", new LinearGradientBrush(C(0x8CFFFFFF), C(0x1FFFFFFF), 90));
        }
        Set("Fg", Current.Foreground);
        Set("FgSubtle", Current.Subtle);
        Set("Hover", Current.Hover);
        Set("Pressed", Current.Pressed);
        Set("FlyoutBg", Current.FlyoutBackground);
        Set("FlyoutBorder", Current.FlyoutBorder);
        Set("FlyoutFg", Current.FlyoutForeground);
        Set("FlyoutSubtle", Current.FlyoutSubtle);
        Set("InputBg", Current.InputBackground);
        Set("Accent", Current.Accent);
        Set("AccentFg", Current.AccentForeground);
        SetBrush("DockNoise", material?.UseTexture == true ? CreateNoiseBrush() : Brushes.Transparent.Clone());
        SetBrush("DockSpecular", Current.Liquid && Current.DockFill.A > 1 && material?.UsesSolidFallback != true
            ? new LinearGradientBrush(C(0x30FFFFFF), C(0x00FFFFFF), 90)
            : Brushes.Transparent.Clone());
        SetBrush("DockInnerRim", Current.Liquid && Current.DockFill.A > 1 && material?.UsesSolidFallback != true
            ? new LinearGradientBrush(C(0x42FFFFFF), C(0x0AFFFFFF), 90)
            : Brushes.Transparent.Clone());
    }

    private static DrawingBrush CreateNoiseBrush()
    {
        var group = new DrawingGroup();
        // A tiny deterministic vector tile: native-resolution, no scaled bitmap, and intentionally restrained.
        var white = new SolidColorBrush(C(0x0BFFFFFF)); white.Freeze();
        var black = new SolidColorBrush(C(0x08000000)); black.Freeze();
        group.Children.Add(new GeometryDrawing(white, null, new EllipseGeometry(new Point(1.5, 2.0), 0.45, 0.45)));
        group.Children.Add(new GeometryDrawing(black, null, new EllipseGeometry(new Point(7.0, 5.5), 0.4, 0.4)));
        group.Children.Add(new GeometryDrawing(white, null, new EllipseGeometry(new Point(10.5, 9.0), 0.35, 0.35)));
        group.Freeze();
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Opacity = 0.34,
        };
    }

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}
