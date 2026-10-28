namespace Strata.Core;

public enum DockEdge { Bottom, Top }
public enum DockShape { Pill, FullWidth }
/// <summary>
/// Dock styles. The numbers are stored (the sunset automation saves its style as a number), so they never change.
/// 1.2 offers Glass, Clear (shown as "Liquid glass") and Dark; Frost, Midnight, Black and White are 1.1 styles kept only so older settings
/// still load, and <see cref="DockStyles.Normalize"/> moves them to the nearest 1.2 style.
/// </summary>
public enum DockStyle { Glass = 0, Frost = 1, Midnight = 2, Black = 3, White = 4, Clear = 5, Dark = 6 }

public static class DockStyles
{
    /// <summary>The styles Strata offers, in the order Settings shows them.</summary>
    public static IReadOnlyList<DockStyle> Selectable { get; } = [DockStyle.Glass, DockStyle.Clear, DockStyle.Dark];

    /// <summary>What Settings calls a style. <see cref="DockStyle.Clear"/> is shown as "Liquid glass" (its stored
    /// name stays Clear, so 1.1 settings and the sunset rule's stored style keep working).</summary>
    public static string DisplayName(DockStyle style) => Normalize(style) switch
    {
        DockStyle.Clear => "Liquid glass",
        DockStyle.Dark => "Dark",
        _ => "Glass",
    };

    /// <summary>A 1.1 style becomes the nearest 1.2 one: the light ones (Frost, White) Clear, the dark ones (Midnight, Black) Dark.</summary>
    public static DockStyle Normalize(DockStyle style) => style switch
    {
        DockStyle.Frost or DockStyle.White => DockStyle.Clear,
        DockStyle.Midnight or DockStyle.Black => DockStyle.Dark,
        DockStyle.Clear or DockStyle.Dark => style,
        _ => DockStyle.Glass,
    };
}
public enum DockSize { Small, Medium, Large }
public enum TaskbarMode { Hide, AutoHide, Leave }
public enum AppIdentityKind { Standard, ExplorerFolder }

public sealed class PinnedApp
{
    public string Name { get; set; } = "";
    /// <summary>Shortcut, executable, or empty for packaged apps.</summary>
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>AppUserModelID; used to launch packaged (Store) apps.</summary>
    public string? Aumid { get; set; }
    /// <summary>Executable this pin matches against running windows.</summary>
    public string? ExePath { get; set; }
    /// <summary>Structural shell identity for pins that do not expose an executable or AppUserModelID.</summary>
    public AppIdentityKind IdentityKind { get; set; }

    /// <summary>
    /// The members when this pin is a group (one app dropped onto another): launchers behind one tile, never
    /// nested. Null or empty for an ordinary pin. A group pin's own Path/Aumid stay empty, so it matches no
    /// windows and older builds simply show an empty tile rather than misbehaving.
    /// </summary>
    public List<PinnedApp>? Group { get; set; }
}

public sealed class AppSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    public DockShape Shape { get; set; } = DockShape.Pill;
    public DockStyle Style { get; set; } = DockStyle.Glass;
    public DockSize Size { get; set; } = DockSize.Medium;
    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.Hide;
    public bool ReserveSpace { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowStartButton { get; set; } = true;
    public bool ShowSearchButton { get; set; } = true;

    public List<string> LeftWidgets { get; set; } = ["weather", "media"];
    public List<string> RightWidgets { get; set; } = ["aiusage", "screentime", "cpu", "ram", "volume", "battery", "clock"];

    /// <summary>Widgets added in a later version. Settings from before it get each one on the dock once.</summary>
    public static readonly string[] AddedInLaterVersions = ["aiusage"];

    /// <summary>Which of <see cref="AddedInLaterVersions"/> have been put on the dock once, so one the user then
    /// removes stays removed. Null in settings saved before 1.2.</summary>
    public List<string>? IntroducedWidgets { get; set; }

    /// <summary>Put each widget added in a later version on the dock once (front of the right side).</summary>
    public void IntroduceNewWidgets()
    {
        IntroducedWidgets ??= [];
        foreach (var id in AddedInLaterVersions)
        {
            if (IntroducedWidgets.Contains(id)) continue;
            if (!LeftWidgets.Contains(id) && !RightWidgets.Contains(id)) RightWidgets.Insert(0, id);
            IntroducedWidgets.Add(id);
        }
    }

    public List<PinnedApp> Pinned { get; set; } = [];
    /// <summary>Win+Ctrl+Alt+1…9 open or switch to the dock's first nine apps (<see cref="AppHotkeys"/>).</summary>
    public bool AppShortcuts { get; set; } = true;
    /// <summary>Red unread counts on apps whose window titles show them ("(3) WhatsApp"); see <see cref="UnreadCount"/>.</summary>
    public bool UnreadBadges { get; set; } = true;
    /// <summary>A dock on every screen, each with the windows on that screen; the main one keeps the widgets.</summary>
    public bool DockOnEveryScreen { get; set; }
    /// <summary>Liquid glass opacity, 0 (fully transparent) to 100 (a solid fill).</summary>
    public int GlassOpacity { get; set; } = Theme.DefaultGlassOpacity;
    /// <summary>Where Strata checks for updates: a folder with update.json, or an https address (a GitHub repository,
    /// or an update.json). Empty means updates are off. The name is kept from 1.2's first builds, which had folders only.</summary>
    public string UpdateFolder { get; set; } = "";
    public bool PinnedImported { get; set; }

    public string WeatherCity { get; set; } = "";
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }
    public bool Fahrenheit { get; set; }

    public bool Use24HourClock { get; set; } = !System.Globalization.CultureInfo.CurrentCulture
        .DateTimeFormat.ShortTimePattern.Contains('t');

    public int PomodoroWorkMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;

    public bool TrackScreenTime { get; set; } = true;
    public int EventReminderMinutes { get; set; } = 5;
    public bool QuietHoursEnabled { get; set; } = true;
    public TimeSpan QuietHoursStart { get; set; } = TimeSpan.FromHours(22);
    public TimeSpan QuietHoursEnd { get; set; } = TimeSpan.FromHours(7);

    public Quota.QuotaSettings Quota { get; set; } = new();

    public double DockHeight => Size switch { DockSize.Small => 40, DockSize.Large => 58, _ => 48 };
    public double IconSize => Size switch { DockSize.Small => 24, DockSize.Large => 36, _ => 30 };
    public double FontSize => Size switch { DockSize.Small => 11.5, DockSize.Large => 14, _ => 12.5 };

    private const string FileName = "settings.json";
    public static AppSettings Load()
    {
        var s = Storage.Load<AppSettings>(FileName);
        s.Style = DockStyles.Normalize(s.Style);
        // A loaded or imported file is data, not an invariant: null members, nested groups and empty groups are
        // repaired before anything renders, and group members get their shell identities like any other pin.
        DockGroups.Sanitize(s.Pinned);
        Launcher.RefreshShellIdentities(s.Pinned);
        foreach (var pin in s.Pinned)
            if (pin.Group is { Count: > 0 } members) Launcher.RefreshShellIdentities(members);
        // Not saved here: an unreadable file must not be overwritten just by starting. The next save records it.
        s.IntroduceNewWidgets();
        return s;
    }
    public void Save() => Storage.Save(FileName, this);
}
