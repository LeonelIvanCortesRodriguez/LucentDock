namespace Strata.Core;

/// <summary>
/// Counts failures per component (widget, flyout, the UI thread as a whole). A component that fails
/// <see cref="Threshold"/> times within <see cref="Window"/> is quarantined: the dock replaces it with a small
/// notice instead of retrying it every second forever. Quarantine is cleared when the user retries.
/// </summary>
public sealed class FailureTracker(int threshold = 3, TimeSpan? window = null, DiagnosticErrorJournal? diagnostics = null)
{
    private readonly Dictionary<string, List<DateTime>> _failures = new();
    private readonly Dictionary<string, string> _quarantined = new();

    public int Threshold { get; } = threshold;
    public TimeSpan Window { get; } = window ?? TimeSpan.FromMinutes(5);
    public IReadOnlyDictionary<string, string> Quarantined => _quarantined;

    /// <returns>True exactly when this failure puts the component into quarantine.</returns>
    public bool Record(string component, Exception error, DateTime now)
    {
        diagnostics?.Record(component, error, now);
        Log.Write($"{component} failed: {error}");
        if (_quarantined.ContainsKey(component)) return false;
        if (!_failures.TryGetValue(component, out var list)) _failures[component] = list = [];
        list.Add(now);
        list.RemoveAll(t => now - t > Window);
        if (list.Count < Threshold) return false;
        _quarantined[component] = error.Message;
        Log.Write($"{component} was paused after {list.Count} failures in {Window.TotalMinutes:0} minutes.");
        return true;
    }

    public bool IsQuarantined(string component) => _quarantined.ContainsKey(component);

    public int RecentFailures(string component, DateTime now) =>
        _failures.TryGetValue(component, out var list) ? list.Count(t => now - t <= Window) : 0;

    public void Release(string component)
    {
        _quarantined.Remove(component);
        _failures.Remove(component);
    }
}
