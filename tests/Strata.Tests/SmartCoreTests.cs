using Strata.Core;
using Strata.Smart;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class SignalStoreTests
{
    private static readonly DateTime T0 = new(2026, 3, 10, 9, 0, 0);

    [Fact]
    public void ValueIsFreshOnlyWithinItsTtl()
    {
        var clock = new ManualClock(T0);
        var store = new SignalStore(clock);
        store.Publish(Signals.Cpu, 0.5, "test");
        Assert.True(store.TryGetFresh(Signals.Cpu, out var v));
        Assert.Equal(0.5, v);

        clock.Advance(Signals.Cpu.Ttl + TimeSpan.FromSeconds(1));
        Assert.False(store.TryGetFresh(Signals.Cpu, out _));
        var snap = store.Get(Signals.Cpu)!;
        Assert.False(snap.IsFresh(clock.Now));
        Assert.Equal("test", snap.Source);
        Assert.Equal(T0, snap.Timestamp);
    }

    [Fact]
    public void ChangeEventFiresOnlyForRealChangesAndRecovery()
    {
        var clock = new ManualClock(T0);
        var store = new SignalStore(clock);
        int changes = 0;
        store.Changed += (_, _) => changes++;
        store.Publish(Signals.Cpu, 0.5, "test");
        store.Publish(Signals.Cpu, 0.5, "test");
        Assert.Equal(1, changes);
        store.Publish(Signals.Cpu, 0.6, "test");
        Assert.Equal(2, changes);
        clock.Advance(TimeSpan.FromSeconds(10));
        store.Publish(Signals.Cpu, 0.6, "test"); // same value, but it had gone stale
        Assert.Equal(3, changes);
    }

    [Fact]
    public void ExpiryIsAnnouncedOnce()
    {
        var clock = new ManualClock(T0);
        var store = new SignalStore(clock);
        var seen = new List<SignalChangedEventArgs>();
        store.Publish(Signals.Cpu, 0.5, "test");
        store.Changed += (_, e) => seen.Add(e);
        clock.Advance(TimeSpan.FromSeconds(10));
        store.CheckExpiry();
        store.CheckExpiry();
        Assert.Single(seen);
        Assert.False(seen[0].Current.IsFresh(clock.Now));
    }

    [Fact]
    public void ErrorsMakeTheSignalUnknown()
    {
        var store = new SignalStore(new ManualClock(T0));
        store.PublishError(Signals.Battery, "power", "API failed");
        Assert.False(store.TryGetFresh(Signals.Battery, out _));
        Assert.Equal("API failed", store.Get(Signals.Battery)!.Error);
    }

    [Fact]
    public void PinnedValuesOverrideLivePublishersUntilUnpinned()
    {
        var store = new SignalStore(new ManualClock(T0));
        store.Pin(Signals.Battery, new BatteryState(true, 10, false), "test-control");
        store.Publish(Signals.Battery, new BatteryState(true, 80, false), "power");
        Assert.Equal(10, store.ValueOrDefault(Signals.Battery)!.Percent);
        store.Unpin(Signals.Battery);
        store.Publish(Signals.Battery, new BatteryState(true, 80, false), "power");
        Assert.Equal(80, store.ValueOrDefault(Signals.Battery)!.Percent);
    }

    [Fact]
    public void ConfidenceIsRecorded()
    {
        var store = new SignalStore(new ManualClock(T0));
        store.Publish(Signals.Network, new NetworkState(NetworkLevel.Internet, 0, 0), "network", confidence: 0.6);
        Assert.Equal(0.6, store.Get(Signals.Network)!.Confidence);
    }
}

[Trait("Category", "Unit")]
public class ConditionTests
{
    private static RuleContext Ctx(SignalStore s) => new(s, s.Clock.Now,
        new RuleDefinition { Id = "x", Name = "x", Description = "", When = Condition.Not(Condition.All()), Actions = [] },
        new Dictionary<string, string>());

