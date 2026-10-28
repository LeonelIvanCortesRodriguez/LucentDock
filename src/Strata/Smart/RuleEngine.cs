using System.Diagnostics;
using System.Globalization;
using Strata.Core;

namespace Strata.Smart;

public enum FirePolicy
{
    /// <summary>Fire when the condition becomes true; again only after it was false in between.</summary>
    OnTransition,
    /// <summary>Fire on entry, then again every cooldown while the condition stays true.</summary>
    Repeating,
    /// <summary>At most once per calendar day.</summary>
    OncePerDay,
    /// <summary>Once per distinct key (e.g. once per calendar event).</summary>
    OncePerKey,
}

public enum ParameterKind { Number, Text, Style, Toggle }

public sealed record AutomationParameter(string Id, string Label, string Default, ParameterKind Kind = ParameterKind.Number,
    double Min = 0, double Max = 100000, string Unit = "");

/// <summary>An automation: when <see cref="When"/> holds (for <see cref="Sustain"/>), run <see cref="Actions"/>;
/// undo the reversible ones when <see cref="ExitWhen"/> (default: not When) becomes true.</summary>
public sealed class RuleDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Condition When { get; init; }
    public Condition? ExitWhen { get; init; }
    public required IReadOnlyList<RuleAction> Actions { get; init; }
    public int Priority { get; init; } = 50;
    public FirePolicy Policy { get; init; } = FirePolicy.OnTransition;
    public TimeSpan Cooldown { get; init; } = TimeSpan.Zero;
    /// <summary>Condition must hold this long before firing; a parameter id may override it (seconds).</summary>
    public TimeSpan Sustain { get; init; } = TimeSpan.Zero;
    public string? SustainParam { get; init; }
    public Func<RuleContext, string?>? DedupKey { get; init; }
    /// <summary>Wait for quiet hours to end instead of firing silently (and using up the period).</summary>
    public bool DeferDuringQuietHours { get; init; }
    public bool EnabledByDefault { get; init; } = true;
    public IReadOnlyList<AutomationParameter> Parameters { get; init; } = [];
    /// <summary>Widgets this automation may promote, hide or highlight (checked by the catalog tests).</summary>
    public IReadOnlyList<string> WidgetsTouched { get; init; } = [];

    public IEnumerable<string> SignalIds => When.SignalIds.Concat(ExitWhen?.SignalIds ?? []).Distinct();
}

