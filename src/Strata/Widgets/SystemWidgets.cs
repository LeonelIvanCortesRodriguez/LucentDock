using System.Windows;
using System.Windows.Controls;
using Strata.Controls;
using Strata.Native;
using Strata.Smart;

namespace Strata.Widgets;

public static class Live
{
    /// <summary>Update now, then on every shared heartbeat while the element is on screen (open flyouts).</summary>
    public static T Updating<T>(this T element, Action update) where T : FrameworkElement
    {
        update(); // fill in immediately so the first frame is never blank
        void OnTick() => update();
        element.Loaded += (_, _) => { Heartbeat.Tick -= OnTick; Heartbeat.Tick += OnTick; };
        element.Unloaded += (_, _) => Heartbeat.Tick -= OnTick;
        return element;
    }
}

public sealed class CpuWidget : Widget
{
    private readonly TextBlock _value;
    private readonly Sparkline _spark = new() { Width = 34, Height = 14, Capacity = 30 };

    public CpuWidget(IDockHost host) : base(host)
    {
        _value = UI.Text("–", Settings.FontSize, weight: FontWeights.SemiBold);
        _value.MinWidth = 30;
        Child = UI.Row(6, UI.Text("CPU", Settings.FontSize - 1.5, subtle: true), _value, _spark);
        ToolTip = "Processor usage";
        _spark.SetValues(SystemStats.CpuHistory); // full chart immediately after a dock rebuild
        Announce("CPU");
    }

    public override void Tick()
    {
        if (!Signals.TryGetFresh(Smart.Signals.Cpu, out double cpu)) { _value.Text = "–"; return; }
        _value.Text = $"{cpu * 100:0}%";
        _spark.Push(cpu);
        Announce("CPU", $"{cpu * 100:0} percent");
    }

    protected override FrameworkElement BuildFlyout()
    {
        var big = UI.Text("", 30, flyout: true, weight: FontWeights.SemiBold);
        var chart = new Sparkline { Height = 70, Capacity = SystemStats.HistoryLength, StrokeThickness = 2 };
        chart.SetValues(SystemStats.CpuHistory);
        var uptime = UI.Text("", 12.5, subtle: true, flyout: true);
        var panel = UI.Column(8,
            UI.Header("Processor"),
            big,
            UI.Card(chart, new Thickness(8)),
            UI.Text($"{Environment.ProcessorCount} logical processors", 12.5, subtle: true, flyout: true),
            uptime);
        return panel.Updating(() =>
        {
            if (Signals.TryGetFresh(Smart.Signals.Cpu, out double cpu)) big.Text = $"{cpu * 100:0}%";
            chart.SetValues(SystemStats.CpuHistory);
            var up = SystemStats.Uptime;
            uptime.Text = $"Up for {(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
        });
    }
}

public sealed class RamWidget : Widget
{
    private readonly TextBlock _value;
    private readonly Sparkline _spark = new() { Width = 34, Height = 14, Capacity = 30 };

    public RamWidget(IDockHost host) : base(host)
    {
        _value = UI.Text("–", Settings.FontSize, weight: FontWeights.SemiBold);
        _value.MinWidth = 30;
        Child = UI.Row(6, UI.Text("RAM", Settings.FontSize - 1.5, subtle: true), _value, _spark);
        ToolTip = "Memory in use";
        _spark.SetValues(SystemStats.MemoryHistory);
        Announce("Memory");
    }

    public override void Tick()
    {
        if (!Signals.TryGetFresh(Smart.Signals.Memory, out double mem)) { _value.Text = "–"; return; }
        _value.Text = $"{mem * 100:0}%";
        _spark.Push(mem);
        Announce("Memory", $"{mem * 100:0} percent in use");
    }

    protected override FrameworkElement BuildFlyout()
    {
        var big = UI.Text("", 30, flyout: true, weight: FontWeights.SemiBold);
        var detail = UI.Text("", 12.5, subtle: true, flyout: true);
        var chart = new Sparkline { Height = 70, Capacity = SystemStats.HistoryLength, StrokeThickness = 2 };
        var panel = UI.Column(8, UI.Header("Memory"), big, UI.Card(chart, new Thickness(8)), detail);
        return panel.Updating(() =>
        {
            if (Signals.TryGetFresh(Smart.Signals.Memory, out double mem)) big.Text = $"{mem * 100:0}%";
            detail.Text = $"{SystemStats.FormatBytes(SystemStats.MemoryUsed)} of {SystemStats.FormatBytes(SystemStats.MemoryTotal)} in use";
            chart.SetValues(SystemStats.MemoryHistory);
        });
    }
}