    [Fact]
    public void MultiSignalAndOrWithUnknowns()
    {
        var store = new SignalStore(new ManualClock(new DateTime(2026, 1, 1, 9, 0, 0)));
        var cpuHigh = Condition.On(Signals.Cpu, (v, _) => v >= 0.85, (v, _) => $"cpu {v}");
        var memHigh = Condition.On(Signals.Memory, (v, _) => v >= 0.9, (v, _) => $"mem {v}");
        store.Publish(Signals.Cpu, 0.95, "t");

        Assert.Equal(true, Condition.Any(cpuHigh, memHigh).Evaluate(Ctx(store)).Value);   // one true is enough
        Assert.Null(Condition.All(cpuHigh, memHigh).Evaluate(Ctx(store)).Value);           // memory unknown
        store.Publish(Signals.Memory, 0.5, "t");
        var all = Condition.All(cpuHigh, memHigh).Evaluate(Ctx(store));
        Assert.Equal(false, all.Value);
        Assert.Contains("mem", all.Explanation);
        store.Publish(Signals.Memory, 0.95, "t");
        var both = (cpuHigh & memHigh).Evaluate(Ctx(store));
        Assert.True(both.IsTrue);
        Assert.Contains("cpu", both.Explanation);
        Assert.Contains("mem", both.Explanation);
        Assert.Equal(false, (!cpuHigh).Evaluate(Ctx(store)).Value);
        Assert.Equal(new[] { "system.cpu", "system.memory" }, (cpuHigh | memHigh).SignalIds.ToArray());
    }
}

[Trait("Category", "Unit")]
public class LayoutOverlayTests
{
    private static readonly List<string> Left = ["weather", "media"];
    private static readonly List<string> Right = ["cpu", "ram", "clock"];
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void NoLayersMeansTheSavedLayoutExactly()
    {
        var e = LayoutOverlay.Compose(Left, Right, [], Now);
        Assert.Equal(Left, e.Left);
        Assert.Equal(Right, e.Right);
        Assert.Empty(e.Emphasized);
    }

    [Fact]
    public void PromoteCollapseAndRestoreNeverMutateTheBase()
    {
        var stack = new LayoutStack();
        var promote = stack.Push(new LayoutLayer("a", 50, ["battery", "clock"], [], ["battery"]));
        var collapse = stack.Push(new LayoutLayer("b", 40, [], ["media"], []));
        var e = LayoutOverlay.Compose(Left, Right, stack.Layers, Now);
        Assert.Equal(new[] { "weather" }, e.Left);
        Assert.Equal(new[] { "battery", "clock", "cpu", "ram" }, e.Right);
        Assert.Contains("battery", e.Emphasized);

        promote.Dispose();
        collapse.Dispose();
        promote.Dispose(); // idempotent
        var restored = LayoutOverlay.Compose(Left, Right, stack.Layers, Now);
        Assert.Equal(new[] { "weather", "media" }, restored.Left);
        Assert.Equal(new[] { "cpu", "ram", "clock" }, restored.Right);
        Assert.Equal(new[] { "weather", "media" }, Left);
    }

    [Fact]
    public void HigherPriorityWinsConflicts()
    {
        var highPromote = new LayoutLayer("battery", 90, ["media"], [], []);
        var lowCollapse = new LayoutLayer("focus", 70, [], ["media"], []);
        Assert.Contains("media", LayoutOverlay.Compose(Left, Right, [highPromote, lowCollapse], Now).Right);

        var lowPromote = highPromote with { Priority = 10 };
        var e = LayoutOverlay.Compose(Left, Right, [lowPromote, lowCollapse], Now);
        Assert.DoesNotContain("media", e.Left.Concat(e.Right));
    }

