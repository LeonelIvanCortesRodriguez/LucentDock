using Strata.Core;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>
/// End-to-end runs of the real built-in automations: signals in, dock layout / theme / notifications out,
/// on a simulated clock. These exercise the same engine, catalog and layout code the app uses.
/// </summary>
[Trait("Category", "Integration")]
public class AutomationScenarioTests
{
    private static readonly DateTime Afternoon = new(2026, 3, 10, 14, 0, 0);

    private static CalendarState Calendar(DateTime now, params CalendarEventInfo[] events)
    {
        var next = events.Where(e => e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        var current = events.Where(e => e.Start <= now && e.Start > now.AddHours(-1)).OrderByDescending(e => e.Start).FirstOrDefault();
        return new CalendarState(next, current, events.Length);
    }

    private static void AssertBaseLayout(SmartHarness h)
    {
        var e = h.Dock.Effective(h.Clock.Now);
        Assert.Equal(h.Dock.BaseLeft, e.Left);
        Assert.Equal(h.Dock.BaseRight, e.Right);
        Assert.Empty(e.Emphasized);
    }

    [Fact]
    public void MeetingPreparationSurfacesWidgetsAndRestoresAfterwards()
    {
        var h = new SmartHarness(Afternoon).Start();
        var standup = new CalendarEventInfo("e1", "Standup", Afternoon.AddMinutes(9), false);
        h.Run(TimeSpan.FromSeconds(1), () => h.Signals.Publish(Signals.Calendar, Calendar(h.Clock.Now, standup), "calendar"));

        var during = h.Dock.Effective(h.Clock.Now);
        Assert.Equal(new[] { "calendar", "clock", "volume", "battery", "network" }, during.Right.Take(5));
        Assert.Contains("calendar", during.Emphasized);
        Assert.DoesNotContain("Standup", h.HistoryFor(Automations.MeetingPrep).First(e => e.Kind == "Fired").Explanation);
        Assert.Contains(Signals.Calendar.Id, h.HistoryFor(Automations.MeetingPrep).First(e => e.Kind == "Fired").Explanation);

        // Runs through the event (reminder at 14:04) and past the 15-minute hold.
        h.Run(TimeSpan.FromMinutes(26), () => h.Signals.Publish(Signals.Calendar, Calendar(h.Clock.Now, standup), "calendar"));
        AssertBaseLayout(h);
        Assert.Contains(h.HistoryFor(Automations.MeetingPrep), e => e.Kind == "Ended");
        Assert.Equal(1, h.Fired(Automations.MeetingPrep));
    }

    [Fact]
    public void EventReminderOncePerEventIncludingBackToBackEvents()
    {
        var h = new SmartHarness(Afternoon).Start();
        var a = new CalendarEventInfo("a", "Design review", Afternoon.AddMinutes(10), false);
        var b = new CalendarEventInfo("b", "1:1", Afternoon.AddMinutes(20), false);
        h.Run(TimeSpan.FromMinutes(25), () => h.Signals.Publish(Signals.Calendar, Calendar(h.Clock.Now, a, b), "calendar"));

        Assert.Equal(new[] { "Design review", "1:1" }, h.Notifier.Shown.Select(n => n.Title));
        Assert.Equal(2, h.Fired(Automations.EventReminder));
    }

    [Fact]
    public void EventReminderRespectsQuietHours()
    {
        var quiet = new QuietHours { Enabled = true, Start = TimeSpan.FromHours(14), End = TimeSpan.FromHours(15) };
        var h = new SmartHarness(Afternoon, quiet: quiet).Start();
        var late = new CalendarEventInfo("q", "Late call", Afternoon.AddMinutes(10), false);
        h.Run(TimeSpan.FromMinutes(8), () => h.Signals.Publish(Signals.Calendar, Calendar(h.Clock.Now, late), "calendar"));
        Assert.Empty(h.Notifier.Shown);
        Assert.Contains(h.HistoryFor(Automations.EventReminder), e => e.Kind == "Fired" && e.Detail.Contains("quiet hours"));
    }

    [Fact]
    public void LowBatteryNotifiesOnceAndDoesNotFlap()
    {
        var h = new SmartHarness(Afternoon).Start();
        void Battery(int pct, bool charging = false) => h.Signals.Publish(Signals.Battery, new BatteryState(true, pct, charging), "power");

        Battery(15);
        Assert.Equal("battery", h.Dock.Effective(h.Clock.Now).Right[0]);
        Assert.Contains("battery", h.Dock.Effective(h.Clock.Now).Emphasized);
        Battery(14); Battery(13); Battery(22); // 22 is below threshold + 5: stays active, no repeats
        Assert.Single(h.Notifier.Shown);
        Assert.Equal("Battery low", h.Notifier.Shown[0].Title);

        Battery(23, charging: true);
        AssertBaseLayout(h);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Battery(18); // re-entry inside the 30-minute cooldown: suppressed
        Assert.Single(h.Notifier.Shown);
        Assert.Contains(h.HistoryFor(Automations.LowBattery), e => e.Kind == "Suppressed");

        Battery(40, charging: true);
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        Battery(18);
        Assert.Equal(2, h.Notifier.Shown.Count);
    }

    [Fact]
    public void DesktopsWithoutBatteryNeverTrigger()
    {
        var h = new SmartHarness(Afternoon).Start();
        h.Signals.Publish(Signals.Battery, new BatteryState(false, 0, false), "power");
        Assert.Equal(0, h.Fired(Automations.LowBattery));
    }

    [Fact]
    public void FocusModeHidesDistractionsAndRestores()
    {
        var h = new SmartHarness(Afternoon).Start();
        h.Signals.Publish(Signals.Pomodoro, new PomodoroState(true, FocusPhase.Work, TimeSpan.FromMinutes(25), 0), "pomodoro");
        var e = h.Dock.Effective(h.Clock.Now);
        foreach (var hidden in WidgetManifest.WithRole(WidgetRole.Distracting))
            Assert.DoesNotContain(hidden, e.Left.Concat(e.Right));
        Assert.Equal("pomodoro", e.Right[0]);
        Assert.Contains("clock", e.Right);   // essentials stay
        Assert.Contains("battery", e.Right);

        h.Signals.Publish(Signals.Pomodoro, new PomodoroState(true, FocusPhase.Break, TimeSpan.FromMinutes(5), 1), "pomodoro");
        AssertBaseLayout(h);
    }

    [Fact]
    public void LowBatteryOutranksFocusMode()
    {
        var h = new SmartHarness(Afternoon).Start();
        h.Dock.BaseRight.Add("media");
        h.Signals.Publish(Signals.Pomodoro, new PomodoroState(true, FocusPhase.Work, TimeSpan.FromMinutes(25), 0), "pomodoro");
        h.Signals.Publish(Signals.Battery, new BatteryState(true, 10, false), "power");
        Assert.Equal("battery", h.Dock.Effective(h.Clock.Now).Right[0]); // priority 90 beats focus (70)
    }

    [Fact]
    public void SunsetThemeHonoursManualOverride()
    {
        var h = new SmartHarness(new DateTime(2026, 3, 10, 18, 0, 0)).Start();
        void Sun(bool evening) => h.Signals.Publish(Signals.Sun, new SunState(null, new DateTime(2026, 3, 10, 18, 30, 0), evening, "location"), "sun");

        Sun(false);
        Assert.Equal(DockStyle.Glass, h.Dock.Theme.Effective);
        Sun(true);
        Assert.Equal(DockStyle.Dark, h.Dock.Theme.Effective);
        Assert.Contains(Signals.Sun.Id, h.HistoryFor(Automations.SunsetTheme).Single(e => e.Kind == "Fired").Explanation);

        h.Dock.Theme.UserChose(DockStyle.Clear);
        Assert.Equal(DockStyle.Clear, h.Dock.Theme.Effective);

        Sun(false); // morning
        Assert.Equal(DockStyle.Clear, h.Dock.Theme.Effective);
        Sun(true);  // next evening: automation applies again
        Assert.Equal(DockStyle.Dark, h.Dock.Theme.Effective);
    }

    [Fact]
    public void SunsetSetTimesAreReadFromSavedStateBeforeTheEngineStarts()
    {
        // Phase B: the app seeds the sun signal from the rule's own times before Start. Those times used to be
        // unloaded then, so a saved 20:00 evening start read as the 19:00 default and Midnight flashed for a second.
        var persistence = new InMemoryPersistence();
        var saved = new AutomationStateFile();
        saved.Rules[Automations.SunsetTheme] = new RuleState { Parameters = { ["eveningStart"] = "20:00", ["morningStart"] = "07:00" } };
        persistence.Save(saved);
        var h = new SmartHarness(new DateTime(2026, 10, 23, 19, 30, 0), persistence: persistence);

        var parameters = h.Engine.Status(Automations.SunsetTheme).State.Parameters;
        Assert.Equal("20:00", parameters.GetValueOrDefault("eveningStart"));
        bool evening = SunCalculator.IsEveningBySetTime(h.Clock.Now.TimeOfDay,
            SunCalculator.ParseTimeOfDay(parameters.GetValueOrDefault("eveningStart"), TimeSpan.FromHours(19)),
            SunCalculator.ParseTimeOfDay(parameters.GetValueOrDefault("morningStart"), TimeSpan.FromHours(7)));
        h.Signals.Publish(Signals.Sun, new SunState(null, null, evening, "set time"), "sun");
        h.Start();

        Assert.False(evening);
        Assert.Empty(h.HistoryFor(Automations.SunsetTheme));
        Assert.Equal(DockStyle.Glass, h.Dock.Theme.Effective);
        Assert.Same(h.Engine.Status(Automations.SunsetTheme).State, h.Engine.StateFile.Rules[Automations.SunsetTheme]);
    }

    [Fact]
    public void ResourcePressureNeedsASustainedCondition()
    {
        var h = new SmartHarness(Afternoon).Start();
        void Load(double cpu, double mem = 0.5) { h.Signals.Publish(Signals.Cpu, cpu, "system"); h.Signals.Publish(Signals.Memory, mem, "system"); }

        h.Run(TimeSpan.FromSeconds(1), () => Load(0.99));
        h.Run(TimeSpan.FromSeconds(10), () => Load(0.2));
        Assert.Equal(0, h.Fired(Automations.ResourcePressure)); // one-second spike ignored

        h.Run(TimeSpan.FromSeconds(30), () => Load(0.95));
        Assert.Equal(0, h.Fired(Automations.ResourcePressure));
        h.Run(TimeSpan.FromSeconds(31), () => Load(0.95));
        Assert.Equal(1, h.Fired(Automations.ResourcePressure));
        var e = h.Dock.Effective(h.Clock.Now);
        Assert.Equal(new[] { "cpu", "ram" }, e.Right.Take(2));
        Assert.Contains("cpu", e.Emphasized);
        Assert.DoesNotContain("ram", e.Emphasized);
        Assert.Empty(h.Notifier.Shown); // notification is opt-in for this automation

        h.Run(TimeSpan.FromSeconds(3), () => Load(0.75)); // between limit-15 and limit: still active (hysteresis)
        Assert.Contains("cpu", h.Dock.Effective(h.Clock.Now).Emphasized);
        h.Run(TimeSpan.FromSeconds(3), () => Load(0.3));
        AssertBaseLayout(h);
    }

    [Fact]
    public void ConnectivityLossIsSustainedAndDoesNotFloodNotifications()
    {
        var h = new SmartHarness(Afternoon).Start();
        void Net(NetworkLevel l) => h.Signals.Publish(Signals.Network, new NetworkState(l, 0, 0), "network");

        h.Run(TimeSpan.FromSeconds(10), () => Net(NetworkLevel.None));
        Assert.Equal(0, h.Fired(Automations.Connectivity)); // brief blip
        h.Run(TimeSpan.FromSeconds(15), () => Net(NetworkLevel.None));
        Assert.Equal(1, h.Fired(Automations.Connectivity));
        Assert.Equal("network", h.Dock.Effective(h.Clock.Now).Right[0]);
        Assert.Single(h.Notifier.Shown);

        h.Run(TimeSpan.FromSeconds(5), () => Net(NetworkLevel.Internet));
        AssertBaseLayout(h);

        // Flapping connection: the dock reacts each time, the notification does not repeat for 15 minutes.
        for (int i = 0; i < 3; i++)
        {
            h.Run(TimeSpan.FromSeconds(25), () => Net(NetworkLevel.Constrained));
            Assert.Equal("network", h.Dock.Effective(h.Clock.Now).Right[0]);
            h.Run(TimeSpan.FromSeconds(5), () => Net(NetworkLevel.Internet));
        }
        Assert.Single(h.Notifier.Shown);
        Assert.Contains(h.HistoryFor(Automations.Connectivity), e => e.Kind == "Fired" && e.Detail.Contains("already sent recently"));
    }

    [Fact]
    public void ScreenTimeCheckIsOncePerDayAndWaitsForQuietHours()
    {
        var quiet = new QuietHours { Enabled = true, Start = TimeSpan.FromHours(12), End = TimeSpan.FromHours(13) };
        var h = new SmartHarness(new DateTime(2026, 3, 10, 12, 30, 0), quiet: quiet).Start();
        int seconds = 4 * 3600 + 60;
        void Publish() => h.Signals.Publish(Signals.ScreenTime, new ScreenTimeState(seconds++, "Editor"), "screentime");

        h.Run(TimeSpan.FromMinutes(10), Publish);
        Assert.Empty(h.Notifier.Shown);
        Assert.Contains(h.HistoryFor(Automations.ScreenTimeCheck), e => e.Kind == "Deferred");

        h.Run(TimeSpan.FromMinutes(25), Publish); // past 13:00
        Assert.Single(h.Notifier.Shown);
        Assert.Contains("Editor", h.Notifier.Shown[0].Body);
        Assert.Equal("screentime", h.Dock.Effective(h.Clock.Now).Right[0]);

        h.Run(TimeSpan.FromMinutes(11), Publish); // promotion is temporary (10 min)
        AssertBaseLayout(h);
        Assert.Single(h.Notifier.Shown); // still once today

        // Next day: the count resets, then crosses the threshold again.
        h.Clock.Now = new DateTime(2026, 3, 11, 9, 0, 0);
        seconds = 100;
        h.Run(TimeSpan.FromSeconds(2), Publish);
        seconds = 4 * 3600 + 1;
        h.Run(TimeSpan.FromSeconds(2), Publish);
        Assert.Equal(2, h.Notifier.Shown.Count);
    }

    [Fact]
    public void ParametersChangeBehaviourAndArePersisted()
    {
        var h = new SmartHarness(Afternoon).Start();
        h.Engine.SetParameter(Automations.LowBattery, "threshold", "40");
        h.Signals.Publish(Signals.Battery, new BatteryState(true, 35, false), "power");
        Assert.Equal(1, h.Fired(Automations.LowBattery));
        Assert.Equal("40", h.Persistence.State.Rules[Automations.LowBattery].Parameters["threshold"]);
        Assert.Contains(Signals.Battery.Id, h.Engine.Status(Automations.LowBattery).State.LastExplanation);
    }
}

[Trait("Category", "Unit")]
public class AutomationCatalogTests
{
    [Fact]
    public void AutomationsAreConsistent()
    {
        var rules = Automations.Create();
        Assert.Equal(9, rules.Count);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        var signalIds = Signals.All.Select(s => s.Id).ToHashSet();
        foreach (var r in rules)
        {
            Assert.NotEmpty(r.Actions);
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
            foreach (var s in r.SignalIds) Assert.Contains(s, signalIds);
            foreach (var w in r.WidgetsTouched) Assert.NotNull(WidgetManifest.Find(w));
            foreach (var p in r.Parameters.Where(p => p.Kind is ParameterKind.Number or ParameterKind.Toggle))
            {
                var v = double.Parse(p.Default, System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(v, p.Kind == ParameterKind.Toggle ? 0 : p.Min, p.Kind == ParameterKind.Toggle ? 1 : p.Max);
            }
            foreach (var a in r.Actions.OfType<PromoteWidgets>()) foreach (var w in a.Widgets) Assert.NotNull(WidgetManifest.Find(w));
            foreach (var a in r.Actions.OfType<CollapseWidgets>()) foreach (var w in a.Widgets) Assert.NotNull(WidgetManifest.Find(w));
        }
    }

    [Fact]
    public void WidgetManifestAndCatalogAgree()
    {
        Assert.Equal(WidgetManifest.All.Select(w => w.Id).OrderBy(x => x), WidgetCatalog.All.Select(w => w.Id).OrderBy(x => x));
        Assert.Equal(WidgetManifest.All.Count, WidgetManifest.All.Select(w => w.Id).Distinct().Count());
        var signalIds = Signals.All.Select(s => s.Id).ToHashSet();
        foreach (var w in WidgetManifest.All) foreach (var s in w.Consumes) Assert.Contains(s, signalIds);
        Assert.Equal(14, WidgetManifest.All.Count); // the real count, reported in the docs
    }
}
