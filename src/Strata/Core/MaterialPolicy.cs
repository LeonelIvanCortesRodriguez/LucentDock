using Microsoft.Win32;
using System.Windows;
using Strata.Native;

namespace Strata.Core;

public enum MaterialMode
{
    Transparent,
    NativeAcrylic,
    Solid,
    SolidFallback,
}

public enum MaterialFallbackReason
{
    None,
    OpacityEndpoint,
    TransparencyDisabled,
    HighContrast,
    BatterySaver,
    RemoteSession,
    CompositionUnavailable,
    BackdropUnsupported,
}

/// <summary>Privacy-safe system capabilities that decide whether premium material effects are appropriate.</summary>
public sealed record MaterialEnvironment(
    bool TransparencyEffects,
    bool HighContrast,
    bool ReducedMotion,
    bool BatterySaver,
    bool RemoteSession,
    bool CompositionAvailable,
    bool SystemBackdropSupported)
{
    public static MaterialEnvironment PremiumFixture { get; } = new(
        TransparencyEffects: true, HighContrast: false, ReducedMotion: false, BatterySaver: false,
        RemoteSession: false, CompositionAvailable: true, SystemBackdropSupported: true);

    public static MaterialEnvironment ReadCurrent()
    {
        bool transparency = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int value) transparency = value != 0;
        }
        catch { /* An inaccessible preference leaves the documented DWM default available. */ }

        bool batterySaver = Win32.GetSystemPowerStatus(out var power) && power.SystemStatusFlag == 1;
        bool composition = Win32.DwmIsCompositionEnabled(out bool enabled) == 0 && enabled;
        return new MaterialEnvironment(
            transparency,
            SystemParameters.HighContrast,
            !SystemParameters.ClientAreaAnimation,
            batterySaver,
            Win32.GetSystemMetrics(Win32.SM_REMOTESESSION) != 0,
            composition,
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621));
    }
}

/// <summary>The stable, testable contract between appearance settings and native composition.</summary>
public sealed record MaterialDecision(
    MaterialMode Mode,
    MaterialFallbackReason Reason,
    bool UseSystemBackdrop,
    bool UseTexture,
    bool UseMotion)
{
    public bool UsesSolidFallback => Mode == MaterialMode.SolidFallback;
}

public static class MaterialPolicy
{
    public static bool AllowsSystemBackdrop(MaterialEnvironment environment) =>
        Decide(DockStyle.Glass, Theme.DefaultGlassOpacity, environment).UseSystemBackdrop;

    public static MaterialDecision Decide(DockStyle style, int glassOpacity, MaterialEnvironment environment)
    {
        style = DockStyles.Normalize(style);
        int opacity = Math.Clamp(glassOpacity, 0, 100);

        if (environment.HighContrast)
            return SolidFallback(MaterialFallbackReason.HighContrast);

        if (style == DockStyle.Dark || (style == DockStyle.Clear && opacity == 100))
            return new(MaterialMode.Solid, MaterialFallbackReason.OpacityEndpoint,
                UseSystemBackdrop: false, UseTexture: !environment.BatterySaver, UseMotion: CanMove(environment));

        if (style == DockStyle.Clear && opacity == 0)
            return new(MaterialMode.Transparent, MaterialFallbackReason.OpacityEndpoint,
                UseSystemBackdrop: false, UseTexture: false, UseMotion: CanMove(environment));

        if (!environment.TransparencyEffects) return SolidFallback(MaterialFallbackReason.TransparencyDisabled);
        if (environment.BatterySaver) return SolidFallback(MaterialFallbackReason.BatterySaver);
        if (environment.RemoteSession) return SolidFallback(MaterialFallbackReason.RemoteSession);
        if (!environment.CompositionAvailable) return SolidFallback(MaterialFallbackReason.CompositionUnavailable);
        if (!environment.SystemBackdropSupported) return SolidFallback(MaterialFallbackReason.BackdropUnsupported);

        return new(MaterialMode.NativeAcrylic, MaterialFallbackReason.None,
            UseSystemBackdrop: true, UseTexture: true, UseMotion: CanMove(environment));
    }

    private static bool CanMove(MaterialEnvironment environment) =>
        !environment.ReducedMotion && !environment.BatterySaver && !environment.RemoteSession;

    private static MaterialDecision SolidFallback(MaterialFallbackReason reason) =>
        new(MaterialMode.SolidFallback, reason, UseSystemBackdrop: false, UseTexture: false, UseMotion: false);
}
