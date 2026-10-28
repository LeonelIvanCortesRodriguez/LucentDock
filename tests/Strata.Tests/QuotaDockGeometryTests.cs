using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Strata.Controls;
using Strata.Core;
using Strata.Quota;
using Strata.Smart;
using Strata.Views;
using Strata.Widgets;
using Xunit.Abstractions;

namespace Strata.Tests;

/// <summary>
/// The owner's requirement for the AI usage tile, on the real <see cref="QuotaWidget"/> inside its real
/// <see cref="StableWidgetHost"/> on a real pill <see cref="DockWindow"/> (SizeToContent.Width): moving between Claude
/// and Codex, loading, sign-in, unavailable, stale, used and remaining figures, warning and critical dots and the
/// countdown changes only the tile's own content. The dock's outer size, the tile's slot and both neighbours keep their
/// physical bounds, nothing enters or leaves overflow, and long text is trimmed inside the slot.
/// No window is shown and no AppBar is registered (the dock never gets a handle); quota figures come only from fixture
/// monitors injected into <see cref="QuotaService"/>, so neither account is read, and the dock's window list is a
/// fixture with no windows, so the desktop is not enumerated.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QuotaDockGeometryTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly double[] DpiScales = [1.0, 1.25, 1.5, 2.0, 2.5];
    private static readonly DateTime FixedNow = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Local);

    private sealed class NoWindows : IWindowSource
    {
        public bool Refresh() => false;
        public IReadOnlyList<TrackedWindow> Windows => [];
        public IntPtr Foreground => IntPtr.Zero;
        public void Activate(IntPtr window) { }
        public void Minimize(IntPtr window) { }
        public void Close(IntPtr window) { }
        public bool IsMinimized(IntPtr window) => false;
    }

    /// <summary>A monitor whose width the test sets, so the overflow planner can be held right at its limit.</summary>
    private sealed class TestMonitor : IMonitorWindowGeometry
    {
        public int WidthPx { get; set; } = 3840;
        public double Scale { get; set; } = 1;
        public IReadOnlyList<IntPtr> EnumerateMonitors() => [IntPtr.Zero];
        public IntPtr PrimaryMonitor() => IntPtr.Zero;
        public PixelBounds Bounds(IntPtr monitor) => new(0, 0, WidthPx, 2160);
        public IntPtr MonitorForWindow(IntPtr window) => IntPtr.Zero;
        public double ScaleForWindow(IntPtr window) => Scale;
        public void PlaceTopmost(IntPtr window, int x, int y) { }
    }

    private sealed class FixtureSource(Func<Task<QuotaSnapshot>> read) : IQuotaSource
    {
        public Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct) => read();
    }

    private sealed record State(string Name, Func<QuotaSettings, QuotaMonitor> Monitor, bool ShowsRemaining = true,
        QuotaTileMode Mode = QuotaTileMode.Rotate, bool ExpectProviderTrimmed = false);

    private static DateTimeOffset Now => new(FixedNow);

    private static QuotaWindow Window(string label, double? used = null, double? remaining = null, DateTimeOffset? resets = null,
        int? minutes = 300, QuotaWindowKind kind = QuotaWindowKind.Session) => new()
    {
        Id = label, Label = label, Kind = kind, ReportedUsedPercent = used, ReportedRemainingPercent = remaining,
        ResetsAt = resets, WindowMinutes = minutes, Reached = used >= 100,
    };

    private static QuotaSnapshot Snapshot(QuotaProvider provider, TimeSpan age, bool sample, params QuotaWindow[] windows) => new()
    {
        Provider = provider, Source = "Test fixture", IsSample = sample, ObservedAt = Now - age, Plan = "Fixture", Windows = windows,
    };

    /// <summary>A fixture monitor: each provider either has a reading, fails with <paramref name="claudeError"/> /
    /// <paramref name="codexError"/>, or (null and no error) never answers, which is the loading state.</summary>
    private static QuotaMonitor Monitor(QuotaSettings q, QuotaSnapshot? claude, QuotaSnapshot? codex,
        QuotaErrorKind claudeError = QuotaErrorKind.None, QuotaErrorKind codexError = QuotaErrorKind.None)
    {
        static Func<Task<QuotaSnapshot>> Read(QuotaSnapshot? s, QuotaErrorKind error) => () =>
            error != QuotaErrorKind.None ? Task.FromException<QuotaSnapshot>(new QuotaSourceException(error, "fixture " + error))
            : s is not null ? Task.FromResult(s)
            : new TaskCompletionSource<QuotaSnapshot>().Task;
        // A reading with an error after it is kept and shown as stale, as after a real failed refresh.
        var stored = new QuotaStore { Claude = claude, Codex = codex };
        var monitor = new QuotaMonitor(q, new FixtureSource(Read(codex, codexError)), new FixtureSource(Read(claude, claudeError)),
            () => null, () => false, stored, null, () => Now);
        if (claudeError != QuotaErrorKind.None || codexError != QuotaErrorKind.None) RunWhilePumping(monitor.RefreshAsync());
        return monitor;
    }

    private static IReadOnlyList<State> States()
    {
        var soon = Now.AddMinutes(68);
        var days = Now.AddDays(4).AddHours(2);
        var session = Window("Session (5h)", used: 36, resets: soon);
        var weekly = Window("Weekly", used: 58, resets: days, minutes: 10080, kind: QuotaWindowKind.Weekly);
        QuotaSnapshot Claude(params QuotaWindow[] w) => Snapshot(QuotaProvider.Claude, TimeSpan.FromMinutes(1), false, w);
        QuotaSnapshot Codex(params QuotaWindow[] w) => Snapshot(QuotaProvider.Codex, TimeSpan.FromMinutes(1), false, w);
        return
        [
            new("loading", q => Monitor(q, null, null)),
            new("claude percent remaining, codex percent remaining", q => Monitor(q, Claude(session), Codex(weekly))),
            new("percent used", q => Monitor(q, Claude(session), Codex(weekly)), ShowsRemaining: false),
            new("countdown absent", q => Monitor(q, Claude(Window("Session (5h)", used: 20)), Codex(Window("Weekly", used: 10, minutes: 10080, kind: QuotaWindowKind.Weekly)))),
            new("warning dot", q => Monitor(q, Claude(Window("Session (5h)", used: 85, resets: soon)), Codex(Window("Weekly", used: 82, resets: days, minutes: 10080, kind: QuotaWindowKind.Weekly)))),
            new("critical dot", q => Monitor(q, Claude(Window("Session (5h)", used: 97, resets: soon)), Codex(Window("Weekly", used: 100, resets: days, minutes: 10080, kind: QuotaWindowKind.Weekly)))),
            new("over limit", q => Monitor(q, Claude(Window("Spend limit", remaining: -12, resets: soon, minutes: null, kind: QuotaWindowKind.Spend)), Codex(weekly))),
            new("reset passed, waiting for a new sample", q => Monitor(q, Claude(Window("Session (5h)", used: 40, resets: Now.AddMinutes(-3))), Codex(Window("Weekly", used: 40, resets: Now.AddMinutes(-1), minutes: 10080, kind: QuotaWindowKind.Weekly)))),
            new("signed out and unavailable", q => Monitor(q, null, null, QuotaErrorKind.SignInRequired, QuotaErrorKind.Offline)),
            new("no windows reported", q => Monitor(q, Claude(), Codex())),
            new("stale", q => Monitor(q, Snapshot(QuotaProvider.Claude, TimeSpan.FromHours(3), false, session), Snapshot(QuotaProvider.Codex, TimeSpan.FromHours(5), false, weekly))),
            new("stale after an error", q => Monitor(q, Claude(session), Codex(weekly), QuotaErrorKind.Timeout, QuotaErrorKind.RateLimited)),
            new("one loading, one fresh", q => Monitor(q, null, Codex(weekly))),
            new("longest labels", q => Monitor(q,
                Snapshot(QuotaProvider.Claude, TimeSpan.FromMinutes(1), true, Window("Claude Opus 4.1 Extended Thinking · weekly", used: 99.96, resets: days, minutes: 10080, kind: QuotaWindowKind.Model)),
                Snapshot(QuotaProvider.Codex, TimeSpan.FromMinutes(1), true, Window("GPT-5.5 Codex Max Extended · 7-day window", remaining: -100, resets: days, minutes: 10080, kind: QuotaWindowKind.Model))),
                ExpectProviderTrimmed: true),
            new("short labels", q => Monitor(q, Claude(Window("Weekly", used: 1, resets: days, minutes: 10080, kind: QuotaWindowKind.Weekly)), Codex(Window("Weekly", used: 0.04, resets: days, minutes: 10080, kind: QuotaWindowKind.Weekly)))),
            new("most constrained mode", q => Monitor(q, Claude(session), Codex(weekly)), Mode: QuotaTileMode.MostConstrained),
        ];
    }

    private sealed record Sample(string Size, double Dpi, string State, int Rotation, int DockWidth, int DockHeight,
        int SlotLeft, int SlotWidth, int LeftNeighbourLeft, int LeftNeighbourWidth, int RightNeighbourLeft, int RightNeighbourWidth,
        string OverflowAtLimit, string OverflowTight, string Provider, string Value, string Countdown, bool Dot);

    [Fact]
    public void RealQuotaWidgetChangesOnlyItsOwnContentOnThePillDock()
    {
        var samples = new List<Sample>();
        var failures = new List<string>();
        RunSta(() =>
        {
            var previousClock = AppClock.Current;
            var previousTheme = Theme.Current;
            AppClock.Current = new ManualClock(FixedNow);
            try { Exercise(samples, failures); }
            finally
            {
                // The dock's rebuilds set the global palette and fill the application resources; other tests render
                // with the default palette and no application resources, as before this test made an App.
                AppClock.Current = previousClock;
                QuotaService.Reset();
                typeof(Theme).GetProperty(nameof(Theme.Current))!.SetValue(null, previousTheme);
                Application.Current?.Resources.Clear();
            }
        });

        int maxDockWidthVariation = samples.GroupBy(s => (s.Size, s.Dpi)).Max(g => g.Max(s => s.DockWidth) - g.Min(s => s.DockWidth));
        int maxDockHeightVariation = samples.GroupBy(s => (s.Size, s.Dpi)).Max(g => g.Max(s => s.DockHeight) - g.Min(s => s.DockHeight));
        output.WriteLine($"{samples.Count} samples; max dock width variation {maxDockWidthVariation}px, height {maxDockHeightVariation}px");
        foreach (var g in samples.GroupBy(s => (s.Size, s.Dpi)))
            output.WriteLine($"{g.Key.Size} @ {g.Key.Dpi * 100:0}%: dock {g.First().DockWidth}x{g.First().DockHeight}px, slot {g.First().SlotLeft}+{g.First().SlotWidth}px");
        if (Environment.GetEnvironmentVariable("STRATA_LAYOUT_EVIDENCE") is { Length: > 0 } evidence)
            File.WriteAllText(evidence, JsonSerializer.Serialize(new { maxDockWidthVariation, maxDockHeightVariation, samples }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
        Assert.Equal(3 * DpiScales.Length * States().Count * 2, samples.Count);
    }

    /// <summary>
    /// The media pill's two widths on the real pill dock, through a real layout pass. Idle it takes the compact
    /// slot, playing it takes the full one, the dock's own width moves by exactly the difference and the other
    /// widgets keep their widths. Because the pill is centred on the monitor, that difference also means every tile
    /// slides half of it sideways whenever a track starts or stops, which is why the slot glides between the two.
    /// The detached-tree tests in StableWidgetLayoutTests read the width property back and cannot see a layout at
    /// all; this one can, and it is what fails if a slot stops having a fixed width.
    /// </summary>
    [Fact]
    public void TheMediaPillTakesItsTwoDeclaredWidthsOnTheRealPillDock()
    {
        RunSta(() =>
        {
            var previousClock = AppClock.Current;
            var previousTheme = Theme.Current;
            var previousMedia = MediaService.Current;
            AppClock.Current = new ManualClock(FixedNow);
            try
            {
                var app = SharedApp.Value;
                typeof(App).GetField("<WindowSnapshots>k__BackingField", Private)!.SetValue(app, new WindowSnapshotCoordinator(new NoWindows()));
                app.ResetScreenTime();
                var s = app.Settings;
                s.Shape = DockShape.Pill;
                s.Style = DockStyle.Clear;
                s.ShowStartButton = true;
                s.ShowSearchButton = false;
                s.Pinned = [];
                s.Size = DockSize.Medium;
                s.LeftWidgets = ["media"];
                s.RightWidgets = ["clock"];
                var media = new MediaService(app.Signals);
                media.UseFixture("", "", false, null);
                MediaService.Current = media;

                var dock = new DockWindow(app, materialEnvironment: () => MaterialEnvironment.PremiumFixture, monitorGeometry: new TestMonitor());
                dock.SynchronizeSettings();
                Pump();

                var hosts = (Dictionary<Widget, StableWidgetHost>)typeof(DockWindow).GetField("_widgetHosts", Private)!.GetValue(dock)!;
                var pill = dock.Widgets.Single(w => w.WidgetId == "media");
                var slot = hosts[pill];
                var clock = hosts[dock.Widgets.Single(w => w.WidgetId == "clock")];
                double full = StableWidgetHost.WidthFor("media", DockSize.Medium);
                double quiet = StableWidgetHost.IdleWidthFor("media", DockSize.Medium);

                double Width() { Pump(); return Measure(dock).Width; }

                // Nothing playing: the compact slot, and a dock measured around it.
                Assert.Equal(quiet, slot.Width);
                Assert.Equal(quiet, slot.TargetWidth);
                double idleDock = Width();
                double clockWidth = clock.ActualWidth;

                // A track starts: the slot and the dock both grow by exactly the declared difference.
                media.UseFixture("Song", "Artist", true, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(200));
                pill.Tick();
                Assert.Equal(full, slot.Width);
                double playingDock = Width();
                Assert.Equal(full - quiet, playingDock - idleDock, 1);
                Assert.Equal(clockWidth, clock.ActualWidth);

                // And back, to the pixel: the two widths are the only two it has.
                media.UseFixture("", "", false, null);
                pill.Tick();
                Assert.Equal(quiet, slot.Width);
                Assert.Equal(idleDock, Width(), 1);
                Assert.Equal(clockWidth, clock.ActualWidth);
            }
            finally
            {
                AppClock.Current = previousClock;
                MediaService.Current = previousMedia;
                QuotaService.Reset();
                typeof(Theme).GetProperty(nameof(Theme.Current))!.SetValue(null, previousTheme);
                Application.Current?.Resources.Clear();
            }
        });
    }

    private static void Exercise(List<Sample> samples, List<string> failures)
    {
        var app = SharedApp.Value;
        // The App's queued startup (OnStartup: taskbar, tray, a real dock) runs only on its own thread's dispatcher,
        // which never runs. Everything here is on another thread.
        Assert.NotSame(Thread.CurrentThread, app.Dispatcher.Thread);
        // The dock's window list: a fixture with no windows (set before the dock reads it).
        typeof(App).GetField("<WindowSnapshots>k__BackingField", Private)!.SetValue(app, new WindowSnapshotCoordinator(new NoWindows()));
        app.ResetScreenTime();
        var s = app.Settings;
        s.Shape = DockShape.Pill;
        s.Style = DockStyle.Clear;
        s.ShowStartButton = true;
        s.ShowSearchButton = false;
        s.Pinned = [];
        s.LeftWidgets = [];
        s.RightWidgets = ["notes", "aiusage", "clock"];
        s.Quota.Alerts = false;
        s.Quota.ClaudeEnabled = true;
        s.Quota.CodexEnabled = true;

        var monitor = new TestMonitor();
        var dock = new DockWindow(app, materialEnvironment: () => MaterialEnvironment.PremiumFixture, monitorGeometry: monitor);
        Assert.Equal(SizeToContent.Manual, dock.SizeToContent);   // before the first build

        foreach (var size in new[] { DockSize.Small, DockSize.Medium, DockSize.Large })
        foreach (double dpi in DpiScales)
        {
            s.Size = size;
            s.Quota.TileMode = QuotaTileMode.Rotate;
            s.Quota.TileShowsRemaining = true;
            var states = States();
            // The widget takes its monitor from QuotaService: hand it a fixture before the dock builds it.
            InjectQuotaMonitor(s, states[0].Monitor(s.Quota));
            dock.SynchronizeSettings();
            Pump();
            Assert.Equal(SizeToContent.Width, dock.SizeToContent);
            VisualTreeHelper.SetRootDpi(dock, new DpiScale(dpi, dpi));
            monitor.Scale = dpi;

            var hosts = (Dictionary<Widget, StableWidgetHost>)typeof(DockWindow).GetField("_widgetHosts", Private)!.GetValue(dock)!;
            var quota = Assert.IsType<QuotaWidget>(dock.Widgets.Single(w => w.WidgetId == "aiusage"));
            var slot = hosts[quota];
            var leftNeighbour = hosts[dock.Widgets.Single(w => w.WidgetId == "notes")];
            var rightNeighbour = hosts[dock.Widgets.Single(w => w.WidgetId == "clock")];
            Assert.True(ReferenceEquals(Get<QuotaMonitor>(quota, "_monitor"), QuotaService.For(s)), "the widget is not on the fixture monitor");
            Assert.Equal(StableWidgetHost.WidthFor("aiusage", size), slot.Width);

            Measure(dock);
            var layout = (Grid)typeof(DockWindow).GetField("_layout", Private)!.GetValue(dock)!;
            layout.Measure(new Size(double.PositiveInfinity, dock.Height));
            // At the limit: the monitor is less than one physical pixel wider than the dock needs. Tight: the AI slot's width short.
            int fitsPx = (int)Math.Ceiling((layout.DesiredSize.Width + 6 * 4) * dpi) + 1;
            int tightPx = fitsPx - (int)Math.Round(slot.Width * dpi);

            Sample? first = null;
            foreach (var state in states)
            {
                s.Quota.TileShowsRemaining = state.ShowsRemaining;
                s.Quota.TileMode = state.Mode;
                typeof(QuotaWidget).GetField("_monitor", Private)!.SetValue(quota, state.Monitor(s.Quota));
                for (int rotation = 0; rotation < 2; rotation++)
                {
                    typeof(QuotaWidget).GetField("_rotation", Private)!.SetValue(quota, rotation);
                    typeof(QuotaWidget).GetMethod("Render", Private | BindingFlags.DeclaredOnly, Type.EmptyTypes)!.Invoke(quota, null);
                    Pump();
                    Measure(dock);

                    string atLimit = Overflow(dock, monitor, fitsPx, hosts), tight = Overflow(dock, monitor, tightPx, hosts);
                    Overflow(dock, monitor, fitsPx, hosts);   // back to nothing overflowed before measuring bounds
                    var outer = Measure(dock);
                    var provider = Get<TextBlock>(quota, "_provider");
                    var value = Get<TextBlock>(quota, "_value");
                    var countdown = Get<TextBlock>(quota, "_countdown");
                    var dot = Get<System.Windows.Shapes.Ellipse>(quota, "_dot");
                    var (slotLeft, slotWidth) = Bounds(slot, dock, dpi);
                    var (ln, lw) = Bounds(leftNeighbour, dock, dpi);
                    var (rn, rw) = Bounds(rightNeighbour, dock, dpi);
                    var sample = new Sample(size.ToString(), dpi, state.Name, rotation, Px(outer.Width, dpi), Px(outer.Height, dpi),
                        slotLeft, slotWidth, ln, lw, rn, rw, atLimit, tight, provider.Text, value.Text, countdown.Text, dot.Visibility == Visibility.Visible);
                    samples.Add(sample);
                    string where = $"{size} @ {dpi * 100:0}% / {state.Name} / rotation {rotation}";

                    first ??= sample;
                    if (Math.Abs(sample.DockWidth - first.DockWidth) > 1) failures.Add($"{where}: dock width {sample.DockWidth}px, was {first.DockWidth}px");
                    if (Math.Abs(sample.DockHeight - first.DockHeight) > 1) failures.Add($"{where}: dock height {sample.DockHeight}px, was {first.DockHeight}px");
                    if ((slotLeft, slotWidth) != (first.SlotLeft, first.SlotWidth)) failures.Add($"{where}: AI slot {slotLeft}+{slotWidth}px, was {first.SlotLeft}+{first.SlotWidth}px");
                    if ((ln, lw) != (first.LeftNeighbourLeft, first.LeftNeighbourWidth)) failures.Add($"{where}: left neighbour moved to {ln}+{lw}px");
                    if ((rn, rw) != (first.RightNeighbourLeft, first.RightNeighbourWidth)) failures.Add($"{where}: right neighbour moved to {rn}+{rw}px");
                    if (atLimit != "none") failures.Add($"{where}: at the limit, overflowed {atLimit}");
                    if (tight != first.OverflowTight) failures.Add($"{where}: tight overflow {tight}, was {first.OverflowTight}");
                    if (slot.Visibility != Visibility.Visible) failures.Add($"{where}: the AI slot collapsed");
                    foreach (var text in new[] { provider, value, countdown })
                    {
                        if (text.Visibility != Visibility.Visible || text.ActualWidth == 0) continue;
                        var r = text.TransformToAncestor(slot).TransformBounds(new Rect(text.RenderSize));
                        if (r.Left < -0.5 || r.Right > slot.ActualWidth + 0.5) failures.Add($"{where}: '{text.Text}' leaves the slot ({r.Left:0.#}..{r.Right:0.#} of {slot.ActualWidth:0.#})");
                    }
                    if (state.ExpectProviderTrimmed && NaturalWidth(provider) <= provider.ActualWidth + 0.5)
                        failures.Add($"{where}: the longest label '{provider.Text}' fits untrimmed; the fixture no longer tests trimming");
                }
            }
        }

        // The App never started: no notifications, no taskbar change.
        Assert.Null(app.Notifications);
        Assert.False(app.TaskbarShown);

        // Only the user's own size choice moves the slot: Small < Medium < Large.
        double Slot(string size) => samples.Where(x => x.Size == size && x.Dpi == 1.0).Select(x => x.SlotWidth).First();
        if (!(Slot("Small") < Slot("Medium") && Slot("Medium") < Slot("Large"))) failures.Add("slot width does not follow the dock size");
    }

    /// <summary>
    /// The one App a DockWindow needs. WPF queues Application startup on the creating thread's dispatcher; this thread
    /// never pumps (it only sleeps), so App.OnStartup, which would touch the taskbar and show a real dock, never runs.
    /// </summary>
    private static readonly Lazy<App> SharedApp = new(() =>
    {
        if (Application.Current is not null) throw new InvalidOperationException("an Application already exists in this test process");
        App? app = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => { app = new App(new TestMonitor()); ready.Set(); Thread.Sleep(Timeout.Infinite); })
        { IsBackground = true, Name = "Strata test App (never pumped)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("test App was not created");
        return app!;
    });

    private static string Overflow(DockWindow dock, TestMonitor monitor, int widthPx, Dictionary<Widget, StableWidgetHost> hosts)
    {
        // The planner runs only for a dock with a handle; lend it a placeholder for this call alone (nothing native is called).
        var hwnd = typeof(DockWindow).GetField("_hwnd", Private)!;
        monitor.WidthPx = widthPx;
        hwnd.SetValue(dock, new IntPtr(1));
        try { typeof(DockWindow).GetMethod("ApplyResponsiveOverflow", Private)!.Invoke(dock, null); }
        finally { hwnd.SetValue(dock, IntPtr.Zero); }
        var over = hosts.Values.Where(h => h.IsOverflowed).Select(h => h.WidgetId).Order().ToList();
        over.AddRange(dock.AppButtons.Where(a => a.Visibility != Visibility.Visible).Select(_ => "app"));
        return over.Count == 0 ? "none" : string.Join(",", over);
    }

    private static void InjectQuotaMonitor(AppSettings settings, QuotaMonitor monitor)
    {
        typeof(QuotaService).GetField("_monitor", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, monitor);
        typeof(QuotaService).GetField("_builtFor", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
        Assert.Same(monitor, QuotaService.For(settings));
    }

    /// <summary>
    /// What SizeToContent.Width does for this borderless window: measure the window's own template root at unbounded
    /// width and the window's Height, and take that as the window's outer size. (A Window that has never had a native
    /// source measures as 0x0, and giving the dock one would register its AppBar; the live candidate check measures the
    /// real window with GetWindowRect.) Returns the outer size in DIPs.
    /// </summary>
    private static Size Measure(DockWindow dock)
    {
        dock.ApplyTemplate();
        var root = (UIElement)VisualTreeHelper.GetChild(dock, 0);
        root.Measure(new Size(double.PositiveInfinity, dock.Height));
        root.Arrange(new Rect(0, 0, root.DesiredSize.Width, dock.Height));
        root.UpdateLayout();
        return root.RenderSize;
    }

    private static (int Left, int Width) Bounds(FrameworkElement e, Visual root, double dpi)
    {
        var r = e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));
        return (Px(r.Left, dpi), Px(r.Right, dpi) - Px(r.Left, dpi));
    }

    private static int Px(double dip, double dpi) => (int)Math.Round(dip * dpi, MidpointRounding.AwayFromZero);

    private static double NaturalWidth(TextBlock t)
    {
        var copy = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight };
        copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return copy.DesiredSize.Width;
    }

    private static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Private)!.GetValue(o)!;

    private static void Pump()
    {
        for (int i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
    }

    private static void RunWhilePumping(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "fixture refresh did not finish");
    }

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "STA dock geometry test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
