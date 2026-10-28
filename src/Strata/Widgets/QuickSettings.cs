using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;

namespace Strata.Widgets;

/// <summary>
/// Quick settings under the volume slider: Wi-Fi and Bluetooth switch on and off (Windows.Devices.Radios),
/// Focus starts or stops Strata's focus session, and Night light opens its Windows settings page, because
/// Windows has no public way for an app to switch night light.
/// </summary>
public static class QuickSettings
{
    public const string WifiSettings = "ms-settings:network-wifi";
    public const string BluetoothSettings = "ms-settings:bluetooth";
    public const string NightLightSettings = "ms-settings:nightlight";

    /// <summary>What a radio tile says under its name.</summary>
    public static string RadioStatus(bool? on) => on switch { true => "On", false => "Off", null => "Not available" };

    /// <summary>The tiles, refreshed by <paramref name="refresh"/> (the flyout's once-a-second update).</summary>
    public static FrameworkElement Build(IDockHost host, out Action refresh)
    {
        var radios = RadioSource.Current;
        var wifi = new QuickTile("", "Wi-Fi");
        var bluetooth = new QuickTile("", "Bluetooth");
        var focus = new QuickTile("", "Focus");
        var night = new QuickTile("", "Night light");

        void Radio(QuickTile tile, QuickRadio radio, string settingsPage)
        {
            tile.Click += async (_, e) =>
            {
                e.Handled = true;
                bool? now = radios.IsOn(radio);
                // No radio, or Windows won't let apps switch it: open its settings page instead.
                if (now is null || !await radios.SetAsync(radio, !now.Value)) Launcher.Start(settingsPage, "");
                Update();
            };
            tile.ToolTip = $"Turn {tile.Label} on or off. If Strata may not switch it, its Windows settings open.";
        }
        Radio(wifi, QuickRadio.WiFi, WifiSettings);
        Radio(bluetooth, QuickRadio.Bluetooth, BluetoothSettings);

        focus.Click += (_, e) =>
        {
            e.Handled = true;
            if (FocusRunning) PomodoroTimer.Reset(); else PomodoroTimer.StartFocus(host.Settings);
            Update();
        };
        focus.ToolTip = "Start or stop a focus session (the focus timer; focus mode hides distracting widgets)";

        night.Click += (_, e) => { e.Handled = true; Launcher.Start(NightLightSettings, ""); host.CloseFlyout(); };
        night.ToolTip = "Open night light settings";
        night.Show(null, "Settings");

        void Update()
        {
            wifi.Show(radios.IsOn(QuickRadio.WiFi), RadioStatus(radios.IsOn(QuickRadio.WiFi)));
            bluetooth.Show(radios.IsOn(QuickRadio.Bluetooth), RadioStatus(radios.IsOn(QuickRadio.Bluetooth)));
            focus.Show(FocusRunning, FocusRunning ? $"{(int)Math.Ceiling(PomodoroTimer.Remaining(host.Settings).TotalMinutes)} min left" : "Off");
        }
        Update();
        refresh = Update;

        var grid = new UniformGrid { Columns = 2 };
        foreach (var t in new[] { wifi, bluetooth, focus, night }) grid.Children.Add(t.Button);
        AutomationProperties.SetName(grid, "Quick settings");
        return grid;
    }

    private static bool FocusRunning => PomodoroTimer.Running && PomodoroTimer.Current == PomodoroTimer.Phase.Work;
}

/// <summary>One quick-settings tile (a flyout button): accent when on, plain when off or when it only opens a page.</summary>
public sealed class QuickTile
{
    private readonly TextBlock _status;
    public FlatButton Button { get; }
    public string Label { get; }
    public bool? IsOn { get; private set; }
    public event RoutedEventHandler Click { add => Button.Click += value; remove => Button.Click -= value; }
    public object? ToolTip { get => Button.ToolTip; set => Button.ToolTip = value; }

    public QuickTile(string glyph, string label)
    {
        Label = label;
        var icon = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = label, FontFamily = Theme.UiFont, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        _status = new TextBlock { FontFamily = Theme.UiFont, FontSize = 11, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(_status);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        Button = new FlatButton { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 6, 6) };
        AutomationProperties.SetName(Button, label);
    }

    /// <summary>Show the state: true lights the tile, false and null leave it plain.</summary>
    public void Show(bool? on, string status)
    {
        IsOn = on;
        _status.Text = status;
        if (on == true)
        {
            Button.SetResourceReference(Control.BackgroundProperty, "Accent");
            Button.SetResourceReference(Control.ForegroundProperty, "AccentFg");
        }
        else
        {
            Button.SetResourceReference(Control.BackgroundProperty, "InputBg");
            Button.SetResourceReference(Control.ForegroundProperty, "FlyoutFg");
        }
        AutomationProperties.SetItemStatus(Button, status);
    }
}
