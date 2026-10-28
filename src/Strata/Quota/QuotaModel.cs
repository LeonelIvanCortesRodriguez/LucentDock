using System.Globalization;
using System.Text.Json.Serialization;

namespace Strata.Quota;

public enum QuotaProvider { Claude, Codex }

/// <summary>What a quota window measures. The tile shows the kind the user picked for each provider.</summary>
public enum QuotaWindowKind { Session, Weekly, Model, Spend, Other }

/// <summary>
/// One quota window as the provider reported it. The provider's own figure is kept exactly
/// (<see cref="ReportedUsedPercent"/>, or <see cref="ReportedRemainingPercent"/> where that is what it reports);
/// the other side is derived as 100 minus it and never stored. Nothing here is secret.
/// </summary>
public sealed record QuotaWindow
{
    /// <summary>Stable id, e.g. "five_hour", "seven_day", "codex/primary", "gpt-5.5-pro/secondary".</summary>
    public required string Id { get; init; }
    /// <summary>What the UI calls it, e.g. "Session (5h)", "Weekly", "GPT-5.5 Pro · weekly".</summary>
    public required string Label { get; init; }
    public QuotaWindowKind Kind { get; init; }
    public double? ReportedUsedPercent { get; init; }
    public double? ReportedRemainingPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public int? WindowMinutes { get; init; }
    /// <summary>The provider says this limit is reached (or it is at 100% used).</summary>
    public bool Reached { get; init; }
    /// <summary>Extra provider facts for the flyout, e.g. "$314.12 of $500 (monthly)".</summary>
    public string? Detail { get; init; }

    // Derived, never stored: storage keeps only what the provider reported.
    [JsonIgnore] public double? UsedPercent => ReportedUsedPercent ?? (ReportedRemainingPercent is double r ? 100 - r : null);
    [JsonIgnore] public double? RemainingPercent => ReportedRemainingPercent ?? (ReportedUsedPercent is double u ? 100 - u : null);
}

/// <summary>Credits as the provider states them. Values stay strings: providers send decimal strings and units vary.</summary>
public sealed record QuotaCredits(bool? HasCredits, bool? Unlimited, string? Balance, string? Unit, string? Total, string? Period);

/// <summary>Codex rate-limit reset credits: how many are available and when the next one expires.</summary>
public sealed record ResetCreditsInfo(long AvailableCount, DateTimeOffset? NextExpiry);

/// <summary>A normalized, non-secret reading from one provider. This is all Strata ever stores.</summary>
public sealed record QuotaSnapshot
{
    public required QuotaProvider Provider { get; init; }
    /// <summary>Where it came from: "Codex app-server", "Claude Code status line" or "Sample data".</summary>
    public required string Source { get; init; }
    public string? Plan { get; init; }
    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];
    public QuotaCredits? Credits { get; init; }
    public ResetCreditsInfo? ResetCredits { get; init; }
    /// <summary>True or false only when the provider says so; null means unknown (never inferred from percentages).</summary>
    public bool? Blocked { get; init; }
    public string? BlockedReason { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public bool IsSample { get; init; }
}

public enum QuotaStatus { Disabled, NotConnected, WaitingForFirstSample, Fresh, Stale, Error }

public enum QuotaErrorKind { None, NotInstalled, SignInRequired, RateLimited, Offline, BadResponse, Timeout, Failed }

/// <summary>What the UI knows about one provider right now.</summary>
public sealed record ProviderState(
    QuotaProvider Provider,
    QuotaStatus Status,
    QuotaSnapshot? Snapshot,
    QuotaErrorKind Error = QuotaErrorKind.None,
    string? ErrorText = null,
    DateTimeOffset? LastAttempt = null)
{
    public static ProviderState Waiting(QuotaProvider p) => new(p, QuotaStatus.WaitingForFirstSample, null);
}

/// <summary>A provider failed in a way the UI should explain (sign in again, offline, ...).</summary>
public sealed class QuotaSourceException(QuotaErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public QuotaErrorKind Kind { get; } = kind;
}

public enum WindowPhase { Current, ResetPassed, Unavailable }

public enum QuotaSeverity { Normal, Warning, Critical }

