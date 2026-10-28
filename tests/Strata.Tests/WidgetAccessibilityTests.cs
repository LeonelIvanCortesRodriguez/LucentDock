using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using Strata.Core;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>
/// Found by the Phase B UI Automation sweep on 1.2: the Weather tile never set an accessible name, so a screen
/// reader announced it as just "button".
/// </summary>
[Trait("Category", "Unit")]
public class WidgetAccessibilityTests
{
    private sealed class Host : IDockHost
    {
        public AppSettings Settings { get; } = new() { WeatherCity = "", WeatherLatitude = null, WeatherLongitude = null };
        public ScreenTimeTracker ScreenTime { get; } = new();
        public SignalStore Signals { get; } = new(new ManualClock(new DateTime(2026, 3, 10, 9, 0, 0)));
        public void Notify(string key, string title, string text, bool urgent = false) { }
        public void ShowFlyout(FrameworkElement anchor, FrameworkElement content, double width = 320, bool fromKeyboard = false) { }
        public void CloseFlyout() { }
        public void SaveSettings() { }
        public void OpenSettings() { }
    }

    [Fact]
    public void The_weather_tile_has_a_name_even_without_a_city() => OnSta(() =>
    {
        var tile = new WeatherWidget(new Host());
        Assert.Equal("Weather", AutomationProperties.GetName(tile));
        Assert.Equal("no city set", AutomationProperties.GetItemStatus(tile));
    });

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA accessibility test timed out.");
        error?.Throw();
    }
}