public sealed class NetworkWidget : Widget
{
    private readonly TextBlock _down, _up;

    public NetworkWidget(IDockHost host) : base(host)
    {
        _down = UI.Text("", Settings.FontSize - 1, weight: FontWeights.SemiBold);
        _up = UI.Text("", Settings.FontSize - 1, subtle: true);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70 };
        col.Children.Add(_down);
        col.Children.Add(_up);
        Child = col;
        Announce("Network");
    }

    public static string LevelText(NetworkLevel level) => level switch
    {
        NetworkLevel.Internet => "Online",
        NetworkLevel.Constrained => "Limited internet",
        NetworkLevel.LocalOnly => "No internet",
        NetworkLevel.None => "Offline",
        _ => "Checking…",
    };

    public override void Tick()
    {
        var n = Signals.ValueOrDefault(Smart.Signals.Network);
        if (n is { HasInternet: false, Level: not NetworkLevel.Unknown })
        {
            _down.Text = LevelText(n.Level);
            _up.Text = "";
        }
        else
        {
            _down.Text = "↓ " + SystemStats.FormatRate(n?.DownBytesPerSec ?? 0);
            _up.Text = "↑ " + SystemStats.FormatRate(n?.UpBytesPerSec ?? 0);
        }
        ToolTip = n is null ? "Network" : $"{LevelText(n.Level)} · {_down.Text} {_up.Text}";
        Announce("Network", n is null ? null : LevelText(n.Level));
    }

    protected override FrameworkElement BuildFlyout()
    {
        double Scale() => Math.Max(64 * 1024, SystemStats.DownHistory.Concat(SystemStats.UpHistory).DefaultIfEmpty(0).Max());
        var down = new Sparkline { Height = 60, Capacity = SystemStats.HistoryLength, StrokeThickness = 2 };
        var up = new Sparkline { Height = 40, Capacity = SystemStats.HistoryLength, StrokeThickness = 1.5, Filled = false, Opacity = 0.7 };
        var status = UI.Text("", 13, flyout: true, weight: FontWeights.SemiBold);
        var downText = UI.Text("", 13, flyout: true);
        var upText = UI.Text("", 13, subtle: true, flyout: true);
        var chart = new Grid();
        chart.Children.Add(down);
        chart.Children.Add(up);
        var panel = UI.Column(8, UI.Header("Network"), status, UI.Row(16, downText, upText), UI.Card(chart, new Thickness(8)));
        return panel.Updating(() =>
        {
            // Rescale the whole history each second so the chart always fits the busiest moment.
            double s = Scale();
            down.SetValues(SystemStats.DownHistory.Select(v => v / s));
            up.SetValues(SystemStats.UpHistory.Select(v => v / s));
            var n = Signals.ValueOrDefault(Smart.Signals.Network);
            status.Text = n is null ? "Checking…" : LevelText(n.Level);
            downText.Text = "↓ " + SystemStats.FormatRate(n?.DownBytesPerSec ?? 0);
            upText.Text = "↑ " + SystemStats.FormatRate(n?.UpBytesPerSec ?? 0);
        });
    }
}

public sealed class BatteryWidget : Widget
{
    private readonly TextBlock _icon, _value;

    public BatteryWidget(IDockHost host) : base(host)
    {
        _icon = UI.Icon("", Settings.FontSize + 3);
        _value = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        Child = UI.Row(5, _icon, _value);
        Announce("Battery");
    }

