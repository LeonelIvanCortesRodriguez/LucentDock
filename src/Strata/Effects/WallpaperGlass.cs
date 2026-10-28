using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Strata.Effects;

/// <summary>
/// Maps a screen-space rectangle to the source rectangle of the wallpaper image that Windows shows there, for
/// each wallpaper placement mode. Pure math, so the placement rules are pinned by tests. All rectangles are in
/// physical pixels; the image size is in image pixels.
/// </summary>
public static class WallpaperMapping
{
    /// <summary>Windows' stored placement: registry WallpaperStyle (+ TileWallpaper=1 meaning Tile).</summary>
    public enum Placement { Center, Tile, Stretch, Fit, Fill, Span }

    public static Placement FromRegistryValues(string? wallpaperStyle, string? tileWallpaper)
    {
        if (tileWallpaper == "1") return Placement.Tile;
        return wallpaperStyle switch
        {
            "2" => Placement.Stretch,
            "6" => Placement.Fit,
            "22" => Placement.Span,
            "0" => Placement.Center,
            _ => Placement.Fill, // "10" and anything unknown: Windows 11's default
        };
    }

    /// <summary>
    /// The wallpaper-image rectangle shown at <paramref name="screenRect"/>. The reference surface is the
    /// monitor for every mode except Span, which stretches over the whole virtual screen. Regions the image
    /// does not reach (Fit letterboxes, Center margins) are clamped to the nearest image pixels: the dock sits
    /// mid-monitor on the bottom edge, where every common mode has coverage, so the clamp only trims extremes.
    /// </summary>
    public static Rect SourceRect(Rect screenRect, Rect monitorRect, Rect virtualRect, Size image, Placement placement)
    {
        if (image.Width <= 0 || image.Height <= 0) return Rect.Empty;
        Rect surface = placement == Placement.Span ? virtualRect : monitorRect;
        double sx, sy, ox, oy; // screen = image * s + o  (per axis), so image = (screen - o) / s
        switch (placement)
        {
            case Placement.Stretch:
            case Placement.Span:
                sx = surface.Width / image.Width; sy = surface.Height / image.Height;
                ox = surface.X; oy = surface.Y;
                break;
            case Placement.Fit:
            {
                double s = Math.Min(surface.Width / image.Width, surface.Height / image.Height);
                sx = sy = s;
                ox = surface.X + (surface.Width - image.Width * s) / 2;
                oy = surface.Y + (surface.Height - image.Height * s) / 2;
                break;
            }
            case Placement.Center:
            case Placement.Tile:
            {
                sx = sy = 1;
                ox = surface.X + (placement == Placement.Center ? (surface.Width - image.Width) / 2 : 0);
                oy = surface.Y + (placement == Placement.Center ? (surface.Height - image.Height) / 2 : 0);
                break;
            }
            default: // Fill: cover the surface, centered crop
            {
                double s = Math.Max(surface.Width / image.Width, surface.Height / image.Height);
                sx = sy = s;
                ox = surface.X + (surface.Width - image.Width * s) / 2;
                oy = surface.Y + (surface.Height - image.Height * s) / 2;
                break;
            }
        }
        double x = (screenRect.X - ox) / sx, y = (screenRect.Y - oy) / sy;
        double w = screenRect.Width / sx, h = screenRect.Height / sy;
        if (placement == Placement.Tile) { x %= image.Width; y %= image.Height; if (x < 0) x += image.Width; if (y < 0) y += image.Height; }
        x = Math.Clamp(x, 0, Math.Max(0, image.Width - 1)); y = Math.Clamp(y, 0, Math.Max(0, image.Height - 1));
        w = Math.Clamp(w, 1, image.Width - x); h = Math.Clamp(h, 1, image.Height - y);
        return new Rect(x, y, w, h);
    }
}

/// <summary>
/// The strip of the user's wallpaper behind a screen rectangle, pre-softened, for the refraction layer: real
/// liquid glass bends what is behind it, and the wallpaper is the one thing behind the dock that is knowable
/// without capturing anyone's screen. Loaded once per wallpaper change and dock geometry change; nothing runs
/// per frame.
/// </summary>
public sealed class WallpaperGlass
{
    private string? _loadedPath;
    private DateTime _loadedWrite;
    private BitmapSource? _image;
    private bool _loadFailed; // a bad wallpaper is remembered until the file changes, not retried per call

    /// <summary>The current wallpaper file: the per-user setting, else the transcoded copy Windows keeps.</summary>
    public static (string? Path, WallpaperMapping.Placement Placement) ReadCurrent()
    {
        string? path = null, style = null, tile = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            path = key?.GetValue("WallPaper") as string;
            style = key?.GetValue("WallpaperStyle") as string;
            tile = key?.GetValue("TileWallpaper") as string;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            string transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            path = File.Exists(transcoded) ? transcoded : null;
        }
        return (path, WallpaperMapping.FromRegistryValues(style, tile));
    }

    /// <summary>The softened wallpaper strip for <paramref name="screenRect"/>, or null when there is none.</summary>
    public BitmapSource? GetStrip(Rect screenRect, Rect monitorRect, Rect virtualRect)
    {
        try
        {
            var (path, placement) = ReadCurrent();
            if (path is null) return null;
            var write = File.GetLastWriteTimeUtc(path);
            if (path != _loadedPath || write != _loadedWrite)
            {
                _loadedPath = path; _loadedWrite = write; _image = null; _loadFailed = false;
                try
                {
                    // ReadWrite sharing: Windows rewrites the transcoded file while announcing the change, and a
                    // sharing violation here would otherwise be retried on every geometry event (audit F8).
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapSource frame = decoder.Frames[0];
                    // The strip is softened anyway, so a 2048-wide master loses nothing visible and keeps an 8K
                    // wallpaper from pinning ~130 MB per dock for its lifetime (audit F6).
                    if (frame.PixelWidth > 2048)
                        frame = new TransformedBitmap(frame, new ScaleTransform(2048.0 / frame.PixelWidth, 2048.0 / frame.PixelWidth));
                    var cached = new WriteableBitmap(frame); // realize once; drop the decoder chain
                    cached.Freeze();
                    _image = cached;
                }
                catch { _loadFailed = true; } // remembered: no retry until the file's stamp changes
            }
            if (_image is null || _loadFailed) return null;
            var src = WallpaperMapping.SourceRect(screenRect, monitorRect, virtualRect,
                new Size(_image.PixelWidth, _image.PixelHeight), placement);
            if (src.IsEmpty || src.Width < 1 || src.Height < 1) return null;
            var crop = new CroppedBitmap(_image, new Int32Rect(
                (int)src.X, (int)src.Y, Math.Max(1, (int)src.Width), Math.Max(1, (int)src.Height)));
            // Soften by travelling through a small intermediate: the rim band should bend the same creamy
            // backdrop the blur shows, not a sharp photograph. Downscale-then-upscale is a cheap gaussian.
            var small = new TransformedBitmap(crop, new ScaleTransform(
                Math.Max(0.02, 24.0 / Math.Max(24, crop.PixelWidth)), Math.Max(0.02, 8.0 / Math.Max(8, crop.PixelHeight))));
            var soft = new TransformedBitmap(small, new ScaleTransform(
                crop.PixelWidth / (double)Math.Max(1, small.PixelWidth), crop.PixelHeight / (double)Math.Max(1, small.PixelHeight)));
            soft.Freeze();
            return soft;
        }
        catch { return null; } // a missing or malformed wallpaper never costs the dock its render
    }
}
