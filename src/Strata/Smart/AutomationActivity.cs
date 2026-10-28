namespace Strata.Smart;

public sealed record AutomationActivityEntry(
    DateTime At,
    string RuleId,
    string RuleName,
    string Status,
    string Why,
    string Changed,
    bool Undone);

/// <summary>
/// Privacy-safe projection for the activity view. Persistence is sanitized too; this independently derives its
/// text from fixed definitions and never trusts historical explanation/outcome strings.
/// </summary>
public static class AutomationActivity
{
    public const int ViewLimit = 50;

    public static IReadOnlyList<AutomationActivityEntry> Project(
        IEnumerable<HistoryEntry> history,
        IEnumerable<RuleDefinition> rules,
        int limit = ViewLimit)
    {
        var definitions = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        return history.Reverse().Take(Math.Clamp(limit, 0, ViewLimit)).Select(entry =>
        {
            definitions.TryGetValue(entry.RuleId, out var rule);
            string name = rule?.Name ?? "Automation";
            string signals = rule is null ? "its inputs" : string.Join(", ", rule.SignalIds.Order(StringComparer.Ordinal));
            string changes = rule is null ? "No action summary available"
                : string.Join("; ", rule.Actions.Select(a => a.Description));
            bool undone = entry.Kind == "Ended";
            string why = entry.Kind switch
            {
                "Fired" => $"Condition matched using {signals}",
                "Resumed" => "It was active when Strata last stopped",
                "Ended" => $"Condition cleared using {signals}",
                "Deferred" => "Condition matched during quiet hours",
                "Suppressed" => "Condition matched but its repeat policy prevented another action",
                "Enabled" or "Disabled" => "Changed by the user",
                "Error" => "A bounded automation operation failed",
                _ => "Automation state changed",
            };
            string changed = entry.Kind switch
            {
                "Ended" => "Restored any temporary dock effects",
                "Deferred" or "Suppressed" => "No changes were applied",
                "Enabled" => "Enabled the rule",
                "Disabled" => "Disabled the rule and removed active effects",
                "Error" => "Recorded the failure without personal content",
                "Fired" when entry.PrivacySafe => entry.Detail,
                _ => changes.Length == 0 ? "Evaluated the rule; no action was configured" : changes,
            };
            return new AutomationActivityEntry(entry.At, entry.RuleId, name, entry.Kind, why, changed, undone);
        }).ToList();
    }
}