/// <summary>
/// How a window reads in the UI. Every percentage says "used" or "remaining"; missing data reads
/// "Unavailable", never 0; a window whose reset time has passed waits for a new sample instead of guessing.
/// </summary>
public static class QuotaFormat
{
    public const string Unavailable = "Unavailable";
    public const string WaitingForFirstSample = "Waiting for first sample";
    public const string WaitingAfterReset = "Reset; waiting for a new sample";

    public static WindowPhase Phase(QuotaWindow w, DateTimeOffset now) =>
        w.UsedPercent is null ? WindowPhase.Unavailable
        : w.ResetsAt is { } r && r <= now ? WindowPhase.ResetPassed
        : WindowPhase.Current;

    /// <summary>"7% remaining" or "93% used"; "Unavailable" or the reset notice when there is no current figure.</summary>
    public static string Value(QuotaWindow w, DateTimeOffset now, bool remaining) => Phase(w, now) switch
    {
        WindowPhase.Unavailable => Unavailable,
        WindowPhase.ResetPassed => WaitingAfterReset,
        _ => remaining ? Remaining(w.RemainingPercent!.Value) : Used(w.UsedPercent!.Value),
    };

    public static string Used(double used) => $"{Number(used)}% used";

    /// <summary>Remaining below zero (a spend limit can exceed 100% used) reads "0% remaining (over limit)".</summary>
    public static string Remaining(double remaining) => remaining < 0 ? "0% remaining (over limit)" : $"{Number(remaining)}% remaining";

    /// <summary>
    /// At most one decimal, so used and remaining always add up to 100 as shown ("64.5% used · 35.5% remaining").
    /// Rounding never reaches a limit that hasn't been hit (99.96 reads "99.9", not "100") and never shows a
    /// non-zero amount as 0 ("&lt;0.1").
    /// </summary>
    public static string Number(double value)
    {
        string s = value.ToString("0.#", CultureInfo.InvariantCulture);
        if (s == "100" && value < 100) return "99.9";
        if (s == "0" && value > 0) return "<0.1";
        return s;
    }

    /// <summary>Time until reset: "1h 8m", "2d 13h", "45m", "&lt;1m"; null when there is no reset time.</summary>
    public static string? Countdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } r) return null;
        var d = r - now;
        if (d <= TimeSpan.Zero) return "now";
        if (d < TimeSpan.FromMinutes(1)) return "<1m";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalDays}d {d.Hours}h";
    }

    /// <summary>Absolute local reset time: "today 14:05", "tomorrow 9:00 AM", "Fri 11:41", "28 Oct 11:41".</summary>
    public static string? ResetLocal(DateTimeOffset? resetsAt, DateTimeOffset now, bool use24Hour)
    {
        if (resetsAt is not { } r) return null;
        var local = r.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string time = local.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        int days = (local.Date - today).Days;
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            > 1 and < 7 => $"{local.ToString("ddd", CultureInfo.InvariantCulture)} {time}",
            _ => $"{local.ToString("d MMM", CultureInfo.InvariantCulture)} {time}",
        };
    }

    /// <summary>"just now", "3m ago", "2h ago", "3d ago".</summary>
    public static string Age(DateTimeOffset observedAt, DateTimeOffset now)
    {
        var d = now - observedAt;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    /// <summary>
    /// The local clock disagrees with the provider: the reading is from the future, or the reset is further away
    /// than the window is long. Countdowns are then unreliable, so the UI shows the absolute time only.
    /// </summary>
    public static bool ClockSkewSuspected(QuotaSnapshot s, QuotaWindow w, DateTimeOffset now)
    {
        if (s.ObservedAt > now + TimeSpan.FromMinutes(2)) return true;
        return w.ResetsAt is { } r && w.WindowMinutes is int m && m > 0 && r - now > TimeSpan.FromMinutes(m + 10);
    }

    /// <summary>Warning colours apply only at the configured thresholds (remaining percent), or when the limit is reached.</summary>
    public static QuotaSeverity Severity(QuotaWindow w, DateTimeOffset now, int warnRemaining, int criticalRemaining)
    {
        if (Phase(w, now) != WindowPhase.Current) return QuotaSeverity.Normal;
        if (w.Reached) return QuotaSeverity.Critical;
        double remaining = w.RemainingPercent!.Value;
        return remaining <= criticalRemaining ? QuotaSeverity.Critical
            : remaining <= warnRemaining ? QuotaSeverity.Warning
            : QuotaSeverity.Normal;
    }

    public static string ProviderName(QuotaProvider p) => p == QuotaProvider.Claude ? "Claude" : "Codex";

    /// <summary>Window label from its length: 300 minutes is the session window, 10080 the weekly one.</summary>
    public static (string Label, QuotaWindowKind Kind) DurationLabel(int? minutes) => minutes switch
    {
        300 => ("Session (5h)", QuotaWindowKind.Session),
        10080 => ("Weekly", QuotaWindowKind.Weekly),
        null => ("Window", QuotaWindowKind.Other),
        < 60 => ($"{minutes}-minute window", QuotaWindowKind.Other),
        < 1440 => ($"{minutes / 60}-hour window", minutes <= 360 ? QuotaWindowKind.Session : QuotaWindowKind.Other),
        _ => ($"{minutes / 1440}-day window", QuotaWindowKind.Other),
    };
}

