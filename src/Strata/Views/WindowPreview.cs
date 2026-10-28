using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Native;
using Strata.Widgets;

namespace Strata.Views;

/// <summary>
/// The taskbar-style hover previews for one app: a card above (or below) its dock icon with a tile per open window
/// (its title, a close button and a live picture drawn by DWM), and how many windows are open. Clicking a tile
/// switches to that window. It never takes focus, so hovering never disturbs the window you are working in.
/// Minimized windows have no live picture (Windows doesn't draw them), so their tile shows the app's icon.
/// </summary>
public sealed class WindowPreview : Window
{
    public const int MaxTiles = 6;
    public const double TileWidth = 208, PictureHeight = 120;

    private readonly FrameworkElement _anchor;
    private readonly bool _below;
    private readonly List<(Border Box, IntPtr Source, LiveThumbnail? Picture)> _pictures = [];
    private IntPtr _hwnd;

    public AppButton? App { get; }

    public WindowPreview(AppButton anchor, IReadOnlyList<TrackedWindow> windows, bool below,
        Action<IntPtr> activate, Action<IntPtr> close)
    {
        _anchor = anchor;
        App = anchor;
        _below = below;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;      // DWM thumbnails need an ordinary window
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.UiFont;

        var tiles = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = (TileWidth + 8) * 3 };
        foreach (var w in windows.Take(MaxTiles)) tiles.Children.Add(Tile(w, activate, close));
        var header = UI.Text(Header(anchor.DisplayName, windows.Count), 12.5, flyout: true, weight: FontWeights.SemiBold);
        header.Margin = new Thickness(4, 0, 0, 8);
        var col = new StackPanel();
        col.Children.Add(header);
        col.Children.Add(tiles);
        if (windows.Count > MaxTiles)
            col.Children.Add(UI.Text($"and {windows.Count - MaxTiles} more: right-click the icon for all", 11.5, subtle: true, flyout: true).Also(t => t.Margin = new Thickness(4, 6, 0, 0)));
        var card = FlyoutWindow.CreateCard(col);
        card.Padding = new Thickness(10, 10, 10, 10);
        Content = card;
        System.Windows.Automation.AutomationProperties.SetName(this, Header(anchor.DisplayName, windows.Count));

        SourceInitialized += (_, _) => OnSourceInitialized(windows);
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => { if (IsLoaded) Place(); };
        ContentRendered += (_, _) => ShowPictures();
        Closed += (_, _) => { foreach (var p in _pictures) p.Picture?.Dispose(); _pictures.Clear(); };
    }

    /// <summary>"Excel · 3 windows", or just the name for one.</summary>
    public static string Header(string app, int windows) => windows <= 1 ? app : $"{app} · {windows} windows";

    private FrameworkElement Tile(TrackedWindow w, Action<IntPtr> activate, Action<IntPtr> close)
    {
        var handle = w.Handle;
        var title = UI.Text(w.Title.Length > 0 ? w.Title : "Untitled", 12, flyout: true);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var closeButton = UI.IconButton(Glyphs.Close, () => close(handle), 10, "Close window");
        closeButton.Padding = new Thickness(6, 4, 6, 4);
        var top = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
        DockPanel.SetDock(closeButton, Dock.Right);
        top.Children.Add(closeButton);
        top.Children.Add(title);

        var box = new Border { Height = PictureHeight, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)) };
        if (w.IsMinimized || Win32.IsIconic(handle))
        {
            var icon = new Image { Width = 40, Height = 40, Source = IconCache.For(w.App, handle, 64, 1), Opacity = 0.9 };
            var label = UI.Text("Minimized", 11.5, subtle: true, flyout: true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            box.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } };
            box.Tag = "minimized";
        }

        var tile = new Border { Width = TileWidth, Padding = new Thickness(6), Margin = new Thickness(4), CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = new StackPanel { Children = { top, box } } };
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Hover");
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) => { if (!closeButton.IsMouseOver) { e.Handled = true; activate(handle); } };
        // Middle-click closes that window, as on the taskbar's previews.
        tile.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; close(handle); } };
        System.Windows.Automation.AutomationProperties.SetName(tile, w.Title);
        if (box.Tag is null)
        {
            _pictures.Add((box, handle, null));
            box.SizeChanged += (_, _) => ShowPictures();
        }
        return tile;
    }

    private void OnSourceInitialized(IReadOnlyList<TrackedWindow> windows)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetExStyle(_hwnd) | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)!.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == (int)Win32.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Win32.MA_NOACTIVATE); }
            return IntPtr.Zero;
        });
        WindowComposition.Apply(_hwnd, rounded: true, darkMode: !Theme.Current.IsLight,
            systemBackdrop: MaterialPolicy.AllowsSystemBackdrop(MaterialEnvironment.ReadCurrent()));
        for (int i = 0; i < _pictures.Count; i++)
            _pictures[i] = (_pictures[i].Box, _pictures[i].Source, LiveThumbnail.Register(_hwnd, _pictures[i].Source));
    }

    /// <summary>How many tiles have a live picture registered (for checks).</summary>
    public int LivePictures => _pictures.Count(p => p.Picture is not null);

    private void ShowPictures()
    {
        if (_hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var toDevice = target.TransformToDevice;
        foreach (var (box, _, picture) in _pictures)
        {
            if (picture is null || !box.IsVisible) continue;
            var dip = box.TransformToAncestor(this).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
            var px = new Rect(toDevice.Transform(dip.TopLeft), toDevice.Transform(dip.BottomRight));
            var fit = LiveThumbnail.Fit(picture.SourceSize, px);
            if (!fit.IsEmpty) picture.Show(fit);
        }
    }

    private void Place()
    {
        var source = PresentationSource.FromVisual(_anchor);
        if (source?.CompositionTarget is null) return;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(_anchor.PointToScreen(new Point(0, 0)));
        var bottomRight = fromDevice.Transform(_anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight)));
        var at = _anchor.PointToScreen(new Point(_anchor.ActualWidth / 2, _anchor.ActualHeight / 2));
        var mon = Win32.MonitorAt((int)at.X, (int)at.Y).rcMonitor;
        var monTl = fromDevice.Transform(new Point(mon.Left, mon.Top));
        var monBr = fromDevice.Transform(new Point(mon.Right, mon.Bottom));
        const double gap = 10;
        double left = Math.Clamp((topLeft.X + bottomRight.X) / 2 - ActualWidth / 2, monTl.X + 4, monBr.X - ActualWidth - 4);
        double top = _below ? bottomRight.Y + gap + 6 : topLeft.Y - gap - ActualHeight - 6;
        Left = left;
        Top = Math.Clamp(top, monTl.Y, Math.Max(monTl.Y, monBr.Y - ActualHeight));
    }
}
