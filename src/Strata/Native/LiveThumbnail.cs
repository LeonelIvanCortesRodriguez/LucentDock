using System.Runtime.InteropServices;
using System.Windows;

namespace Strata.Native;

/// <summary>
/// A live picture of another window, drawn by DWM into a rectangle of one of Strata's windows: what the taskbar's
/// hover previews use. Nothing is copied or stored; DWM composes the source window's current content directly.
/// The destination must be an ordinary (non-layered) top-level window of this process.
/// </summary>
public sealed class LiveThumbnail : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Properties
    {
        public uint Flags;
        public Win32.RECT Destination;
        public Win32.RECT Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; }

    private const uint RectDestination = 0x1, OpacityFlag = 0x4, VisibleFlag = 0x8, ClientAreaOnly = 0x10;

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);

    private IntPtr _handle;

    private LiveThumbnail(IntPtr handle) => _handle = handle;

    /// <summary>Register <paramref name="source"/> to be drawn into <paramref name="destination"/>; null if DWM refuses.</summary>
    public static LiveThumbnail? Register(IntPtr destination, IntPtr source)
    {
        if (destination == IntPtr.Zero || source == IntPtr.Zero) return null;
        try { return DwmRegisterThumbnail(destination, source, out var h) == 0 && h != IntPtr.Zero ? new LiveThumbnail(h) : null; }
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>The source window's size in physical pixels (empty if unknown).</summary>
    public Size SourceSize => _handle != IntPtr.Zero && DwmQueryThumbnailSourceSize(_handle, out var s) == 0 ? new Size(s.Cx, s.Cy) : Size.Empty;

    /// <summary>Draw it into <paramref name="destinationPixels"/> (physical pixels, relative to the destination window).</summary>
    public bool Show(Rect destinationPixels)
    {
        if (_handle == IntPtr.Zero) return false;
        var p = new Properties
        {
            Flags = RectDestination | OpacityFlag | VisibleFlag | ClientAreaOnly,
            Destination = new Win32.RECT
            {
                Left = (int)Math.Round(destinationPixels.Left), Top = (int)Math.Round(destinationPixels.Top),
                Right = (int)Math.Round(destinationPixels.Right), Bottom = (int)Math.Round(destinationPixels.Bottom),
            },
            Opacity = 255,
            Visible = true,
            SourceClientAreaOnly = false,
        };
        return DwmUpdateThumbnailProperties(_handle, ref p) == 0;
    }

    /// <summary>The largest rectangle with the source's shape that fits <paramref name="box"/>, centred in it.</summary>
    public static Rect Fit(Size source, Rect box)
    {
        if (source.IsEmpty || source.Width <= 0 || source.Height <= 0 || box.Width <= 0 || box.Height <= 0) return Rect.Empty;
        double scale = Math.Min(box.Width / source.Width, box.Height / source.Height);
        double w = source.Width * scale, h = source.Height * scale;
        return new Rect(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_handle);
        _handle = IntPtr.Zero;
    }
}
