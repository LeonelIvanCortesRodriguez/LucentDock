using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using Strata.Core;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>Unread counts read from window titles.</summary>
public class UnreadCountTests
{
    [Theory]
    [InlineData("(3) WhatsApp", 3)]
    [InlineData("(12) Chat | Microsoft Teams", 12)]
    [InlineData("[4] Slack - Workspace", 4)]
    [InlineData("(99+) Feed", 99)]
    [InlineData("Inbox (7) - someone@example.com - Mail", 7)]
    [InlineData("Bandeja de entrada (2) - Outlook", 2)]
    [InlineData("WhatsApp", 0)]
    [InlineData("Report (2).docx - Word", 0)]          // a copy's number, not a count
    [InlineData("Budget 2026 (final) - Excel", 0)]
    [InlineData("(3)Notes", 0)]                         // no space after it: a file name, not a count
    [InlineData("Inbox - Mail", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Reads_the_two_shapes_apps_use(string? title, int expected) => Assert.Equal(expected, UnreadCount.FromTitle(title));

    [Fact]
    public void An_app_adds_up_its_windows() =>
        Assert.Equal(5, UnreadCount.ForWindows(["(3) Chat", "Inbox (2) - Mail", "Notes"]));

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void Badge_text(int unread, string? expected) => Assert.Equal(expected, UnreadCount.Badge(unread));

    [Fact]
    public void Unread_badges_are_on_by_default() => Assert.True(new AppSettings().UnreadBadges);
}

/// <summary>Where a dragged app lands.</summary>
public class DockOrderTests
{
    private static PinnedApp Pin(string name) => new() { Name = name, Path = name + ".exe" };

    [Fact]
    public void The_slot_is_how_many_centres_are_to_the_left()
    {
        double[] centres = [20, 60, 100];
        Assert.Equal(0, DockOrder.InsertIndex(centres, 5));
        Assert.Equal(1, DockOrder.InsertIndex(centres, 40));
        Assert.Equal(3, DockOrder.InsertIndex(centres, 500));
    }

    [Theory]
    [InlineData(3, 0, 2, 2)]       // first pin dragged to the end of the pins
    [InlineData(3, 2, 0, 0)]       // last pin dragged to the front
    [InlineData(3, 1, 1, null)]    // dropped where it was
    [InlineData(3, 0, 5, 2)]       // a pin dragged past the running apps stays the last pin
    [InlineData(3, -1, 1, 1)]      // a running app dropped among the pins is pinned there
    [InlineData(3, -1, 3, 3)]      // ... or right after them
    [InlineData(3, -1, 4, null)]   // dropped among the running apps: nothing changes
    [InlineData(0, -1, 0, 0)]      // the first pin
    public void Pin_target(int pins, int pinIndex, int landed, int? expected) =>
        Assert.Equal(expected, DockOrder.PinTarget(pins, pinIndex, landed));

    [Fact]
    public void Dragging_a_pin_reorders_the_pins()
    {
        var a = Pin("a"); var b = Pin("b"); var c = Pin("c");
        var pins = new List<PinnedApp> { a, b, c };
        Assert.True(DockOrder.Apply(pins, a, null, 2));
        Assert.Equal([b, c, a], pins);
        Assert.False(DockOrder.Apply(pins, a, null, 2));    // already there
    }

    [Fact]
    public void Dragging_a_running_app_among_the_pins_pins_it()
    {
        var a = Pin("a"); var b = Pin("b");
        var pins = new List<PinnedApp> { a, b };
        var running = Pin("running");
        Assert.True(DockOrder.Apply(pins, null, () => running, 1));
        Assert.Equal([a, running, b], pins);
        Assert.False(DockOrder.Apply(pins, null, () => Pin("x"), 7));   // dropped among running apps
        Assert.Equal(3, pins.Count);
        Assert.False(DockOrder.Apply(pins, Pin("not pinned"), null, 0));  // a stale pin is ignored
    }
}

/// <summary>The automatic layouts: a calmer dock at night, and a quieter one during meetings.</summary>
[Trait("Category", "Integration")]
public class AutomaticLayoutTests
{
    private static readonly DateTime Evening = new(2026, 3, 10, 21, 58, 0);

    [Fact]
    public void Night_layout_hides_work_widgets_at_night_and_restores_them_in_the_morning()
    {
        var h = new SmartHarness(Evening).Start();
        h.Run(TimeSpan.FromMinutes(1));
        Assert.Contains("cpu", h.Dock.Effective(h.Clock.Now).Right);          // 21:59: still day

        h.Run(TimeSpan.FromMinutes(2));                                       // 22:01
        var night = h.Dock.Effective(h.Clock.Now);
        Assert.DoesNotContain("cpu", night.Right);
        Assert.DoesNotContain("ram", night.Right);
        Assert.Contains("clock", night.Right);                                // essentials stay
        Assert.Contains("media", night.Left);                                 // and so does what you wind down with
        Assert.Contains(h.HistoryFor(Automations.NightLayout), e => e.Kind == "Fired");

        h.Clock.Advance(TimeSpan.FromHours(9) - TimeSpan.FromMinutes(2));     // 06:59
        h.Run(TimeSpan.FromMinutes(2));                                       // 07:01
        var morning = h.Dock.Effective(h.Clock.Now);
        Assert.Equal(h.Dock.BaseRight, morning.Right);
        Assert.Contains(h.HistoryFor(Automations.NightLayout), e => e.Kind == "Ended");
    }

    [Fact]
    public void An_alert_at_night_still_brings_its_widget_forward()
    {
        var h = new SmartHarness(new DateTime(2026, 3, 10, 23, 0, 0)).Start();
        h.Run(TimeSpan.FromSeconds(70), () =>
        {
            h.Signals.Publish(Signals.Cpu, 0.97, "test");
            h.Signals.Publish(Signals.Memory, 0.40, "test");
        });
        var e = h.Dock.Effective(h.Clock.Now);
        Assert.Equal("cpu", e.Right[0]);                // resource pressure (priority 50) wins over night (15)
        Assert.Contains("ram", e.Right);                // resource pressure shows CPU and memory together
        Assert.Contains("cpu", e.Emphasized);           // highlighting the one that is high
    }

    [Fact]
    public void Night_hours_can_be_changed_and_the_layout_turned_off()
    {
        var h = new SmartHarness(new DateTime(2026, 3, 10, 20, 30, 0)).Start();
        h.Engine.SetParameter(Automations.NightLayout, "from", "20:00");
        h.Run(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain("cpu", h.Dock.Effective(h.Clock.Now).Right);

        h.Engine.SetEnabled(Automations.NightLayout, false);
        h.Run(TimeSpan.FromMinutes(1));
        Assert.Contains("cpu", h.Dock.Effective(h.Clock.Now).Right);
    }

    [Fact]
    public void Meetings_hide_media_weather_and_screen_time_unless_turned_off()
    {
        var start = new DateTime(2026, 3, 10, 14, 0, 0);
        foreach (bool quiet in new[] { true, false })
        {
            var h = new SmartHarness(start).Start();
            if (!quiet) h.Engine.SetParameter(Automations.MeetingPrep, "quiet", "0");
            var review = new CalendarEventInfo("r", "Review", start.AddMinutes(5), false);
            h.Run(TimeSpan.FromSeconds(2), () => h.Signals.Publish(Signals.Calendar, new CalendarState(review, null, 1), "calendar"));
            var e = h.Dock.Effective(h.Clock.Now);
            Assert.Equal("calendar", e.Right[0]);
            Assert.Equal(!quiet, e.Left.Contains("media"));
            Assert.Equal(!quiet, e.Left.Contains("weather"));
            Assert.Equal(!quiet, e.Right.Contains("screentime"));
        }
    }
}

/// <summary>Settings backup and restore.</summary>
public class SettingsBackupTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 9, 30, 0);

    [Fact]
    public void A_backup_round_trips_settings_and_automation_choices_only()
    {
        var s = new AppSettings
        {
            Style = DockStyle.Clear, GlassOpacity = 15, Edge = DockEdge.Top, UnreadBadges = false,
            LeftWidgets = ["media"], RightWidgets = ["clock", "battery"],
            Pinned = [new PinnedApp { Name = "Sample", Path = @"C:\Sample\sample.exe" }],
        };
        var meeting = new RuleState { Enabled = false, Parameters = { ["lead"] = "20" },
            LastExplanation = "\"Private meeting\" starts in 3 min", FireCount = 4, FiredKeys = { ["k"] = Now } };
        var untouched = new RuleState { LastExplanation = "anything", FireCount = 2 };
        string json = SettingsBackup.Serialize(s, [(Automations.MeetingPrep, meeting), (Automations.LowBattery, untouched)], "1.2.0", Now);

        Assert.DoesNotContain("Private meeting", json);       // no history or explanations: they can carry event titles
        Assert.DoesNotContain("firedKeys", json);
        Assert.DoesNotContain(Automations.LowBattery, json);  // automations left at their defaults aren't listed

        var (file, error) = SettingsBackup.Parse(json);
        Assert.Null(error);
        Assert.Equal(SettingsBackup.FormatName, file!.Format);
        Assert.Equal(Now, file.ExportedAt);
        Assert.Equal(DockStyle.Clear, file.Settings!.Style);
        Assert.Equal(15, file.Settings.GlassOpacity);
        Assert.Equal(DockEdge.Top, file.Settings.Edge);
        Assert.False(file.Settings.UnreadBadges);
        Assert.Equal(@"C:\Sample\sample.exe", Assert.Single(file.Settings.Pinned).Path);
        Assert.False(file.Automations[Automations.MeetingPrep].Enabled);
        Assert.Equal("20", file.Automations[Automations.MeetingPrep].Parameters["lead"]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"style\": \"Glass\" }")]                                        // a settings.json, not a backup
    [InlineData("{ \"format\": \"something-else\", \"settings\": {} }")]
    [InlineData("{ \"format\": \"strata-settings\" }")]                             // no settings in it
    public void Other_files_are_refused(string json)
    {
        var (file, error) = SettingsBackup.Parse(json);
        Assert.Null(file);
        Assert.Equal("That file isn't a Strata settings backup.", error);
    }

    [Fact]
    public void A_backup_from_a_newer_strata_is_refused_with_a_reason()
    {
        var (file, error) = SettingsBackup.Parse("{ \"format\": \"strata-settings\", \"formatVersion\": 2, \"app\": \"2.0.0\", \"settings\": {} }");
        Assert.Null(file);
        Assert.Contains("newer Strata (2.0.0)", error);
    }

    [Fact]
    public void Restored_values_are_made_safe()
    {
        var (file, _) = SettingsBackup.Parse("""
            { "format": "strata-settings", "formatVersion": 1, "settings": {
                "style": "White", "glassOpacity": 400,
                "leftWidgets": ["media", "no-such-widget", "media"], "rightWidgets": ["media", "clock"] } }
            """);
        var s = file!.Settings!;
        Assert.Equal(DockStyle.Clear, s.Style);             // a 1.1 style moves to its 1.2 one
        Assert.Equal(100, s.GlassOpacity);
        Assert.Equal(["media"], s.LeftWidgets);             // unknown and repeated widgets dropped
        Assert.Contains("clock", s.RightWidgets);
        Assert.DoesNotContain("media", s.RightWidgets);     // a widget sits on one side only
    }

    [Fact]
    public void The_suggested_name_has_the_date_and_extension() =>
        Assert.Equal("Strata settings 2026-10-25.strata-settings.json", SettingsBackup.SuggestedName(Now));
}

/// <summary>Quick settings in the volume flyout, on fixture radios: nothing on this PC is read or switched.</summary>
[Collection("Focus timer")]
public class QuickSettingsTests
{
    private sealed class Host : IDockHost
    {
        public AppSettings Settings { get; } = new();
        public ScreenTimeTracker ScreenTime { get; } = new();
        public SignalStore Signals { get; } = new(new ManualClock(new DateTime(2026, 3, 10, 9, 0, 0)));
        public int Closed { get; private set; }
        public void Notify(string key, string title, string text, bool urgent = false) { }
        public void ShowFlyout(System.Windows.FrameworkElement anchor, System.Windows.FrameworkElement content, double width = 320, bool fromKeyboard = false) { }
        public void CloseFlyout() => Closed++;
        public void SaveSettings() { }
        public void OpenSettings() { }
    }

