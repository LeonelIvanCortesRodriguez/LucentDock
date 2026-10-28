using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Strata.Controls;
using Strata.Core;
using Strata.Native;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Views;

public enum AppClickKind { LaunchPin, ActivateWindow, MinimizeWindow }
public readonly record struct AppClickDecision(AppClickKind Kind, IntPtr Window);

public sealed class DockWindow : Window, IDockHost, IDockActions
{
    private readonly App _app;
    private readonly Func<MaterialEnvironment> _materialEnvironment;
    private readonly IMonitorWindowGeometry _monitorGeometry;
    private readonly IWindowSource _tracker;
    private readonly IDisposable _windowSubscription;
    private readonly AppBarSafety _appBarSafety;
    private readonly LayoutStack _layouts = new();
    private readonly ThemeController _theme;
    private readonly TaskbarSafetyLifecycle _taskbarSafety;
    private readonly List<(string Owner, PinnedApp Pin, string Reason)> _suggestions = [];
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // Hover previews of an app's windows (see HoverIntent, WindowPreview); the timer runs only while one may change.
    private readonly HoverIntent<AppButton> _hover = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private WindowPreview? _preview;
    private readonly List<Widget> _widgets = [];
    private readonly Dictionary<Widget, StableWidgetHost> _widgetHosts = [];
    private readonly List<AppButton> _appButtons = [];
    private readonly List<Widget> _overflowWidgets = [];
    private readonly List<AppButton> _overflowApps = [];
    private DockButton? _overflowButton;
    private bool _overflowScheduled;

    private readonly Border _shell = new();
    private readonly Grid _materialRoot = new();
    private readonly Border _noiseLayer = new() { IsHitTestVisible = false };
    private readonly Border _specularLayer = new() { IsHitTestVisible = false };
    // The wallpaper layer sits under everything and is the app-owned acrylic for EVERY translucent style: the
    // softened wallpaper strip, tinted by the palette (the dock composes no DWM material at all). Liquid glass
    // additionally runs the lens-and-light shader on it; the other styles show it as flat frosted tint.
    private readonly Border _refractionLayer = new() { IsHitTestVisible = false };
    private readonly Border _refractionTint = new() { IsHitTestVisible = false };
    private Effects.LiquidGlassEffect? _refractionEffect;
    private readonly Effects.WallpaperGlass _wallpaperGlass = new();
    private readonly Border _innerRim = new() { IsHitTestVisible = false, BorderThickness = new Thickness(1), Margin = new Thickness(1) };
    private readonly Grid _layout = new();
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly StackPanel _apps = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

    private IntPtr _hwnd;
    private uint _appBarMessage;
    private bool _nativeDockWasReady;
    private uint _taskbarCreatedMessage;
    private bool _fullscreenHidden;
    private IntPtr _lastForeground;
    private FlyoutWindow? _flyout;
    private FrameworkElement? _flyoutAnchor;
    private (FrameworkElement Anchor, DateTime At) _lastFlyoutClose;
    private int _seconds;
    private bool _keyboardMode;
    // Dragging an app along the dock (to reorder pins, or pin a running app).
    private AppButton? _dragApp;
    private Point _dragStart;
    private bool _dragMoving;
    private bool _suppressClick;
    private List<double> _dragCentres = [];
    private List<double> _dragWidths = [];
    private double _dragX;
    private int? _groupZone;

    /// <summary>Dim the would-be group target while the drag hovers its central half.</summary>
    private void SetGroupZoneDimmed(int? othersIndex, bool dimmed)
    {
        if (othersIndex is not int z) return;
        int childIndex = z < _dragFrom ? z : z + 1;
        if (childIndex >= 0 && childIndex < _apps.Children.Count && _apps.Children[childIndex] is FrameworkElement fe)
            fe.Opacity = dimmed ? 0.55 : 1.0;
    }
    private int _dragFrom, _dragSlot;
    private MaterialDecision _material = new(MaterialMode.SolidFallback, MaterialFallbackReason.CompositionUnavailable,
        UseSystemBackdrop: false, UseTexture: false, UseMotion: false);

    private const double PillGap = 6;
    private const int KeyboardHotKeyId = 0x5354;
    private const int CommandPaletteHotKeyId = 0x5350;
    private DockHotkey? _hotkey;
    private CommandPaletteHotkey? _paletteHotkey;
    private AppHotkeys? _appHotkeys;
    private CommandPaletteWindow? _commandPalette;
    private IReadOnlyList<PinnedApp>? _installedApplications;
    /// <summary>How many of the Win+Ctrl+Alt+1…9 app shortcuts are registered (0 when off).</summary>
    public int AppShortcutsRegistered => _appHotkeys?.Registered ?? 0;
    /// <summary>Whether <see cref="DockHotkey.Gesture"/> is registered; null before the window initializes.</summary>
    public bool? KeyboardShortcutRegistered => _hotkey?.Registered;
    public bool? CommandPaletteShortcutRegistered => _paletteHotkey?.Registered;

    public AppSettings Settings => _app.Settings;
    public ScreenTimeTracker ScreenTime => _app.ScreenTime;
    public SignalStore Signals => _app.Signals;
    public IReadOnlyList<Widget> Widgets => _widgets;
    public IReadOnlyList<AppButton> AppButtons => _appButtons;
    public DockStyle EffectiveTheme => _theme.Effective;

    /// <summary>For off-screen previews: lend out the dock's visual tree and take it back.</summary>
    public Border DetachShell() { Content = null; return _shell; }
    public void AttachShell(Border shell) => Content = shell;

    /// <summary>The screen this dock is on: zero for the main dock (the primary screen).</summary>
    private readonly IntPtr _monitor;
    /// <summary>An extra dock on another screen (<see cref="AppSettings.DockOnEveryScreen"/>): apps, Start and the clock only.</summary>
    public bool IsSecondary => _monitor != IntPtr.Zero;
    public IntPtr Monitor => _monitor;

    public DockWindow(App app, IntPtr monitor = default, Func<MaterialEnvironment>? materialEnvironment = null,
        IMonitorWindowGeometry? monitorGeometry = null)
    {
        _monitor = monitor;
        _app = app;
        _materialEnvironment = materialEnvironment ?? MaterialEnvironment.ReadCurrent;
        _monitorGeometry = monitorGeometry ?? new WindowsMonitorWindowGeometry();
        _tracker = app.WindowSnapshots.Source;
        _windowSubscription = app.WindowSnapshots.Subscribe(() => RefreshWindows(force: true));
        _appBarSafety = new AppBarSafety(new AppBarController(), app.Taskbar);
        _theme = new ThemeController(app.Settings.Style);
        _taskbarSafety = new TaskbarSafetyLifecycle(
            new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => _app.Taskbar.IsHidingOrAutoHiding,
            () => _hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd) && Win32.IsWindowVisible(_hwnd),
            () => _fullscreenHidden,
            () => PreserveTaskbarAccess(TaskbarSafetyBlockers.DockUnavailable),
            OnDockSafetyHealthy);
        _layouts.Changed += Rebuild;
        _theme.Changed += OnThemeChanged;
        Title = "Strata";
        WindowStyle = WindowStyle.None;
        // Layered, per-pixel alpha: the one composition mode whose transparency the OS guarantees for this class
        // of window. The non-layered recipe (transparent HwndTarget + DWM backdrop/accent) composed OPAQUE black
        // on this machine at every opacity -- measured against a pure white backdrop on 2026-10-27, on the shipped
        // 1.3.0 build too. The dock composes no DWM material at all: its frost is the app-painted wallpaper strip
        // (UpdateWallpaperRefraction), so every visible pixel is the app's own.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        FontFamily = Theme.UiFont;
        AllowDrop = true;

        _refractionLayer.Child = _refractionTint;
        _materialRoot.Children.Add(_refractionLayer);
        _materialRoot.Children.Add(_noiseLayer);
        _materialRoot.Children.Add(_specularLayer);
        _materialRoot.Children.Add(_innerRim);
        _materialRoot.Children.Add(_layout);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _shell.Child = _materialRoot;
        Content = _shell;

        SourceInitialized += OnSourceInitialized;
        // The dock re-centres on every width change (pill shape sizes to its content), so an open flyout has to
        // follow its tile; this covers every cause, including a widget taking its other declared width.
        SizeChanged += (_, _) => { PositionWindow(); ScheduleResponsiveOverflow(); _flyout?.FollowAnchor(); };
        Drop += OnDrop;
        MouseRightButtonUp += (_, e) => { if (!e.Handled) ShowDockMenu(); };
        PreviewKeyDown += OnDockKeyDown;
        Deactivated += (_, _) => ExitKeyboardMode("deactivated");

