using System.Text.Json;
using Strata.Smart;

namespace Strata.Core;

/// <summary>One automation's choices in a backup: on or off, and its parameters. Never its history or state.</summary>
public sealed class AutomationChoice
{
    public bool? Enabled { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>What a settings backup holds: the dock's settings and each automation's choices.</summary>
public sealed class SettingsBackupFile
{
    public string Format { get; set; } = SettingsBackup.FormatName;
    public int FormatVersion { get; set; } = SettingsBackup.CurrentVersion;
    public string App { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public AppSettings? Settings { get; set; }
    public Dictionary<string, AutomationChoice> Automations { get; set; } = new();
}

/// <summary>
/// Back up and restore Strata's settings as one JSON file the user saves where they like (a USB stick, a synced
/// folder), to move them to another PC or keep a copy. It holds settings.json and each automation's on/off and
/// parameters, and nothing else: no screen time, notes, todos, events, AI usage, automation history or sign-ins.
/// </summary>
public static class SettingsBackup
{
    public const string FormatName = "strata-settings";
    public const int CurrentVersion = 1;
    public const string FileExtension = ".strata-settings.json";

    public static string Serialize(AppSettings settings, IEnumerable<(string Id, RuleState State)> rules, string appVersion, DateTime now)
    {
        var file = new SettingsBackupFile
        {
            App = appVersion,
            ExportedAt = now,
            Settings = settings,
            Automations = rules
                .Where(r => r.State.Enabled is not null || r.State.Parameters.Count > 0)
                .ToDictionary(r => r.Id, r => new AutomationChoice { Enabled = r.State.Enabled, Parameters = new(r.State.Parameters) }),
        };
        return JsonSerializer.Serialize(file, Storage.Json);
    }

    /// <summary>Read a backup; on failure the error says why, in words for the user.</summary>
    public static (SettingsBackupFile? File, string? Error) Parse(string json)
    {
        SettingsBackupFile? file;
        try { file = JsonSerializer.Deserialize<SettingsBackupFile>(json, Storage.Json); }
        catch (JsonException) { return (null, "That file isn't a Strata settings backup."); }
        if (file is null || file.Format != FormatName || file.Settings is null)
            return (null, "That file isn't a Strata settings backup.");
        if (file.FormatVersion > CurrentVersion)
            return (null, $"That backup was made by a newer Strata ({file.App}). Update Strata, then restore it.");
        var s = file.Settings;
        s.Style = DockStyles.Normalize(s.Style);
        s.GlassOpacity = Math.Clamp(s.GlassOpacity, 0, 100);
        s.LeftWidgets = s.LeftWidgets.Where(id => WidgetManifest.Find(id) is not null).Distinct().ToList();
        s.RightWidgets = s.RightWidgets.Where(id => WidgetManifest.Find(id) is not null && !s.LeftWidgets.Contains(id)).Distinct().ToList();
        s.IntroduceNewWidgets();
        return (file, null);
    }

    /// <summary>
    /// Put a parsed backup into effect: save its settings, apply each automation choice through the rule engine, and
    /// forget the AI usage monitor so the next one reads the restored Claude/Codex switches and executable paths.
    /// Returns the settings now in effect (the app swaps them in, then rebuilds the dock).
    /// </summary>
    public static AppSettings Restore(SettingsBackupFile file, RuleEngine? engine)
    {
        var settings = file.Settings ?? throw new ArgumentException("The backup has no settings.", nameof(file));
        settings.Save();
        if (engine is not null)
        {
            foreach (var rule in Automations.Create())
            {
                if (!file.Automations.TryGetValue(rule.Id, out var choice)) continue;
                if (choice.Enabled is bool on && engine.Status(rule.Id).Enabled != on) engine.SetEnabled(rule.Id, on);
                foreach (var parameter in rule.Parameters)
                    if (choice.Parameters.TryGetValue(parameter.Id, out var value)) engine.SetParameter(rule.Id, parameter.Id, value);
            }
        }
        Quota.QuotaService.Reset();
        return settings;
    }

    /// <summary>A file name for a backup made on <paramref name="day"/>.</summary>
    public static string SuggestedName(DateTime day) => $"Strata settings {day:yyyy-MM-dd}{FileExtension}";
}
