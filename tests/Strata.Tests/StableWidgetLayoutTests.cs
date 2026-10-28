using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Strata.Controls;
using Strata.Core;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class StableWidgetLayoutTests
{
    private sealed class Host : IDockHost
    {
        public AppSettings Settings { get; } = new();
        public ScreenTimeTracker ScreenTime { get; } = new();
        public SignalStore Signals { get; } = new(new ManualClock(new DateTime(2026, 1, 1, 12, 0, 0)));
        public void Notify(string key, string title, string text, bool urgent = false) { }
        public void ShowFlyout(FrameworkElement anchor, FrameworkElement content, double width = 320, bool fromKeyboard = false) { }
        public void CloseFlyout() { }
        public void SaveSettings() { }
        public void OpenSettings() { }
    }

    private sealed class TextProbeWidget : Widget
    {
        private readonly TextBlock _text = UI.Text("");
        public TextProbeWidget(IDockHost host) : base(host) => Child = _text;
        public void Set(string text) => _text.Text = text;
    }

    public static IEnumerable<object[]> DpiScales => [
        [1.0], [1.25], [1.5], [2.0], [2.5],
    ];

    /// <summary>
    /// A hundred AI-usage states in a row leave the dock and the neighbouring slots where they were. The row here
    /// is detached from any PresentationSource, so WPF's layout manager never runs over it and this cannot, on its
    /// own, catch a slot that stopped having a fixed width at all: that is what
    /// QuotaDockGeometryTests.RealQuotaWidgetChangesOnlyItsOwnContentOnThePillDock is for, on a real dock.
    /// </summary>
    [Theory]
    [MemberData(nameof(DpiScales))]
    public void OneHundredAiTransitionsNeverMoveNeighboursOrResizeDock(double dpiScale)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var probe = new TextProbeWidget(new Host());
                var stable = new StableWidgetHost("aiusage", DockSize.Medium, probe);
                var left = new Border { Width = 80 };
                var right = new Border { Width = 94 };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(left);
                row.Children.Add(stable);
                row.Children.Add(right);
                string[] states = [
                    "Codex 0% remaining", "Claude 100% remaining", "Loading…", "Signed out", "Warning",
                    "Limit reached", "Resets at the end of a very long exceptional description", "Unavailable",
                    "Network error while refreshing provider usage",
                ];
                int? dockWidth = null, neighbourLeft = null, neighbourWidth = null;
                for (int i = 0; i < 100; i++)
                {
                    probe.Set(states[i % states.Length]);
                    row.Measure(new Size(double.PositiveInfinity, 48));
                    row.Arrange(new Rect(0, 0, row.DesiredSize.Width, 48));
                    int physicalDock = (int)Math.Round(row.ActualWidth * dpiScale, MidpointRounding.AwayFromZero);
                    int physicalLeft = (int)Math.Round(right.TranslatePoint(new Point(), row).X * dpiScale, MidpointRounding.AwayFromZero);
                    int physicalWidth = (int)Math.Round(right.ActualWidth * dpiScale, MidpointRounding.AwayFromZero);
                    dockWidth ??= physicalDock;
                    neighbourLeft ??= physicalLeft;
                    neighbourWidth ??= physicalWidth;
                    Assert.InRange(Math.Abs(physicalDock - dockWidth.Value), 0, 1);
                    Assert.Equal(neighbourLeft.Value, physicalLeft);
                    Assert.Equal(neighbourWidth.Value, physicalWidth);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA layout test timed out");
        if (failure is not null) throw failure;
    }

    [Theory]
    [MemberData(nameof(DpiScales))]
    public void EveryLiveWidgetHasInvariantPhysicalBounds(double dpiScale)
    {
        string[] ids = ["clock", "network", "cpu", "ram", "media", "battery", "screentime", "weather", "aiusage"];
        var first = StableWidgetHost.PhysicalBounds(ids, DockSize.Medium, dpiScale);
        for (int refresh = 0; refresh < 100; refresh++)
            Assert.Equal(first, StableWidgetHost.PhysicalBounds(ids, DockSize.Medium, dpiScale));
    }

    [Fact]
    public void AiSlotIsDeliberatelyCompactAndScalesOnlyWithUserSize()
    {
        Assert.InRange(StableWidgetHost.WidthFor("aiusage", DockSize.Medium), 230, 280);
        Assert.True(StableWidgetHost.WidthFor("aiusage", DockSize.Small) < StableWidgetHost.WidthFor("aiusage", DockSize.Medium));
        Assert.True(StableWidgetHost.WidthFor("aiusage", DockSize.Large) > StableWidgetHost.WidthFor("aiusage", DockSize.Medium));
    }

    // ---- The second declared width: a widget with nothing at all to show ----

    private sealed class IdleProbeWidget : Widget
    {
        public IdleProbeWidget(IDockHost host) : base(host) => Child = UI.Text("");
        public void Report(bool idle) => IsIdle = idle;
    }

    [Fact]
    public void OnlyTheMediaPillDeclaresAnIdleWidthAndItIsNarrowerAtEverySize()
    {
        foreach (DockSize size in Enum.GetValues<DockSize>())
        {
            Assert.True(StableWidgetHost.IdleWidthFor("media", size) < StableWidgetHost.WidthFor("media", size),
                $"the idle media slot must be narrower than the playing one at {size}");
            Assert.InRange(StableWidgetHost.IdleWidthFor("media", size), 30, 60);     // an icon's room, no more
        }
        // Every other widget has one width: asking for its idle width gives back the only width it has.
        foreach (string id in new[] { "clock", "network", "cpu", "ram", "battery", "screentime", "weather", "aiusage", "calendar", "volume", "todos" })
            Assert.Equal(StableWidgetHost.WidthFor(id, DockSize.Medium), StableWidgetHost.IdleWidthFor(id, DockSize.Medium));
    }

    [Fact]
    public void AnIdleSlotTakesTheCompactWidthAndGivesItBackWhenTheWidgetHasSomethingToShow() => OnSta(() =>
    {
        var probe = new IdleProbeWidget(new Host());
        var stable = new StableWidgetHost("media", DockSize.Medium, probe);
        double playing = StableWidgetHost.WidthFor("media", DockSize.Medium);
        double quiet = StableWidgetHost.IdleWidthFor("media", DockSize.Medium);

        Assert.Equal(playing, stable.Width);
        probe.Report(true);
        Assert.Equal(quiet, stable.Width);
        probe.Report(false);
        Assert.Equal(playing, stable.Width);
        // Only the state change moves it: repeating the same report cannot.
        probe.Report(false);
        Assert.Equal(playing, stable.Width);
    });

    /// <summary>
    /// The real construction order: a widget renders itself in its own constructor, so the media pill knows it is
    /// idle before its slot exists. The slot must start compact rather than take its full width and snap.
    /// </summary>
    [Fact]
    public void AWidgetAlreadyIdleBeforeItsSlotExistsStartsCompact() => OnSta(() =>
    {
        var probe = new IdleProbeWidget(new Host());
        probe.Report(true);

        var stable = new StableWidgetHost("media", DockSize.Medium, probe);

        Assert.Equal(StableWidgetHost.IdleWidthFor("media", DockSize.Medium), stable.Width);
    });

    /// <summary>
    /// An idle flip is the only thing that may resize a slot: a widget that never reports idle has one width for
    /// its whole life, however its content changes. Like the rest of this file this reads the width property back
    /// in a detached tree, which proves the contract the host declares but not what a layout does with it;
    /// QuotaDockGeometryTests measures the same thing on a real dock through a real layout pass.
    /// </summary>
    [Fact]
    public void AWidgetThatNeverGoesIdleKeepsItsSlotWidthThroughEveryRefresh() => OnSta(() =>
    {
        var probe = new TextProbeWidget(new Host());
        var stable = new StableWidgetHost("aiusage", DockSize.Medium, probe);

        for (int i = 0; i < 100; i++)
        {
            probe.Set(i % 2 == 0 ? "Claude 100% remaining" : "");
            Assert.Equal(StableWidgetHost.WidthFor("aiusage", DockSize.Medium), stable.Width);
        }
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA layout test timed out");
        if (failure is not null) throw failure;
    }
}