        _secondTimer.Tick += (_, _) => OnSecond();
        _hoverTimer.Tick += (_, _) => UpdatePreview();
    }

    // ---------------- Lifecycle ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetExStyle(_hwnd) | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        int noRound = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));

        _appBarMessage = Win32.RegisterWindowMessage("Strata.AppBarMessage");
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        if (!IsSecondary)   // the shortcuts belong to the main dock
        {
            _hotkey = new DockHotkey((mods, vk) => Win32.RegisterHotKey(_hwnd, KeyboardHotKeyId, mods, vk), _app.Notifications);
            _hotkey.Register();
            _paletteHotkey = new CommandPaletteHotkey((mods, vk) => Win32.RegisterHotKey(_hwnd, CommandPaletteHotKeyId, mods, vk), _app.Notifications);
            _paletteHotkey.Register();
            _appHotkeys = new AppHotkeys((id, mods, vk) => Win32.RegisterHotKey(_hwnd, id, mods, vk), id => Win32.UnregisterHotKey(_hwnd, id));
            if (Settings.AppShortcuts) _appHotkeys.Register();
        }

        Rebuild();
        _secondTimer.Start();
    }

    /// <summary>Apply settings: style, size, edge, widgets. Called at start and after every settings change.</summary>
    public void Rebuild()
    {
        CloseFlyout();
        _material = MaterialPolicy.Decide(_theme.Effective, Settings.GlassOpacity, _materialEnvironment());
        Theme.Apply(_theme.Effective, Settings.GlassOpacity, _material);
        foreach (var host in _widgetHosts.Values) host.Detach();
        foreach (var w in _widgets) { w.IdleChanged -= OnWidgetIdleChanged; w.Dispose(); }
        _widgets.Clear();
        _widgetHosts.Clear();
        _overflowWidgets.Clear();
        _overflowApps.Clear();
        _overflowButton = null;
        _left.Children.Clear();
        _right.Children.Clear();
        _apps.Children.Clear();
        _appButtons.Clear();

        bool pill = Settings.Shape == DockShape.Pill;
        double h = Settings.DockHeight;
        Height = h;
        FontSize = Settings.FontSize;

        _shell.Padding = new Thickness(6, 4, 6, 4);
        // Liquid glass is a true pill: the layered window clips by its own pixels, so the radius is free to be
        // half the dock's height, like the reference. The other styles keep DWM's 8 so nothing else moves.
        double radius = Theme.Current.Liquid ? Math.Max(WindowComposition.SystemCornerRadiusDip, h / 2 - 2)
                                             : WindowComposition.SystemCornerRadiusDip;
        _shell.CornerRadius = pill ? new CornerRadius(radius) : new CornerRadius(0);
        _noiseLayer.CornerRadius = _shell.CornerRadius;
        _specularLayer.CornerRadius = _shell.CornerRadius;
        _refractionLayer.CornerRadius = _shell.CornerRadius;
        _innerRim.CornerRadius = pill
            ? new CornerRadius(Math.Max(0, WindowComposition.SystemCornerRadiusDip - 1))
            : new CornerRadius(0);
        _shell.BorderThickness = pill ? new Thickness(1)
            : Settings.Edge == DockEdge.Bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
        _shell.SetResourceReference(Border.BackgroundProperty, "DockFill");
        _shell.SetResourceReference(Border.BorderBrushProperty, "DockBorder");
        _noiseLayer.SetResourceReference(Border.BackgroundProperty, "DockNoise");
        _specularLayer.SetResourceReference(Border.BackgroundProperty, "DockSpecular");
        _innerRim.SetResourceReference(Border.BorderBrushProperty, "DockInnerRim");
        ApplyLiquidGlassEffect();

        // Layout: [start, search, left widgets] [apps] [right widgets]
        _layout.Children.Clear();
        _layout.ColumnDefinitions.Clear();
        if (pill)
        {
            SizeToContent = SizeToContent.Width;
            for (int i = 0; i < 5; i++) _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddToLayout(_left, 0);
            AddToLayout(MakeDivider(), 1);
            AddToLayout(_apps, 2);
            AddToLayout(MakeDivider(), 3);
            AddToLayout(_right, 4);
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _left.HorizontalAlignment = HorizontalAlignment.Left;
            _right.HorizontalAlignment = HorizontalAlignment.Right;
            AddToLayout(_left, 0);
            AddToLayout(_apps, 1);
            AddToLayout(_right, 2);
        }

        // An extra dock on another screen: Start, its apps and the clock. Widgets and automations stay on the main dock.
        var effective = IsSecondary ? new EffectiveLayout([], ["clock"], new HashSet<string>(), [])
            : LayoutOverlay.Compose(Settings.LeftWidgets, Settings.RightWidgets, _layouts.Layers, AppClock.Now);
        if (Settings.ShowStartButton) _left.Children.Add(MakeStartButton());
        if (Settings.ShowSearchButton && !IsSecondary) _left.Children.Add(MakeShellButton("\uE721", "Search", Launcher.OpenSearch));
        foreach (var id in effective.Left) AddWidget(id, _left, effective.Emphasized.Contains(id));
        if (!IsSecondary) foreach (var suggestion in _suggestions) _left.Children.Add(MakeSuggestion(suggestion.Pin, suggestion.Reason));
        foreach (var id in effective.Right) AddWidget(id, _right, effective.Emphasized.Contains(id));
        _overflowButton = MakeShellButton("\uE712", "More apps and widgets", ShowOverflow);
        _overflowButton.Visibility = Visibility.Collapsed;
        _right.Children.Add(_overflowButton);

        ApplyBackdrop();
        RequestWindowRefresh();
        foreach (var w in _widgets) SafeTick(w);
        ScheduleResponsiveOverflow();

        if (_hwnd != IntPtr.Zero)
        {
            EnsureAppBar();
            PositionWindow();
            ApplyBackdrop();
        }
    }

    private void AddToLayout(UIElement e, int column)
    {
        Grid.SetColumn(e, column);
        _layout.Children.Add(e);
    }

    private Border MakeDivider()
    {
        var d = new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8), Opacity = 0.6 };
        d.SetResourceReference(Border.BackgroundProperty, "DockBorder");
        return d;
    }

    private void AddWidget(string id, Panel target, bool emphasized)
    {
        var info = WidgetCatalog.Find(id);
        if (info is null) return;
        string component = "widget:" + id;
        if (_app.Failures.IsQuarantined(component))
        {
            var paused = new DockButton { Content = UI.Text($"{info.Name} paused", Settings.FontSize - 1, subtle: true),
                ToolTip = "This widget was paused after repeated errors. Activate to retry.", Padding = new Thickness(8, 0, 8, 0) };
            paused.Click += (_, _) => { _app.Failures.Release(component); Rebuild(); };
            target.Children.Add(paused);
            return;
        }
        try
        {
            var w = info.Create(this);
            w.WidgetId = id;
            w.IsEmphasized = emphasized;
            _widgets.Add(w);
            var stable = new StableWidgetHost(id, Settings.Size, w);
            _widgetHosts[w] = stable;
            w.IdleChanged += OnWidgetIdleChanged;
            target.Children.Add(stable);
        }
        catch (Exception ex) { _app.Failures.Record(component, ex, AppClock.Now); }
    }

    private DockButton MakeSuggestion(PinnedApp pin, string reason)
    {
        var button = new DockButton { Content = UI.Text($"Open {pin.Name}?", Settings.FontSize, weight: FontWeights.SemiBold),
            Padding = new Thickness(9, 0, 9, 0), ToolTip = $"Suggested for {reason}" };
        button.Named($"Open {pin.Name}", $"Suggested for {reason}");
        button.Click += (_, _) => Launcher.Launch(pin);
        return button;
    }

    private DockButton MakeStartButton()
    {
        double s = Settings.IconSize * 0.62;
        var grid = new Grid { Width = s, Height = s };
        for (int i = 0; i < 4; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        grid.RowDefinitions.RemoveAt(3); grid.RowDefinitions.RemoveAt(2);
        grid.ColumnDefinitions.RemoveAt(3); grid.ColumnDefinitions.RemoveAt(2);
        var fill = new LinearGradientBrush(Color.FromRgb(0x5B, 0xB4, 0xFF), Color.FromRgb(0x2F, 0x6F, 0xEB), 45);
        fill.Freeze();
        for (int i = 0; i < 4; i++)
        {
            var sq = new Border { Background = fill, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0.9) };
            Grid.SetRow(sq, i / 2);
            Grid.SetColumn(sq, i % 2);
            grid.Children.Add(sq);
        }
        return MakeShellButton(grid, "Start", Launcher.OpenStartMenu);
    }

    private DockButton MakeShellButton(object content, string tooltip, Action click)
    {
        var child = content as UIElement ?? UI.Icon((string)content, Settings.FontSize + 4);
        var b = new DockButton
        {
            Content = child,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(10),
            ToolTip = tooltip,
        };
        b.Named(tooltip);
        b.Click += (_, _) => click();
        return b;
    }

    // ---------------- Glass, shape and position ----------------

    /// <summary>
    /// One glass, one pass: for Liquid glass the legacy overlays (noise, specular sheen, inner rim) are retired
    /// -- nine translucent layers read as a silver bar, which the owner rejected -- and the single shader on the
    /// wallpaper layer carries the lens, the light and the glass. The other styles keep every layer as shipped.
    /// </summary>
    private void ApplyLiquidGlassEffect()
    {
        bool liquid = Theme.Current.Liquid && Theme.Current.DockFill.A > 1
            && !_material.UsesSolidFallback && Effects.LiquidGlassEffect.IsSupported;
        _noiseLayer.Visibility = liquid ? Visibility.Collapsed : Visibility.Visible;
        _specularLayer.Visibility = liquid ? Visibility.Collapsed : Visibility.Visible;
        _innerRim.Visibility = liquid ? Visibility.Collapsed : Visibility.Visible;
        _specularLayer.Effect = null;
        if (!_liquidGlassHooked)
        {
            _liquidGlassHooked = true;
            _refractionLayer.SizeChanged += (_, _) => UpdateWallpaperRefraction();
        }
        UpdateWallpaperRefraction();
    }
    private bool _liquidGlassHooked;

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // Desktop covers wallpaper changes; General is where slideshow transitions land on some builds.
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => { _refractionKey = ""; UpdateWallpaperRefraction(); });
    }

    /// <summary>
    /// Real liquid glass bends what is behind it. The wallpaper is the one thing behind the dock that is knowable
    /// without capturing the screen, so its strip — softened — feeds the lens as the bottom layer, rim band only:
    /// the interior stays the live blur, the specular layer keeps the light. Loaded on geometry and wallpaper
    /// changes; nothing runs per frame.
    /// </summary>
    private string _refractionKey = "";

    private static Color FromAccentTint(uint aabbggrr) => Color.FromArgb(
        (byte)(aabbggrr >> 24), (byte)(aabbggrr & 0xFF), (byte)(aabbggrr >> 8 & 0xFF), (byte)(aabbggrr >> 16 & 0xFF));

    private void UpdateWallpaperRefraction()
    {
        // Every translucent style gets the frosted wallpaper strip -- the default Glass style's blur came from
        // DWM before the dock owned its pixels, and without this it would be a faint veil (audit F2). Liquid
        // glass keeps the full lens-and-light shader; the others are flat frosted tint.
        bool liquid = Theme.Current.Liquid && Theme.Current.DockFill.A > 1;
        bool wanted = _material.Mode == MaterialMode.NativeAcrylic
            && (liquid || !Theme.Current.Liquid)
            && Effects.LiquidGlassEffect.IsSupported && _hwnd != IntPtr.Zero
            && !PrivacyGuard.PreviewActive;
        if (!wanted || !Win32.GetWindowRect(_hwnd, out var r))
        {
            _refractionKey = "";
            _refractionLayer.Background = null;
            _refractionLayer.Effect = null;
            _refractionTint.Background = null;
            return;
        }
        var mon = _monitorGeometry.Bounds(_monitor);
        // Rebuilding costs registry reads, file stats and three bitmaps; callers fire on every move, poll and
        // preference change, so identical geometry is skipped. A wallpaper change clears the key (audit F7); the
        // monitor origin and radius are part of the key so display-layout changes refresh too (audit F12).
        string key = $"{r.Left},{r.Top},{r.Right},{r.Bottom}|{mon.Left},{mon.Top}|{_refractionLayer.ActualWidth:0},{_refractionLayer.ActualHeight:0}|{_shell.CornerRadius.TopLeft:0}|{Settings.GlassOpacity}|{Theme.Current.Name}";
        if (key == _refractionKey && _refractionLayer.Background is not null) return;
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        var strip = _wallpaperGlass.GetStrip(
            new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)),
            new Rect(mon.Left, mon.Top, mon.Width, mon.Height),
            new Rect(vs.X, vs.Y, vs.Width, vs.Height));
        if (strip is null)
        {
            _refractionKey = "";
            _refractionLayer.Background = null;
            _refractionLayer.Effect = null;
            _refractionTint.Background = null;
            return;
        }
        _refractionKey = key;
        _refractionLayer.Background = new ImageBrush(strip) { Stretch = Stretch.Fill };
        _refractionTint.CornerRadius = _refractionLayer.CornerRadius;
        if (liquid)
        {
            _refractionTint.Background = null; // the milk gradient in the shell fill is Liquid's only wash
            _refractionEffect ??= new Effects.LiquidGlassEffect();
            // Full mode: the creamy wallpaper is the whole glass -- lens at the rim, light, interior intact.
            // The layer sits one pixel inside the shell border, so the outline radius follows it (audit F12).
            var p = Effects.LiquidGlassParameters.For(Settings.DockHeight, Settings.GlassOpacity);
            _refractionEffect.Apply(p, _refractionLayer.ActualWidth, _refractionLayer.ActualHeight,
                Math.Max(0, _shell.CornerRadius.TopLeft - 1));
            _refractionEffect.RimOnly = 0.0;
            _refractionLayer.Effect = _refractionEffect;
        }
        else
        {
            // Flat frosted tint: the palette's AABBGGRR acrylic tint, painted by the app over its own frost.
            _refractionLayer.Effect = null;
            var tint = FromAccentTint(Theme.Current.AcrylicTint);
            _refractionTint.Background = tint.A > 0 ? new SolidColorBrush(tint) : null;
        }
    }

    private void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowComposition.Apply(_hwnd, rounded: Settings.Shape == DockShape.Pill,
            darkMode: !Theme.Current.IsLight && !Theme.Current.LightBackdrop,
            systemBackdrop: _material.UseSystemBackdrop,
            accentTint: Theme.Current.AcrylicTint,
            transparentGlass: _material.Mode == MaterialMode.Transparent,
            glassWindow: true);
    }

    private double Scale => _hwnd == IntPtr.Zero ? 1 : _monitorGeometry.ScaleForWindow(_hwnd);

    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        DockPlacement placement = DockPlacementPlanner.Calculate(_monitorGeometry.Bounds(_monitor), Scale,
            Settings.DockHeight, ActualWidth, PillGap, Settings.Shape, Settings.Edge);
        DockDimensionContract dimensions = DockPlacementPlanner.Dimensions(placement);
        if (double.IsNaN(dimensions.WidthDip)) Width = double.NaN;
        else if (double.IsNaN(Width) || Math.Abs(Width - dimensions.WidthDip) > 0.5) Width = dimensions.WidthDip;
        MaxWidth = dimensions.MaxWidthDip;
        _monitorGeometry.PlaceTopmost(_hwnd, placement.X, placement.Y);
        // The dock moved: the strip of wallpaper behind it is different now.
        UpdateWallpaperRefraction();
    }

    // ---------------- AppBar: reserve screen space like the real taskbar ----------------

    private Win32.APPBARDATA NewAppBarData() => new()
    {
        cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
        hWnd = _hwnd,
        uCallbackMessage = _appBarMessage,
    };

    /// <summary>
    /// Register and reserve (when space reservation is on) through <see cref="AppBarSafety"/>: a failure restores the
    /// Windows taskbar with the AppBar blocker, and only a verified register + reserve clears it. A visible dock does not.
    /// </summary>
    private bool EnsureAppBar()
    {
        // SourceInitialized runs before Show(): a nonzero HWND alone is not a ready AppBar owner.
        if (_hwnd == IntPtr.Zero || !Win32.IsWindow(_hwnd) || !Win32.IsWindowVisible(_hwnd)) return false;
        var (bottom, mon, heightPx) = AppBarGeometry();
        bool ok = _appBarSafety.Ensure(_hwnd, _appBarMessage, Settings.ReserveSpace, bottom, mon, heightPx);
        _app.TaskbarShown = !_app.Taskbar.IsHidingOrAutoHiding;
        return ok;
    }

    /// <summary>One privacy-safe readiness observation: native visibility, physical bounds, and AppBar result.</summary>
    internal DockReadinessSample ObserveReadiness()
    {
        if (_hwnd == IntPtr.Zero) return default;
        PositionWindow();
        bool nativeDockReady = IsVisible && Win32.IsWindow(_hwnd) && Win32.IsWindowVisible(_hwnd);
        if (nativeDockReady && !_nativeDockWasReady)
            _appBarSafety.RearmRegistration(AppBarReadinessTransition.DockBecameReady);
        _nativeDockWasReady = nativeDockReady;
        bool appBarReady = nativeDockReady && EnsureAppBar();
        double scale = Scale;
        return new DockReadinessSample(
            nativeDockReady,
            (int)Math.Round(ActualWidth * scale),
            (int)Math.Round(ActualHeight * scale),
            appBarReady);
    }

    public void UnregisterAppBar()
    {
        _appBarSafety.AppBar.Unregister(_hwnd);
    }

    private bool SetAppBarPos()
    {
        if (_hwnd == IntPtr.Zero) return false;
        var (bottom, mon, heightPx) = AppBarGeometry();
        bool ok = _appBarSafety.Reserve(_hwnd, Settings.ReserveSpace, bottom, mon, heightPx);
        _app.TaskbarShown = !_app.Taskbar.IsHidingOrAutoHiding;
        return ok;
    }

    private (bool Bottom, Win32.RECT Monitor, int HeightPx) AppBarGeometry()
    {
        PixelBounds bounds = _monitorGeometry.Bounds(_monitor);
        return (Settings.Edge == DockEdge.Bottom,
            new Win32.RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
            DockPlacementPlanner.AppBarHeight(Settings.DockHeight, PillGap, Settings.Shape, Scale));
    }

    private bool PreserveTaskbarAccess(TaskbarSafetyBlockers reason)
    {
        bool restored = _app.Taskbar.RestoreForSafety(reason);
        _app.TaskbarShown = !_app.Taskbar.IsHidingOrAutoHiding;
        return restored;
    }

    private void OnDockSafetyHealthy()
    {
        if (!_app.Taskbar.ResumeAfterDockRecovery()) return;
        _app.TaskbarShown = !_app.Taskbar.IsHidingOrAutoHiding;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == KeyboardHotKeyId)
        {
            Dispatcher.BeginInvoke(FocusDockForKeyboard);
            handled = true;
        }
        else if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == CommandPaletteHotKeyId)
        {
            Dispatcher.BeginInvoke(ShowCommandPalette);
            handled = true;
        }
        else if (msg == Win32.WM_HOTKEY && AppHotkeys.IndexFor(wParam.ToInt32()) is int app)
        {
            Dispatcher.BeginInvoke(() => { if (app < _appButtons.Count) { DismissPreview(); OnAppClick(_appButtons[app]); } });
            handled = true;
        }
        if (_appBarMessage != 0 && msg == (int)_appBarMessage)
        {
            switch (wParam.ToInt32())
            {
                case Win32.ABN_POSCHANGED: SetAppBarPos(); PositionWindow(); break;
                case Win32.ABN_FULLSCREENAPP: SetFullscreenHidden(lParam != IntPtr.Zero); break;
            }
            handled = true;
        }
        else if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted: re-register the AppBar, but preserve any taskbar safety suspension.
            _app.BeginExplorerReapplication();
            var (bottom, mon, heightPx) = AppBarGeometry();
            _appBarSafety.OnExplorerRestarted(_hwnd, _appBarMessage, Settings.ReserveSpace, bottom, mon, heightPx);
            _app.TaskbarShown = !_app.Taskbar.IsHidingOrAutoHiding;
            _taskbarSafety.Rearm();
            PositionWindow();
            _app.ObserveDockReadiness();
        }
        else if (msg is 0x007E /* WM_DISPLAYCHANGE */ or 0x02E0 /* WM_DPICHANGED */)
        {
            Dispatcher.BeginInvoke(() =>
            {
                bool needsRegistrationRecovery = !_appBarSafety.AppBar.Registered &&
                    (_appBarSafety.AppBar.RegistrationAttempts > 0 || _appBarSafety.AppBar.RegistrationSuspended);
                if (needsRegistrationRecovery)
                {
                    _appBarSafety.RearmRegistration(AppBarReadinessTransition.DpiOrDisplayChanged);
                    _app.BeginAppBarReapplication();
                }
                IconCache.Invalidate();
                Rebuild();
                SetAppBarPos();
                PositionWindow();
                ApplyBackdrop();
                if (needsRegistrationRecovery) _app.ObserveDockReadiness();
            }, DispatcherPriority.Background);
        }
        else if (msg is (int)Win32.WM_SETTINGCHANGE or (int)Win32.WM_THEMECHANGED or (int)Win32.WM_DWMCOMPOSITIONCHANGED ||
                 msg == (int)Win32.WM_POWERBROADCAST && wParam.ToInt32() == Win32.PBT_APMPOWERSTATUSCHANGE)
        {
            // Re-evaluate accessibility, transparency, compositor, session and Battery Saver fallbacks in place.
            Dispatcher.BeginInvoke(Rebuild, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void SetFullscreenHidden(bool hidden)
    {
        if (_fullscreenHidden == hidden) return;
        _fullscreenHidden = hidden;
        if (hidden) { CloseFlyout(); ClosePreview(); Win32.ShowWindow(_hwnd, Win32.SW_HIDE); }
        else { Win32.ShowWindow(_hwnd, Win32.SW_SHOWNA); PositionWindow(); }
    }

    /// <summary>Win+Alt+S: enter keyboard mode on the first dock item.</summary>
    private void FocusDockForKeyboard()
    {
        FocusDockForKeyboard(null);
        Commands.Write("dock.focus", ("source", "hotkey"), ("item", CommandItem(Keyboard.FocusedElement as FrameworkElement)));
    }

    private void FocusDockForKeyboard(DockButton? target)
    {
        if (_hwnd == IntPtr.Zero) return;
        _keyboardMode = true;
        long ex = Win32.GetExStyle(_hwnd) & ~Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Activate();
        (target ?? FindDockButtons().FirstOrDefault(b => b.IsVisible && b.IsEnabled))?.Focus();
    }

    /// <summary>A flyout or menu opened in keyboard mode has started closing: put focus back on the dock if it should.</summary>
    private void ReturnKeyboardFocus(FlyoutWindow closing, DockButton? opener, FlyoutCloseReason reason)
    {
        Commands.Write("flyout.close", ("item", CommandItem(opener)), ("reason", reason.ToString().ToLowerInvariant()));
        void Return()
        {
            if (_flyout is not null && _flyout != closing) // never fight a flyout that is now open
            {
                Commands.Write("focus.return", ("opener", CommandItem(opener)), ("target", "none"), ("result", "other-flyout-open"));
                return;
            }
            // Anything but the closing flyout or the dock in front (another app, Settings) means the action went there.
            IntPtr fg = Win32.GetForegroundWindow();
            bool movedElsewhere = fg != IntPtr.Zero && fg != _hwnd && fg != new WindowInteropHelper(closing).Handle;
            var target = KeyboardFocusReturn.Target(openedInKeyboardMode: true, reason, movedElsewhere, opener,
                FindDockButtons().Where(b => b.IsVisible && b.IsEnabled).ToList());
            if (target is not null) FocusDockForKeyboard(target);
            Commands.Write("focus.return", ("opener", CommandItem(opener)), ("target", CommandItem(target)),
                ("result", target is null ? "not-returned" : target.IsKeyboardFocused ? "focused" : "not-focused"));
        }
        // Escape returns at once, while Strata still owns the foreground. Deferred to Background it would run only
        // after the fade-out (animation frames outrank it), when Windows has already activated another app and
        // refuses the switch back. A menu action runs first, at Normal priority, so its own window can take over.
        if (reason == FlyoutCloseReason.Escape) Return();
        else Dispatcher.BeginInvoke(Return, DispatcherPriority.Normal);
    }

    private void ExitKeyboardMode(string reason)
    {
        if (!_keyboardMode || _hwnd == IntPtr.Zero) return;
        _keyboardMode = false;
        Commands.Write("keyboard.exit", ("reason", reason));
        long ex = Win32.GetExStyle(_hwnd) | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    private List<DockButton> FindDockButtons()
    {
        var result = new List<DockButton>();
        void Walk(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is DockButton button) result.Add(button); else Walk(child);
            }
        }
        Walk(_layout);
        return result;
    }

    private void OnDockKeyDown(object sender, KeyEventArgs e)
    {
        if (!_keyboardMode) return;
        if (e.Key == Key.Escape) { CloseFlyout(); ExitKeyboardMode("escape"); e.Handled = true; return; }
        if (DockKeyboard.IsContextMenu(e.Key, e.SystemKey, Keyboard.Modifiers))
        {
            Commands.Write("contextmenu", ("item", CommandItem(Keyboard.FocusedElement as FrameworkElement)));
            if (Keyboard.FocusedElement is AppButton app) ShowAppMenu(app, fromKeyboard: true);
            else ShowDockMenu(Keyboard.FocusedElement as FrameworkElement, fromKeyboard: true);
            e.Handled = true;
            return;
        }
        int direction = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
        if (direction == 0) return;
        var buttons = FindDockButtons().Where(b => b.IsVisible && b.IsEnabled).ToList();
        int current = buttons.IndexOf(Keyboard.FocusedElement as DockButton ?? buttons.FirstOrDefault()!);
        string from = CommandItem(Keyboard.FocusedElement as FrameworkElement);
        if (buttons.Count > 0) buttons[(Math.Max(0, current) + direction + buttons.Count) % buttons.Count].Focus();
        Commands.Write("nav", ("dir", direction > 0 ? "next" : "prev"), ("from", from), ("to", CommandItem(Keyboard.FocusedElement as FrameworkElement)));
        e.Handled = true;
    }

    private static PhaseBCommandLog Commands => PhaseBCommandLog.Current;

    /// <summary>A dock item for the Phase B command log: its position and role, never its name.</summary>
    private string CommandItem(FrameworkElement? element)
    {
        if (!Commands.Enabled) return "";
        int index = element is DockButton button ? FindDockButtons().Where(b => b.IsVisible && b.IsEnabled).ToList().IndexOf(button) : -1;
        string role = element switch
        {
            AppButton => "app",
            Widget w => "widget:" + w.WidgetId,
            DockButton d when AutomationProperties.GetName(d) == "Start" => "start",
            DockButton d when AutomationProperties.GetName(d) == "Search" => "search",
            _ => "other",
        };
        return PhaseBCommandLog.Item(index, role);
    }

    /// <summary>Fallback for when space is not reserved: hide while a fullscreen window covers the screen.</summary>
    private void CheckFullscreen()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || (_flyout is not null && new WindowInteropHelper(_flyout).Handle == fg)) return;
        string cls = Win32.GetClassNameOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") { SetFullscreenHidden(false); return; }
        var mon = Win32.GetMonitor(_monitor).rcMonitor;
        bool covers = Win32.GetWindowRect(fg, out var r) && r.Left <= mon.Left && r.Top <= mon.Top
                      && r.Right >= mon.Right && r.Bottom >= mon.Bottom
                      && Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST) == Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        SetFullscreenHidden(covers);
    }

    // ---------------- Apps and windows ----------------

    private void RequestWindowRefresh()
    {
        if (PrivacyGuard.PreviewActive) RefreshWindows(force: true);
        else _app.RefreshWindowSnapshot(force: true);
    }

    private void RefreshWindows(bool force)
    {
        if (_dragMoving) return;   // the dock's order is the user's while they drag; refresh when they drop
        bool preview = PrivacyGuard.PreviewActive;
        bool changed = preview;
        IntPtr fg = preview ? IntPtr.Zero : Win32.GetForegroundWindow();
        bool regroup = RequiresWindowRegroup(changed, force, preview, Settings.DockOnEveryScreen, _hwnd != IntPtr.Zero);
        if (!regroup)
        {
            if (fg != _lastForeground)
            {
                _lastForeground = fg;
                foreach (var b in _appButtons) b.UpdateActive(fg);
            }
            return;
        }
        _lastForeground = fg;

        var windows = preview ? new List<TrackedWindow>() : _tracker.Windows.ToList();
        // With a dock on every screen, each lists the windows on its own screen.
        if (Settings.DockOnEveryScreen && !preview && _hwnd != IntPtr.Zero)
            windows = OnScreen(windows, _monitorGeometry.MonitorForWindow,
                _monitorGeometry.MonitorForWindow(_hwnd)).ToList();
        var buttons = new List<AppButton>();
        var existing = new Dictionary<string, Queue<AppButton>>();
        foreach (var old in _appButtons)
        {
            if (!existing.TryGetValue(old.Key, out var q)) existing[old.Key] = q = new Queue<AppButton>();
            q.Enqueue(old);
        }

        AppButton Reuse(PinnedApp? pin, AppIdentity? id)
        {
            string key = AppButton.KeyFor(pin, id);
            if (existing.TryGetValue(key, out var q) && q.Count > 0) return q.Dequeue();
            return new AppButton(pin, id, Settings.IconSize, Settings.Edge == DockEdge.Top);
        }

        foreach (var pin in Settings.Pinned)
        {
            // A group owns its members' windows: its tile carries their indicator, badge and previews, and the
            // members never reappear as separate running buttons at the end of the dock (audit F5).
            var mine = DockGroups.IsGroup(pin)
                ? windows.Where(w => pin.Group!.Any(m => m is not null && w.App.Matches(m))).ToList()
                : windows.Where(w => w.App.Matches(pin)).ToList();
            windows.RemoveAll(mine.Contains);
            var b = Reuse(pin, mine.FirstOrDefault()?.App);
            b.SetWindows(mine, fg, Settings.UnreadBadges);
            buttons.Add(b);
        }
        foreach (var group in windows.GroupBy(w => w.App.Key))
        {
            var b = Reuse(null, group.First().App);
            b.SetWindows(group, fg, Settings.UnreadBadges);
            buttons.Add(b);
        }

        if (!buttons.SequenceEqual(_appButtons))
        {
            _apps.Children.Clear();
            foreach (var b in buttons)
            {
                if (b.Tag is null)
                {
                    b.Tag = "wired";
                    b.Click += (_, _) => { if (_suppressClick) { _suppressClick = false; return; } DismissPreview(); OnAppClick(b); };
                    b.PreviewMouseLeftButtonDown += (_, e) => { _dragApp = b; _dragStart = e.GetPosition(_apps); _dragMoving = false; };
                    b.PreviewMouseMove += (_, e) => DragApp(b, e);
                    b.PreviewMouseLeftButtonUp += (_, _) => DropApp(b);
                    b.LostMouseCapture += (_, _) => { if (ReferenceEquals(_dragApp, b) && _dragMoving) DropApp(b); };
                    b.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; DismissPreview(); OpenNew(b); } };
                    b.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; DismissPreview(); ShowAppMenu(b); };
                    b.FilesDropped += files => { DismissPreview(); OpenFilesWith(b, files); };
                    b.DragRested += () => { if (b.Windows.Count > 0) Win32.ActivateWindow(b.Windows[^1].Handle); };
                    b.Scrolled += delta => { DismissPreview(); StepWindows(b, delta); };
                    b.MouseEnter += (_, _) => { _hover.Enter(b, DateTime.Now); UpdatePreview(); };
                    b.MouseLeave += (_, _) => { _hover.Leave(b, DateTime.Now); UpdatePreview(); };
                }
                _apps.Children.Add(b);
            }
            _appButtons.Clear();
            _appButtons.AddRange(buttons);
        }
        for (int i = 0; i < _appButtons.Count; i++)
            _appButtons[i].Shortcut = Settings.AppShortcuts && (_appHotkeys?.Registered ?? 0) > 0 ? AppHotkeys.Label(i) : null;
        ScheduleResponsiveOverflow();
    }

    // ---------------- Responsive overflow ----------------

    private void ScheduleResponsiveOverflow()
    {
        if (_overflowScheduled || _overflowButton is null) return;
        _overflowScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _overflowScheduled = false;
            ApplyResponsiveOverflow();
        }, DispatcherPriority.ContextIdle);
    }

    private void ApplyResponsiveOverflow()
    {
        if (_overflowButton is null || _hwnd == IntPtr.Zero) return;
        foreach (var host in _widgetHosts.Values) host.SetOverflowed(false);
        foreach (var app in _appButtons) app.Visibility = Visibility.Visible;
        _overflowWidgets.Clear();
        _overflowApps.Clear();
        _overflowButton.Visibility = Visibility.Collapsed;

        _layout.Measure(new Size(double.PositiveInfinity, Height));
        double maximum = _monitorGeometry.Bounds(_monitor).Width / Scale - PillGap * 4;
        double overflowWidth = Settings.IconSize + 24;
        double need = _layout.DesiredSize.Width - maximum;
        if (need <= 0) return;
        need += overflowWidth;

        static int WidgetPriority(Widget widget)
        {
            if (widget.WidgetId == "clock") return 50;
            return WidgetManifest.Find(widget.WidgetId)?.Role switch
            {
                WidgetRole.Tool => 0,
                WidgetRole.Distracting => 10,
                WidgetRole.Status => 20,
                _ => 40,
            };
        }

        var candidates = _widgets.Where(_widgetHosts.ContainsKey)
            .Select(w => new DockOverflowCandidate("widget:" + w.WidgetId,
                _widgetHosts[w].ActualWidth > 0 ? _widgetHosts[w].ActualWidth : _widgetHosts[w].Width,
                WidgetPriority(w)))
            .Concat(_appButtons.Skip(1).Select((a, i) => new DockOverflowCandidate("app:" + i,
                a.ActualWidth > 0 ? a.ActualWidth : Settings.IconSize + 14, 60 + i)))
            .ToList();
        var selected = DockOverflowPlanner.Select(need, candidates).ToHashSet(StringComparer.Ordinal);
        foreach (var widget in _widgets)
        {
            if (!selected.Contains("widget:" + widget.WidgetId) || !_widgetHosts.TryGetValue(widget, out var host)) continue;
            host.SetOverflowed(true);
            _overflowWidgets.Add(widget);
        }
        for (int i = 1; i < _appButtons.Count; i++)
        {
            if (!selected.Contains("app:" + (i - 1))) continue;
            _appButtons[i].Visibility = Visibility.Collapsed;
            _overflowApps.Add(_appButtons[i]);
        }
        _overflowButton.Visibility = _overflowWidgets.Count + _overflowApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverflow()
    {
        if (_overflowButton is null) return;
        var column = UI.Column(6, UI.Header("More"));
        if (_overflowApps.Count > 0)
        {
            column.Children.Add(UI.Text("Apps", 11.5, subtle: true, flyout: true, weight: FontWeights.SemiBold));
            foreach (var app in _overflowApps)
            {
                var captured = app;
                string status = captured.Windows.Count == 0 ? "Pinned app" : $"{captured.Windows.Count} open window{(captured.Windows.Count == 1 ? "" : "s")}";
                var button = UI.TextButton(captured.DisplayName, () => { CloseFlyout(); OnAppClick(captured); });
                button.Named(captured.DisplayName, status);
                column.Children.Add(button);
            }
        }
        if (_overflowWidgets.Count > 0)
        {
            column.Children.Add(UI.Text("Widgets", 11.5, subtle: true, flyout: true, weight: FontWeights.SemiBold));
            foreach (var widget in _overflowWidgets)
            {
                var captured = widget;
                string name = AutomationProperties.GetName(captured);
                string status = AutomationProperties.GetItemStatus(captured);
                string label = string.IsNullOrWhiteSpace(name) ? WidgetManifest.Find(captured.WidgetId)?.Name ?? "Widget" : name;
                var button = UI.TextButton(label,
                    () =>
                    {
                        CloseFlyout();
                        if (captured.PreviewFlyout() is { } content)
                            ShowFlyout(_overflowButton, content, captured.PreviewFlyoutWidth, fromKeyboard: true);
                        else
                            ShowFlyout(_overflowButton, UI.Column(6, UI.Header(label), UI.Text(status, 12.5, flyout: true)), 260, fromKeyboard: true);
                    });
                button.Named(label, status);
                column.Children.Add(button);
            }
        }
        double monitorHeightDip = _monitorGeometry.Bounds(_monitor).Height / Scale;
        ShowFlyout(_overflowButton, CreateOverflowScroller(column, monitorHeightDip), 300, fromKeyboard: _keyboardMode);
    }

    /// <summary>Bounds large overflow catalogs to the current monitor while leaving every child in the tab order.</summary>
    public static ScrollViewer CreateOverflowScroller(FrameworkElement content, double monitorHeightDip)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(64, Math.Min(560, monitorHeightDip - 96)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            IsTabStop = false,
            PanningMode = PanningMode.VerticalOnly,
        };
        scroll.Named("More apps and widgets", "Scroll to reach every overflowed dock item. Tab moves through the available commands.");
        return scroll;
    }

    // ---------------- Drag to reorder and pin ----------------

    /// <summary>The dragged app follows the pointer; the apps it passes slide aside to show where it will land.</summary>
    private void DragApp(AppButton b, MouseEventArgs e)
    {
        if (!ReferenceEquals(_dragApp, b) || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_apps);
        if (!_dragMoving)
        {
            // Past a few pixels it's a drag, not a click (a click may wobble slightly).
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 3) return;
            _dragMoving = true;
            DismissPreview();
            _dragFrom = _dragSlot = _apps.Children.IndexOf(b);
            var others = _apps.Children.OfType<FrameworkElement>().Where(c => !ReferenceEquals(c, b)).ToList();
            _dragCentres = others.Select(c => c.TranslatePoint(new Point(c.ActualWidth / 2, 0), _apps).X).ToList();
            _dragWidths = others.Select(c => c.ActualWidth).ToList();
            b.Opacity = 0.8;
            Panel.SetZIndex(b, 1);
        }
        Shift(b, pos.X - _dragStart.X, animate: false);
        _dragX = pos.X;
        int slot = DockOrder.InsertIndex(_dragCentres, pos.X);
        // When the release would make a group, the target dims so the drop is never a surprise (audit F1).
        int? zone = DockGroups.DropOnto(_dragCentres, _dragWidths, pos.X, _dragFrom, slot);
        if (zone != _groupZone)
        {
            SetGroupZoneDimmed(_groupZone, false);
            // Only a pinned target can host a group; running-only buttons never dim.
            _groupZone = zone is int z && (z < _dragFrom ? z : z + 1) < Settings.Pinned.Count ? zone : null;
            SetGroupZoneDimmed(_groupZone, true);
        }
        if (slot == _dragSlot) return;
        _dragSlot = slot;
        double width = b.ActualWidth + b.Margin.Left + b.Margin.Right;
        int k = 0;
        foreach (var other in _apps.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(other, b)) continue;
            double by = k >= _dragFrom && k < slot ? -width : k >= slot && k < _dragFrom ? width : 0;
            Shift(other, by, animate: _material.UseMotion);
            k++;
        }
    }

    private static void Shift(FrameworkElement e, double x, bool animate)
    {
        if (e.RenderTransform is not TranslateTransform t) e.RenderTransform = t = new TranslateTransform();
        if (!animate) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = x; return; }
        t.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(x, TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>End a drag: save the new pin order (or the new pin), or put everything back when nothing changed.</summary>
    private void DropApp(AppButton b)
    {
        if (!ReferenceEquals(_dragApp, b)) return;
        _dragApp = null;
        if (!_dragMoving) return;
        _dragMoving = false;
        _suppressClick = true;   // the release ends the drag; it is not a click on the app
        Dispatcher.BeginInvoke(() => _suppressClick = false, DispatcherPriority.Background);
        foreach (var other in _apps.Children.OfType<FrameworkElement>())
        {
            if (other.RenderTransform is TranslateTransform t) t.BeginAnimation(TranslateTransform.XProperty, null);
            other.RenderTransform = Transform.Identity;
            other.Opacity = 1;
        }
        _groupZone = null;
        b.Opacity = 1;
        Panel.SetZIndex(b, 0);
        var identity = b.Identity;
        // Released on another pinned button's centre: that is a drop ONTO it, and onto makes a group. Released
        // between buttons: the reorder it always was.
        if (DockGroups.DropOnto(_dragCentres, _dragWidths, _dragX, _dragFrom, _dragSlot) is int onto)
        {
            int childIndex = onto < _dragFrom ? onto : onto + 1; // the centres exclude the dragged button
            if (childIndex < Settings.Pinned.Count)
            {
                var target = Settings.Pinned[childIndex];
                var dragged = b.Pin ?? (identity is not null ? Launcher.FromRunning(identity) : null);
                if (dragged is not null && DockGroups.MergeInto(Settings.Pinned, dragged, target, DockGroups.NameFor(target, dragged)))
                {
                    SaveAndRefresh();
                    return;
                }
            }
        }
        bool changed = DockOrder.Apply(Settings.Pinned, b.Pin,
            b.Pin is null && identity is not null ? () => Launcher.FromRunning(identity) : null, _dragSlot);
        if (changed) SaveAndRefresh();
        else RequestWindowRefresh();
    }

    /// <summary>
    /// A widget took its other declared width (<see cref="Widget.IsIdle"/>). A pill dock resizes, and its own
    /// SizeChanged re-plans the overflow and moves any open flyout; a full-width dock is pinned to the monitor and
    /// raises no SizeChanged at all, so its plan is re-run from here or widgets could stay clipped.
    /// </summary>
    private void OnWidgetIdleChanged() => ScheduleResponsiveOverflow();

    private void OnAppClick(AppButton b)
    {
        if (b.Pin?.Group is { Count: > 0 } members)
        {
            // A group tile opens its folder: a running member is focused, anything else is one tap to launch.
            var column = UI.Column(6, UI.Header(b.DisplayName));
            foreach (var app in members)
            {
                if (app is null) continue;
                var captured = app;
                column.Children.Add(UI.TextButton(captured.Name is { Length: > 0 } n ? n : "App", () =>
                {
                    CloseFlyout();
                    var window = b.Windows.LastOrDefault(w => w.App.Matches(captured));
                    if (window is not null) Win32.ActivateWindow(window.Handle);
                    else Launcher.Launch(captured);
                }));
            }
            // An overflowed tile is collapsed, so its flyout anchors on the overflow button instead (audit F7).
            ShowFlyout(b.IsVisible ? b : _overflowButton ?? (DockButton)b, column, 260, fromKeyboard: _keyboardMode);
            return;
        }
        AppClickDecision decision = DecideNormalAppClick(b.Windows, _lastForeground, Win32.IsIconic);
        if (decision.Kind == AppClickKind.LaunchPin)
        {
            if (b.Pin is not null) ClickMissingPin(b.Pin);
            return;
        }
        if (decision.Kind == AppClickKind.MinimizeWindow) Win32.ShowWindow(decision.Window, Win32.SW_MINIMIZE);
        else Win32.ActivateWindow(decision.Window);
        Dispatcher.BeginInvoke(RequestWindowRefresh, DispatcherPriority.Background);
    }

    /// <summary>Normal click can launch only when no existing window is attached; grouped windows always win.</summary>
    public static AppClickDecision DecideNormalAppClick(IReadOnlyList<TrackedWindow> windows, IntPtr foreground,
        Func<IntPtr, bool> isMinimized)
    {
        if (windows.Count == 0) return new(AppClickKind.LaunchPin, IntPtr.Zero);
        int current = -1;
        for (int i = 0; i < windows.Count; i++) if (windows[i].Handle == foreground) { current = i; break; }
        if (windows.Count == 1)
        {
            IntPtr window = windows[0].Handle;
            return current == 0 && !isMinimized(window)
                ? new(AppClickKind.MinimizeWindow, window)
                : new(AppClickKind.ActivateWindow, window);
        }
        TrackedWindow next = current < 0 ? windows[^1] : windows[(current + 1) % windows.Count];
        return new(AppClickKind.ActivateWindow, next.Handle);
    }

    /// <summary>What a normal click does with a pin that has no window of its own on this dock.</summary>
    public enum MissingPinClick { Launch, ExplorerTrackingUnavailable, ExplorerWindowElsewhere, ExplorerLaunchPending }

    /// <summary>How long a folder window is given to appear before another click may open one. The window list is
    /// re-read every 400 ms and Explorer takes longer than that to show a window, so without this an impatient
    /// second click (or a double-click) opens a second window.</summary>
    public static readonly TimeSpan ExplorerLaunchSettle = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// A normal click on a pin with no window of its own opens the app. File Explorer has three extra conditions,
    /// all of them the same rule: never add a folder window beside one the owner already has.
    /// Only an authoritative snapshot proves there is none to activate; <paramref name="folderWindowsAnywhere"/> is
    /// counted across every screen, because this dock's own list is filtered to its monitor and the window may be on
    /// another one (or on another virtual desktop); and a launch already asked for has to be given time to appear.
    /// </summary>
    public static MissingPinClick DecideMissingPinClick(AppIdentityKind identityKind, bool explorerEnumerationAvailable,
        int folderWindowsAnywhere, TimeSpan sinceExplorerLaunch)
    {
        if (identityKind != AppIdentityKind.ExplorerFolder) return MissingPinClick.Launch;
        if (!explorerEnumerationAvailable) return MissingPinClick.ExplorerTrackingUnavailable;
        if (folderWindowsAnywhere > 0) return MissingPinClick.ExplorerWindowElsewhere;
        if (sinceExplorerLaunch < ExplorerLaunchSettle) return MissingPinClick.ExplorerLaunchPending;
        return MissingPinClick.Launch;
    }

    private DateTime _explorerLaunchedAt = DateTime.MinValue;

    private void ClickMissingPin(PinnedApp pin)
    {
        int folderWindows = pin.IdentityKind == AppIdentityKind.ExplorerFolder
            ? _tracker.Windows.Count(w => w.App.IdentityKind == AppIdentityKind.ExplorerFolder) : 0;
        switch (DecideMissingPinClick(pin.IdentityKind, _tracker.ExplorerEnumerationAvailable, folderWindows,
                                      AppClock.Now - _explorerLaunchedAt))
        {
            case MissingPinClick.Launch:
                if (pin.IdentityKind == AppIdentityKind.ExplorerFolder) _explorerLaunchedAt = AppClock.Now;
                Launcher.Launch(pin);
                break;
            case MissingPinClick.ExplorerTrackingUnavailable:
                _app.Notify("explorer-tracking-unavailable-on-click", "File Explorer window tracking is unavailable",
                    "Strata cannot see which folder windows are open, so a click will not open one in case it is already there. Use New window if that is what you want.",
                    urgent: true);
                break;
            // Nothing to say in either of these: the owner has a folder window already, here in a moment or on
            // another screen, and the dock that holds it can bring it forward.
            case MissingPinClick.ExplorerWindowElsewhere:
            case MissingPinClick.ExplorerLaunchPending:
                break;
        }
    }

    // ---------------- Window previews ----------------

    private void UpdatePreview()
    {
        ShowPreviewFor(_hover.Update(DateTime.Now));
        if (_hover.Pending) _hoverTimer.Start(); else _hoverTimer.Stop();
    }

    private void DismissPreview()
    {
        _hover.Dismiss();
        ClosePreview();
        _hoverTimer.Stop();
    }

    private void ShowPreviewFor(AppButton? b)
    {
        // No previews over an open flyout or while the dock is driven from the keyboard.
        if (b is null || b.Windows.Count == 0 || _flyout is not null || _keyboardMode) { ClosePreview(); return; }
        if (_preview is not null && ReferenceEquals(_preview.App, b)) return;
        ClosePreview();
        var preview = new WindowPreview(b, b.Windows.ToList(), below: Settings.Edge == DockEdge.Top,
            activate: h => { DismissPreview(); Win32.ActivateWindow(h); Dispatcher.BeginInvoke(RequestWindowRefresh, DispatcherPriority.Background); },
            close: h => CloseFromPreview(b, h));
        preview.MouseEnter += (_, _) => { _hover.EnterPreview(); UpdatePreview(); };
        preview.MouseLeave += (_, _) => { _hover.LeavePreview(DateTime.Now); UpdatePreview(); };
        _preview = preview;
        preview.Show();
    }

    /// <summary>Close one window from its preview tile, then show the previews again without it.</summary>
    private void CloseFromPreview(AppButton b, IntPtr window)
    {
        Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            RequestWindowRefresh();
            if (_preview is not null && ReferenceEquals(_preview.App, b)) { ClosePreview(); ShowPreviewFor(b); }
        };
        later.Start();
    }

    private void ClosePreview()
    {
        var p = _preview;
        _preview = null;
        p?.Close();
    }

    /// <summary>The windows on one screen (<paramref name="screen"/>, an HMONITOR).</summary>
    public static IEnumerable<TrackedWindow> OnScreen(IEnumerable<TrackedWindow> windows, Func<IntPtr, IntPtr> monitorOf, IntPtr screen) =>
        windows.Where(w => monitorOf(w.Handle) == screen);

    /// <summary>
    /// Per-monitor membership is live state even when the window set itself is unchanged. Rebuild those groups on
    /// each bounded tracker refresh so dragging a window across monitors cannot leave a stale count on either dock.
    /// </summary>
    public static bool RequiresWindowRegroup(bool sourceChanged, bool force, bool preview, bool dockOnEveryScreen, bool dockHandleReady) =>
        sourceChanged || force || preview || (dockOnEveryScreen && dockHandleReady);

    /// <summary>Scroll over an app: switch to its next (or previous) window without opening the previews.</summary>
    private void StepWindows(AppButton b, int delta)
    {
        int current = b.Windows.FindIndex(w => w.Handle == _lastForeground);
        int next = AppButton.NextWindowIndex(b.Windows.Count, current, delta);
        if (next < 0) return;
        Win32.ActivateWindow(b.Windows[next].Handle);
        _lastForeground = b.Windows[next].Handle;
        Dispatcher.BeginInvoke(RequestWindowRefresh, DispatcherPriority.Background);
    }

    private void OpenFilesWith(AppButton b, IReadOnlyList<string> files)
    {
        if (DockGroups.IsGroup(b.Pin)) return; // a group cannot open files; dropping on it is a quiet no-op
        if (!Launcher.OpenWith(b.Pin, b.Identity, files))
            _app.Notify("open-with", b.DisplayName, $"Strata couldn't open {(files.Count == 1 ? "that file" : "those files")} with {b.DisplayName}.");
    }

    private void OpenNew(AppButton b)
    {
        if (DockGroups.IsGroup(b.Pin)) { OnAppClick(b); return; } // a group's "new window" is its folder
        if (b.Identity is not null) Launcher.LaunchNew(b.Identity);
        else if (b.Pin is not null) Launcher.Launch(b.Pin);
    }

    /// <summary>App menu, shown in the same card style as widget flyouts.</summary>
    public DockMenu BuildAppMenu(AppButton b)
    {
        var menu = new DockMenu();
        menu.Header(b.DisplayName);
        foreach (var w in b.Windows.Take(12))
        {
            var handle = w.Handle;
            menu.Add(w.Title.Length > 60 ? w.Title[..59] + "…" : w.Title, () => Win32.ActivateWindow(handle), Glyphs.Window);
        }
        if (b.Windows.Count > 0) menu.Separator();
        // Recent files, from the app's own jump list (read now, never stored).
        var recent = ShellDocuments.Recent(Launcher.RecentAppIds(b.Pin, b.Identity), 6);
        if (recent.Count > 0)
        {
            menu.Header("Recent");
            foreach (var file in recent)
            {
                var path = file;
                string name = System.IO.Path.GetFileName(path);
                menu.Add(name.Length > 50 ? name[..49] + "…" : name, () => OpenFilesWith(b, [path]), Glyphs.Document);
            }
            menu.Separator();
        }
        else if (!ShellDocuments.RecentTrackingOn())
        {
            // Windows keeps no recent files while this is off; the choice stays the user's.
            menu.Add("Turn on recent files in Windows…", () => Launcher.Start("ms-settings:personalization-start", ""), Glyphs.Settings);
            menu.Separator();
        }
        // A group has no executable of its own: "Open" would silently do nothing (audit F6).
        if (!DockGroups.IsGroup(b.Pin))
            menu.Add(NewInstanceCommandLabel(b.Pin?.IdentityKind ?? b.Identity?.IdentityKind ?? AppIdentityKind.Standard,
                b.Windows.Count), () => OpenNew(b), Glyphs.NewWindow);

        if (b.Pin is not null)
        {
            int index = Settings.Pinned.IndexOf(b.Pin);
            menu.Add("Move left", () => MovePin(b.Pin, -1), Glyphs.Left, index > 0);
            menu.Add("Move right", () => MovePin(b.Pin, 1), Glyphs.Right, index < Settings.Pinned.Count - 1);
            if (DockGroups.IsGroup(b.Pin))
            {
                menu.Add("Ungroup", () => { DockGroups.Ungroup(Settings.Pinned, b.Pin); SaveAndRefresh(); }, Glyphs.Window);
                // Named for what it does: this deletes the members with the group (audit F6).
                menu.Add("Remove group and its apps", () => { Settings.Pinned.Remove(b.Pin); SaveAndRefresh(); }, Glyphs.Unpin, danger: true);
            }
            else menu.Add("Unpin from dock", () => { Settings.Pinned.Remove(b.Pin); SaveAndRefresh(); }, Glyphs.Unpin);
        }
        else if (b.Identity is not null)
        {
            var id = b.Identity;
            menu.Add("Pin to dock", () => { Settings.Pinned.Add(Launcher.FromRunning(id)); SaveAndRefresh(); }, Glyphs.Pin);
        }
        if (b.Windows.Count > 0)
        {
            menu.Separator();
            string label = b.Windows.Count == 1 ? "Close window" : $"Close all {b.Windows.Count} windows";
            var handles = b.Windows.Select(w => w.Handle).ToList();
            menu.Add(label, () =>
            {
                foreach (var h in handles) Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }, Glyphs.Close, danger: true);
        }
        return menu;
    }

    private void ShowAppMenu(AppButton b, bool fromKeyboard = false) => ShowFlyout(b, BuildAppMenu(b).Build(CloseFlyout), 270, fromKeyboard);

    public static string NewInstanceCommandLabel(AppIdentityKind identityKind, int windowCount) =>
        identityKind == AppIdentityKind.ExplorerFolder || windowCount > 0 ? "New window" : "Open";

    private void MovePin(PinnedApp pin, int delta)
    {
        int i = Settings.Pinned.IndexOf(pin);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Settings.Pinned.Count) return;
        (Settings.Pinned[i], Settings.Pinned[j]) = (Settings.Pinned[j], Settings.Pinned[i]);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        Settings.Save();
        _appButtons.Clear();
        _apps.Children.Clear();
        RequestWindowRefresh();
    }

    public DockMenu BuildDockMenu()
    {
        var menu = new DockMenu();
        menu.Header("Strata");
        menu.Add("Command palette…", ShowCommandPalette, Glyphs.Search);
        menu.Add("Settings…", OpenSettings, Glyphs.Settings);
        menu.Add("Widgets…", () => _app.OpenSettings("widgets"), Glyphs.Widgets);
        menu.Separator();
        menu.Add("Task Manager", () => Launcher.Start("taskmgr.exe", ""), Glyphs.TaskManager);
        menu.Add(_app.TaskbarShown ? "Hide Windows taskbar" : "Show Windows taskbar", _app.ToggleTaskbar, Glyphs.Taskbar);
        menu.Separator();
        menu.Add("Exit Strata", _app.Quit, Glyphs.Power, danger: true);
        return menu;
    }

    public void ShowCommandPalette()
    {
        if (IsSecondary) return;
        if (_commandPalette is { IsLoaded: true })
        {
            _commandPalette.Activate();
            return;
        }
        CloseFlyout();
        RequestWindowRefresh();
        _commandPalette = new CommandPaletteWindow(BuildCommandCatalog());
        _commandPalette.Closed += (_, _) => _commandPalette = null;
        _commandPalette.Show();
        _commandPalette.Activate();
    }

    private IReadOnlyList<PaletteCommand> BuildCommandCatalog()
    {
        var commands = new List<PaletteCommand>();
        foreach (var button in _appButtons)
        {
            var captured = button;
            commands.Add(new PaletteCommand("app:" + StartMenuApplicationSource.OpaqueId(captured.Key), captured.DisplayName,
                captured.Windows.Count == 0 ? "Open application" : $"{captured.Windows.Count} open window{(captured.Windows.Count == 1 ? "" : "s")}",
                "application app open switch", PaletteCommandKind.Application, () => OnAppClick(captured)));
            foreach (var window in captured.Windows)
            {
                IntPtr handle = window.Handle;
                string title = string.IsNullOrWhiteSpace(window.Title) ? captured.DisplayName : window.Title;
                commands.Add(new PaletteCommand($"window:{handle.ToInt64():x}", title, $"Window · {captured.DisplayName}",
                    "window activate switch", PaletteCommandKind.Window, () => Win32.ActivateWindow(handle)));
            }
        }

        _installedApplications ??= new StartMenuApplicationSource().Enumerate();
        var existingNames = commands.Where(c => c.Kind == PaletteCommandKind.Application)
            .Select(c => c.Title).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var pin in _installedApplications.Where(p => !existingNames.Contains(p.Name)))
        {
            var captured = pin;
            commands.Add(new PaletteCommand("installed:" + StartMenuApplicationSource.OpaqueId(captured.Aumid ?? captured.Path), captured.Name,
                "Installed application", "application app installed launch open", PaletteCommandKind.Application,
                () => Launcher.Launch(captured)));
        }

        foreach (var (id, title) in new[]
        {
            ("look", "Appearance"), ("taskbar", "Taskbar & startup"), ("widgets", "Widgets"),
            ("automations", "Automations"), ("activity", "Automation activity"), ("apps", "Pinned apps"), ("time", "Clock, timers & weather"),
            ("aiusage", "AI usage"), ("privacy", "Privacy & data"), ("about", "About"),
        })
        {
            string page = id;
            commands.Add(new PaletteCommand("settings:" + page, title, "Strata settings", "settings preferences configure",
                PaletteCommandKind.Setting, () => _app.OpenSettings(page)));
        }

        foreach (var info in WidgetCatalog.All)
        {
            var widget = _widgets.FirstOrDefault(w => w.WidgetId == info.Id);
            if (widget is null)
                commands.Add(new PaletteCommand("widget:" + info.Id, info.Name, "Configure widget", info.Description,
                    PaletteCommandKind.Widget, () => _app.OpenSettings("widgets")));
            else
            {
                var captured = widget;
                commands.Add(new PaletteCommand("widget:" + info.Id, info.Name, info.Description, "widget show open",
                    PaletteCommandKind.Widget, () => OpenWidgetFromPalette(captured)));
            }
        }

        commands.Add(new PaletteCommand("action:restore-taskbar", "Restore Windows taskbar", "Safe recovery action",
            "taskbar show restore recovery", PaletteCommandKind.Action, _app.RestoreTaskbarForUser));
        commands.Add(new PaletteCommand("action:focus", PomodoroTimer.Running ? "Pause focus timer" : "Start focus timer",
            "Reversible Strata action", "focus pomodoro timer start pause", PaletteCommandKind.Action, () =>
            {
                PomodoroTimer.Toggle(Settings);
                Signals.Publish(Smart.Signals.Pomodoro, PomodoroTimer.State(Settings), "command-palette");
            }));

        if (_app.Automations is { } engine)
        {
            foreach (var rule in engine.Rules)
            {
                bool enabled = engine.Status(rule.Id).Enabled;
                string id = rule.Id;
                string name = rule.Name;
                commands.Add(new PaletteCommand("automation:" + id, (enabled ? "Disable " : "Enable ") + name,
                    "Automation control · reversible", rule.Description + " automation rule", PaletteCommandKind.Automation,
                    () => engine.SetEnabled(id, !engine.Status(id).Enabled)));
            }
        }
        return commands;
    }

    private void OpenWidgetFromPalette(Widget widget)
    {
        FrameworkElement anchor = _widgetHosts.TryGetValue(widget, out var host) && host.IsVisible
            ? host : _overflowButton is { IsVisible: true } ? _overflowButton : _shell;
        string name = WidgetManifest.Find(widget.WidgetId)?.Name ?? "Widget";
        if (widget.PreviewFlyout() is { } content)
            ShowFlyout(anchor, content, widget.PreviewFlyoutWidth, fromKeyboard: true);
        else
            ShowFlyout(anchor, UI.Column(6, UI.Header(name), UI.Text(AutomationProperties.GetItemStatus(widget), 12.5, flyout: true)),
                280, fromKeyboard: true);
    }

    private void ShowDockMenu(FrameworkElement? anchor = null, bool fromKeyboard = false)
    {
        // Anchor to the focused item (keyboard) or whatever was right-clicked, so the menu appears next to it.
        anchor ??= Mouse.DirectlyOver as FrameworkElement ?? _shell;
        ShowFlyout(anchor, BuildDockMenu().Build(CloseFlyout), 240, fromKeyboard);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files) Settings.Pinned.Add(Launcher.FromFile(f));
        SaveAndRefresh();
    }

    // ---------------- Timers ----------------

    private void OnSecond()
    {
        _seconds++;
        // App-wide work runs once a second on the main dock only: an extra screen dock repeating it would count
        // screen time twice (found in the 1.2 final review).
        if (!IsSecondary)
        {
            SystemStats.Update();
            try { ScreenTime.Tick(); } catch (Exception ex) { _app.Failures.Record("screen-time", ex, AppClock.Now); }
            _app.PublishSignals();
        }
        foreach (var w in _widgets) SafeTick(w);
        if (!IsSecondary)
        {
            PomodoroTimer.Tick(this);
            // Open flyouts refresh from this (Updating): the volume level, the clock's seconds, quick settings.
            // Nothing raised it before the 1.2 final review, so flyouts showed only what they had when opened.
            Heartbeat.Raise();
        }
        _layouts.PruneExpired(AppClock.Now);
        if (_seconds % 2 == 0) _app.Taskbar.Enforce();
        if (!Settings.ReserveSpace) CheckFullscreen();
        _taskbarSafety.Heartbeat(AppClock.Now);
        // Keep the dock above normal topmost windows (e.g. after a fullscreen app closes).
        if (_seconds % 5 == 0 && !_fullscreenHidden)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void SafeTick(Widget w)
    {
        try { w.Tick(); }
        catch (Exception ex)
        {
            string component = "widget:" + w.WidgetId;
            if (_app.Failures.Record(component, ex, AppClock.Now)) Rebuild();
        }
    }

    // ---------------- IDockHost ----------------

    public void Notify(string key, string title, string text, bool urgent = false) => _app.Notify(key, title, text, urgent);

    public void ShowFlyout(FrameworkElement anchor, FrameworkElement content, double width = 320, bool fromKeyboard = false)
    {
        // A second click on the item whose flyout is open closes it, like a taskbar button. The dock never takes
        // focus (MA_NOACTIVATE), so that click does not close the flyout by itself.
        if (IsSecondClick(_flyout is not null, _flyoutAnchor, anchor)) { CloseFlyout(); return; }
        DismissPreview();
        // A click on the widget whose flyout just closed (by losing focus to that click) should not reopen it.
        if (_lastFlyoutClose.Anchor == anchor && DateTime.Now - _lastFlyoutClose.At < TimeSpan.FromMilliseconds(350)) return;
        CloseFlyout();
        var f = new FlyoutWindow(anchor, content, width, below: Settings.Edge == DockEdge.Top);
        f.Closed += (_, _) =>
        {
            _lastFlyoutClose = (anchor, DateTime.Now);
            if (_flyout == f) { _flyout = null; _flyoutAnchor = null; }
        };
        f.Deactivated += (_, _) => _lastFlyoutClose = (anchor, DateTime.Now);
        // Opened from dock keyboard mode: closing returns focus to the item that opened it (see KeyboardFocusReturn).
        if (_keyboardMode)
        {
            f.CloseStarted += reason => ReturnKeyboardFocus(f, anchor as DockButton, reason);
            Commands.Write("flyout.open", ("item", CommandItem(anchor)));
        }
        _flyout = f;
        _flyoutAnchor = anchor;
        f.Show();
        f.Activate();
        if (fromKeyboard) f.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public void CloseFlyout()
    {
        _flyout?.CloseAnimated();
        _flyout = null;
        _flyoutAnchor = null;
    }

    /// <summary>True when the item clicked is the one whose flyout is open: the click closes it.</summary>
    public static bool IsSecondClick(bool flyoutOpen, object? openAnchor, object clicked) =>
        flyoutOpen && ReferenceEquals(openAnchor, clicked);

    public void SaveSettings() => Settings.Save();

    public void OpenSettings() => _app.OpenSettings();

    public void UserSettingsChanged()
    {
        _theme.UserChose(Settings.Style);
        if (_appHotkeys is not null)
        {
            if (Settings.AppShortcuts) _appHotkeys.Register(); else _appHotkeys.Unregister();
        }
        Rebuild();
    }

    public void ResetTaskbarSafety() => _taskbarSafety.Rearm();

    /// <summary>
    /// Synchronizes settings loaded by a non-interactive production path (for example the deterministic
    /// renderer) without recording a manual override of an active automation.
    /// </summary>
    public void SynchronizeSettings()
    {
        _theme.SyncUserStyle(Settings.Style);
        Rebuild();
    }

    private void OnThemeChanged()
    {
        _app.PersistThemeOverride(_theme.OverriddenOwner);
        Rebuild();
    }

    public void RestoreThemeOverride(string? owner)
    {
        _theme.OverriddenOwner = owner;
        Rebuild();
    }

    public IDisposable PushLayout(LayoutLayer layer) => _layouts.Push(layer);
    public IDisposable RequestTheme(string owner, DockStyle style) => _theme.Request(owner, style);

    public IDisposable? ShowSuggestion(string owner, string appName, string reason)
    {
        var pin = Settings.Pinned.FirstOrDefault(p => p.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (pin is null) return null;
        var item = (owner, pin, reason);
        _suggestions.Add(item);
        Rebuild();
        return new ActionHandle(() => { if (_suggestions.Remove(item)) Rebuild(); });
    }

    public void StartFocus()
    {
        PomodoroTimer.StartFocus(Settings);
        Signals.Publish(Smart.Signals.Pomodoro, PomodoroTimer.State(Settings), "automation");
    }

    public void Shutdown()
    {
        _secondTimer.Stop();
        _windowSubscription.Dispose();
        _hoverTimer.Stop();
        if (_hwnd != IntPtr.Zero && _hotkey?.Registered == true) Win32.UnregisterHotKey(_hwnd, KeyboardHotKeyId);
        if (_hwnd != IntPtr.Zero && _paletteHotkey?.Registered == true) Win32.UnregisterHotKey(_hwnd, CommandPaletteHotKeyId);
        _appHotkeys?.Unregister();
        _commandPalette?.Close();
        CloseFlyout();
        ClosePreview();
        UnregisterAppBar();
        foreach (var w in _widgets) w.Dispose();
    }

    private sealed class ActionHandle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
