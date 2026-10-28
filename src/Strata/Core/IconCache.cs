using System.Windows.Media;
using Strata.Native;

namespace Strata.Core;

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    public static int SourcePixels(double renderedDip, double dpiScale, bool animated = true) =>
        Math.Clamp((int)Math.Ceiling(renderedDip * Math.Max(1, dpiScale) * (animated ? 2 : 1)), 16, 512);

    public static string Key(string identity, int requestedPixels, double dpiScale) =>
        $"{identity}|px:{requestedPixels}|dpi:{Math.Round(dpiScale * 96)}";

    public static void Invalidate() => Cache.Clear();

    public static void NormalizePremultipliedAlpha(byte[] bgra, bool hasAlpha) =>
        ShellInterop.NormalizePremultipliedAlpha(bgra, hasAlpha);

    public static ImageSource? For(AppIdentity app, IntPtr window = default, int requestedPixels = 64, double dpiScale = 1)
    {
        string cacheKey = Key(app.Key, requestedPixels, dpiScale);
        if (Cache.TryGetValue(cacheKey, out var cached) && cached is not null) return cached;
        ImageSource? img = null;
        if (app.Aumid is not null) img = ShellInterop.GetShellImage(@"shell:AppsFolder\" + app.Aumid, requestedPixels);
        if (img is null && !string.IsNullOrEmpty(app.ExePath)) img = ShellInterop.GetShellImage(app.ExePath, requestedPixels);
        if (img is null && window != IntPtr.Zero) img = FromWindow(window);
        Cache[cacheKey] = img;
        return img;
    }

    public static ImageSource? For(PinnedApp pin, int requestedPixels = 64, double dpiScale = 1)
    {
        string identity = "pin:" + (pin.Aumid ?? pin.Path).ToLowerInvariant();
        string key = Key(identity, requestedPixels, dpiScale);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? img = null;
        if (pin.Aumid is not null) img = ShellInterop.GetShellImage(@"shell:AppsFolder\" + pin.Aumid, requestedPixels);
        if (img is null && !string.IsNullOrEmpty(pin.Path) && (File.Exists(pin.Path) || Directory.Exists(pin.Path)))
            img = ShellInterop.GetShellImage(pin.Path, requestedPixels);
        if (img is null && !string.IsNullOrEmpty(pin.ExePath) && File.Exists(pin.ExePath))
            img = ShellInterop.GetShellImage(pin.ExePath, requestedPixels);
        Cache[key] = img;
        return img;
    }

    private static ImageSource? FromWindow(IntPtr h)
    {
        IntPtr icon = IntPtr.Zero;
        foreach (int kind in new[] { Win32.ICON_BIG, Win32.ICON_SMALL2, Win32.ICON_SMALL })
        {
            Win32.SendMessageTimeout(h, Win32.WM_GETICON, new IntPtr(kind), IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 100, out icon);
            if (icon != IntPtr.Zero) break;
        }
        if (icon == IntPtr.Zero) icon = Win32.GetClassLongPtr(h, Win32.GCLP_HICON);
        if (icon == IntPtr.Zero) icon = Win32.GetClassLongPtr(h, Win32.GCLP_HICONSM);
        // These icons belong to the other window; copy, never destroy.
        return ShellInterop.HIconToSource(icon);
    }
}
