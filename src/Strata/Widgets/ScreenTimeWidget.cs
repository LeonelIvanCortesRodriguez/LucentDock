using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Strata.Core;

namespace Strata.Widgets;

public sealed class ScreenTimeWidget : Widget
{
    private readonly TextBlock _value;

    public ScreenTimeWidget(IDockHost host) : base(host)
    {
        _value = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        Child = UI.Row(6, UI.Icon("\uE81C", Settings.FontSize + 2), _value);
        Announce("Screen time");
    }

    public override void Tick()
    {
        if (!Settings.TrackScreenTime) { _value.Text = "Off"; ToolTip = "Screen time tracking is off (Settings)"; return; }
        int total = Host.ScreenTime.Today.TotalSeconds;
        _value.Text = ScreenTimeTracker.Format(total);
        ToolTip = $"Screen time today: {ScreenTimeTracker.Format(total)}";
        Announce("Screen time", $"{ScreenTimeTracker.Format(total)} today");
    }

    protected override double FlyoutWidth => 360;

    protected override FrameworkElement BuildFlyout()
    {
        var day = Smart.AppClock.Today;
        var title = UI.Header("");
        var total = UI.Text("", 30, flyout: true, weight: FontWeights.SemiBold);
        total.FontFamily = Theme.DisplayFont;
        var chart = new Grid { Height = 70, Margin = new Thickness(0, 6, 0, 2) };
        var axis = new Grid();
        var apps = new StackPanel();
        Controls.FlatButton next = null!;
        var prev = UI.IconButton("\uE76B", () => { day = day.AddDays(-1); Render(); }, 11, "Previous day");
        next = UI.IconButton("\uE76C", () => { if (day < Smart.AppClock.Today) { day = day.AddDays(1); Render(); } }, 11, "Next day");
        var header = new DockPanel();
        var nav = UI.Row(2, prev, next);
        DockPanel.SetDock(nav, Dock.Right);
        header.Children.Add(nav);
        header.Children.Add(title);

        void Render()
        {
            var usage = day == Smart.AppClock.Today ? Host.ScreenTime.Today : ScreenTimeTracker.LoadDay(day);
            title.Text = day == Smart.AppClock.Today ? "Screen time today" : day.ToString("dddd, d MMM");
            total.Text = ScreenTimeTracker.Format(usage.TotalSeconds);

            // Hour-by-hour bars
            chart.Children.Clear();
            chart.ColumnDefinitions.Clear();
            int peak = Math.Max(60, usage.Hours.Max());
            for (int h = 0; h < 24; h++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition());
                var bar = new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    CornerRadius = new CornerRadius(2),
                    Height = Math.Max(2, 70.0 * usage.Hours[h] / peak),
                    ToolTip = $"{h:00}:00 - {ScreenTimeTracker.Format(usage.Hours[h])}",
                    Background = Brushes.Transparent,
                };
                bar.SetResourceReference(Border.BackgroundProperty, usage.Hours[h] > 0 ? "Accent" : "InputBg");
                Grid.SetColumn(bar, h);
                chart.Children.Add(bar);
            }
            axis.Children.Clear();
            foreach (var (label, align) in new[] { ("12 AM", HorizontalAlignment.Left), ("12 PM", HorizontalAlignment.Center), ("11 PM", HorizontalAlignment.Right) })
            {
                var t = UI.Text(label, 10.5, subtle: true, flyout: true);
                t.HorizontalAlignment = align;
                axis.Children.Add(t);
            }

            // Top apps
            apps.Children.Clear();
            var top = usage.Apps.Values.OrderByDescending(a => a.Seconds).Take(8).ToList();
            if (top.Count == 0)
                apps.Children.Add(UI.Text(Settings.TrackScreenTime ? "No activity recorded yet." : "Tracking is off. Turn it on in Settings.", 12.5, subtle: true, flyout: true));
            int max = top.Count > 0 ? top[0].Seconds : 1;
            foreach (var a in top)
            {
                var identity = new AppIdentity(a.Aumid, a.ExePath, a.Name);
                var icon = new Image { Width = 22, Height = 22, Source = IconCache.For(identity), VerticalAlignment = VerticalAlignment.Center };
                var name = UI.Text(a.Name, 12.5, flyout: true);
                var time = UI.Text(ScreenTimeTracker.Format(a.Seconds), 12.5, subtle: true, flyout: true);
                var bar = UI.Bar((double)a.Seconds / max, 5);
                bar.Margin = new Thickness(0, 4, 0, 0);
                var labels = new DockPanel();
                DockPanel.SetDock(time, Dock.Right);
                labels.Children.Add(time);
                labels.Children.Add(name);
                var col = UI.Column(0, labels, bar);
                col.Margin = new Thickness(10, 0, 0, 0);
                var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(col);
                apps.Children.Add(row);
            }
            next.Opacity = day < Smart.AppClock.Today ? 1 : 0.35;
        }

        Render();
        var note = UI.Text("Counted while you're active. Kept only on this PC.", 11, subtle: true, flyout: true);
        note.Margin = new Thickness(0, 6, 0, 0);
        var scroller = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = apps };
        return UI.Column(4, header, total, chart, axis, UI.Separator(), scroller, note);
    }
}
