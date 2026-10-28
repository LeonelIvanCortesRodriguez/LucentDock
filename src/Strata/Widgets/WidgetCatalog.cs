namespace Strata.Widgets;

public sealed record WidgetInfo(string Id, string Name, string Group, string Description, Func<IDockHost, Widget> Create);

public static class WidgetCatalog
{
    public static readonly IReadOnlyList<WidgetInfo> All =
    [
        new("clock", "Clock", "System", "Time and date; click for a calendar", h => new ClockWidget(h)),
        new("cpu", "CPU", "System", "Processor usage with a live chart", h => new CpuWidget(h)),
        new("ram", "Memory", "System", "Memory in use", h => new RamWidget(h)),
        new("network", "Network", "System", "Download and upload speed", h => new NetworkWidget(h)),
        new("battery", "Battery", "System", "Charge level (hidden on desktops)", h => new BatteryWidget(h)),
        new("volume", "Volume", "Media", "Scroll to change, click for the slider", h => new VolumeWidget(h)),
        new("media", "Now playing", "Media", "Track, artwork and controls for any player", h => new MediaWidget(h)),
        new("calendar", "Calendar", "Productivity", "Your next event, with reminders", h => new CalendarWidget(h)),
        new("todos", "Todos", "Productivity", "A quick todo list", h => new TodoWidget(h)),
        new("notes", "Notes", "Productivity", "A scratchpad that saves as you type", h => new NotesWidget(h)),
        new("pomodoro", "Focus timer", "Productivity", "Pomodoro sessions and breaks", h => new PomodoroWidget(h)),
        new("weather", "Weather", "Productivity", "Current weather and 6-day forecast", h => new WeatherWidget(h)),
        new("screentime", "Screen time", "Productivity", "Where your day went, by app", h => new ScreenTimeWidget(h)),
        new("aiusage", "AI usage", "Productivity", "Claude and Codex plan limits, with reset times", h => new QuotaWidget(h)),
    ];

    public static WidgetInfo? Find(string id) => All.FirstOrDefault(w => w.Id == id);
}
