namespace Strata.Quota;

/// <summary>One reading of a limit: when, and its used percentage.</summary>
public sealed record QuotaSample(DateTimeOffset At, double Used);

/// <summary>
/// The used figure of each limit over its current reset period (and recent ones), for the forecast and the trend.
/// "provider|window|period" → samples. Figures and times only.
/// </summary>
public sealed class QuotaHistoryStore
{
    public const string FileName = "quota-history.json";
    public Dictionary<string, List<QuotaSample>> Windows { get; set; } = new();
}

/// <summary>Where a limit is heading: when it will be reached at the current pace, or where it will be at the reset.</summary>
public sealed record QuotaProjection(DateTimeOffset? ReachedAt, double UsedAtReset);

public static class QuotaForecast
{
    /// <summary>A new sample is kept every 10 minutes, or sooner when the figure moves by a point or more.</summary>
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(10);
    /// <summary>The pace is measured over at least this long, so one burst is not taken for a trend.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(30);
    public const int MaxSamples = 400;

    public static string Key(QuotaProvider provider, QuotaWindow w) => $"{provider}|{w.Id}|{QuotaAlerts.Period(w.ResetsAt)}";

    /// <summary>Add this reading's figures (fresh, real readings only). Returns true if anything was kept.</summary>
    public static bool Record(QuotaHistoryStore store, ProviderState state, DateTimeOffset now)
    {
        if (state.Status != QuotaStatus.Fresh || state.Snapshot is not { IsSample: false } s) return false;
        bool changed = false;
        foreach (var w in s.Windows)
        {
            if (QuotaFormat.Phase(w, now) != WindowPhase.Current || w.UsedPercent is not double used || w.ResetsAt is null) continue;
            var list = store.Windows.TryGetValue(Key(state.Provider, w), out var l) ? l : store.Windows[Key(state.Provider, w)] = [];
            var last = list.LastOrDefault();
            if (last is not null && s.ObservedAt - last.At < SampleEvery && Math.Abs(used - last.Used) < 1) continue;
            if (last is not null && s.ObservedAt <= last.At) continue;
            list.Add(new QuotaSample(s.ObservedAt, used));
            if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
            changed = true;
        }
        return changed;
    }

    /// <summary>Forget periods that ended more than eight days ago.</summary>
    public static void Prune(QuotaHistoryStore store, DateTimeOffset now)
    {
        foreach (var k in store.Windows.Keys.ToList())
            if (store.Windows[k].Count == 0 || k.Split('|') is { Length: >= 3 } p && DateTimeOffset.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var reset) && reset < now.AddDays(-8))
                store.Windows.Remove(k);
    }

    /// <summary>
    /// The projection for a limit from its samples this period: the pace over the last day (or since the period
    /// began), applied from now. Null until there are readings at least <see cref="MinimumSpan"/> apart.
    /// </summary>
    public static QuotaProjection? Project(IReadOnlyList<QuotaSample> samples, QuotaWindow w, DateTimeOffset now)
    {
        if (w.UsedPercent is not double used || w.ResetsAt is not { } reset || reset <= now) return null;
        var recent = samples.Where(x => x.At >= now.AddDays(-1) && x.At <= now).OrderBy(x => x.At).ToList();
        if (recent.Count < 2 || recent[^1].At - recent[0].At < MinimumSpan) return null;
        double hours = (recent[^1].At - recent[0].At).TotalHours;
        double pace = Math.Max(0, (recent[^1].Used - recent[0].Used) / hours);   // percentage points an hour
        double atReset = Math.Min(100, used + pace * (reset - now).TotalHours);
        DateTimeOffset? reachedAt = null;
        if (used >= 100) reachedAt = now;
        else if (pace > 0 && atReset >= 100) reachedAt = now.AddHours((100 - used) / pace);
        return new QuotaProjection(reachedAt, atReset);
    }

    /// <summary>"At this pace: limit reached Thu 14:00 (in 2d 3h)", or "At this pace: about 62% used by the reset".</summary>
    public static string Describe(QuotaProjection p, DateTimeOffset now, bool use24Hour)
    {
        if (p.ReachedAt is { } at && at <= now) return "Limit reached";
        if (p.ReachedAt is { } when)
            return $"At this pace: limit reached {QuotaFormat.ResetLocal(when, now, use24Hour)} (in {QuotaFormat.Countdown(when, now)}), before the reset";
        return $"At this pace: about {QuotaFormat.Number(p.UsedAtReset)}% used by the reset";
    }
}
