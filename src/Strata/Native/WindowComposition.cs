using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Strata.Native;

/// <summary>
/// The composition path for the dock and the card windows. The dock is a layered window (per-pixel alpha the
/// compositor guarantees) with the classic blur-behind accent carrying the frost and the palette's AABBGGRR
/// tint; the DWM system backdrops are not used for it, because they compose black for this class of borderless
/// no-activate window on some machines (measured 2026-10-27, shipped builds included). The opaque card windows
/// (flyouts, palette, previews) keep the original non-layered recipe exactly as 1.3.0 shipped.
/// </summary>
public static class WindowComposition
{
    /// <summary>Radius of DWMWCP_ROUND corners (8 effective pixels; DWM scales it with the window's DPI).</summary>
    public const double SystemCornerRadiusDip = 8;

    /// <summary>
    /// WPF clears a non-layered window to opaque black by default, so every pixel it doesn't paint (corners,
    /// translucent fills) would show black instead of the DWM backdrop. Make the surface transparent.
    /// </summary>
    public static void MakeRenderSurfaceTransparent(IntPtr hwnd)
    {
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        // A surface that stays opaque black is exactly the invisible failure the 2026-10-27 glass hunt was made
        // of, so the miss is named rather than silent.
        else Core.Log.Write($"Composition surface NOT set: source={(source is null ? "null" : "ok")}, target=null");
    }

    public static void Apply(IntPtr hwnd, bool rounded, bool darkMode, bool systemBackdrop,
        uint accentTint = 0, bool transparentGlass = false, bool glassWindow = false)
    {
        MakeRenderSurfaceTransparent(hwnd);
        int corner = rounded ? Win32.DWMWCP_ROUND : Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int dark = darkMode ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (!glassWindow)
        {
            // The opaque card windows keep the 1.3.0 recipe exactly: system backdrop where allowed, the frame
            // extended, no accent. Their look was never broken, and only their antialiased edge pixels show it.
            int cardBackdrop = systemBackdrop ? Win32.DWMSBT_TRANSIENTWINDOW : Win32.DWMSBT_NONE;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref cardBackdrop, sizeof(int));
            uint cardNoBorder = Win32.DWMWA_COLOR_NONE;
            Win32.DwmSetWindowAttributeUInt(hwnd, Win32.DWMWA_BORDER_COLOR, ref cardNoBorder, sizeof(uint));
            var cardMargins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(hwnd, ref cardMargins);
            return;
        }
        // The dock: a pure layered window. No DWM backdrop (they compose black for this class of borderless
        // no-activate window on some machines -- measured 2026-10-27, shipped builds included), no frame
        // extension (the extended sheet with no material is an opaque dark layer), and no accent either: the
        // accent applies to the window RECTANGLE, which showed as a second dock silhouette with dark corners
        // behind the rounded pill. The glass is the wallpaper layer the app paints itself -- the dock reserves
        // its screen space, so the wallpaper is the only truth behind it -- and every visible pixel is the app's.
        int backdrop = Win32.DWMSBT_NONE;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        uint noBorder = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttributeUInt(hwnd, Win32.DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
        var margins = new Win32.MARGINS();
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        SetAccent(hwnd, AccentDisabled, 0);
        _ = accentTint; _ = systemBackdrop; _ = transparentGlass; // material modes now gate the app's own layers only
    }

    private const int AccentDisabled = 0;
    private const int AccentTransparentGradient = 2;
    private const int AccentBlurBehind = 3;
    private const int AccentAcrylicBlurBehind = 4;
    private const int WcaAccentPolicy = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData { public int Attrib; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    private static void SetAccent(IntPtr hwnd, int state, uint gradientColor)
    {
        // Flags 2 ("draw all") is what working accent implementations pass; without it some builds draw nothing.
        var policy = new AccentPolicy { AccentState = state, AccentFlags = state == AccentDisabled ? 0 : 2, GradientColor = gradientColor };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new WindowCompositionAttribData { Attrib = WcaAccentPolicy, Data = memory, SizeOfData = size };
            if (SetWindowCompositionAttribute(hwnd, ref data) == 0)
                Core.Log.Write($"Accent policy {state} was refused for {hwnd:x}");
        }
        catch (Exception ex) { Core.Log.Write($"Accent policy unavailable: {ex.GetType().Name}"); }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
