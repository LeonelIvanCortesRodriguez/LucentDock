using System.Globalization;
using Strata.Core;

namespace Strata.Smart;

/// <summary>The built-in automations. Each one reads only signals and acts only through <see cref="RuleAction"/>s.</summary>
public static class Automations
{
    public const string MeetingPrep = "meeting-prep";
    public const string LowBattery = "low-battery";
    public const string FocusMode = "focus-mode";
    public const string SunsetTheme = "sunset-theme";
    public const string ResourcePressure = "resource-pressure";
    public const string Connectivity = "connectivity";
    public const string ScreenTimeCheck = "screen-time";
    public const string EventReminder = "event-reminder";
    public const string NightLayout = "night-layout";

    /// <summary>What the night layout hides: system and work status, nothing you use to wind down.</summary>
    public static readonly IReadOnlyList<string> NightHidden = ["cpu", "ram", "network", "aiusage"];

    private static bool IsNight(RuleContext ctx) => SunCalculator.IsEveningBySetTime(ctx.Now.TimeOfDay,
        SunCalculator.ParseTimeOfDay(ctx.Text("from"), TimeSpan.FromHours(22)), SunCalculator.ParseTimeOfDay(ctx.Text("to"), TimeSpan.FromHours(7)));

    private static string Pct(double v) => $"{Math.Round(v * 100)}%";
    private static string Time(DateTime t) => t.ToString("t", CultureInfo.CurrentCulture);

    private static double MinutesUntil(CalendarEventInfo e, RuleContext ctx) => (e.Start - ctx.Now).TotalMinutes;

    /// <summary>The event meeting preparation is about: the next one if it is close, else one in progress.</summary>
    private static CalendarEventInfo? MeetingFocus(CalendarState c, RuleContext ctx)
    {
        if (c.Next is { AllDay: false } n && MinutesUntil(n, ctx) <= ctx.Number("lead")) return n;
        if (c.Current is { AllDay: false } cur && -MinutesUntil(cur, ctx) <= ctx.Number("hold")) return cur;
        return null;
    }

