namespace Strata.Smart;

/// <summary>A daily window (may cross midnight) during which non-urgent notifications are held back.</summary>
public sealed class QuietHours
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Start { get; set; } = TimeSpan.FromHours(22);
    public TimeSpan End { get; set; } = TimeSpan.FromHours(7);

    public bool IsQuiet(DateTime t)
    {
        if (!Enabled || Start == End) return false;
        var tod = t.TimeOfDay;
        return Start < End ? tod >= Start && tod < End : tod >= Start || tod < End;
    }

    /// <summary>When the current quiet period ends (only meaningful while <see cref="IsQuiet"/>).</summary>
    public DateTime NextEnd(DateTime t)
    {
        var end = t.Date + End;
        return end > t ? end : end.AddDays(1);
    }

    public string Describe() => Enabled ? $"{DateTime.Today.Add(Start):HH:mm}–{DateTime.Today.Add(End):HH:mm}" : "off";
}

public interface INotifier
{
    void Show(string title, string body);
}

public enum NotifyOutcome { Sent, SuppressedQuietHours, SuppressedDuplicate, Failed }

public sealed record NotificationRecord(DateTime At, string Key, string Title, string Source, NotifyOutcome Outcome, string Body = "");

/// <summary>
/// The only way anything in Strata shows a notification. It applies quiet hours and de-duplication in one
/// place, keeps a history, and publishes <see cref="Signals.Notifications"/>.
/// </summary>
public sealed class NotificationCenter(INotifier notifier, IClock clock, QuietHours quiet, SignalStore? signals = null)
{
    private readonly List<NotificationRecord> _recent = [];
    private DateTime _clearedAt = DateTime.MinValue;

    /// <summary>The running app's centre, for the clock flyout's history. Null in previews and tests.</summary>
    public static NotificationCenter? Current { get; set; }

    /// <summary>Key → last time a notification with that key was sent. Persisted with the automation state.</summary>
    public Dictionary<string, DateTime> SentKeys { get; set; } = new();
    public QuietHours Quiet { get; } = quiet;
    public IReadOnlyList<NotificationRecord> Recent => _recent;

    public NotifyOutcome Send(string key, string title, string body, string source, TimeSpan dedupWindow, bool urgent = false)
    {
        var now = clock.Now;
        NotifyOutcome outcome;
        if (SentKeys.TryGetValue(key, out var last) && now - last < dedupWindow)
            outcome = NotifyOutcome.SuppressedDuplicate;
        else if (!urgent && Quiet.IsQuiet(now))
            outcome = NotifyOutcome.SuppressedQuietHours;
        else
        {
            try
            {
                notifier.Show(title, body);
                SentKeys[key] = now;
                outcome = NotifyOutcome.Sent;
            }
            catch (Exception ex)
            {
                Core.Log.Write($"Notification '{key}' failed: {ex.Message}");
                outcome = NotifyOutcome.Failed;
            }
        }
        _recent.Add(new NotificationRecord(now, key, title, source, outcome, body));
        if (_recent.Count > 100) _recent.RemoveAt(0);
        PublishState();
        return outcome;
    }

    /// <summary>What the user saw, or would have seen without quiet hours, since the last Clear: newest first.
    /// Kept for this session only (reminders can carry event titles), never written to disk.</summary>
    public IReadOnlyList<NotificationRecord> History(int max = 50) =>
        _recent.Where(r => r.At > _clearedAt && r.Outcome is NotifyOutcome.Sent or NotifyOutcome.SuppressedQuietHours)
            .Reverse().Take(max).ToList();

    /// <summary>When the newest history entry arrived, if any.</summary>
    public DateTime? LastShownAt => History(1).FirstOrDefault()?.At;

    public void ClearHistory() => _clearedAt = clock.Now;

    public void PublishState()
    {
        var now = clock.Now;
        var sentToday = _recent.Count(r => r.Outcome == NotifyOutcome.Sent && r.At.Date == now.Date);
        var lastSent = _recent.LastOrDefault(r => r.Outcome == NotifyOutcome.Sent)?.At;
        signals?.Publish(Signals.Notifications, new NotificationState(Quiet.IsQuiet(now), sentToday, lastSent), "notifications");
    }

    /// <summary>Forget de-duplication keys older than a month so the state file stays small.</summary>
    public void Prune()
    {
        var cutoff = clock.Now.AddDays(-30);
        foreach (var k in SentKeys.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) SentKeys.Remove(k);
    }

    public static string Describe(NotifyOutcome o) => o switch
    {
        NotifyOutcome.Sent => "notification sent",
        NotifyOutcome.SuppressedQuietHours => "notification held (quiet hours)",
        NotifyOutcome.SuppressedDuplicate => "notification skipped (already sent recently)",
        _ => "notification failed",
    };
}
