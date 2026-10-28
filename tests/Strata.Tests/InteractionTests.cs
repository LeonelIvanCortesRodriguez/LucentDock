using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using Strata.Core;
using Strata.Native;
using Strata.Quota;
using Strata.Views;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>
/// The upgrades asked for after testing b11c93d live: taskbar-style hover previews and window counts, scrolling
/// and middle-clicking the media pill, and a hover summary of every AI usage limit.
/// </summary>
[Trait("Category", "Unit")]
public class InteractionTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 21, 0, 0);

    // ---- Hover previews: when they show and hide ----

    [Fact]
    public void Previews_show_after_the_pointer_rests_on_an_icon()
    {
        var hover = new HoverIntent<string>();
        hover.Enter("excel", T0);
        Assert.Null(hover.Update(T0.AddMilliseconds(200)));       // passing over an icon shows nothing
        Assert.True(hover.Pending);
        Assert.Equal("excel", hover.Update(T0.AddMilliseconds(500)));
        Assert.False(hover.Pending);
    }

    [Fact]
    public void Previews_stay_while_the_pointer_is_over_them_and_hide_shortly_after_it_leaves()
    {
        var hover = new HoverIntent<string>();
        hover.Enter("excel", T0);
        hover.Update(T0.AddMilliseconds(500));
        hover.Leave("excel", T0.AddMilliseconds(600));
        hover.EnterPreview();                                        // moving up onto the previews
        Assert.Equal("excel", hover.Update(T0.AddSeconds(5)));
        hover.LeavePreview(T0.AddSeconds(6));
        Assert.Equal("excel", hover.Update(T0.AddSeconds(6.1)));    // a short grace period
        Assert.Null(hover.Update(T0.AddSeconds(6.4)));
    }

    [Fact]
    public void Moving_to_another_icon_switches_at_once_and_a_click_hides_until_the_pointer_leaves()
    {
        var hover = new HoverIntent<string>();
        hover.Enter("excel", T0);
        hover.Update(T0.AddMilliseconds(500));
        hover.Leave("excel", T0.AddMilliseconds(700));
        hover.Enter("word", T0.AddMilliseconds(720));
        Assert.Equal("word", hover.Update(T0.AddMilliseconds(730)));
        hover.Dismiss();                                             // clicked the icon
        Assert.Null(hover.Update(T0.AddSeconds(3)));
        hover.Leave("word", T0.AddSeconds(3));
        hover.Enter("word", T0.AddSeconds(4));
        Assert.Equal("word", hover.Update(T0.AddSeconds(4.5)));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(2, "2")]
    [InlineData(9, "9")]
    [InlineData(14, "9+")]
    public void The_icon_shows_how_many_windows_are_open(int windows, string? badge) => Assert.Equal(badge, AppButton.CountBadge(windows));

    [Fact]
    public void The_preview_header_says_how_many_windows()
    {
        Assert.Equal("Excel · 3 windows", WindowPreview.Header("Excel", 3));
        Assert.Equal("Excel", WindowPreview.Header("Excel", 1));
    }

    [Fact]
    public void A_window_picture_keeps_its_shape_and_is_centred()
    {
        var fit = LiveThumbnail.Fit(new Size(1600, 900), new Rect(10, 20, 300, 180));
        Assert.Equal(new Rect(10, 25.625, 300, 168.75), fit);
        var tall = LiveThumbnail.Fit(new Size(400, 800), new Rect(0, 0, 300, 180));
        Assert.Equal(90, tall.Width, 6);
        Assert.Equal(105, tall.Left, 6);
        Assert.True(LiveThumbnail.Fit(Size.Empty, new Rect(0, 0, 300, 180)).IsEmpty);
    }

    /// <summary>DWM accepts a live picture of a real window in the preview, and lets it go again.</summary>
    [Fact]
    public void The_preview_registers_a_live_picture_for_each_open_window() => OnSta(() =>
    {
        var a = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        var b = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        var ha = new WindowInteropHelper(a).EnsureHandle();
        var hb = new WindowInteropHelper(b).EnsureHandle();
        var app = new AppIdentity(null, @"C:\Fixture\TestApp.exe", "Test app");
        TrackedWindow W(IntPtr h, string title) => new() { Handle = h, App = app, Order = 1, Title = title };
        var button = new AppButton(null, app, 26, topEdge: false);
        var preview = new WindowPreview(button, [W(ha, "One"), W(hb, "Two")], below: false, _ => { }, _ => { });
        new WindowInteropHelper(preview).EnsureHandle();               // registers without showing anything
        Assert.Equal(2, preview.LivePictures);
        preview.Close();
        Assert.Equal(0, preview.LivePictures);
        a.Close(); b.Close();
    });

    // ---- Media pill: scroll and middle-click ----

    [Fact]
    public void Scrolling_the_pill_changes_the_volume_by_two_percent_a_notch()
    {
        var at = TimeSpan.FromSeconds(60);
        var length = TimeSpan.FromSeconds(200);
        Assert.Equal((52, (TimeSpan?)null), MediaWidget.Scroll(120, false, 50, true, at, length));
        Assert.Equal((48, (TimeSpan?)null), MediaWidget.Scroll(-120, false, 50, true, at, length));
        Assert.Equal((100, (TimeSpan?)null), MediaWidget.Scroll(120, false, 99, true, at, length));
        Assert.Equal(((int?)null, (TimeSpan?)null), MediaWidget.Scroll(120, false, -1, true, at, length));   // no playback device
    }

    [Fact]
    public void Shift_scroll_skips_ten_seconds_where_the_app_allows_seeking()
    {
        var length = TimeSpan.FromSeconds(200);
        Assert.Equal(((int?)null, (TimeSpan?)TimeSpan.FromSeconds(70)), MediaWidget.Scroll(120, true, 50, true, TimeSpan.FromSeconds(60), length));
        Assert.Equal(((int?)null, (TimeSpan?)TimeSpan.Zero), MediaWidget.Scroll(-120, true, 50, true, TimeSpan.FromSeconds(4), length));
        Assert.Equal(((int?)null, (TimeSpan?)length), MediaWidget.Scroll(120, true, 50, true, TimeSpan.FromSeconds(195), length));
        Assert.Equal(((int?)null, (TimeSpan?)null), MediaWidget.Scroll(120, true, 50, false, TimeSpan.FromSeconds(60), length));
    }

    // ---- AI usage: every limit on hover ----

    [Fact]
    public void Hovering_ai_usage_lists_every_limit_of_both_providers_with_its_reset()
    {
        var now = QuotaFixtures.Now;
        var claude = new ProviderState(QuotaProvider.Claude, QuotaStatus.Fresh, QuotaFixtures.Snapshot(QuotaProvider.Claude, now.AddMinutes(-1),
            QuotaFixtures.Window("Session (5h)", 44, now.AddHours(2).AddMinutes(10)),
            QuotaFixtures.Window("Weekly", 45, now.AddDays(3).AddHours(4), QuotaWindowKind.Weekly, 10080)) with { Plan = "Pro" });
        var codex = new ProviderState(QuotaProvider.Codex, QuotaStatus.Fresh, QuotaFixtures.Snapshot(QuotaProvider.Codex, now,
            QuotaFixtures.Window("Weekly", 100, now.AddDays(5), QuotaWindowKind.Weekly, 10080)) with { Plan = "Plus" });
        var text = QuotaWidget.HoverSummary([claude, codex], now, use24Hour: true);
        var lines = text.Split('\n');
        Assert.Equal("Claude · Pro", lines[0]);
        Assert.StartsWith("  Session (5h): 44% used · resets in 2h 10m", lines[1]);
        Assert.StartsWith("  Weekly: 45% used · resets in 3d 4h", lines[2]);
        Assert.Contains("Codex · Plus", lines);
        Assert.Contains(lines, l => l.StartsWith("  Weekly: limit reached · resets in 5d"));
        Assert.Equal("Click for details", lines[^1]);
    }

    [Fact]
    public void Hovering_ai_usage_says_what_is_missing_instead_of_inventing_figures()
    {
        var now = QuotaFixtures.Now;
        var text = QuotaWidget.HoverSummary([
            new ProviderState(QuotaProvider.Claude, QuotaStatus.Error, null, QuotaErrorKind.SignInRequired, "Sign in"),
            ProviderState.Waiting(QuotaProvider.Codex)], now, use24Hour: true);
        Assert.Contains("  Sign in needed", text.Split('\n'));
        Assert.Contains("  " + QuotaFormat.WaitingForFirstSample, text.Split('\n'));
        Assert.DoesNotContain("%", text);
        Assert.Equal("AI usage is off", QuotaWidget.HoverSummary([new ProviderState(QuotaProvider.Codex, QuotaStatus.Disabled, null)], now, true));
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA interaction test timed out.");
        error?.Throw();
    }
}
