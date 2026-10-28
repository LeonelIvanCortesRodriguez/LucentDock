using Strata.Core;
using Strata.Smart;

namespace Strata.Tests;

/// <summary>Records what automations do to the dock, using the real layout stack and theme controller.</summary>
public sealed class FakeDock : IDockActions
{
    public LayoutStack Layouts { get; } = new();
    public ThemeController Theme { get; } = new(DockStyle.Glass);
    public List<string> Suggestions { get; } = [];
    public int FocusStarts { get; private set; }
    public Func<LayoutLayer, IDisposable>? PushOverride { get; set; }

    public List<string> BaseLeft { get; } = ["weather", "media"];
    public List<string> BaseRight { get; } = ["screentime", "cpu", "ram", "volume", "battery", "clock"];

    public IDisposable PushLayout(LayoutLayer layer) => PushOverride?.Invoke(layer) ?? Layouts.Push(layer);
    public IDisposable RequestTheme(string owner, DockStyle style) => Theme.Request(owner, style);

    public IDisposable? ShowSuggestion(string owner, string appName, string reason)
    {
        Suggestions.Add(appName);
        return new ActionHandle(() => Suggestions.Remove(appName));
    }

    public void StartFocus() => FocusStarts++;

    public EffectiveLayout Effective(DateTime now) => LayoutOverlay.Compose(BaseLeft, BaseRight, Layouts.Layers, now);

    private sealed class ActionHandle(Action a) : IDisposable { public void Dispose() => a(); }
}

public sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Body)> Shown { get; } = [];
    public bool Throw { get; set; }
    public void Show(string title, string body)
    {
        if (Throw) throw new InvalidOperationException("tray unavailable");
        Shown.Add((title, body));
    }
}

/// <summary>A complete engine on a manual clock, wired exactly like the app but with fakes at the edges.</summary>
public sealed class SmartHarness
{
    public ManualClock Clock { get; }
    public SignalStore Signals { get; }
    public QuietHours Quiet { get; } = new() { Enabled = false };
    public FakeNotifier Notifier { get; } = new();
    public NotificationCenter Notifications { get; }
    public FakeDock Dock { get; } = new();
    public InMemoryPersistence Persistence { get; }
    public RuleEngine Engine { get; }

    public SmartHarness(DateTime? start = null, IEnumerable<RuleDefinition>? rules = null, InMemoryPersistence? persistence = null,
        QuietHours? quiet = null)
    {
        Clock = new ManualClock(start ?? new DateTime(2026, 3, 10, 14, 0, 0));
        Signals = new SignalStore(Clock);
        if (quiet is not null) Quiet = quiet;
        Notifications = new NotificationCenter(Notifier, Clock, Quiet, Signals);
        Persistence = persistence ?? new InMemoryPersistence();
        Engine = new RuleEngine(Signals, Clock, Dock, Notifications, Persistence);
        foreach (var r in rules ?? Automations.Create()) Engine.Register(r);
    }

    public SmartHarness Start() { Engine.Start(); return this; }

    /// <summary>Advance time in one-second steps, re-publishing the given signals each second like the live sources do.</summary>
    public void Run(TimeSpan duration, Action? eachSecond = null)
    {
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromSeconds(1))
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            eachSecond?.Invoke();
            PublishMinute();
            Signals.CheckExpiry();
            Engine.Heartbeat();
            Dock.Layouts.PruneExpired(Clock.Now);
        }
    }

    public void PublishMinute() =>
        Signals.Publish(Strata.Smart.Signals.Minute, new DateTime(Clock.Now.Year, Clock.Now.Month, Clock.Now.Day, Clock.Now.Hour, Clock.Now.Minute, 0), "test");

    public IEnumerable<HistoryEntry> HistoryFor(string id) => Engine.History.Where(h => h.RuleId == id);
    public int Fired(string id) => HistoryFor(id).Count(h => h.Kind == "Fired");
}