    [Fact]
    public void ExpiredLayersAreIgnoredAndPruned()
    {
        var stack = new LayoutStack();
        stack.Push(new LayoutLayer("t", 10, ["notes"], [], [], ExpiresAt: Now.AddMinutes(10)));
        Assert.Contains("notes", LayoutOverlay.Compose(Left, Right, stack.Layers, Now).Right);
        Assert.DoesNotContain("notes", LayoutOverlay.Compose(Left, Right, stack.Layers, Now.AddMinutes(11)).Right);
        Assert.Equal(Now.AddMinutes(10), stack.NextExpiry);
        Assert.True(stack.PruneExpired(Now.AddMinutes(11)));
        Assert.Empty(stack.Layers);
    }

    [Fact]
    public void ManualThemeOverrideLastsUntilTheAutomationEnds()
    {
        var theme = new ThemeController(DockStyle.Glass);
        var request = theme.Request("sunset-theme", DockStyle.Dark);
        Assert.Equal(DockStyle.Dark, theme.Effective);

        theme.UserChose(DockStyle.Clear);           // user overrides tonight
        Assert.Equal(DockStyle.Clear, theme.Effective);
        Assert.Equal("sunset-theme", theme.OverriddenOwner);

        request.Dispose();                           // morning: automation ends
        Assert.Null(theme.OverriddenOwner);
        Assert.Equal(DockStyle.Clear, theme.Effective);

        theme.Request("sunset-theme", DockStyle.Dark); // next evening applies again
        Assert.Equal(DockStyle.Dark, theme.Effective);
    }

    [Fact]
    public void ChoosingTheAutomationStyleStillRecordsAManualOverride()
    {
        var theme = new ThemeController(DockStyle.Glass);
        theme.Request("sunset-theme", DockStyle.Dark);
        int changes = 0;
        theme.Changed += () => changes++;
        theme.UserChose(DockStyle.Dark);
        Assert.Equal("sunset-theme", theme.OverriddenOwner);
        Assert.Equal(DockStyle.Dark, theme.Effective);
        Assert.Equal(1, changes);
    }
}

[Trait("Category", "Unit")]
public class NotificationCenterTests
{
    [Fact]
    public void DuplicatesWithinTheWindowAreSuppressed()
    {
        var clock = new ManualClock(new DateTime(2026, 1, 1, 12, 0, 0));
        var notifier = new FakeNotifier();
        var center = new NotificationCenter(notifier, clock, new QuietHours { Enabled = false });
        Assert.Equal(NotifyOutcome.Sent, center.Send("k", "t", "b", "test", TimeSpan.FromMinutes(10)));
        Assert.Equal(NotifyOutcome.SuppressedDuplicate, center.Send("k", "t", "b", "test", TimeSpan.FromMinutes(10)));
        Assert.Equal(NotifyOutcome.Sent, center.Send("other", "t", "b", "test", TimeSpan.FromMinutes(10)));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(NotifyOutcome.Sent, center.Send("k", "t", "b", "test", TimeSpan.FromMinutes(10)));
        Assert.Equal(3, notifier.Shown.Count);
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void QuietHoursCrossMidnight(int h, int m, bool quiet) =>
        Assert.Equal(quiet, new QuietHours().IsQuiet(new DateTime(2026, 1, 1, h, m, 0)));

    [Fact]
    public void QuietHoursHoldNonUrgentNotificationsOnly()
    {
        var clock = new ManualClock(new DateTime(2026, 1, 1, 23, 0, 0));
        var notifier = new FakeNotifier();
        var center = new NotificationCenter(notifier, clock, new QuietHours());
        Assert.Equal(NotifyOutcome.SuppressedQuietHours, center.Send("a", "t", "b", "test", TimeSpan.Zero));
        Assert.Equal(NotifyOutcome.Sent, center.Send("b", "t", "b", "test", TimeSpan.Zero, urgent: true));
        Assert.Single(notifier.Shown);
        Assert.Equal(new DateTime(2026, 1, 2, 7, 0, 0), center.Quiet.NextEnd(clock.Now));
    }

    [Fact]
    public void FailingNotifierIsContained()
    {
        var center = new NotificationCenter(new FakeNotifier { Throw = true }, new ManualClock(new DateTime(2026, 1, 1, 12, 0, 0)),
            new QuietHours { Enabled = false });
        Assert.Equal(NotifyOutcome.Failed, center.Send("a", "t", "b", "test", TimeSpan.Zero));
    }
}

[Trait("Category", "Unit")]
public class RuleEngineTests
{
    private sealed class ThrowingAction : RuleAction
    {
        public override string Description => "explode";
        public override ActionResult Execute(ActionContext ctx) => throw new InvalidOperationException("boom");
    }

