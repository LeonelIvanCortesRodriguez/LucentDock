using System.Security.Cryptography;
using Strata.Core;
using Strata.Quota;
using Strata.Smart;
using Strata.Views;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>
/// The round asked for after 77cd7eb: the AI usage forecast, app shortcuts, scrolling an app's windows,
/// notification history, a dock on every screen, and updates from a release folder.
/// </summary>
[Trait("Category", "Unit")]
public class ForecastShortcutsUpdatesTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    // ---- AI usage forecast ----

    private static QuotaWindow Weekly(double used, DateTimeOffset resets) =>
        QuotaFixtures.Window("weekly", used, resets, QuotaWindowKind.Weekly, 10080) with { Label = "Weekly" };

    private static ProviderState Fresh(double used, DateTimeOffset observed, DateTimeOffset resets, bool sample = false) =>
        new(QuotaProvider.Claude, QuotaStatus.Fresh, QuotaFixtures.Snapshot(QuotaProvider.Claude, observed, Weekly(used, resets)) with { IsSample = sample });

    [Fact]
    public void Readings_are_kept_every_ten_minutes_or_when_they_move_a_point()
    {
        var store = new QuotaHistoryStore();
        var resets = Now.AddDays(3);
        Assert.True(QuotaForecast.Record(store, Fresh(40, Now, resets), Now));
        Assert.False(QuotaForecast.Record(store, Fresh(40.4, Now.AddMinutes(4), resets), Now.AddMinutes(4)));   // too soon, too small
        Assert.True(QuotaForecast.Record(store, Fresh(41.5, Now.AddMinutes(5), resets), Now.AddMinutes(5)));    // moved a point
        Assert.True(QuotaForecast.Record(store, Fresh(41.6, Now.AddMinutes(16), resets), Now.AddMinutes(16))); // ten minutes on
        Assert.False(QuotaForecast.Record(store, Fresh(90, Now.AddMinutes(30), resets, sample: true), Now.AddMinutes(30)));
        Assert.Equal(3, store.Windows.Single().Value.Count);
    }

    [Fact]
    public void A_steady_pace_that_crosses_the_limit_says_when()
    {
        var resets = Now.AddDays(3);
        // 10 points in 5 hours: 2 points an hour; at 60% now, 100% in 20 hours, before the reset.
        var samples = new List<QuotaSample> { new(Now.AddHours(-5), 50), new(Now, 60) };
        var p = QuotaForecast.Project(samples, Weekly(60, resets), Now)!;
        Assert.Equal(Now.AddHours(20), p.ReachedAt);
        Assert.StartsWith("At this pace: limit reached ", QuotaForecast.Describe(p, Now, true));
        Assert.Contains("(in 20h", QuotaForecast.Describe(p, Now, true));
    }

    [Fact]
    public void A_slow_pace_says_where_it_will_be_at_the_reset()
    {
        var resets = Now.AddHours(10);
        var samples = new List<QuotaSample> { new(Now.AddHours(-4), 30), new(Now, 32) };
        var p = QuotaForecast.Project(samples, Weekly(32, resets), Now)!;
        Assert.Null(p.ReachedAt);
        Assert.Equal(37, p.UsedAtReset, 6);
        Assert.Equal("At this pace: about 37% used by the reset", QuotaForecast.Describe(p, Now, true));
    }

    [Fact]
    public void No_forecast_without_half_an_hour_of_readings_or_a_reset_time()
    {
        var resets = Now.AddDays(1);
        Assert.Null(QuotaForecast.Project([new(Now.AddMinutes(-10), 20), new(Now, 25)], Weekly(25, resets), Now));
        Assert.Null(QuotaForecast.Project([new(Now, 25)], Weekly(25, resets), Now));
        Assert.Null(QuotaForecast.Project([new(Now.AddHours(-2), 20), new(Now, 25)], Weekly(25, resets) with { ResetsAt = null }, Now));
    }

    [Fact]
    public void Old_periods_are_dropped_from_the_history()
    {
        var store = new QuotaHistoryStore();
        QuotaForecast.Record(store, Fresh(10, Now, Now.AddDays(1)), Now);
        QuotaForecast.Prune(store, Now.AddDays(5));
        Assert.Single(store.Windows);
        QuotaForecast.Prune(store, Now.AddDays(10));
        Assert.Empty(store.Windows);
    }

    // ---- App shortcuts and scrolling an app ----

    [Fact]
    public void App_shortcuts_are_win_ctrl_alt_and_skip_combinations_other_apps_hold()
    {
        Assert.Equal("Win+Ctrl+Alt+1", AppHotkeys.Label(0));
        Assert.Equal("Win+Ctrl+Alt+9", AppHotkeys.Label(8));
        Assert.Null(AppHotkeys.Label(9));
        Assert.Equal(2, AppHotkeys.IndexFor(AppHotkeys.FirstId + 2));
        Assert.Null(AppHotkeys.IndexFor(AppHotkeys.FirstId + 9));
        var registered = new List<(int Id, uint Vk)>();
        var keys = new AppHotkeys((id, mods, vk) =>
        {
            Assert.Equal(AppHotkeys.Modifiers, mods);
            if (vk == '3') return false;                       // held by another app
            registered.Add((id, vk)); return true;
        }, id => registered.RemoveAll(r => r.Id == id));
        Assert.Equal(8, keys.Register());
        Assert.Equal('1', (char)registered[0].Vk);
        keys.Unregister();
        Assert.Empty(registered);
        Assert.Equal(0, keys.Registered);
    }

    [Theory]
    [InlineData(3, -1, -120, 2)]   // from another app: its most recent window
    [InlineData(3, 0, -120, 1)]    // scroll down: next
    [InlineData(3, 2, -120, 0)]    // wraps round
    [InlineData(3, 0, 120, 2)]     // scroll up: previous, wrapping
    [InlineData(1, 0, -120, 0)]
    [InlineData(0, -1, -120, -1)]
    public void Scrolling_an_app_steps_through_its_windows(int count, int current, int delta, int expected) =>
        Assert.Equal(expected, AppButton.NextWindowIndex(count, current, delta));

    // ---- Notification history ----

    private sealed class NullNotifier : INotifier { public void Show(string title, string body) { } }

    [Fact]
    public void The_history_lists_shown_and_quiet_hours_notifications_newest_first_until_cleared()
    {
        var clock = new ManualClock(new DateTime(2026, 10, 25, 9, 0, 0));
        var quiet = new QuietHours { Enabled = true, Start = new TimeSpan(22, 0, 0), End = new TimeSpan(7, 0, 0) };
        var center = new NotificationCenter(new NullNotifier(), clock, quiet);
        center.Send("a", "First", "one", "test", TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(5));
        center.Send("b", "Second", "two", "test", TimeSpan.FromMinutes(1));
        center.Send("b", "Second again", "dup", "test", TimeSpan.FromMinutes(1));      // duplicate: not shown, not listed
        clock.Advance(TimeSpan.FromHours(14));                                          // 23:05, quiet hours
        center.Send("c", "Third", "held", "test", TimeSpan.FromMinutes(1));
        var history = center.History();
        Assert.Equal(["Third", "Second", "First"], history.Select(r => r.Title));
        Assert.Equal(NotifyOutcome.SuppressedQuietHours, history[0].Outcome);
        Assert.Equal("two", history[1].Body);
        Assert.Equal(2, ClockWidget.Unread(history, new DateTime(2026, 10, 25, 9, 1, 0)));
        center.ClearHistory();
        Assert.Empty(center.History());
        Assert.Null(center.LastShownAt);
    }

    // ---- A dock on every screen ----

    [Fact]
    public void Every_screen_but_the_main_one_gets_a_dock_only_when_asked()
    {
        IntPtr main = 1, second = 2, third = 3;
        Assert.Equal([second, third], App.ScreensNeedingDocks(true, [main, second, third, second], main));
        Assert.Empty(App.ScreensNeedingDocks(false, [main, second], main));
        Assert.Empty(App.ScreensNeedingDocks(true, [main], main));
    }

    [Fact]
    public void Each_dock_lists_the_windows_on_its_own_screen()
    {
        var app = new AppIdentity(null, @"C:\Fixture\app.exe", "App");
        TrackedWindow W(int h) => new() { Handle = h, App = app, Order = h };
        var screenOf = new Dictionary<IntPtr, IntPtr> { [10] = 1, [11] = 2, [12] = 1 };
        var mine = DockWindow.OnScreen([W(10), W(11), W(12)], h => screenOf[h], 1).Select(w => (int)w.Handle);
        Assert.Equal([10, 12], mine);

        // Moving a window changes no HWND/title/process facts, but every-screen mode must still regroup it.
        Assert.True(DockWindow.RequiresWindowRegroup(sourceChanged: false, force: false, preview: false,
            dockOnEveryScreen: true, dockHandleReady: true));
        screenOf[(IntPtr)10] = 2;
        mine = DockWindow.OnScreen([W(10), W(11), W(12)], h => screenOf[h], 1).Select(w => (int)w.Handle);
        Assert.Equal([12], mine);
        Assert.False(DockWindow.RequiresWindowRegroup(false, false, false, false, true));
    }

    // ---- Updates from a release folder ----

    [Theory]
    [InlineData("1.2.1", "1.2.0+abc", 1)]
    [InlineData("1.2.0+def", "1.2.0+abc", 0)]   // same version, another build: not an update
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.2.0", "1.3.0", -1)]
    [InlineData("2", "1.9", 1)]
    public void Versions_compare_by_their_numbers_not_their_build(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(Updater.Compare(a, b)));

    private static string Folder(string version, byte[] exe, string? sha = null, long? size = null, string file = "Strata.exe")
    {
        var dir = Path.Combine(Path.GetTempPath(), "strata-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Strata.exe"), exe);
        File.WriteAllText(Path.Combine(dir, Updater.ManifestName), System.Text.Json.JsonSerializer.Serialize(new
        {
            version, file, sha256 = sha ?? Convert.ToHexString(SHA256.HashData(exe)), size = size ?? exe.Length,
        }));
        return dir;
    }

    [Fact]
    public void A_newer_release_is_offered_and_an_equal_one_is_up_to_date()
    {
        byte[] exe = [1, 2, 3, 4, 5];
        Assert.Equal(UpdateState.Off, Updater.Check("", "1.2.0").State);
        Assert.Equal(UpdateState.Unreachable, Updater.Check(Path.Combine(Path.GetTempPath(), "no-such-strata-folder"), "1.2.0").State);
        var newer = Folder("1.2.1+abc", exe);
        var check = Updater.Check(newer, "1.2.0+def");
        Assert.Equal(UpdateState.Available, check.State);
        Assert.Equal("Strata 1.2.1 is ready to install.", check.Message);
        Assert.Equal(UpdateState.UpToDate, Updater.Check(Folder("1.2.0+abc", exe), "1.2.0+def").State);
        Assert.Equal(UpdateState.Invalid, Updater.Check(Folder("1.2.1", exe, size: 99), "1.2.0").State);
        Assert.Equal(UpdateState.Invalid, Updater.Check(Folder("1.2.1", exe, file: @"..\evil.exe"), "1.2.0").State);
    }

    [Fact]
    public void Only_a_release_matching_its_hash_is_staged_for_install()
    {
        byte[] exe = [9, 8, 7, 6];
        var good = Folder("1.2.1", exe);
        var m = Updater.Check(good, "1.2.0").Manifest!;
        var (staged, _) = Updater.Stage(good, m, runningExe: null, Path.GetTempPath());
        Assert.NotNull(staged);
        Assert.Equal(exe, File.ReadAllBytes(staged!));
        Assert.NotEqual(Path.GetDirectoryName(staged), good);              // a copy outside the release folder

        var tampered = Folder("1.2.1", exe, sha: new string('A', 64));
        var (none, why) = Updater.Stage(tampered, Updater.Check(tampered, "1.2.0").Manifest!, null, Path.GetTempPath());
        Assert.Null(none);
        Assert.Contains("SHA-256", why);
        Assert.Null(Updater.Signer(staged!));                              // unsigned
    }

    [Fact]
    public void Local_manifest_read_is_bounded_and_malformed_input_is_rejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "strata-update-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Strata.exe"), [1]);
            File.WriteAllBytes(Path.Combine(dir, Updater.ManifestName), new byte[Updater.MaxManifestBytes + 1]);
            Assert.Equal(UpdateState.Invalid, Updater.Check(dir, "1.2.0").State);
            File.WriteAllText(Path.Combine(dir, Updater.ManifestName), "{ malformed");
            Assert.Equal(UpdateState.Invalid, Updater.Check(dir, "1.2.0").State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Local_staging_io_failure_is_reported_and_partial_directory_is_removed()
    {
        string root = Path.Combine(Path.GetTempPath(), "strata-stage-root-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(Path.GetTempPath(), "strata-stage-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(source);
        try
        {
            var manifest = new UpdateManifest("1.3.0", "missing.exe", new string('A', 64), 1);
            var (staged, message) = Updater.Stage(source, manifest, null, root);
            Assert.Null(staged);
            Assert.Contains("nothing was installed", message);
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, true); Directory.Delete(source, true); }
    }
}