    [Theory]
    [InlineData(true, "On")]
    [InlineData(false, "Off")]
    [InlineData(null, "Not available")]
    public void Radio_status(bool? on, string expected) => Assert.Equal(expected, QuickSettings.RadioStatus(on));

    [Fact]
    public async Task Fixture_radios_switch_and_a_missing_radio_refuses()
    {
        var radios = new FixtureRadios(wifi: true, bluetooth: null);
        Assert.True(await radios.SetAsync(QuickRadio.WiFi, false));
        Assert.False(radios.IsOn(QuickRadio.WiFi));
        Assert.False(await radios.SetAsync(QuickRadio.Bluetooth, true));
        Assert.Null(radios.IsOn(QuickRadio.Bluetooth));
    }

    [Fact]
    public void Tiles_show_state_and_switch_it() => OnSta(() =>
    {
        var radios = new FixtureRadios(wifi: true, bluetooth: false);
        RadioSource.Current = radios;
        PomodoroTimer.Reset();
        try
        {
            var host = new Host();
            var grid = (System.Windows.Controls.Primitives.UniformGrid)QuickSettings.Build(host, out var refresh);
            var tiles = grid.Children.OfType<Button>().ToList();
            Assert.Equal(["Wi-Fi", "Bluetooth", "Focus", "Night light"], tiles.Select(AutomationProperties.GetName));
            Assert.Equal(["On", "Off", "Off", "Settings"], tiles.Select(AutomationProperties.GetItemStatus));

            tiles[1].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(radios.IsOn(QuickRadio.Bluetooth));
            Assert.Equal("On", AutomationProperties.GetItemStatus(tiles[1]));

            tiles[2].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(PomodoroTimer.Running);                                  // Focus starts a focus session
            Assert.EndsWith("min left", AutomationProperties.GetItemStatus(tiles[2]));
            tiles[2].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(PomodoroTimer.Running);                                 // and stops it

            radios.SetAsync(QuickRadio.WiFi, false).Wait();                      // changed elsewhere: the next refresh shows it
            refresh();
            Assert.Equal("Off", AutomationProperties.GetItemStatus(tiles[0]));
        }
        finally
        {
            PomodoroTimer.Reset();
            RadioSource.Current = new FixtureRadios(null, null);
        }
    });

    private static void OnSta(Action action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA dock-upgrade test timed out.");
        error?.Throw();
    }
}

/// <summary>Open flyouts refresh once a second from the dock's heartbeat (never raised before the 1.2 final review).</summary>
public class FlyoutRefreshTests
{
    [Fact]
    public void A_shown_panel_refreshes_on_each_heartbeat_and_stops_once_closed()
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try
            {
                int updates = 0;
                var panel = new StackPanel().Updating(() => updates++);
                Assert.Equal(1, updates);                          // filled in at once
                var window = new System.Windows.Window { Content = panel, Left = -20000, Top = -20000, Width = 50, Height = 50,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None };
                window.Show();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Heartbeat.Raise();
                Heartbeat.Raise();
                Assert.Equal(3, updates);
                window.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Heartbeat.Raise();
                Assert.Equal(3, updates);                          // a closed flyout no longer listens
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA dock-upgrade test timed out.");
        error?.Throw();
    }
}