    private static RuleDefinition CpuRule(string id = "cpu-test", FirePolicy policy = FirePolicy.OnTransition,
        TimeSpan? cooldown = null, TimeSpan? sustain = null, int priority = 50, bool defer = false, IReadOnlyList<RuleAction>? actions = null) => new()
    {
        Id = id,
        Name = id,
        Description = "test rule",
        Priority = priority,
        Policy = policy,
        Cooldown = cooldown ?? TimeSpan.Zero,
        Sustain = sustain ?? TimeSpan.Zero,
        DeferDuringQuietHours = defer,
        When = Condition.On(Signals.Cpu, (v, _) => v >= 0.8, (v, _) => "cpu at " + v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
        Actions = actions ?? [new PromoteWidgets(["cpu"], _ => ["cpu"]), new Notify(_ => ("CPU", "high"), _ => id, TimeSpan.Zero)],
    };

    [Fact]
    public void FiresOnlyOnTransition()
    {
        var h = new SmartHarness(rules: [CpuRule()]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        h.Signals.Publish(Signals.Cpu, 0.95, "t"); // still true: no second fire
        Assert.Equal(1, h.Fired("cpu-test"));
        h.Signals.Publish(Signals.Cpu, 0.3, "t");
        Assert.Contains(h.HistoryFor("cpu-test"), e => e.Kind == "Ended" && e.Detail.Contains("Restored"));
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Equal(2, h.Fired("cpu-test"));
        Assert.Equal(2, h.Notifier.Shown.Count);
    }

    [Fact]
    public void CooldownSuppressesReentry()
    {
        var h = new SmartHarness(rules: [CpuRule(cooldown: TimeSpan.FromMinutes(10))]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        h.Signals.Publish(Signals.Cpu, 0.3, "t");
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Equal(1, h.Fired("cpu-test"));
        Assert.Contains(h.HistoryFor("cpu-test"), e => e.Kind == "Suppressed" && e.Explanation.Contains("repeat policy"));
        Assert.Empty(h.Dock.Layouts.Layers); // suppressed means no dock change either

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.Signals.Publish(Signals.Cpu, 0.3, "t");
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Equal(2, h.Fired("cpu-test"));
    }

    [Fact]
    public void SustainIgnoresSpikesAndUsesTheSharedHeartbeat()
    {
        var h = new SmartHarness(rules: [CpuRule(sustain: TimeSpan.FromSeconds(60))]).Start();
        h.Run(TimeSpan.FromSeconds(1), () => h.Signals.Publish(Signals.Cpu, 0.99, "t"));
        h.Run(TimeSpan.FromSeconds(5), () => h.Signals.Publish(Signals.Cpu, 0.2, "t"));
        Assert.Equal(0, h.Fired("cpu-test"));

        // Constant value: no change events after the first, so only the heartbeat can fire it.
        h.Run(TimeSpan.FromSeconds(59), () => h.Signals.Publish(Signals.Cpu, 0.9, "t"));
        Assert.Equal(0, h.Fired("cpu-test"));
        h.Run(TimeSpan.FromSeconds(2), () => h.Signals.Publish(Signals.Cpu, 0.9, "t"));
        Assert.Equal(1, h.Fired("cpu-test"));
        Assert.Contains(Signals.Cpu.Id, h.HistoryFor("cpu-test").Last(e => e.Kind == "Fired").Explanation);
    }

    [Fact]
    public void RepeatingPolicyRefiresEveryCooldown()
    {
        var h = new SmartHarness(rules: [CpuRule(policy: FirePolicy.Repeating, cooldown: TimeSpan.FromMinutes(5))]).Start();
        h.Run(TimeSpan.FromMinutes(11), () => h.Signals.Publish(Signals.Cpu, 0.9, "t"));
        Assert.Equal(3, h.Fired("cpu-test")); // t=1s, 5m1s, 10m1s
    }

    [Fact]
    public void QuietHoursDeferWithoutUsingUpTheDay()
    {
        var quiet = new QuietHours { Enabled = true, Start = TimeSpan.FromHours(12), End = TimeSpan.FromHours(13) };
        var h = new SmartHarness(new DateTime(2026, 3, 10, 12, 30, 0), [CpuRule(policy: FirePolicy.OncePerDay, defer: true)], quiet: quiet).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Equal(0, h.Fired("cpu-test"));
        Assert.Contains(h.HistoryFor("cpu-test"), e => e.Kind == "Deferred" && e.Explanation.Contains("quiet hours"));
        Assert.Empty(h.Notifier.Shown);

        h.Run(TimeSpan.FromMinutes(31), () => h.Signals.Publish(Signals.Cpu, 0.9, "t"));
        Assert.Equal(1, h.Fired("cpu-test"));
        Assert.Single(h.Notifier.Shown);
        Assert.Single(h.HistoryFor("cpu-test"), e => e.Kind == "Deferred"); // deferred once, not every second
    }

    [Fact]
    public void PriorityOrdersEvaluationAndResolvesLayoutConflicts()
    {
        var promote = CpuRule("promoter", priority: 90, actions: [new PromoteWidgets(["media"])]);
        var collapse = CpuRule("collapser", priority: 70, actions: [new CollapseWidgets(["media"])]);
        var h = new SmartHarness(rules: [collapse, promote]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        var fired = h.Engine.History.Where(e => e.Kind == "Fired").Select(e => e.RuleId).ToList();
        Assert.Equal(new[] { "promoter", "collapser" }, fired);
        Assert.Equal("media", h.Dock.Effective(h.Clock.Now).Right[0]);
    }

    [Fact]
    public void StatePersistsAndActiveAutomationsResumeWithoutRepeatingNotifications()
    {
        var store = new InMemoryPersistence();
        var h1 = new SmartHarness(rules: [CpuRule()], persistence: store).Start();
        h1.Signals.Publish(Signals.Cpu, 0.9, "t");
        h1.Engine.SetParameter("cpu-test", "note", "kept");
        h1.Engine.Stop();
        Assert.Empty(h1.Dock.Layouts.Layers); // stopping undoes dock changes
        Assert.True(store.State.Rules["cpu-test"].Active);
        Assert.Equal("kept", store.State.Rules["cpu-test"].Parameters["note"]);

        var h2 = new SmartHarness(h1.Clock.Now, [CpuRule()], store).Start();
        Assert.Single(h2.Dock.Layouts.Layers);                 // layout re-applied
        Assert.Empty(h2.Notifier.Shown);                       // but no second notification
        Assert.Contains(h2.Engine.History, e => e.Kind == "Resumed");
        h2.Signals.Publish(Signals.Cpu, 0.2, "t");
        Assert.Empty(h2.Dock.Layouts.Layers);
        Assert.False(store.State.Rules["cpu-test"].Active);
        Assert.Equal(1, store.State.Rules["cpu-test"].FireCount);
    }

    [Fact]
    public void OncePerDayIsRememberedAcrossRestarts()
    {
        var store = new InMemoryPersistence();
        var h1 = new SmartHarness(rules: [CpuRule(policy: FirePolicy.OncePerDay)], persistence: store).Start();
        h1.Signals.Publish(Signals.Cpu, 0.9, "t");
        h1.Signals.Publish(Signals.Cpu, 0.2, "t");
        h1.Engine.Stop();
        var h2 = new SmartHarness(h1.Clock.Now.AddHours(1), [CpuRule(policy: FirePolicy.OncePerDay)], store).Start();
        h2.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Contains(h2.Engine.History, e => e.Kind == "Suppressed" && e.Explanation.Contains("repeat policy"));
        Assert.Empty(h2.Notifier.Shown);
    }

    [Fact]
    public void FailingActionsAreIsolated()
    {
        var bad = CpuRule("bad", actions: [new ThrowingAction(), new PromoteWidgets(["ram"])]);
        var good = CpuRule("good");
        var h = new SmartHarness(rules: [bad, good]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Equal(1, h.Fired("good"));
        Assert.Contains(h.HistoryFor("bad"), e => e.Kind == "Error");
        Assert.Contains(h.Dock.Layouts.Layers, l => l.Owner == "bad"); // the other action of the bad rule still ran
        Assert.True(h.Engine.Stats.Errors >= 1);
    }

    [Fact]
    public void RepeatedlyFailingRuleIsDisabledThenCanBeReenabled()
    {
        var crashing = new RuleDefinition
        {
            Id = "crash", Name = "crash", Description = "",
            When = Condition.On(Signals.Cpu, (_, _) => throw new InvalidOperationException("bad condition"), (_, _) => ""),
            Actions = [],
        };
        var h = new SmartHarness(rules: [crashing, CpuRule("good")]).Start();
        for (int i = 0; i < 4; i++) h.Signals.Publish(Signals.Cpu, 0.81 + i * 0.01, "t");
        var status = h.Engine.Status("crash");
        Assert.False(status.Enabled);
        Assert.True(status.State.ErrorDisabled);
        Assert.Contains(h.HistoryFor("crash"), e => e.Kind == "Disabled");
        Assert.Equal(1, h.Fired("good")); // unaffected

        h.Engine.SetEnabled("crash", true);
        Assert.False(h.Engine.Status("crash").State.ErrorDisabled);
    }

    [Fact]
    public void RepeatedlyFailingActionsDisableOnlyTheirRule()
    {
        var bad = CpuRule("bad-actions", actions: [new ThrowingAction()]);
        var good = CpuRule("good-actions");
        var h = new SmartHarness(rules: [bad, good]).Start();
        for (int i = 0; i < RuleEngine.ErrorLimit; i++)
        {
            h.Signals.Publish(Signals.Cpu, 0.9, "t");
            h.Signals.Publish(Signals.Cpu, 0.1, "t");
        }
        Assert.True(h.Engine.Status("bad-actions").State.ErrorDisabled);
        Assert.False(h.Engine.Status("good-actions").State.ErrorDisabled);
        Assert.Equal(RuleEngine.ErrorLimit, h.Fired("good-actions"));
    }

    [Fact]
    public void DisablingAnActiveAutomationRestoresTheDock()
    {
        var h = new SmartHarness(rules: [CpuRule()]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        Assert.Single(h.Dock.Layouts.Layers);
        h.Engine.SetEnabled("cpu-test", false);
        Assert.Empty(h.Dock.Layouts.Layers);
        Assert.False(h.Persistence.State.Rules["cpu-test"].Enabled);
    }

    [Fact]
    public void ManualThemeOverrideOwnerIsPersisted()
    {
        var h = new SmartHarness(rules: [CpuRule()]).Start();
        h.Engine.SetThemeOverrideOwner(Automations.SunsetTheme);
        Assert.Equal(Automations.SunsetTheme, h.Persistence.State.ThemeOverrideOwner);
        h.Engine.SetThemeOverrideOwner(null);
        Assert.Null(h.Persistence.State.ThemeOverrideOwner);
    }

    [Fact]
    public void HistoryExplainsWhyARuleFired()
    {
        var h = new SmartHarness(rules: [CpuRule()]).Start();
        h.Signals.Publish(Signals.Cpu, 0.91, "t");
        var fired = h.HistoryFor("cpu-test").Single(e => e.Kind == "Fired");
        Assert.Equal("Condition matched using system.cpu", fired.Explanation);
        Assert.Contains("Show cpu first", fired.Detail);
        Assert.Contains("notification sent", fired.Detail);
        Assert.Equal("Condition matched using system.cpu", h.Engine.Status("cpu-test").State.LastExplanation);
    }

    [Fact]
    public void StaleSignalsNeitherEnterNorExit()
    {
        var h = new SmartHarness(rules: [CpuRule()]).Start();
        h.Signals.Publish(Signals.Cpu, 0.9, "t");
        h.Run(TimeSpan.FromSeconds(30)); // no publishes: CPU goes stale
        Assert.Single(h.Dock.Layouts.Layers);           // unknown does not exit
        Assert.Equal(1, h.Fired("cpu-test"));
    }

    [Fact]
    public void DuplicateRuleIdsAreRejected()
    {
        var h = new SmartHarness(rules: [CpuRule()]);
        Assert.Throws<ArgumentException>(() => h.Engine.Register(CpuRule()));
    }
}

[Trait("Category", "Unit")]
public class SunCalculatorTests
{
    [Theory]
    [InlineData("19:00", "07:00", "18:59", false)]
    [InlineData("19:00", "07:00", "19:00", true)]
    [InlineData("19:00", "07:00", "02:30", true)]
    [InlineData("19:00", "07:00", "07:00", false)]
    [InlineData("17:46", "17:52", "17:50", true)]   // same-day window (found in Phase B: was ignored)
    [InlineData("17:46", "17:52", "17:53", false)]
    [InlineData("17:46", "17:52", "17:40", false)]
    [InlineData("12:00", "12:00", "12:00", false)]  // empty window
    public void SetTimeEveningWindow(string evening, string morning, string now, bool expected) =>
        Assert.Equal(expected, SunCalculator.IsEveningBySetTime(TimeSpan.Parse(now), TimeSpan.Parse(evening), TimeSpan.Parse(morning)));

    [Theory]
    [InlineData("19:00", 19, 0)]
    [InlineData("7:05", 7, 5)]
    [InlineData(" 23:59 ", 23, 59)]
    [InlineData("25:00", 6, 0)]
    [InlineData("evening", 6, 0)]
    [InlineData(null, 6, 0)]
    public void TimeOfDayParameterParsing(string? text, int h, int m) =>
        Assert.Equal(new TimeSpan(h, m, 0), SunCalculator.ParseTimeOfDay(text, TimeSpan.FromHours(6)));

    [Fact]
    public void SunSourceReadsTheAutomationsTimeParameters()
    {
        // The sun signal must use the sunset automation's own parameters, not hard-coded 19:00/07:00.
        string app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Strata", "App.cs"));
        Assert.Contains("GetValueOrDefault(\"eveningStart\")", app);
        Assert.Contains("GetValueOrDefault(\"morningStart\")", app);
        Assert.Contains("SunCalculator.IsEveningBySetTime(", app);
    }

    [Fact]
    public void LondonMidsummer()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var (rise, set) = SunCalculator.Compute(new DateTime(2024, 6, 21), 51.5074, -0.1278, tz);
        Assert.InRange(set!.Value.TimeOfDay, new TimeSpan(21, 10, 0), new TimeSpan(21, 30, 0)); // ~21:21 BST
        Assert.InRange(rise!.Value.TimeOfDay, new TimeSpan(4, 35, 0), new TimeSpan(4, 55, 0)); // ~04:43 BST
    }

    [Fact]
    public void PolarDayHasNoSunset()
    {
        var (rise, set) = SunCalculator.Compute(new DateTime(2024, 6, 21), 80, 15, TimeZoneInfo.Utc);
        Assert.Null(rise);
        Assert.Null(set);
    }
}
