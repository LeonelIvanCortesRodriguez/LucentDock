using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Strata.Core;
using Strata.Quota;

namespace Strata.Widgets;

/// <summary>
/// AI usage: Claude and Codex subscription quotas from each provider's own interface (see <see cref="QuotaMonitor"/>).
/// The tile shows one provider's one window, always saying "used" or "remaining"; the flyout shows every window.
/// Token or cost estimates are a different thing and are never shown here as quota.
/// </summary>
public sealed class QuotaWidget : Widget
{
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xF2, 0xB8, 0x4B));
    private static readonly Brush CriticalBrush = Frozen(Color.FromRgb(0xF2, 0x6D, 0x6D));

    private readonly QuotaMonitor _monitor;
    private readonly TextBlock _provider, _value, _countdown;
    private readonly Ellipse _dot;
    private int _rotation, _tick;

    private QuotaSettings Q => Settings.Quota;

    public QuotaWidget(IDockHost host) : this(host, QuotaService.For(host.Settings)) { }

    internal QuotaWidget(IDockHost host, QuotaMonitor monitor) : base(host)
    {
        _monitor = monitor;
        _provider = UI.Text("", Settings.FontSize - 1, subtle: true, weight: FontWeights.SemiBold);
        _value = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        _countdown = UI.Text("", Settings.FontSize - 1, subtle: true);
        _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        Child = BuildStableTile();
        _monitor.Changed += OnChanged;
        _monitor.Tick();
        Render();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { Render(); Alert(); RecordHistory(); });

    private QuotaHistoryStore? _history;

    private Grid BuildStableTile()
    {
        double valueWidth = Settings.Size switch { DockSize.Small => 80, DockSize.Large => 104, _ => 92 };
        double resetWidth = Settings.Size switch { DockSize.Small => 42, DockSize.Large => 60, _ => 52 };
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(resetWidth) });
        _dot.Margin = new Thickness(0, 0, 6, 0);
        _provider.Margin = new Thickness(0, 0, 5, 0);
        _provider.TextTrimming = TextTrimming.CharacterEllipsis;
        _value.TextAlignment = TextAlignment.Center;
        _value.TextTrimming = TextTrimming.CharacterEllipsis;
        _countdown.TextAlignment = TextAlignment.Right;
        _countdown.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_provider, 1);
        Grid.SetColumn(_value, 2);
        Grid.SetColumn(_countdown, 3);
        grid.Children.Add(_dot);
        grid.Children.Add(_provider);
        grid.Children.Add(_value);
        grid.Children.Add(_countdown);
        return grid;
    }

    /// <summary>Keep this reading's figures for the trend and the forecast (<see cref="QuotaForecast"/>).</summary>
    private void RecordHistory()
    {
        if (PrivacyGuard.PreviewActive) return;
        _history ??= Storage.Load<QuotaHistoryStore>(QuotaHistoryStore.FileName);
        var now = Now;
        bool changed = QuotaForecast.Record(_history, _monitor.Claude, now) | QuotaForecast.Record(_history, _monitor.Codex, now);
        QuotaForecast.Prune(_history, now);
        if (changed) Storage.Save(QuotaHistoryStore.FileName, _history);
    }

    private QuotaAlertLog? _alertLog;

    /// <summary>Notify once per limit and reset period (see <see cref="QuotaAlerts"/>); quiet hours apply.</summary>
    private void Alert()
    {
        if (!Q.Alerts || PrivacyGuard.PreviewActive) return;
        _alertLog ??= Storage.Load<QuotaAlertLog>(QuotaAlertLog.FileName);
        var now = Now;
        var alerts = new[] { _monitor.Claude, _monitor.Codex }
            .SelectMany(st => QuotaAlerts.Evaluate(st, now, Q.WarnRemainingPercent, _alertLog, Settings.Use24HourClock)).ToList();
        QuotaAlerts.Prune(_alertLog, now);
        foreach (var a in alerts) Host.Notify(a.Key, a.Title, a.Text);
        if (alerts.Count > 0) Storage.Save(QuotaAlertLog.FileName, _alertLog);
    }

    public override void Tick()
    {
        _monitor.Tick();
        // Rotation (and the countdown) advance every 8 seconds.
        if (++_tick % 8 == 0) { _rotation++; Render(); }
    }

    private DateTimeOffset Now => new(Smart.AppClock.Now);

    private void Render()
    {
        var now = Now;
        var items = QuotaSelector.TileItems(Q.TileMode, _monitor.Claude, _monitor.Codex, Q.ClaudeWindow, Q.CodexWindow, now);
        var item = items[_rotation % items.Count];
        string name = QuotaFormat.ProviderName(item.Provider);
        string window = item.Window?.Label ?? "";
        string value = TileValue(item, now, compact: true);
        string accessibleValue = TileValue(item, now, compact: false);
        string? left = item.Window is { } w && QuotaFormat.Phase(w, now) == WindowPhase.Current && !QuotaFormat.ClockSkewSuspected(item.State.Snapshot!, w, now)
            ? QuotaFormat.Countdown(w.ResetsAt, now) : null;
        var severity = item.Window is { } sw ? QuotaFormat.Severity(sw, now, Q.WarnRemainingPercent, Q.CriticalRemainingPercent) : QuotaSeverity.Normal;
        if (item.State.Status == QuotaStatus.Stale && severity == QuotaSeverity.Normal) value += " (stale)";

        _provider.Text = (window.Length > 0 ? $"{name} · {window}" : name) + (item.State.Snapshot?.IsSample == true ? " · SAMPLE" : "");
        _value.Text = value;
        _countdown.Text = left ?? "";
        _countdown.Visibility = left is null ? Visibility.Collapsed : Visibility.Visible;
        _dot.Fill = severity == QuotaSeverity.Critical ? CriticalBrush : WarnBrush;
        _dot.Visibility = severity == QuotaSeverity.Normal ? Visibility.Collapsed : Visibility.Visible;
        string sample = item.State.Snapshot?.IsSample == true ? " (sample data)" : "";
        string shown = $"{name}{(window.Length > 0 ? $" {window}" : "")}: {accessibleValue}{(left is null ? "" : $", resets in {left}")}{sample}";
        // Hovering shows every limit of both providers; the tile itself shows one.
        ToolTip = HoverSummary([_monitor.Claude, _monitor.Codex], now, Settings.Use24HourClock);
        Announce("AI usage", $"{shown}{(severity == QuotaSeverity.Normal ? "" : $", {severity.ToString().ToLowerInvariant()}")}");
    }

    private string TileValue(TileItem item, DateTimeOffset now, bool compact) => item.State.Status switch
    {
        QuotaStatus.Disabled => "Off",
        QuotaStatus.NotConnected => compact ? "Signed out" : "Not connected",
        QuotaStatus.WaitingForFirstSample => compact ? "Loading…" : QuotaFormat.WaitingForFirstSample,
        QuotaStatus.Error when item.Window is null => item.State.Error == QuotaErrorKind.SignInRequired
            ? (compact ? "Sign in" : "Sign in needed") : "Unavailable",
        _ when item.State.Snapshot is null => compact ? "Loading…" : QuotaFormat.WaitingForFirstSample,
        _ when item.Window is null => QuotaFormat.Unavailable,
        _ => QuotaFormat.Value(item.Window, now, Q.TileShowsRemaining),
    };

    /// <summary>
    /// The tile's hover text: for each provider, its plan and every limit with "used" and when it resets (or its
    /// state when there are no figures), and how fresh the reading is. Never blended across providers.
    /// </summary>
    public static string HoverSummary(IEnumerable<ProviderState> states, DateTimeOffset now, bool use24Hour)
    {
        var lines = new List<string>();
        foreach (var st in states)
        {
            if (st.Status == QuotaStatus.Disabled) continue;
            var s = st.Snapshot;
            lines.Add(QuotaFormat.ProviderName(st.Provider) + (s?.Plan is { } plan ? $" · {plan}" : "") + (s?.IsSample == true ? " (sample data)" : ""));
            string? state = st.Status switch
            {
                QuotaStatus.NotConnected => "Not connected",
                QuotaStatus.WaitingForFirstSample => QuotaFormat.WaitingForFirstSample,
                QuotaStatus.Error when s is null => st.Error == QuotaErrorKind.SignInRequired ? "Sign in needed" : QuotaFormat.Unavailable,
                _ when s is null => QuotaFormat.WaitingForFirstSample,
                _ when s.Windows.Count == 0 => QuotaFormat.Unavailable + ": no limits reported",
                _ => null,
            };
            if (state is not null) { lines.Add("  " + state); continue; }
            foreach (var w in s!.Windows)
            {
                var phase = QuotaFormat.Phase(w, now);
                string figure = phase != WindowPhase.Current ? QuotaFormat.Value(w, now, remaining: false)
                    : w.Reached ? "limit reached" : QuotaFormat.Used(w.UsedPercent!.Value);
                string? left = phase == WindowPhase.Current && !QuotaFormat.ClockSkewSuspected(s, w, now) ? QuotaFormat.Countdown(w.ResetsAt, now) : null;
                string? at = QuotaFormat.ResetLocal(w.ResetsAt, now, use24Hour);
                string resets = left is not null ? $" · resets in {left}" : at is not null && phase == WindowPhase.Current ? $" · resets {at}" : "";
                lines.Add($"  {w.Label}: {figure}{resets}");
            }
            lines.Add($"  {(st.Status == QuotaStatus.Stale ? "Stale: updated" : "Updated")} {QuotaFormat.Age(s.ObservedAt, now)}");
        }
        if (lines.Count == 0) return "AI usage is off";
        lines.Add("Click for details");
        return string.Join("\n", lines);
    }

    protected override double FlyoutWidth => 380;

    protected override FrameworkElement? BuildFlyout()
    {
        var now = Now;
        var refresh = UI.IconButton("", () => { _ = Refresh(); }, 13, "Refresh AI usage");
        refresh.IsEnabled = !_monitor.Refreshing;
        var header = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(UI.Header("AI usage"));
        var col = UI.Column(8, header);
        if (_monitor.Claude.Snapshot?.IsSample == true || _monitor.Codex.Snapshot?.IsSample == true)
            col.Children.Add(Wrap(UI.Text("SAMPLE DATA: illustrative values, not your accounts.", 11.5, flyout: true, weight: FontWeights.SemiBold)));
        foreach (var st in new[] { _monitor.Claude, _monitor.Codex })
        {
            if (st.Status == QuotaStatus.Disabled) continue;
            col.Children.Add(UI.Separator());
            col.Children.Add(ProviderSection(st, now));
        }
        var note = Wrap(UI.Text("Each provider's own figures. Percentages from different providers or windows are never combined.", 11.5, subtle: true, flyout: true));
        col.Children.Add(note);
        var settings = UI.TextButton("AI usage settings", Host.OpenSettings);
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        col.Children.Add(settings);
        return col;
    }

    private async Task Refresh()
    {
        await _monitor.RefreshAsync();
        Render();
    }

    private UIElement ProviderSection(ProviderState st, DateTimeOffset now)
    {
        var s = st.Snapshot;
        string title = QuotaFormat.ProviderName(st.Provider) + (s?.Plan is { } plan ? $" · {plan}" : "");
        var col = UI.Column(6, UI.Text(title, 13.5, flyout: true, weight: FontWeights.SemiBold));
        string status = st.Status switch
        {
            QuotaStatus.NotConnected => "Not connected.",
            QuotaStatus.WaitingForFirstSample => "Waiting for first sample.",
            QuotaStatus.Error => st.ErrorText ?? "Unavailable.",
            _ => $"{(st.Status == QuotaStatus.Stale ? "Stale: last updated" : "Updated")} {QuotaFormat.Age(s!.ObservedAt, now)} · {s.Source}"
                 + (st.Status == QuotaStatus.Stale && st.ErrorText is { } e ? $". {e}" : ""),
        };
        col.Children.Add(Wrap(UI.Text(status, 12, subtle: st.Status is QuotaStatus.Fresh, flyout: true)));
        // Signing in needs the user and a browser, so this is the one thing that opens a visible window.
        if (st.Provider == QuotaProvider.Claude && _monitor.ClaudeNeedsSignIn && s?.IsSample != true)
        {
            var signIn = UI.TextButton("Sign in to Claude…", () =>
            {
                if (!ClaudeUsageSource.StartSignIn(ClaudeUsageSource.Locate(Q.ClaudePath))) Host.OpenSettings();
                Host.CloseFlyout();
            }, accent: true);
            signIn.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(signIn);
        }
        if (s is null) return col;
        if (s.Windows.Count == 0) col.Children.Add(UI.Text(QuotaFormat.Unavailable + ": no quota windows reported.", 12, subtle: true, flyout: true));
        foreach (var w in s.Windows) col.Children.Add(WindowRow(st.Provider, s, w, now));
        if (s.Blocked == true) col.Children.Add(Wrap(UI.Text($"Blocked: {s.BlockedReason ?? "limit reached"}", 12, flyout: true, weight: FontWeights.SemiBold)));
        if (s.Credits is { } c)
        {
            string credits = c.Unit == "extra usage" ? $"Extra usage: {(c.HasCredits == true ? "on" : "off")}"
                : c.Unlimited == true ? "Credits: unlimited"
                : c.Balance is { } b ? $"Credits: {b} {c.Unit}{(c.Total is { } t ? $" of {t}" : "")}{(c.Period is { } p ? $" ({p})" : "")}"
                : c.HasCredits == false ? "Credits: none" : "Credits: " + QuotaFormat.Unavailable;
            col.Children.Add(UI.Text(credits, 12, subtle: true, flyout: true));
        }
        if (s.ResetCredits is { } rc)
            col.Children.Add(UI.Text($"Reset credits available: {rc.AvailableCount}"
                + (rc.NextExpiry is { } x ? $", next expires {QuotaFormat.ResetLocal(x, now, Settings.Use24HourClock)}" : ""), 12, subtle: true, flyout: true));
        return col;
    }

    private UIElement WindowRow(QuotaProvider provider, QuotaSnapshot s, QuotaWindow w, DateTimeOffset now)
    {
        var phase = QuotaFormat.Phase(w, now);
        string figures = phase == WindowPhase.Current
            ? $"{QuotaFormat.Used(w.UsedPercent!.Value)} · {QuotaFormat.Remaining(w.RemainingPercent!.Value)}"
            : QuotaFormat.Value(w, now, remaining: true);
        var top = new DockPanel();
        var fig = UI.Text(figures, 12, flyout: true);
        DockPanel.SetDock(fig, Dock.Right);
        top.Children.Add(fig);
        top.Children.Add(UI.Text(w.Label, 12.5, flyout: true, weight: FontWeights.SemiBold));
        var row = UI.Column(4, top);
        if (phase == WindowPhase.Current) row.Children.Add(UI.Bar(Math.Clamp(w.UsedPercent!.Value / 100, 0, 1)));
        string? at = QuotaFormat.ResetLocal(w.ResetsAt, now, Settings.Use24HourClock);
        if (at is not null)
        {
            bool skew = QuotaFormat.ClockSkewSuspected(s, w, now);
            string? left = skew || phase != WindowPhase.Current ? null : QuotaFormat.Countdown(w.ResetsAt, now);
            string resets = phase == WindowPhase.ResetPassed ? $"Reset {at}" : left is null ? $"Resets {at}" : $"Resets in {left} · {at}";
            if (skew) resets += " (this PC's clock may be off)";
            row.Children.Add(UI.Text(resets, 11.5, subtle: true, flyout: true));
        }
        if (w.Reached && phase == WindowPhase.Current) row.Children.Add(UI.Text("Limit reached", 11.5, flyout: true, weight: FontWeights.SemiBold));
        // The trend this period, and where it is heading.
        if (!s.IsSample && phase == WindowPhase.Current && !w.Reached && _history?.Windows.GetValueOrDefault(QuotaForecast.Key(provider, w)) is { } samples)
        {
            if (samples.Count >= 3)
            {
                var trend = new Strata.Controls.Sparkline { Height = 22, Capacity = samples.Count, StrokeThickness = 1.5 };
                trend.SetValues(samples.Select(x => x.Used / 100));
                System.Windows.Automation.AutomationProperties.SetName(trend, $"{w.Label} usage this period");
                row.Children.Add(trend);
            }
            if (QuotaForecast.Project(samples, w, now) is { } projection)
            {
                var forecast = Wrap(UI.Text(QuotaForecast.Describe(projection, now, Settings.Use24HourClock), 11.5, flyout: true,
                    subtle: projection.ReachedAt is null));
                if (projection.ReachedAt is not null) forecast.Foreground = WarnBrush;
                row.Children.Add(forecast);
            }
        }
        if (w.Detail is { } d) row.Children.Add(UI.Text(d, 11.5, subtle: true, flyout: true));
        var severity = QuotaFormat.Severity(w, now, Q.WarnRemainingPercent, Q.CriticalRemainingPercent);
        if (severity != QuotaSeverity.Normal)
            fig.Foreground = severity == QuotaSeverity.Critical ? CriticalBrush : WarnBrush;
        System.Windows.Automation.AutomationProperties.SetName(row, $"{w.Label}: {figures}{(at is null ? "" : $", resets {at}")}");
        row.Focusable = false;
        return row;
    }

    private static TextBlock Wrap(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t; }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public override void Dispose() => _monitor.Changed -= OnChanged;
}
