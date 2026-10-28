namespace Strata.Quota;

/// <summary>A notification to show: its key (sent once), title and text.</summary>
public sealed record QuotaAlert(string Key, string Title, string Text);

/// <summary>
/// What the alerts remember between runs: for each provider window and reset period, the highest level it reached
/// ("warn" or "reached"), and which alerts were already sent. Window ids, reset times and levels only.
/// </summary>
public sealed class QuotaAlertLog
{
    public const string FileName = "quota-alerts.json";
    /// <summary>"provider|window|period" → "warn" or "reached".</summary>
    public Dictionary<string, string> Levels { get; set; } = new();
    public HashSet<string> Sent { get; set; } = [];
}

/// <summary>
/// AI usage notifications: once when a limit reaches the warning level (the same threshold as the tile's warning
/// colour), once when it is reached, and once when a limit that had warned or been reached resets. Only fresh,
/// real readings count: never stale or sample data, never a window past its reset.
/// </summary>
public static class QuotaAlerts
{
    private const string Warn = "warn", Reached = "reached";

    public static List<QuotaAlert> Evaluate(ProviderState state, DateTimeOffset now, int warnRemainingPercent, QuotaAlertLog log, bool use24Hour)
    {
        var alerts = new List<QuotaAlert>();
        if (state.Status != QuotaStatus.Fresh || state.Snapshot is not { IsSample: false } s) return alerts;
        string provider = QuotaFormat.ProviderName(state.Provider);
        foreach (var w in s.Windows)
        {
            if (QuotaFormat.Phase(w, now) != WindowPhase.Current || w.UsedPercent is not double used) continue;
            string id = $"{state.Provider}|{w.Id}";
            string period = Period(w.ResetsAt);
            bool reached = w.Reached || used >= 100;
            bool warn = !reached && w.RemainingPercent is double r && r <= warnRemainingPercent;
            string name = $"{provider} · {w.Label}";
            string? left = QuotaFormat.ClockSkewSuspected(s, w, now) ? null : QuotaFormat.Countdown(w.ResetsAt, now);
            string? at = QuotaFormat.ResetLocal(w.ResetsAt, now, use24Hour);
            string resets = left is not null ? $"Resets in {left}{(at is null ? "" : $" ({at})")}." : at is not null ? $"Resets {at}." : "";

            if (reached || warn)
            {
                string level = reached ? Reached : Warn;
                // The same period, even if its reported reset time moved by a few minutes.
                string key = log.Levels.Keys.FirstOrDefault(k => SamePeriod(k, id, w.ResetsAt)) ?? $"{id}|{period}";
                if (!log.Levels.TryGetValue(key, out var had) || had == Warn) log.Levels[key] = level;
                if (reached && log.Sent.Add($"quota|{key}|{Reached}"))
                {
                    log.Sent.Add($"quota|{key}|{Warn}");   // no warning after the fact
                    alerts.Add(new($"quota|{key}|{Reached}", $"{name}: limit reached", resets));
                }
                else if (warn && log.Sent.Add($"quota|{key}|{Warn}"))
                    alerts.Add(new($"quota|{key}|{Warn}", $"{name}: {QuotaFormat.Used(used)}", $"{QuotaFormat.Remaining(w.RemainingPercent!.Value)}. {resets}".Trim()));
                continue;
            }

            // Below the warning level in a new period: an earlier period that warned or was reached has reset.
            foreach (var old in log.Levels.Keys.Where(k => k.StartsWith(id + "|", StringComparison.Ordinal) && !SamePeriod(k, id, w.ResetsAt)).ToList())
            {
                log.Levels.Remove(old);
                if (log.Sent.Add($"quota|{old}|reset"))
                    alerts.Add(new($"quota|{old}|reset", $"{name} has reset", $"{QuotaFormat.Used(used)} now: available again."));
            }
        }
        return alerts;
    }

    /// <summary>Forget periods and sent alerts whose reset is more than two weeks past, so the log stays small.</summary>
    public static void Prune(QuotaAlertLog log, DateTimeOffset now)
    {
        bool Old(string key) => ParsePeriod(key) is { } at && at < now.AddDays(-14);
        foreach (var k in log.Levels.Keys.Where(Old).ToList()) log.Levels.Remove(k);
        log.Sent.RemoveWhere(k => k.StartsWith("quota|", StringComparison.Ordinal) && Old(k[6..]));
    }

    /// <summary>True when <paramref name="key"/> is this window's period ending at <paramref name="resetsAt"/>, give or take 30 minutes.</summary>
    private static bool SamePeriod(string key, string id, DateTimeOffset? resetsAt) =>
        key.StartsWith(id + "|", StringComparison.Ordinal)
        && (ParsePeriod(key) is { } at ? resetsAt is { } r && Math.Abs((r - at).TotalMinutes) < 30 : resetsAt is null);

    /// <summary>The reset time in a "provider|window|period[|...]" key, if it has one.</summary>
    private static DateTimeOffset? ParsePeriod(string key) =>
        key.Split('|') is { Length: >= 3 } parts && DateTimeOffset.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>A reset period's stable name: its reset time in UTC, to the minute.</summary>
    public static string Period(DateTimeOffset? resetsAt) =>
        resetsAt is { } r ? r.ToUniversalTime().ToString("yyyy-MM-ddTHH:mmZ", System.Globalization.CultureInfo.InvariantCulture) : "-";
}
