using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;

namespace Strata.Widgets;

public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public bool AllDay { get; set; }
    public bool Reminded { get; set; }
}

/// <summary>Local calendar events, stored in %APPDATA%\Strata\events.json.</summary>
public static class EventStore
{
    private const string FileName = "events.json";
    private static List<CalendarEvent>? _events;

    public static List<CalendarEvent> All => _events ??= Storage.Load<List<CalendarEvent>>(FileName);

    public static void Save() => Storage.Save(FileName, All);

    public static IEnumerable<CalendarEvent> On(DateTime day) =>
        All.Where(e => e.Start.Date == day.Date).OrderBy(e => e.AllDay ? DateTime.MinValue : e.Start);

    /// <summary>Raised when events are added or removed, so the calendar signal updates immediately.</summary>
    public static event Action? Changed;

    /// <summary>Replace the event list (preview fixtures).</summary>
    public static void UseFixture(List<CalendarEvent> events) => _events = events;

    /// <summary>The calendar signal: next upcoming timed event, the one in progress, and today's count.
    /// Reminders are not sent here any more: the "Upcoming-event reminder" automation does that.</summary>
    public static Smart.CalendarState BuildState(DateTime now)
    {
        static Smart.CalendarEventInfo Info(CalendarEvent e) => new(e.Id, e.Title, e.Start, e.AllDay);
        var timed = All.Where(e => !e.AllDay).ToList();
        var next = timed.Where(e => e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        var current = timed.Where(e => e.Start <= now && e.Start > now.AddHours(-1)).OrderByDescending(e => e.Start).FirstOrDefault();
        return new Smart.CalendarState(next is null ? null : Info(next), current is null ? null : Info(current), On(now.Date).Count());
    }

    public static void SaveAndNotify()
    {
        // Keep the file small: forget events older than 60 days.
        All.RemoveAll(e => e.Start < Smart.AppClock.Now.AddDays(-60));
        Save();
        Changed?.Invoke();
    }
}

public sealed class CalendarWidget : Widget
{
    private readonly TextBlock _label;

    public CalendarWidget(IDockHost host) : base(host)
    {
        _label = UI.Text("", Settings.FontSize);
        _label.MaxWidth = 170;
        Child = UI.Row(6, UI.Icon("\uE787", Settings.FontSize + 2), _label);
    }

    public override void Tick()
    {
        var state = Signals.ValueOrDefault(Smart.Signals.Calendar);
        var now = Smart.AppClock.Now;
        var next = state?.Next is { } n && n.Start - now <= TimeSpan.FromHours(12) ? n : null;
        if (next is null)
        {
            int today = state?.TodayCount ?? 0;
            _label.Text = today > 0 ? $"{today} today" : now.ToString("MMM d");
            ToolTip = "Calendar";
            Announce("Calendar", today > 0 ? $"{today} events today" : "no upcoming events");
            return;
        }
        var until = next.Start - now;
        string when = until.TotalMinutes < 1 ? "now" : until.TotalMinutes < 60 ? $"in {(int)Math.Ceiling(until.TotalMinutes)} min"
            : next.Start.ToString(Settings.Use24HourClock ? "HH:mm" : "h:mm tt");
        _label.Text = $"{next.Title} · {when}";
        ToolTip = $"{next.Title}\n{next.Start:f}";
        Announce("Calendar", $"next: {next.Title}, {when}");
    }

    protected override double FlyoutWidth => 330;
    protected override FrameworkElement BuildFlyout() => BuildFlyoutContent(Host, showClock: false);

    /// <summary>
    /// Accepts the PC's own time format ("9:30 p. m."), English ("9:30 PM"), 24-hour ("21:30") and bare hours ("9").
    /// </summary>
    public static bool TryParseTime(string input, out TimeSpan time)
    {
        time = default;
        string s = input.Trim();
        if (s.Length == 0) return false;
        if (int.TryParse(s, out int hour) && hour is >= 0 and <= 23) { time = TimeSpan.FromHours(hour); return true; }
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US") })
        {
            if (DateTime.TryParse(s, culture, DateTimeStyles.NoCurrentDateDefault, out var dt) && dt.Date == DateTime.MinValue.Date)
            {
                time = dt.TimeOfDay;
                return true;
            }
        }
        return false;
    }