    public static IReadOnlyList<RuleDefinition> Create()
    {
        var distracting = WidgetManifest.WithRole(WidgetRole.Distracting);

        return
        [
            new RuleDefinition
            {
                Id = MeetingPrep,
                Name = "Meeting preparation",
                Description = "Before and during an event, bring the calendar, clock, volume, battery and network to the front, and hide media, weather and screen time. Put the dock back afterwards.",
                Priority = 60,
                Policy = FirePolicy.OncePerKey,
                When = Condition.On(Signals.Calendar,
                    (c, ctx) => MeetingFocus(c, ctx) is not null,
                    (c, ctx) => MeetingFocus(c, ctx) is not { } n ? "no event close enough"
                        : MinutesUntil(n, ctx) >= 0 ? $"\"{n.Title}\" starts in {Math.Ceiling(MinutesUntil(n, ctx))} min"
                        : $"\"{n.Title}\" started {Math.Floor(-MinutesUntil(n, ctx))} min ago",
                    Signals.Minute),
                DedupKey = ctx => ctx.Value(Signals.Calendar) is { } c ? MeetingFocus(c, ctx)?.Key : null,
                Actions =
                [
                    new PromoteWidgets(["calendar", "clock", "volume", "battery", "network"], _ => ["calendar"]),
                    new CollapseWidgets(distracting, enabledParam: "quiet"),
                    new SuggestApp("meetingApp", "meeting"),
                ],
                Parameters =
                [
                    new("lead", "Minutes before the event", "10", Min: 1, Max: 120, Unit: "min"),
                    new("hold", "Keep it up after the start for", "15", Min: 0, Max: 120, Unit: "min"),
                    new("quiet", "Hide media, weather and screen time meanwhile", "1", ParameterKind.Toggle),
                    new("meetingApp", "Suggest opening this pinned app", "", ParameterKind.Text),
                ],
                WidgetsTouched = ["calendar", "clock", "volume", "battery", "network", .. distracting],
            },

            new RuleDefinition
            {
                Id = LowBattery,
                Name = "Low battery",
                Description = "When the battery runs low and isn't charging, put the battery widget first and send one notification.",
                Priority = 90,
                Policy = FirePolicy.OnTransition,
                Cooldown = TimeSpan.FromMinutes(30),
                When = Condition.On(Signals.Battery,
                    (b, ctx) => b.HasBattery && !b.Charging && b.Percent <= ctx.Number("threshold"),
                    (b, ctx) => !b.HasBattery ? "no battery" : b.Charging ? $"charging at {b.Percent}%"
                        : $"battery at {b.Percent}% (alert at {ctx.Number("threshold")}%) and not charging"),
                // Hysteresis: stay active until charging or 5 points above the threshold, so it can't flap.
                ExitWhen = Condition.On(Signals.Battery,
                    (b, ctx) => !b.HasBattery || b.Charging || b.Percent >= ctx.Number("threshold") + 5,
                    (b, _) => b.Charging ? "charger connected" : $"battery back to {b.Percent}%"),
                Actions =
                [
                    new PromoteWidgets(["battery"], _ => ["battery"]),
                    new Notify(ctx => ("Battery low", $"{ctx.Value(Signals.Battery)?.Percent}% left. Plug in soon."),
                        _ => "battery-low", TimeSpan.FromMinutes(30)),
                ],
                Parameters = [new("threshold", "Alert at", "20", Min: 5, Max: 50, Unit: "%")],
                WidgetsTouched = ["battery"],
            },

            new RuleDefinition
            {
                Id = FocusMode,
                Name = "Focus mode",
                Description = "During a focus session, hide distracting widgets (" + string.Join(", ", distracting) + ") and keep essentials. Restore them when focus ends.",
                Priority = 70,
                Policy = FirePolicy.OnTransition,
                When = Condition.On(Signals.Pomodoro,
                    (p, _) => p.Running && p.Phase == FocusPhase.Work,
                    (p, _) => p.Running && p.Phase == FocusPhase.Work ? $"focus session running ({(int)p.Remaining.TotalMinutes} min left)"
                        : p.Running ? "on a break" : "focus timer stopped"),
                Actions =
                [
                    new CollapseWidgets(distracting),
                    new PromoteWidgets(["pomodoro"], _ => ["pomodoro"]),
                ],
                WidgetsTouched = [.. distracting, "pomodoro"],
            },

            new RuleDefinition
            {
                Id = SunsetTheme,
                Name = "Sunset theme",
                Description = "In the evening (after sunset at your weather city, or from a set time), switch to the evening style. Choosing a style yourself overrides it until morning.",
                Priority = 20,
                Policy = FirePolicy.OnTransition,
                When = Condition.On(Signals.Sun,
                    (s, _) => s.IsEvening,
                    (s, _) => s.Basis == "location" && s.Sunset is { } set
                        ? (s.IsEvening ? $"sunset was at {Time(set)}" : $"sunset is at {Time(set)}")
                        : s.IsEvening ? "evening hours (set time)" : "daytime"),
                Actions = [new SwitchTheme(ctx => DockStyles.Normalize((DockStyle)(int)ctx.Number("style")))],
                Parameters =
                [
                    new("style", "Evening style", ((int)DockStyle.Dark).ToString(CultureInfo.InvariantCulture), ParameterKind.Style),
                    new("eveningStart", "Evening starts (when no weather city is set)", "19:00", ParameterKind.Text),
                    new("morningStart", "Morning starts", "07:00", ParameterKind.Text),
                ],
            },

            new RuleDefinition
            {
                Id = ResourcePressure,
                Name = "Resource pressure",
                Description = "When CPU or memory stays high, show CPU and memory up front. A short spike doesn't count.",
                Priority = 50,
                Policy = FirePolicy.OnTransition,
                Cooldown = TimeSpan.FromMinutes(10),
                SustainParam = "sustain",
                When = Condition.Any(
                    Condition.On(Signals.Cpu, (v, ctx) => v * 100 >= ctx.Number("cpu"), (v, ctx) => $"CPU at {Pct(v)} (limit {ctx.Number("cpu")}%)"),
                    Condition.On(Signals.Memory, (v, ctx) => v * 100 >= ctx.Number("memory"), (v, ctx) => $"memory at {Pct(v)} (limit {ctx.Number("memory")}%)")),
                ExitWhen = Condition.All(
                    Condition.On(Signals.Cpu, (v, ctx) => v * 100 < ctx.Number("cpu") - 15, (v, _) => $"CPU down to {Pct(v)}"),
                    Condition.On(Signals.Memory, (v, ctx) => v * 100 < ctx.Number("memory") - 5, (v, _) => $"memory down to {Pct(v)}")),
                Actions =
                [
                    new PromoteWidgets(["cpu", "ram"], ctx =>
                    {
                        var hot = new List<string>();
                        if (ctx.Signals.TryGetFresh(Signals.Cpu, out var c) && c * 100 >= ctx.Number("cpu")) hot.Add("cpu");
                        if (ctx.Signals.TryGetFresh(Signals.Memory, out var m) && m * 100 >= ctx.Number("memory")) hot.Add("ram");
                        return hot;
                    }),
                    new Notify(_ => ("Your PC is working hard", "CPU or memory has been high for a while."),
                        _ => "resource-pressure", TimeSpan.FromHours(1), enabledParam: "notify"),
                ],
                Parameters =
                [
                    new("cpu", "CPU limit", "85", Min: 10, Max: 100, Unit: "%"),
                    new("memory", "Memory limit", "90", Min: 10, Max: 100, Unit: "%"),
                    new("sustain", "Must last at least", "60", Min: 5, Max: 3600, Unit: "s"),
                    new("notify", "Also send a notification", "0", ParameterKind.Toggle),
                ],
                WidgetsTouched = ["cpu", "ram"],
            },

            new RuleDefinition
            {
                Id = Connectivity,
                Name = "Connectivity",
                Description = "If the internet connection drops or becomes limited for a while, show the network widget and notify once per outage.",
                Priority = 80,
                Policy = FirePolicy.OnTransition,
                SustainParam = "sustain",
                When = Condition.On(Signals.Network,
                    (n, _) => n.Level is NetworkLevel.None or NetworkLevel.LocalOnly or NetworkLevel.Constrained,
                    (n, _) => n.Level switch
                    {
                        NetworkLevel.None => "no network connection",
                        NetworkLevel.LocalOnly => "connected to the network but not the internet",
                        NetworkLevel.Constrained => "internet access is limited",
                        _ => "online",
                    }),
                ExitWhen = Condition.On(Signals.Network, (n, _) => n.Level == NetworkLevel.Internet, (_, _) => "back online"),
                Actions =
                [
                    new PromoteWidgets(["network"], _ => ["network"]),
                    new Notify(ctx => ctx.Value(Signals.Network)?.Level == NetworkLevel.None
                            ? ("No internet connection", "The network widget will stay up front until you're back online.")
                            : ("Limited internet connection", "Some apps may not work. The network widget will stay up front."),
                        _ => "connectivity", TimeSpan.FromMinutes(15)),
                ],
                Parameters = [new("sustain", "Must last at least", "20", Min: 5, Max: 600, Unit: "s")],
                WidgetsTouched = ["network"],
            },

            new RuleDefinition
            {
                Id = ScreenTimeCheck,
                Name = "Screen-time check",
                Description = "Once a day, after a set amount of screen time, suggest a break and briefly put screen time first. Waits for quiet hours to end.",
                Priority = 40,
                Policy = FirePolicy.OncePerDay,
                DeferDuringQuietHours = true,
                When = Condition.On(Signals.ScreenTime,
                    (s, ctx) => s.TodaySeconds >= ctx.Number("hours") * 3600,
                    (s, ctx) => $"{RuleEngine.FormatSpan(TimeSpan.FromSeconds(s.TodaySeconds))} of screen time today (threshold {ctx.Number("hours")} h)"),
                Actions =
                [
                    new Notify(ctx =>
                        {
                            var s = ctx.Value(Signals.ScreenTime);
                            string top = s?.TopApp is { Length: > 0 } t ? $" Most of it in {t}." : "";
                            return ($"{ctx.Number("hours")} hours of screen time today", "Time for a short break?" + top);
                        },
                        ctx => "screentime:" + RuleEngine.DayKey(ctx.Now), TimeSpan.FromHours(20)),
                    new PromoteWidgets(["screentime"], _ => ["screentime"], durationMinutesParam: "showFor"),
                ],
                Parameters =
                [
                    new("hours", "After", "4", Min: 0.5, Max: 16, Unit: "h"),
                    new("showFor", "Keep screen time up front for", "10", Min: 1, Max: 240, Unit: "min"),
                ],
                WidgetsTouched = ["screentime"],
            },

            new RuleDefinition
            {
                Id = EventReminder,
                Name = "Upcoming-event reminder",
                Description = "Notify shortly before each calendar event, once per event. Respects quiet hours.",
                Priority = 85,
                Policy = FirePolicy.OncePerKey,
                When = Condition.On(Signals.Calendar,
                    (c, ctx) => c.Next is { AllDay: false } n && MinutesUntil(n, ctx) <= ctx.Number("minutes"),
                    (c, ctx) => c.Next is { } n ? $"\"{n.Title}\" at {Time(n.Start)}" : "no upcoming event",
                    Signals.Minute),
                DedupKey = ctx => ctx.Value(Signals.Calendar)?.Next?.Key,
                Actions =
                [
                    new Notify(ctx =>
                        {
                            var n = ctx.Value(Signals.Calendar)!.Next!;
                            int mins = Math.Max(0, (int)Math.Ceiling(MinutesUntil(n, ctx)));
                            return (n.Title, mins == 0 ? "Starting now" : $"In {mins} min, at {Time(n.Start)}");
                        },
                        ctx => "event:" + ctx.Value(Signals.Calendar)?.Next?.Key, TimeSpan.FromHours(6)),
                ],
                Parameters = [new("minutes", "Remind me", "5", Min: 0, Max: 120, Unit: "min before")],
                WidgetsTouched = [],
            },

            new RuleDefinition
            {
                Id = NightLayout,
                Name = "Night layout",
                Description = "At night, a calmer dock: hide CPU, memory, network and AI usage. They come back in the morning, "
                    + "and an alert (low battery, a lost connection, a busy PC) still brings its widget forward.",
                Priority = 15,
                Policy = FirePolicy.OnTransition,
                When = Condition.On(Signals.Minute,
                    (_, ctx) => IsNight(ctx),
                    (_, ctx) => IsNight(ctx) ? $"night ({ctx.Text("from")} to {ctx.Text("to")})" : $"daytime (night starts at {ctx.Text("from")})"),
                Actions = [new CollapseWidgets(NightHidden)],
                Parameters =
                [
                    new("from", "Night starts", "22:00", ParameterKind.Text),
                    new("to", "Morning starts", "07:00", ParameterKind.Text),
                ],
                WidgetsTouched = [.. NightHidden],
            },
        ];
    }
}
