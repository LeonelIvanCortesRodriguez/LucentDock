using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Strata.Core;
using Strata.Views;
using Strata.Widgets;

namespace Strata;

/// <summary>
/// Renders the real UI (dock, every widget flyout, menus, settings pages) to PNG files off-screen.
/// Used to review and audit the look without touching the desktop: no window is shown,
/// the taskbar is not changed, and settings are never saved.
/// </summary>
public static class PreviewRenderer
{
    private const double Scale = 2;
    private static int _failures;
    private static readonly List<string> Written = [];
    public static IReadOnlyList<DockStyle> DockStyles { get; } = Core.DockStyles.Selectable;

    public static int RenderAll(App app, string dir)
    {
        _failures = 0;
        Written.Clear();
        Directory.CreateDirectory(dir);
        var s = app.Settings;
        // Show every widget, whatever the user's own layout is (in memory only).
        s.LeftWidgets = ["weather", "media", "calendar", "todos", "notes", "pomodoro", "aiusage"];
        s.RightWidgets = ["screentime", "network", "cpu", "ram", "volume", "battery", "clock"];
        WeatherService.UseSampleData(s, Smart.AppClock.Today);
        SystemStats.LoadFixture(
            Enumerable.Range(0, 60).Select(i => 0.28 + Math.Sin(i / 7.0) * 0.08),
            Enumerable.Range(0, 60).Select(i => 0.61 + Math.Sin(i / 11.0) * 0.025),
            Enumerable.Range(0, 60).Select(i => 180_000d + i * 3_200),
            Enumerable.Range(0, 60).Select(i => 42_000d + i * 900),
            16UL * 1024 * 1024 * 1024, TimeSpan.FromHours(52));
        app.Signals.Pin(Smart.Signals.Cpu, SystemStats.Cpu, "preview-fixture");
        app.Signals.Pin(Smart.Signals.Memory, SystemStats.Memory, "preview-fixture");
        app.Signals.Pin(Smart.Signals.Network, new Smart.NetworkState(Smart.NetworkLevel.Internet,
            SystemStats.DownBytesPerSec, SystemStats.UpBytesPerSec), "preview-fixture");
        app.Signals.Pin(Smart.Signals.Battery, new Smart.BatteryState(true, 76, false), "preview-fixture");
        app.Signals.Pin(Smart.Signals.Calendar, EventStore.BuildState(Smart.AppClock.Now), "preview-fixture");
        app.Signals.Pin(Smart.Signals.Pomodoro, PomodoroTimer.State(s), "preview-fixture");
        app.Signals.Pin(Smart.Signals.ScreenTime, new Smart.ScreenTimeState(app.ScreenTime.Today.TotalSeconds, app.ScreenTime.TopApp), "preview-fixture");
        if (WeatherService.For(s) is { } weather)
            app.Signals.Pin(Smart.Signals.Weather, new Smart.WeatherState(s.WeatherCity, weather.Code, weather.Temperature, weather.IsDay), "preview-fixture");

        // Fixture capabilities keep previews byte-stable regardless of the build machine's power or accessibility state.
        var dock = new DockWindow(app, materialEnvironment: () => MaterialEnvironment.PremiumFixture);

        // 1. The dock in each style, plus the full-width shape.
        foreach (var style in DockStyles)
        {
            s.Style = style;
            s.Shape = DockShape.Pill;
            Try($"dock-{style.ToString().ToLowerInvariant()}", () => RenderDock(dock, dir, $"dock-{style.ToString().ToLowerInvariant()}.png"));
        }
        // Liquid glass from fully transparent to a solid fill.
        s.Style = DockStyle.Clear;
        foreach (int opacity in new[] { 0, 100 })
        {
            s.GlassOpacity = opacity;
            Try($"dock-liquid-{opacity}", () => RenderDock(dock, dir, $"dock-clear-opacity-{opacity}.png"));
        }
        s.GlassOpacity = Theme.DefaultGlassOpacity;
        s.Style = DockStyle.Glass;
        s.Shape = DockShape.FullWidth;
        Try("dock-fullwidth", () => RenderDock(dock, dir, "dock-glass-fullwidth.png", width: 1920));
        s.Shape = DockShape.Pill;

        // 2. Every widget flyout, in Glass and Dark (liquid glass flyouts are Glass's, opaque for readability).
        foreach (var style in new[] { DockStyle.Glass, DockStyle.Dark })
        {
            s.Style = style;
            dock.SynchronizeSettings();
            foreach (var w in dock.Widgets)
            {
                string name = $"flyout-{w.GetType().Name.Replace("Widget", "").ToLowerInvariant()}-{style.ToString().ToLowerInvariant()}.png";
                Try(name, () =>
                {
                    var content = w.PreviewFlyout();
                    if (content is null) return;
                    var card = FlyoutWindow.CreateCard(content);
                    card.Width = w.PreviewFlyoutWidth + 34; // card padding and border
                    Save(Wallpaper(card), Path.Combine(dir, name));
                });
            }

            // 3. Menus
            Try($"menu-dock-{style}", () => Save(Wallpaper(Card(dock.BuildDockMenu(), 240)), Path.Combine(dir, $"menu-dock-{style.ToString().ToLowerInvariant()}.png")));
            var firstApp = dock.AppButtons.FirstOrDefault(b => b.Windows.Count > 0) ?? dock.AppButtons.FirstOrDefault();
            if (firstApp is not null)
                Try($"menu-app-{style}", () => Save(Wallpaper(Card(dock.BuildAppMenu(firstApp), 270)), Path.Combine(dir, $"menu-app-{style.ToString().ToLowerInvariant()}.png")));
        }

        // 4. Command palette, with a fixture-only mixed catalog.
        Try("command-palette", () =>
        {
            Action noOp = () => { };
            var palette = new CommandPaletteWindow(
            [
                new("window:fixture", "Sample editor — Release notes", "Window · Sample Editor", "editor window", PaletteCommandKind.Window, noOp),
                new("app:fixture", "Sample Editor", "2 open windows", "application app", PaletteCommandKind.Application, noOp),
                new("settings:look", "Appearance", "Strata settings", "settings", PaletteCommandKind.Setting, noOp),
                new("widget:aiusage", "AI usage", "Claude and Codex plan limits", "widget", PaletteCommandKind.Widget, noOp),
                new("action:taskbar", "Restore Windows taskbar", "Safe recovery action", "taskbar restore", PaletteCommandKind.Action, noOp),
            ]);
            palette.SetQuery("sample");
            var content = (FrameworkElement)palette.Content;
            palette.Content = null;
            var host = new Border { Child = content, Width = CommandPaletteWindow.PaletteWidth, Height = CommandPaletteWindow.PaletteHeight };
            Save(host, Path.Combine(dir, "command-palette.png"));
            host.Child = null;
            palette.Close();
        });

        // 5. Settings pages
        s.Style = DockStyle.Glass;
        Theme.Apply(s.Style, s.GlassOpacity);
        foreach (var page in new[] { "look", "taskbar", "widgets", "automations", "activity", "apps", "time", "aiusage", "privacy", "about" })
        {
            Try($"settings-{page}", () =>
            {
                var window = new SettingsWindow(app, page);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Child = content, Width = 860 };
                host.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
                Save(host, Path.Combine(dir, $"settings-{page}.png"));
            });
        }