    public override void Tick()
    {
        var b = Signals.ValueOrDefault(Smart.Signals.Battery);
        if (b is null || !b.HasBattery)
        {
            Visibility = Visibility.Collapsed; // desktops: nothing to show
            return;
        }
        Visibility = Visibility.Visible;
        int step = Math.Clamp((int)Math.Round(b.Percent / 10.0), 0, 10);
        _icon.Text = ((char)((b.Charging ? 0xEBAB : 0xEBA0) + step)).ToString();
        _value.Text = $"{b.Percent}%";
        ToolTip = b.Charging ? $"{b.Percent}% - charging" : $"{b.Percent}% on battery";
        Announce("Battery", b.Charging ? $"{b.Percent} percent, charging" : $"{b.Percent} percent");
    }
}

public sealed class ClockWidget : Widget
{
    private readonly TextBlock _time, _date;
    private readonly System.Windows.Shapes.Ellipse _unread;
    /// <summary>When the user last opened the clock flyout (shared by every dock's clock).</summary>
    private static DateTime _seenAt = DateTime.MinValue;

    public ClockWidget(IDockHost host) : base(host)
    {
        _time = UI.Text("", Settings.FontSize + 0.5, weight: FontWeights.SemiBold);
        _date = UI.Text("", Settings.FontSize - 1.5, subtle: true);
        _time.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Right;
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(_time);
        if (Settings.Size != Core.DockSize.Small) col.Children.Add(_date);
        // A dot when notifications arrived since the flyout was last opened.
        _unread = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _unread.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        Child = UI.Row(0, _unread, col);
    }

    /// <summary>How many notifications arrived after <paramref name="seenAt"/>.</summary>
    public static int Unread(IReadOnlyList<NotificationRecord> history, DateTime seenAt) => history.Count(r => r.At > seenAt);

    public override void Tick()
    {
        var now = AppClock.Now;
        _time.Text = now.ToString(Settings.Use24HourClock ? "HH:mm" : "h:mm tt");
        _date.Text = now.ToString("ddd d MMM");
        int unread = NotificationCenter.Current is { } c ? Unread(c.History(), _seenAt) : 0;
        _unread.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = now.ToString("dddd, d MMMM yyyy") + (unread > 0 ? $"\n{unread} new notification{(unread == 1 ? "" : "s")}" : "");
        Announce("Clock", now.ToString("f") + (unread > 0 ? $", {unread} new notification{(unread == 1 ? "" : "s")}" : ""));
    }

    protected override double FlyoutWidth => 330;

    protected override FrameworkElement BuildFlyout()
    {
        _seenAt = AppClock.Now;
        _unread.Visibility = Visibility.Collapsed;
        var col = UI.Column(10, NotificationSection(NotificationCenter.Current, Settings.Use24HourClock, Host), UI.Separator(),
            CalendarWidget.BuildFlyoutContent(Host, showClock: true));
        return col;
    }

    /// <summary>The notification history: what Strata showed (or held during quiet hours) this session, newest first.</summary>
    public static FrameworkElement NotificationSection(NotificationCenter? center, bool use24Hour, IDockHost host)
    {
        var history = center?.History() ?? [];
        var header = new DockPanel();
        if (history.Count > 0)
        {
            var clear = UI.TextButton("Clear", () => { center!.ClearHistory(); host.CloseFlyout(); });
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
        }
        header.Children.Add(UI.Header("Notifications"));
        var col = UI.Column(6, header);
        if (history.Count == 0)
        {
            col.Children.Add(UI.Text("None since Strata started.", 12, subtle: true, flyout: true));
            return col;
        }
        foreach (var r in history.Take(6))
        {
            var title = UI.Text(r.Title, 12.5, flyout: true, weight: FontWeights.SemiBold);
            title.TextWrapping = TextWrapping.Wrap;
            var entry = UI.Column(1, title);
            if (r.Body.Length > 0)
                entry.Children.Add(UI.Text(r.Body, 12, subtle: true, flyout: true).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
            string when = r.At.ToString(use24Hour ? "HH:mm" : "h:mm tt") + (r.Outcome == NotifyOutcome.SuppressedQuietHours ? " · held during quiet hours" : "");
            entry.Children.Add(UI.Text(when, 11, subtle: true, flyout: true));
            System.Windows.Automation.AutomationProperties.SetName(entry, $"{r.Title}. {r.Body} {when}");
            col.Children.Add(entry);
        }
        if (history.Count > 6) col.Children.Add(UI.Text($"and {history.Count - 6} earlier", 11.5, subtle: true, flyout: true));
        return col;
    }
}