/// <summary>Persisted per-automation state and user configuration.</summary>
public sealed class RuleState
{
    public bool? Enabled { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public DateTime? LastFired { get; set; }
    public string? LastExplanation { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastPeriod { get; set; }
    public int FireCount { get; set; }
    public bool Active { get; set; }
    public string? ActiveKey { get; set; }
    public Dictionary<string, DateTime> FiredKeys { get; set; } = new();
    public int ConsecutiveErrors { get; set; }
    public bool ErrorDisabled { get; set; }
    public string? LastError { get; set; }
}

public sealed record HistoryEntry(DateTime At, string RuleId, string Kind, string Explanation, string Detail, bool PrivacySafe = false);

public sealed class AutomationStateFile
{
    public Dictionary<string, RuleState> Rules { get; set; } = new();
    public Dictionary<string, DateTime> NotificationKeys { get; set; } = new();
    public string? ThemeOverrideOwner { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
}

public interface IAutomationPersistence
{
    AutomationStateFile Load();
    void Save(AutomationStateFile state);
}

public sealed class InMemoryPersistence : IAutomationPersistence
{
    public AutomationStateFile State { get; private set; } = new();
    public int Saves { get; private set; }
    public AutomationStateFile Load() => State;
    public void Save(AutomationStateFile state) { State = state; Saves++; }
}

public sealed record RuleStatus(RuleDefinition Rule, bool Enabled, bool Active, bool Suppressed, RuleState State, DateTime? WaitingUntil);

public sealed class EngineStats
{
    public long SignalChanges { get; set; }
    public long Evaluations { get; set; }
    public long Fires { get; set; }
    public long Exits { get; set; }
    public long Suppressions { get; set; }
    public long Deferrals { get; set; }
    public long Errors { get; set; }
    public TimeSpan EvaluationTime { get; set; }

    public override string ToString() =>
        $"changes={SignalChanges} evaluations={Evaluations} fires={Fires} exits={Exits} suppressed={Suppressions} " +
        $"deferred={Deferrals} errors={Errors} evalTime={EvaluationTime.TotalMilliseconds:0.0}ms";
}

/// <summary>
/// Evaluates automations when the signals they depend on change (no per-rule polling). Time-based waits
/// (sustain periods, cooldowns, quiet-hour deferrals) are handled by one shared <see cref="Heartbeat"/>
/// that only touches rules whose wake-up time has passed. Every evaluation and action is isolated:
/// a failing rule is recorded, and disabled after repeated failures, without affecting the others.
/// </summary>
public sealed class RuleEngine
{
    public const int ErrorLimit = 3;
    private const int HistoryLimit = 200;

    private sealed class Entry(RuleDefinition def, int order)
    {
        public RuleDefinition Def { get; } = def;
        public int Order { get; } = order;
        public RuleState State { get; set; } = new();
        public bool Active;
        public bool Suppressed;
        public string? ActiveKey;
        public DateTime? TrueSince;
        public DateTime? WakeAt;
        public DateTime? DeferredUntil;
        public readonly List<IDisposable> Effects = [];
    }

    private readonly SignalStore _signals;
    private readonly IClock _clock;
    private readonly IDockActions _dock;
    private readonly NotificationCenter _notifications;
    private readonly IAutomationPersistence _persistence;
    private readonly DiagnosticErrorJournal? _diagnostics;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, List<Entry>> _bySignal = new();
    private readonly HashSet<Entry> _pending = [];
    private AutomationStateFile _file = new();
    private bool _loaded;
    private bool _draining;
    private bool _started;

    public EngineStats Stats { get; } = new();
    public event Action? StateChanged;

    public RuleEngine(SignalStore signals, IClock clock, IDockActions dock, NotificationCenter notifications,
        IAutomationPersistence persistence, DiagnosticErrorJournal? diagnostics = null)
    {
        _signals = signals;
        _clock = clock;
        _dock = dock;
        _notifications = notifications;
        _persistence = persistence;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<RuleDefinition> Rules => _entries.Select(e => e.Def).ToList();
    public IReadOnlyList<HistoryEntry> History => _file.History;
    public NotificationCenter Notifications => _notifications;
    public AutomationStateFile StateFile => _file;

    public void Register(RuleDefinition def)
    {
        if (_entries.Any(e => e.Def.Id == def.Id)) throw new ArgumentException($"Duplicate automation id {def.Id}");
        var entry = new Entry(def, _entries.Count);
        _entries.Add(entry);
        if (_loaded) AttachState(entry);
        foreach (var id in def.SignalIds)
        {
            if (!_bySignal.TryGetValue(id, out var list)) _bySignal[id] = list = [];
            list.Add(entry);
        }
    }

    /// <summary>
    /// Load persisted state once, on first use. Before this was deferred to Start, so the sun signal seeded at
    /// startup used the sunset rule's default times and fired a one-second Midnight flash in between.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _file = SafeLoad();
        foreach (var e in _entries) AttachState(e);
    }

    private Entry Find(string id)
    {
        EnsureLoaded();
        return _entries.First(x => x.Def.Id == id);
    }

    private void AttachState(Entry e)
    {
        e.State = _file.Rules.TryGetValue(e.Def.Id, out var s) ? s : new RuleState();
        _file.Rules[e.Def.Id] = e.State;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        EnsureLoaded();
        var sentBeforeStart = _notifications.SentKeys; // e.g. the dock shortcut warning, sent while the dock initialized
        _notifications.SentKeys = _file.NotificationKeys;
        foreach (var (key, at) in sentBeforeStart) _notifications.SentKeys[key] = at;
        // Re-apply automations that were active when Strata last stopped (layouts live in memory only).
        foreach (var e in Ordered(_entries).Where(e => e.State.Active && IsEnabled(e)))
        {
            var ctx = Context(e, _clock.Now);
            e.Active = true;
            e.ActiveKey = e.State.ActiveKey;
            RunActions(e, ctx, resuming: true);
            AddHistory(e, "Resumed");
        }
        _signals.Changed += OnSignalChanged;
        foreach (var e in _entries) _pending.Add(e);
        Drain();
        Save();
    }

    /// <summary>Undo every active effect (on exit). Active flags stay persisted so the next start resumes them.</summary>
    public void Stop()
    {
        if (!_started) return;
        _signals.Changed -= OnSignalChanged;
        foreach (var e in _entries) DisposeEffects(e);
        Save();
        _started = false;
    }

    private void OnSignalChanged(object? sender, SignalChangedEventArgs args)
    {
        Stats.SignalChanges++;
        if (!_bySignal.TryGetValue(args.Id, out var dependents)) return;
        foreach (var e in dependents) _pending.Add(e);
        Drain();
    }

    /// <summary>Called once a second by the dock's shared heartbeat. Cheap when nothing is waiting.</summary>
    public void Heartbeat()
    {
        if (!_started) return;
        var now = _clock.Now;
        foreach (var e in _entries)
        {
            if (e.DeferredUntil is { } d && now >= d) { e.DeferredUntil = null; _pending.Add(e); }
            if (e.WakeAt is { } w && now >= w) { e.WakeAt = null; _pending.Add(e); }
        }
        if (_pending.Count > 0) Drain();
    }

    /// <summary>Evaluate queued rules, highest priority first. Actions may publish signals; those are handled in
    /// later passes (bounded, so a feedback loop between automations cannot spin forever).</summary>
    private void Drain()
    {
        if (_draining) return;
        _draining = true;
        try
        {
            for (int pass = 0; pass < 10 && _pending.Count > 0; pass++)
            {
                var batch = Ordered(_pending).ToList();
                _pending.Clear();
                foreach (var e in batch) Evaluate(e);
            }
            if (_pending.Count > 0)
            {
                Core.Log.Write($"Automation evaluation still busy after 10 passes; deferring {_pending.Count} rule(s).");
                foreach (var e in _pending) e.WakeAt = _clock.Now.AddSeconds(1);
                _pending.Clear();
            }
        }
        finally { _draining = false; }
    }

    private static IEnumerable<Entry> Ordered(IEnumerable<Entry> entries) =>
        entries.OrderByDescending(e => e.Def.Priority).ThenBy(e => e.Order);

    private bool IsEnabled(Entry e) => (e.State.Enabled ?? e.Def.EnabledByDefault) && !e.State.ErrorDisabled;

    private RuleContext Context(Entry e, DateTime now) => new(_signals, now, e.Def, e.State.Parameters);

    private TimeSpan SustainFor(Entry e, RuleContext ctx) =>
        e.Def.SustainParam is { } p ? TimeSpan.FromSeconds(ctx.Number(p)) : e.Def.Sustain;

    private void Evaluate(Entry e)
    {
        var now = _clock.Now;
        var watch = Stopwatch.StartNew();
        Stats.Evaluations++;
        try
        {
            var ctx = Context(e, now);
            if (!IsEnabled(e))
            {
                if (e.Active) Exit(e, ctx, "automation turned off");
                e.TrueSince = null;
                return;
            }
            if (e.DeferredUntil is { } until && now < until) return;

            var when = e.Def.When.Evaluate(ctx);
            if (e.Active)
            {
                var exit = e.Def.ExitWhen?.Evaluate(ctx) ?? Negate(when);
                if (exit.IsTrue)
                {
                    Exit(e, ctx, exit.Explanation);
                }
                else if (when.IsTrue)
                {
                    string? key = e.Def.DedupKey?.Invoke(ctx);
                    if (e.Def.Policy == FirePolicy.OncePerKey && key is not null && key != e.ActiveKey)
                    {
                        Exit(e, ctx, "moving on to the next item");
                        Enter(e, ctx, when, key);
                    }
                    else if (e.Def.Policy == FirePolicy.Repeating && !e.Suppressed)
                    {
                        var due = (e.State.LastFired ?? now) + e.Def.Cooldown;
                        if (now >= due) Fire(e, ctx, when, key);
                        else e.WakeAt = due;
                    }
                }
                return;
            }

            if (!when.IsTrue)
            {
                e.TrueSince = null;
                e.WakeAt = null;
                return;
            }

            e.TrueSince ??= now;
            var sustain = SustainFor(e, ctx);
            if (sustain > TimeSpan.Zero && now - e.TrueSince.Value < sustain)
            {
                e.WakeAt = e.TrueSince.Value + sustain;
                return;
            }
            var explanation = sustain > TimeSpan.Zero
                ? $"{when.Explanation} (for {FormatSpan(now - e.TrueSince.Value)})"
                : when.Explanation;
            Enter(e, ctx, new Truth(true, explanation), e.Def.DedupKey?.Invoke(ctx));
        }
        catch (Exception ex)
        {
            HandleError(e, now, ex);
        }
        finally
        {
            Stats.EvaluationTime += watch.Elapsed;
        }
    }

    private static Truth Negate(Truth t) => t.Value is null ? t : new Truth(!t.Value, t.Explanation);

    private void Enter(Entry e, RuleContext ctx, Truth when, string? key)
    {
        var now = ctx.Now;
        string? blocked = null;
        if (e.Def.Policy == FirePolicy.OncePerDay && e.State.LastPeriod == DayKey(now))
            blocked = "already ran today";
        else if (e.Def.Policy == FirePolicy.OncePerKey && key is not null && e.State.FiredKeys.ContainsKey(key))
            blocked = "already handled this one";
        else if (e.State.LastFired is { } last && e.Def.Cooldown > TimeSpan.Zero && now - last < e.Def.Cooldown)
            blocked = $"cooling down ({FormatSpan(e.Def.Cooldown - (now - last))} left)";

        if (blocked is null && e.Def.DeferDuringQuietHours && _notifications.Quiet.IsQuiet(now))
        {
            // Hold the whole automation until quiet hours end, without using up its once-per-period allowance.
            var until = _notifications.Quiet.NextEnd(now);
            e.DeferredUntil = until;
            e.WakeAt = until;
            Stats.Deferrals++;
            AddHistory(e, "Deferred");
            Save();
            return;
        }

        e.Active = true;
        e.ActiveKey = key;
        e.State.Active = true;
        e.State.ActiveKey = key;
        if (blocked is not null)
        {
            // Stay "active" so we wait for the condition to clear, but change nothing on the dock.
            e.Suppressed = true;
            Stats.Suppressions++;
            AddHistory(e, "Suppressed");
            Save();
            return;
        }
        e.Suppressed = false;
        Fire(e, ctx, when, key);
    }

    private void Fire(Entry e, RuleContext ctx, Truth when, string? key)
    {
        var outcomes = RunActions(e, ctx, resuming: false);
        var now = ctx.Now;
        e.State.LastFired = now;
        e.State.LastPeriod = DayKey(now);
        if (key is not null) e.State.FiredKeys[key] = now;
        foreach (var old in e.State.FiredKeys.Where(kv => kv.Value < now.AddDays(-30)).Select(kv => kv.Key).ToList())
            e.State.FiredKeys.Remove(old);
        e.State.FireCount++;
        // A repeating rule must wake itself: a constant value raises no change events.
        if (e.Def.Policy == FirePolicy.Repeating && e.Def.Cooldown > TimeSpan.Zero) e.WakeAt = now + e.Def.Cooldown;
        e.State.LastExplanation = ConditionSummary(e);
        e.State.LastOutcome = string.Join("; ", outcomes);
        Stats.Fires++;
        AddHistory(e, "Fired", e.State.LastOutcome);
        Save();
    }

    private List<string> RunActions(Entry e, RuleContext ctx, bool resuming)
    {
        var outcomes = new List<string>();
        var actx = new ActionContext(ctx, _dock, _notifications, resuming);
        int failures = 0;
        foreach (var action in e.Def.Actions)
        {
            if (resuming && !action.IsEffect) continue;
            try
            {
                var result = action.Execute(actx);
                if (result.Effect is not null) e.Effects.Add(result.Effect);
                if (result.Failed) failures++;
                outcomes.Add(action is Notify ? result.Summary : action.Description);
            }
            catch (Exception ex)
            {
                // One failing action never stops the others or the engine.
                failures++;
                Stats.Errors++;
                outcomes.Add($"{action.Description} (failed)");
                Core.Log.Write($"Automation {e.Def.Id}: action {action.GetType().Name} failed: {ex}");
                _diagnostics?.Record("automation:" + e.Def.Id, ex, ctx.Now);
            }
        }
        if (failures > 0) CountError(e, ctx.Now, $"{failures} action(s) failed");
        else { e.State.ConsecutiveErrors = 0; e.State.LastError = null; }
        return outcomes;
    }

    private void Exit(Entry e, RuleContext ctx, string why)
    {
        DisposeEffects(e);
        e.Active = false;
        e.Suppressed = false;
        e.ActiveKey = null;
        e.TrueSince = null;
        e.WakeAt = null;
        e.State.Active = false;
        e.State.ActiveKey = null;
        Stats.Exits++;
        AddHistory(e, "Ended");
        Save();
    }

    private static void DisposeEffects(Entry e)
    {
        for (int i = e.Effects.Count - 1; i >= 0; i--)
        {
            try { e.Effects[i].Dispose(); }
            catch (Exception ex) { Core.Log.Write($"Undo for {e.Def.Id} failed: {ex.Message}"); }
        }
        e.Effects.Clear();
    }

    private void HandleError(Entry e, DateTime now, Exception ex)
    {
        Stats.Errors++;
        _diagnostics?.Record("automation:" + e.Def.Id, ex, now);
        Core.Log.Write($"Automation {e.Def.Id} failed: {ex}");
        CountError(e, now, ex.Message);
    }

    private void CountError(Entry e, DateTime now, string message)
    {
        e.State.ConsecutiveErrors++;
        e.State.LastError = "A bounded automation operation failed.";
        AddHistory(e, "Error");
        if (e.State.ConsecutiveErrors >= ErrorLimit && !e.State.ErrorDisabled)
        {
            e.State.ErrorDisabled = true;
            DisposeEffects(e);
            e.Active = false;
            e.State.Active = false;
            AddHistory(e, "Disabled");
        }
        Save();
    }

    // ---------------- Configuration ----------------

    public RuleStatus Status(string id)
    {
        var e = Find(id); // the app reads rule parameters (e.g. sunset times) while seeding signals, before Start
        return new RuleStatus(e.Def, IsEnabled(e), e.Active && !e.Suppressed, e.Suppressed, e.State, e.DeferredUntil ?? e.WakeAt);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var e = Find(id);
        e.State.Enabled = enabled;
        if (enabled && e.State.ErrorDisabled)
        {
            e.State.ErrorDisabled = false;
            e.State.ConsecutiveErrors = 0;
        }
        AddHistory(e, enabled ? "Enabled" : "Disabled");
        _pending.Add(e);
        Drain();
        Save();
    }

    public void SetParameter(string id, string parameter, string value)
    {
        var e = Find(id);
        e.State.Parameters[parameter] = value;
        _pending.Add(e);
        Drain();
        Save();
    }

    public void SetThemeOverrideOwner(string? owner)
    {
        _file.ThemeOverrideOwner = owner;
        Save();
    }

    public static string DayKey(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(0, (int)t.TotalSeconds)} s";

    private static string SignalSummary(Entry e)
    {
        string ids = string.Join(", ", e.Def.SignalIds.Order(StringComparer.Ordinal));
        return ids.Length == 0 ? "its configured inputs" : ids;
    }

    private static string ConditionSummary(Entry e) => $"Condition matched using {SignalSummary(e)}";

    private static string ActionSummary(Entry e)
    {
        string actions = string.Join("; ", e.Def.Actions.Select(action => action.Description));
        return actions.Length == 0 ? "No action was configured" : actions;
    }

    private void AddHistory(Entry e, string kind, string? safeDetail = null)
    {
        string explanation = kind switch
        {
            "Fired" => ConditionSummary(e),
            "Resumed" => "It was active when Strata last stopped",
            "Ended" => $"Condition cleared using {SignalSummary(e)}",
            "Deferred" => "Condition matched during quiet hours",
            "Suppressed" => "Condition matched but its repeat policy prevented another action",
            "Enabled" or "Disabled" => "Changed by the user",
            "Error" => "A bounded automation operation failed",
            _ => "Automation state changed",
        };
        string detail = safeDetail ?? kind switch
        {
            "Ended" => "Restored any temporary dock effects",
            "Deferred" or "Suppressed" => "No changes were applied",
            "Enabled" => "Enabled the rule",
            "Disabled" => "Disabled the rule and removed active effects",
            "Error" => "Recorded the failure without personal content",
            _ => ActionSummary(e),
        };
        _file.History.Add(new HistoryEntry(_clock.Now, e.Def.Id, kind, explanation, detail, PrivacySafe: true));
        if (_file.History.Count > HistoryLimit) _file.History.RemoveRange(0, _file.History.Count - HistoryLimit);
        StateChanged?.Invoke();
    }

    private AutomationStateFile SafeLoad()
    {
        try { return _persistence.Load(); }
        catch (Exception ex)
        {
            Core.Log.Write($"Automation state unreadable, starting fresh: {ex.Message}");
            return new AutomationStateFile();
        }
    }

    private void Save()
    {
        if (!_started) return;
        try
        {
            _notifications.Prune();
            _file.NotificationKeys = _notifications.SentKeys;
            _persistence.Save(_file);
        }
        catch (Exception ex) { Core.Log.Write($"Saving automation state failed: {ex.Message}"); }
    }
}