        File.WriteAllLines(Path.Combine(dir, "index.txt"),
            new[] { $"Strata v{Updater.Short(App.Version)} deterministic preview set", "Every value, app, event, and media item comes from sanitized fixtures.", "" }
                .Concat(Written)
                .Append("")
                .Append($"{Written.Count} images, {_failures} failures"));
        Log.Write($"Rendered {Written.Count} previews to {dir} ({_failures} failures).");
        return _failures;
    }

    private static void RenderDock(DockWindow dock, string dir, string file, double width = double.NaN)
    {
        dock.SynchronizeSettings();
        var shell = dock.DetachShell();
        // Off-screen rendering has no wallpaper strip; paint the acrylic tint behind the shell to approximate
        // the frost. The plate's radius must match the live shell's, or it pokes out at the pill's corners --
        // Liquid is a full pill now, the other styles keep the system radius (audit F8).
        uint tint = Theme.Current.AcrylicTint; // AABBGGRR
        var approx = new SolidColorBrush(Color.FromArgb((byte)(tint >> 24), (byte)tint, (byte)(tint >> 8), (byte)(tint >> 16)));
        double radius = Theme.Current.Liquid
            ? Math.Max(Strata.Native.WindowComposition.SystemCornerRadiusDip, dock.Settings.DockHeight / 2 - 2)
            : Strata.Native.WindowComposition.SystemCornerRadiusDip;
        var holder = new Border
        {
            Height = dock.Settings.DockHeight,
            Background = approx,
            CornerRadius = dock.Settings.Shape == DockShape.Pill ? new CornerRadius(radius) : new CornerRadius(0),
        };
        try
        {
            holder.Child = shell;
            if (!double.IsNaN(width)) holder.Width = width;
            Save(Wallpaper(holder), Path.Combine(dir, file));
        }
        finally
        {
            holder.Child = null;
            dock.AttachShell(shell);
        }
    }

    private static Border Card(DockMenu menu, double width)
    {
        var card = FlyoutWindow.CreateCard(menu.Build(() => { }));
        card.Width = width + 34;
        return card;
    }

    /// <summary>A stand-in desktop so glass styles read as they would over a wallpaper.</summary>
    private static Border Wallpaper(FrameworkElement child)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x1D, 0x3B, 0x6E), 0),
                new GradientStop(Color.FromRgb(0x4B, 0x2C, 0x6B), 0.55),
                new GradientStop(Color.FromRgb(0x0F, 0x6A, 0x7A), 1),
            },
        };
        return new Border { Background = brush, Padding = new Thickness(28), Child = child };
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(element.Width is > 0 ? element.Width : double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        // A second pass lets controls that size themselves on SizeChanged (progress bars) settle.
        element.Measure(new Size(size.Width, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        size = element.DesiredSize;

        var visualPrivacyHits = PreviewPrivacy.ScanVisual(element);
        if (visualPrivacyHits.Count > 0)
            throw new PrivacyViolationException(string.Join(" ", visualPrivacyHits));

        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Written.Add($"{Path.GetFileName(path)}  {(int)size.Width}x{(int)size.Height}");
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _failures++;
            Written.Add($"FAILED {what}: {ex.GetType().Name}: {ex.Message}");
            Log.Write($"Preview {what} failed: {ex}");
        }
    }
}
