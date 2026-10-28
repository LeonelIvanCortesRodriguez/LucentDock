using System.Windows.Forms;
using Strata.Native;

namespace Strata.Core;

public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public readonly record struct DockPlacement(double? WidthDip, double? MaxWidthDip, int X, int Y, int HeightPx);
public readonly record struct DockDimensionContract(double WidthDip, double MaxWidthDip);

/// <summary>All monitor/window native inputs used to select and place docks, injectable for deterministic tests.</summary>
public interface IMonitorWindowGeometry
{
    IReadOnlyList<IntPtr> EnumerateMonitors();
    IntPtr PrimaryMonitor();
    PixelBounds Bounds(IntPtr monitor);
    IntPtr MonitorForWindow(IntPtr window);
    double ScaleForWindow(IntPtr window);
    void PlaceTopmost(IntPtr window, int x, int y);
}

public sealed class WindowsMonitorWindowGeometry : IMonitorWindowGeometry
{
    public IReadOnlyList<IntPtr> EnumerateMonitors() => Screen.AllScreens
        .Select(screen => Win32.MonitorFromPoint(new Win32.POINT
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2,
        }, Win32.MONITOR_DEFAULTTONEAREST))
        .Where(handle => handle != IntPtr.Zero)
        .Distinct()
        .ToList();

    public IntPtr PrimaryMonitor() =>
        Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, Win32.MONITOR_DEFAULTTOPRIMARY);

    public PixelBounds Bounds(IntPtr monitor)
    {
        Win32.RECT bounds = Win32.GetMonitor(monitor).rcMonitor;
        return new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    public IntPtr MonitorForWindow(IntPtr window) =>
        Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST);

    public double ScaleForWindow(IntPtr window)
    {
        uint dpi = window == IntPtr.Zero ? 0 : Win32.GetDpiForWindow(window);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    public void PlaceTopmost(IntPtr window, int x, int y) =>
        Win32.SetWindowPos(window, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
}

public static class DockPlacementPlanner
{
    public static DockPlacement Calculate(PixelBounds monitor, double scale, double dockHeightDip,
        double actualWidthDip, double pillGapDip, DockShape shape, DockEdge edge)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (monitor.Width <= 0 || monitor.Height <= 0) throw new ArgumentOutOfRangeException(nameof(monitor));
        bool pill = shape == DockShape.Pill;
        int heightPx = (int)Math.Round(dockHeightDip * scale);
        int widthPx = pill ? (int)Math.Round(actualWidthDip * scale) : monitor.Width;
        int gapPx = pill ? (int)Math.Round(pillGapDip * scale) : 0;
        int x = pill ? monitor.Left + (monitor.Width - widthPx) / 2 : monitor.Left;
        int y = edge == DockEdge.Bottom ? monitor.Bottom - heightPx - gapPx : monitor.Top + gapPx;
        return new(
            WidthDip: pill ? null : monitor.Width / scale,
            MaxWidthDip: pill ? Math.Max(0, monitor.Width / scale - pillGapDip * 4) : null,
            X: x, Y: y, HeightPx: heightPx);
    }

    public static int AppBarHeight(double dockHeightDip, double pillGapDip, DockShape shape, double scale) =>
        (int)Math.Ceiling((dockHeightDip + (shape == DockShape.Pill ? pillGapDip * 2 : 0)) * scale);

    /// <summary>Every shape transition resets both WPF constraints; no stale pill/full-width value survives.</summary>
    public static DockDimensionContract Dimensions(DockPlacement placement) => placement.MaxWidthDip is { } max
        ? new(double.NaN, max)
        : new(placement.WidthDip ?? double.NaN, double.PositiveInfinity);
}
