namespace Strata.Core;

/// <summary>How a widget behaves when automations reshape the dock.</summary>
public enum WidgetRole
{
    /// <summary>Always kept (clock, battery, volume, focus timer, calendar).</summary>
    Essential,
    /// <summary>System status that matters during problems (CPU, memory, network).</summary>
    Status,
    /// <summary>Useful but tempting; hidden during focus sessions (media, weather, screen time).</summary>
    Distracting,
    /// <summary>Tools you open on purpose (todos, notes).</summary>
    Tool,
}

/// <summary>Pure description of a widget, with no UI. The UI factory lives in Widgets.WidgetCatalog.</summary>
public sealed record WidgetDescriptor(
    string Id,
    string Name,
    string Group,
    string Description,
    WidgetRole Role,
    IReadOnlyList<string> Consumes);

/// <summary>
/// The widget manifest. Adding a widget means one entry here plus one factory in WidgetCatalog;
/// tests check the two stay in sync and that every consumed signal exists.
/// </summary>
public static class WidgetManifest
{
    public static readonly IReadOnlyList<WidgetDescriptor> All =
    [
        new("clock", "Clock", "System", "Time and date; click for a calendar", WidgetRole.Essential, ["time.minute"]),
        new("cpu", "CPU", "System", "Processor usage with a live chart", WidgetRole.Status, ["system.cpu"]),
        new("ram", "Memory", "System", "Memory in use", WidgetRole.Status, ["system.memory"]),
        new("network", "Network", "System", "Connection and speed", WidgetRole.Status, ["network.state"]),
        new("battery", "Battery", "System", "Charge level (hidden on desktops)", WidgetRole.Essential, ["power.battery"]),
        new("volume", "Volume", "Media", "Scroll to change, click for the slider and quick settings", WidgetRole.Essential, []),
        new("media", "Now playing", "Media", "Track, artwork and controls for any player", WidgetRole.Distracting, ["media.playback"]),
        new("calendar", "Calendar", "Productivity", "Your next event, with reminders", WidgetRole.Essential, ["calendar.state"]),
        new("todos", "Todos", "Productivity", "A quick todo list", WidgetRole.Tool, []),
        new("notes", "Notes", "Productivity", "A scratchpad that saves as you type", WidgetRole.Tool, []),
        new("pomodoro", "Focus timer", "Productivity", "Pomodoro sessions and breaks", WidgetRole.Essential, ["focus.pomodoro"]),
        new("weather", "Weather", "Productivity", "Current weather and 6-day forecast", WidgetRole.Distracting, ["weather.current"]),
        new("screentime", "Screen time", "Productivity", "Where your day went, by app", WidgetRole.Distracting, ["screentime.today"]),
        new("aiusage", "AI usage", "Productivity", "Claude and Codex plan limits, with reset times", WidgetRole.Status, []),
    ];

    public static WidgetDescriptor? Find(string id) => All.FirstOrDefault(w => w.Id == id);

    public static IReadOnlyList<string> WithRole(WidgetRole role) => All.Where(w => w.Role == role).Select(w => w.Id).ToList();
}