public enum QuotaTileMode { Claude, Codex, MostConstrained, Rotate }

/// <summary>One thing the dock tile can show: a provider and, if it has one, the window to show.</summary>
public sealed record TileItem(QuotaProvider Provider, ProviderState State, QuotaWindow? Window);

/// <summary>Chooses what the tile shows. It always shows one provider's one window: never a blend of two.</summary>
public static class QuotaSelector
{
    /// <summary>The window of the preferred kind, else the first current window, else the first window.</summary>
    public static QuotaWindow? Preferred(QuotaSnapshot? s, QuotaWindowKind kind, DateTimeOffset now)
    {
        if (s is null || s.Windows.Count == 0) return null;
        return s.Windows.FirstOrDefault(w => w.Kind == kind && QuotaFormat.Phase(w, now) == WindowPhase.Current)
            ?? s.Windows.FirstOrDefault(w => w.Kind == kind)
            ?? s.Windows.FirstOrDefault(w => QuotaFormat.Phase(w, now) == WindowPhase.Current)
            ?? s.Windows[0];
    }

    /// <summary>The single window with the least remaining among providers whose data is fresh; ties go to the earlier reset.</summary>
    public static TileItem? MostConstrained(IEnumerable<ProviderState> states, DateTimeOffset now) =>
        states.Where(st => st.Status == QuotaStatus.Fresh && st.Snapshot is not null)
            .SelectMany(st => st.Snapshot!.Windows.Where(w => QuotaFormat.Phase(w, now) == WindowPhase.Current)
                .Select(w => new TileItem(st.Provider, st, w)))
            .OrderBy(t => t.Window!.RemainingPercent)
            .ThenBy(t => t.Window!.ResetsAt ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

    public static IReadOnlyList<TileItem> TileItems(QuotaTileMode mode, ProviderState claude, ProviderState codex,
        QuotaWindowKind claudeWindow, QuotaWindowKind codexWindow, DateTimeOffset now)
    {
        TileItem For(ProviderState st, QuotaWindowKind kind) => new(st.Provider, st, Preferred(st.Snapshot, kind, now));
        var enabled = new[] { (claude, claudeWindow), (codex, codexWindow) }.Where(x => x.Item1.Status != QuotaStatus.Disabled).ToList();
        return mode switch
        {
            QuotaTileMode.Claude => [For(claude, claudeWindow)],
            QuotaTileMode.Codex => [For(codex, codexWindow)],
            QuotaTileMode.Rotate => enabled.Count == 0 ? [For(claude, claudeWindow)] : enabled.Select(x => For(x.Item1, x.Item2)).ToList(),
            // Nothing fresh to compare: fall back to each enabled provider's own window rather than inventing a figure.
            _ => MostConstrained([claude, codex], now) is { } most ? [most]
                : enabled.Count == 0 ? [For(claude, claudeWindow)] : enabled.Select(x => For(x.Item1, x.Item2)).ToList(),
        };
    }
}