    /// <summary>Month view plus the selected day's events. Shared with the clock widget.</summary>
    public static FrameworkElement BuildFlyoutContent(IDockHost host, bool showClock)
    {
        var month = new DateTime(Smart.AppClock.Today.Year, Smart.AppClock.Today.Month, 1);
        var selected = Smart.AppClock.Today;
        var root = new StackPanel();

        if (showClock)
        {
            var time = UI.Text("", 34, flyout: true, weight: FontWeights.SemiBold);
            time.FontFamily = Theme.DisplayFont;
            var date = UI.Text(Smart.AppClock.Now.ToString("dddd, d MMMM yyyy"), 13, subtle: true, flyout: true);
            root.Children.Add(time);
            root.Children.Add(date);
            root.Children.Add(UI.Separator().Also(s => s.Margin = new Thickness(0, 10, 0, 8)));
            root.Updating(() => time.Text = Smart.AppClock.Now.ToString(host.Settings.Use24HourClock ? "HH:mm:ss" : "h:mm:ss tt"));
        }

        var monthTitle = UI.Text("", 14, flyout: true, weight: FontWeights.SemiBold);
        var grid = new UniformGrid { Columns = 7 };
        var dayTitle = UI.Text("", 13, flyout: true, weight: FontWeights.SemiBold);
        var list = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var nav = UI.Row(2,
            UI.IconButton("\uE76B", () => { month = month.AddMonths(-1); Render(); }, 11, "Previous month"),
            UI.IconButton("\uE76C", () => { month = month.AddMonths(1); Render(); }, 11, "Next month"));
        DockPanel.SetDock(nav, Dock.Right);
        header.Children.Add(nav);
        header.Children.Add(monthTitle);
        root.Children.Add(header);

        root.Children.Add(grid);
        dayTitle.Margin = new Thickness(0, 10, 0, 6);
        root.Children.Add(dayTitle);
        root.Children.Add(list);

        var titleBox = UI.Input(placeholder: "New event");
        var timeBox = UI.Input(placeholder: Smart.AppClock.Today.AddHours(9.5).ToString(host.Settings.Use24HourClock ? "HH:mm" : "h:mm tt"));
        timeBox.Width = 90;
        var add = UI.TextButton("Add", AddEvent, accent: true);
        var form = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(timeBox, Dock.Right);
        add.Margin = new Thickness(6, 0, 0, 0);
        timeBox.Margin = new Thickness(6, 0, 0, 0);
        form.Children.Add(add);
        form.Children.Add(timeBox);
        form.Children.Add(titleBox);
        root.Children.Add(form);
        var hint = UI.Text("Leave the time empty for an all-day event.", 11.5, subtle: true, flyout: true);
        hint.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(hint);

        titleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddEvent(); };
        timeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddEvent(); };

        void AddEvent()
        {
            string title = titleBox.Text.Trim();
            if (title.Length == 0) return;
            bool allDay = string.IsNullOrWhiteSpace(timeBox.Text);
            DateTime start = selected.Date;
            if (!allDay)
            {
                if (!TryParseTime(timeBox.Text, out var t))
                {
                    timeBox.SetResourceReference(Control.BackgroundProperty, "Pressed");
                    return;
                }
                start = selected.Date + t;
            }
            EventStore.All.Add(new CalendarEvent { Title = title, Start = start, AllDay = allDay });
            EventStore.SaveAndNotify();
            titleBox.Text = timeBox.Text = "";
            timeBox.SetResourceReference(Control.BackgroundProperty, "InputBg");
            Render();
        }

        void Render()
        {
            monthTitle.Text = month.ToString("MMMM yyyy");
            grid.Children.Clear();
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
            {
                var dow = (DayOfWeek)(((int)firstDay + i) % 7);
                var t = UI.Text(CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(dow), 11, subtle: true, flyout: true);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 0, 0, 4);
                grid.Children.Add(t);
            }
            int offset = ((int)month.DayOfWeek - (int)firstDay + 7) % 7;
            var start = month.AddDays(-offset);
            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                grid.Children.Add(DayCell(day));
            }

            dayTitle.Text = selected.Date == Smart.AppClock.Today ? "Today" : selected.ToString("dddd, d MMMM");
            list.Children.Clear();
            var events = EventStore.On(selected).ToList();
            if (events.Count == 0)
                list.Children.Add(UI.Text("No events", 12.5, subtle: true, flyout: true));
            foreach (var ev in events)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var del = UI.IconButton("\uE74D", () => { EventStore.All.Remove(ev); EventStore.SaveAndNotify(); Render(); }, 11, "Delete");
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                string when = ev.AllDay ? "All day" : ev.Start.ToString(host.Settings.Use24HourClock ? "HH:mm" : "h:mm tt");
                var w = UI.Text(when, 12.5, subtle: true, flyout: true);
                w.Width = 64;
                row.Children.Add(UI.Row(6, w, UI.Text(ev.Title, 13, flyout: true)));
                list.Children.Add(row);
            }
        }

        FrameworkElement DayCell(DateTime day)
        {
            bool inMonth = day.Month == month.Month;
            bool isToday = day.Date == Smart.AppClock.Today;
            bool isSelected = day.Date == selected.Date;
            var text = UI.Text(day.Day.ToString(), 12.5, subtle: !inMonth, flyout: true,
                weight: isToday ? FontWeights.Bold : FontWeights.Normal);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            if (isToday) text.Foreground = Brushes.White;
            var dot = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            bool hasEvents = EventStore.On(day).Any();
            if (hasEvents) dot.SetResourceReference(Border.BackgroundProperty, isToday ? "FlyoutFg" : "Accent");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(text);
            stack.Children.Add(dot);
            var cell = new Controls.DockButton { Height = 34, Width = 34, CornerRadius = new CornerRadius(17), Content = stack, Padding = new Thickness(0) };
            if (isToday) cell.SetResourceReference(BackgroundProperty, "Accent");
            else if (isSelected) cell.SetResourceReference(BackgroundProperty, "InputBg");
            cell.Named(day.ToString("D") + (hasEvents ? ", has events" : "") + (isToday ? ", today" : ""));
            cell.Click += (_, _) => { selected = day; if (!inMonth) month = new DateTime(day.Year, day.Month, 1); Render(); };
            return cell;
        }

        Render();
        return root;
    }
}

/// <summary>WPF's UniformGrid lives in Primitives; alias it so widget code reads naturally.</summary>
public sealed class UniformGrid : System.Windows.Controls.Primitives.UniformGrid { }
