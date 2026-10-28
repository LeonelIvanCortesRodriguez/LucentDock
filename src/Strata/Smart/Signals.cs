namespace Strata.Smart;

public sealed record BatteryState(bool HasBattery, int Percent, bool Charging);

public enum NetworkLevel { Unknown, None, LocalOnly, Constrained, Internet }

public sealed record NetworkState(NetworkLevel Level, double DownBytesPerSec, double UpBytesPerSec)
{
    public bool HasInternet => Level == NetworkLevel.Internet;
}

public sealed record ForegroundApp(string Key, string Name);

public sealed record CalendarEventInfo(string Id, string Title, DateTime Start, bool AllDay)
{
    public string Key => $"{Id}:{Start.Ticks}";
}

/// <summary>
/// <see cref="Next"/>: earliest timed event that has not started yet.
/// <see cref="Current"/>: the latest timed event that started within the last hour (a meeting in progress).
/// Kept separate so a reminder for the next event is never blocked by one that just started.
/// </summary>
public sealed record CalendarState(CalendarEventInfo? Next, CalendarEventInfo? Current, int TodayCount);

public enum FocusPhase { Work, Break }

public sealed record PomodoroState(bool Running, FocusPhase Phase, TimeSpan Remaining, int CompletedToday);

public sealed record MediaState(bool HasSession, bool Playing, string Title, string Artist, string SourceApp);

public sealed record WeatherState(string City, int Code, double Temperature, bool IsDay);

/// <summary>Whether it is evening, from sunset at the weather location or a fixed local time.</summary>
public sealed record SunState(DateTime? Sunrise, DateTime? Sunset, bool IsEvening, string Basis);

public sealed record ActivityState(bool Idle, TimeSpan IdleFor);

public sealed record ScreenTimeState(int TodaySeconds, string? TopApp);

public sealed record NotificationState(bool QuietHours, int SentToday, DateTime? LastSent);

/// <summary>Every signal the dock knows about. Widgets and automations refer to these keys, never to each other.</summary>
public static class Signals
{
    public static readonly SignalKey<DateTime> Minute = new("time.minute", TimeSpan.FromMinutes(2), "Local time, once a minute");
    public static readonly SignalKey<SunState> Sun = new("time.sun", TimeSpan.FromHours(2), "Sunrise, sunset and whether it is evening");
    public static readonly SignalKey<BatteryState> Battery = new("power.battery", TimeSpan.FromSeconds(30), "Battery level and charging");
    public static readonly SignalKey<double> Cpu = new("system.cpu", TimeSpan.FromSeconds(5), "Processor use, 0..1");
    public static readonly SignalKey<double> Memory = new("system.memory", TimeSpan.FromSeconds(5), "Memory use, 0..1");
    public static readonly SignalKey<NetworkState> Network = new("network.state", TimeSpan.FromSeconds(15), "Connectivity level and throughput");
    public static readonly SignalKey<ForegroundApp> Foreground = new("app.foreground", TimeSpan.FromSeconds(5), "App in front");
    public static readonly SignalKey<ScreenTimeState> ScreenTime = new("screentime.today", TimeSpan.FromMinutes(2), "Active time today");
    public static readonly SignalKey<CalendarState> Calendar = new("calendar.state", TimeSpan.FromMinutes(2), "Next calendar event");
    public static readonly SignalKey<PomodoroState> Pomodoro = new("focus.pomodoro", TimeSpan.FromSeconds(5), "Focus timer state");
    public static readonly SignalKey<MediaState> Media = new("media.playback", TimeSpan.FromSeconds(15), "Now playing");
    public static readonly SignalKey<WeatherState> Weather = new("weather.current", TimeSpan.FromHours(2), "Current weather");
    public static readonly SignalKey<ActivityState> Activity = new("user.activity", TimeSpan.FromSeconds(5), "Whether the user is active");
    public static readonly SignalKey<NotificationState> Notifications = new("notify.state", TimeSpan.FromDays(1), "Quiet hours and notifications sent");

    public static readonly IReadOnlyList<SignalKey> All =
        [Minute, Sun, Battery, Cpu, Memory, Network, Foreground, ScreenTime, Calendar, Pomodoro, Media, Weather, Activity, Notifications];

    public static SignalKey? Find(string id) => All.FirstOrDefault(k => k.Id == id);
}
