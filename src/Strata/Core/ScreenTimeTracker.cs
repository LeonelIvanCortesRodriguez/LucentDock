using Strata.Native;

namespace Strata.Core;

public sealed class AppUsage
{
    public string Name { get; set; } = "";
    public string? ExePath { get; set; }
    public string? Aumid { get; set; }
    public int Seconds { get; set; }
}

public sealed class DayUsage
{
    public string Date { get; set; } = "";
    public Dictionary<string, AppUsage> Apps { get; set; } = new();
    /// <summary>Active seconds in each hour of the day (24 entries).</summary>
    public int[] Hours { get; set; } = new int[24];
    public int TotalSeconds => Hours.Sum();
}

/// <summary>
/// Counts which app is in the foreground each second while you are at the PC.
/// Time stops counting after two minutes without keyboard or mouse input.
/// Stored per day in %APPDATA%\Strata\screentime, never uploaded.
/// </summary>
public sealed class ScreenTimeTracker
{
    private static readonly TimeSpan IdleCutoff = TimeSpan.FromMinutes(2);
    // Validated by PID and process start time, so a recycled window handle is never credited to the wrong app.
    private readonly WindowIdentityCache _identities;
    private DayUsage _today;
    private DateTime _lastSave = Smart.AppClock.Now;
    private bool _dirty;

    public bool Enabled { get; set; } = true;

    public ScreenTimeTracker(IProcessInfo? processes = null)
    {
        _identities = new WindowIdentityCache(processes ?? new Win32ProcessInfo(), WindowTracker.Identify, capacity: 500);
        _today = LoadDay(Smart.AppClock.Today);
    }

    /// <summary>Largest app by time today, for the screen-time signal.</summary>
    public string? TopApp => _today.Apps.Values.OrderByDescending(a => a.Seconds).FirstOrDefault()?.Name;

    public DayUsage Today
    {
        get
        {
            RollOverIfNeeded();
            return _today;
        }
    }

    private static string FileFor(DateTime day) => Path.Combine("screentime", $"{day:yyyy-MM-dd}.json");

    public static DayUsage LoadDay(DateTime day)
    {
        var d = Storage.Load<DayUsage>(FileFor(day));
        d.Date = day.ToString("yyyy-MM-dd");
        if (d.Hours.Length != 24) d.Hours = new int[24];
        return d;
    }

    private void RollOverIfNeeded()
    {
        string today = Smart.AppClock.Today.ToString("yyyy-MM-dd");
        if (_today.Date == today) return;
        Save();
        _today = LoadDay(Smart.AppClock.Today);
        _identities.Clear();
    }

    /// <summary>Called once per second.</summary>
    public void Tick()
    {
        if (!Enabled) return;
        PrivacyGuard.ThrowIfPreview("live screen-time tracking");
        RollOverIfNeeded();
        if (Win32.GetIdleTime() > IdleCutoff) return;

        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        var app = _identities.Resolve(fg);
        if (app is null) return;

        string exeName = Path.GetFileName(app.ExePath ?? "");
        // The lock screen and the desktop itself are not app time.
        if (exeName.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase)) return;
        if (exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
            Win32.GetClassNameOf(fg) is "Progman" or "WorkerW") return;
        if (exeName.Equals("Strata.exe", StringComparison.OrdinalIgnoreCase)) return;

        if (!_today.Apps.TryGetValue(app.Key, out var usage))
        {
            usage = new AppUsage { Name = app.DisplayName, ExePath = app.ExePath, Aumid = app.Aumid };
            _today.Apps[app.Key] = usage;
        }
        usage.Seconds++;
        _today.Hours[Smart.AppClock.Now.Hour]++;
        _dirty = true;

        if (Smart.AppClock.Now - _lastSave > TimeSpan.FromMinutes(1)) Save();
    }

    public void Save()
    {
        if (!_dirty) return;
        Storage.Save(FileFor(DateTime.ParseExact(_today.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), _today);
        _lastSave = Smart.AppClock.Now;
        _dirty = false;
    }

    public static string Format(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m";
        return $"{t.Seconds}s";
    }
}
