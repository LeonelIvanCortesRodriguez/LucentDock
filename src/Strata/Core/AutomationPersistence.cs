using Strata.Smart;

namespace Strata.Core;

/// <summary>Atomic, storage-root-aware persistence for automation configuration and runtime state.</summary>
public sealed class AutomationPersistence : IAutomationPersistence
{
    public const string FileName = "automations.json";
    public AutomationStateFile Load() => AutomationPrivacy.SanitizeForPersistence(Storage.Load<AutomationStateFile>(FileName));
    public void Save(AutomationStateFile state) => Storage.Save(FileName, AutomationPrivacy.SanitizeForPersistence(state));
}

/// <summary>Defense-in-depth migration: persisted automation data never retains evaluated titles, names, or error text.</summary>
public static class AutomationPrivacy
{
    private static readonly IReadOnlyDictionary<string, RuleDefinition> Definitions =
        Automations.Create().ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static AutomationStateFile SanitizeForPersistence(AutomationStateFile source)
    {
        var safe = new AutomationStateFile
        {
            NotificationKeys = new(source.NotificationKeys, StringComparer.Ordinal),
            ThemeOverrideOwner = source.ThemeOverrideOwner,
            History = source.History.Select(Sanitize).ToList(),
        };
        foreach (var (id, state) in source.Rules)
        {
            safe.Rules[id] = new RuleState
            {
                Enabled = state.Enabled,
                Parameters = new(state.Parameters, StringComparer.Ordinal),
                LastFired = state.LastFired,
                LastExplanation = state.LastExplanation is null ? null : "Condition matched using configured signals",
                LastOutcome = state.LastOutcome is null ? null : "Configured actions were applied",
                LastPeriod = state.LastPeriod,
                FireCount = state.FireCount,
                Active = state.Active,
                ActiveKey = state.ActiveKey,
                FiredKeys = new(state.FiredKeys, StringComparer.Ordinal),
                ConsecutiveErrors = state.ConsecutiveErrors,
                ErrorDisabled = state.ErrorDisabled,
                LastError = state.LastError is null ? null : "A bounded automation operation failed.",
            };
        }
        return safe;
    }

    private static HistoryEntry Sanitize(HistoryEntry entry)
    {
        Definitions.TryGetValue(entry.RuleId, out RuleDefinition? definition);
        string signals = definition is null ? "configured signals" :
            string.Join(", ", definition.SignalIds.Order(StringComparer.Ordinal)) is { Length: > 0 } ids ? ids : "configured signals";
        string actions = definition is null ? "Configured actions were applied" :
            string.Join("; ", definition.Actions.Select(action => action.Description)) is { Length: > 0 } descriptions
                ? descriptions : "No action was configured";
        return entry.Kind switch
    {
        "Fired" => entry with { Explanation = $"Condition matched using {signals}", Detail = actions, PrivacySafe = true },
        "Resumed" => entry with { Explanation = "It was active when Strata last stopped", Detail = actions, PrivacySafe = true },
        "Ended" => entry with { Explanation = $"Condition cleared using {signals}", Detail = "Restored any temporary dock effects", PrivacySafe = true },
        "Deferred" => entry with { Explanation = "Condition matched during quiet hours", Detail = "No changes were applied", PrivacySafe = true },
        "Suppressed" => entry with { Explanation = "Repeat policy prevented another action", Detail = "No changes were applied", PrivacySafe = true },
        "Enabled" => entry with { Explanation = "Changed by the user", Detail = "Enabled the rule", PrivacySafe = true },
        "Disabled" => entry with { Explanation = "Changed by the user", Detail = "Disabled the rule and removed active effects", PrivacySafe = true },
        "Error" => entry with { Explanation = "A bounded automation operation failed", Detail = "Recorded without personal content", PrivacySafe = true },
        _ => entry with { Explanation = "Automation state changed", Detail = "No personal content retained", PrivacySafe = true },
    };
    }
}

/// <summary>Routes smart notifications through the one application tray resource.</summary>
public sealed class AppNotifier(Action<string, string> show) : INotifier
{
    public void Show(string title, string body) => show(title, body);
}
